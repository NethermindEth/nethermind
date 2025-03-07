@echo off
set GOEXPERIMENT=cgocheck2
go build -ldflags="-s -w" -buildmode=c-shared -o secp256r1.dll main.go