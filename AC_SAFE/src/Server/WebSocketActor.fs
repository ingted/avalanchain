namespace Avalanchain.Server
open System.Threading

module WebSocketActor = 
    open Proto
    open Proto.FSharp
    open Giraffe
    open Giraffe.Common
    open Giraffe.WebSocket

    open System
    open System.Net.WebSockets

    type WebSocketMessage = | WebSocketMessage of string
        with member __.Value = match __ with | WebSocketMessage msg -> msg
    type WebSocketDispatcher = WebSocketMessage -> Async<unit>
    type WebSocketDisposer = string -> Async<unit>
    type WebSocketMessageHandler = WebSocketMessage -> Async<unit>
    type private WebSocketConnectionMessage = 
        | Message of WebSocketReference * WebSocketMessage
        | NewConnection of WebSocketReference

    let webSocketBase (isBroadcast: bool) (route: string) (log: string -> unit) (connection: WebSocketDispatcher -> WebSocketDisposer -> WebSocketReference -> WebSocketMessageHandler) cancellationToken =
        let connectionManager = ConnectionManager()
        let spawnChild (parentCtx: Proto.IContext) (ref: WebSocketReference) =
            let name = ref.ID

            let dispatcher (msg: WebSocketMessage) = 
                if isBroadcast then async { match msg with | WebSocketMessage msg -> do! connectionManager.BroadcastTextAsync(msg, cancellationToken) }
                else async { match msg with | WebSocketMessage msg -> do! ref.SendTextAsync(msg, cancellationToken) }
            let disposer reason = async { do! ref.CloseAsync(reason, cancellationToken) }
            let handler = connection dispatcher disposer ref 

            let props = Actor.createAsync handler |> Actor.initProps
            let pid = parentCtx.SpawnNamed(props, name)
            log (sprintf "Spawned WebSocket connection: '%s' with PID: '%A' Id: '%s' Address: '%s'" name pid pid.Id pid.Address)
            pid

        let handler (ctx: IContext) (msg: WebSocketConnectionMessage) = async {
            match msg with 
            | NewConnection ref -> spawnChild ctx ref |> ignore
            | Message (ref, msg) -> 
                match ctx.Children |> Seq.tryFind (fun c -> c.Id.EndsWith ref.ID) with
                | Some pid -> pid <! msg
                | None -> log (sprintf "ERROR: WebSocket connection not found: '%s' for route '%s'" ref.ID route)
        }

        let pid = Actor.create2Async handler |> Actor.initProps |> Actor.spawnNamed ("ws_" + route)

        connectionManager.CreateSocket( (fun ref -> task { pid <! NewConnection ref } ),
                                        (fun ref msg -> task { pid <! Message(ref, WebSocketMessage msg) }),
                                        cancellationToken = cancellationToken)

    let webSocket route log connection cancellationToken = 
        webSocketBase false route log connection cancellationToken
    let webSocketBroadcast route log connection cancellationToken = 
        webSocketBase true route log connection cancellationToken


    let webSocketClient url (log: string -> unit) (connection: WebSocketDispatcher -> WebSocketDisposer -> WebSocketReference -> WebSocketMessageHandler) cancellationToken =
        let socket = new ClientWebSocket()
        let ref = WebSocketReference.FromWebSocket socket
        let messageSize = DefaultWebSocketOptions.ReceiveBufferSize
        let receive (reference: WebSocketReference) (handler: string -> Async<unit>) (cancellationToken:CancellationToken) = async {
            let buffer = Array.zeroCreate messageSize |> ArraySegment<byte>
            use memoryStream = new IO.MemoryStream()
            let mutable endOfMessage = false
            let mutable keepRunning = Unchecked.defaultof<_>
            printfn "WS Rec started:"

            while not endOfMessage do
                let! received = reference.WebSocket.ReceiveAsync(buffer, cancellationToken)
                printfn "WS Mes: %A" received
                if received.CloseStatus.HasValue then
                    do! reference.WebSocket.CloseAsync(received.CloseStatus.Value, received.CloseStatusDescription, cancellationToken)
                    keepRunning <- false
                    endOfMessage <- true
                else
                    memoryStream.Write(buffer.Array,buffer.Offset,received.Count)
                    if received.EndOfMessage then
                        match received.MessageType with
                        | WebSocketMessageType.Binary ->
                            raise (NotImplementedException())
                        | WebSocketMessageType.Close ->
                            keepRunning <- false 
                            endOfMessage <- true
                        | WebSocketMessageType.Text ->
                            let! r = 
                                memoryStream.ToArray()
                                |> System.Text.Encoding.UTF8.GetString
                                |> fun s -> s.TrimEnd(char 0)
                                |> handler 

                            keepRunning <- true
                            endOfMessage <- true
                        | _ ->
                            raise (NotImplementedException())

            return keepRunning
        }        
        
        let logger str (msg: WebSocketMessage) = 
            match msg with | WebSocketMessage msg -> printfn "%s:%s" str msg 
            msg

        async { do! socket.ConnectAsync(url, cancellationToken) } |> Async.RunSynchronously
        printfn "Socket state: %A" (socket.State)    
        let dispatcher (msg: WebSocketMessage) = 
            logger "Disp" msg |> ignore; 
            async { match msg with | WebSocketMessage msg -> do! ref.SendTextAsync(msg, cancellationToken) }
        let disposer reason = async { do! ref.CloseAsync(reason, cancellationToken) }
        let handler = logger "Rec" >> connection dispatcher disposer ref 
        let receiver msg = async {
            let mutable running = true
            while running && not cancellationToken.IsCancellationRequested do
                let! msg = receive ref (WebSocketMessage >> handler) cancellationToken
                running <- msg
        }
        
        let pid = Actor.createAsync receiver |> Actor.spawnPropsPrefix ("wsClient_" + url.ToString() + "_") // TODO: Add pid Poisoning on dispose
        pid <! "start"
        log (sprintf "Spawned WebSocket client to: '%A' with PID: '%A' Id: '%s' Address: '%s'" url pid pid.Id pid.Address)

        dispatcher, disposer


        // let parentHandler (ctx: IContext) (msg: obj) =
        //     printfn "(Parent) Message: %A" msg
        //     match msg with
        //     | :? string as message when message = "kill" ->
        //         printfn "Will kill someone"
        //         let children = ctx.Children
        //         let childToKill = children |> Seq.head
        //         "die" >! childToKill
        //     | :? Proto.Started ->
        //         [ 1 .. 3 ] |> List.iter (spawnChild ctx)
        //     | _ -> printfn "Some other message: %A" msg

        // let wsStreams = new ConcurrentDictionary<WebSocketReference, WebSocketConnection>()

        // let addSubscription = 

        // wsConnectionManager.CreateSocket(
        //                             (fun ref -> task { 
        //                                 let sink = new Subject<_>()
        //                                 let source = //connection ref sink
        //                                 source |> Observable.subscribe (fun msg -> ref.SendTextAsync(msg, cancellationToken))
        //                                 () 
        //                             }),
        //                             (fun ref msg -> ref.SendTextAsync("Hi " + msg, cancellationToken)),
        //                             cancellationToken = cancellationToken)

    // type WebSocketConnection = {
    //     Ref: WebSocketReference
    //     Sink: string -> Task<unit>
    //     Source: Subject<string>
    // }

// let webSocket (wsConnectionManager: ConnectionManager) (connection: WebSocketReference -> IObservable<string> -> IObservable<string>) cancellationToken =
//     let wsStreams = new ConcurrentDictionary<WebSocketReference, WebSocketConnection>()

//     wsConnectionManager.CreateSocket(
//                                 (fun ref -> 
//                                             task { 
//                                                 let sink = new Subject<_>()
//                                                 let source = connection sink
//                                                 source |> Observable.subscribe (fun msg -> ref.SendTextAsync(msg, cancellationToken).Wait())
//                                                 () 
//                                             }),
//                                 (fun ref msg -> ref.SendTextAsync("Hi " + msg, cancellationToken)),
//                                 cancellationToken = cancellationToken)

                