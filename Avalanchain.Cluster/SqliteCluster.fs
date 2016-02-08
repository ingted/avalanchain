﻿module Avalanchain.Cluster.SQLite

open Akka.Actor
open Akka.Cluster
open Akka.Persistence.Sqlite
open Akka.Cluster.Sharding
open Akka.FSharp
open Akka.FSharp.Actors
open Akka.FSharp.Spawn
open System
open FSharp.Core
open System.Collections.Immutable
open System.Data
open System.Data.SQLite
open Microsoft.FSharp.Quotations

open Messages
open AutomaticCluster
open Akka.Persistence.FSharp
open Akka.Persistence



type EventSourcingLogic<'TAdminCommand, 'TBusinessCommand, 'TState, 'TEvent> = {
    UpdateState: 'TState -> 'TEvent -> 'TState
    ProcessBusinessCommand: 'TBusinessCommand -> 'TEvent option // TODO: Add Chessie error reporting
    ProcessAdminCommand: 'TAdminCommand -> 'TEvent option // TODO: Add Chessie error reporting
}

type ResActor<'TAdminCommand, 'TBusinessCommand, 'TState, 'TEvent> (eventSourcingLogic) as self = 
    inherit PersistentActor()
    let mutable state = Unchecked.defaultof<'TState>
    do (UntypedActor.Context.SetReceiveTimeout(Nullable(TimeSpan.FromMinutes(2.0))))
    member private __.Self = base.Self
    member private __.Context = UntypedActor.Context
    override __.PersistenceId with get() = (sprintf "Actor %s-%s" (self.Context.Parent.Path.Name) self.Self.Path.Name)
    override __.ReceiveRecover(msg: obj) = 
        match msg with 
        | :? 'TEvent as e -> 
            state <- eventSourcingLogic.UpdateState state e
            true
        | :? SnapshotOffer as so -> 
            match so.Snapshot with
            | :? 'TState as sos -> 
                state <- sos
                true
            | _ -> false
        | _ -> false
    override this.ReceiveCommand(msg: obj) = 
        match msg with 
        | :? 'TBusinessCommand as c -> 
            match eventSourcingLogic.ProcessBusinessCommand c with 
            | Some e -> this.Persist(e, (fun ee -> (state <- eventSourcingLogic.UpdateState state ee) |> ignore))
                        true
            | None -> false
        | :? 'TAdminCommand as c -> 
            match eventSourcingLogic.ProcessAdminCommand c with 
            | Some e -> this.Persist(e, (fun ee -> (state <- eventSourcingLogic.UpdateState state ee) |> ignore)) // TODO: Rethink Admin channel logic
                        true
            | None -> false
        | _ -> false

let simpleEventSourcingLogic = {
    UpdateState = (fun state e -> e::state)
    ProcessBusinessCommand = (fun cmd -> Some(sprintf "Received '%s'" (cmd.ToString())))
    ProcessAdminCommand = (fun ac -> None)
}

type ShardedMessageExtractor() =
    interface IMessageExtractor with 
        member __.EntityId(message) = match message with
                                        | :? ShardedMessage as msg -> msg.EntityId
                                        | _ -> null
        member __.ShardId(message) = match message with
                                        | :? ShardedMessage as msg -> msg.ShardId
                                        | _ -> null
        member __.EntityMessage(message) = match message with
                                                | :? ShardedMessage as msg -> msg.Message :> Object
                                                | _ -> null


type ShardedSystem (system, clusterFactory: ActorSystem -> IAutomaticCluster) =
    let automaticCluster = clusterFactory(system)
    let sharding = ClusterSharding.Get(system)
    member __.System = system
    member __.StartShardRegion<'Message, 'TAdminCommand, 'TBusinessCommand, 'TState, 'TEvent> (messageExtractor, eventSourcingLogic, regionName, options : SpawnOption list) = 
        let expr = <@ fun () -> new ResActor<'TAdminCommand, 'TBusinessCommand, 'TState, 'TEvent>(eventSourcingLogic) @>
        let props = Props.Create (Linq.Expression.ToExpression(expr))
        let appliedProps = applySpawnOptions props options
        sharding.Start(regionName, appliedProps, ClusterShardingSettings.Create(system), messageExtractor)
    member __.StartPersisted<'Message, 'TAdminCommand, 'TBusinessCommand, 'TState, 'TEvent> (eventSourcingLogic, name, options : SpawnOption list) = 
        let expr = <@ fun () -> new ResActor<'TAdminCommand, 'TBusinessCommand, 'TState, 'TEvent>(eventSourcingLogic) @>
        let props = Props.Create (Linq.Expression.ToExpression(expr))
        let appliedProps = applySpawnOptions props options
        system.ActorOf(appliedProps, name)
    member this.StartShardRegion<'Message, 'TAdminCommand, 'TBusinessCommand, 'TState, 'TEvent> (regionName, options) = 
        this.StartShardRegion (new ShardedMessageExtractor(), simpleEventSourcingLogic, regionName, options)
    member this.StartPersisted<'Message, 'TAdminCommand, 'TBusinessCommand, 'TState, 'TEvent> (name, options : SpawnOption list) = 
        this.StartPersisted (simpleEventSourcingLogic, name, options) 

    interface IDisposable with
        member __.Dispose() = automaticCluster.Dispose() // TODO: Implement the pattern properly

 
let produceMessages (system: ActorSystem) (shardRegion: IActorRef) =
    let entitiesCount = 20
    let shardsCount = 10
    let rand = new Random()

    system.Scheduler.Advanced.ScheduleRepeatedly(
        TimeSpan.FromSeconds(1.0), 
        TimeSpan.FromSeconds(0.001), 
        fun () ->
            for i = 0 to 1 do
                let shardId = rand.Next(shardsCount)
                let entityId = rand.Next(entitiesCount)

                shardRegion.Tell({ShardId = shardId.ToString(); EntityId = entityId.ToString(); Message = "hello world"})
    )

let runExample (system: ActorSystem) =
    let shardedSystem = new ShardedSystem (system, (fun s -> new AutomaticClusterSqlite(s) :> IAutomaticCluster))
    //let shardRegion = shardedSystem.StartShardRegion messageExtractor "printer" <@ actorOf (fun msg -> printfn "Shard Received: %s\n" msg) @> []
    //let shardRegion = shardedSystem.StartShardRegion2 messageExtractor "printer" (Props.Create<ResActor>())

    let shardRegion = shardedSystem.StartShardRegion ("printer", [])

//////    // general update state method
//////    let update state e = 
//////        e::state
//////
//////    // apply is invoked when actor receives a recovery event
//////    let apply _ = update
//////
//////    // exec is invoked when a actor receives a new message from another entity
//////    let exec (mailbox: Eventsourced<_,ShardedMessage,_>) state cmd = 
//////        printfn "Cmd Received: %A\n" cmd
//////        mailbox.PersistEvent (update state) [cmd]
////////        match cmd with
////////        | "print" -> printf "State is: %A\n" state          // print current actor state
////////        | s       -> mailbox.PersistEvent (update state) [s]     // persist event and call update state on complete
//////
//////    let shardRegion = 
//////        spawnPersist system "s0" {  // s0 identifies actor uniquelly across different incarnations
//////            state = []              // initial state
//////            apply = apply           // recovering function
//////            exec = exec             // command handler
//////        } []  

    System.Threading.Thread.Sleep(2000)
    //Console.Write("Press ENTER to start producing messages...")
    //Console.ReadLine() |> ignore

    produceMessages system shardRegion