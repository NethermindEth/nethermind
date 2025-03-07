package main

import (
  "C"
  "crypto/ecdsa"
  "crypto/elliptic"
  "math/big"
  "unsafe"
)

//export VerifyBytes
func VerifyBytes(data *C.uchar, length C.int) C.uchar {
  if length != 160 {
    return 0
  }

  // Create a zero-copy slice from the raw pointer
  bytes := unsafe.Slice((*byte)(unsafe.Pointer(data)), length)

  // Extract values directly from the slice
  var hash = bytes[0:32]
  var r, s = new(big.Int).SetBytes(bytes[32:64]), new(big.Int).SetBytes(bytes[64:96])
  var x, y = new(big.Int).SetBytes(bytes[96:128]), new(big.Int).SetBytes(bytes[128:160])
  var publicKey = &ecdsa.PublicKey{Curve: elliptic.P256(), X: x, Y: y}

  if ecdsa.Verify(publicKey, hash, r, s) {
    return 1
  }
  return 0
}

func main() {}
