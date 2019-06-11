Configuration
*************

DbConfig
^^^^^^^^

 - BlockCacheSize - description missing

 - BlockInfosDbBlockCacheSize - description missing

 - BlockInfosDbCacheIndexAndFilterBlocks - description missing

 - BlockInfosDbWriteBufferNumber - description missing

 - BlockInfosDbWriteBufferSize - description missing

 - BlocksDbBlockCacheSize - description missing

 - BlocksDbCacheIndexAndFilterBlocks - description missing

 - BlocksDbWriteBufferNumber - description missing

 - BlocksDbWriteBufferSize - description missing

 - CacheIndexAndFilterBlocks - description missing

 - CodeDbBlockCacheSize - description missing

 - CodeDbCacheIndexAndFilterBlocks - description missing

 - CodeDbWriteBufferNumber - description missing

 - CodeDbWriteBufferSize - description missing

 - HeadersDbBlockCacheSize - description missing

 - HeadersDbCacheIndexAndFilterBlocks - description missing

 - HeadersDbWriteBufferNumber - description missing

 - HeadersDbWriteBufferSize - description missing

 - PendingTxsDbBlockCacheSize - description missing

 - PendingTxsDbCacheIndexAndFilterBlocks - description missing

 - PendingTxsDbWriteBufferNumber - description missing

 - PendingTxsDbWriteBufferSize - description missing

 - ReceiptsDbBlockCacheSize - description missing

 - ReceiptsDbCacheIndexAndFilterBlocks - description missing

 - ReceiptsDbWriteBufferNumber - description missing

 - ReceiptsDbWriteBufferSize - description missing

 - TraceDbBlockCacheSize - description missing

 - TraceDbCacheIndexAndFilterBlocks - description missing

 - TraceDbWriteBufferNumber - description missing

 - TraceDbWriteBufferSize - description missing

 - WriteBufferNumber - description missing

 - WriteBufferSize - description missing

DiscoveryConfig
^^^^^^^^^^^^^^^

 - BitsPerHop - description missing

 - BootnodePongTimeout - description missing

 - Bootnodes - description missing

 - BucketsCount - description missing

 - BucketSize - description missing

 - Concurrency - description missing

 - DiscoveryInterval - description missing

 - DiscoveryNewCycleWaitTime - description missing

 - DiscoveryPersistenceInterval - description missing

 - EvictionCheckInterval - description missing

 - IsDiscoveryNodesPersistenceOn - description missing

 - MasterExternalIp - description missing

 - MasterHost - description missing

 - MasterPort - description missing

 - MaxDiscoveryRounds - description missing

 - MaxNodeLifecycleManagersCount - description missing

 - NodeLifecycleManagersCleanupCount - description missing

 - PingMessageVersion - description missing

 - PingRetryCount - description missing

 - PongTimeout - description missing

 - SendNodeTimeout - description missing

 - UdpChannelCloseTimeout - description missing

EthStatsConfig
^^^^^^^^^^^^^^

 - Contact - description missing

 - Enabled - description missing

 - Name - description missing

 - Secret - description missing

 - Server - description missing

HiveConfig
^^^^^^^^^^

 - BlocksDir - description missing

 - Bootnode - description missing

 - ChainFile - description missing

 - GenesisFilePath - description missing

 - HomesteadBlockNr - description missing

 - KeysDir - description missing

InitConfig
^^^^^^^^^^

 - BaseDbPath - description missing

 - ChainSpecFormat - description missing

 - ChainSpecPath - description missing

 - DiscoveryEnabled - If 'false' then the node does not try to find nodes beyond the bootnodes configured.

 - DiscoveryPort - description missing

 - EnableUnsecuredDevWallet - If 'true' then it enables thewallet / key store in the application.

 - GenesisHash - description missing

 - HttpHost - description missing

 - HttpPort - description missing

 - IsMining - description missing

 - JsonRpcEnabled - Defines whether the JSON RPC service is enabled on node startup at the 'HttpPort'

 - JsonRpcEnabledModules - Defines whether the JSON RPC service is enabled on node startup at the 'HttpPort'

 - KeepDevWalletInMemory - If 'true' then any accounts created will be only valid during the session and deleted when application closes.

 - LogDirectory - In case of null, the path is set to [applicationDirectiory]\logs

 - LogFileName - description missing

 - LogPerfStatsOnDebug - description missing

 - ObsoletePendingTransactionInterval - description missing

 - P2PPort - description missing

 - PeerManagerEnabled - description missing

 - PeerNotificationThreshold - description missing

 - ProcessingEnabled - If 'false' then the node does not download/process new blocks..

 - RemovePendingTransactionInterval - description missing

 - RemovingLogFilesEnabled - description missing

 - StaticNodesPath - description missing

 - StoreReceipts - description missing

 - StoreTraces - description missing

 - SynchronizationEnabled - If 'false' then the node does not download/process new blocks..

 - WebSocketsEnabled - Defines whether the WebSockets service is enabled on node startup at the 'HttpPort'

