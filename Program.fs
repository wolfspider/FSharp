(* Copyright (C) 2012--2015 Microsoft Research and INRIA *)

module HttpEntryPoint

open System
open System.IO
open System.Net
open HttpServer
open Fiber
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

    let fib = FiberBuilder()
    let inline millis n = TimeSpan.FromMilliseconds (float n)
    
    let program = fib {
      let c = fib { 
        do! Fiber.delay (millis 3000)
        return 2
      }
      let a = fib {
        do! Fiber.delay (millis 5000)
        return 3
      }
      let! d = a |> Fiber.race (c)
      let ch =
        match d with
        | Choice1Of2 t -> t
        | Choice2Of2 t -> t
      let! b = a |> Fiber.timeout (millis 8000)
      HttpLogger.HttpLogger.Info (String.Format("Fiber Results: {0} {1}",b,ch))
      return b }
    let cancel = Cancel ()
    let result = Scheduler.test(program, cancel)
    let valu = result.Value
    let rs = 
      match valu with
      | Ok fn -> fn.ToString()
      | Error exn -> exn.ToString()
 
    HttpLogger.HttpLogger.Info (String.Format("Scheduler Result: {0}", rs))
    Console.ReadLine () |> ignore
    

    let mimesmap   = try_read_mimes (Path.Combine("./htdocs", "mime.types")) in

        HttpServer.run {
            docroot    = "./htdocs"  ;
            mimesmap   = mimesmap         ;
            localaddr  = IPEndPoint(IPAddress.Loopback, 2443);
            servname   = "localhost";
        }
