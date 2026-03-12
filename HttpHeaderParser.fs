module HttpHeaderParser

open Resharp

let private isHeaderNameChar (c: char) =
    (c >= 'a' && c <= 'z') ||
    (c >= 'A' && c <= 'Z') ||
    (c >= '0' && c <= '9') ||
    c = '-'

let private isValidHeaderName (name: string) =
    if System.String.IsNullOrEmpty(name) then false
    else
        let mutable ok = true
        let mutable i = 0
        while ok && i < name.Length do
            if not (isHeaderNameChar name.[i]) then
                ok <- false
            i <- i + 1
        ok

module private Rx =
    let opts = ResharpOptions.HighThroughputDefaults

    // Matches the old shape:
    //   header-name + optional ws + ":" + anything except CR/LF
    let normalHeader =
        Regex(@"\A[a-zA-Z0-9-]+[ \t]*:[^\r\n]*\z", opts)

    // Continuation must start with SP/HTAB
    let continuationHeader =
        Regex(@"\A[ \t]+[^\r\n]*\z", opts)

let tryParseHeaderLine (line: string) : Choice<(string * string), string> option =
    if isNull line then
        None
    elif line = "" then
        None
    elif Rx.continuationHeader.IsMatch(line) then
        let v = line.Trim()
        if v = "" then None else Some(Choice2Of2 v)
    elif Rx.normalHeader.IsMatch(line) then
        let colon = line.IndexOf(':')
        if colon <= 0 then
            None
        else
            let name = line.Substring(0, colon).Trim()
            if not (isValidHeaderName name) then
                None
            else
                let value = line.Substring(colon + 1).Trim()
                Some(Choice1Of2(name, value))
    else
        None