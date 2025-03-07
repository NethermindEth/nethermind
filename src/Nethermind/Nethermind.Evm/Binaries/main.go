package main

import (
	"C"
	"crypto/ecdsa"
	"crypto/elliptic"
	"fmt"
	"math/big"
	"unsafe"
)

//export VerifyBytes
func VerifyBytes(data *C.uchar, length C.int) C.uchar {
	fmt.Printf("VerifyBytes called with data=%p length=%d\n", data, length)

	if length != 160 {
		fmt.Println("Invalid length")
		return 0
	}

	// Create a zero-copy slice from the raw pointer
	bytes := unsafe.Slice((*byte)(unsafe.Pointer(data)), length)

	// Check if the data looks corrupted
	fmt.Printf("First 8 bytes: %x\n", bytes[:8])

	// Extract values directly from the slice
	var hash = bytes[0:32]
	var r, s = new(big.Int).SetBytes(bytes[32:64]), new(big.Int).SetBytes(bytes[64:96])
	var x, y = new(big.Int).SetBytes(bytes[96:128]), new(big.Int).SetBytes(bytes[128:160])
	var publicKey = &ecdsa.PublicKey{Curve: elliptic.P256(), X: x, Y: y}

	if ecdsa.Verify(publicKey, hash, r, s) {
		fmt.Println("Signature verified")
		return 1
	}
	fmt.Println("Signature failed")
	return 0
}

func main() {}
