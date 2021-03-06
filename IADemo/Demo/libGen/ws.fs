namespace Avalanchain

module WebSockets =

    open System.Collections.Concurrent

    open Suave
    open Suave.Http
    open Suave.Operators
    open Suave.Filters
    open Suave.Successful
    open Suave.Files
    open Suave.RequestErrors
    open Suave.Logging
    open Suave.Utils

    open System
    open System.Net
    open System.Text

    open Suave.Sockets
    open Suave.Sockets.Control
    open Suave.WebSocket

    open Akka.IO
    open Akka.Streams
    open Akkling
    open Akkling.Streams
    open Akkling.Persistence

    type IncomingConnection = Akka.Streams.Dsl.Tcp.IncomingConnection


    let internal webSocketSink (webSocket : WebSocket) = //(errorQueue: ISourceQueue<Error>) = 
        Flow.id
        // |> Flow.iter(fun msg -> printfn "Sending %A" msg)
        |> Flow.asyncMap 1 (fun (m: ByteString) -> webSocket.send Text (m.ToArray() |> ByteSegment) true)
        // |> Flow.map(fun r -> match r with    
        //                         | Choice1Of2 () -> None
        //                         | Choice2Of2 (error: Error) -> Some error)
        // |> Flow.iter(printfn "%A")
        // |> Flow.toMat (Sink.forEach (printfn "Error enqueue result: %A")) Keep.left
        |> Flow.toMat (Sink.ignore) Keep.left
        // |> Flow.choose id
        // |> Flow.asyncMap 1 (errorQueue.AsyncOffer)
        // |> Flow.toMat (Sink.forEach (printfn "Error enqueue result: %A")) Keep.left

    let internal webSocketSource (webSocket : WebSocket) =
        let emptyResponse = [||] |> ByteSegment
        let emptyAsync = async { return Choice1Of2 () }
        ()
        |> Source.asyncUnfold (
            fun s -> async { 
                let! msg = webSocket.read()
                // printfn "Received msg: %A" msg 
                let ret, (sendAsync: Async<Choice<unit, Error>>) =
                    match msg with
                    | Choice1Of2(validMsg) -> 
                        match validMsg with
                        | (Text, data, true) ->
                            // printfn "Received: %A" data 
                            data |> ByteString.FromBytes, emptyAsync
                        | (Ping, _, _) -> 
                            printfn "Received PING" 
                            ByteString.FromBytes [], (webSocket.send Pong emptyResponse true)
                        | (Close, _, _) -> 
                            printfn "Received CLOSE"
                            null, (webSocket.send Close emptyResponse true) // after sending a Close message, break unfold
                        | _ -> ByteString.FromBytes [], emptyAsync
                    | Choice2Of2(error) -> 
                        // Example error handling logic here
                        printfn "Error: [%A]" error
                        ByteString.FromBytes [], emptyAsync
                let! _ = sendAsync // Send pending system messages (PONG or CLOSE) to the websocket
                return s, ret
            })
        |> Source.filter(fun b -> not b.IsEmpty)


    let webSocketServer config (wsPaths: Map<string, IncomingConnection -> unit>) mat =
        Source.queue OverflowStrategy.DropNew 1000 
        |> Source.mapMaterializedValue(
            fun connQueue ->
                let wsPaths = new ConcurrentDictionary<string, IncomingConnection -> unit>(wsPaths)
                let app : WebPart = 
                    [   wsPaths 
                        |> Seq.map(fun wsPath -> path wsPath.Key >=> handShake (fun webSocket (context: HttpContext) -> 
                                                                                    async { let! _ = connQueue.AsyncOffer(wsPath.Value, webSocket, context)
                                                                                            while true do
                                                                                                do! Async.Sleep 1000
                                                                                            return Choice1Of2 () }
                                                                                ))
                        |> Seq.toList
                        [
                            GET >=> choose [ path "/" >=> OK (String.Join(", ", wsPaths |> Seq.map(fun kv -> kv.Key))) ]
                            NOT_FOUND "Found no handlers." 
                        ]
                    ]
                    |> List.collect id
                    |> choose 
                let server = async { startWebServer { config with logger = Targets.create Verbose [||] } app } |> Async.Start 
                connQueue, server
            )
        |> Source.runForEach mat (
            fun (handler, webSocket, context) ->
                let localEP = IPEndPoint(context.connection.ipAddr, context.connection.port |> int)
                let remoteEP = IPEndPoint(context.clientIpTrustProxy, context.clientPortTrustProxy |> int)
                let ic = IncomingConnection(localEP, remoteEP, 
                            Flow.ofSinkAndSourceMat (webSocketSink webSocket) (fun _ _ -> Akka.NotUsed.Instance) (webSocketSource webSocket))
                handler ic
                ()
        )


   