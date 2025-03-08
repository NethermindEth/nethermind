package main

import (
	"C"
	"fmt"
)

//export VerifyBytes
func VerifyBytes() {
	fmt.Printf("Go: VerifyBytes called")
}

func main() {}
