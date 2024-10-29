module HttpHeaders

open System
open System.Globalization


let CONTENT_LENGTH = "Content-Length"
let CONTENT_TYPE = "Content-Type"

let normkey = fun (key: string) -> key.Trim().ToLowerInvariant()

type HttpHeaders() =
    let mutable headers: (string * string) list = []

    static member OfList(headers: (string * string) list) =
        let aout = HttpHeaders() in

        headers
        |> List.map (fun (h, v) -> (h.Trim(), v))
        |> List.iter (fun (h, v) -> aout.Add h v)

        aout

    member _.ToSeq() = headers |> List.rev |> Seq.ofList

    member _.Exists(key: string) =
        let key = normkey key in headers |> List.exists (fun (k, _) -> normkey k = key)

    member _.Get(key: string) =
        let key = normkey key in

        match headers |> List.tryFind (fun (k, _) -> normkey k = key) with
        | Some(_, value) -> Some value
        | None -> None

    member self.GetDfl(key: string, dfl: string) =
        match self.Get(key) with
        | None -> dfl
        | Some x -> x

    member _.GetAll(key: string) =
        let key = normkey key in headers |> List.filter (fun (k, _) -> normkey k = key)

    member _.Set (key: string) (value: string) =
        let normed = normkey key in
        headers <- headers |> List.filter (fun (k, _) -> normkey k <> normed)
        headers <- (key, value) :: headers

    member _.Add (key: string) (value: string) = headers <- (key, value) :: headers

    member _.Del(key: string) =
        let key = normkey key in headers <- headers |> List.filter (fun (k, _) -> normkey k = key)

    member self.ContentLength() =
        match self.Get CONTENT_LENGTH with
        | None -> None
        | Some clen ->
            try
                Some(Int64.Parse(clen, NumberStyles.None))
            with
            | :? FormatException
            | :? OverflowException -> raise (FormatException())


exception InvalidHttpHeaderContinuation

type HttpHeadersBuilder() =
    let mutable lastseen = None
    let mutable headers = HttpHeaders()

    member private _.MaybePop() =
        match lastseen with
        | Some(h, v) ->
            headers.Add h v
            lastseen <- None
        | _ -> ()

    member self.Push (key: string) (value: string) =
        self.MaybePop()
        lastseen <- Some(key, value.Trim())

    member _.PushContinuation(value: string) =
        match lastseen with
        | Some(h, v) -> lastseen <- Some(h, String.Format("{0} {1}", v, value.Trim()))
        | _ -> raise InvalidHttpHeaderContinuation

    member self.Headers =
        self.MaybePop()
        headers
