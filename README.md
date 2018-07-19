<img src="Nethermind.png" width="600">
Full Ethereum client written in.NET including devp2p, EVM, full archive node synchronization (ETH62).

## Contributors welcome
At Nethermind we are building an Open Source multiplatform Ethereum client implementation in .NET Core (running seamlessly both on Linux and Windows). Simultaneously our team works on Nethermind trading tools, analytics and decentralized exchange (0x relay).

Nethermind client can be used in your projects, when setting up private Ethereum networks or dApps. Nethermind is under development and below is the long list of items that are still to be implemented (and we would love to see open source contributions here):

### core EVM related:
Improve performance (heap allocations) of EVM by replacing BigInteger with Int256 implementation: one can use int128 implementation as a basis for this work
https://github.com/ricksladkey/dirichlet-numerics

### networking / devp2p
1) Implement light client implementation (LES protocol)
2) Implement Warp sync protocol (from Parity - PAR protocol)
3) Reverse engineer and implement discovery v5 protocol from Geth
4) Implement eth63 sync protocol (geth fast sync)

### consensus
1) Implement Clique (PoA as in Rinkeby by Geth)
2) Implement PoA as in Parity (integrate with Kivan network)

### JSON RPC / dev usability
1) Add solc (solidity compiler) and add tools for deploying contracts

### tools / private network
1) Test sync processes with Hive tests

### store / DB
1) Tune RocksDB to limit memory usage (ideally remove dependency on RocksDB sharp and add our own wrapper around C++ library with PInVokes just for the functions we use)
2) Further improve performance of RLP decoding / encoding by using Recyclable Memory Streams everywhere
3) Implement pruning

### research / future implementations
1) implement sharding
2) implement Casper
3) support plasma cash
4) state channels

# Links
http://nethermind.io/
