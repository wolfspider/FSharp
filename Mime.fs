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

module private MimeRegex =
    // Compile once, reuse.
    // This parser usually runs at startup, so the default options are enough.
    let commentSuffix = Resharp.Regex(@"#.*\z")
    let token = Resharp.Regex(@"[^\s#]+")

let tryParseMimeLine (line: string) : (string * string list) option =
    if isNull line then
        None
    else
        // Remove inline comment, then trim.
        let cleaned = MimeRegex.commentSuffix.Replace(line, "").Trim()

        if cleaned = "" then
            None
        else
            let matches = MimeRegex.token.Matches(cleaned)

            if matches.Length = 0 then
                None
            else
                let ctype = matches[0].Value

                let exts =
                    [ for i in 1 .. matches.Length - 1 do
                          yield matches[i].Value ]

                Some(ctype, exts)

let of_stream (stream: Stream) =
    use reader = new StreamReader(stream, Encoding.ASCII)
    let mime = MimeMap()

    Utils.IO.ReadAllLinesAsync reader
    |> AsyncSeq.iter (fun line ->
        match tryParseMimeLine line with
        | Some(ctype, exts) ->
            exts |> List.iter (fun ext -> mime.Bind ext ctype)
        | None -> ())
    |> Async.RunSynchronously

    mime

let of_file (filename: string) =
    use stream = File.Open(filename, FileMode.Open, FileAccess.Read)
    of_stream stream