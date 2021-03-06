package com.avalanchain.jwt.jwt.actors

import java.net.InetAddress
import java.security.KeyPair
import java.util.UUID

import akka.NotUsed
import akka.actor.{Actor, ActorContext, ActorLogging, ActorRef, ActorRefFactory, ActorSystem, ExtendedActorSystem, Props}
import akka.pattern.{ask, pipe}
import akka.stream.ActorMaterializer
import akka.stream.actor.ActorPublisher
import akka.stream.scaladsl.{Sink, Source}
import akka.util.Timeout
import com.avalanchain.jwt.utils._
import com.avalanchain.jwt.basicChain.{Frame, _}
import com.avalanchain.jwt.jwt.actors.ChainRegistryActor.{GetFrameSource, GetFrameTokenSource, GetJsonSource, JwtError, _}
import com.avalanchain.jwt.jwt.actors.network.{ChainLogFactory, LevelDBLogFactory, NetworkMonitor, NodeStatus}
import com.avalanchain.jwt.utils.CirceCodecs
import com.typesafe.config.ConfigFactory
import io.circe.Json

import scala.concurrent.{ExecutionContext, Future}
import scala.concurrent._
import scala.concurrent.duration._
import scala.concurrent.ExecutionContext.Implicits.global

/**
  * Created by Yuriy Habarov on 25/11/2016.
  */
trait ActorNode extends CirceCodecs {
  val SystemName: String = "avalanchain"

  val localhost = InetAddress.getLocalHost.getHostAddress

  def port: Int
  def keyPair: KeyPair
  val publicKey = keyPair.getPublic
  protected val privateKey = keyPair.getPrivate

  val nodeIdToken: NodeIdToken = NodeIdToken("AC", localhost, port, publicKey, privateKey)

  println(s"Port - ${port}")
  implicit val system = ActorSystem(SystemName,
    ConfigFactory.parseString(s"akka.remote.netty.tcp.host = ${localhost}")
      .withFallback(ConfigFactory.parseString(s"akka.remote.netty.tcp.port = $port"))
      .withFallback(ConfigFactory.load("node.conf")))
  implicit val materializer = ActorMaterializer()(system)
  implicit val executor: ExecutionContext = system.dispatcher
  implicit val timeout = Timeout(5 seconds)
  implicit val logFactory = new LevelDBLogFactory(nodeIdToken, system)

  private val myAddress = system.asInstanceOf[ExtendedActorSystem].provider.rootPath.address

  case object GetNodePort

  private val addr = system.actorOf(Props(new Actor {
    def receive = {
      case GetNodePort => sender() ! system.asInstanceOf[ExtendedActorSystem].provider.getDefaultAddress.port
    }
  }), "addr")

  def localport(): Future[Int] = (addr ? GetNodePort).map(_.asInstanceOf[Option[Int]].get)

  // TODO: Drop the methods
  def newChain(jwtAlgo: JwtAlgo = JwtAlgo.HS512, id: Id = randomId) = {
    val chainDef: ChainDef = ChainDef.New(jwtAlgo, id, keyPair.getPublic, ResourceGroup.ALL)
    val chainDefToken = TypedJwtToken[ChainDef](chainDef, keyPair.getPrivate)
    chainDefToken
  }

  def derivedChain(parentRef: ChainRef, jwtAlgo: JwtAlgo = JwtAlgo.HS512, id: Id = randomId): (ChainDefToken, ChainDef.Derived) = {
    val chainDef = ChainDef.Derived(jwtAlgo, id, keyPair.getPublic, ResourceGroup.ALL, parentRef, ChainDerivationFunction.Map("function(a) { return { b: a.e + 'aaa' }; }"))
    val chainDefToken = TypedJwtToken[ChainDef](chainDef, keyPair.getPrivate)
    (chainDefToken, chainDef)
  }

//  def newChain2(jwtAlgo: JwtAlgo = JwtAlgo.HS512, id: Id = UUID.randomUUID().toString.replace("-", ""), initValue: Option[Json] = Some(Json.fromString("{}"))) = {
//    (registry ? CreateChain(newChain(jwtAlgo, id, initValue))).mapTo[ChainCreationResult]
//  }
//
//  def getChain(chainRef: ChainRef) = (registry ? GetChainByRef(chainRef)).mapTo[Either[ChainRegistryError, (ChainDefToken, ActorRef)]]
//
//  def sink(chainRef: ChainRef) = (registry ? GetJsonSink(chainRef)).mapTo[Either[ChainRegistryError, Sink[Json, NotUsed]]]
//
//  def source(chainRef: ChainRef, from: Position, to: Position) =
//    (registry ? GetJsonSource(chainRef, from, to)).mapTo[Either[ChainRegistryError, Source[Either[JwtError, Json], NotUsed]]]
//
//  def sourceF(chainRef: ChainRef, from: Position, to: Position) =
//    (registry ? GetFrameSource(chainRef, from, to)).mapTo[Either[ChainRegistryError, Source[Either[JwtError, Frame], NotUsed]]]
//
//  def sourceFT(chainRef: ChainRef, from: Position, to: Position) =
//    (registry ? GetFrameTokenSource(chainRef, from, to)).mapTo[Either[ChainRegistryError, Source[FrameToken, NotUsed]]]
//
//  def monitorSource() = {
//    val monitorRef = actor("monitor" + (UUID.randomUUID().toString.replace("-", "")))(new NetworkMonitor())
//    Source.fromPublisher[NodeStatus](ActorPublisher(monitorRef))
//  }
//
//  def nodesSnapshot(): Future[Map[NodeStatus.Address, NodeStatus]] = {
//    monitorSource().takeWithin(10 milliseconds).runFold(Map.empty[NodeStatus.Address, NodeStatus])((acc, s) => { acc + (s.address -> s) })
//  }
}
