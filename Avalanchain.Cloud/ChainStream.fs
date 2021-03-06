﻿namespace Avalanchain.Cloud

open System
open System.Collections
open System.Linq
open FSharp.Control
open FSharp.Collections
open FSharpx.Collections
open MBrace.Core
open MBrace.Flow
open MBrace.Library
open Avalanchain
open Avalanchain.SecKeys
open Avalanchain.Utils
open Avalanchain.StreamEvent



type StreamFrameData<'T> = {
    Nonce: Nonce
    Value: 'T
}
and HashedFrameData<'T> = Hashed<StreamFrameData<'T>>


type IStreamFrame<'T> = 
    abstract member Nonce: Nonce
    abstract member Value: 'T
    abstract member Hash: Hash
    abstract member MerkledHash: Hash


type MerkledHash = {
    MH: Hash  // Merkle Hash = Hash(PMH, OH)
    OH: Hash // Own Hash
    PMH: Hash // Parent Merkle Hash
}

type StreamFrame<'T> = {
    Frame: HashedFrameData<'T>
    Hash: Hash
    Merkled: MerkledHash
    Timestamp: DateTimeOffset
    SubmitterKey: SigningPublicKey
    SubmitterSignature: SubmitterSignature
}
with 
    interface IStreamFrame<'T> with
        member this.Hash = this.Frame.Hash
        member this.Nonce = this.Frame.Value.Nonce
        member this.Value = this.Frame.Value.Value
        member this.MerkledHash = this.Merkled.MH

type CloudStream = {
    Id: string
    Position: unit -> Cloud<int64>
    CurrentJson: unit -> Cloud<string>
    ItemJson: uint64 -> Cloud<string>
    GetPageJson: uint64 -> uint32 -> Cloud<string[]>
    GetFramesPageJson: uint64 -> uint32 -> Cloud<string[]>
    GetCurrentPageJson: uint32 -> Cloud<string[]>
    GetCurrentFramesPageJson: uint32 -> Cloud<string[]>
}

type NodeContext = {
    WorkerId: string
    Streams: CloudDictionary<CloudStream>
}

type ClusterContext(nodeContextDict: CloudDictionary<NodeContext>) = 
    //let nodeContextDict = local { return! CloudDictionary.New<NodeContext>("nodes") }
    member this.AddStream (stream: CloudStream) = local {
        let! cw = Cloud.CurrentWorker 
        let! streamsOpt = nodeContextDict.TryFindAsync (cw.Id) |> Cloud.OfAsync
        match streamsOpt with 
        | Some nodeContext -> 
            do! nodeContext.Streams.ForceAddAsync(stream.Id + "##" + Guid.NewGuid().ToString(), stream) |> Cloud.OfAsync
        | None -> 
            let! streams = CloudDictionary.New<CloudStream>("streams")
            do! streams.ForceAddAsync(stream.Id + "##" + Guid.NewGuid().ToString(), stream) |> Cloud.OfAsync
            do! nodeContextDict.ForceAddAsync(cw.Id, { WorkerId = cw.Id; Streams = streams }) |> Cloud.OfAsync
    }
    member this.Streams = local {
        let! cw = Cloud.CurrentWorker 
        let! streamsOpt = nodeContextDict.TryFindAsync (cw.Id) |> Cloud.OfAsync
        match streamsOpt with 
        | Some nodeContext -> 
            let! ret = nodeContext.Streams.GetEnumerableAsync() |> Cloud.OfAsync
            return ret.ToArray()
        | None -> 
            return [||]
    }

    member this.AllStreams() = local { 
        let! nodesKV = nodeContextDict.GetEnumerableAsync() |> Cloud.OfAsync
        let streamDicts = nodesKV |> Seq.map(fun kv -> kv.Value.Streams)
        let streamKVs = 
            nodesKV 
            |> Seq.map(fun kv -> 
                        let streams = 
                            kv.Value.Streams.GetEnumerableAsync() 
                            |> Async.RunSynchronously
                            |> Seq.map(fun kv -> kv.Key, kv.Value)
                            |> Seq.toArray

                        kv.Key, (streams))
            |> Seq.toArray

        return streamKVs 
    }

    member this.AllStreams2() = local { 
        let! nodesKV = nodeContextDict.GetEnumerableAsync() |> Cloud.OfAsync
        let streamDicts = nodesKV |> Seq.map(fun kv -> kv.Value.Streams)
        let! streamKVs = 
            streamDicts 
                |> Cloud.Sequential.collect (fun sd -> 
                                                let streams = sd.GetEnumerableAsync() |> Cloud.OfAsync
                                                streams :> Cloud<Generic.KeyValuePair<string, CloudStream> seq>) |> Cloud.AsLocal

        return streamKVs |> Array.map(fun kv -> kv.Value)
    }


