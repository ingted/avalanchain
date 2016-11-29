package com.avalanchain.jwt.jwt.demo

import java.math.MathContext
import java.security.KeyPair
import java.time.OffsetDateTime
import java.util.UUID
import java.util.concurrent.atomic.AtomicReference

import akka.NotUsed
import akka.actor.ActorSystem
import akka.stream.Materializer
import akka.stream.scaladsl.{Flow, Sink, Source}
import com.avalanchain.jwt.KeysDto.PubKey
import com.avalanchain.jwt.basicChain._
import com.avalanchain.jwt.jwt.CurveContext
import com.avalanchain.jwt.jwt.actors.network.NewChain
import com.avalanchain.jwt.jwt.demo.account.AccountCommand._
import com.avalanchain.jwt.utils.{CirceCodecs, CirceSimpleCodecs}
import com.rbmhtechnology.eventuate.DurableEvent
import com.rbmhtechnology.eventuate.adapter.stream.DurableEventWriter
import org.joda.time.field.OffsetDateTimeField

import scala.collection.immutable.Seq
import io.circe.{Decoder, DecodingFailure, Encoder, Json}
import io.circe.syntax._
import io.circe.parser._
import io.circe.generic.JsonCodec
import io.circe.generic.auto._

import scala.util.Random


/**
  * Created by Yuriy Habarov on 28/11/2016.
  */
