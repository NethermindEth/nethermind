Configuration
*************

InitConfig
^^^^^^^^^^

- JsonRpcEnabled - enables RPC endpoints (configured also through HttpHost and HttpPort)

- NetworkEnabled - if disabled then all networking (discovery, peer manager, sync) is disabled

- DiscoveryEnabled - enables / disables discovery protocol for finding new peers on the network

- SynchronizationEnabled - enables / disabled eth62/ eth63 synchronizations (if disabled then new blocks will not be downloaded and downloaded)

- ProcessingEnabled - enables / disables processing of downloaded blocks

- IsMining - enables blocks production (by validators in PoA networks and miners in PoW networks)

- P2PPort - port to listen for incoming connections in devp2p protocols (most typically 30303)

- DiscoveryPort - port to listen for incoming connections in discovery protocol

- ChainSpecPath - path to a chainspec file that defines network properties like genesis block, initial allocations, network id, sealing engine

- GenesisHash - if left empty no genesis block hash check is done, if set then chainspec genesis procesing result is validated against the hash provided

- BaseDbPath - base path for all database directories (it is combined with baseDbPath from command line)

- LogFileName - name of the log file (e.g. mainnet.log)

- ObsoletePendingTransactionInterval - if transaction is known to be older than this many seconds then we do not broadcast it after hearing about it from other nodes

- RemovePendingTransactionInterval - time (in seconds) after which pending transactions are removed from memory

- PeerNotificationThreshold - percentage of peers that will be notified about new pending transactions

DbConfig
^^^^^^^^

- WriteBufferSize - size of a single memory buffer for RocksDB data

- WriteBufferNumber - number of RocksDB write buffers

- BlockCacheSize - size of the data block cache for RocksDB

- CacheIndexAndFilterBlocks - set to true to limit mory taken by RocksDB to the size of WriteBufferSize * WiteBufferNumber + BlockCacheSize, otherwise index and filter blocks may take significant amount of memory (many gigabytes)

Sample configuration (mainnet)
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

::

    [
      {
        "ConfigModule": "InitConfig",
        "ConfigItems": {
          "JsonRpcEnabled": false,
          "NetworkEnabled": true,
          "DiscoveryEnabled": true,
          "SynchronizationEnabled": true,
          "PeerManagerEnabled": true,
          "ProcessingEnabled": true,
          "IsMining": false,
          "DiscoveryPort": 30312,
          "P2PPort": 30312,
          "HttpHost": "127.0.0.1",
          "HttpPort": 8345,
          "ChainSpecPath": "chainspec/foundation.json",
          "GenesisHash": "0xd4e56740f876aef8c010b86a40d5f56745a118d0906a34e69aec8c0db1cb8fa3",
          "BaseDbPath": "nethermind_db/mainnet",
          "LogFileName": "mainnet.logs.txt",
          "ObsoletePendingTransactionInterval": 15,
          "RemovePendingTransactionInterval": 600,
          "PeerNotificationThreshold": 20
        }
      },
      {
        "ConfigModule": "DbConfig",
        "ConfigItems": {
          "WriteBufferSize": 67108864,
          "WriteBufferNumber": 6,
          "BlockCacheSize": 67108864,
          "CacheIndexAndFilterBlocks": true
        }
      }
    ]
