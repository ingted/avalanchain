﻿#r "../packages/FSharp.Core.Fluent-4.0.1.0.0.5/lib/portable-net45+netcore45+wpa81+wp8+MonoAndroid1+MonoTouch1/FSharp.Core.Fluent-4.0.dll"
#r "../packages/FSharpx.Extras.1.10.3/lib/40/FSharpx.Extras.dll"
#r "../packages/FSharpx.Async.1.12.0/lib/net40/FSharpx.Async.dll"
#r "../packages/FSharpx.Collections.1.14.0/lib/net40/FSharpx.Collections.dll"
#r "../packages/Base58Check.0.2.0/lib/Net40/Base58Check.dll"

#load "SecPrimitives.fs"
#load "SecKeys.fs"

open System
open System.Text
open System.Security.Cryptography

open Avalanchain
open Avalanchain.SecKeys

//let cp = new CspParameters()
//cp.KeyContainerName <- "Test"
//let rsa = new RSACryptoServiceProvider(cp)
//let rsaParams = rsa.ExportParameters(false) // true - for exporting private key
// 
//let PublicKey = rsaParams.Modulus 
//let exp = rsaParams.Exponent |> Convert.ToBase64String

let keyPairTest keysGenerator = 
    let keyPair = keysGenerator()
    let testData = Encoding.ASCII.GetBytes("Test string")
    let encodedData = keyPair.Encryptor(Decrypted testData)
    let (Decrypted decodedData) = keyPair.Decryptor(encodedData) 
    let decodedText = Encoding.ASCII.GetString(decodedData)

    let signed = keyPair.Signer (Unsigned testData)
    let verified = keyPair.Verifier signed (Unsigned testData)
    testData.[0] <- 0uy
    let verified2 = keyPair.Verifier signed (Unsigned testData)
    verified && (not verified2)

let tRSA = keyPairTest (fun () -> cryptoContextRSANet "Test")
let t1 = keyPairTest (fun () -> cryptoContextDHNet)
let tDefault = keyPairTest cryptoContext


///////

let hash0 = [| for i in 0uy .. 5uy -> i |]
let hash1 = [| for i in 10uy .. 15uy -> i |]
let hash2 = [| for i in 20uy .. 25uy -> i |]
let hash3 = [| for i in 30uy .. 35uy -> i |]
let hash4 = [| for i in 40uy .. 45uy -> i |]

let hasher = cryptoContext().Hasher
let serializer = (fun h -> h)

let zeroMerkle = [] |> SecPrimitives.toMerkle serializer hasher None
let oneMerkle = [ hash0 ] |> SecPrimitives.toMerkle serializer hasher None
let twoMerkle = [ hash0; hash1 ] |> SecPrimitives.toMerkle serializer hasher None
let threeMerkle = [ hash0; hash1; hash2 ] |> SecPrimitives.toMerkle serializer hasher None
let fourMerkle = [ hash0; hash1; hash2; hash3 ] |> SecPrimitives.toMerkle serializer hasher None
let fiveMerkle = [ hash0; hash1; hash2; hash3; hash4 ] |> SecPrimitives.toMerkle serializer hasher None


