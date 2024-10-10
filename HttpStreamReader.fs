module HttpStreamReader

open System
open System.IO
open System.Text

open HttpHeaders
open HttpData
open HttpLogger
open Utils

exception InvalidHttpRequest of string
exception NoHttpRequest

type HttpStreamReader(stream: Stream) =
    let (*---*) buffer: byte[] = Array.zeroCreate 65536
    let mutable position: int = 0
    let mutable available: int = 0

    static member LF = Convert.ToByte('\n')
    static member CR = Convert.ToByte('\r')

    member self.Stream = stream

    interface IDisposable with
        member self.Dispose() =
            if not (isNull stream) then
                Utils.noexn (fun () -> stream.Close())

    member private self.EnsureAvailable() =

        if position = available then
            begin
                position <- 0
                let str = stream.ReadAsync(buffer, 0, buffer.Length)
                available <- str.Result
            end

        position < available

    member private self.ReadLine() : string =
        let (*---*) output = StringBuilder() in

        let mutable crlf = false in
        let mutable eol = false in
        let (*---*) eof = not (self.EnsureAvailable()) in

        while not eol do
            if self.EnsureAvailable() then
                while position < available && not eol do
                    let b = buffer.[position] in
                    let c = Convert.ToChar(b) in

                    position <- position + 1

                    if b > 127uy then
                        raise (DecoderFallbackException())

                    if c = '\n' then
                        eol <- true
                    else
                        if crlf then
                            ignore (output.Append '\r')

                        if c = '\r' then
                            crlf <- true
                        else
                            crlf <- false
                            ignore (output.Append(Convert.ToChar(b)))
            else
                eol <- true

        if eof && (output.Length = 0) then
            null
        else
            begin
                (*HttpLogger.Debug ("<-- " + (output.ToString ()));*)
                output.ToString()
            end

    member self.ReadRequest() =
        let mutable httpcmd = self.ReadLine() in
        let (*---*) headers = HttpHeadersBuilder() in
        let (*---*) isvalid = ref true in

        if isNull httpcmd then
            begin () end

        let rec readheaders =
            fun () ->
                let line = self.ReadLine() in

                if isNull line then
                    isvalid.Value <- false
                elif line <> "" then
                    try
                        match line.Trim() with
                        | Match "^(?<name>[a-zA-Z0-9-]+)\s*:\s*(?<value>.*)$" m -> headers.Push m.["name"] m.["value"]
                        | Match "^\s+(?<value>.*)\s+$" m -> headers.PushContinuation m.["value"]
                        | _ -> isvalid.Value <- false
                    with InvalidHttpHeaderContinuation ->
                        isvalid.Value <- false

                    readheaders () in

        readheaders ()

        if isNull httpcmd then
            begin
                isvalid.Value <- false
                httpcmd <- ""
            end

        if not isvalid.Value then
            begin
                isvalid.Value <- false
                httpcmd <- ""
            end

        match httpcmd with
        | Match "^(?<method>[A-Z]+) (?<path>\S+) HTTP/(?<version>(:?\d+\.\d+))$" m ->
            let version = httpversion_of_string m.["version"] in
            let httpmth = m.["method"].ToUpperInvariant() in
            let path = m.["path"].UrlDecode() in
            let headers = headers.Headers in

            { version = version
              mthod = httpmth
              path = path
              headers = headers }
        | _ ->
            let version = "" in
            let httpmth = "" in
            let path = "" in
            let headers = headers.Headers in

            { version = httpversion_of_string version
              mthod = httpmth
              path = path
              headers = headers }
