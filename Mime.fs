module Mime

open System
open System.IO
open System.Text
open FSharp.Control

type mime = string

type MimeMap() =
    let mutable mimes: Map<string, string> = Map.empty

    static member CanonizeExt(ext: string) =
        let ext = ext.ToLowerInvariant()
        if ext.StartsWith(".") then ext else "." + ext

    member _.Bind(ext: string)(mime: mime) =
        let ext = MimeMap.CanonizeExt(ext)

        if ext = "." then
            raise (ArgumentException("cannot bind empty extension"))

        mimes <- Map.add ext mime mimes

    member _.Lookup(ext: string) =
        mimes.TryFind(MimeMap.CanonizeExt ext)

let inline sliceByRange (source: ReadOnlySpan<char>) (r: Range) =
    let s = r.Start.GetOffset(source.Length)
    let e = r.End.GetOffset(source.Length)
    source.Slice(s, e - s)

let tryParseMimeLine (line: string) : (string * string list) option =
    if isNull line then None
    else
        let mutable span = line.AsSpan()

        // strip comment
        let hash = span.IndexOf('#')
        if hash >= 0 then
            span <- span.Slice(0, hash)

        span <- span.Trim()

        if span.IsEmpty then None
        else
            let seps = " \t".AsSpan()

            // Use MemoryExtensions to avoid extension resolution issues in F#
            let mutable it = MemoryExtensions.SplitAny(span, seps)

            if not (it.MoveNext()) then None
            else
                let ctypeSpan = sliceByRange span it.Current
                let exts = ResizeArray<string>()

                while it.MoveNext() do
                    let extSpan = sliceByRange span it.Current
                    if not extSpan.IsEmpty then
                        exts.Add(extSpan.ToString())

                Some(ctypeSpan.ToString(), List.ofSeq exts)

let of_stream (stream: Stream) =
    use reader = new StreamReader(stream, Encoding.ASCII)
    let mime = MimeMap()

    Utils.IO.ReadAllLinesAsync reader
    |> AsyncSeq.iter (fun line ->
        match tryParseMimeLine line with
        | Some(ctype, exts) ->
            exts |> List.iter (fun ext -> mime.Bind ext ctype)
        | None -> ()
    )
    |> Async.RunSynchronously

    mime

let of_file (filename: string) =
    use stream = File.Open(filename, FileMode.Open, FileAccess.Read)
    of_stream stream
