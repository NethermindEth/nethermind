Configuration
*************

DbConfig
^^^^^^^^

 BlockCacheSize

 BlockInfosDbBlockCacheSize

 BlockInfosDbCacheIndexAndFilterBlocks

 BlockInfosDbWriteBufferNumber

 BlockInfosDbWriteBufferSize

 BlocksDbBlockCacheSize

 BlocksDbCacheIndexAndFilterBlocks

 BlocksDbWriteBufferNumber

 BlocksDbWriteBufferSize

 CacheIndexAndFilterBlocks

 CodeDbBlockCacheSize

 CodeDbCacheIndexAndFilterBlocks

 CodeDbWriteBufferNumber

 CodeDbWriteBufferSize

 HeadersDbBlockCacheSize

 HeadersDbCacheIndexAndFilterBlocks

 HeadersDbWriteBufferNumber

 HeadersDbWriteBufferSize

 PendingTxsDbBlockCacheSize

 PendingTxsDbCacheIndexAndFilterBlocks

 PendingTxsDbWriteBufferNumber

 PendingTxsDbWriteBufferSize

 ReceiptsDbBlockCacheSize

 ReceiptsDbCacheIndexAndFilterBlocks

 ReceiptsDbWriteBufferNumber

 ReceiptsDbWriteBufferSize

 TraceDbBlockCacheSize

 TraceDbCacheIndexAndFilterBlocks

 TraceDbWriteBufferNumber

 TraceDbWriteBufferSize

 WriteBufferNumber

 WriteBufferSize

DiscoveryConfig
^^^^^^^^^^^^^^^

 BitsPerHop

 BootnodePongTimeout

 Bootnodes

 BucketsCount

 BucketSize

 Concurrency

 DiscoveryInterval

 DiscoveryNewCycleWaitTime

 DiscoveryPersistenceInterval

 EvictionCheckInterval

 IsDiscoveryNodesPersistenceOn

 MasterExternalIp

 MasterHost

 MasterPort

 MaxDiscoveryRounds

 MaxNodeLifecycleManagersCount

 NodeLifecycleManagersCleanupCount

 PingMessageVersion

 PingRetryCount

 PongTimeout

 SendNodeTimeout

 UdpChannelCloseTimeout

EthStatsConfig
^^^^^^^^^^^^^^

 Contact

 Enabled

 Name

 Secret

 Server

HiveConfig
^^^^^^^^^^

 BlocksDir

 Bootnode

 ChainFile

 GenesisFilePath

 HomesteadBlockNr

 KeysDir

InitConfig
^^^^^^^^^^

 BaseDbPath

 ChainSpecFormat

 ChainSpecPath

 DiscoveryEnabled
   If 'false' then the node does not try to find nodes beyond the bootnodes configured.
   default value: true

 DiscoveryPort

 EnableUnsecuredDevWallet
   If 'true' then it enables thewallet / key store in the application.
   default value: false

 GenesisHash

 HttpHost

 HttpPort

 IsMining

 JsonRpcEnabled
   Defines whether the JSON RPC service is enabled on node startup at the 'HttpPort'
   default value: false

 JsonRpcEnabledModules
   Defines whether the JSON RPC service is enabled on node startup at the 'HttpPort'
   default value: "Clique,Db,Debug,Eth,Net,Trace,TxPool,Web3"

 KeepDevWalletInMemory
   If 'true' then any accounts created will be only valid during the session and deleted when application closes.
   default value: false

 LogDirectory
   In case of null, the path is set to [applicationDirectiory]\logs
   default value: null

 LogFileName

 ObsoletePendingTransactionInterval

 P2PPort

 PeerManagerEnabled

 PeerNotificationThreshold

 ProcessingEnabled
   If 'false' then the node does not download/process new blocks..
   default value: true

 RemovePendingTransactionInterval

 RemovingLogFilesEnabled

 StaticNodesPath
   
   default value: Data/static-nodes.json

 StoreReceipts

 StoreTraces

 SynchronizationEnabled
   If 'false' then the node does not download/process new blocks..
   default value: true

 WebSocketsEnabled
   Defines whether the WebSockets service is enabled on node startup at the 'HttpPort'
   default value: false

JsonRpcConfig
^^^^^^^^^^^^^

 EnabledModules

KeyStoreConfig
^^^^^^^^^^^^^^

 Cipher

 IVSize

 Kdf

 KdfparamsDklen

 KdfparamsN

 KdfparamsP

 KdfparamsR

 KdfparamsSaltLen

 KeyStoreDirectory

 KeyStoreEncoding

 SymmetricEncrypterBlockSize

 SymmetricEncrypterKeySize

 TestNodeKey

MetricsConfig
^^^^^^^^^^^^^

 MetricsEnabled

 MetricsIntervalSeconds

 MetricsPushGatewayUrl

 NodeName

