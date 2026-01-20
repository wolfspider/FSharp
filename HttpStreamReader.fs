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
    
        // ---------- Fast parsing helpers (Span-based) ----------

    let isUpperAZ (c: char) = c >= 'A' && c <= 'Z'

    let isHeaderNameChar (c: char) =
        (c >= 'a' && c <= 'z') ||
        (c >= 'A' && c <= 'Z') ||
        (c >= '0' && c <= '9') ||
        c = '-'

    let isValidHeaderName (name: ReadOnlySpan<char>) =
        if name.IsEmpty then false
        else
            let mutable ok = true
            let mutable i = 0
            while ok && i < name.Length do
                if not (isHeaderNameChar name.[i]) then ok <- false
                i <- i + 1
            ok

    let tryParseHeaderLine (line: string) : Choice<(string * string), string> option =
        let span = line.AsSpan()
        if span.IsEmpty then None
        else
            // continuation line (obs-fold)
            if span.[0] = ' ' || span.[0] = '\t' then
                let v = span.Trim()
                if v.IsEmpty then None else Some(Choice2Of2(v.ToString()))
            else
                let colon = span.IndexOf(':')
                if colon <= 0 then None
                else
                    let nameSpan = span.Slice(0, colon).Trim()
                    if not (isValidHeaderName nameSpan) then None
                    else
                        let valueSpan = span.Slice(colon + 1).Trim()
                        Some(Choice1Of2(nameSpan.ToString(), valueSpan.ToString()))

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

    member private _.EnsureAvailable() =
        if position = available then
            position <- 0
            let t = stream.ReadAsync(buffer, 0, buffer.Length)
            available <- t.Result

        position < available

    member private self.ReadLine() : string =
        // If we have no data at all, it's EOF
        if not (self.EnsureAvailable()) then
            null
        else
            let CR = 13uy
            let LF = 10uy

            // Common case: line fits in the current buffer -> no copying
            let span0 = Span<byte>(buffer, position, available - position)
            let nl0 = span0.IndexOf(LF)

            if nl0 >= 0 then
                // line ends in this buffer
                let mutable len = nl0

                // trim trailing '\r' if present
                if len > 0 && span0.[len - 1] = CR then
                    len <- len - 1

                // validate ASCII
                for i = 0 to len - 1 do
                    if span0.[i] > 127uy then
                        raise (DecoderFallbackException())

                let s = Encoding.ASCII.GetString(buffer, position, len)
                position <- position + nl0 + 1 // consume '\n'
                s

            else
                // Slow path: line spans buffers -> accumulate into pooled temp
                let mutable tmp = ArrayPool<byte>.Shared.Rent(4096)
                let mutable tmpLen = 0
                let mutable doneLine = false
                let mutable sawAny = false

                let inline ensureCapacity (need: int) =
                    if need > tmp.Length then
                        let newSize = max (tmp.Length * 2) need
                        let tmp2 = ArrayPool<byte>.Shared.Rent(newSize)
                        Buffer.BlockCopy(tmp, 0, tmp2, 0, tmpLen)
                        ArrayPool<byte>.Shared.Return(tmp)
                        tmp <- tmp2

                try
                    while not doneLine do
                        if not (self.EnsureAvailable()) then
                            // EOF
                            doneLine <- true
                        else
                            let span = Span<byte>(buffer, position, available - position)
                            let nl = span.IndexOf(LF)

                            if nl >= 0 then
                                // copy bytes up to '\n'
                                let chunkLen = nl
                                if chunkLen > 0 then
                                    sawAny <- true
                                    ensureCapacity (tmpLen + chunkLen)

                                    // validate + copy
                                    for i = 0 to chunkLen - 1 do
                                        let b = span.[i]
                                        if b > 127uy then raise (DecoderFallbackException())
                                        tmp.[tmpLen + i] <- b

                                    tmpLen <- tmpLen + chunkLen

                                // consume through '\n'
                                position <- position + nl + 1
                                doneLine <- true
                            else
                                // no '\n' here, copy all remaining bytes
                                let chunkLen = span.Length
                                if chunkLen > 0 then
                                    sawAny <- true
                                    ensureCapacity (tmpLen + chunkLen)

                                    for i = 0 to chunkLen - 1 do
                                        let b = span.[i]
                                        if b > 127uy then raise (DecoderFallbackException())
                                        tmp.[tmpLen + i] <- b

                                    tmpLen <- tmpLen + chunkLen

                                // consume all available
                                position <- available

                    // If we hit EOF and never collected anything, return null
                    if not sawAny && tmpLen = 0 then
                        null
                    else
                        // trim trailing '\r'
                        if tmpLen > 0 && tmp.[tmpLen - 1] = CR then
                            tmpLen <- tmpLen - 1

                        Encoding.ASCII.GetString(tmp, 0, tmpLen)

                finally
                    ArrayPool<byte>.Shared.Return(tmp)

    // ---------- ReadRequest ----------

    member self.ReadRequest() =
        let mutable httpcmd = self.ReadLine()
        let headersb = HttpHeadersBuilder()
        let isvalid = ref true

        // header loop
        let rec readheaders () =
            let line = self.ReadLine()

            if isNull line then
                // EOF before blank line terminator -> invalid request
                isvalid.Value <- false
            elif line <> "" then
                try
                    match tryParseHeaderLine line with
                    | Some(Choice1Of2(name, value)) ->
                        headersb.Push name value
                    | Some(Choice2Of2(value)) ->
                        headersb.PushContinuation value
                    | None ->
                        isvalid.Value <- false
                with InvalidHttpHeaderContinuation ->
                    isvalid.Value <- false

                readheaders ()

        if isNull httpcmd then
            isvalid.Value <- false
            httpcmd <- ""

        // IMPORTANT: actually consume headers
        readheaders ()

        if not isvalid.Value then
            httpcmd <- ""

        let headers = headersb.Headers

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
