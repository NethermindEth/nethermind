Configuration
*************

Use '/' as the path separator so the configs can be shared between all platforms supported (Linux, Windows, MacOS).
'--config', '--baseDbPath', and '--log' options are available from the command line to select config file, base DB directory prefix and log level respectively. 

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

 ConfigsDbBlockCacheSize

 ConfigsDbCacheIndexAndFilterBlocks

 ConfigsDbWriteBufferNumber

 ConfigsDbWriteBufferSize

 ConsumerDepositApprovalsDbBlockCacheSize

 ConsumerDepositApprovalsDbCacheIndexAndFilterBlocks

 ConsumerDepositApprovalsDbWriteBufferNumber

 ConsumerDepositApprovalsDbWriteBufferSize

 ConsumerReceiptsDbBlockCacheSize

 ConsumerReceiptsDbCacheIndexAndFilterBlocks

 ConsumerReceiptsDbWriteBufferNumber

 ConsumerReceiptsDbWriteBufferSize

 ConsumerSessionsDbBlockCacheSize

 ConsumerSessionsDbCacheIndexAndFilterBlocks

 ConsumerSessionsDbWriteBufferNumber

 ConsumerSessionsDbWriteBufferSize

 DepositsDbBlockCacheSize

 DepositsDbCacheIndexAndFilterBlocks

 DepositsDbWriteBufferNumber

 DepositsDbWriteBufferSize

 EthRequestsDbBlockCacheSize

 EthRequestsDbCacheIndexAndFilterBlocks

 EthRequestsDbWriteBufferNumber

 EthRequestsDbWriteBufferSize

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

 RecycleLogFileNum

 TraceDbBlockCacheSize

 TraceDbCacheIndexAndFilterBlocks

 TraceDbWriteBufferNumber

 TraceDbWriteBufferSize

 WriteAheadLogSync

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
   Node owner contact details displayed on the ethstats page.
   default value: null

 Enabled
   If 'true' then EthStats publishing gets enabled.
   default value: false

 Name
   Node name displayed on the given ethstats server.
   default value: null

 Secret
   Password for publishing to a given ethstats server.
   default value: null

 Server
   EthStats server wss://hostname:port/api/
   default value: null

HiveConfig
^^^^^^^^^^

These items need only be set when testing with Hive (Ethereum Foundation tool)

 BlocksDir
   Path to a directory with additional blocks.
   default value: "/blocks"

 ChainFile
   Path to a file with a test chain definition.
   default value: "/chain.rlp"

 KeysDir
   Path to a test key store directory.
   default value: "/keys"

InitConfig
^^^^^^^^^^

 BaseDbPath
   Base directoy path for all the nethermind databases.
   default value: "db"

 ChainSpecFormat
   Format of the chain definition file - genesis (Geth style - not tested recently / may fail) or chainspec (Parity style).
   default value: "chainspec"

 ChainSpecPath
   Path to the chain definition file (Parity chainspec or Geth genesis file).
   default value: null

 DiscoveryEnabled
   If 'false' then the node does not try to find nodes beyond the bootnodes configured.
   default value: true

 EnableRc7Fix

 EnableUnsecuredDevWallet
   If 'true' then it enables the wallet / key store in the application.
   default value: false

 GenesisHash
   Hash of the genesis block - if the default null value is left then the genesis block validity will not be checked which is useful for ad hoc test/private networks.
   default value: null

 IsMining
   If 'true' then the node will try to seal/mine new blocks
   default value: false

 KeepDevWalletInMemory
   If 'true' then any accounts created will be only valid during the session and deleted when application closes.
   default value: false

 LogDirectory
   In case of null, the path is set to [applicationDirectiory]\logs
   default value: null

 LogFileName
   Name of the log file generated (useful when launching multiple networks with the same log folder).
   default value: "log.txt"

 PeerManagerEnabled
   If 'false' then the node does not connect to newly discovered peers..
   default value: true

 ProcessingEnabled
   If 'false' then the node does not download/process new blocks..
   default value: true

 StaticNodesPath
   Path to the file with a list of static nodes.
   default value: "Data/static-nodes.json"

 StoreReceipts
   If set to 'false' then transaction receipts will not be stored in the database.
   default value: true

 StoreTraces
   If set to 'true' then the detailed VM trace data will be stored in teh DB (huge data sets).
   default value: false

 SynchronizationEnabled
   If 'false' then the node does not download/process new blocks..
   default value: true

 WebSocketsEnabled
   Defines whether the WebSockets service is enabled on node startup at the 'HttpPort'
   default value: false

