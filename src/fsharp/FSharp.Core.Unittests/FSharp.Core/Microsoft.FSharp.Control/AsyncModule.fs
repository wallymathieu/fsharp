// Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

// Various tests for the:
// Microsoft.FSharp.Control.Async module

namespace FSharp.Core.Unittests.FSharp_Core.Microsoft_FSharp_Control

open System
open FSharp.Core.Unittests.LibraryTestFx
open NUnit.Framework

module LeakUtils =
    // when testing for liveness, the things that we want to observe must always be created in
    // a nested function call to avoid the GC (possibly) treating them as roots past the last use in the block.
    // We also need something non trivial to disuade the compiler from inlining in Release builds.
    type ToRun<'a>(f : unit -> 'a) =
        member this.Invoke() = f()
   
    let run (toRun : ToRun<'a>) = toRun.Invoke()

// ---------------------------------------------------

[<TestFixture>]
type AsyncModule() =
    
    /// Simple asynchronous task that delays 200ms and returns a list of the current tick count
    let getTicksTask =
        async {
            do! Async.SwitchToThreadPool()
            let tickstamps = ref [] // like timestamps but for ticks :)
            
            for i = 1 to 10 do
                tickstamps := DateTime.Now.Ticks :: !tickstamps
                do! Async.Sleep(20)
                
            return !tickstamps
        }     

    let wait (wh : #System.Threading.WaitHandle) (timeoutMilliseconds : int) = 
#if FX_NO_WAITONE_MILLISECONDS
        wh.WaitOne(TimeSpan.FromMilliseconds (float timeoutMilliseconds))
#else
#if FX_NO_EXIT_CONTEXT_FLAGS
        wh.WaitOne(timeoutMilliseconds)
#else
        wh.WaitOne(timeoutMilliseconds, exitContext=false)
#endif
#endif

    let dispose(d : #IDisposable) = d.Dispose()

    let testErrorAndCancelRace computation = 
        for _ in 1..100 do
            let cts = new System.Threading.CancellationTokenSource()
            use barrier = new System.Threading.ManualResetEvent(false)
            async { cts.Cancel() } 
            |> Async.Start

            let c = ref 0
            let incr () = System.Threading.Interlocked.Increment(c) |> ignore

            Async.StartWithContinuations(
                computation,
                (fun _ -> failwith "success not expected"),
                (fun _ -> incr()),
                (fun _ -> incr()),
                cts.Token
            )

            wait barrier 100
            |> ignore
            if !c = 2 then Assert.Fail("both error and cancel continuations were called")

    [<Test>]
    member this.AwaitIAsyncResult() =

        let beginOp, endOp, cancelOp = Async.AsBeginEnd(fun() -> getTicksTask)

        // Begin the async operation and wait
        let operationIAR = beginOp ((), new AsyncCallback(fun iar -> ()), null)
        match Async.AwaitIAsyncResult(operationIAR) |> Async.RunSynchronously with
        | true  -> ()
        | false -> Assert.Fail("Timed out. Expected to succeed.")

        // When the operation has already completed
        let operationIAR = beginOp ((), new AsyncCallback(fun iar -> ()), null)
        sleep(250)
        
        let result = Async.AwaitIAsyncResult(operationIAR) |> Async.RunSynchronously        
        match result with
        | true  -> ()
        | false -> Assert.Fail("Timed out. Expected to succeed.")

        // Now with a timeout
        let operationIAR = beginOp ((), new AsyncCallback(fun iar -> ()), null)
        let result = Async.AwaitIAsyncResult(operationIAR, 1) |> Async.RunSynchronously
        match result with
        | true  -> Assert.Fail("Timeout expected")
        | false -> ()

    [<Test>]
    member this.``AwaitWaitHandle.Timeout``() = 
        use waitHandle = new System.Threading.ManualResetEvent(false)
        let startMs = DateTime.Now.Millisecond

        let r = 
            Async.AwaitWaitHandle(waitHandle, 500)
            |> Async.RunSynchronously

        Assert.IsFalse(r, "Timeout expected")

        let endMs = DateTime.Now.Millisecond
        let delta = endMs - startMs
        Assert.IsTrue(abs ((abs delta) - 500) < 400, sprintf "Delta is too big %d" delta)

    [<Test>]
    member this.``AwaitWaitHandle.TimeoutWithCancellation``() = 
        use barrier = new System.Threading.ManualResetEvent(false)
        use waitHandle = new System.Threading.ManualResetEvent(false)
        let cts = new System.Threading.CancellationTokenSource()

        Async.AwaitWaitHandle(waitHandle, 5000)
        |> Async.Ignore
        |> fun c -> 
                    Async.StartWithContinuations(
                        c, 
                        (failwithf "Unexpected success %A"), 
                        (failwithf "Unexpected error %A"), 
                        (fun _ -> barrier.Set() |> ignore), 
                        cts.Token
                    )

        // wait a bit then signal cancellation
        let timeout = wait barrier 500
        Assert.IsFalse(timeout, "timeout=true is not expected")

        cts.Cancel()

        // wait 10 seconds for completion
        let ok = wait barrier 10000
        if not ok then Assert.Fail("Async computation was not completed in given time")
    
    [<Test>]
    member this.``AwaitWaitHandle.DisposedWaitHandle1``() = 
        let wh = new System.Threading.ManualResetEvent(false)
        
        dispose wh
        let test = async {
            try
                let! timeout = Async.AwaitWaitHandle wh
                Assert.Fail(sprintf "Unexpected success %A" timeout)
            with
                | :? ObjectDisposedException -> ()
                | e -> Assert.Fail(sprintf "Unexpected error %A" e)
            }
        Async.RunSynchronously test
    
    [<Test>]
    member this.``OnCancel.RaceBetweenCancellationHandlerAndDisposingHandlerRegistration``() = 
        let test() = 
            let flag = ref 0
            let isSet() = lock flag (fun() -> !flag = 1)
            let cts = new System.Threading.CancellationTokenSource()
            let go = async {
                use! holder = Async.OnCancel(fun() -> lock flag (fun() -> flag := 1) |> ignore)
                while true do
                    do! Async.Sleep 50
                }
            Async.Start (go, cancellationToken = cts.Token)
            sleep(100)
            cts.Cancel()
            sleep(100)
            Assert.IsTrue(isSet())
        for _i = 1 to 50 do test()

    [<Test>]
    member this.``OnCancel.CancelThatWasSignalledBeforeRunningTheComputation``() = 
        let test() = 
            let cts = new System.Threading.CancellationTokenSource()
            let go e (flag : bool ref) = async {
                let! _ = Async.AwaitWaitHandle e
                sleep 500
                use! _holder = Async.OnCancel(fun () -> flag := true)
                while true do
                    do! Async.Sleep 100
                }

            let evt = new System.Threading.ManualResetEvent(false)
            let finish = new System.Threading.ManualResetEvent(false)
            let cancelledWasCalled = ref false
            Async.StartWithContinuations(go evt cancelledWasCalled, ignore, ignore, (fun _ -> finish.Set() |> ignore),  cancellationToken = cts.Token)
            sleep 500
            evt.Set() |> ignore
            cts.Cancel()

            let ok = wait finish 3000
            Assert.IsTrue(ok, "Computation should be completed")
            Assert.IsFalse(!cancelledWasCalled, "Cancellation handler should not be called")

        for _i = 1 to 50 do test()


    [<Test>]
    member this.``Async.AwaitWaitHandle does not leak memory`` () =
        // This test checks that AwaitWaitHandle does not leak continuations (described in #131),
        // We only test the worst case - when the AwaitWaitHandle is already set.
        use manualResetEvent = new System.Threading.ManualResetEvent(true)
        
        let tryToLeak() = 
            let resource = 
                LeakUtils.ToRun (fun () ->
                    let resource = obj()
                    let work = 
                        async { 
                            let! _ = Async.AwaitWaitHandle manualResetEvent
                            GC.KeepAlive(resource)
                            return ()
                        }

                    work |> Async.RunSynchronously |> ignore
                    WeakReference(resource))
                  |> LeakUtils.run

            Assert.IsTrue(resource.IsAlive)

            GC.Collect()
            GC.WaitForPendingFinalizers()
            GC.Collect()
            GC.WaitForPendingFinalizers()
            GC.Collect()

            Assert.IsFalse(resource.IsAlive)
        
        // The leak hangs on a race condition which is really hard to trigger in F# 3.0, hence the 100000 runs...
        for _ in 1..100 do tryToLeak()
           
    [<Test>]
    member this.``AwaitWaitHandle.DisposedWaitHandle2``() = 
        let wh = new System.Threading.ManualResetEvent(false)
        let barrier = new System.Threading.ManualResetEvent(false)

        let test = async {
            let! timeout = Async.AwaitWaitHandle(wh, 10000)
            Assert.IsFalse(timeout, "Timeout expected")
            barrier.Set() |> ignore
            }
        Async.Start test

        // await 3 secs then dispose waithandle - nothing should happen
        let timeout = wait barrier 3000
        Assert.IsFalse(timeout, "Barrier was reached too early")
        dispose wh
        
        let ok = wait barrier 10000
        if not ok then Assert.Fail("Async computation was not completed in given time")

    [<Test>]
    member this.``RunSynchronously.NoThreadJumpsAndTimeout``() = 
            let longRunningTask = async { sleep(5000) }
            try
                Async.RunSynchronously(longRunningTask, timeout = 500)
                Assert.Fail("TimeoutException expected")
            with
                :? System.TimeoutException -> ()
#if FSHARP_CORE_PORTABLE
// do nothing
#else
    [<Test>]
    member this.``RunSynchronously.NoThreadJumpsAndTimeout.DifferentSyncContexts``() = 
        let run syncContext =
            let old = System.Threading.SynchronizationContext.Current
            System.Threading.SynchronizationContext.SetSynchronizationContext(syncContext)
            let longRunningTask = async { sleep(5000) }
            let failed = ref false
            try
                Async.RunSynchronously(longRunningTask, timeout = 500)
                failed := true
            with
                :? System.TimeoutException -> ()
            System.Threading.SynchronizationContext.SetSynchronizationContext(old)
            if !failed then Assert.Fail("TimeoutException expected")
        run null
        run (System.Threading.SynchronizationContext())
#endif

    [<Test>]
    member this.``RaceBetweenCancellationAndError.AwaitWaitHandle``() = 
        let disposedEvent = new System.Threading.ManualResetEvent(false)
        dispose disposedEvent

        testErrorAndCancelRace(Async.AwaitWaitHandle disposedEvent)

    [<Test>]
    member this.``RaceBetweenCancellationAndError.Sleep``() =
        testErrorAndCancelRace (Async.Sleep (-5))


    [<Test>]
    member this.``error on one workflow should cancel all others``() =
        let counter = 
            async {
                let counter = ref 0
                let job i = async { 
                    if i = 55 then failwith "boom" 
                    else 
                        do! Async.Sleep 1000 
                        incr counter 
                }

                let! _ = Async.Parallel [ for i in 1 .. 100 -> job i ] |> Async.Catch
                do! Async.Sleep 5000
                return !counter
            } |> Async.RunSynchronously

        Assert.AreEqual(0, counter)

    [<Test>]
    member this.``AwaitWaitHandle.ExceptionsAfterTimeout``() = 
        let wh = new System.Threading.ManualResetEvent(false)
        let test = async {
            try
                let! timeout = Async.AwaitWaitHandle(wh, 1000)
                do! Async.Sleep 500
                raise (new InvalidOperationException("EXPECTED"))
                return Assert.Fail("Should not get here")
            with
                :? InvalidOperationException as e when e.Message = "EXPECTED" -> return ()
            }
        Async.RunSynchronously(test)
        
#if FSHARP_CORE_NETCORE_PORTABLE
// nothing
#else
    [<Test>]
    member this.``FromContinuationsCanTailCallCurrentThread``() = 
        let cnt = ref 0
        let origTid = System.Threading.Thread.CurrentThread.ManagedThreadId 
        let finalTid = ref -1
        let rec f n =
            if n = 0 then
                async { 
                    finalTid := System.Threading.Thread.CurrentThread.ManagedThreadId
                    return () }
            else
                async {
                    incr cnt
                    do! Async.FromContinuations(fun (k,_,_) -> k())
                    do! f (n-1) 
                }
        // 5000 is big enough that does-not-stackoverflow means we are tailcalling thru FromContinuations
        f 5000 |> Async.StartImmediate 
        Assert.AreEqual(origTid, !finalTid)
        Assert.AreEqual(5000, !cnt)
#endif

    [<Test>]
    member this.``AwaitWaitHandle With Cancellation``() = 
        let run wh = async {
            let! r = Async.AwaitWaitHandle wh
            Assert.IsTrue(r, "Timeout not expected")
            return() 
            }
        let test () = 
            let wh = new System.Threading.ManualResetEvent(false)
            let cts = new System.Threading.CancellationTokenSource()
            let asyncs =
                [ 
                  yield! List.init 100 (fun _ -> run wh)
                  yield async { cts.Cancel() }
                  yield async { wh.Set() |> ignore }
                ]
            try
                asyncs
                  |> Async.Parallel
                  |> fun c -> Async.RunSynchronously(c, cancellationToken = cts.Token)
                  |> ignore
            with
#if FX_NO_OPERATION_CANCELLED
                _ -> ()
#else
                :? System.OperationCanceledException -> () // OK
#endif
        for _ in 1..1000 do test()

    [<Test>]
    member this.``StartWithContinuationsVersusDoBang``() = 
        // worthwhile to note these three
        // case 1
        let r = ref ""
        async {
            try
                do! Async.FromContinuations(fun (s, _, _) -> s())
                return failwith "boom"
            with
                e-> r := e.Message 
            } |> Async.RunSynchronously 
        Assert.AreEqual("boom", !r)
        // case 2
        r := ""
        try
            Async.StartWithContinuations(Async.FromContinuations(fun (s, _, _) -> s()), (fun () -> failwith "boom"), (fun e -> r := e.Message), (fun oce -> ()))
        with
            e -> r := "EX: " + e.Message
        Assert.AreEqual("EX: boom", !r)
        // case 3
        r := ""
        Async.StartWithContinuations(async { return! failwith "boom" }, (fun x -> ()), (fun e -> r := e.Message), (fun oce -> ()))
        Assert.AreEqual("boom", !r)


#if FSHARP_CORE_PORTABLE
// nothing
#else
#if FSHARP_CORE_NETCORE_PORTABLE
// nothing
#else
    [<Test>]
    member this.``SleepContinuations``() = 
        let okCount = ref 0
        let errCount = ref 0
        let test() = 
            let cts = new System.Threading.CancellationTokenSource() 
 
            System.Threading.ThreadPool.QueueUserWorkItem(fun _-> 
                System.Threading.Thread.Sleep 50 
                try 
                    Async.StartWithContinuations( 
                        Async.Sleep(1000), 
                        (fun _ -> printfn "ok"; incr okCount), 
                        (fun _ -> printfn "error"; incr errCount), 
                        (fun _ -> printfn "cancel"; failwith "BOOM!"), 
                        cancellationToken = cts.Token 
                    ) 
                with _ -> () 
            ) |> ignore 
            System.Threading.Thread.Sleep 50 
            try cts.Cancel() with _ -> () 
            System.Threading.Thread.Sleep 1500 
            printfn "====" 
        for i = 1 to 20 do test()
        Assert.AreEqual(0, !okCount)
        Assert.AreEqual(0, !errCount)
#endif
#endif

#if FSHARP_CORE_PORTABLE
// nothing
#else
#if FSHARP_CORE_2_0
// nothing
#else
#if FSHARP_CORE_NETCORE_PORTABLE
//nothing
#else
// we are on the desktop
    member this.RunExeAndExpectOutput(exeName, expected:string) =
        let curDir = (new Uri(System.Reflection.Assembly.GetExecutingAssembly().CodeBase)).LocalPath |> System.IO.Path.GetDirectoryName
        let psi = System.Diagnostics.ProcessStartInfo(exeName)
        psi.WorkingDirectory <- curDir
        psi.RedirectStandardOutput <- true
        psi.UseShellExecute <- false
        let p = System.Diagnostics.Process.Start(psi)
        let out = p.StandardOutput.ReadToEnd()
        p.WaitForExit()
        let out = out.Replace("\r\n", "\n")
        let expected = expected.Replace("\r\n", "\n")
        Assert.AreEqual(expected, out)

    [<Test>]
    member this.``ContinuationsThreadingDetails.AsyncWithSyncContext``() =
        this.RunExeAndExpectOutput("AsyncWithSyncContext.exe", """
EmptyParallel [|("ok", true); ("caught:boom", true)|]
NonEmptyParallel [|("ok", true); ("form exception:boom", true)|]
ParallelSeqArgumentThrows [|("error", true)|]
Sleep1Return [|("ok", true); ("form exception:boom", true)|]
Sleep0Return [|("ok", true); ("form exception:boom", true)|]
Return [|("ok", true); ("caught:boom", true)|]
FromContinuationsSuccess [|("ok", true); ("caught:boom", true)|]
FromContinuationsError [|("error", true)|]
FromContinuationsCancel [|("cancel", true)|]
FromContinuationsThrows [|("error", true)|]
FromContinuationsSchedulesFutureSuccess [|("ok", false); ("unhandled", false)|]
FromContinuationsSchedulesFutureError [|("error", false)|]
FromContinuationsSchedulesFutureCancel [|("cancel", false)|]
FromContinuationsSchedulesFutureSuccessAndThrowsQuickly [|("error", true); ("unhandled", false)|]
FromContinuationsSchedulesFutureErrorAndThrowsQuickly [|("error", true); ("unhandled", false)|]
FromContinuationsSchedulesFutureCancelAndThrowsQuickly [|("error", true); ("unhandled", false)|]
FromContinuationsSchedulesFutureSuccessAndThrowsSlowly [|("ok", false); ("unhandled", false);
  ("caught:A continuation provided by Async.FromContinuations was invoked multiple times",
   true)|]
FromContinuationsSchedulesFutureErrorAndThrowsSlowly [|("error", false);
  ("caught:A continuation provided by Async.FromContinuations was invoked multiple times",
   true)|]
FromContinuationsSchedulesFutureCancelAndThrowsSlowly [|("cancel", false);
  ("caught:A continuation provided by Async.FromContinuations was invoked multiple times",
   true)|]
AwaitWaitHandleAlreadySignaled0 [|("ok", true); ("caught:boom", true)|]
AwaitWaitHandleAlreadySignaled1 [|("ok", true); ("form exception:boom", true)|]
"""               )
    [<Test>]
    member this.``ContinuationsThreadingDetails.AsyncSansSyncContext``() =
        this.RunExeAndExpectOutput("AsyncSansSyncContext.exe", """
EmptyParallel [|("ok", true); ("caught:boom", true)|]
NonEmptyParallel [|("ok", false); ("unhandled", false)|]
ParallelSeqArgumentThrows [|("error", true)|]
Sleep1Return [|("ok", false); ("unhandled", false)|]
Sleep0Return [|("ok", false); ("unhandled", false)|]
Return [|("ok", true); ("caught:boom", true)|]
FromContinuationsSuccess [|("ok", true); ("caught:boom", true)|]
FromContinuationsError [|("error", true)|]
FromContinuationsCancel [|("cancel", true)|]
FromContinuationsThrows [|("error", true)|]
FromContinuationsSchedulesFutureSuccess [|("ok", false); ("unhandled", false)|]
FromContinuationsSchedulesFutureError [|("error", false)|]
FromContinuationsSchedulesFutureCancel [|("cancel", false)|]
FromContinuationsSchedulesFutureSuccessAndThrowsQuickly [|("error", true); ("unhandled", false)|]
FromContinuationsSchedulesFutureErrorAndThrowsQuickly [|("error", true); ("unhandled", false)|]
FromContinuationsSchedulesFutureCancelAndThrowsQuickly [|("error", true); ("unhandled", false)|]
FromContinuationsSchedulesFutureSuccessAndThrowsSlowly [|("ok", false); ("unhandled", false);
  ("caught:A continuation provided by Async.FromContinuations was invoked multiple times",
   true)|]
FromContinuationsSchedulesFutureErrorAndThrowsSlowly [|("error", false);
  ("caught:A continuation provided by Async.FromContinuations was invoked multiple times",
   true)|]
FromContinuationsSchedulesFutureCancelAndThrowsSlowly [|("cancel", false);
  ("caught:A continuation provided by Async.FromContinuations was invoked multiple times",
   true)|]
AwaitWaitHandleAlreadySignaled0 [|("ok", true); ("caught:boom", true)|]
AwaitWaitHandleAlreadySignaled1 [|("ok", false); ("unhandled", false)|]
"""               )

#endif
#endif
#endif