# Send Blobs tool

## Run using docker

```
docker run ghcr.io/flcl42/send-blobs:latest http://localhost:8545 5 0x0000000000000000000000000000000000000000000000000000000000000000 0x000000000000000000000000000000000000f1c1
docker run ghcr.io/flcl42/send-blobs:latest http://localhost:8545 5x6 0x0000000000000000000000000000000000000000000000000000000000000000 0x000000000000000000000000000000000000f1c1 10000 4
```

## Usage

```sh
./SendBlobs http://localhost:8545 # url-that-does-not-require-auth-in-header
                       1000,5x6,100x2        # transaction count: just a number or a list of tx-count x blob-count
                       0x0000..0000          # secret-key
                       0x0000..0042          # receiver-address
                       10000                 # data gas price limit, 1000 by default
                       4                     # fee multiplier to compete with other txs in the pool, 4 by default

# send 5 transactions, 1 blob each
./SendBlobs http://localhost:8545 5 \
                                             0x0000000000000000000000000000000000000000000000000000000000000000 \
                                             0x000000000000000000000000000000000000f1c1 10000 4

# send several transactions with 1 blob, with 6 blobs and than with 2 blobs
./SendBlobs http://localhost:8545 10x1,10x6,10x2 \
                                             0x0000000000000000000000000000000000000000000000000000000000000000 \
                                             0x000000000000000000000000000000000000f1c1 10000 4

#send a couple of transactions

./SendBlobs http://localhost:8545 2x4-1 \
                                             0x0000000000000000000000000000000000000000000000000000000000000000 \
                                             0x0000000000000000000000000000000000000001 10000 4

<<BrokenTxs
Issues that can be intentionally added:
  1 = 0 blobs
  2 = 1st blob is of wrong size
  3 = 7 blobs
  4 = 1st blob's wrong proof
  5 = 1st blob's wrong commitment
  6 = 1st blob with a modulo correct, but > modulo value
  7 = max fee per blob gas = max value
  8 = max fee per blob gas > max value
  9 = 1st proof removed
  10 = 1st commitment removed
  11 = max fee per blob gas = max value / blobgasperblob + 1
  14 = 100 blobs
  15 = 1000 blobs

Syntax:

         2x3-4
         ^      tx count
           ^    blobs in every tx (optional, default to 1)
             ^  how it's broken (optional, tx is correct by default)

BrokenTxs
```

## Build

```sh
apt install libsnappy-dev dotnet-sdk-7.0 -y
cd ./tools/SendBlobs
dotnet publish --sc -o .
./SendBlobs
```

or via docker

```sh
cd ./nethermind/ # repository root
docker build . -f ./tools/SendBlobs/Dockerfile -t send-blobs
docker run send-blobs ... # args samples above
```