JsonRpcConfig
^^^^^^^^^^^^^

 Enabled
   Defines whether the JSON RPC service is enabled on node startuo. Configure host nad port if default values do not work for you.
   default value: false

 EnabledModules
   Defines which RPC modules should be enabled.
   default value: all

 Host
   Host for JSON RPC calls. Ensure the firewall is configured when enabling JSON RPC.
   default value: "127.0.0.1"

 Port
   Port number for JSON RPC calls. Ensure the firewall is configured when enabling JSON RPC.
   default value: 8545

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

Configuration of the Prometheus + Grafana metrics publication. Documentation of the required setup is not yet ready (but the metrics do work and are used by the dev team)

 Enabled
   If 'true' then the node publishes various metrics to Prometheus at the given interval.
   default value: false

 IntervalSeconds
   
   default value: 5

 NodeName
   Name displayed in the Grafana dashboard
   default value: "Nethermind"

 PushGatewayUrl
   Prometheus URL.
   default value: "http://localhost:9091/metrics"

NetworkConfig
^^^^^^^^^^^^^

 ActivePeersMaxCount
   Max number of connected peers.
   default value: 25

 CandidatePeerCountCleanupThreshold
   
   default value: 11000

 DiscoveryPort
   UDP port number for incoming discovery connections.
   default value: 30303

 ExternalIp
   Use only if your node cannot resolve external IP automatically.
   default value: null

 IsPeersPersistenceOn
   If 'false' then discovered node list will be cleared on each restart.
   default value: true

 LocalIp
   Use only if your node cannot resolve local IP automatically.
   default value: null

 MaxCandidatePeerCount
   
   default value: 10000

 MaxPersistedPeerCount
   
   default value: 2000

 P2PPingInterval
   
   default value: 10000

 P2PPort
   TPC/IP port number for incoming P2P connections.
   default value: 30303

 PeersPersistenceInterval
   
   default value: 5000

 PeersUpdateInterval
   
   default value: 100

 PersistedPeerCountCleanupThreshold
   
   default value: 2200

 StaticPeers
   List of nodes for which we will keep the connection on. Static nodes are not counted to the max number of nodes limit.
   default value: null

 TrustedPeers
   Currently ignored.
   default value: null

SyncConfig
^^^^^^^^^^

 DownloadBodiesInFastSync
   If set to 'true' then the block bodies will be downloaded in the Fast Sync mode.
   default value: true

 DownloadReceiptsInFastSync
   If set to 'true' then the receipts will be downloaded in the Fast Sync mode.
   default value: true

 FastBlocks
   If set to 'true' then in the Fast Sync mode blocks will be first downloaded from the provided PivotNumber downwards.
   default value: false

 FastSync
   If set to 'true' then the Fast Sync (eth/63) synchronization algorithm will be used.
   default value: false

 PivotHash
   Hash of the pivot block for the Fast Blocks sync.
   default value: null

 PivotNumber
   Number of the pivot block for the Fast Blocks sync.
   default value: null

 PivotTotalDifficulty
   Total Difficulty of the pivot block for the Fast Blocks sync.
   default value: null

TxPoolConfig
^^^^^^^^^^^^

 ObsoletePendingTransactionInterval
   
   default value: 15

 PeerNotificationThreshold
   
   default value: 5

 RemovePendingTransactionInterval
   
   default value: 600

Sample configuration (mainnet)
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

