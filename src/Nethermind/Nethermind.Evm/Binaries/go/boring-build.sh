export GOEXPERIMENT=boringcrypto
export CGO_ENABLED=1
go build -ldflags="-s -w" -buildmode=c-shared -o secp256r1-boring.so main.go
./goversion -crypto -m .