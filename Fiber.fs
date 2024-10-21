(* /// MIT License
/// 
/// Copyright (c) 2024 Bartosz Sypytkowski
/// 
/// Permission is hereby granted, free of charge, to any person obtaining a copy
/// of this software and associated documentation files (the "Software"), to deal
/// in the Software without restriction, including without limitation the rights
/// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
/// copies of the Software, and to permit persons to whom the Software is
/// furnished to do so, subject to the following conditions:
/// 
/// The above copyright notice and this permission notice shall be included in all
/// copies or substantial portions of the Software.
/// 
/// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
/// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
/// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
/// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
/// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
/// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
/// SOFTWARE.
///
 * Modifications:
 * --------------
 * Modified by: <[WolfSpider]>
 * Purpose: Added support for Atomics.
 * Date: 2024-10-10 
 * --------------
 * Modified by: <[WolfSpider]>
 * Purpose: Update Scheduler with verified formally code.
 * Date: 2024-10-16 *)


module Fiber

open System
open System.Threading

type FiberResult<'a> = Result<'a, exn> option

[<Sealed; AllowNullLiteral>]
type Cancel(parent: Cancel) =
    let mutable flag: int = 0
    let mutable children: Cancel list = []
    new() = Cancel(null)
    /// Check if token was cancelled
    member __.Cancelled = flag = 1

    /// Remove child token
    member private __.RemoveChild(child) =
        let rec loop child =
            let children' = children
            let nval = children' |> List.filter ((<>) child)

            if not (obj.ReferenceEquals(children', Interlocked.CompareExchange(&children, nval, children'))) then
                loop child

        if not (List.isEmpty children) then
            loop child

    /// Create a new child token and return it.
    member this.AddChild() =
        let rec loop child =
            let children' = children

            if
                (obj.ReferenceEquals(children', Interlocked.CompareExchange(&children, child :: children', children')))
            then
                child
            else
                loop child

        loop (Cancel this)

    /// Cancel a token
    member this.Cancel() =
        if Interlocked.Exchange(&flag, 1) = 0 then
            for child in Interlocked.Exchange(&children, []) do
                child.Cancel()

            if not (isNull parent) then
                parent.RemoveChild(this)

[<Interface>]
type IScheduler =
    abstract Schedule: (unit -> unit) -> unit
    abstract Delay: TimeSpan * (unit -> unit) -> unit

type Fiber<'a> = Fiber of (IScheduler * Cancel -> (FiberResult<'a> -> unit) -> unit)

