﻿// Learn more about F# at http://fsharp.org

open System
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open avalanchain.Common.x509
open avalanchain.Common.Jwt

open Org.BouncyCastle.Pkcs
open Org.BouncyCastle.Crypto.Parameters

[<EntryPoint>]
let main argv =
    printfn "Hello World from F#!"

    let keyPair = generateKeys()

    let cert = generateCertificate "cert1" keyPair

    let certPem = cert |> toPem
    let privPem = keyPair.Private |> toPem
    printfn "Cert %A" (certPem)

    printfn "Cert %A" (cert.GetPublicKey() |> toPem)

    printfn "Priv %A" (privPem)

    let cert2 = new X509Certificate2(certPem |> Text.Encoding.UTF8.GetBytes)

    let pubKey = cert2.GetECDsaPublicKey()

    printfn "Cert2 %A" (cert2)
    printfn "pubKey %A" (pubKey)

    // let cert2 = new X509Certificate2((certPem + "\n" + privPem) |> Text.Encoding.UTF8.GetBytes)

    // printfn "Cert2 %A" (cert2)
    // printfn "PrivKey %A" (cert2.GetECDsaPrivateKey())

    let fileName = "X509.store"
    let friendlyName = "cert1Store"
    let password = "password"
    let cert3 = toPkcs12 friendlyName password cert keyPair
    printfn "Cert3 %A" (cert3)


    let cert4 = new X509Certificate2(cert3, password)
    printfn "Cert4 %A" (cert4)
    printfn "Cert4.HasPrivateKey %A" (cert4.HasPrivateKey)
    printfn "Cert4.Pub %A" ((cert4.GetECDsaPublicKey() :?> ECDsaCng).Key.Export(CngKeyBlobFormat.EccPublicBlob))
    printfn "Cert4.Pub %A" ((cert4.GetECDsaPublicKey() :?> ECDsaCng).Key.Export(CngKeyBlobFormat.EccFullPublicBlob))
    printfn "Cert4.Pub %A" ((cert4.GetECDsaPublicKey() :?> ECDsaCng).Key.Export(CngKeyBlobFormat.GenericPublicBlob))
    printfn "Cert4.Params %A" ((cert4.GetKeyAlgorithmParametersString()))
    
    printfn "asdsada"

    // let pkcs8Blob = PrivateKeyInfoFactory.CreatePrivateKeyInfo(keyPair.Private).GetDerEncoded()
    // let importedKey = CngKey.Import(pkcs8Blob, CngKeyBlobFormat.Pkcs8PrivateBlob, CngProvider.MicrosoftSoftwareKeyStorageProvider)
    // printfn "importedKey: %A" importedKey

    // let privK = (cert4.GetECDsaPrivateKey() :?> ECDsaCng)
    // // privK.ExportPolicy <- CngExportPolicies.AllowPlaintextExport
    // printfn "Cert4.Priv %A" (privK)

    // let encoded = Jose.JWT.Encode("ASDFGGH", privK.Key, Jose.JwsAlgorithm.ES384)
    // printfn "encoded: %s" encoded

    // // printfn "validated: %s" (Jose.JWT.(encoded, privK.Key, Jose.JwsAlgorithm.ES384
    // printfn "decoded: %s" (Jose.JWT.Decode(encoded, privK.Key, Jose.JwsAlgorithm.ES384))

    //// let key2 = CngKey.Create(CngAlgorithm.ECDsaP384)
    //// printfn "decoded: %s" (Jose.JWT.Decode(encoded, key2, Jose.JwsAlgorithm.ES384))


    // let bcPKInfo = PrivateKeyInfoFactory.CreatePrivateKeyInfo(keyPair.Private).GetDerEncoded()
    // printfn "Cert4.Priv %A" (bcPKInfo)

    let eccKeys (keyPair: Org.BouncyCastle.Crypto.AsymmetricCipherKeyPair) = 
        let d = (keyPair.Private :?> ECPrivateKeyParameters).D.ToByteArray()
        let pubKey = keyPair |> publicKeyParam
        let x = pubKey.Q.AffineXCoord.ToBigInteger().ToByteArray()
        let y = pubKey.Q.AffineYCoord.ToBigInteger().ToByteArray()
        printfn "%A" ((keyPair.Private :?> ECPrivateKeyParameters).D)
        printfn "%A" (pubKey.Q.AffineXCoord.ToBigInteger())
        printfn "%A" (pubKey.Q.AffineYCoord.ToBigInteger())
        printfn "%d %d %d" x.Length y.Length d.Length
        
        Security.Cryptography.EccKey.New(x, y, d, CngKeyUsages.Signing)
        //CngKey.Create(CngAlgorithm.ECDsaP384)
        
        // let ecParameters = ECParameters()
        // ecParameters.Curve = ECCurve.NamedCurves.nistP384
        //ECDsaCng.Create(ECParameters())


    //let ecdsa = (new ECDsaCng(ECCurve.NamedCurves.nistP384)) 
    let ecdsa = eccKeys(keyPair)
    printfn "ecdsa: %A" ((ecdsa))

    //X509CertificateBuilder

    let ecc = ecc384Keys()
    printfn "Ecc %A" (ecc)


    Console.ReadLine() |> ignore

    0 // return an integer exit code
