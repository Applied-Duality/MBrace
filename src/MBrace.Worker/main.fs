﻿namespace Nessos.MBrace.Runtime

    module internal Main =
        
        open System
        open System.Diagnostics
        open System.Threading

        open Nessos.FsPickler
        open Nessos.UnionArgParser

        open Nessos.Vagrant

        open Nessos.Thespian
        open Nessos.Thespian.Serialization
        open Nessos.Thespian.Remote.ConnectionPool
        open Nessos.Thespian.Remote.TcpProtocol

        open Nessos.MBrace
        open Nessos.MBrace.Utils
        open Nessos.MBrace.Store
        open Nessos.MBrace.Runtime
        open Nessos.MBrace.Runtime.Logging
        open Nessos.MBrace.Runtime.Definitions
        open Nessos.MBrace.Runtime.ProcessDomain.Configuration

        let selfProc = Process.GetCurrentProcess()

        // if anyone can suggest a less hacky way, be my guest..
        // a process spawned from command line is UserInteractive but has null window handle
        // a process spawned in an autonomous window is UserInteractive and has a non-trivial window handle
        let isWindowed = Environment.UserInteractive && selfProc.MainWindowHandle <> 0n

        let rec mainLoop (parent: System.Diagnostics.Process) =
            let rec loop () =
                async {
                    if (try parent.HasExited with _ -> true) then return 1
                    else
                        do! Async.Sleep 4000
                        return! loop ()
                }

            Async.RunSynchronously (loop ())
        
        //TODO!! check the parent logger actor name
        let getParentLogger serializername (parentAddress: Address) =

            let uri = sprintf' "utcp://%s/*/common.loggerActor.0/%s" (parentAddress.ToString()) serializername

            ActorRef.fromUri uri

        [<EntryPoint>]
        let main args =
            //
            //  parse configuration
            //

#if APPDOMAIN_ISOLATION
            let exiter = new ExceptionExiter(fun msg -> failwith (defaultArg msg "processdomain error")) :> IExiter
            let results = workerConfig.ParseCommandLine(inputs = args)
#else
            let exiter = new ConsoleProcessExiter(true) :> IExiter
            let results = workerConfig.ParseCommandLine(errorHandler = plugExiter exiter)
#endif
            //
            // Register Things
            //

            let workingDirectory = results.GetResult <@ Working_Directory @>
            SystemConfiguration.InitializeConfiguration(
                    workingDirectory = workingDirectory, 
                    cleanupWorkingDirectory = false, 
                    useVagrantPickler = false)

            let parentProc = results.PostProcessResult(<@ Parent_Pid @>, Process.GetProcessById)
            let debugMode = results.Contains <@ Debug @>
            let processDomainId = results.GetResult <@ Process_Domain_Id @>
            let hostname = results.GetResult (<@ HostName @>, defaultValue = "localhost")
            let minThreads = results.GetResult <@ Min_Threads @>
            let port = results.GetResult (<@ Port @>, defaultValue = -1)
            let parentAddress = results.PostProcessResult (<@ Parent_Address @>, Address.Parse)
            let activator = results.PostProcessResult (<@ Store_Activator @>, fun bs -> Serialization.Deserialize<StoreActivationInfo> bs)

            let threadPoolSuccess = results.Catch(fun () -> ThreadPool.SetMinThreads(minThreads, minThreads))

            // Register Logger
            let logger =
                let pid = System.Diagnostics.Process.GetCurrentProcess().Id
                lazy(getParentLogger Serialization.SerializerRegistry.DefaultName parentAddress)
                |> Logger.lazyWrap
                // prepend "ProcessDomain" prefix to all log entries
                |> Logger.map (fun e -> {e with Message = sprintf "[worker %d] %s" pid e.Message})

            SystemConfiguration.Logger <- logger

            // load store dependencies from cache
            let results = VagrantRegistry.Instance.LoadCachedAssemblies(activator.Dependencies, loadPolicy = AssemblyLoadPolicy.ResolveAll)
                
            let storeInfo = StoreRegistry.Activate(activator, makeDefault = true)

            // Register listeners
            TcpListenerPool.DefaultHostname <- hostname
            if port = -1 then TcpListenerPool.RegisterListener(IPEndPoint.any)
            else TcpListenerPool.RegisterListener(IPEndPoint.anyIp port)

            // Register exiter
            IoC.RegisterValue<IExiter>(exiter)

            logger.Logf Info "ALLOCATED PORT: %d" port
            logger.Logf Info "PROC ID: %d" <| Process.GetCurrentProcess().Id

            let unloadF () = 
#if APPDOMAIN_ISOLATION
                AppDomain.Unload(AppDomain.CurrentDomain)
#else
                Process.GetCurrentProcess().Kill()
#endif

            try
                let nodeManagerReceiver = ActorRef.fromUri <| sprintf' "utcp://%s/*/activatorReceiver/%s" (parentAddress.ToString()) "FsPickler"
                logger.LogInfo <| sprintf' "PARENT ADDRESS: %O" parentAddress

                let d : IDisposable option ref = ref None

                d := Nessos.Thespian.Cluster.Common.Cluster.OnNodeManagerSet
                     |> Observable.subscribe (Option.iter (fun nodeManager ->
                            try 
                                nodeManagerReceiver <!= fun ch -> ch, nodeManager
                                d.Value.Value.Dispose()
                            with e -> logger.LogWithException e "PROCESS DOMAIN INIT FAULT" Error))
                     |> Some

                let address = new Address(TcpListenerPool.DefaultHostname, TcpListenerPool.GetListener().LocalEndPoint.Port)
                Definitions.Service.bootProcessDomain address

                mainLoop parentProc
            with e ->
                logger.LogWithException e "Failed to start process domain." Error
                1
