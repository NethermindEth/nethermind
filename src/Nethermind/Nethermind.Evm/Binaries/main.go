package main

import (
	"C"
	"fmt"
	"runtime/debug"
	"unsafe"
)

func init() {
	debug.SetMaxStack(16 * 1024 * 1024) // 16 MB stack per goroutine
}

//export VerifyBytes
func VerifyBytes(data *C.uchar, length C.int) C.uchar {
	fmt.Printf("Go: VerifyBytes called with data=%p length=%d\n", data, length)

	if length != 160 {
		fmt.Println("Go: Invalid length")
		return 0
	}

	// Create a zero-copy slice from the raw pointer
	bytes := unsafe.Slice((*byte)(unsafe.Pointer(data)), length)

	// Check if the data looks corrupted
	fmt.Printf("Go: First 8 bytes: %x\n", bytes[:8])

	return 1
}

func main() {}
