package com.avalanchain.jwt.jwt.actors.network

import java.security.KeyPair
import java.util.UUID

import akka.actor.{ActorRef, ActorRefFactory, ActorSystem}
import akka.stream.Materializer
import akka.stream.scaladsl.{Flow, Sink, Source}
import com.avalanchain.jwt.basicChain.ChainDef.{Derived, Fork, New}
import com.avalanchain.jwt.basicChain.ChainDerivationFunction._
import com.avalanchain.jwt.basicChain.JwtAlgo.{ES512, HS512}
import com.avalanchain.jwt.basicChain._
import com.avalanchain.jwt.jwt.script.{ScriptFunction, ScriptFunction2, ScriptPredicate}
import com.rbmhtechnology.eventuate.DurableEvent
import com.rbmhtechnology.eventuate.adapter.stream.{DurableEventSource, DurableEventWriter}
import com.rbmhtechnology.eventuate.adapter.stream.DurableEventProcessor._
import com.rbmhtechnology.eventuate.log.leveldb.LeveldbEventLog
import io.circe.{Decoder, Encoder, Json}
import io.circe.parser._
import io.circe.syntax._
import io.circe.generic.auto._
import cats.implicits._

import scala.collection.immutable.Seq
import scala.util.Try

/**
  * Created by Yuriy Habarov on 26/11/2016.
  */


abstract class Chain (
  val nodeId: NodeId,
  val chainDefToken: ChainDefToken,
  val keyPair: KeyPair,
  protected val commandLogName: Option[String],
  implicit val actorSystem: ActorSystem,
  implicit val materializer: Materializer) {

  if (chainDefToken.payload.isEmpty) throw new RuntimeException(s"Inconsistent ChainDefToken: '$chainDefToken'")
  val chainRef = ChainRef(chainDefToken)
  var statusInternal: ChainStatus = ChainStatus.Created
  def status = statusInternal

  protected def newId() = UUID.randomUUID().toString
  protected def createLog(id: String = chainRef.sig): ActorRef =
    actorSystem.actorOf(LeveldbEventLog.props(id, nodeId))

  protected val commandLog = commandLogName.map(createLog(_))
  protected val eventLog = createLog()
  protected val initialVal = parse("{}").getOrElse(Json.Null)
  protected val initialState = ChainState(None, new FrameRef(chainRef.sig), -1, initialVal)
  def sourceFrame = Source.fromGraph(DurableEventSource(eventLog)).map(_.payload.asInstanceOf[FrameToken])
  def source = sourceFrame.map(_.payloadJson)

  private var currentFrame: Option[FrameToken] = None
  private var currentValue: Option[Json] = None
  private var currentPosition: Position = -1
  def current = currentValue
  def pos = currentPosition

  protected def processCurrent = sourceFrame.runWith(Sink.foreach(frame => {
    currentFrame = Some(frame)
    if (frame.payload.nonEmpty) {
      currentPosition = frame.payload.get.pos
      currentValue = Some(frame.payload.get.v
      )
    }
  }))

  protected def toFrame(state: ChainState, v: Json) = {
    val newPos: Position = state.pos + 1
    val token = chainDefToken.payload.get.algo match {
      case HS512 => TypedJwtToken[FSym](FSym(chainRef, newPos, state.lastRef, v), state.lastRef.sig).asInstanceOf[FrameToken]
      case ES512 => TypedJwtToken[FAsym](FAsym(chainRef, newPos, state.lastRef, v, keyPair.getPublic), keyPair.getPrivate).asInstanceOf[FrameToken]
    }
    (ChainState(Some(token), FrameRef(token), newPos, v), Seq(token))
  }

  def processingLogic(state: ChainState, event: DurableEvent): (ChainState, Seq[FrameToken])

  protected val process =
    commandLog.map(
      cl => Source.fromGraph(DurableEventSource(cl))
        .via(statefulProcessor(newId, eventLog)(initialState)(processingLogic))
        .runWith(Sink.ignore))

}
//object Chain {
//
//}

class NewChain(nodeId: NodeId, chainDefToken: ChainDefToken, keyPair: KeyPair)
               (implicit actorSystem: ActorSystem, materializer: Materializer)
  extends Chain(nodeId, chainDefToken, keyPair, Some(ChainRef(chainDefToken).sig + "COM"), actorSystem, materializer) {


  def sink = Flow[Cmd].map(DurableEvent(_)).via(DurableEventWriter(newId, commandLog.get)).to(Sink.ignore)

  def processingLogic(state: ChainState, event: DurableEvent): (ChainState, Seq[FrameToken]) = {
    event.payload match {
      case cmd: Cmd => toFrame(state, cmd.v)
    }
  }
}


class DerivedChain(nodeId: NodeId, chainDefToken: ChainDefToken, keyPair: KeyPair, derived: Derived)
                   (implicit actorSystem: ActorSystem, materializer: Materializer)
  extends Chain(nodeId, chainDefToken, keyPair, Some(derived.parent.sig), actorSystem, materializer) {

  val func: (Json, Json) => Seq[Json] =
    derived.cdf match {
      case Copy => (_, e) => Seq(e) // TODO: Deal with failures explicitely
      case ChainDerivationFunction.Map(f) => (_, e) => Seq(ScriptFunction(f)(e))
      case Filter(f) => (_, e) => if (ScriptPredicate(f)(e)) Seq(e) else Seq()
      //case Fold(f, init) => source.map(_.fold(init)((acc, e) => ScriptFunction2(f)(acc, e)).to(PersistentSink[Json](chainDefToken)))
      case Fold(f, init) => (acc, e) => Seq(ScriptFunction2(f)(acc, e))
      case GroupBy(f, max) => throw new RuntimeException("Not implemented") //source.map(_.toOption.get).groupBy(max, e => ScriptFunction(f)(e)).to(PersistentSink[Json](chainDefToken))
      case Reduce(f) => (acc, e) => Seq(ScriptFunction2(f)(acc, e))
    }

  def processingLogic(state: ChainState, event: DurableEvent): (ChainState, Seq[FrameToken]) = {
    event.payload match {
      case ft: FrameToken =>
        if (ft.payload.nonEmpty) {
          val ret = func(state.lastValue, ft.payload.get.v).map(v => toFrame(state, v)).unzip
          (ret._1.last, ret._2.flatten)
        }
        else {
        statusInternal = ChainStatus.Failed(s"Incorrect FrameToken payload format: ${ft.payloadJson}")
        (state, Seq[FrameToken]())
      }
    }
  }
}