package object account {
  type AccountId = UUID
  //type SignedAccountId = Signed[AccountId]

  case class Account(accountId: AccountId, expire: OffsetDateTime, pubAcc: PubKey, pub: PubKey)

  sealed trait AccountCommand extends JwtPayload.Asym { def accountId: AccountId }
  object AccountCommand {
    final case class Add(accountId: AccountId, balance: PaymentAmount, expire: OffsetDateTime, pubAcc: PubKey, pub: PubKey) extends AccountCommand
    final case class Block(accountId: AccountId, pub: PubKey) extends AccountCommand
    final case class Disable(accountId: AccountId, pub: PubKey) extends AccountCommand
  }
  trait AccountCommandCodecs extends CirceSimpleCodecs {
    import io.circe.generic.semiauto._
    implicit val encoderAccountCommand: Encoder[AccountCommand] = deriveEncoder
    implicit val decoderAccountCommand: Decoder[AccountCommand] = deriveDecoder
  }
  object AccountCommandCodecs extends AccountCommandCodecs


  type AccountEvent = TypedJwtToken[AccountCommand]

  type PaymentAmount = BigDecimal
  type PaymentBalances = Map[AccountId, PaymentAmount]

  sealed trait AccountStatus
  object AccountStatus {
    final case object Active extends AccountStatus
    final case object Blocked extends AccountStatus
    final case object Disabled extends AccountStatus
  }

  case class AccountState(account: Account, balance: PaymentAmount, status: AccountStatus) extends JwtPayload.Sym
  trait AccountStateCodecs extends AccountCommandCodecs {
    import io.circe.generic.semiauto._
    implicit val encoderAccountState: Encoder[AccountState] = deriveEncoder
    implicit val decoderAccountState: Decoder[AccountState] = deriveDecoder
  }
  object AccountStateCodecs extends AccountStateCodecs
  type AccountStates = Map[AccountId, AccountState]

  trait PaymentAttempt
  case class PaymentTransaction(from: AccountId, to: AccountId, amount: PaymentAmount) extends PaymentAttempt

  trait PaymentRejection extends PaymentAttempt
  object PaymentRejection {
    case class WrongSignature(sig: String) extends PaymentRejection
    case class FromAccountNotExists(account: AccountId) extends PaymentRejection
    case class ToAccountMissing(account: AccountId) extends PaymentRejection
    case class UnexpectedNonPositiveAmount(amount: PaymentAmount) extends PaymentRejection
    case class NotEnoughFunds(available: PaymentAmount, expected: PaymentAmount) extends PaymentRejection
  }

  case class Transaction(from: AccountId, to: AccountId, amount: PaymentAmount, pub: PubKey) extends JwtPayload.Asym
//
//  class CurrencyChain(nodeId: NodeIdToken, chainDefToken: ChainDefToken, keyPair: KeyPair)
//                (implicit actorSystem: ActorSystem, materializer: Materializer)
//    extends NewChain(nodeId, chainDefToken, keyPair) {
//
//
//    def sink = Flow[Cmd].map(DurableEvent(_)).via(DurableEventWriter(newId, commandLog.get)).to(Sink.ignore)
//
//    override def processingLogic(state: ChainState, event: DurableEvent): (ChainState, Seq[FrameToken]) = {
//      event.payload match {
//        case cmd: Cmd => toFrame(state, cmd.v)
//      }
//    }
//  }

  class CurrencyNode(nodeId: NodeIdToken, keyPair: KeyPair, chainFactory: String => ChainDefToken)(implicit actorSystem: ActorSystem, materializer: Materializer)
    extends CirceCodecs {

    val accountChainDefToken = chainFactory("__accounts__")
    val accountChain = new NewChain(nodeId, accountChainDefToken, keyPair)

    val accountSink = Flow[AccountCommand].map(pt => Cmd(pt.asJson)).to(accountChain.sink)

    val accountSource: Source[AccountCommand, NotUsed] = accountChain.source[AccountCommand]
    val accountSourceToken = accountChain.sourceFrame
    val accountSourceJson = accountChain.sourceJson

    val accountsSource: Source[AccountStates, NotUsed] = accountSource.scan(Map.empty[AccountId, AccountState])((acc, ac) => ac match {
      case Add(accountId, balance, expire, pubAcc, pub) =>
        val account = Account(accountId, expire, pubAcc, pub)
        val accountState = AccountState(account, balance, AccountStatus.Active)
        acc + (accountId -> accountState)
      case Block(accountId, pub) =>
        val accountState = acc(accountId).copy(status = AccountStatus.Blocked)
        acc + (accountId -> accountState)
      case Disable(accountId, pub) =>
        val accountState = acc(accountId).copy(status = AccountStatus.Disabled)
        acc + (accountId -> accountState)
    })

    private val accountStates = new AtomicReference(Map.empty[AccountId, AccountState])
    private val accountStatesUpdater = accountsSource.runForeach(accountStates.set(_))

    val transactionChainDefToken = chainFactory("__transactions__")
    val transactionChain = new NewChain(nodeId, transactionChainDefToken, keyPair)

    val transactionSink = Flow[PaymentTransaction].map(pt => Cmd(Transaction(pt.from, pt.to, pt.amount, keyPair.getPublic).asJson)).to(transactionChain.sink)

    val transactionSource: Source[Transaction, NotUsed] = transactionChain.source[Transaction]
    val transactionSourceToken = transactionChain.sourceFrame
    val transactionSourceJson = transactionChain.sourceJson


    private val processAccountFuture = accountChain.process()
    private val processTransactionsFuture = transactionChain.process()

    val trace = accountChain.sourceDES.runForeach(e => println(s"DES: $e"))
    //val trace2 = accountChain.source.runForeach(e => println(s"DES: ${e.payloadJson}"))
//    val accountCommand = Add(UUID.randomUUID(), 1000, OffsetDateTime.now().plusYears(1), CurveContext.newKeys().getPublic, keyPair.getPublic)
//    Source.single(Cmd(accountCommand.asJson)).runWith(chainNode.chatNode.sink)

    def randomPayment() = {
      val accStates = accountStates.get().values.toArray
      val from = accStates(Random.nextInt(accStates.length))
      val to = accStates(Random.nextInt(accStates.length))
      val amount = (Random.nextDouble() * from.balance.round(MathContext.DECIMAL32)).round(MathContext.DECIMAL32)
      val payment = PaymentTransaction(from.account.accountId, to.account.accountId, amount)
      Source.single(payment).runWith(transactionSink)
    }

    def addAccount1000(): AccountCommand.Add = {
      val accountCommand = Add(UUID.randomUUID(), 1000, OffsetDateTime.now().plusYears(1), CurveContext.newKeys().getPublic, keyPair.getPublic)
      Source.single(accountCommand).runWith(accountSink)
      accountCommand
    }

    (0 until 100).foreach(_ => addAccount1000)
    (0 until 100).foreach(_ => randomPayment)
  }
}