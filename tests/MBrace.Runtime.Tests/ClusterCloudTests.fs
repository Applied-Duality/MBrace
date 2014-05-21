namespace Nessos.MBrace.Runtime.Tests

    open System
    open System.IO

    open FsUnit
    open NUnit.Framework

    open Nessos.MBrace
    open Nessos.MBrace.Utils
    open Nessos.MBrace.Client

    type ``Cluster Cloud Tests``() =
        inherit ``Cloud Tests``()

        let currentRuntime : MBraceRuntime option ref = ref None
        
        override __.Name = "Cluster Cloud Tests"
        override __.IsLocalTesting = false
        override __.ExecuteExpression<'T>(expr: Quotations.Expr<Cloud<'T>>): 'T =
            MBrace.RunRemote __.Runtime expr

        member __.Runtime =
            match currentRuntime.Value with
            | None -> invalidOp "No runtime specified in test fixture."
            | Some r -> r

        [<TestFixtureSetUp>]
        member test.InitRuntime() =
            lock currentRuntime (fun () ->
                match currentRuntime.Value with
                | Some runtime -> runtime.Kill()
                | None -> ()
            
                MBraceSettings.MBracedExecutablePath <- Path.Combine(Directory.GetCurrentDirectory(), "mbraced.exe")
                let runtime = MBraceRuntime.InitLocal(3, debug = true)
                currentRuntime := Some runtime)
        
        [<TestFixtureTearDown>]
        member test.FiniRuntime() =
            lock currentRuntime (fun () -> 
                match currentRuntime.Value with
                | None -> invalidOp "No runtime specified in test fixture."
                | Some r -> r.Shutdown() ; currentRuntime := None)


        [<Test>]
        member test.SerializationTest () =
            shouldFailwith<MBraceException> (fun () -> <@ cloud { return new System.Net.HttpListener() } @> |> test.ExecuteExpression |> ignore)

        [<Test>] 
        member t.``Cloud Log`` () = 
            let ps = <@ cloud { do! Cloud.Log "Cloud Log Test Msg" } @> |> t.Runtime.CreateProcess
            Threading.Thread.Sleep(delay) // storelogger flushes every 2 seconds
            let dumps = ps.GetLogs()
            ()
