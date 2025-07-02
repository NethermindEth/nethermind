# Send Blobs tool

## Run using docker

```
docker run nethermindeth/send-blobs:latest --rpcurl http://localhost:8545 --bloboptions 5 --privatekey 0x0000000000000000000000000000000000000000000000000000000000000000 --receiveraddress 0x000000000000000000000000000000000000f1c1
docker run nethermindeth/send-blobs:latest --rpcurl http://localhost:8545 --bloboptions 5x6 --privatekey 0x0000000000000000000000000000000000000000000000000000000000000000 --receiveraddress 0x000000000000000000000000000000000000f1c1 --maxfeeperblobgas 10000 --feemultiplier 4
```

## Usage

```
Usage: SendBlobs [options] [command]

Options:
  --help                                 Show help information
  --rpcurl <rpcUrl>                      Url of the Json RPC.
  --bloboptions <blobOptions>            Options in format '10x1-2', '2x5-5' etc. for the blobs.
  --privatekey <privateKey>              The key to use for sending blobs.
  --keyfile <keyFile>                    File containing private keys that each blob tx will be send from.
  --receiveraddress <receiverAddress>    Receiver address of the blobs.
  --maxfeeperblobgas <maxFeePerBlobGas>  (Optional) Set the maximum fee per blob data.
  --feemultiplier <feeMultiplier>        (Optional) A multiplier to use for gas fees.
  --maxpriorityfee <maxPriorityFee>      (Optional) The maximum priority fee for each transaction.
  --fork <fork>                          (Optional) Fork rules: Cancun/Prague/Osaka

Commands:
  distribute  Distribute funds from an address to a number of new addresses.
  reclaim     Reclaim funds distributed from the 'distribute' command.


Use "SendBlobs [command] --help" for more information about a command.

Usage: SendBlobs __distribute__ [options]

Options:
  --help                             Show help information
  --rpcurl <rpcUrl>                  Url of the Json RPC.
  --privatekey <privateKey>          The private key to distribute funds from.
  --number <number>                  The number of new addresses/keys to make.
  --keyfile <keyFile>                File where the newly generated keys are written.
  --maxpriorityfee <maxPriorityFee>  (Optional) The maximum priority fee for each transaction.
  --maxfee <maxFee>                  (Optional) The maxFeePerGas fee paid for each transaction.


Usage: SendBlobs __reclaim__ [options]

Options:
  --help                               Show help information
  --rpcurl <rpcUrl>                    Url of the Json RPC.
  --receiveraddress <receiverAddress>  The address to send the funds to.
  --keyfile <keyFile>                  File of the private keys to reclaim from.
  --maxpriorityfee <maxPriorityFee>    (Optional) The maximum priority fee for each transaction.
  --maxfee <maxFee>                    (Optional) The maxFeePerGas paid for each transaction.

sh
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
```
<<BrokenTxs
Issues/Options that can be intentionally added:
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

BrokenTxs
```

## Debug

```
For funding the private key used in the project launch settings, the address is:
0x428a95ceb38b706fbfe74fa0144701cfc1c25ef7
```

## Build

```sh
apt install libsnappy-dev dotnet-sdk-7.0 -y
cd ./nethermind/tools/SendBlobs
dotnet publish --sc -o .
./SendBlobs
```

or via docker

```sh
cd ./nethermind/ # repository root
docker build . -f ./tools/SendBlobs/Dockerfile -t send-blobs
docker run send-blobs ... # args samples above
```
