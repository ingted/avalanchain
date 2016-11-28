package com.avalanchain.jwt.jwt.chat

import java.security.{KeyPair, PrivateKey}
import java.time.OffsetDateTime

import akka.NotUsed
import akka.actor.{ActorRef, ActorSystem}
import akka.stream.Materializer
import akka.stream.scaladsl.Source
import com.avalanchain.jwt.KeysDto.PubKey
import com.avalanchain.jwt.basicChain._
import com.avalanchain.jwt.jwt.actors.network.NewChain
import com.avalanchain.jwt.jwt.demo.Demo.{ChatMsg, ChatMsgToken}
import com.avalanchain.jwt.utils.{CirceDecoders, CirceEncoders}
import io.circe.{Decoder, DecodingFailure, Encoder, Json}
import io.circe.syntax._
import io.circe.parser._
import io.circe.generic.JsonCodec
import io.circe.generic.auto._


/**
  * Created by Yuriy on 28/11/2016.
  */
class ChatNode(nodeId: NodeIdToken, keyPair: KeyPair, chainFactory: String => ChainDefToken)(implicit actorSystem: ActorSystem, materializer: Materializer)
  extends CirceEncoders with CirceDecoders {

  val chainDefToken = chainFactory("__chat__")

  val chain = new NewChain(nodeId, chainDefToken, keyPair)

  val sink = chain.sink

  val source: Source[ChatMsg, NotUsed] = chain.source[ChatMsg]
  val sourceToken = chain.sourceFrame
  val sourceJson = chain.sourceJson

  chain.process()
}
