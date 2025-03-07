package main

import (
  "C"
  "crypto/ecdsa"
  "crypto/elliptic"
  "math/big"
)

//export VerifyBytes
func VerifyBytes(bytes []byte) bool {
  if len(bytes) != 160 {
    return false
  }

  var hash = bytes[0:32]
  var r, s = new(big.Int).SetBytes(bytes[32:64]), new(big.Int).SetBytes(bytes[64:96])
  var x, y = new(big.Int).SetBytes(bytes[96:128]), new(big.Int).SetBytes(bytes[128:160])
  var publicKey = &ecdsa.PublicKey{Curve: elliptic.P256(), X: x, Y: y}

  return ecdsa.Verify(publicKey, hash, r, s)
}

func main() {}