[<RequireQualifiedAccess>]
module Fiber =

    /// Wraps value into fiber.
    let success r =
        Fiber <| fun (_, c) next -> if c.Cancelled then next None else next (Some(Ok r))

    /// Wraps exception into fiber.
    let fail e =
        Fiber
        <| fun (_, c) next -> if c.Cancelled then next None else next (Some(Error e))

    /// Returns a cancelled fiber.
    let cancelled<'a> = Fiber <| fun _ next -> next None

    /// Returns a fiber, which will delay continuation execution after a given timeout.
    let delay timeout =
        Fiber
        <| fun (s, c) next ->
            if c.Cancelled then
                next None
            else
                s.Delay(timeout, (fun () -> if c.Cancelled then next None else next (Some(Ok()))))

    /// Maps result of Fiber execution to another value and returns new Fiber with mapped value.
    let mapResult fn (Fiber call) =
        Fiber
        <| fun (s, c) next ->
            if c.Cancelled then
                next None
            else
                try
                    call (s, c) (fun result ->
                        if c.Cancelled then
                            next None
                        else
                            next (Option.map fn result))
                with e ->
                    next (Some(Error e))

    /// Maps successful result of Fiber execution to another value and returns new Fiber with mapped value.
    let map fn fiber = mapResult (Result.map fn) fiber

    /// Allows to recover from exception (if `fn` returns Ok) or recast it (if `fn` returns Error).
    let catch fn fiber =
        mapResult
            (function
            | Error e -> fn e
            | other -> other)
            fiber

    let bind fn (Fiber call) =
        Fiber
        <| fun (s, c) next ->
            if c.Cancelled then
                next None
            else
                try
                    call (s, c) (fun result ->
                        if c.Cancelled then
                            next None
                        else
                            match result with
                            | Some(Ok r) ->
                                let (Fiber call2) = fn r
                                call2 (s, c) next
                            | None -> next None
                            | Some(Error e) -> next (Some(Error e)))
                with e ->
                    next (Some(Error e))

    /// Starts both fibers running in parallel, returning the result from the winner
    /// (the one which completed first) while cancelling the other.
    let race (Fiber left) (Fiber right) : Fiber<Choice<'a, 'b>> =
        Fiber
        <| fun (s, c) next ->
            if c.Cancelled then
                next None
            else
                let mutable flag = 0
                let child = c.AddChild()

                let run fiber choice =
                    s.Schedule(fun () ->
                        fiber (s, child) (fun result ->
                            if Interlocked.Exchange(&flag, 1) = 0 then
                                child.Cancel()

                                if c.Cancelled then
                                    next None
                                else
                                    match result with
                                    | None -> next None
                                    | Some(Ok v) -> next (Some(Ok(choice v)))
                                    | Some(Error e) -> next (Some(Error e))))

                run left Choice1Of2
                run right Choice2Of2

    let timeout (t: TimeSpan) fiber =
        Fiber
        <| fun (s, c) next ->
            let (Fiber call) = race (delay t) fiber

            call (s, c) (fun result ->
                if c.Cancelled then
                    next None
                else
                    match result with
                    | None -> next None
                    | Some(Ok(Choice1Of2 _)) -> next None // timeout won
                    | Some(Ok(Choice2Of2 v)) -> next (Some(Ok v))
                    | Some(Error e) -> next (Some(Error e)))


    /// Executes a bunch of Fiber operations in parallel, returning an Fiber which may contain
    /// a gathered set of results or (potential) failures that have happened during the execution.
    let parallelfib fibs =
        Fiber
        <| fun (s, c) next ->
            if c.Cancelled then
                next None
            else
                let mutable remaining = Array.length fibs
                let successes = Array.zeroCreate remaining
                let childCancel = c.AddChild()

                fibs
                |> Array.iteri (fun i (Fiber call) ->
                    s.Schedule(fun () ->
                        call (s, childCancel) (fun result ->
                            match result with
                            | Some(Ok success) ->
                                successes.[i] <- success

                                if c.Cancelled && Interlocked.Exchange(&remaining, -1) > 0 then
                                    next None
                                elif Interlocked.Decrement(&remaining) = 0 then
                                    if c.Cancelled then next None else next (Some(Ok successes))
                            | Some(Error fail) ->
                                if Interlocked.Exchange(&remaining, -1) > 0 then
                                    childCancel.Cancel()
                                    if c.Cancelled then next None else next (Some(Error fail))
                            | None ->
                                if Interlocked.Exchange(&remaining, -1) > 0 then
                                    next None)))

    // Example of Atom type with swap function
    type Atom<'T when 'T: not struct>(value: 'T) =
        let refCell = ref value

        let rec swap f =
            let currentValue = refCell.Value
            let result = Interlocked.CompareExchange(refCell, f currentValue, currentValue)

            if obj.ReferenceEquals(result, currentValue) then
                result
            else
                delay (TimeSpan.FromTicks 20) |> ignore
                swap f

        //if obj.ReferenceEquals(result, currentValue) then result
        //else Thread.SpinWait 20; swap f
        member _.Value = refCell.Value
        member _.Swap(f: 'T -> 'T) = swap f

    let atom value = new Atom<_>(value)
    let swap (atom: Atom<_>) (f: _ -> _) = atom.Swap f

    // Blocking function example
    let blocking (s: IScheduler) (cancel: Cancel) (Fiber fn) =

        let rs = atom None

        s.Schedule(fun () ->
            fn (s, cancel) (fun result ->
                if not cancel.Cancelled then
                    swap rs (fun _ -> Some result) |> ignore))

        rs.Value

    /// Converts given Fiber into F# Async.
    let toAsync s (Fiber call) =
        Async.FromContinuations
        <| fun (onSuccess, onError, onCancel) ->
            call (s, Cancel())
            <| fun result ->
                match result with
                | None -> onCancel (OperationCanceledException "")
                | Some(Ok value) -> onSuccess value
                | Some(Error e) -> onError e

[<RequireQualifiedAccess>]
module Scheduler =

    /// Default environment, which is backed by .NET Thread pool.
    let shared =
        { new IScheduler with
            member __.Schedule fn =
                System.Threading.ThreadPool.QueueUserWorkItem(WaitCallback(ignore >> fn))
                |> ignore

            member __.Delay(timeout: TimeSpan, fn) =
                let mutable t = Unchecked.defaultof<Timer>

                let callback =
                    fun _ ->
                        t.Dispose()
                        fn ()
                        ()

                t <- new Timer(callback, null, int timeout.TotalMilliseconds, Timeout.Infinite) }

    type Task = { Time: int64; Func: unit -> unit }

    type TestScheduler(initialTime: DateTime) =
        let mutable running = false
        let mutable currentTime = initialTime.Ticks
        let mutable sortedTasks = []

        let insertTask delay fn =
            let at = currentTime + delay
            let newTask = { Time = at; Func = fn }

            // Partition the tasks into two lists: before and after the new task's time
            let tasksBefore, tasksAfter =
                List.partition (fun task -> task.Time <= at) sortedTasks

            // Insert the new task between tasksBefore and tasksAfter
            sortedTasks <- tasksBefore @ [ newTask ] @ tasksAfter

        let rec run () =
            match sortedTasks with
            | [] -> running <- false
            | { Time = time; Func = func } :: remainingTasks ->
                let now = DateTime.UtcNow.Ticks

                // Calculate the delay needed to wait for the next task
                let delay = time - now

                // If the next task is in the future, wait for that amount of time
                if delay > 0L then
                    // Convert delay from ticks to milliseconds (1 tick = 100ns)
                    let milliseconds = delay / TimeSpan.TicksPerMillisecond
                    Thread.Sleep(int milliseconds)

                // Update current time to the task's time
                currentTime <- time

                // Gather all tasks scheduled for the same time
                let sameTimeTasks, otherTasks =
                    List.partition (fun task -> task.Time = time) remainingTasks

                // Update the sortedTasks list with only the tasks for future times
                sortedTasks <- otherTasks

                // Execute all functions scheduled for this time
                for task in sameTimeTasks do
                    task.Func ()

                // Execute the current task function
                func ()

                // Continue with the remaining tasks
                run ()

        member __.UtcNow() = DateTime(currentTime)

        interface IScheduler with
            member _.Schedule fn =
                insertTask 0L fn

                if not running then
                    running <- true
                    run ()

            member _.Delay(timeout: TimeSpan, fn) = insertTask timeout.Ticks fn



    let testasync (fiber, cancel) =
        async {
            //let sh = shared
            let s = TestScheduler(DateTime.UtcNow)
            return! Fiber.toAsync s fiber
        }


    let test (fiber, cancel) =
        let s = TestScheduler(DateTime.UtcNow)
        Fiber.blocking s cancel fiber


[<Struct>]
type FiberBuilder =
    member inline __.Zero = Fiber.success (Unchecked.defaultof<_>)
    member inline __.ReturnFrom fib = fib
    member inline __.Return value = Fiber.success value
    member inline __.Bind(fib, fn) = Fiber.bind fn fib

[<AutoOpen>]
module FiberBuilder =

    let fib = FiberBuilder()

let demo () : int =
    //---------------------
    // run some actual code
    //---------------------

    let fib = FiberBuilder()
    let inline millis n = TimeSpan.FromMilliseconds(float n)

    let program =
        fib {
            let c =
                fib {
                    do! Fiber.delay (millis 3000)
                    Console.WriteLine("3 Second Timeout")
                    return 3
                }

            let a =
                fib {
                    do! Fiber.delay (millis 4000)
                    Console.WriteLine("4 Second Timeout")
                    return 4
                }

            let! d = a |> Fiber.race (c)

            let ch =
                match d with
                | Choice1Of2 t -> t
                | Choice2Of2 t -> t

            Console.WriteLine("4 Blocked against 5")
            let! b = a |> Fiber.timeout (millis 5000)

            printfn "Fiber Results: %A %A" b ch
            return b
        }

    let cancel = Cancel()
    let result = Scheduler.test (program, cancel)

    let rs =
        match result with
        | Some(v) ->
            match v with
            | Some(vv) ->
                match vv with
                | Ok(vvv) -> vvv
                | Error(_) -> 0
            | None -> 0
        | None -> 0


    printfn "Scheduler Result: %A" rs
    Console.ReadLine() |> ignore
    0 // return an integer exit code*)
