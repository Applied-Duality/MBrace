﻿namespace Nessos.MBrace.Runtime

    module internal Main =
        
        open System
        open System.Diagnostics

        open Nessos.FsPickler
        open Nessos.UnionArgParser

        open Nessos.Vagrant

        open Nessos.Thespian
        open Nessos.Thespian.Serialization
        open Nessos.Thespian.Remote.ConnectionPool
        open Nessos.Thespian.Remote.TcpProtocol

        open Nessos.MBrace
        open Nessos.MBrace.Core
        open Nessos.MBrace.Utils
        open Nessos.MBrace.Runtime
        open Nessos.MBrace.Runtime.Logging
        open Nessos.MBrace.Runtime.Store
        open Nessos.MBrace.Runtime.Definitions
        open Nessos.MBrace.Runtime.ProcessDomain.Configuration 
        open Nessos.MBrace.Client

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

//            let results = 
#if APPDOMAIN_ISOLATION
            let exiter = new ExceptionExiter(fun msg -> failwith (defaultArg msg "processdomain error")) :> IExiter
            let results = workerConfig.ParseCommandLine(inputs = args)
#else
            let exiter = new ConsoleProcessExiter(true) :> IExiter
            let results = workerConfig.ParseCommandLine(errorHandler = plugExiter exiter)
#endif

            let parentProc = results.PostProcessResult(<@ Parent_Pid @>, Process.GetProcessById)
            let debugMode = results.Contains <@ Debug @>
            let processDomainId = results.GetResult <@ Process_Domain_Id @>
            let assemblyPath = results.GetResult <@ Assembly_Cache @>
            let hostname = results.GetResult (<@ HostName @>, defaultValue = "localhost")
            let port = results.GetResult (<@ Port @>, defaultValue = -1)
            let parentAddress = results.PostProcessResult (<@ Parent_Address @>, Address.Parse)
            let activator = results.GetResult <@ Store_Activator @>
            let cacheStoreEndpoint = results.GetResult <@ Cache_Store_Endpoint @>

            //
            // Register Things
            //

            let vagrantCache = new VagrantCache(assemblyPath, lookupAppDomain = true)
            let vagrantClient = new VagrantClient()
            do IoC.RegisterValue(vagrantCache)
            do IoC.RegisterValue(vagrantClient)

            // Register Serialization
            do Nessos.MBrace.Runtime.Serialization.Register(FsPickler.CreateBinary())

            // Register Logger
            let logger =
                let pid = System.Diagnostics.Process.GetCurrentProcess().Id
                lazy(getParentLogger Serialization.SerializerRegistry.DefaultName parentAddress)
                |> Logger.lazyWrap
                // prepend "ProcessDomain" prefix to all log entries
                |> Logger.map (fun e -> {e with Message = sprintf "[worker %d] %s" pid e.Message})


            IoC.RegisterValue<ISystemLogger>(logger)
            ThespianLogger.Register(logger)

            // Register Store
            StoreRegistry.ActivateLocalCache(StoreProvider.FileSystem cacheStoreEndpoint)

            // load store dependencies from cache
            activator.Dependencies
            |> vagrantClient.GetAssemblyLoadInfo
            |> List.filter (function Loaded _ | LoadedWithStaticIntialization _ -> false | _ -> true)
            |> List.map (fun l -> vagrantCache.GetCachedAssembly(l.Id, includeImage = true))
            |> List.iter (vagrantClient.LoadPortableAssembly >> ignore)

                
            let storeInfo = StoreRegistry.TryActivate(activator, makeDefault = true)

            // Register listeners
            TcpListenerPool.DefaultHostname <- hostname
            if port = -1 then TcpListenerPool.RegisterListener(IPEndPoint.any)
            else TcpListenerPool.RegisterListener(IPEndPoint.anyIp port)
            TcpConnectionPool.Init()

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
