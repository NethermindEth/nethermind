JSON RPC
********

Standard APIs
=============

Nethermind supports most of the current JSON RPC endpoints supported by geth. For data related endpoints formats of the data should be exectly the same between Geth and Nethermind. Specification of Ethereum JSON RPC can be found `here <https://github.com/ethereum/wiki/wiki/JSON-RPC>`_

web3
^^^^

========= ==================================== ===========
Module    Method                               Supported
========= ==================================== ===========
web3      clientVersion                        true
web3      sha3                                 true
========= ==================================== ===========

net
^^^

========= ==================================== ===========
Module    Method                               Supported
========= ==================================== ===========
net       version                              true
net       listening                            false
net       peerCount                            true
========= ==================================== ===========

eth
^^^

========= ==================================== ===========
Module    Method                               Supported
========= ==================================== ===========
eth       protocolVersion                      false
eth       syncing                              true
eth       coinbase                             false
eth       mining                               true
eth       hashrate                             false
eth       gasPrice                             false
eth       accounts                             true
eth       blockNumber                          true
eth       getBalance                           true
eth       getStorageAt                         true
eth       getTransactionCount                  true
eth       getBlockTransactionCountByHash       true
eth       getBlockTransactionCountByNumber     true
eth       getUncleCountByBlockNumber           true
eth       getCode                              true
eth       sign                                 true (dev)
eth       sendTransaction                      true (pr)
eth       sendRawTransaction                   false
eth       call                                 true
eth       estimateGas                          false
eth       getBlockByHash                       true
eth       getBlockByNumber                     true
eth       getTransactionByHash                 true
eth       getTransactionByBlockHashAndIndex    true
eth       getTransactionByBlockNumberAndIndex  true
eth       getTransactionReceipt                true
eth       getUncleByBlockHashAndIndex          true
eth       getUncleByBlockNumberAndIndex        true
eth       newFilter                            true
eth       newBlockFilter                       true
eth       newPendingTransactionFilter          true
eth       uninstallFilter                      true
eth       getFilterChanges                     true
eth       getFilterLogs                        true
eth       getLogs                              true
eth       getWork                              false
eth       submitWork                           false
eth       submitHashrate                       false
eth       getProof                             false
========= ==================================== ===========

db
^^

========= ==================================== ===========
Module    Method                               Supported
========= ==================================== ===========
db        putString                            false
db        getString                            false
db        putHex                               false
db        getHex                               false
========= ==================================== ===========

shh
^^^

========= ==================================== ===========
Module    Method                               Supported
========= ==================================== ===========
shh       \*                                   false
========= ==================================== ===========

Management APIs
===============

These are APIs defined by Geth `here <https://github.com/ethereum/go-ethereum/wiki/Management-APIs>`_

admin
^^^^^

========= ==================================== ===========
Module    Method                               Supported
========= ==================================== ===========
admin     addPeer                              false
admin     dataDir                              true
admin     nodeInfo                             false
admin     peers                                false
admin     setSolc                              false
admin     startRPC                             false
admin     startWS                              false
admin     stopRPC                              false
admin     stopWS                               false
========= ==================================== ===========

debug
^^^^^

========= ==================================== ===========
Module    Method                               Supported
========= ==================================== ===========
debug     traceBlockByNumber                   true
debug     traceBlockByHash                     true
debug     traceBlockFromFile                   false
debug     traceTransaction                     true
========= ==================================== ===========

miner
^^^^^

========= ==================================== ===========
Module    Method                               Supported
========= ==================================== ===========
miner     \*                                   false
========= ==================================== ===========

personal
^^^^^^^^

========= ================================= ===========
Module    Method                            Supported
========= ================================= ===========
personal  \*                                false
========= ================================= ===========

txpool
^^^^^^

========= ==================================== ===========
Module    Method                               Supported
========= ==================================== ===========
txpool    content                              true
txpool    inspect                              true
txpool    status                               true
========= ==================================== ===========
