module Utils

open System
open System.Buffers
open System.IO
open System.Text
open System.Web
open FSharp.Control
open Fiber

(* ------------------------------------------------------------------------ *)
type String with
    member self.UrlDecode() = HttpUtility.UrlDecode(self)

(* ------------------------------------------------------------------------ *)
type Stream with
    member self.CopyTo(output: Stream, length: int64) : int64 =
        let buffer: byte[] = ArrayPool<byte>.Shared.Rent(128 * 1024)
        let mutable position: int64 = 0L
        let mutable eof: bool = false

        try
            while not eof && (position < length) do
                let remaining = min (int64 buffer.Length) (length - position)
                let rr = self.ReadAsync(buffer, 0, int remaining).Result

                if rr = 0 then
                    eof <- true
                else
                    // Run ONE fiber step, but ensure the WriteAsync completes
                    let s =
                        fib {
                            // IMPORTANT: wait for the write to finish
                            output.WriteAsync(buffer, 0, rr).GetAwaiter().GetResult()
                            position <- position + int64 rr
                            return ()
                        }

                    let cancel = Cancel()
                    Scheduler.testwait (s, cancel) |> ignore

            position
        finally
            ArrayPool<byte>.Shared.Return(buffer)


(* ------------------------------------------------------------------------ *)
let noexn =
    fun cb ->
        try
            cb ()
        with _ ->
            ()

(* ------------------------------------------------------------------------ *)


(* ------------------------------------------------------------------------ *)
let (|Match|_|) pattern input =
    let re = System.Text.RegularExpressions.Regex(pattern)
    let m = re.Match(input) in

    if m.Success then
        Some(
            re.GetGroupNames()
            |> Seq.map (fun n -> (n, m.Groups[n]))
            |> Seq.filter (fun (_, g) -> g.Success)
            |> Seq.map (fun (n, g) -> (n, g.Value))
            |> Map.ofSeq
        )
    else
        None

(* ------------------------------------------------------------------------ *)

let inline private hexVal (c: char) =
    if c >= '0' && c <= '9' then int c - int '0'
    elif c >= 'a' && c <= 'f' then 10 + (int c - int 'a')
    elif c >= 'A' && c <= 'F' then 10 + (int c - int 'A')
    else -1

/// URL-decode from ReadOnlySpan<char>.
/// - decodePlusAsSpace = true for querystring/form-data
/// - decodePlusAsSpace = false for URL path
let urlDecodeSpanFast (data: ReadOnlySpan<char>) (decodePlusAsSpace: bool) : string =
    // Fast path: if no '%' and no '+' (when relevant), return original
    let needsDecode =
        if decodePlusAsSpace then
            data.IndexOfAny('%', '+') >= 0
        else
            data.IndexOf('%') >= 0

    if not needsDecode then
        data.ToString()
    else
        // output will never exceed input length
        let outChars = ArrayPool<char>.Shared.Rent(data.Length)

        let mutable i = 0
        let mutable o = 0

        try
            while i < data.Length do
                let c = data.[i]

                if c = '%' then
                    // Parse a run of contiguous %HH%HH%HH...
                    let mutable j = i
                    let mutable byteCount = 0

                    // First pass: count how many valid %HH groups in this run
                    while j + 2 < data.Length && data.[j] = '%' do
                        let hi = hexVal data.[j + 1]
                        let lo = hexVal data.[j + 2]
                        if hi < 0 || lo < 0 then
                            // stop at first invalid triple
                            j <- data.Length // force exit
                        else
                            byteCount <- byteCount + 1
                            j <- j + 3

                    if byteCount = 0 then
                        // malformed "%", keep it as-is
                        outChars.[o] <- '%'
                        o <- o + 1
                        i <- i + 1
                    else
                        // Second pass: actually decode bytes
                        // Rent exactly byteCount bytes (no over-allocation)
                        let bytes = ArrayPool<byte>.Shared.Rent(byteCount)
                        try
                            let mutable k = 0
                            let mutable jj = i
                            let mutable allAscii = true

                            while k < byteCount do
                                // These must be valid by construction from the first pass
                                let hi = hexVal data.[jj + 1]
                                let lo = hexVal data.[jj + 2]
                                let b = byte ((hi <<< 4) ||| lo)
                                bytes.[k] <- b
                                if b >= 0x80uy then allAscii <- false
                                k <- k + 1
                                jj <- jj + 3

                            if allAscii then
                                // Very fast path: bytes -> chars directly
                                for t = 0 to byteCount - 1 do
                                    outChars.[o] <- char bytes.[t]
                                    o <- o + 1
                            else
                                // UTF-8 decode (handles multi-byte sequences)
                                let written = Encoding.UTF8.GetChars(bytes, 0, byteCount, outChars, o)
                                o <- o + written

                            // Advance input pointer past the %HH run
                            i <- i + (byteCount * 3)
                        finally
                            ArrayPool<byte>.Shared.Return(bytes)

                elif decodePlusAsSpace && c = '+' then
                    outChars.[o] <- ' '
                    o <- o + 1
                    i <- i + 1

                else
                    outChars.[o] <- c
                    o <- o + 1
                    i <- i + 1

            new string(outChars, 0, o)
        finally
            ArrayPool<char>.Shared.Return(outChars)

/// Convenience wrappers
let urlDecodePath (s: string) =
    if isNull s then null
    else urlDecodeSpanFast (s.AsSpan()) false

let urlDecodeQuery (s: string) =
    if isNull s then null
    else urlDecodeSpanFast (s.AsSpan()) true


module IO =
    let ReadAllLinesAsync (stream: StreamReader) : AsyncSeq<string> =
        asyncSeq {
            while not stream.EndOfStream do
                let! line = stream.ReadLineAsync() |> Async.AwaitTask
                yield line
        }
