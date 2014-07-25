namespace Nessos.MBrace.Client

    open System
    open System.Text
    open System.Threading.Tasks
    open System.Diagnostics

    open Microsoft.FSharp.Quotations

    open Nessos.Thespian
    open Nessos.Thespian.ActorExtensions.RetryExtensions

    open Nessos.MBrace
    open Nessos.MBrace.Utils
    open Nessos.MBrace.Store
    open Nessos.MBrace.Runtime
    open Nessos.MBrace.Runtime.Compiler
    open Nessos.MBrace.Runtime.Logging
    open Nessos.MBrace.Client.Reporting

    open Nessos.MBrace.Client

    /// <summary>
    ///     Provides a handle and administration API for a running MBrace cluster.
    /// </summary>
    [<NoEquality ; NoComparison ; AutoSerializable(false)>]
    type MBraceRuntime private (runtime : ActorRef<MBraceNodeMsg>, disposables : IDisposable list) =

        static let handleError (e : exn) =
            match e with
            | CommunicationExceptionRec e -> mfailwithInner e.InnerException "Error communicating with runtime."
            | MBraceExn e -> reraise' e
            | MessageHandlingExceptionRec(MBraceExn e) -> reraise' e
            | MessageHandlingExceptionRec e -> mfailwithInner e "Runtime replied with exception."
            | _ -> reraise' e

        let postAsync msg = async {
            try return! runtime.PostAsync msg
            with e -> return handleError e
        }

        let postWithReplyAsync msgBuilder = async {
            try return! runtime.PostWithReply(msgBuilder, MBraceSettings.DefaultTimeout)
            with e -> return handleError e
        }

        let post m = postAsync m |> Async.RunSynchronously
        let postWithReply m = postWithReplyAsync m |> Async.RunSynchronously

        let clusterConfiguration = CacheAtom.Create(fun () -> postWithReply(fun ch -> GetClusterDeploymentInfo(ch, false)))
        let nodeConfiguration = CacheAtom.Create(fun () -> postWithReply(fun ch -> GetNodeDeploymentInfo(ch, false)))
        let storeInfo = 
            CacheAtom.Create((fun () -> 
                let info = clusterConfiguration.Value in StoreRegistry.TryGetStoreInfo info.StoreId |> Option.get), 
                interval = 2000)

        let processManager = 
            new Nessos.MBrace.Client.ProcessManager(
                    (fun () -> clusterConfiguration.Value.ProcessManager), 
                    (fun () -> storeInfo.Value))

        //
        //  Runtime Boot/Connect methods
        //

        static let initOfProxyActor(actor : Actor<MBraceNodeMsg>) = 
            do actor.Start()
            new MBraceRuntime(actor.Ref, [actor :> IDisposable])

        /// <summary>
        ///     Asynchronously connect to an MBrace runtime with given uri.
        ///     Uri can correspond to any of the nodes participating in the cluster.
        /// </summary>
        /// <param name="uri">Connection uri.</param>
        static member ConnectAsync(uri: Uri): Async<MBraceRuntime> =
            async {
                try
                    let node = MBraceNode uri
                    let! proxy = RuntimeProxy.connect node.Ref
                    return initOfProxyActor proxy

                with e -> return handleError e
            }

        /// <summary>
        ///     Asynchronously boots an MBrace runtime with given nodes/configuration.s
        /// </summary>
        /// <param name="master">Designated initial master node in cluster.</param>
        /// <param name="config">Boot configuration.</param>
        static member internal BootAsync (master : MBraceNode, config : BootConfiguration) : Async<MBraceRuntime> =
            async {
                let! proxy = RuntimeProxy.boot(master.Ref, config)
                return initOfProxyActor proxy
            }

        /// <summary>
        ///     Asynchronously boots a collection of nodes to form an MBrace cluster.
        /// </summary>
        /// <param name="nodes">Collection of constituent MBrace Nodes.</param>
        /// <param name="replicationFactor">Master node replication factor. Defaults to 2.</param>
        /// <param name="failoverFactor">Alternative master node failover factor. Defaults to 2.</param>
        /// <param name="storeDefinition">Store provider that will be used by the runtime.</param>
        static member BootAsync(nodes : MBraceNode list, ?replicationFactor, ?failoverFactor, ?storeDefinition : StoreDefinition) : Async<MBraceRuntime> = async {
            let failoverFactor = defaultArg failoverFactor 2
            let replicationFactor = defaultArg replicationFactor (if failoverFactor = 0 then 0 else 2)
            let storeId = 
                match storeDefinition with 
                | None -> StoreRegistry.DefaultStoreInfo.Id
                | Some p -> let info = StoreRegistry.Activate(p, makeDefault = false) in info.Id

            let nodes = nodes |> Seq.map (fun n -> n.Ref) |> Seq.toArray
            let! proxy = RuntimeProxy.bootNodes(nodes, replicationFactor, failoverFactor, Some storeId)
            return initOfProxyActor proxy
        }

        /// <summary>
        ///     Connect to an MBrace runtime with given uri.
        ///     Uri can correspond to any of the nodes participating in the cluster.
        /// </summary>
        /// <param name="uri">Connection uri.</param>
        static member Connect(uri: Uri): MBraceRuntime = MBraceRuntime.ConnectAsync(uri) |> Async.RunSynchronously

        /// <summary>
        ///     Connect to an MBrace runtime with given hostname and TCP port.
        ///     Uri can correspond to any of the nodes participating in the cluster.
        /// </summary>
        /// <param name="uri">Connection uri.</param>
        static member Connect(host: string, port : int) : MBraceRuntime = MBraceRuntime.Connect(MBraceUri.hostPortToUri(host, port))

        /// <summary>
        ///     Connect to an MBrace runtime with given uri.
        ///     Uri can correspond to any of the nodes participating in the cluster.
        /// </summary>
        /// <param name="uri">Connection uri.</param>
        static member Connect(uri: string): MBraceRuntime = MBraceRuntime.Connect(Uri(uri))

        /// <summary>
        ///     Asynchronously connect to an MBrace runtime with given uri.
        ///     Uri can correspond to any of the nodes participating in the cluster.
        /// </summary>
        /// <param name="uri">Connection uri.</param>
        static member ConnectAsync(uri: string): Async<MBraceRuntime> = MBraceRuntime.ConnectAsync(Uri(uri))

        /// <summary>
        ///     Boots a collection of nodes to form an MBrace cluster.
        /// </summary>
        /// <param name="nodes">Collection of constituent MBrace Nodes.</param>
        /// <param name="replicationFactor">Master node replication factor. Defaults to 2.</param>
        /// <param name="failoverFactor">Alternative master node failover factor. Defaults to 2.</param>
        /// <param name="storeDefinition">Store provider that will be used by the runtime.</param>
        static member Boot(nodes : MBraceNode list, ?replicationFactor, ?failoverFactor, ?storeDefinition) : MBraceRuntime = 
            MBraceRuntime.BootAsync(nodes, ?replicationFactor = replicationFactor, ?failoverFactor = failoverFactor, ?storeDefinition = storeDefinition)
            |> Async.RunSynchronously

        /// <summary>
        ///     Spawn and initialize a local MBrace cluster of given size.
        /// </summary>
        /// <param name="totalNodes">Cluster size.</param>
        /// <param name="masterPort">Specify a TCP port for the master node. Defaults to self-assigned port.</param>
        /// <param name="hostname">Hostname/Address used for cluster communication.</param>
        /// <param name="replicationFactor">Master node replication factor. Defaults to 2.</param>
        /// <param name="failoverFactor">Alternative master node failover factor. Defaults to 2.</param>
        /// <param name="storeDefinition">Store provider that will be used by the runtime.</param>
        /// <param name="debug">Spawn nodes in debug mode.</param>
        /// <param name="background">Spawn nodes in background. Defaults to false.</param>
        static member InitLocal(totalNodes : int, ?masterPort, ?hostname, ?replicationFactor : int, ?failoverFactor : int,
                                        ?storeDefinition, ?debug, ?background) : MBraceRuntime =

            if totalNodes < 3 then invalidArg "totalNodes" "should have at least 3 nodes."
            let nodes = MBraceNode.SpawnMultiple(totalNodes, ?masterPort = masterPort, ?storeDefinition = storeDefinition,
                                                    ?hostname = hostname, ?debug = debug, ?background = background)
            
            MBraceRuntime.Boot(nodes, ?replicationFactor = replicationFactor, ?failoverFactor = failoverFactor, ?storeDefinition = storeDefinition)

        static member FromActorRef(ref : ActorRef<MBraceNodeMsg>) = new MBraceRuntime(ref, [])

        //
        //  Cluster Management section
        //

        /// <summary>
        ///     Asynchronously boots a new MBrace in nodes specified by runtime object.
        /// </summary>
        /// <param name="replicationFactor">Master node replication factor. Defaults to 2.</param>
        /// <param name="failoverFactor">Alternative master node failover factor. Defaults to 2.</param>
        member r.BootAsync (?replicationFactor, ?failoverFactor) : Async<unit> =
            async {
                if nodeConfiguration.Value.State <> Idle then
                    mfailwith "Cannot boot; runtime is already active."
                else
                    match clusterConfiguration.TryGetLastSuccessfulValue () with
                    | None -> mfailwith "Cannot boot; insufficient cluster information."
                    | Some info ->
                        let nodes = info.Nodes |> Array.map (fun n -> n.Reference)
                        let config =
                            {
                                Nodes = nodes
                                ReplicationFactor = defaultArg replicationFactor info.ReplicationFactor
                                FailoverFactor = defaultArg failoverFactor info.FailoverFactor
                                StoreId = None
                            }

                        let! _ = postWithReplyAsync <| fun ch -> MasterBoot(ch,config)

                        return ()
            }
        
        /// <summary>
        ///     Asynchronously shuts down the MBrace runtime.
        /// </summary>
        member r.ShutdownAsync () : Async<unit> = async {
            // force clusterConfiguration evaluation
            do clusterConfiguration.TryGetValue() |> ignore

            return! postWithReplyAsync ShutdownSync
        }

        /// <summary>
        ///     Asynchronously reboots the MBrace runtime.
        /// </summary>
        /// <param name="replicationFactor">Master node replication factor. Defaults to 2.</param>
        /// <param name="failoverFactor">Alternative master node failover factor. Defaults to 2.</param>
        member r.RebootAsync (?replicationFactor, ?failoverFactor) : Async<unit> =
            async {
                do! r.ShutdownAsync()

                return! r.BootAsync(?replicationFactor = replicationFactor, ?failoverFactor = failoverFactor)
            }

        /// <summary>
        ///     Asynchronously attaches a collection of nodes to an existing MBrace runtime.
        /// </summary>
        /// <param name="nodes">Nodes to be attached.</param>
        member r.AttachAsync (nodes : seq<MBraceNode>) : Async<unit> =
            async {
                for node in nodes do
                    do! postWithReplyAsync (fun ch -> Attach(ch, node.Ref))
            }

        /// <summary>
        ///     Asynchronously attaches a node to an existing MBrace runtime.
        /// </summary>
        /// <param name="nodes">Node to be attached.</param>
        member r.AttachAsync (node : MBraceNode) : Async<unit> = postWithReplyAsync(fun ch -> Attach(ch, node.Ref))

        /// <summary>
        ///     Asynchronously detaches a node from existing MBrace runtime.
        /// </summary>
        /// <param name="node">Node to be detached.</param>
        member r.DetachAsync (node: MBraceNode) : Async<unit> =
            async {
                try
                    do! node.Ref.PostWithReply(Detach, MBraceSettings.DefaultTimeout)
                with
                | MBraceExn e -> return reraise' e
                | CommunicationException _ -> return mfailwithf "Failed to connect to node %A." node.Uri
                | MessageHandlingExceptionRec e -> return mfailwithInner e "Node %A replied with exception." node.Uri
            }


        /// <summary>
        ///     Boots a new MBrace runtime in Nodes specified by runtime object.
        /// </summary>
        /// <param name="replicationFactor">Master node replication factor. Defaults to 2.</param>
        /// <param name="failoverFactor">Alternative master node failover factor. Defaults to 2.</param>
        member r.Boot (?replicationFactor, ?failoverFactor) : unit = 
            r.BootAsync(?replicationFactor = replicationFactor, ?failoverFactor = failoverFactor) |> Async.RunSynchronously


        /// <summary>
        ///     Shuts down the MBrace runtime.
        /// </summary>
        member r.Shutdown() : unit = r.ShutdownAsync() |> Async.RunSynchronously

        /// <summary>
        ///     Reboots the MBrace runtime.
        /// </summary>
        /// <param name="replicationFactor">Master node replication factor. Defaults to 2.</param>
        /// <param name="failoverFactor">Alternative master node failover factor. Defaults to 2.</param>
        member r.Reboot(?replicationFactor, ?failoverFactor) : unit = 
            r.RebootAsync(?replicationFactor = replicationFactor, ?failoverFactor = failoverFactor) |> Async.RunSynchronously

        /// <summary>
        ///     Attaches a collection of Nodes to an existing MBrace runtime.
        /// </summary>
        /// <param name="nodes">Nodes to be attached.</param>
        member r.Attach (nodes : seq<MBraceNode>) : unit = r.AttachAsync nodes |> Async.RunSynchronously

        /// <summary>
        ///     Attaches a Node to an existing MBrace runtime.
        /// </summary>
        /// <param name="node">Node to be attached.</param>
        member r.Attach (node : MBraceNode) : unit = r.AttachAsync node |> Async.RunSynchronously

        /// <summary>
        ///     Detaches a node from existing MBrace runtime.
        /// </summary>
        /// <param name="node">Node to be detached.</param>
        member r.Detach (node : MBraceNode) : unit = r.DetachAsync node |> Async.RunSynchronously

        /// <summary>
        ///     Spawns a specified number of nodes and attaches to existing runtime.
        /// </summary>
        /// <param name="totalNodes">Additional nodes to be created.</param>
        /// <param name="permissions">Cluster permissions to be given to new Nodes.</param>
        /// <param name="debug">Spawn new nodes in debug mode.</param>
        /// <param name="background">Spawn new nodes in background.</param>
        member r.AttachLocal(totalNodes : int, ?permissions, ?debug, ?background) : unit =
            let nodes = MBraceNode.SpawnMultiple(totalNodes, ?permissions = permissions, ?debug = debug, ?background = background)
            r.Attach(nodes)


        //
        //  Region Misc runtime information
        //

        /// <summary>
        ///     Pings the remote node, returning the response timespan.
        /// </summary>
        /// <param name="timeout">The amount of time in milliseconds to wait for a reply from the node (default is 3 seconds).</param>
        member __.Ping(?silent: bool, ?timeout: int) : TimeSpan =
            let silent = defaultArg silent false
            let timeout = defaultArg timeout 3000

            let timer = new Stopwatch()

            timer.Start()
            runtime <!== (Ping, timeout)
            timer.Stop()

            timer.Elapsed

        /// <summary>
        ///     UUID identifying the current MBrace cluster deployment.
        /// </summary>
        member r.Id : Guid = clusterConfiguration.Value.DeploymentId

        /// <summary>
        ///     List of MBrace Nodes that constitute the current cluster.
        /// </summary>
        member r.Nodes : MBraceNode list = 
            match clusterConfiguration.TryGetLastSuccessfulValue() with
            | None -> mfailwith "Cannot extract runtime information." 
            | Some info -> info.Nodes |> Seq.map (fun n -> MBraceNode(n)) |> Seq.toList

        /// <summary>
        ///     List of MBrace Nodes in current cluster that exist in this machine.
        /// </summary>
        member r.LocalNodes : MBraceNode list = r.Nodes |> List.filter (fun node -> node.IsLocal)

        /// <summary>
        ///     Current Master node in the MBrace cluster.
        /// </summary>
        member r.Master : MBraceNode = let nI = clusterConfiguration.Value.MasterNode in MBraceNode(nI)

        /// <summary>
        ///     Alternative Master nodes in the MBrace cluster.
        /// </summary>
        member r.Alts : MBraceNode list = 
            clusterConfiguration.Value.Nodes 
            |> Seq.filter(fun nI -> nI.State = AltMaster) 
            |> Seq.map (fun n -> MBraceNode(n)) 
            |> Seq.toList

        /// <summary>
        ///     Gets if the current runtime is active (booted).
        /// </summary>
        member r.Active : bool = nodeConfiguration.Value.State <> Idle

        /// <summary>
        ///     Gets if the current runtime is running (entirely) in the local machine.
        /// </summary>
        member r.IsLocalRuntime : bool = r.Nodes |> List.forall(fun n -> n.IsLocal)

        /// <summary>
        ///     Prints a report on the cluster status to StdOut.
        /// </summary>
        /// <param name="showPerformanceCounters">Show Node performance metrics. Defaults to false.</param>
        member r.ShowInfo (?showPerformanceCounters : bool) : unit = 
            try
                let showPerformanceCounters = defaultArg showPerformanceCounters false
                let info =
                    if showPerformanceCounters then
                        postWithReply (fun ch -> GetClusterDeploymentInfo(ch, true))
                    else
                        clusterConfiguration.Value

                Reporting.MBraceNodeReporter.Report(info.Nodes, showPerf = showPerformanceCounters, showBorder = false)
                |> Console.WriteLine

            with e -> Console.Error.WriteLine e.Message

        /// <summary>
        ///     Gets all system logs generated by the MBrace cluster.
        /// </summary>
        member r.GetSystemLogs() : SystemLogEntry [] = postWithReply GetLogDump

        /// <summary>
        ///     Prints all system logs generated by the MBrace cluster to StdOut.
        /// </summary>
        member r.ShowSystemLogs() : unit = r.GetSystemLogs() |> Reporting.Logs.show

        /// <summary>
        ///     Violently kills all nodes in a local runtime.
        /// </summary>
        member r.Kill() : unit =
            if not r.IsLocalRuntime then
                invalidOp "'Kill' operation is reserved for local runtimes."

            for node in r.LocalNodes do node.Kill()

            (r :> IDisposable).Dispose()

        /// <summary>
        ///     Gets a client object for managing store definitions in given runtime.
        /// </summary>
        member r.GetStoreClient() : StoreClient = new StoreClient(storeInfo.Value)

        //
        //  Computation Section
        //


        /// <summary>
        ///     Creates a new cloud computation in runtime.
        /// </summary>
        /// <param name="computation">Computation to be executed.</param>
        member __.CreateProcess (computation : CloudComputation<'T>) : Process<'T> = 
            processManager.CreateProcess computation |> Async.RunSynchronously

        /// <summary>
        ///     Asynchronously creates a new cloud computation in runtime and awaits its result.
        /// </summary>
        /// <param name="computation">Computation to be executed.</param>
        member __.RunAsync (computation : CloudComputation<'T>) : Async<'T> =
            async {
                let! proc = processManager.CreateProcess computation
                return! proc.AwaitResultAsync ()
            }

        /// <summary>
        ///     Synchronously creates a new cloud computation in runtime and awaits its result.
        /// </summary>
        /// <param name="computation">Computation to be executed.</param>
        member __.Run (computation : CloudComputation<'T>) : 'T = __.RunAsync computation |> Async.RunSynchronously

        /// <summary>
        ///     Creates a new cloud computation in runtime.
        /// </summary>
        /// <param name="expr">Quoted cloud workflow to be executed.</param>
        /// <param name="name">Assigned name to cloud computation.</param>
        member __.CreateProcess (expr : Expr<Cloud<'T>>, ?name) : Process<'T> =
            let computation = CloudComputation.Compile(expr, ?name = name)
            __.CreateProcess computation

        /// <summary>
        ///     Asynchronously creates a new cloud computation in runtime and awaits its result.
        /// </summary>
        /// <param name="expr">Quoted cloud workflow to be executed.</param>
        /// <param name="name">Assigned name to cloud computation.</param>
        member __.RunAsync (expr : Expr<Cloud<'T>>, ?name) : Async<'T> =
            let computation = CloudComputation.Compile(expr, ?name = name)
            __.RunAsync computation

        /// <summary>
        ///     Asynchronously create a new cloud computation in runtime and await its result.
        /// </summary>
        /// <param name="expr">Quoted cloud workflow to be executed.</param>
        /// <param name="name">Assigned name to cloud computation.</param>
        member __.Run (expr : Expr<Cloud<'T>>, ?name) : 'T =
            let computation = CloudComputation.Compile(expr, ?name = name)
            __.Run computation 

        /// <summary>
        ///     Creates a new cloud computation in runtime.
        /// </summary>
        /// <param name="expr">Cloud workflow to be executed.</param>
        /// <param name="name">Assigned name to cloud computation.</param>
        [<CompilerMessage("Cloud blocks should be wrapped in quotation literals for better debug support.", 44)>]
        member __.CreateProcess (block : Cloud<'T>, ?name) : Process<'T> =
            let computation = CloudComputation.Compile(block, ?name = name)
            __.CreateProcess computation

        /// <summary>
        ///     Asynchronously creates a new cloud computation in runtime and awaits its result.
        /// </summary>
        /// <param name="expr">Cloud workflow to be executed.</param>
        /// <param name="name">Assigned name to cloud computation.</param>
        [<CompilerMessage("Cloud blocks should be wrapped in quotation literals for better debug support.", 44)>]
        member __.RunAsync (block : Cloud<'T>, ?name) : Async<'T> =
            let computation = CloudComputation.Compile(block, ?name = name)
            __.RunAsync computation

        /// <summary>
        ///     Synchronously creates a new cloud computation in runtime and awaits its result.
        /// </summary>
        /// <param name="expr">Cloud workflow to be executed.</param>
        /// <param name="name">Assigned name to cloud computation.</param>
        [<CompilerMessage("Cloud blocks should be wrapped in quotation literals for better debug support.", 44)>]
        member __.Run (block : Cloud<'T>, ?name) : 'T =
            let computation = CloudComputation.Compile(block, ?name = name)
            __.Run computation 

        /// <summary>
        ///     Kills cloud process with given id.
        /// </summary>
        /// <param name="pid">Process Id to be killed.</param>
        member __.KillProcess (pid : ProcessId) : unit = processManager.Kill pid |> Async.RunSynchronously

        /// <summary>
        ///     Gets cloud process in runtime by given id.
        /// </summary>
        /// <param name="pid">Process Id to be retrieved.</param>
        member __.GetProcess (pid : ProcessId) : Process = processManager.GetProcess pid |> Async.RunSynchronously

        /// <summary>
        ///     Gets cloud process in runtime by given id.
        /// </summary>
        /// <param name="pid">Process Id to be retrieved.</param>
        member __.GetProcess<'T> (pid : ProcessId) : Process<'T> = __.GetProcess pid :?> Process<'T>

        /// <summary>
        ///     Gets all running or completed process from the runtime.
        /// </summary>
        member __.GetAllProcesses () : Process [] = processManager.GetAllProcesses () |> Async.RunSynchronously

        /// <summary>
        ///     Deletes process info from runtime by given id.
        /// </summary>
        /// <param name="pid">Process Id to be retrieved.</param>
        member __.ClearProcessInfo (pid : ProcessId) : unit = processManager.ClearProcessInfo pid |> Async.RunSynchronously

        /// <summary>
        ///     Deletes *all* process information from runtime.
        /// </summary>
        member __.ClearAllProcessInfo () : unit = processManager.ClearAllProcessInfo () |> Async.RunSynchronously

        /// <summary>
        ///     Prints runtime process information to StdOut.
        /// </summary>
        member __.ShowProcessInfo () : unit = processManager.GetInfo () |> Console.WriteLine

        /// <summary>
        ///     Gets printed runtime process information.
        /// </summary>
        member __.GetProcessInfo () : string = processManager.GetInfo ()


        interface IDisposable with
            member r.Dispose() = for d in disposables do d.Dispose()