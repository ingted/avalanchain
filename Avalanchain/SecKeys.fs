﻿module Avalanchain.SecKeys

open System
open System.Text
open System.Security.Cryptography
open Base58Check

open Avalanchain.SecPrimitives

type PublicKey = byte array
and SigningPublicKey = PublicKey
and EncryptionPublicKey = PublicKey
//and PrivateKey = SecurityKey

[<StructuredFormatDisplay("{AsString}")>]
type Unsigned = Unsigned of Serialized
    with 
        member inline this.Bytes = match this with Unsigned s -> s
        member inline m.AsString = m.ToString()
        override this.ToString() = Convert.ToBase64String(this.Bytes)
[<StructuredFormatDisplay("{AsString}")>]
type Signed = Signed of byte array * SigningPublicKey
    with 
        member inline this.Bytes = match this with Signed (s, _) -> s
        member inline m.AsString = m.ToString()
        override this.ToString() = Convert.ToBase64String(this.Bytes)

type Signature = 
    | Signature of SimpleSignature
    | RingSignature of RingSignature // one of group unsure which one
    | GroupSignature of GroupSignature // (k of n)
    | RemotableSignature of RingSignature * SimpleSignature // simple visible inside the group and ring one visible outside and inside
    with 
        member inline this.Signed = 
            match this with Signature s -> (match s with RSA ss | NotSigned ss -> Some ss | _ -> None) | _ -> None // TODO: Revisit for other cases
        member inline this.SigningPublicKey = 
            match this with Signature s -> (match s with RSA (Signed (ba, spk)) | NotSigned (Signed (ba, spk)) -> Some spk | _ -> None) | _ -> None
and SimpleSignature =
    | NotSigned of Signed
    | RSA of Signed
    | DSA // to be used
    | ECDSA // Ed25519 
    | ElGamal
    | Schnorr // to be used for rings as well
    | PointchevalStern
and RingSignature = RingSignature // LSAG lib
and GroupSignature = GroupSignature

type Signer = Unsigned -> Signature
type Verifier = Signature -> Unsigned -> bool

type Proof = {
    Signature: Signature
    ValueHash: Hash
}

type SignedProof<'T> = {
    Value: 'T
    Proof: Proof
}

type ProofVerifier = Proof -> bool

type Encryption =
    | Unencrypted
    | RSA
    | DHNet
    | SECP256k1
    | Curve25519 

type Encrypted = Encrypted of byte array
type Decrypted = Decrypted of byte array
type Encryptor = Decrypted -> Encrypted
type Decryptor = Encrypted -> Decrypted
let encrypt encryptor value = 
    match value with Decrypted d -> Encrypted(encryptor d)
let decrypt decryptor value = 
    match value with Encrypted e -> Decrypted(decryptor e)

type CryptoContext (*<'TData>*) = {
    Hasher: Hasher
    SigningPublicKey: SigningPublicKey
    Signer: Signer
    Verifier: Verifier
    EncryptionPublicKey: EncryptionPublicKey 
    //PrivateKey: PrivateKey
    //SignatureMethod: Signature
    EncryptionMethod: Encryption
    Encryptor: Encryptor
    Decryptor: Decryptor
    Dispose: unit -> unit
}
with 
    member this.ProofVerifier proof = this.Verifier proof.Signature (Unsigned proof.ValueHash.Bytes)
    member this.HashSigner (hash: Hash) = this.Signer (Unsigned hash.Bytes)
    member this.Address = this.SigningPublicKey |> this.Hasher |> (fun h -> Base58CheckEncoding.Encode h.Bytes)

let dataHasher serializer cryptoContext data = 
    let serialized = serializer data
    { Hash = cryptoContext.Hasher serialized; Value = data }

