﻿namespace Nessos.MBrace.Runtime

    open System
    open System.Diagnostics
    open System.Net
    open System.Collections.Generic

    open Nessos.Thespian
    open Nessos.Thespian.Agents
    open Nessos.Thespian.ImemDb

    open Nessos.MBrace.Runtime
    open Nessos.MBrace.Utils

    type private PerfCounter = System.Diagnostics.PerformanceCounter

    /// Represents measurements on resources usage.
    type PerformanceCounter = single option

    /// Some node metrics, such as CPU, memory usage, etc
    type NodePerformanceInfo =
        {
            CpuUsage            : PerformanceCounter
            CpuUsageAverage     : PerformanceCounter
            TotalMemory         : PerformanceCounter
            MemoryUsage         : PerformanceCounter
            NetworkUsageUp      : PerformanceCounter
            NetworkUsageDown    : PerformanceCounter
        } 

    type private Counter = TotalCpu | TotalMemoryUsage 
    type private Message = Info of AsyncReplyChannel<NodePerformanceInfo> | Stop of AsyncReplyChannel<unit>

    /// Collects statistics on CPU, memory, network, etc.
    type PerformanceMonitor (?updateInterval : int, ?maxSamplesCount : int) =

        let onMono = Utils.runsOnMono

        // Get a new counter value after 0.5 sec and keep the last 20 values
        let updateInterval = defaultArg updateInterval 500
        let maxSamplesCount = defaultArg maxSamplesCount 20
    
        let perfCounters = new List<PerfCounter>()

        // Performance counters 
        let cpuUsage =
            if PerformanceCounterCategory.Exists("Processor") then 
                let pc = new PerfCounter("Processor", "% Processor Time", "_Total",true)
                perfCounters.Add(pc)
                Some <| fun () -> pc.NextValue()
            else None
    
        let totalMemory = 
            if onMono then
                if PerformanceCounterCategory.Exists("Mono Memory") then 
                    let pc = new PerfCounter("Mono Memory", "Total Physical Memory")
                    perfCounters.Add(pc)
                    Some <| fun () -> pc.NextValue()
                else None
            else
                let ci = Microsoft.VisualBasic.Devices.ComputerInfo () // TODO: Maybe use WMI
                let mb = ci.TotalPhysicalMemory / uint64 (1 <<< 20) |> single
                Some(fun () -> mb)
    
        let memoryUsage = 
            if PerformanceCounterCategory.Exists("Memory") 
            then 
                match totalMemory with
                | None -> None
                | Some(getNext) ->
                    let pc = new PerfCounter("Memory", "Available Mbytes",true)
                    perfCounters.Add(pc)
                    let totalMemory = getNext()
                    Some <| (fun () -> 100.f - 100.f * pc.NextValue() / totalMemory)
            else None
    
        let networkSentUsage =
            if PerformanceCounterCategory.Exists("Network Interface") then 
                let inst = (new PerformanceCounterCategory("Network Interface")).GetInstanceNames()
                let pc = 
                    inst |> Array.map (fun nic -> new PerfCounter("Network Interface", "Bytes Sent/sec", nic))
                Seq.iter perfCounters.Add pc
                Some(fun () -> pc |> Array.fold (fun sAcc s -> sAcc + 8.f * s.NextValue () / 1024.f) 0.f) // kbps
            else None
    
        let networkReceivedUsage =
            if PerformanceCounterCategory.Exists("Network Interface") then 
                let inst = (new PerformanceCounterCategory("Network Interface")).GetInstanceNames()
                let pc = 
                    inst |> Array.map (fun nic -> new PerfCounter("Network Interface", "Bytes Received/sec",nic))
                Seq.iter perfCounters.Add pc
                Some(fun () -> pc |> Array.fold (fun rAcc r -> rAcc + 8.f * r.NextValue () / 1024.f ) 0.f) // kbps
            else None
    
        let getPerfValue : (unit -> single) option -> PerformanceCounter = function
            | None -> None
            | Some(getNext) -> Some <| getNext()
    
        let getAverage (values : PerformanceCounter seq) =
            if Seq.exists ((=) None) values then None
            else values |> Seq.map (function (Some v) -> v | v -> failwithf "Invalid state '%A'" v)
                        |> Seq.average
                        |> Some
    
        let queues = dict [ TotalCpu, Queue<PerformanceCounter>()
                            TotalMemoryUsage, Queue<PerformanceCounter>() ]
    
        let newValue cnt : PerformanceCounter = 
            match cnt with
            | TotalCpu -> cpuUsage
            | TotalMemoryUsage -> memoryUsage
            |> getPerfValue
    
        let updateQueues () =
            queues
            |> Seq.iter (fun (KeyValue (cnt, q)) ->
                let newVal = newValue cnt
    
                if q.Count < maxSamplesCount then q.Enqueue newVal
                else q.Dequeue() |> ignore; q.Enqueue newVal)
    
        let newNodePerformanceInfo () : NodePerformanceInfo =
            {
                CpuUsage            = queues.[TotalCpu].Peek()
                CpuUsageAverage     = queues.[TotalCpu] |> getAverage
                TotalMemory         = totalMemory       |> getPerfValue
                MemoryUsage         = queues.[TotalMemoryUsage].Peek()
                NetworkUsageUp      = networkSentUsage      |> getPerfValue
                NetworkUsageDown    = networkReceivedUsage  |> getPerfValue
            }

        let perfCounterActor = 
            new MailboxProcessor<Message>(fun inbox ->    
                let rec agentLoop () : Async<unit> = async {
                    updateQueues ()
    
                    while inbox.CurrentQueueLength <> 0 do
                        let! msg = inbox.Receive()
                        match msg with
                        | Stop ch -> ch.Reply (); return ()
                        | Info ch -> newNodePerformanceInfo () |> ch.Reply
    
                    do! Async.Sleep updateInterval
    
                    return! agentLoop ()
                }
                agentLoop ())

        let monitored =
             let l = new List<string>()
             if cpuUsage.IsSome then l.Add("%Cpu")
             if totalMemory.IsSome then l.Add("Total Memory")
             if memoryUsage.IsSome then l.Add("%Memory")
             if networkSentUsage.IsSome then l.Add("Network (sent)")
             if networkReceivedUsage.IsSome then l.Add("Network (received)")
             l

        member this.GetCounters () : NodePerformanceInfo =
            perfCounterActor.PostAndReply(fun ch -> Info ch)

        member this.Start () =
            perfCounterActor.Start()
            this.GetCounters() |> ignore // first value always 0

        member this.MonitoredCategories : string seq = monitored :> _

        interface System.IDisposable with
            member this.Dispose () = 
                perfCounterActor.PostAndReply(fun ch -> Stop ch)
                perfCounters |> Seq.iter (fun c -> c.Dispose())  