JsonRpcConfig
^^^^^^^^^^^^^

 - EnabledModules - description missing

KeyStoreConfig
^^^^^^^^^^^^^^

 - Cipher - description missing

 - IVSize - description missing

 - Kdf - description missing

 - KdfparamsDklen - description missing

 - KdfparamsN - description missing

 - KdfparamsP - description missing

 - KdfparamsR - description missing

 - KdfparamsSaltLen - description missing

 - KeyStoreDirectory - description missing

 - KeyStoreEncoding - description missing

 - SymmetricEncrypterBlockSize - description missing

 - SymmetricEncrypterKeySize - description missing

 - TestNodeKey - description missing

MetricsConfig
^^^^^^^^^^^^^

 - MetricsEnabled - description missing

 - MetricsIntervalSeconds - description missing

 - MetricsPushGatewayUrl - description missing

 - NodeName - description missing

NetworkConfig
^^^^^^^^^^^^^

 - ActivePeersMaxCount - description missing

 - CandidatePeerCountCleanupThreshold - description missing

 - DbBasePath - description missing

 - IsPeersPersistenceOn - description missing

 - MaxCandidatePeerCount - description missing

 - MaxPersistedPeerCount - description missing

 - P2PPingInterval - description missing

 - P2PPingRetryCount - description missing

 - PeersPersistenceInterval - description missing

 - PeersUpdateInterval - description missing

 - PersistedPeerCountCleanupThreshold - description missing

 - StaticPeers - description missing

 - TrustedPeers - description missing

SyncConfig
^^^^^^^^^^

 - DownloadBodiesInFastSync - description missing

 - DownloadReceiptsInFastSync - description missing

 - FastBlocks - description missing

 - FastSync - description missing

 - PivotHash - description missing

 - PivotNumber - description missing

 - PivotTotalDifficulty - description missing

Sample configuration (mainnet)
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

