﻿(* Copyright (C) 2012--2015 Microsoft Research and INRIA *)

module HttpEntryPoint

open System
open System.IO
open System.Net
open HttpServer
open Microsoft.FSharp.Text

let try_read_mimes path =
    try
        Mime.of_file path
    with :? IOException as e ->
        Console.WriteLine("cannot read mime-types: " + e.Message)
        Mime.MimeMap ()


type options = {
    rootdir   : string;
    certdir   : string;
    dhdir     : string;
    localaddr : IPEndPoint;
    localname : string;
    remotename: string option;
}

let _ =
    HttpLogger.HttpLogger.Level <- HttpLogger.DEBUG;

    let mimesmap   = try_read_mimes (Path.Combine("./htdocs", "mime.types")) in

        HttpServer.run {
            docroot    = "./htdocs"  ;
            mimesmap   = mimesmap         ;
            localaddr  = IPEndPoint(IPAddress.Loopback, 2443);
            servname   = "localhost";
        }
