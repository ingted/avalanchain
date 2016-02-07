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


Parallel.For(0, 16, (fun i -> 
    let nodePath = sprintf "%d" i
    let node = defaultProjections |> buildNode nodePath |> returnOrFail
    let streamRef = node.Streams.Refs.head()

    let ress = [for i in 0M..1M..999M -> i] |> List.map (fun i -> node.Push streamRef i)
    ress |> ignore
))
