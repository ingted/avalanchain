package com.avalanchain.core.chainFlow

import akka.actor.{ActorRef, Props}
import akka.stream.actor.ActorSubscriberMessage.OnNext
import akka.stream.actor.{ActorSubscriber, ActorSubscriberMessage, MaxInFlightRequestStrategy}
import com.avalanchain.core.domain.{HashedValue, _}

/**
  * Created by Yuriy on 29/04/2016.
  */
class ChainGroupByNode[T](node: CryptoContext, val chainRef: ChainRef, val snapshotInterval: Int, initial: T, keySelector: T => String) extends ActorSubscriber {
  import ActorSubscriberMessage._

  val MaxQueueSize = 10
  var queue = Map.empty[Int, ActorRef]
  def getChild(name: String) = {
    context.child(name) match {
      case Some(ch) => ch
      case None => context.actorOf(Props(new ChainPersistentActor[T](node, chainRef, snapshotInterval, initial)), name)
    }
  }

  override val requestStrategy = new MaxInFlightRequestStrategy(max = MaxQueueSize) {
    override def inFlightInternally: Int = queue.size
  }

  def receive = {
    case OnNext(msg: HashedValue[T]) =>
      getChild(keySelector(msg.value)).forward(msg)
  }
}