//                dumps |> Seq.find(fun dump -> dump.Print().Contains("Cloud Log Test Msg")) |> ignore
            
        [<Test>] 
        member test.``Cloud Trace`` () = 
            let ps = <@ testTrace 1 @> |> test.Runtime.CreateProcess
            Threading.Thread.Sleep(delay)
            let dumps = ps.GetLogs()
            ()
            should equal true (traceHasValue dumps "a" "1")
            should equal true (traceHasValue dumps "x" "2")

        [<Test>] 
        member test.``Serialization Exception`` () = 
            test.SerializationTest()

        [<Test>]
        member test.``Cloud Trace Exception `` () = 
            let ps = <@ cloud { return 1 / 0 } |> Cloud.Trace @> |> test.Runtime.CreateProcess
            Threading.Thread.Sleep(delay)
            shouldFailwith<CloudException> (fun () -> ps.AwaitResult() |> ignore)
            let dumps = ps.GetLogs()
            should equal true (dumps |> Seq.exists(fun e -> e.Message.Contains("DivideByZeroException")))
                
        [<Test>] 
        member test.``Cloud Trace handle Exception`` () = 
            let ps = <@ testTraceExc () @> |> test.Runtime.CreateProcess
            Threading.Thread.Sleep(delay)
            let dumps = ps.GetLogs()
            should equal true (traceHasValue dumps "ex" "error")

        [<Test>] 
        member test.``Cloud Trace For Loop`` () = 
            let ps = <@ testTraceForLoop () @> |> test.Runtime.CreateProcess
            Threading.Thread.Sleep(delay)
            let dumps = ps.GetLogs()
            should equal true (traceHasValue dumps "i" "1")
            should equal true (traceHasValue dumps "i" "2")
            should equal true (traceHasValue dumps "i" "3")

        [<Test>]
        member test.``Quotation Cloud Trace`` () = 
            let ps = <@ cloud { let! x = cloud { return 1 } in return x } |> Cloud.Trace @> |> test.Runtime.CreateProcess
            Threading.Thread.Sleep(delay)
            let dumps = ps.GetLogs()
            should equal false (traceHasValue dumps "x" "1")

        [<Test>]
        member test.``Cloud NoTraceInfo Attribute`` () = 
            let ps = <@ cloud { return! cloud { return 1 } <||> cloud { return 2 } } |> Cloud.Trace @> |> test.Runtime.CreateProcess
            Threading.Thread.Sleep(delay)
            let dumps = ps.GetLogs()
            let result = 
                dumps 
                |> Seq.exists 
                    (fun e -> 
                        match e.TraceInfo with
                        | Some i -> i.Function = Some ("op_LessBarBarGreater")
                        | None -> false)

            should equal false result

        [<Test>]
        member test.``Parallel Cloud Trace`` () = 
            let proc = <@ testParallelTrace () @> |> test.Runtime.CreateProcess 
            proc.AwaitResult() |> ignore
            Threading.Thread.Sleep(delay) 
            let dumps = proc.GetLogs()
            should equal true (traceHasValue dumps "x" "2")
            should equal true (traceHasValue dumps "y" "3")
            should equal true (traceHasValue dumps "r" "5")

        [<Test; Repeat 10>]
        member test.``Z3 Test Kill Process - Process killed, process stops`` () =
            let mref = MutableCloudRef.New 0 |> MBrace.RunLocal
            let ps = 
                <@ cloud {
                    while true do
                        do! Async.Sleep 100 |> Cloud.OfAsync
                        let! curval = MutableCloudRef.Read mref
                        do! MutableCloudRef.Force(mref, curval + 1)
                } @> |> test.Runtime.CreateProcess

            test.Runtime.KillProcess(ps.ProcessId)
            Threading.Thread.Sleep(delay)
            let val1 = MutableCloudRef.Read mref |> MBrace.RunLocal
            Threading.Thread.Sleep(delay)
            let val2 = MutableCloudRef.Read mref |> MBrace.RunLocal

            should equal ps.Result ProcessResult.Killed
            should equal val1 val2

        [<Test; Repeat 10>]
        member test.``Z4 Test Kill Process - Fork bomb`` () =
            let m = MutableCloudRef.New 0 |> MBrace.RunLocal
            let rec fork () : Cloud<unit> = 
                cloud { do! MutableCloudRef.Force(m,1)
                        let! _ = fork() <||> fork() in return () }
            let ps = test.Runtime.CreateProcess <@ fork () @>
            Async.RunSynchronously <| Async.Sleep 4000
            test.Runtime.KillProcess(ps.ProcessId)
            Async.RunSynchronously <| Async.Sleep 2000
            MutableCloudRef.Force(m, 0) |> MBrace.RunLocal
            Async.RunSynchronously <| Async.Sleep 1000
            let v = MutableCloudRef.Read(m) |> MBrace.RunLocal
            should equal ProcessResult.Killed ps.Result
            should equal 0 v

        [<Test>]
        member __.``Fetch logs`` () =
            __.Runtime.GetSystemLogs() |> Seq.isEmpty |> should equal false
            __.Runtime.Nodes.Head.GetSystemLogs() |> Seq.isEmpty |> should equal false


        [<Test; Category("Runtime Administration")>]
        member __.``Ping the Runtime``() =
            should greaterThan 0 (__.Runtime.Ping())
            
        [<Test; Category("Runtime Administration")>]
        member __.``Get Runtime Status`` () =
            __.Runtime.Active |> should equal true

        [<Test; Category("Runtime Administration")>]
        member __.``Get Runtime Information`` () =
            __.Runtime.ShowInfo ()

        [<Test; Category("Runtime Administration")>]
        member __.``Get Runtime Performance Information`` () =
            __.Runtime.ShowInfo (true)

        [<Test; Category("Runtime Administration")>]
        member __.``Get Runtime Deployment Id`` () =
            __.Runtime.Id |> ignore

        [<Test; Category("Runtime Administration")>]
        member __.``Get Runtime Nodes`` () =
            __.Runtime.Nodes |> Seq.length |> should greaterThan 0

        [<Test; Category("Runtime Administration")>]
        member __.``Get Master Node`` () =
            __.Runtime.Master |> ignore

        [<Test; Category("Runtime Administration")>]
        member __.``Get Alt Nodes`` () =
            __.Runtime.Alts |> ignore

        [<Test; Category("Runtime Administration"); ExpectedException(typeof<Nessos.MBrace.NonExistentObjectStoreException>)>]
        member __.``Delete container`` () =
            let s = __.Runtime.Run <@ CloudSeq.New([1..10]) @> 
            __.Runtime.DeleteContainer(s.Container) 
            Seq.toList s |> ignore

        [<Test; Repeat 4; Category("Runtime Administration")>]
        member __.``Reboot runtime`` () =
            __.Runtime.Reboot()
            __.Runtime.Run <@ cloud { return 1 + 1 } @> |> should equal 2

        [<Test; Category("Runtime Administration")>]
        member __.``Attach Node`` () =
            let n = __.Runtime.Nodes |> List.length
            __.Runtime.AttachLocal 1 

            do wait 1000

            let n' = __.Runtime.Nodes |> List.length 
            n' - n |> should equal 1

        [<Test; Category("Runtime Administration")>]
        member __.``Detach Node`` () =
            let nodes = __.Runtime.Nodes 
            let n = nodes.Length
            let node2 = nodes.[1] 
            __.Runtime.Detach node2 
            
            do wait 1000

            let n' =__.Runtime.Nodes |> List.length
            n - n' |> should equal 1
