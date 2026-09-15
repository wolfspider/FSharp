module HttpServer

open System
open System.IO
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.AspNetCore.StaticFiles
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Primitives
open HttpData

let private safeJoin (root: string) (relativePath: string) =
    let fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
    let prefix =
        if Path.EndsInDirectorySeparator(fullRoot) then fullRoot
        else fullRoot + string Path.DirectorySeparatorChar
    let fullPath = Path.GetFullPath(Path.Combine(root, relativePath))
    if not (fullPath.StartsWith(prefix, StringComparison.Ordinal)) then
        raise (UnauthorizedAccessException("Path traversal detected."))
    fullPath

// Keep the byref MIME lookup outside the task state machine.
let private contentType (provider: FileExtensionContentTypeProvider) (path: string) =
    let mutable value = ""
    if provider.TryGetContentType(path, &value) then value
    else "application/octet-stream"

let private fileHandler (root: string) =
    let contentTypes = FileExtensionContentTypeProvider()
    let indexPath = Path.Combine(root, "index.html")

    RequestDelegate(fun context ->
        task {
            let rawPath = context.Request.Path.Value
            let reqPath =
                if isNull rawPath || rawPath = "/" then "/index.html"
                else rawPath
            let relativePath = reqPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)
            let resolvedPath =
                try Some(safeJoin root relativePath)
                with _ -> None

            match resolvedPath with
            | None ->
                context.Response.StatusCode <- StatusCodes.Status403Forbidden
                do! context.Response.WriteAsync("Forbidden\n")
            | Some requestedFile ->
                // Match the C# application's index.html fallback.
                let filePath = if File.Exists(requestedFile) then requestedFile else indexPath
                if not (File.Exists(filePath)) then
                    context.Response.StatusCode <- StatusCodes.Status404NotFound
                    do! context.Response.WriteAsync(sprintf "Missing file: %s\n" filePath)
                else
                    context.Response.StatusCode <- StatusCodes.Status200OK
                    context.Response.ContentType <- contentType contentTypes filePath
                    context.Response.Headers.["Connection"] <- StringValues("keep-alive")
                    context.Response.Headers.["Cache-Control"] <- StringValues("no-store")

                    // Same constructor and copy overload as the supplied C#.
                    let fs =
                        new FileStream(
                            filePath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite,
                            128 * 1024,
                            FileOptions.SequentialScan)
                    // Explicit async disposal, equivalent to C# await using.
                    use lifetime = (fs :> IAsyncDisposable)
                    context.Response.ContentLength <- Nullable(fs.Length)
                    do! fs.CopyToAsync(context.Response.Body)
        } :> Task)

/// Install optional F# middleware before the catch-all file endpoint.
let runWith (configure: WebApplication -> unit) (config: HttpServerConfig) =
    let root = Path.GetFullPath(config.docroot)
    Directory.CreateDirectory(root) |> ignore

    let builder = WebApplication.CreateSlimBuilder(Array.empty<string>)
    builder.Logging.SetMinimumLevel(LogLevel.Warning) |> ignore
    builder.WebHost.ConfigureKestrel(fun options ->
        options.AddServerHeader <- false
        options.Listen(config.localaddr, fun endpoint ->
            // Existing listener is plaintext HTTP, despite using port 2443.
            endpoint.Protocols <- HttpProtocols.Http1)) |> ignore

    let app = builder.Build()
    try
        configure app
        app.Map("/{**path}", fileHandler root) |> ignore
        printfn "Serving %s at http://%O. Press Ctrl+C to stop." root config.localaddr
        app.Run()
    finally
        // Blocking only at host shutdown, never inside a request handler.
        app.DisposeAsync().AsTask().GetAwaiter().GetResult()

let run (config: HttpServerConfig) = runWith ignore config
