# Send Blobs tool

## Run using docker

```
docker run nethermindeth/send-blobs:latest --rpcurl http://localhost:8545 --bloboptions 5 --privatekey 0x0000000000000000000000000000000000000000000000000000000000000000 --receiveraddress 0x000000000000000000000000000000000000f1c1
docker run nethermindeth/send-blobs:latest --rpcurl http://localhost:8545 --bloboptions 5x6 --privatekey 0x0000000000000000000000000000000000000000000000000000000000000000 --receiveraddress 0x000000000000000000000000000000000000f1c1 --maxfeeperblobgas 10000 --feemultiplier 4
```

## Usage


The tool can help with:

- blob spamming with random data, from multiple accounts
- sending files as blobs,
- batch funds distribution

Use "SendBlobs [command] --help" for more information about supported commands.

The default fork for now is Prague, which means blob will be sent with V0 proofs. use `--fork Osaka` option to change it to V1.

## Build

```sh
apt install libsnappy-dev dotnet-sdk-9.0 -y
cd ./nethermind/tools/SendBlobs
dotnet publish --sc -o .
./SendBlobs
```

or via docker

```sh
cd ./nethermind/ # repository root
docker build . -f ./tools/SendBlobs/Dockerfile -t send-blobs
docker run send-blobs
```

### Examples

```sh
./SendBlobs  --rpcurl           http://localhost:8545 # url-that-does-not-require-auth-in-header
             --bloboptions      1000,5x6,100x2        # transaction count: just a number or a list of tx-count x blob-count
             --privatekey       0x0000..0000          # secret-key
             --receiveraddress  0x0000..0042          # receiver-address
             --maxfeeperblobgas 10000                 # data gas price limit, 1000 by default
             --feemultiplier    4                     # fee multiplier to compete with other txs in the pool, 4 by default

# send 5 transactions, 1 blob each
./SendBlobs --rpcurl http://localhost:8545 --bloboptions 5 \
                                             0x0000000000000000000000000000000000000000000000000000000000000000 \
                                             0x000000000000000000000000000000000000f1c1 10000 4

# send several transactions with 1 blob, with 6 blobs and than with 2 blobs
./SendBlobs --rpcurl http://localhost:8545
            --bloboptions 10x1,10x6,10x2 \
            --privatekey  0x0000000000000000000000000000000000000000000000000000000000000000 \
            --receiveraddress 0x000000000000000000000000000000000000f1c1 \
            --maxfeeperblobgas 10000 \
            --feemultiplier 4

#send a couple of transactions

./SendBlobs --rpcurl http://localhost:8545 \
            --bloboptions 2x4-1 \
            --privatekey 0x0000000000000000000000000000000000000000000000000000000000000000 \
            --receiveraddress 0x0000000000000000000000000000000000000001 \
            --maxfeeperblobgas 10000 \
            --feemultiplier 4
```

## Blob options

Issues/Options that can be intentionally added to simulate broken transactions:

```
  1    = 0 blobs
  2    = 1st blob has more blobs than allowed
  3    = 1st blob is shorten
  4    = 1st blob's wrong proof
  5    = 1st blob's wrong commitment
  6    = 1st blob with a modulo correct, but > modulo value
  7    = max fee per blob gas = max value
  
  9    = 1st proof removed
  10   = 1st commitment removed
  11   = max fee per blob gas = max value / blobgasperblob + 1
  14   = 100 blobs
  15   = 1000 blobs
  wait = wait for each transaction to be included in a block before posting the next

Syntax:

         2x3-4
         ^      tx count
           ^    blobs in every tx (optional, default to 1)
             ^  how it's broken (optional, tx is correct by default) or write true
```

## Debug

For funding the private key used in the project launch settings, the address is: `0x428a95ceb38b706fbfe74fa0144701cfc1c25ef7`
