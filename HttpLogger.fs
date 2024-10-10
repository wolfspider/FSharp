module HttpLogger

open System
open System.Threading

type level =
    | DEBUG
    | INFO
    | ERROR

type HttpLogger() =
    static let mutable loglevel: level = INFO

    static member private lock = new Object()

    static member Level
        with get () = loglevel
        and set newlevel = loglevel <- newlevel

    static member private WriteLine(s: string) =
        lock HttpLogger.lock (fun () -> Console.WriteLine(s))

    static member Log level message =
        if level >= loglevel then
            begin
                HttpLogger.WriteLine(
                    String.Format("[Thread {0}] [{1}] {2}", Thread.CurrentThread.ManagedThreadId, DateTime.Now, message)
                )
            end

    static member Debug message = HttpLogger.Log DEBUG message

    static member Info message = HttpLogger.Log INFO message

    static member Error message = HttpLogger.Log ERROR message