//// Dummy
let cryptoContextDummy = 
    let pubKey = [|1uy; 2uy; 3uy|]
    { 
        Hasher = (fun bytes -> Hash(bytes))
        SigningPublicKey = pubKey
        EncryptionPublicKey = pubKey
        EncryptionMethod = Unencrypted
        Encryptor = (fun data -> data |> encrypt (fun d -> d))
        Decryptor = (fun data -> data |> decrypt (fun d -> d))
        Signer = (fun (Unsigned data) -> 
            Signature(SimpleSignature.NotSigned(Signed(data, pubKey))))
        Verifier = (fun signature (Unsigned data) ->
                        match signature with
                        | Signature s -> 
                            match s with 
                            | SimpleSignature.NotSigned (Signed (bytes, pk)) -> data = bytes // Change to VerifyHash()?
                            | _ -> false
                        | _ -> false
                    )
        Dispose = (fun () -> ())
    }
 
//// RSA.Net

let cryptoContextRSANet containerName = 
    let cp = new CspParameters()
    cp.KeyContainerName <- containerName
    cp.Flags <- CspProviderFlags.UseMachineKeyStore
    let rsa = new RSACryptoServiceProvider(cp)
    let sha = new SHA256Managed()
    let rsaParams = rsa.ExportParameters(false) // true - for exporting private key
    let pubKey = rsaParams.Modulus
    { 
        Hasher = (fun bytes -> Hash(sha.ComputeHash(sha.ComputeHash(bytes))))
        SigningPublicKey = pubKey // NOTE: Exponent is always the same by convention: rsaParams.Exponent |> Convert.ToBase64String = "AQAB"
        EncryptionPublicKey = pubKey
        EncryptionMethod = RSA
        Encryptor = (fun data -> data |> encrypt (fun d -> rsa.Encrypt(d, true)))
        Decryptor = (fun data -> data |> decrypt (fun d -> rsa.Decrypt(d, true)))
        Signer = (fun (Unsigned data) -> 
            Signature(SimpleSignature.RSA(Signed(rsa.SignData(data, sha), pubKey))))
        Verifier = (fun signature (Unsigned data) ->
                        match signature with
                        | Signature s -> 
                            match s with 
                            | SimpleSignature.RSA (Signed (bytes, pk)) -> rsa.VerifyData(data, sha, bytes) // Change to VerifyHash()?
                            | _ -> false
                        | _ -> false
                    )
        Dispose = (fun () -> 
                    sha.Dispose() 
                    rsa.PersistKeyInCsp <- false
                    rsa.Clear()
                    rsa.Dispose())
    }
 
let cryptoContextDHNet = 
    let ecdh = new ECDiffieHellmanCng()
    ecdh.KeyDerivationFunction <- ECDiffieHellmanKeyDerivationFunction.Hash
    ecdh.HashAlgorithm <- CngAlgorithm.Sha256
    let sha = new SHA256Managed()
    let dsa = new ECDsaCng()
    let enc data = data // TODO: Placeholder. Needs a proper implementation 
    let dec data = data // TODO: Placeholder. Needs a proper implementation 
    let spk = dsa.Key.Export(CngKeyBlobFormat.EccPublicBlob) 
    dsa.HashAlgorithm <- CngAlgorithm.Sha256
    { 
        Hasher = (fun bytes -> Hash(sha.ComputeHash(bytes)))
        SigningPublicKey = spk
        EncryptionPublicKey = ecdh.PublicKey.ToByteArray()
        EncryptionMethod = DHNet
        Encryptor = (fun data -> data |> encrypt (fun d -> enc(d)))
        Decryptor = (fun data -> data |> decrypt (fun d -> dec(d)))
        Signer = (fun (Unsigned data) -> Signature(SimpleSignature.RSA(Signed(dsa.SignData(data), spk))))
        Verifier = (fun signature (Unsigned data) ->
                        match signature with
                        | Signature s -> 
                            match s with 
                            | SimpleSignature.RSA (Signed (bytes, _)) -> 
                                dsa.VerifyData(data, bytes) // Change to VerifyHash()?
                            | _ -> false
                        | _ -> false
                    )
        Dispose = (fun () -> 
                    dsa.Dispose() 
                    ecdh.Clear()
                    ecdh.Dispose())
    }


let cryptoContext() = cryptoContextRSANet "Test"
let cryptoContextNamed name = cryptoContextRSANet name
//let cryptoContext() = cryptoContextDummy
