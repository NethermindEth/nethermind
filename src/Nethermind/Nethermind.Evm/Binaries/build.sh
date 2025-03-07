export GOEXPERIMENT=cgocheck2
go build -race -ldflags="-s -w" -buildmode=c-shared -o secp256r1.so main.go