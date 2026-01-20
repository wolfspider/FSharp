module HttpHeaders

open System
open System.Globalization
open System.Collections.Generic

let CONTENT_LENGTH = "Content-Length"
let CONTENT_TYPE = "Content-Type"

type HttpHeaders() =
    let mutable headers: (string * string) list = []
    let index = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    
    static member OfList(headers: (string * string) list) =
        let aout = HttpHeaders() in

        headers
        |> List.map (fun (h, v) -> (h.Trim(), v))
        |> List.iter (fun (h, v) -> aout.Add h v)

        aout

    member _.ToSeq() = headers |> List.rev |> Seq.ofList

    member _.Exists(key: string) =
        index.ContainsKey(key)

    member _.Get(key: string) =
        match index.TryGetValue(key) with
        | true, value -> Some value
        | _ -> None

    member self.GetDfl(key: string, dfl: string) =
        match self.Get(key) with
        | None -> dfl
        | Some x -> x


    member _.GetAll(key: string) =
        headers |> List.filter (fun (k, _) -> String.Equals(k, key, StringComparison.OrdinalIgnoreCase))

    member _.Set(key: string)(value: string) =
        headers <- headers |> List.filter (fun (k, _) -> not (String.Equals(k, key, StringComparison.OrdinalIgnoreCase)))
        headers <- (key, value) :: headers
        index[key] <- value

    member _.Add(key: string)(value: string) =
        headers <- (key, value) :: headers
        index[key] <- value

    member _.Del(key: string) =
        headers <- headers |> List.filter (fun (k, _) -> not (String.Equals(k, key, StringComparison.OrdinalIgnoreCase)))
        index.Remove(key) |> ignore

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
    let mutable lastseen : (string * string) option = None
    let headers = HttpHeaders()

    member private _.MaybePop() =
        match lastseen with
        | Some(h, v) ->
            headers.Add h v
            lastseen <- None
        | None -> ()

    member self.Push(key: string)(value: string) =
        self.MaybePop()
        lastseen <- Some(key, value.Trim())

    member _.PushContinuation(value: string) =
        match lastseen with
        | Some(h, v) ->
            lastseen <- Some(h, String.Format("{0} {1}", v, value.Trim()))
        | None ->
            raise InvalidHttpHeaderContinuation

    member self.Headers =
        self.MaybePop()
        headers
