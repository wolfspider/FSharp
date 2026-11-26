module HttpStreamReader

open System
open System.Buffers
open System.IO
open System.Text
open HttpHeaders
open HttpData
open HttpLogger
open Utils

exception InvalidHttpRequest of string
exception NoHttpRequest

type HttpStreamReader(stream: Stream) =
    let buffer: byte[] = ArrayPool<byte>.Shared.Rent(32768)
    let mutable position: int = 0
    let mutable available: int = 0
    
    let tryParseRequestLine (line: string) : (string * string * string) option =
        if isNull line then None
        else
            let span = line.AsSpan()

            let sp1 = span.IndexOf(' ')
            if sp1 <= 0 then None
            else
                let rest1 = span.Slice(sp1 + 1)
                let sp2 = rest1.IndexOf(' ')
                if sp2 <= 0 then None
                else
                    let sp2Abs = sp1 + 1 + sp2

                    let mspan = span.Slice(0, sp1)
                    let pspan = span.Slice(sp1 + 1, sp2Abs - (sp1 + 1))
                    let vspan = span.Slice(sp2Abs + 1)

                    if mspan.IsEmpty || pspan.IsEmpty || vspan.IsEmpty then None
                    else
                        // method must be A-Z+
                        let mutable ok = true
                        let mutable i = 0
                        while ok && i < mspan.Length do
                            let c = mspan.[i]
                            if c < 'A' || c > 'Z' then ok <- false
                            i <- i + 1

                        if not ok then None
                        elif not (vspan.StartsWith("HTTP/".AsSpan(), StringComparison.Ordinal)) then None
                        else
                            let verSpan = vspan.Slice(5)
                            if verSpan.IsEmpty then None
                            else
                                // Path decode: '+' should NOT become space in path
                                let path =
                                    // tiny fast-path: only decode if needed
                                    if pspan.IndexOf('%') < 0 then pspan.ToString()
                                    else Utils.urlDecodeSpanFast pspan false

                                Some(mspan.ToString(), path, verSpan.ToString())


    static member LF = Convert.ToByte('\n')
    static member CR = Convert.ToByte('\r')

    member _.Stream = stream

    interface IDisposable with
        member _.Dispose() =
            if not (isNull stream) then
                noexn (fun () -> stream.Close())
            ArrayPool<byte>.Shared.Return(buffer)

    static member private DecodeLine(bytes: Span<byte>) =
        let length =
            if bytes.Length > 0 && bytes.[bytes.Length - 1] = 13uy then bytes.Length - 1
            else bytes.Length
        Encoding.ASCII.GetString(Span<byte>.op_Implicit(bytes.Slice(0, length)))

    member private _.ReadLineAsync(stop: Threading.CancellationToken) = task {
        let mutable fragments: byte[] = null
        let mutable used = 0

        // Span values stay within this synchronous scan of the current buffer.
        let scan () =
            let bytes = buffer.AsSpan(position, available - position)
            let lf = bytes.IndexOf(10uy)
            let count = if lf >= 0 then lf else bytes.Length
            let part = bytes.Slice(0, count)
            if part.IndexOfAnyInRange(128uy, 255uy) >= 0 then raise (DecoderFallbackException())
            position <- position + count + (if lf >= 0 then 1 else 0)
            if lf >= 0 && isNull fragments then HttpStreamReader.DecodeLine(part)
            else
                let needed = Checked.(+) used count
                if isNull fragments || fragments.Length < needed then
                    let next = ArrayPool<byte>.Shared.Rent(needed)
                    if not (isNull fragments) then
                        fragments.AsSpan(0, used).CopyTo(next.AsSpan())
                        ArrayPool<byte>.Shared.Return(fragments)
                    fragments <- next
                part.CopyTo(fragments.AsSpan(used))
                used <- needed
                if lf >= 0 then HttpStreamReader.DecodeLine(fragments.AsSpan(0, used)) else null

        let mutable line: string = null
        let mutable eof = false
        try
            while isNull line && not eof do
                if position = available then
                    let! count = stream.ReadAsync(buffer.AsMemory(), stop)
                    position <- 0
                    available <- count
                if available > 0 then line <- scan ()
                else eof <- true
            if eof && used > 0 then return HttpStreamReader.DecodeLine(fragments.AsSpan(0, used))
            else return line
        finally
            if not (isNull fragments) then ArrayPool<byte>.Shared.Return(fragments)
    }

    member self.ReadRequestAsync(stop: Threading.CancellationToken) = task {
        let! first = self.ReadLineAsync(stop)
        if isNull first then raise NoHttpRequest
        let mutable httpcmd = first
        let headersb = HttpHeadersBuilder()
        let mutable complete = false
        while not complete do
            let! line = self.ReadLineAsync(stop)
            if isNull line then raise NoHttpRequest
            elif line = "" then complete <- true
            else
                try
                    match HttpHeaderParser.tryParseHeaderLine line with
                    | Some(Choice1Of2(name, value)) -> headersb.Push name value
                    | Some(Choice2Of2(value)) -> headersb.PushContinuation value
                    | None -> httpcmd <- ""
                with InvalidHttpHeaderContinuation -> httpcmd <- ""

        let headers = headersb.Headers

        return
          match tryParseRequestLine httpcmd with
          | Some(methodStr, path, verStr) ->
            let version = httpversion_of_string verStr
            let httpmth = methodStr.ToUpperInvariant()

            { version = version
              mthod = httpmth
              path = path
              headers = headers }

          | None ->
            { version = httpversion_of_string ""
              mthod = ""
              path = ""
              headers = headers }

    }

    member self.ReadRequest() =
        self.ReadRequestAsync(Threading.CancellationToken.None).GetAwaiter().GetResult()
