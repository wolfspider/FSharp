module HttpServer

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading
open System.Threading.Tasks
open System.Collections.Concurrent
open HttpHeaders
open HttpStreamReader
open HttpData
open HttpLogger
open Utils
open Fiber


exception HttpResponseExnException of HttpResponse

let HttpResponseExnWithCode =
    fun code -> HttpResponseExnException(http_response_of_code code)

type HttpClientHandler(server: HttpServer, peer: TcpClient) =
    let stream = peer.GetStream()
    let reader = new HttpStreamReader(stream)
    let mutable handlers = []

    interface IDisposable with
        member _.Dispose() =
            (reader :> IDisposable).Dispose()
            peer.Close()

    member private _.SendHead version code (headers: seq<string * string>) (stop: CancellationToken) =
        let head = StringBuilder()
        head.AppendFormat("HTTP/{0} {1} {2}\r\n", string_of_httpversion version, HttpCode.code code, HttpCode.http_status code) |> ignore
        for name, value in headers do
            head.Append(name).Append(": ").Append(value).Append("\r\n") |> ignore
        head.Append("\r\n") |> ignore
        let bytes = Encoding.ASCII.GetBytes(head.ToString())
        Fiber.awaitTask (fun () -> task { do! stream.WriteAsync(bytes, 0, bytes.Length, stop) })

    member private _.ResponseOfStream (fi: FileInfo) (stream: Stream) =
        let ctype =
            match server.Config.mimesmap.Lookup(Path.GetExtension(fi.FullName)) with
            | Some ctype -> ctype
            | None -> "text/plain" in

        { code = HttpCode.HTTP_200
          headers = HttpHeaders.OfList [ (CONTENT_TYPE, ctype) ]
          body = HB_Stream(stream, fi.Length) }


    member private self.ServeStatic(request: HttpRequest) =
        let path = HttpServer.CanonicalPath request.path in
        let path = if path.Equals("") then "index.html" else path
        let path = Path.Combine(server.Config.docroot, path) in

        if request.mthod <> "GET" then
            begin raise (HttpResponseExnWithCode HttpCode.HTTP_400) end

        try
            let infos = FileInfo(path) in

            if not infos.Exists then
                begin raise (HttpResponseExnWithCode HttpCode.HTTP_404) end

            let input =
                try
                    new FileStream(infos.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous ||| FileOptions.SequentialScan)
                with :? IOException ->
                    raise (HttpResponseExnWithCode HttpCode.HTTP_500)

            self.ResponseOfStream infos input
        with
        | :? UnauthorizedAccessException -> raise (HttpResponseExnWithCode HttpCode.HTTP_403)
        | :? PathTooLongException
        | :? NotSupportedException
        | :? ArgumentException -> raise (HttpResponseExnWithCode HttpCode.HTTP_404)

    member private self.ServeRequest(request: HttpRequest, stop: CancellationToken) =
        match List.tryPick (fun handler -> handler request) handlers with
        | Some status -> Fiber.success status
        | None ->
            let close =
                match request.version with
                | HTTPV_10 ->
                    match request.headers.Get "Connection" with
                    | Some v when v.Equals("keep-alive", StringComparison.OrdinalIgnoreCase) -> false
                    | _ -> true
                | _ ->
                    match request.headers.Get "Connection" with
                    | Some v when v.Equals("close", StringComparison.OrdinalIgnoreCase) -> true
                    | _ -> false

            let response =
                try
                    self.ServeStatic request
                with
                | :? IOException as e -> raise e
                | HttpResponseExnException response -> response
                | _ -> http_response_of_code HttpCode.HTTP_500 in

            if close then
                begin response.headers.Set "Connection" "close" end

            response.headers.Set "Content-Length" (String.Format("{0}", (http_body_length response.body)))

            let head () = self.SendHead request.version response.code (response.headers.ToSeq()) stop
            let send =
                match response.body with
                | HB_Raw bytes -> fib {
                    do! head ()
                    if bytes.Length <> 0 then
                        return! Fiber.awaitTask (fun () -> task { do! stream.WriteAsync(bytes, 0, bytes.Length, stop) })
                    else return ()
                  }
                | HB_Stream(f, flen) ->
                    Fiber.finallyDo (fun () -> noexn (fun () -> f.Close())) (fun () ->
                        fib {
                            do! head ()

                            return! Fiber.effect (fun () ->
                                try
                                    let fa = Fiber.atom (fun () -> f)

                                    Fiber.swap fa (fun getFile ->
                                        let input = getFile ()
                                        if input.CopyTo(stream, flen) < flen then
                                            failwith "ReadAndServeRequest: short-read"
                                        getFile)
                                    |> ignore
                                finally
                                    noexn (fun () -> stream.Flush()))
                        })
            Fiber.map (fun () -> not close) send

    member internal self.RunAsync(scheduler: IScheduler, requests: SemaphoreSlim, stop: CancellationToken) = task {
        peer.NoDelay <- true
        peer.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true)
        let mutable keepAlive = true
        try
            while keepAlive && not stop.IsCancellationRequested do
                let! request = reader.ReadRequestAsync(stop)
                do! requests.WaitAsync(stop)
                try
                    let response = fib {
                        let! send = Fiber.effect (fun () -> self.ServeRequest(request, stop))
                        return! send
                    }
                    let! again =
                        Scheduler.runAsyncWith scheduler response
                        |> fun workflow -> Async.StartImmediateAsTask(workflow, cancellationToken = CancellationToken.None)
                    keepAlive <- again
                finally requests.Release() |> ignore
        with NoHttpRequest -> ()
    }

    member self.Start() =
        use requests = new SemaphoreSlim(1)
        try self.RunAsync(Scheduler.shared, requests, CancellationToken.None).GetAwaiter().GetResult()
        finally peer.Close()

