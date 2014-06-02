﻿namespace Nessos.FsPickler.Tests

    open System
    open System.Diagnostics
    open System.Net
    open System.Net.Sockets
    open System.IO
    open System.Threading
    open System.Threading.Tasks

    open Nessos.FsPickler

    open NUnit.Framework

    module ServerDefaults =

        let ipAddr = "127.0.0.1"
        let port = 2323

        let defaultProtocolSerializer () = new BinaryFormatterSerializer() :> ISerializer 

    type Request = Serialize of Type * obj
    type Reply = 
        | Success of byte [] 
        | Error of exn
        | Fault of exn

    type ProtocolError(e : exn) =
        inherit System.Exception("Protocol Error.", e)

    type State = Init | Started | Stopped
        
    type SerializationServer(testedSerializer : ISerializer, ?ipAddr : string, ?port : int,
                                            ?protocolSerializer : ISerializer, ?logF : string -> unit) =

        let ipAddr = defaultArg ipAddr ServerDefaults.ipAddr |> IPAddress.Parse
        let port = defaultArg port ServerDefaults.port
        let protocolSerializer = defaultArg' protocolSerializer ServerDefaults.defaultProtocolSerializer
        let logF = defaultArg logF ignore
        let listener = new TcpListener(ipAddr, port)

        let testSerializer (Serialize (t,o)) =
            try 
                let result = Success <| Serializer.write testedSerializer o
                sprintf "Successfully serialized %O : %s" o t.Name |> logF
                result
            with e -> 
                sprintf "Failed to serialize %O : %s with error:\n %O" o t.Name e |> logF
                Error e

        let loop () =
            async {
                while true do
                    try
                        use! client = listener.AcceptTcpClientAsync()
                        let (_ : TcpClient) = client
                        use stream = client.GetStream()

                        try
                            let! (bytes : byte []) = stream.AsyncReadBytes()

                            let msg = Serializer.read protocolSerializer bytes
                            let result = testSerializer msg

                            do! stream.AsyncWriteBytes <| Serializer.write protocolSerializer result
                        with e ->
                            logF <| sprintf "Protocol error: %O" e
                            do! stream.AsyncWriteBytes <| Serializer.write protocolSerializer (Fault e)
                    with e ->
                        logF <| sprintf "Protocol error: %O" e
            }

        let cts = new CancellationTokenSource()
        let mutable state = Init

        member __.IPEndPoint = new IPEndPoint(ipAddr, port)

        member __.Start() =
            lock state (fun () ->
                match state with
                | Started -> failwith "server is already running."
                | Stopped -> failwith "server has been disposed."
                | Init ->
                    listener.Start()
                    Async.Start(loop(), cts.Token)
                    state <- Started)

        member __.Stop() =
            lock state (fun () ->
                match state with
                | Init -> failwith "server has not been started."
                | Stopped -> failwith "server has been stopped."
                | Started -> cts.Cancel() ; listener.Stop() ; state <- Stopped)

        interface IDisposable with
            member __.Dispose() = 
                lock state (fun () -> cts.Cancel() ; listener.Stop() ; state <- Stopped)


    type SerializationClient(testedSerializer : ISerializer, ?ipAddr : string, ?port : int, ?protocolSerializer : ISerializer) =
        let ipAddr = defaultArg ipAddr ServerDefaults.ipAddr
        let port = defaultArg port ServerDefaults.port
        let protocolSerializer = defaultArg' protocolSerializer ServerDefaults.defaultProtocolSerializer

        let sendSerializationRequest (msg : Request) =
            async {
                try
                    use client = new TcpClient(ipAddr, port)
                    use stream = client.GetStream()

                    let bytes = Serializer.write protocolSerializer msg
                    do! stream.AsyncWriteBytes bytes
                    let! (reply : byte []) = stream.AsyncReadBytes()

                    return Serializer.read protocolSerializer reply
                with e ->
                    return Fault e
            } |> Async.RunSynchronously


        member __.Test(x : 'T) : 'T =
            match sendSerializationRequest(Serialize(typeof<'T>, x)) with
            | Success bytes -> 
                let o = Serializer.read testedSerializer bytes : obj
                o :?> 'T
            | Error e -> raise e
            | Fault e -> raise (ProtocolError e)

        member __.EndPoint = new IPEndPoint(IPAddress.Parse ipAddr, port)
        member __.Serializer = testedSerializer

    type ServerManager(testedSerializer : ISerializer, ?port : int) =
        let port = defaultArg port ServerDefaults.port
        let proc : (Process * IDisposable) option ref = ref None 

        let isActive () = 
            match !proc with
            | Some (p,_) when not p.HasExited -> true
            | _ -> false
        
        member __.Start() =
            lock proc (fun () ->
                if isActive () then failwith "server already running"

                let thisExe = System.IO.Path.GetFullPath("FsPickler.Tests.exe")

                let psi = new ProcessStartInfo()

                psi.FileName <- thisExe
                psi.Arguments <- sprintf "\"%s\" \"%d\"" testedSerializer.Name port
                psi.WorkingDirectory <- Path.GetDirectoryName thisExe
                psi.UseShellExecute <- false
                psi.CreateNoWindow <- true
                psi.RedirectStandardOutput <- true
                psi.RedirectStandardError <- true

                let p = Process.Start psi

                let d1 = p.OutputDataReceived.Subscribe(fun (args : DataReceivedEventArgs) -> Console.WriteLine args.Data)
                let d2 = p.ErrorDataReceived.Subscribe(fun (args : DataReceivedEventArgs) -> Console.Error.WriteLine args.Data)
                let d = Disposable.combine [d1 ; d2]

                p.EnableRaisingEvents <- true
                p.BeginOutputReadLine()
                p.BeginErrorReadLine()

                proc := Some (p,d))

        member __.GetClient() =
            if isActive() then new SerializationClient(testedSerializer, port = port)
            else
                failwith "server is not running"

        member __.Stop () =
            lock proc (fun () ->
                match !proc with
                | Some (p,d) ->
                    if not p.HasExited then p.Kill()
                    d.Dispose()
                    proc := None
                | None ->
                    failwith "server is not running")