::

    {
        "Db": {
              "BlockCacheSize" : [MISSING_DOCS],
              "BlockInfosDbBlockCacheSize" : [MISSING_DOCS],
              "BlockInfosDbCacheIndexAndFilterBlocks" : [MISSING_DOCS],
              "BlockInfosDbWriteBufferNumber" : [MISSING_DOCS],
              "BlockInfosDbWriteBufferSize" : [MISSING_DOCS],
              "BlocksDbBlockCacheSize" : [MISSING_DOCS],
              "BlocksDbCacheIndexAndFilterBlocks" : [MISSING_DOCS],
              "BlocksDbWriteBufferNumber" : [MISSING_DOCS],
              "BlocksDbWriteBufferSize" : [MISSING_DOCS],
              "CacheIndexAndFilterBlocks" : [MISSING_DOCS],
              "CodeDbBlockCacheSize" : [MISSING_DOCS],
              "CodeDbCacheIndexAndFilterBlocks" : [MISSING_DOCS],
              "CodeDbWriteBufferNumber" : [MISSING_DOCS],
              "CodeDbWriteBufferSize" : [MISSING_DOCS],
              "ConfigsDbBlockCacheSize" : [MISSING_DOCS],
              "ConfigsDbCacheIndexAndFilterBlocks" : [MISSING_DOCS],
              "ConfigsDbWriteBufferNumber" : [MISSING_DOCS],
              "ConfigsDbWriteBufferSize" : [MISSING_DOCS],
              "ConsumerDepositApprovalsDbBlockCacheSize" : [MISSING_DOCS],
              "ConsumerDepositApprovalsDbCacheIndexAndFilterBlocks" : [MISSING_DOCS],
              "ConsumerDepositApprovalsDbWriteBufferNumber" : [MISSING_DOCS],
              "ConsumerDepositApprovalsDbWriteBufferSize" : [MISSING_DOCS],
              "ConsumerReceiptsDbBlockCacheSize" : [MISSING_DOCS],
              "ConsumerReceiptsDbCacheIndexAndFilterBlocks" : [MISSING_DOCS],
              "ConsumerReceiptsDbWriteBufferNumber" : [MISSING_DOCS],
              "ConsumerReceiptsDbWriteBufferSize" : [MISSING_DOCS],
              "ConsumerSessionsDbBlockCacheSize" : [MISSING_DOCS],
              "ConsumerSessionsDbCacheIndexAndFilterBlocks" : [MISSING_DOCS],
              "ConsumerSessionsDbWriteBufferNumber" : [MISSING_DOCS],
              "ConsumerSessionsDbWriteBufferSize" : [MISSING_DOCS],
              "DepositsDbBlockCacheSize" : [MISSING_DOCS],
              "DepositsDbCacheIndexAndFilterBlocks" : [MISSING_DOCS],
              "DepositsDbWriteBufferNumber" : [MISSING_DOCS],
              "DepositsDbWriteBufferSize" : [MISSING_DOCS],
              "EthRequestsDbBlockCacheSize" : [MISSING_DOCS],
              "EthRequestsDbCacheIndexAndFilterBlocks" : [MISSING_DOCS],
              "EthRequestsDbWriteBufferNumber" : [MISSING_DOCS],
              "EthRequestsDbWriteBufferSize" : [MISSING_DOCS],
              "HeadersDbBlockCacheSize" : [MISSING_DOCS],
              "HeadersDbCacheIndexAndFilterBlocks" : [MISSING_DOCS],
              "HeadersDbWriteBufferNumber" : [MISSING_DOCS],
              "HeadersDbWriteBufferSize" : [MISSING_DOCS],
              "PendingTxsDbBlockCacheSize" : [MISSING_DOCS],
              "PendingTxsDbCacheIndexAndFilterBlocks" : [MISSING_DOCS],
              "PendingTxsDbWriteBufferNumber" : [MISSING_DOCS],
              "PendingTxsDbWriteBufferSize" : [MISSING_DOCS],
              "ReceiptsDbBlockCacheSize" : [MISSING_DOCS],
              "ReceiptsDbCacheIndexAndFilterBlocks" : [MISSING_DOCS],
              "ReceiptsDbWriteBufferNumber" : [MISSING_DOCS],
              "ReceiptsDbWriteBufferSize" : [MISSING_DOCS],
              "RecycleLogFileNum" : [MISSING_DOCS],
              "TraceDbBlockCacheSize" : [MISSING_DOCS],
              "TraceDbCacheIndexAndFilterBlocks" : [MISSING_DOCS],
              "TraceDbWriteBufferNumber" : [MISSING_DOCS],
              "TraceDbWriteBufferSize" : [MISSING_DOCS],
              "WriteAheadLogSync" : [MISSING_DOCS],
              "WriteBufferNumber" : [MISSING_DOCS],
              "WriteBufferSize" : [MISSING_DOCS]
        },
        "Discovery": {
              "BitsPerHop" : [MISSING_DOCS],
              "BootnodePongTimeout" : [MISSING_DOCS],
              "Bootnodes" : [MISSING_DOCS],
              "BucketsCount" : [MISSING_DOCS],
              "BucketSize" : [MISSING_DOCS],
              "Concurrency" : [MISSING_DOCS],
              "DiscoveryInterval" : [MISSING_DOCS],
              "DiscoveryNewCycleWaitTime" : [MISSING_DOCS],
              "DiscoveryPersistenceInterval" : [MISSING_DOCS],
              "EvictionCheckInterval" : [MISSING_DOCS],
              "IsDiscoveryNodesPersistenceOn" : [MISSING_DOCS],
              "MaxDiscoveryRounds" : [MISSING_DOCS],
              "MaxNodeLifecycleManagersCount" : [MISSING_DOCS],
              "NodeLifecycleManagersCleanupCount" : [MISSING_DOCS],
              "PingMessageVersion" : [MISSING_DOCS],
              "PingRetryCount" : [MISSING_DOCS],
              "PongTimeout" : [MISSING_DOCS],
              "SendNodeTimeout" : [MISSING_DOCS],
              "UdpChannelCloseTimeout" : [MISSING_DOCS]
        },
        "EthStats": {
              "Contact" : null,
              "Enabled" : false,
              "Name" : null,
              "Secret" : null,
              "Server" : null
        },
        "Hive": {
              "BlocksDir" : "/blocks",
              "ChainFile" : "/chain.rlp",
              "KeysDir" : "/keys"
        },
        "Init": {
              "BaseDbPath" : "db",
              "ChainSpecFormat" : "chainspec",
              "ChainSpecPath" : null,
              "DiscoveryEnabled" : true,
              "EnableRc7Fix" : [MISSING_DOCS],
              "EnableUnsecuredDevWallet" : false,
              "GenesisHash" : null,
              "IsMining" : false,
              "KeepDevWalletInMemory" : false,
              "LogDirectory" : null,
              "LogFileName" : "log.txt",
              "PeerManagerEnabled" : true,
              "ProcessingEnabled" : true,
              "StaticNodesPath" : "Data/static-nodes.json",
              "StoreReceipts" : true,
              "StoreTraces" : false,
              "SynchronizationEnabled" : true,
              "WebSocketsEnabled" : false
        },
        "JsonRpc": {
              "Enabled" : false,
              "EnabledModules" : all,
              "Host" : "127.0.0.1",
              "Port" : 8545
        },
        "KeyStore": {
              "Cipher" : [MISSING_DOCS],
              "IVSize" : [MISSING_DOCS],
              "Kdf" : [MISSING_DOCS],
              "KdfparamsDklen" : [MISSING_DOCS],
              "KdfparamsN" : [MISSING_DOCS],
              "KdfparamsP" : [MISSING_DOCS],
              "KdfparamsR" : [MISSING_DOCS],
              "KdfparamsSaltLen" : [MISSING_DOCS],
              "KeyStoreDirectory" : [MISSING_DOCS],
              "KeyStoreEncoding" : [MISSING_DOCS],
              "SymmetricEncrypterBlockSize" : [MISSING_DOCS],
              "SymmetricEncrypterKeySize" : [MISSING_DOCS],
              "TestNodeKey" : [MISSING_DOCS]
        },
        "Metrics": {
              "Enabled" : false,
              "IntervalSeconds" : 5,
              "NodeName" : "Nethermind",
              "PushGatewayUrl" : "http://localhost:9091/metrics"
        },
        "Network": {
              "ActivePeersMaxCount" : 25,
              "CandidatePeerCountCleanupThreshold" : 11000,
              "DiscoveryPort" : 30303,
              "ExternalIp" : null,
              "IsPeersPersistenceOn" : true,
              "LocalIp" : null,
              "MaxCandidatePeerCount" : 10000,
              "MaxPersistedPeerCount" : 2000,
              "P2PPingInterval" : 10000,
              "P2PPort" : 30303,
              "PeersPersistenceInterval" : 5000,
              "PeersUpdateInterval" : 100,
              "PersistedPeerCountCleanupThreshold" : 2200,
              "StaticPeers" : null,
              "TrustedPeers" : null
        },
        "Sync": {
              "DownloadBodiesInFastSync" : true,
              "DownloadReceiptsInFastSync" : true,
              "FastBlocks" : false,
              "FastSync" : false,
              "PivotHash" : null,
              "PivotNumber" : null,
              "PivotTotalDifficulty" : null
        },
        "TxPool": {
              "ObsoletePendingTransactionInterval" : 15,
              "PeerNotificationThreshold" : 5,
              "RemovePendingTransactionInterval" : 600
        },
    }