::

    [
      {
        "ConfigModule": "DbConfig"
        "ConfigItems": {
          "BlockCacheSize" : example
          "BlockInfosDbBlockCacheSize" : example
          "BlockInfosDbCacheIndexAndFilterBlocks" : example
          "BlockInfosDbWriteBufferNumber" : example
          "BlockInfosDbWriteBufferSize" : example
          "BlocksDbBlockCacheSize" : example
          "BlocksDbCacheIndexAndFilterBlocks" : example
          "BlocksDbWriteBufferNumber" : example
          "BlocksDbWriteBufferSize" : example
          "CacheIndexAndFilterBlocks" : example
          "CodeDbBlockCacheSize" : example
          "CodeDbCacheIndexAndFilterBlocks" : example
          "CodeDbWriteBufferNumber" : example
          "CodeDbWriteBufferSize" : example
          "HeadersDbBlockCacheSize" : example
          "HeadersDbCacheIndexAndFilterBlocks" : example
          "HeadersDbWriteBufferNumber" : example
          "HeadersDbWriteBufferSize" : example
          "PendingTxsDbBlockCacheSize" : example
          "PendingTxsDbCacheIndexAndFilterBlocks" : example
          "PendingTxsDbWriteBufferNumber" : example
          "PendingTxsDbWriteBufferSize" : example
          "ReceiptsDbBlockCacheSize" : example
          "ReceiptsDbCacheIndexAndFilterBlocks" : example
          "ReceiptsDbWriteBufferNumber" : example
          "ReceiptsDbWriteBufferSize" : example
          "TraceDbBlockCacheSize" : example
          "TraceDbCacheIndexAndFilterBlocks" : example
          "TraceDbWriteBufferNumber" : example
          "TraceDbWriteBufferSize" : example
          "WriteBufferNumber" : example
          "WriteBufferSize" : example
        }
      },
      {
        "ConfigModule": "DiscoveryConfig"
        "ConfigItems": {
          "BitsPerHop" : example
          "BootnodePongTimeout" : example
          "Bootnodes" : example
          "BucketsCount" : example
          "BucketSize" : example
          "Concurrency" : example
          "DiscoveryInterval" : example
          "DiscoveryNewCycleWaitTime" : example
          "DiscoveryPersistenceInterval" : example
          "EvictionCheckInterval" : example
          "IsDiscoveryNodesPersistenceOn" : example
          "MasterExternalIp" : example
          "MasterHost" : example
          "MasterPort" : example
          "MaxDiscoveryRounds" : example
          "MaxNodeLifecycleManagersCount" : example
          "NodeLifecycleManagersCleanupCount" : example
          "PingMessageVersion" : example
          "PingRetryCount" : example
          "PongTimeout" : example
          "SendNodeTimeout" : example
          "UdpChannelCloseTimeout" : example
        }
      },
      {
        "ConfigModule": "EthStatsConfig"
        "ConfigItems": {
          "Contact" : example
          "Enabled" : example
          "Name" : example
          "Secret" : example
          "Server" : example
        }
      },
      {
        "ConfigModule": "HiveConfig"
        "ConfigItems": {
          "BlocksDir" : example
          "Bootnode" : example
          "ChainFile" : example
          "GenesisFilePath" : example
          "HomesteadBlockNr" : example
          "KeysDir" : example
        }
      },
      {
        "ConfigModule": "InitConfig"
        "ConfigItems": {
          "BaseDbPath" : example
          "ChainSpecFormat" : example
          "ChainSpecPath" : example
          "DiscoveryEnabled" : example
          "DiscoveryPort" : example
          "EnableUnsecuredDevWallet" : example
          "GenesisHash" : example
          "HttpHost" : example
          "HttpPort" : example
          "IsMining" : example
          "JsonRpcEnabled" : example
          "JsonRpcEnabledModules" : example
          "KeepDevWalletInMemory" : example
          "LogDirectory" : example
          "LogFileName" : example
          "LogPerfStatsOnDebug" : example
          "ObsoletePendingTransactionInterval" : example
          "P2PPort" : example
          "PeerManagerEnabled" : example
          "PeerNotificationThreshold" : example
          "ProcessingEnabled" : example
          "RemovePendingTransactionInterval" : example
          "RemovingLogFilesEnabled" : example
          "StaticNodesPath" : example
          "StoreReceipts" : example
          "StoreTraces" : example
          "SynchronizationEnabled" : example
          "WebSocketsEnabled" : example
        }
      },
      {
        "ConfigModule": "JsonRpcConfig"
        "ConfigItems": {
          "EnabledModules" : example
        }
      },
      {
        "ConfigModule": "KeyStoreConfig"
        "ConfigItems": {
          "Cipher" : example
          "IVSize" : example
          "Kdf" : example
          "KdfparamsDklen" : example
          "KdfparamsN" : example
          "KdfparamsP" : example
          "KdfparamsR" : example
          "KdfparamsSaltLen" : example
          "KeyStoreDirectory" : example
          "KeyStoreEncoding" : example
          "SymmetricEncrypterBlockSize" : example
          "SymmetricEncrypterKeySize" : example
          "TestNodeKey" : example
        }
      },
      {
        "ConfigModule": "MetricsConfig"
        "ConfigItems": {
          "MetricsEnabled" : example
          "MetricsIntervalSeconds" : example
          "MetricsPushGatewayUrl" : example
          "NodeName" : example
        }
      },
      {
        "ConfigModule": "NetworkConfig"
        "ConfigItems": {
          "ActivePeersMaxCount" : example
          "CandidatePeerCountCleanupThreshold" : example
          "DbBasePath" : example
          "IsPeersPersistenceOn" : example
          "MaxCandidatePeerCount" : example
          "MaxPersistedPeerCount" : example
          "P2PPingInterval" : example
          "P2PPingRetryCount" : example
          "PeersPersistenceInterval" : example
          "PeersUpdateInterval" : example
          "PersistedPeerCountCleanupThreshold" : example
          "StaticPeers" : example
          "TrustedPeers" : example
        }
      },
      {
        "ConfigModule": "SyncConfig"
        "ConfigItems": {
          "DownloadBodiesInFastSync" : example
          "DownloadReceiptsInFastSync" : example
          "FastBlocks" : example
          "FastSync" : example
          "PivotHash" : example
          "PivotNumber" : example
          "PivotTotalDifficulty" : example
        }
      },
    ]