NetworkConfig
^^^^^^^^^^^^^

 ActivePeersMaxCount

 CandidatePeerCountCleanupThreshold

 DbBasePath

 IsPeersPersistenceOn

 MaxCandidatePeerCount

 MaxPersistedPeerCount

 P2PPingInterval

 P2PPingRetryCount

 PeersPersistenceInterval

 PeersUpdateInterval

 PersistedPeerCountCleanupThreshold

 StaticPeers

 TrustedPeers

SyncConfig
^^^^^^^^^^

 DownloadBodiesInFastSync

 DownloadReceiptsInFastSync

 FastBlocks

 FastSync

 PivotHash

 PivotNumber

 PivotTotalDifficulty

Sample configuration (mainnet)
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

::

    [
      {
        "ConfigModule": "DbConfig"
        "ConfigItems": {
          "BlockCacheSize" : [MISSING_DOCS]
          "BlockInfosDbBlockCacheSize" : [MISSING_DOCS]
          "BlockInfosDbCacheIndexAndFilterBlocks" : [MISSING_DOCS]
          "BlockInfosDbWriteBufferNumber" : [MISSING_DOCS]
          "BlockInfosDbWriteBufferSize" : [MISSING_DOCS]
          "BlocksDbBlockCacheSize" : [MISSING_DOCS]
          "BlocksDbCacheIndexAndFilterBlocks" : [MISSING_DOCS]
          "BlocksDbWriteBufferNumber" : [MISSING_DOCS]
          "BlocksDbWriteBufferSize" : [MISSING_DOCS]
          "CacheIndexAndFilterBlocks" : [MISSING_DOCS]
          "CodeDbBlockCacheSize" : [MISSING_DOCS]
          "CodeDbCacheIndexAndFilterBlocks" : [MISSING_DOCS]
          "CodeDbWriteBufferNumber" : [MISSING_DOCS]
          "CodeDbWriteBufferSize" : [MISSING_DOCS]
          "HeadersDbBlockCacheSize" : [MISSING_DOCS]
          "HeadersDbCacheIndexAndFilterBlocks" : [MISSING_DOCS]
          "HeadersDbWriteBufferNumber" : [MISSING_DOCS]
          "HeadersDbWriteBufferSize" : [MISSING_DOCS]
          "PendingTxsDbBlockCacheSize" : [MISSING_DOCS]
          "PendingTxsDbCacheIndexAndFilterBlocks" : [MISSING_DOCS]
          "PendingTxsDbWriteBufferNumber" : [MISSING_DOCS]
          "PendingTxsDbWriteBufferSize" : [MISSING_DOCS]
          "ReceiptsDbBlockCacheSize" : [MISSING_DOCS]
          "ReceiptsDbCacheIndexAndFilterBlocks" : [MISSING_DOCS]
          "ReceiptsDbWriteBufferNumber" : [MISSING_DOCS]
          "ReceiptsDbWriteBufferSize" : [MISSING_DOCS]
          "TraceDbBlockCacheSize" : [MISSING_DOCS]
          "TraceDbCacheIndexAndFilterBlocks" : [MISSING_DOCS]
          "TraceDbWriteBufferNumber" : [MISSING_DOCS]
          "TraceDbWriteBufferSize" : [MISSING_DOCS]
          "WriteBufferNumber" : [MISSING_DOCS]
          "WriteBufferSize" : [MISSING_DOCS]
        }
      },
      {
        "ConfigModule": "DiscoveryConfig"
        "ConfigItems": {
          "BitsPerHop" : [MISSING_DOCS]
          "BootnodePongTimeout" : [MISSING_DOCS]
          "Bootnodes" : [MISSING_DOCS]
          "BucketsCount" : [MISSING_DOCS]
          "BucketSize" : [MISSING_DOCS]
          "Concurrency" : [MISSING_DOCS]
          "DiscoveryInterval" : [MISSING_DOCS]
          "DiscoveryNewCycleWaitTime" : [MISSING_DOCS]
          "DiscoveryPersistenceInterval" : [MISSING_DOCS]
          "EvictionCheckInterval" : [MISSING_DOCS]
          "IsDiscoveryNodesPersistenceOn" : [MISSING_DOCS]
          "MasterExternalIp" : [MISSING_DOCS]
          "MasterHost" : [MISSING_DOCS]
          "MasterPort" : [MISSING_DOCS]
          "MaxDiscoveryRounds" : [MISSING_DOCS]
          "MaxNodeLifecycleManagersCount" : [MISSING_DOCS]
          "NodeLifecycleManagersCleanupCount" : [MISSING_DOCS]
          "PingMessageVersion" : [MISSING_DOCS]
          "PingRetryCount" : [MISSING_DOCS]
          "PongTimeout" : [MISSING_DOCS]
          "SendNodeTimeout" : [MISSING_DOCS]
          "UdpChannelCloseTimeout" : [MISSING_DOCS]
        }
      },
      {
        "ConfigModule": "EthStatsConfig"
        "ConfigItems": {
          "Contact" : [MISSING_DOCS]
          "Enabled" : [MISSING_DOCS]
          "Name" : [MISSING_DOCS]
          "Secret" : [MISSING_DOCS]
          "Server" : [MISSING_DOCS]
        }
      },
      {
        "ConfigModule": "HiveConfig"
        "ConfigItems": {
          "BlocksDir" : [MISSING_DOCS]
          "Bootnode" : [MISSING_DOCS]
          "ChainFile" : [MISSING_DOCS]
          "GenesisFilePath" : [MISSING_DOCS]
          "HomesteadBlockNr" : [MISSING_DOCS]
          "KeysDir" : [MISSING_DOCS]
        }
      },
      {
        "ConfigModule": "InitConfig"
        "ConfigItems": {
          "BaseDbPath" : [MISSING_DOCS]
          "ChainSpecFormat" : [MISSING_DOCS]
          "ChainSpecPath" : [MISSING_DOCS]
          "DiscoveryEnabled" : true
          "DiscoveryPort" : [MISSING_DOCS]
          "EnableUnsecuredDevWallet" : false
          "GenesisHash" : [MISSING_DOCS]
          "HttpHost" : [MISSING_DOCS]
          "HttpPort" : [MISSING_DOCS]
          "IsMining" : [MISSING_DOCS]
          "JsonRpcEnabled" : false
          "JsonRpcEnabledModules" : "Clique,Db,Debug,Eth,Net,Trace,TxPool,Web3"
          "KeepDevWalletInMemory" : false
          "LogDirectory" : null
          "LogFileName" : [MISSING_DOCS]
          "ObsoletePendingTransactionInterval" : [MISSING_DOCS]
          "P2PPort" : [MISSING_DOCS]
          "PeerManagerEnabled" : [MISSING_DOCS]
          "PeerNotificationThreshold" : [MISSING_DOCS]
          "ProcessingEnabled" : true
          "RemovePendingTransactionInterval" : [MISSING_DOCS]
          "RemovingLogFilesEnabled" : [MISSING_DOCS]
          "StaticNodesPath" : Data/static-nodes.json
          "StoreReceipts" : [MISSING_DOCS]
          "StoreTraces" : [MISSING_DOCS]
          "SynchronizationEnabled" : true
          "WebSocketsEnabled" : false
        }
      },
      {
        "ConfigModule": "JsonRpcConfig"
        "ConfigItems": {
          "EnabledModules" : [MISSING_DOCS]
        }
      },
      {
        "ConfigModule": "KeyStoreConfig"
        "ConfigItems": {
          "Cipher" : [MISSING_DOCS]
          "IVSize" : [MISSING_DOCS]
          "Kdf" : [MISSING_DOCS]
          "KdfparamsDklen" : [MISSING_DOCS]
          "KdfparamsN" : [MISSING_DOCS]
          "KdfparamsP" : [MISSING_DOCS]
          "KdfparamsR" : [MISSING_DOCS]
          "KdfparamsSaltLen" : [MISSING_DOCS]
          "KeyStoreDirectory" : [MISSING_DOCS]
          "KeyStoreEncoding" : [MISSING_DOCS]
          "SymmetricEncrypterBlockSize" : [MISSING_DOCS]
          "SymmetricEncrypterKeySize" : [MISSING_DOCS]
          "TestNodeKey" : [MISSING_DOCS]
        }
      },
      {
        "ConfigModule": "MetricsConfig"
        "ConfigItems": {
          "MetricsEnabled" : [MISSING_DOCS]
          "MetricsIntervalSeconds" : [MISSING_DOCS]
          "MetricsPushGatewayUrl" : [MISSING_DOCS]
          "NodeName" : [MISSING_DOCS]
        }
      },
      {
        "ConfigModule": "NetworkConfig"
        "ConfigItems": {
          "ActivePeersMaxCount" : [MISSING_DOCS]
          "CandidatePeerCountCleanupThreshold" : [MISSING_DOCS]
          "DbBasePath" : [MISSING_DOCS]
          "IsPeersPersistenceOn" : [MISSING_DOCS]
          "MaxCandidatePeerCount" : [MISSING_DOCS]
          "MaxPersistedPeerCount" : [MISSING_DOCS]
          "P2PPingInterval" : [MISSING_DOCS]
          "P2PPingRetryCount" : [MISSING_DOCS]
          "PeersPersistenceInterval" : [MISSING_DOCS]
          "PeersUpdateInterval" : [MISSING_DOCS]
          "PersistedPeerCountCleanupThreshold" : [MISSING_DOCS]
          "StaticPeers" : [MISSING_DOCS]
          "TrustedPeers" : [MISSING_DOCS]
        }
      },
      {
        "ConfigModule": "SyncConfig"
        "ConfigItems": {
          "DownloadBodiesInFastSync" : [MISSING_DOCS]
          "DownloadReceiptsInFastSync" : [MISSING_DOCS]
          "FastBlocks" : [MISSING_DOCS]
          "FastSync" : [MISSING_DOCS]
          "PivotHash" : [MISSING_DOCS]
          "PivotNumber" : [MISSING_DOCS]
          "PivotTotalDifficulty" : [MISSING_DOCS]
        }
      },
    ]
