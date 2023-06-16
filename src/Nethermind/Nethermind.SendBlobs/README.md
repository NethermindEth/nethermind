# Send Blobs tool

## Building

```sh
apt install libsnappy-dev dotnet-sdk-7.0 -y
cd ./nethermind/src/Nethermind/Nethermind.SendBlobs
dotnet publish --sc -o .
```

or via docker

```sh
cd ./nethermind/ # repository root
docker build . -f ./src/Nethermind/Nethermind.SendBlobs/Dockerfile -t send-blobs
docker run send-blobs ... # args sample below
```

## Usage

```sh
./Nethermind.SendBlobs <url-without-auth> <transactions-count-1-blob-each> <secret-key> <receiver-address>
./Nethermind.SendBlobs http://localhost:8545 5 0x0000000000000000000000000000000000000000000000000000000000000000 0x000000000000000000000000000000000000f1c1
```
