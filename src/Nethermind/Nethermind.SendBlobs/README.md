# Send Blobs tool

## Run using docker

```
docker run ghcr.io/flcl42/send-blobs:latest http://localhost:8545 5 0x0000000000000000000000000000000000000000000000000000000000000000 0x000000000000000000000000000000000000f1c1
docker run ghcr.io/flcl42/send-blobs:latest http://localhost:8545 5x6 0x0000000000000000000000000000000000000000000000000000000000000000 0x000000000000000000000000000000000000f1c1 10000 4
```

## Usage

```sh
./Nethermind.SendBlobs http://localhost:8545 # url-without-auth> \
                       1000,5x6,100x2        # transaction count: just a number or a list of tx-count x blob-count \
                       0x0000..0000          # secret-key \
                       0x0000..0042          # receiver-address \
                       10000                 # data gas price limit, 1000 by default \
                       4                     # fee multiplier to compete with other txs in the pool, 4 by default
# more example
# send 5 transactions
./Nethermind.SendBlobs http://localhost:8545 5 0x0000000000000000000000000000000000000000000000000000000000000000 0x000000000000000000000000000000000000f1c1 10000 4
# send several trasnactions with 1 blob, then with 6 blobs and with 2 blobs in the end
./Nethermind.SendBlobs http://localhost:8545 10x1,10x6,10x2 0x0000000000000000000000000000000000000000000000000000000000000000 0x000000000000000000000000000000000000f1c1 10000 4
```

## Build

```sh
apt install libsnappy-dev dotnet-sdk-7.0 -y
cd ./nethermind/src/Nethermind/Nethermind.SendBlobs
dotnet publish --sc -o .
./Nethermind.SendBlobs
```

or via docker

```sh
cd ./nethermind/ # repository root
docker build . -f ./src/Nethermind/Nethermind.SendBlobs/Dockerfile -t send-blobs
docker run send-blobs ... # args sample below
```


