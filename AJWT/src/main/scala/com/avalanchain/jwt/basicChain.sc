import java.nio.charset.{Charset, StandardCharsets}
import java.nio.file.{Files, Path, Paths, StandardOpenOption}
import java.security.{PrivateKey, PublicKey}

import pdi.jwt._
import java.time.Instant
import java.util.UUID
import java.time.OffsetDateTime
import java.time.format.DateTimeFormatter

import com.avalanchain.jwt.jwt.{CurveContext, UserInfo}
import io.circe._
import io.circe.generic.auto._
import io.circe.parser._
import io.circe.syntax._

import scala.collection.immutable._
import scala.util.{Success, Try}
import collection.JavaConverters._
import CurveContext._
import akka.NotUsed
import akka.actor.ActorSystem
import akka.stream.{ActorMaterializer, OverflowStrategy}
import akka.stream.scaladsl.{Sink, Source}
import com.avalanchain.jwt.KeysDto._
import com.avalanchain.jwt.basicChain._
import com.typesafe.config.ConfigFactory
import io.circe.generic.JsonCodec
import org.joda.time.DateTime
import org.joda.time.format.ISODateTimeFormat
import pdi.jwt.exceptions.JwtLengthException

import scala.collection.mutable.ArrayBuffer
import scala.concurrent.Await


val config =
  """
    |akka {
    |  version = 2.4.12
    |
    |  # Loggers to register at boot time (akka.event.Logging$DefaultLogger logs
    |  # to STDOUT)
    |  loggers = ["akka.event.slf4j.Slf4jLogger"]
    |
    |  # Log level used by the configured loggers (see "loggers") as soon
    |  # as they have been started; before that, see "stdout-loglevel"
    |  # Options: OFF, ERROR, WARNING, INFO, DEBUG
    |  loglevel = "DEBUG"
    |
    |  # Log level for the very basic logger activated during ActorSystem startup.
    |  # This logger prints the log messages to stdout (System.out).
    |  # Options: OFF, ERROR, WARNING, INFO, DEBUG
    |  stdout-loglevel = "DEBUG"
    |
    |  # Filter of log events that is used by the LoggingAdapter before
    |  # publishing log events to the eventStream.
    |  logging-filter = "akka.event.slf4j.Slf4jLoggingFilter"
    |
    |  persistence.journal.plugin = "akka.persistence.journal.leveldb"
    |  persistence.snapshot-store.plugin = "akka.persistence.snapshot-store.local"
    |
    |  persistence.journal.leveldb.dir = "target/state/journal"
    |  persistence.snapshot-store.local.dir = "target/state/snapshots"
    |
    |# DO NOT USE THIS IN PRODUCTION !!!
    |# See also https://github.com/typesafehub/activator/issues/287
    |  persistence.journal.leveldb.native = false
    |
    |}
  """.stripMargin

implicit val system = ActorSystem("test", ConfigFactory.parseString(config))
//implicit val system = ActorSystem("mySystem")
implicit val materializer = ActorMaterializer()

//Source(0 until 200).to(Sink.foreach(println)).run()

def time[R](block: => R): R = {
  val t0 = System.nanoTime()
  val result = block    // call-by-name
  val t1 = System.nanoTime()
  println(s"Elapsed time: ${(t1 - t0)} ns or ${(t1 - t0)/1000000} ms")
  result
}

val result = time { 1 to 1000000 sum }

import scala.concurrent.duration._
import scala.concurrent._

val res = time {
  val r = Source(0 until 1000000).runWith(Sink.fold(0)(_+_))
  Await.result(r, 10 seconds)
}

//val fts = new MapFrameTokenStorage()
//val cr = new ChainRegistry(savedKeys(), fts)
//val nc = cr.newChain()
//println(s"Token count: ${fts.frameTokens.toList.length}")
//
//case class Data(int: Int, string: String, double: Double)
//
//time {
//  val r = Source(0 until 1000000)
//    .map(i => (i, nc.add(Data(i, i.toString, i).asJson)))
//    .map(i => { if (i._1 % 100000 == 99999) println(i._1); i })
//    .runWith(Sink.ignore)
//  Await.result(r, 120 seconds)
//}
//
//println(s"Token count: ${fts.frameTokens.toList.length}")
//fts.frameTokens.foreach(e => println(s"Tokens key: '${e._1}', val: '${e._2}'"))

