﻿#I "../packages/Newtonsoft.Json.8.0.2/lib/net45"
#r "../packages/FSharp.Interop.Dynamic.3.0.0.0/lib/portable-net45+sl50+win/FSharp.Interop.Dynamic.dll"
#r "../packages/FSharp.Core.Fluent-4.0.1.0.0.5/lib/portable-net45+netcore45+wpa81+wp8+MonoAndroid1+MonoTouch1/FSharp.Core.Fluent-4.0.dll"
#r "../packages/FSharpx.Extras.1.10.3/lib/40/FSharpx.Extras.dll"
#r "../packages/FSharpx.Async.1.12.0/lib/net40/FSharpx.Async.dll"
#r "../packages/FSharpx.Collections.1.13.4/lib/net40/FSharpx.Collections.dll"
#r "../packages/Chessie.0.2.2/lib/net40/Chessie.dll"
#r "../packages/FSharp.Quotations.Evaluator.1.0.7/lib/net40/FSharp.Quotations.Evaluator.dll"
#r "../packages/FsPickler.1.7.1/lib/net45/FsPickler.dll"
#r "../packages/FsPickler.Json.1.7.1/lib/net45/FsPickler.Json.dll"

#load "SecPrimitives.fs"
#load "SecKeys.fs"
#load "RefsAndPathes.fs"
#load "StreamEvent.fs"
#load "Projection.fs"
#load "Quorum.fs"
#load "Acl.fs"
#load "Utils.fs"
#load "EventStream.fs"
#load "EventProcessor.fs"
#load "FrameSynchronizer.fs"
#load "Node.fs"


open System
open System.Threading
open System.Threading.Tasks

open FSharp.Interop.Dynamic
open FSharp.Core.Fluent
open Chessie.ErrorHandling

open Avalanchain
open SecKeys
open SecPrimitives
open RefsAndPathes
open StreamEvent
open Projection
open EventProcessor
open EventStream
open Quorum
open System.Dynamic
open Node



Parallel.For(0, 2, (fun i -> 
    let ct = cryptoContextRSANet("RSA Test")

    let ss = serializeFunction ct.HashSigner Utils.picklerSerializer ct.Hasher
    let ds = deserializeFunction ct.ProofVerifier Utils.picklerDeserializer
    let projectionStorage = ProjectionStorage<int, int>(ss, ds)

    let addProj f = 
        let res = projectionStorage.Add(f)
        (returnOrFail res)


    let sumF = addProj <@ fun (a:int) (b:int) -> ok (a + b) @>

    let executionSigner signer serializer executionProofData = 
        let serd = serializer executionProofData
        let signed = signer (Unsigned serd)
        signed

    let serializers = picklerSerializers

    let srdh = dataHasher serializers.streamRef ct
    let sddh = dataHasher serializers.streamDef ct
    let dh = dataHasher serializers.data ct 
    let edh = dataHasher serializers.event ct 
    let eph = dataHasher serializers.ep ct 
    let framedh = dataHasher serializers.frame ct 
    let pfr = proofer (executionSigner ct.Signer serializers.epd) eph
    let permissionsChecker hashedEvent = ok()

    let eventProcessor = processEvent ct picklerSerializers dh permissionsChecker pfr

    let streamRef = {
        EventStreamRef.Path = "/stream/0"
        Version = 0u
    }

    let hashedStreamRef = srdh streamRef

    let streamDef = {
        Ref = hashedStreamRef
        Projection = sumF
        EmitsTo = []
        ExecutionPolicy = ExecutionPolicy.None 
    }

    let hashedStreamDef = sddh streamDef



    let intEventStream = 
        EventStream (hashedStreamDef, ct.Hasher, framedh, eventProcessor, serializers.frame) :> IEventStream<int, int>

    let node = {
        Path = "/node/0"
        CryptoContext = ct
        Serializers = serializers
        Streams = EventStreamBag([intEventStream])
        EventHasher = edh
        ExecutionGroups = [ExecutionGroup "_main_"]
    }

    let res = node.Push hashedStreamRef 1

    let ress = [for i in 2..1000 -> i] |> List.map (fun i -> node.Push hashedStreamRef i)
    ress |> ignore
))

