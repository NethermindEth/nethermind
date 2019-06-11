JSON RPC
********

admin
^^^^^

 - admin_addPeer.(enode)

 - admin_dataDir.()

 - admin_nodeInfo.()

 - admin_peers.()

 - admin_removePeer.(enode)

 - admin_setSolc.()

clique
^^^^^^

 - clique_discard.(signer)

 - clique_getSigners.()

 - clique_getSignersAnnotated.()

 - clique_getSignersAtHash.(hash)

 - clique_getSignersAtHashAnnotated.(hash)

 - clique_getSignersAtNumber.(number)

 - clique_getSnapshot.()

 - clique_getSnapshotAtHash.(hash)

 - clique_propose.(signer, vote)

debug
^^^^^

 - debug_dumpBlock.(blockParameter)

 - debug_gcStats.()

 - debug_getBlockRlp.(blockParameter)

 - debug_getConfigValue.(category, name)

 - debug_getFromDb.(dbName, key)

 - debug_memStats.(blockParameter)

 - debug_seedHash.(blockParameter)

 - debug_setHead.(blockParameter)

 - debug_traceBlock.(blockRlp)

 - debug_traceBlockByHash.(blockHash)

 - debug_traceBlockByNumber.(number)

 - debug_traceBlockFromFile.(fileName)

 - debug_traceTransaction.(transactionHash)

 - debug_traceTransactionByBlockAndIndex.(blockParameter, txIndex)

 - debug_traceTransactionByBlockhashAndIndex.(blockHash, txIndex)

eth
^^^

 - eth_accounts.()

 - eth_blockNumber.()

 - eth_call.(transactionCall, blockParameter)

 - eth_coinbase.()

 - eth_estimateGas.(transactionCall)

 - eth_gasPrice.()

 - eth_getBalance.(address, blockParameter)

 - eth_getBlockByHash.(blockHash, returnFullTransactionObjects)

 - eth_getBlockByNumber.(blockParameter, returnFullTransactionObjects)

 - eth_getBlockTransactionCountByHash.(blockHash)

 - eth_getBlockTransactionCountByNumber.(blockParameter)

 - eth_getCode.(address, blockParameter)

 - eth_getFilterChanges.(filterId)

 - eth_getFilterLogs.(filterId)

 - eth_getLogs.(filter)

 - eth_getStorageAt.(address, positionIndex, blockParameter)

 - eth_getTransactionByBlockHashAndIndex.(blockHash, positionIndex)

 - eth_getTransactionByBlockNumberAndIndex.(blockParameter, positionIndex)

 - eth_getTransactionByHash.(transactionHash)

 - eth_getTransactionCount.(address, blockParameter)

 - eth_getTransactionReceipt.(txHashData)

 - eth_getUncleByBlockHashAndIndex.(blockHashData, positionIndex)

 - eth_getUncleByBlockNumberAndIndex.(blockParameter, positionIndex)

 - eth_getUncleCountByBlockHash.(blockHash)

 - eth_getUncleCountByBlockNumber.(blockParameter)

 - eth_getWork.()

 - eth_hashrate.()

 - eth_mining.()

 - eth_newBlockFilter.()

 - eth_newFilter.(filter)

 - eth_newPendingTransactionFilter.()

 - eth_protocolVersion.()

 - eth_sendRawTransaction.(transaction)

 - eth_sendTransaction.(transactionForRpc)

 - eth_sign.(addressData, message)

 - eth_snapshot.()

 - eth_submitHashrate.(hashRate, id)

 - eth_submitWork.(nonce, headerPowHash, mixDigest)

 - eth_syncing.()

 - eth_uninstallFilter.(filterId)

net
^^^

 - net_dumpPeerConnectionDetails.()

 - net_listening.()

 - net_localEnode.()

 - net_peerCount.()

 - net_version.()

personal
^^^^^^^^

 - personal_ecRecover.(message, signature)

 - personal_importRawKey.(keyData, passphrase)

 - personal_listAccounts.()

 - personal_lockAccount.(address)

 - personal_newAccount.(passphrase)

 - personal_sendTransaction.(transaction, passphrase)

 - personal_sign.(message, address, passphrase)

 - personal_unlockAccount.(address, passphrase)

trace
^^^^^

 - trace_call.(message, traceTypes, numberOrTag)

 - trace_callMany.(calls)

 - trace_rawTransaction.(data, traceTypes)

 - trace_replayBlockTransactions.(numberOrTag, traceTypes)

 - trace_replayTransaction.(txHash, traceTypes)

txpool
^^^^^^

 - txpool_content.()

 - txpool_inspect.()

 - txpool_status.()

web3
^^^^

 - web3_clientVersion.()

 - web3_sha3.(data)