//trait FrameTokenStorage {
//  def add(frameToken: FrameToken): Try[Unit]
//  def get(frameRef: FrameRef): Option[FrameToken]
//  //def get(chainRef: ChainRef, pos: Position): Option[FrameToken]
//}

import collection.JavaConverters._

class FileTokenStorage(val folder: Path, val id: String, batchSize: Int = 10, timeWindow: FiniteDuration = 1 second,
                       implicit val system: ActorSystem, implicit val materializer: ActorMaterializer) extends FrameTokenStorage {
  val location = folder.resolve(id)
  private var currentBatchN = 0
  private var indexInBatch = 0
  private var batch =

  Files.createDirectories(location)

  private var groupIdx = -1
  val queue = Source.queue[FrameToken](batchSize, OverflowStrategy.backpressure)
      .groupedWithin(batchSize, timeWindow)
      .map(g => { groupIdx += 1; (groupIdx, g) })
      .to(Sink.foreach(b => {
        val fileName = location.resolve(f"${b._1}%08d")
        Files.deleteIfExists(fileName)
        Files.write(fileName, b._2.map(_.toString).asJavaCollection, StandardCharsets.UTF_8, StandardOpenOption.CREATE_NEW)
      })).run()

  override def add(frameToken: FrameToken): Try[Unit] = {
    queue.offer(frameToken)
    Success()
  }

  override def get(frameRef: FrameRef): Option[FrameToken] = ???

  def getFrom(position: Position)(implicit decoder: Decoder[Frame]): Source[FrameToken, NotUsed] = {
    Source.fromIterator[Path](() => Files.newDirectoryStream(location).iterator().asScala.toList.sorted.iterator)
      .mapConcat[String](f => Files.readAllLines(f).asScala.toList).map(s => new JwtTokenSym[Frame](s))
//      .fold(ArrayBuffer[FrameToken]())((acc: ArrayBuffer[FrameToken], ft) => { acc += ft; acc })
//      .map(a: FrameToken => a.sortBy(_.payload.get.pos))
      .filter(_.payload.get.pos >= position)
  }
}



val fts = new FileTokenStorage(Paths.get("""C:\tmp\AJWT\"""), UUID.randomUUID().toString, 100, 1 second, system, materializer)
val cr = new ChainRegistry(savedKeys(), fts)
val nc = cr.newChain()

//println(s"Token count: ${fts.frameTokens.toList.length}")

case class Data(int: Int, string: String, double: Double)

time {
  val r = Source(0 until 10000)
    .map(i => (i, nc.add(Data(i, i.toString, i).asJson)))
    .map(i => { if (i._1 % 100 == 99) println(i._1); i })
    .runWith(Sink.ignore)
  Await.result(r, 120 seconds)
}

fts.getFrom(0).runForeach(e => println(s"Token: '${e}'"))
//println(s"Token count: ${fts.getFrom(0).toList.length}")





////type Hash = String
////type Sig = String
////
////sealed trait Shackle { val }
////object Shackle {
////  case class Seed(key: PubKey)
////  case class Frame(pos: Position, hash: Hash)
////}
//
//
//
//val user1 = UserInfo("John", "Smith", "john.smith@e.co.uk", "07711223344")
//
//case class T1(userInfo: UserInfo, str: String)
//val t1 = T1(user1, "asdasdasdas,mb,mb,mb,mb,mb,mbdasdasd")
//
//val content = t1.asJson.spaces2
//
//val token1 = Jwt.encode(content, privateKeyEC, JwtAlgorithm.ES512)
////JwtOptions.DEFAULT.
//
////val jwt = JwtToken(token1)
//
//Jwt.decode(token1, publicKeyEC, Seq(JwtAlgorithm.ES512))
//
//val aa = token1.split('.')
//println(aa)
//
//
//val (content2, token2) = encodeUser(privateKeyEC, user1)
//
////val user2 = decodeUser(publicKeyEC)(token1).get
//val userJson = Jwt.decodeRawAll(token1, publicKeyEC, Seq(JwtAlgorithm.ES512)).get
//val jsonHeader = userJson._1
//val jsonBody = userJson._2
//val signature = userJson._3
//
//signature.length
//
//
//val token3 = Jwt.encode(content, signature, JwtAlgorithm.HS512)
////JwtOptions.DEFAULT.
//
//val t3 = Jwt.decode(token3, signature, Seq(JwtAlgorithm.HS512))
