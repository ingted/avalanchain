open System
open System.IO
#if INTERACTIVE
let cd = Path.Combine(__SOURCE_DIRECTORY__, "../bin/Avalanchain")
System.IO.Directory.SetCurrentDirectory(cd)
#endif

#r "../bin/Avalanchain/System.Collections.Immutable.dll"
#r "../bin/Avalanchain/Akka.dll"
#r "../bin/Avalanchain/Hyperion.dll"
#r "../bin/Avalanchain/Newtonsoft.Json.dll"
#r "../bin/Avalanchain/FSharp.PowerPack.Linq.dll"
#r "../bin/Avalanchain/FsPickler.dll"
#r "../bin/Avalanchain/Google.ProtocolBuffers.dll"
#r "../bin/Avalanchain/Google.ProtocolBuffers.Serialization.dll"
#r "../bin/Avalanchain/Akka.Remote.dll"
#r "../bin/Avalanchain/Google.ProtocolBuffers.dll"
#r "../bin/Avalanchain/Akka.Persistence.dll"
#r "../bin/Avalanchain/Akka.Cluster.dll"
#r "../bin/Avalanchain/Akka.Cluster.Tools.dll"
#r "../bin/Avalanchain/Akka.Cluster.Sharding.dll"
#r "../bin/Avalanchain/Akka.Serialization.Hyperion.dll"
#r "../bin/Avalanchain/Akkling.dll"
#r "../bin/Avalanchain/Akkling.Persistence.dll"
#r "../bin/Avalanchain/Akkling.Cluster.Sharding.dll"
#r "../bin/Avalanchain/DotNetty.Buffers.dll"
#r "../bin/Avalanchain/DotNetty.Codecs.dll"
#r "../bin/Avalanchain/DotNetty.Common.dll"
#r "../bin/Avalanchain/DotNetty.Handlers.dll"
#r "../bin/Avalanchain/DotNetty.Transport.dll"


open Akka.Actor
open Akka.Configuration
open Akka.Cluster
open Akka.Cluster.Tools.Singleton
open Akka.Cluster.Sharding
open Akka.Persistence

open Akkling
open Akkling.Persistence
open Akkling.Cluster
open Akkling.Cluster.Sharding
open Hyperion

let configWithPort port =
    let config = Configuration.parse ("""
        akka {
            actor {
              provider = "Akka.Cluster.ClusterActorRefProvider, Akka.Cluster"
              serializers {
                hyperion = "Akka.Serialization.HyperionSerializer, Akka.Serialization.Hyperion"
              }
              serialization-bindings {
                "System.Object" = hyperion
              }
            }
          remote {
            helios.tcp {
              public-hostname = "localhost"
              hostname = "localhost"
              port = """ + port.ToString() + """
            }
          }
          cluster {
            auto-down-unreachable-after = 5s
            seed-nodes = [ "akka.tcp://cluster-system@localhost:5000/" ]
          }
          persistence {
            journal.plugin = "akka.persistence.journal.inmem"
            snapshot-store.plugin = "akka.persistence.snapshot-store.local"
          }
        }
        """)
    config.WithFallback(ClusterSingletonManager.DefaultConfig())

type ClusterStatus = Get
let clusterStatus = fun (ctx : Actor<_>) ->
    let log = ctx.Log.Value
    let cluster = Cluster.Get(ctx.System)
    let rec loop nodes = actor {
        let! (msg: obj) = ctx.Receive ()
        match msg with
        | LifecycleEvent e ->
            match e with
            | PreStart ->
                cluster.Subscribe(ctx.Self, ClusterEvent.InitialStateAsEvents,
                    [| typedefof<ClusterEvent.IMemberEvent> |])
                log.Info (sprintf "Actor subscribed to Cluster status updates: %A" ctx.Self)
            | PostStop ->
                cluster.Unsubscribe(ctx.Self)
                log.Info (sprintf "Actor unsubscribed from Cluster status updates: %A" ctx.Self)

            | _ -> return Unhandled
            return! loop nodes
        | IMemberEvent e ->
            match e with
            | MemberJoined m ->
                log.Info (sprintf "Node joined: %A" m)
                return! loop (nodes |> Map.add (m.Address.ToString(), m.UniqueAddress.ToString()) m.Status)
            | MemberUp m ->
                log.Info (sprintf "Node up: %A" m)
                return! loop (nodes |> Map.add (m.Address.ToString(), m.UniqueAddress.ToString()) m.Status)
            | MemberLeft m ->
                log.Info (sprintf "Node left: %A" m)
                return! loop (nodes |> Map.add (m.Address.ToString(), m.UniqueAddress.ToString()) m.Status)
            | MemberExited m ->
                log.Info (sprintf "Node exited: %A" m)
                return! loop (nodes |> Map.add (m.Address.ToString(), m.UniqueAddress.ToString()) m.Status)
            | MemberRemoved m ->
                log.Info (sprintf "Node removed: %A" m)
                return! loop (nodes |> Map.remove (m.Address.ToString(), m.UniqueAddress.ToString()))
            return! loop nodes
        | :? ClusterStatus as cs ->
            match cs with ClusterStatus.Get -> ctx.Sender() <! nodes
            return! loop nodes
        | _ -> return Unhandled
    }
    loop (Map.empty)

let system1 = System.create "cluster-system" (configWithPort 5000)
let node1 = spawn system1 "clusterStatus" <| props (clusterStatus)

// wait a while before starting a second system
System.Threading.Thread.Sleep(200)

let system2 = System.create "cluster-system" (configWithPort 5001)

let printNodes clusterStatus =
    async {
        let! reply = (retype clusterStatus) <? ClusterStatus.Get
        let (stats: Map<string * string, MemberStatus>) = reply.Value
        for kv in stats do
            let (a, ua) = kv.Key
            let ms = kv.Value
            printfn "\tNode '%s':'%s' status: '%A'" a ua ms
    } |> Async.RunSynchronously

printNodes node1

let system3 = System.create "cluster-system" (configWithPort 5002)
let node3 = spawn system3 "clusterStatus" <| props (clusterStatus)

printNodes node3
