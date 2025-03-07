package main

import (
	"C"
	"fmt"
	"runtime/debug"
)

func init() {
	debug.SetMaxStack(16 * 1024 * 1024) // 16 MB stack per goroutine
}

//export VerifyBytes
func VerifyBytes(data *C.uchar, length C.int) C.uchar {
	fmt.Printf("Go: VerifyBytes called with data=%p length=%d\n", data, length)
	return 1
}

func main() {}