type ChainNode private() =
    member this.CryptoContext = Utils.cryptoContext
    member this.Serializer<'T> (data: 'T) = picklerSerializer data
    member this.Derializer<'T> data = picklerDeserializer data
    member this.JsonSerializer<'T> (data: 'T) = picklerJsonSerializer data
    member this.JsonDerializer<'T> data = picklerJsonDeserializer data
    member this.DataHasher<'T> (data: 'T) = data |> dataHasher (this.Serializer) this.CryptoContext

    static member Instance = new ChainNode()
//    static member AllStreams = cloud {
//        return! local { let! workerRef = Cloud.CurrentWorker
//                        return workerRef, ChainNode.Instance.Streams } |> Cloud.ParallelEverywhere
//    }


type ChainStream<'T> = {
    Id: string
    Position: unit -> Cloud<int64>
    Item: uint64 -> Cloud<'T option>
    Current: unit -> Cloud<'T option>
    GetPage: uint64 -> uint32 -> Cloud<'T[]>
    GetFramesPage: uint64 -> uint32 -> Cloud<IStreamFrame<'T>[]>
    GetCurrentPage: uint32 -> Cloud<'T[]>
    GetCurrentFramesPage: uint32 -> Cloud<IStreamFrame<'T>[]>
    FlowProcess: ICloudProcess<unit>
    ClusterContext: ClusterContext
}
           

type StreamSink<'T> = {
    Push: 'T -> Cloud<unit> // TODO: replace with enqueueing result
    PushBatch: 'T[] -> Cloud<unit>
    CurrentState: unit -> Cloud<int64 * ('T * int64)[]>
}

module ChunkedChainStream =
    let toStream genericStream = {
        Id = genericStream.Id
        Position = genericStream.Position
        CurrentJson = fun nonce -> cloud {
            let! item = genericStream.Current()
            return item |> ChainNode.Instance.JsonSerializer
        }
        ItemJson = fun nonce -> cloud {
            let! item = genericStream.Item nonce
            return item |> ChainNode.Instance.JsonSerializer
        }
        GetPageJson = fun nonce pageSize -> cloud {
            let! item = genericStream.GetPage nonce pageSize
            return item |> Array.map (ChainNode.Instance.JsonSerializer)
        }
        GetFramesPageJson = fun nonce pageSize -> cloud {
            let! item = genericStream.GetFramesPage nonce pageSize
            return item |> Array.map (ChainNode.Instance.JsonSerializer)
        }
        GetCurrentPageJson = fun pageSize -> cloud {
            let! item = genericStream.GetCurrentPage pageSize
            return item |> Array.map (ChainNode.Instance.JsonSerializer)
        }
        GetCurrentFramesPageJson = fun pageSize -> cloud {
            let! item = genericStream.GetCurrentFramesPage pageSize
            return item |> Array.map (ChainNode.Instance.JsonSerializer)
        }
    }

    let buildFrame<'T> parentMH nonce value = 
        let frame = {
            Nonce = nonce
            Value = value
        }
        let hashedFrame = (frame |> ChainNode.Instance.DataHasher)
        let ownHash = hashedFrame.Hash
        let mh = ((parentMH, ownHash) |> ChainNode.Instance.DataHasher).Hash
        {
            Frame = hashedFrame
            Hash = hashedFrame.Hash
            Merkled = { PMH = parentMH; OH = ownHash; MH = mh }
            Timestamp = DateTimeOffset.UtcNow
            SubmitterKey = ChainNode.Instance.CryptoContext.SigningPublicKey
            SubmitterSignature = ChainNode.Instance.CryptoContext.HashSigner hashedFrame.Hash
        } :> IStreamFrame<'T>

    type State<'T> = {
