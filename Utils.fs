module Utils

open System
open System.IO
open System.Web
open FSharp.Control
open Fiber

(* ------------------------------------------------------------------------ *)
type String with
    member self.UrlDecode() = HttpUtility.UrlDecode(self)

(* ------------------------------------------------------------------------ *)
type Stream with
    member self.CopyTo(output: Stream, length: int64) : int64 =
        let (*---*) buffer: byte[] = Array.zeroCreate (128 * 1024) in
        let mutable position: int64 = (int64 0) in
        let mutable eof: bool = false in

        while not eof && (position < length) do
            let remaining = min (int64 buffer.Length) (length - position) in
            let rr = self.ReadAsync(buffer, 0, int remaining).Result in

            if rr = 0 then
                eof <- true
            else
                begin
                    let s =
                        fib {
                            output.WriteAsync(buffer, 0, rr) |> ignore
                            position <- position + (int64 rr)
                            return ()
                        }

                    let cancel = Cancel()

                    Scheduler.testasync (s, cancel) |> ignore
                end

        position

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

module IO =
    let ReadAllLinesAsync (stream: StreamReader) : AsyncSeq<string> =
        asyncSeq {
            while not stream.EndOfStream do
                let! line = stream.ReadLineAsync() |> Async.AwaitTask
                yield line
        }