and HttpServer(localaddr: IPEndPoint, config: HttpServerConfig) =
    let config: HttpServerConfig = config
    let lifecycle = obj()
    let mutable running: CancellationTokenSource option = None

    interface IDisposable with
        member _.Dispose() =
            lock lifecycle (fun () -> running |> Option.iter (fun stop -> stop.Cancel()))

    member self.Config = config

    static member CanonicalPath(path: string) =
        let path =
            path.Split('/')
            |> Array.fold
                (fun canon segment ->
                    match canon, segment with
                    | _, "" -> canon
                    | _, "." -> canon
                    | _ :: ctail, ".." -> ctail
                    | [], ".." -> []
                    | _, segment -> segment :: canon)
                [] in

        String.Join("/", Array.ofList (List.rev path))

    member self.Start(workerCount: int, maxWorkers: int) =
        if workerCount < 1 then invalidArg "workerCount" "Expected at least one worker."
        if maxWorkers < workerCount then invalidArg "maxWorkers" "Maximum must cover the initial pool."
        let readSetting name fallback =
            match Environment.GetEnvironmentVariable(name) with
            | null -> fallback
            | value ->
                match Int32.TryParse(value) with
                | true, n when n > 0 -> n
                | _ -> invalidArg name "Expected a positive integer."
        let backlog = readSetting "FSHARP_HTTP_BACKLOG" 1024
        let maxClients = readSetting "FSHARP_HTTP_MAX_CONNECTIONS" 1024
        use stop = new CancellationTokenSource()
        let token = stop.Token
        let listener = new TcpListener(localaddr)
        use slots = new SemaphoreSlim(maxClients, maxClients)
        use requests = new SemaphoreSlim(maxWorkers, maxWorkers)
        use clients = new CountdownEvent(1)
        use jobs = new BlockingCollection<unit -> unit>()
        let workers = ResizeArray<Thread>()
        let gate = obj()
        let mutable outstanding = 0

        let startWorker () =
            let run () =
                let local = Scheduler.TestScheduler(DateTime.UtcNow) :> IScheduler
                for job in jobs.GetConsumingEnumerable() do local.Schedule(job)
            let worker = Thread(ThreadStart(run), IsBackground = true)
            worker.Name <- sprintf "http-request-%d" (workers.Count + 1)
            worker.Start()
            workers.Add(worker)

        let schedule work =
            lock gate (fun () ->
                if outstanding >= workers.Count && workers.Count < maxWorkers then
                    try startWorker ()
                    with ex -> eprintfn "Request worker: %s" ex.Message
                outstanding <- outstanding + 1
                jobs.Add(fun () ->
                    try work()
                    finally lock gate (fun () -> outstanding <- outstanding - 1)))

        let scheduler =
            { new IScheduler with
                member _.Schedule work = schedule work
                member _.Delay(delay, work) = Scheduler.shared.Delay(delay, fun () -> schedule work) }

        let serve (client: TcpClient) = task {
            let mutable remote: EndPoint = null
            try
                try
                    use peer = client
                    remote <- peer.Client.RemoteEndPoint
                    use registration = token.Register(fun () -> peer.Close())
                    use handler = new HttpClientHandler(self, peer)
                    do! handler.RunAsync(scheduler, requests, token)
                with
                | _ when token.IsCancellationRequested -> ()
                | :? IOException as ex when (match ex.InnerException with
                                             | :? SocketException as socketError ->
                                                 socketError.SocketErrorCode = SocketError.ConnectionReset ||
                                                 ((OperatingSystem.IsFreeBSD() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) &&
                                                  socketError.NativeErrorCode = 32) // EPIPE
                                             | _ -> false) ->
                    let socketError = ex.InnerException :?> SocketException
                    Console.Error.WriteLine(sprintf "[%s] peer=%O socket disconnect: %s (errno=%d)"
                                                (DateTimeOffset.UtcNow.ToString("O")) remote
                                                socketError.Message socketError.NativeErrorCode)
                | ex -> Console.Error.WriteLine(sprintf "[%O] Client: %O" DateTimeOffset.UtcNow ex)
            finally
                slots.Release() |> ignore
                clients.Signal() |> ignore
        }

        let accept () = task {
            try
                while not token.IsCancellationRequested do
                    do! slots.WaitAsync(token)
                    try
                        let! peer = listener.AcceptTcpClientAsync(token)
                        clients.AddCount()
                        serve peer |> ignore
                    with :? SocketException as ex when not token.IsCancellationRequested ->
                        slots.Release() |> ignore
                        eprintfn "Accept: %s" ex.Message
                        do! Task.Delay(100, token)
            with
            | _ when token.IsCancellationRequested -> ()
        }

        lock lifecycle (fun () ->
            if running.IsSome then invalidOp "Server already running."
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true)
            listener.Server.LingerState <- LingerOption(false, 0)
            listener.Start(backlog)
            running <- Some stop)

        try
            lock gate (fun () -> for _ in 1 .. workerCount do startWorker ())
            let accepting = accept ()
            try
                printfn "Listening on %O; backlog requested %d, max connections %d, request workers %d..%d." localaddr backlog maxClients workerCount maxWorkers
                Console.ReadKey() |> ignore
            finally
                stop.Cancel()
                listener.Stop()
                accepting.GetAwaiter().GetResult()
        finally
            stop.Cancel()
            listener.Stop()
            clients.Signal() |> ignore
            clients.Wait()
            jobs.CompleteAdding()
            for worker in workers do worker.Join()
            lock lifecycle (fun () -> running <- None)

    member self.Start(workerCount: int) = self.Start(workerCount, workerCount)

    member self.Start() =
        let readCount name fallback =
            match Environment.GetEnvironmentVariable(name) with
            | null -> fallback
            | value ->
                match Int32.TryParse(value) with
                | true, n when n > 0 -> n
                | _ -> invalidArg name "Expected a positive integer."
        let initial = readCount "FSHARP_HTTP_WORKERS" 32
        self.Start(initial, readCount "FSHARP_HTTP_MAX_WORKERS" (max initial 128))

let run =
    fun config ->
        use http = new HttpServer(config.localaddr, config)
        http.Start()
