@echo off
set GOEXPERIMENT=boringcrypto
set CGO_ENABLED=1
rem this doesn't actually work on Windows!!!
go build -ldflags="-s -w" -buildmode=c-shared -o secp256r1-boring.dll main.go
goversion -crypto -m .