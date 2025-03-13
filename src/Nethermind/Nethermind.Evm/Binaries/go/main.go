package main

// #include <stdlib.h>
import "C"

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"fmt"
	"math/big"
	"runtime"
	"runtime/debug"
	"unsafe"
)

//export VerifyBytes
func VerifyBytes(data *C.uchar, length C.int) C.uchar {
	debug.SetPanicOnFault(true)

	fmt.Printf("VerifyBytes: data=%p length=%d\n", data, length)
	if length != 160 {
		fmt.Printf("VerifyBytes: invalid length\n")
		return 0
	}

	// Create a zero-copy slice from the raw pointer
	fmt.Printf("VerifyBytes: creating slice\n")
	bytes := unsafe.Slice((*byte)(unsafe.Pointer(data)), length)

	// Extract values directly from the slice
	var hash = bytes[0:32]
	var r, s = new(big.Int).SetBytes(bytes[32:64]), new(big.Int).SetBytes(bytes[64:96])
	var x, y = new(big.Int).SetBytes(bytes[96:128]), new(big.Int).SetBytes(bytes[128:160])
	var publicKey = &ecdsa.PublicKey{Curve: elliptic.P256(), X: x, Y: y}

	fmt.Printf("VerifyBytes: checking signature\n")
	if ecdsa.Verify(publicKey, hash, r, s) {
		fmt.Printf("VerifyBytes: signature valid\n")
		return 1
	}
	fmt.Printf("VerifyBytes: signature invalid\n")
	return 0
}

//export ForceGC
func ForceGC() {
	fmt.Println("gc: Forcing GC")
	runtime.GC() // Force garbage collection
	fmt.Println("gc: Forced GC")
}

//export ReportGC
func ReportGC() {
	var before runtime.MemStats
	runtime.ReadMemStats(&before)

	runtime.GC()

	var after runtime.MemStats
	runtime.ReadMemStats(&after)

	fmt.Printf("ReportGC:\n\tBefore: Alloc = %v TotalAlloc = %v Sys = %v NumGC = %v\n\tAfter Alloc = %v TotalAlloc = %v Sys = %v NumGC = %v\n",
		before.Alloc/1024, before.TotalAlloc/1024, before.Sys/1024, before.NumGC,
		after.Alloc/1024, after.TotalAlloc/1024, after.Sys/1024, after.NumGC,
	)
}

func main() {}
