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

NetworkConfig
^^^^^^^^^^^^^

 - ActivePeersMaxCount - description missing

 - BitsPerHop - description missing

 - BootnodePongTimeout - description missing

 - Bootnodes - description missing

 - BucketsCount - description missing

 - BucketSize - description missing

 - CandidatePeerCountCleanupThreshold - description missing

 - Concurrency - description missing

 - DbBasePath - description missing

 - DisconnectDelay - description missing

 - DiscoveryInterval - description missing

 - DiscoveryNewCycleWaitTime - description missing

 - DiscoveryPersistenceInterval - description missing

 - EvictionCheckInterval - description missing

 - FailedConnectionDelay - description missing

 - IsDiscoveryNodesPersistenceOn - description missing

 - IsPeersPersistenceOn - description missing

 - KeyPass - description missing

 - MasterExternalIp - description missing

 - MasterHost - description missing

 - MasterPort - description missing

 - MaxCandidatePeerCount - description missing

 - MaxDiscoveryRounds - description missing

 - MaxNodeLifecycleManagersCount - description missing

 - MaxPersistedPeerCount - description missing

 - NodeLifecycleManagersCleanupCount - description missing

 - P2PPingInterval - description missing

 - P2PPingRetryCount - description missing

 - PeersPersistenceInterval - description missing

 - PeersUpdateInterval - description missing

 - PersistedPeerCountCleanupThreshold - description missing

 - PingMessageVersion - description missing

 - PingRetryCount - description missing

 - PongTimeout - description missing

 - SendNodeTimeout - description missing

 - StaticPeers - description missing

 - TrustedPeers - description missing

 - UdpChannelCloseTimeout - description missing

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
        "ConfigModule": "NetworkConfig"
        "ConfigItems": {
          "ActivePeersMaxCount" : example
          "BitsPerHop" : example
          "BootnodePongTimeout" : example
          "Bootnodes" : example
          "BucketsCount" : example
          "BucketSize" : example
          "CandidatePeerCountCleanupThreshold" : example
          "Concurrency" : example
          "DbBasePath" : example
          "DisconnectDelay" : example
          "DiscoveryInterval" : example
          "DiscoveryNewCycleWaitTime" : example
          "DiscoveryPersistenceInterval" : example
          "EvictionCheckInterval" : example
          "FailedConnectionDelay" : example
          "IsDiscoveryNodesPersistenceOn" : example
          "IsPeersPersistenceOn" : example
          "KeyPass" : example
          "MasterExternalIp" : example
          "MasterHost" : example
          "MasterPort" : example
          "MaxCandidatePeerCount" : example
          "MaxDiscoveryRounds" : example
          "MaxNodeLifecycleManagersCount" : example
          "MaxPersistedPeerCount" : example
          "NodeLifecycleManagersCleanupCount" : example
          "P2PPingInterval" : example
          "P2PPingRetryCount" : example
          "PeersPersistenceInterval" : example
          "PeersUpdateInterval" : example
          "PersistedPeerCountCleanupThreshold" : example
          "PingMessageVersion" : example
          "PingRetryCount" : example
          "PongTimeout" : example
          "SendNodeTimeout" : example
          "StaticPeers" : example
          "TrustedPeers" : example
          "UdpChannelCloseTimeout" : example
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
