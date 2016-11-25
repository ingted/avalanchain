package com.avalanchain.jwt.jwt.actors

import java.net.InetAddress

import akka.actor.ActorSystem
import akka.stream.ActorMaterializer
import akka.util.Timeout
import com.typesafe.config.ConfigFactory
import scala.concurrent.duration._

/**
  * Created by Yuriy Habarov on 25/11/2016.
  */
trait ActorNode {
  val SystemName: String = "avalanchain"

  val port: Int

  val localhost = InetAddress.getLocalHost.getHostAddress
  val system = ActorSystem(SystemName,
    ConfigFactory.parseString(s"akka.remote.netty.tcp.host = ${localhost}")
      .withFallback(ConfigFactory.parseString(s"akka.remote.netty.tcp.port = $port"))
      .withFallback(ConfigFactory.load("application.conf")))
  implicit val materializer = ActorMaterializer()(system)
  implicit val timeout = Timeout(5 seconds)
}