//        States: CloudValue<State<'T>>[]
//        StateSize: uint64
        ChunkSize: uint64
        Chunks: CloudValue<'T[]>[]
        Tail: 'T[]
        Last: 'T option
        LastSinkNonce: int64
    }
    with 
        member inline private this.ChunkedSize = this.ChunkSize * uint64(this.Chunks.LongLength)
        member inline this.Size = this.ChunkedSize + uint64(this.Tail.LongLength)

        member this.GetPage nonce pageSize : Cloud<'T[]> = cloud {
            return! local {
                return
                    if nonce >= this.Size then [||]
                    else 
                        let size = Math.Min(pageSize, (this.Size - nonce))
                        let fromTailStart = Math.Max(0L, (int64(nonce) - int64(this.ChunkedSize))) |> uint64
                        let fromTailEnd = Math.Max(0L, (int64(nonce + size) - int64(this.ChunkedSize))) |> uint64
                        let startChunk = nonce / this.ChunkSize |> int32
                        let endChunk = Math.Min((nonce + size) / this.ChunkSize, uint64(this.Chunks.LongLength) - 1UL) |> int32

                        let chunks = [|for i in startChunk .. 1 .. endChunk -> this.Chunks.[i |> int].GetValueAsync()|] 
                                        |> Async.Parallel 
                                        |> Async.RunSynchronously

                        [| 
                            for i in 0 .. 1 .. (endChunk - startChunk) do 
                                let chunkStart = (if nonce > (uint64(i + startChunk) * this.ChunkSize) then nonce - (uint64(i + startChunk) * this.ChunkSize) else 0UL) |> int32
                                let chunkEnd = if (nonce + size) < (uint64(i + startChunk + 1) * this.ChunkSize) 
                                                then (nonce + size - (uint64(i + startChunk) * this.ChunkSize) |> int32)
                                                else (this.ChunkSize |> int32)
                                for j in chunkStart .. 1 .. chunkEnd - 1 do yield (chunks.[i].[j |> int])
                        
                            if fromTailEnd > fromTailStart then
                                for i in fromTailStart .. fromTailEnd - 1UL do 
                                    yield this.Tail.[i |> int]
                        |]
            }
        }
        member inline this.Item with get(i: uint64) : Cloud<'T option> = 
                                        cloud { 
                                            let! arr = this.GetPage i 1UL
                                            return if arr |> Array.isEmpty then None else Some arr.[0]
                                        }

        static member Create (chunkSize: uint32) (data: 'T[]) = cloud {
            return! local {
                let cs = int(chunkSize)
                let chunkCount = (data.Length / cs)
                let chunkedSize = chunkCount * cs
                let chunks = Array.zeroCreate chunkCount
                for i in 0 .. 1 .. chunkCount - 1 do
                    let! chunk = CloudValue.New([| for j in 0 .. 1 .. cs - 1 do yield data.[i * cs + j] |]) 
                    chunks.[i] <- chunk
                
                return {
                    ChunkSize = chunkSize |> uint64
                    Chunks = chunks
                    Tail = [| for i in chunkCount * cs .. 1 .. data.Length - 1 do yield data.[i] |]
                    Last = if data |> Array.isEmpty then None else (data |> Array.last |> Some) 
                    LastSinkNonce = if data |> Array.isEmpty then -1L else (data.LongLength - 1L)
                }
            }
        }

        member this.Add (data: 'T) : Cloud<State<'T>> = cloud {
            return! local {
                if (uint64(this.Tail.Length) >= this.ChunkSize) then failwith (sprintf "Corrupted state! Tail length (%d) is greater than chunk size (%d)." this.Tail.Length this.ChunkSize)
                    

                let newTail = (Array.append this.Tail [| data |])
                
                if uint64(newTail.Length) = this.ChunkSize then
                    let! chunk = CloudValue.New(newTail, StorageLevel.MemoryAndDisk)
                    return {this with
                                Chunks = (Array.append this.Chunks [| chunk |])
                                Tail = [||]
                                Last = Some data
                                LastSinkNonce = -1L
                            }
                else
                    return {this with
                                Tail = newTail
                                Last = Some data
                                LastSinkNonce = this.LastSinkNonce + 1L
                            }
            }
        }

        member this.AddRange (data: 'T[]) : Cloud<State<'T>> = cloud {
            let rec adder (state: State<'T>) remaining = local {
                match remaining with 
                | [||] -> 
                    return state 
                | x -> 
                    let (h, t) = x |> Array.splitAt 1 
                    let! st = (state.Add h.[0]) |> Cloud.AsLocal
                    return! adder st t
            }
            return! adder this data

        }

        member this.NewSinkNonce (nonce: int64) : State<'T> = {this with LastSinkNonce = nonce}


    let enqueueStream<'T> streamPath clusterContext (getter: (unit -> LocalCloud<IStreamFrame<'T> option * int64>) -> LocalCloud<IStreamFrame<'T>[] * (int64 option)>) maxBatchSize = 
        cloud { 
            let streamId = streamPath //CloudAtom.CreateRandomContainerName() // TODO: Replace with node/stream pubkey
            let! initialState = State.Create maxBatchSize ([||])
            let! stateAtom = CloudAtom.New<State<IStreamFrame<'T>>>(initialState, "state", streamId)
            let positionGetter () = cloud { let! state = stateAtom.GetValueAsync() |> Cloud.OfAsync
                                            return (state.Size |> int64) - 1L}
            let lastGetter () = local { let! state = stateAtom.GetValueAsync() |> Cloud.OfAsync
                                        return state.Last, state.LastSinkNonce }
            let! flowProcess = 
                let rec loop () = local { 
                    try 
                        let! (msgs, sinkPos) = getter lastGetter 
                        if msgs.Length > 0 || sinkPos.IsSome then
                            let! currentState = stateAtom.GetValueAsync() |> Cloud.OfAsync // TODO: Rethink possible race
                            let! newState = 
                                if msgs.Length > 0 then currentState.AddRange msgs |> Cloud.AsLocal
                                else local { return currentState }
                            let newStateWithSinkNonce = 
                                match sinkPos with 
                                | Some nonce -> newState.NewSinkNonce nonce
                                | None -> newState
                            do! stateAtom.ForceAsync(newStateWithSinkNonce) |> Cloud.OfAsync  // The order of State and Position updates is important!

                            do! Cloud.Sleep 1 // Required in order not to block downstreams
                            return! loop ()
                        else 
                            do! Cloud.Sleep 100
                            return! loop ()
                    with 
                        | e -> printfn "Exception!!! %A" e 
                } 
                loop () |> Cloud.CreateProcess

            let getFramesPage = (fun nonce page -> cloud { 
                                        let! v = stateAtom.GetValueAsync() |> Cloud.OfAsync
                                        return! v.GetPage nonce (uint64(page)) })
            let getPage = (fun nonce page -> cloud { 
                                        return! local {
                                            let! v = getFramesPage nonce page |> Cloud.AsLocal
                                            return v |> Array.map (fun kv -> kv.Value) }})
            let (genericStream: ChainStream<'T>) = {
                Id = streamId
                Position = positionGetter
                Item = (fun nonce -> cloud { 
                                        let! pos = positionGetter()
                                        if int64(nonce) <= pos then 
                                            let! v = stateAtom.GetValueAsync() |> Cloud.OfAsync
                                            let! vv = (v.[nonce])
                                            return vv |> Option.bind (fun vvv -> Some vvv.Value) 
                                        else return None })
                Current = (fun unit -> cloud { 
                                        let! v = stateAtom.GetValueAsync() |> Cloud.OfAsync
                                        return v.Last |> Option.bind (fun vvv -> Some vvv.Value) })
                GetPage = getPage
                GetCurrentPage = (fun pageSize -> cloud { 
                                    let! pos = positionGetter()
                                    if pos >= 0L then 
                                        return! getPage ((Math.Max(pos - int64(pageSize), 0L)) |> uint64) pageSize
                                    else return [||] })
                GetCurrentFramesPage = (fun pageSize -> cloud { 
                                    let! pos = positionGetter()
                                    if pos >= 0L then 
                                        return! getFramesPage ((Math.Max(pos - int64(pageSize), 0L)) |> uint64) pageSize
                                    else return [||] })
                GetFramesPage = getFramesPage
                FlowProcess = flowProcess
                ClusterContext = clusterContext
            }

            do! genericStream |> toStream |> clusterContext.AddStream 

            return genericStream
        }

    let streamOfQueue<'T> clusterContext (queue: CloudQueue<'T>) maxBatchSize = cloud {
            let getter (getLast: unit -> LocalCloud<IStreamFrame<'T> option * int64>) = 
                local { let! last = getLast()
                        let! newValue = queue.DequeueBatchAsync(int(maxBatchSize)) |> Cloud.OfAsync
                        let lastNonce = snd last
                        let parentMH = match fst last with 
                                                | None -> Hash.Zero
                                                | Some p -> p.MerkledHash
                        return 
                            (newValue |> Array.mapi (fun i v -> buildFrame parentMH (lastNonce + int64(i) |> uint64) v), Some(lastNonce + newValue.LongLength)) }
            return! enqueueStream "ofQueue" clusterContext getter maxBatchSize
        }

    let streamOfStreamFold<'TS, 'TD> streamPath (stream: ChainStream<'TD>) initialValue maxBatchSize (preFilter: IStreamFrame<'TD> -> bool) (foldF: 'TS option -> IStreamFrame<'TD> -> 'TS) = // lastState -> data -> newState
        cloud { 
            let getter (getLast: unit -> LocalCloud<IStreamFrame<'TS> option * int64>) = local {
                let! last = getLast()
                let lastNonce = snd last 
                let! msgs = stream.GetFramesPage (lastNonce + 1L |> uint64) maxBatchSize |> Cloud.AsLocal
                if msgs |> Array.isEmpty then 
                    return [||], None
                else
                    let batch = msgs.Take(maxBatchSize |> int).ToArray() // Should be done with Linq as Array.take fails on too short arrays
                    return [| 
                                let filtered = batch |> Array.filter preFilter
                                let mutable (state: 'TS option) = 
                                    match fst last, initialValue with 
                                    | Some s, _ -> Some s.Value
                                    | None, None -> None
                                    | None, Some iv -> Some iv
                                let mutable i = lastNonce
                                let mutable parentMH = match fst last with 
                                                        | None -> Hash.Zero
                                                        | Some p -> p.MerkledHash
                                for m in filtered do
                                    state <- foldF state m |> Some
                                    i <- i + 1L
                                    let frame = buildFrame parentMH (uint64(i)) (state.Value)
                                    parentMH <- frame.MerkledHash
                                    yield frame
                            |], Some(lastNonce + batch.LongLength)
            }
            return! enqueueStream (stream.Id + "/" + streamPath) stream.ClusterContext getter maxBatchSize
        } 

    let streamOfStreamGroup<'TS, 'TD> streamPath (stream: ChainStream<'TD>) initialValue maxBatchSize (groupKeySelector: 'TD -> string) = 
        cloud { 
            let! dict = CloudDictionary.New<ChainStream<'TD>> ()



            return dict
        } 


    let streamOfStream<'TS, 'TD> streamPath (stream: ChainStream<'TD>) initialValue maxBatchSize (f: 'TS option -> IStreamFrame<'TD> -> 'TS) = 
        streamOfStreamFold<'TS, 'TD> streamPath stream initialValue maxBatchSize (fun _ -> true) f


    let streamOfSink<'T> clusterContext maxBatchSize = cloud {
            let streamId = "ofSink" //CloudAtom.CreateRandomContainerName()
            let! sinkAtom = CloudAtom.New<int64 * ('T * int64)[]>((-1L, [||]), "sink", streamId)
            let getter (getLast: unit -> LocalCloud<IStreamFrame<'T> option * int64>) : LocalCloud<IStreamFrame<'T>[] * int64 option> = 
                local { 
                        return! local {
                            let! last = getLast()
                            let! (pos, msgs) = sinkAtom.GetValueAsync() |> Cloud.OfAsync
                            let batch = msgs.Take(maxBatchSize |> int).ToArray()
                            if (Array.isEmpty batch) then 
                                return [||], None
                            else
                                let newPos = batch |> Array.last |> snd
                                let lastNonce = snd last 
                                do! sinkAtom.UpdateAsync(fun (p, t) -> 
                                                            let newT = t |> Array.skipWhile (fun x -> snd x <= newPos) 
                                                            (newPos, newT)
                                                        ) |> Cloud.OfAsync
                                let parentMH = match fst last with 
                                                | None -> Hash.Zero
                                                | Some p -> p.MerkledHash
                                return (batch |> Array.mapi (fun i m -> buildFrame parentMH (lastNonce + int64(i) |> uint64) (fst m)), Some(newPos))
                        }
                    }
            let sink = {
                Push = (fun (t: 'T) -> cloud { do! Cloud.OfAsync <| sinkAtom.UpdateAsync(fun (pos, et) -> (pos, Array.append et [| (t, pos + et.LongLength + 1L) |] )) })
                PushBatch = (fun t -> cloud { do! Cloud.OfAsync <| sinkAtom.UpdateAsync(fun (pos, et) -> (pos, t |> Array.mapi (fun i e -> (e, pos + int64(i) + et.LongLength + 1L)) |> Array.append et )) })
                CurrentState = (fun () -> cloud { return! sinkAtom.GetValueAsync() |> Cloud.OfAsync })
            }
            let! stream = enqueueStream streamId clusterContext getter maxBatchSize
            return (sink, stream)
        }

    let everywhereStream<'TS, 'TD> (stream: ChainStream<'TD>) initialValue maxBatchSize (preFilter: IStreamFrame<'TD> -> bool) (foldF: 'TS option -> IStreamFrame<'TD> -> 'TS) = 
        cloud {
            return! streamOfStreamFold "everywhere" stream initialValue maxBatchSize preFilter foldF 
        } 
        |> Cloud.ParallelEverywhere

