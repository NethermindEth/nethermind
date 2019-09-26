JSON RPC
********

JSON RPC is available via HTTP and WS (needs to be explicitly switched on in the InitConfig).
Some of the methods listed below are not implemented by Nethermind (they are marked).

admin
^^^^^

 - admin_addPeer(enode)

 - [NOT IMPLEMENTED]admin_dataDir()

 - [NOT IMPLEMENTED]admin_nodeInfo()

 - admin_peers()

 - admin_removePeer(enode)

 - [NOT IMPLEMENTED]admin_setSolc()

clique
^^^^^^

 - clique_discard(signer)

 - clique_getSigners()

 - clique_getSignersAnnotated()

 - clique_getSignersAtHash(hash)

 - clique_getSignersAtHashAnnotated(hash)

 - clique_getSignersAtNumber(number)

 - clique_getSnapshot()

 - clique_getSnapshotAtHash(hash)

 - clique_propose(signer, vote)

debug
^^^^^

 - [NOT IMPLEMENTED]debug_dumpBlock(blockParameter)

 - [NOT IMPLEMENTED]debug_gcStats()

 - debug_getBlockRlp(number)

 - debug_getBlockRlpByHash(hash)

 - debug_getConfigValue(category, name)

 - [NOT IMPLEMENTED]debug_getFromDb(dbName, key)

 - [NOT IMPLEMENTED]debug_memStats(blockParameter)

 - [NOT IMPLEMENTED]debug_seedHash(blockParameter)

 - [NOT IMPLEMENTED]debug_setHead(blockParameter)

 - debug_traceBlock(blockRlp, options)

 - debug_traceBlockByHash(blockHash, options)

 - debug_traceBlockByNumber(number, options)

 - [NOT IMPLEMENTED]debug_traceBlockFromFile(fileName, options)

 - debug_traceTransaction(transactionHash, options)

 - debug_traceTransactionByBlockAndIndex(blockParameter, txIndex, options)

 - debug_traceTransactionByBlockhashAndIndex(blockHash, txIndex, options)

eth
^^^

 - eth_accounts()

 - eth_blockNumber()

 - eth_call(transactionCall, blockParameter)

 - eth_coinbase()

 - eth_estimateGas(transactionCall)

 - eth_gasPrice()

 - eth_getBalance(address, blockParameter)

 - eth_getBlockByHash(blockHash, returnFullTransactionObjects)

 - eth_getBlockByNumber(blockParameter, returnFullTransactionObjects)

 - eth_getBlockTransactionCountByHash(blockHash)

 - eth_getBlockTransactionCountByNumber(blockParameter)

 - eth_getCode(address, blockParameter)

 - eth_getFilterChanges(filterId)

 - eth_getFilterLogs(filterId)

 - eth_getLogs(filter)

 - eth_getProof(accountAddress, hashRate, blockParameter)

 - eth_getStorageAt(address, positionIndex, blockParameter)

 - eth_getTransactionByBlockHashAndIndex(blockHash, positionIndex)

 - eth_getTransactionByBlockNumberAndIndex(blockParameter, positionIndex)

 - eth_getTransactionByHash(transactionHash)

 - eth_getTransactionCount(address, blockParameter)

 - eth_getTransactionReceipt(txHashData)

 - eth_getUncleByBlockHashAndIndex(blockHashData, positionIndex)

 - eth_getUncleByBlockNumberAndIndex(blockParameter, positionIndex)

 - eth_getUncleCountByBlockHash(blockHash)

 - eth_getUncleCountByBlockNumber(blockParameter)

 - [NOT IMPLEMENTED]eth_getWork()

 - eth_hashrate()

 - eth_mining()

 - eth_newBlockFilter()

 - eth_newFilter(filter)

 - eth_newPendingTransactionFilter()

 - eth_protocolVersion()

 - eth_sendRawTransaction(transaction)

 - eth_sendTransaction(transactionForRpc)

 - eth_sign(addressData, message)

 - eth_snapshot()

 - [NOT IMPLEMENTED]eth_submitHashrate(hashRate, id)

 - [NOT IMPLEMENTED]eth_submitWork(nonce, headerPowHash, mixDigest)

 - eth_syncing()

 - eth_uninstallFilter(filterId)

net
^^^

 - net_dumpPeerConnectionDetails()

 - net_listening()

 - net_localAddress()

 - net_localEnode()

 - net_peerCount()

 - net_version()

parity
^^^^^^

 - parity_getBlockReceipts(blockParameter)

 - parity_pendingTransactions()

personal
^^^^^^^^

 - [NOT IMPLEMENTED]personal_ecRecover(message, signature)

 - [NOT IMPLEMENTED]personal_importRawKey(keyData, passphrase)

 - personal_listAccounts()

 - personal_lockAccount(address)

 - personal_newAccount(passphrase)

 - [NOT IMPLEMENTED]personal_sendTransaction(transaction, passphrase)

 - [NOT IMPLEMENTED]personal_sign(message, address, passphrase)

 - personal_unlockAccount(address, passphrase)

trace
^^^^^

 - trace_block(numberOrTag)

 - [NOT IMPLEMENTED]trace_call(message, traceTypes, numberOrTag)

 - [NOT IMPLEMENTED]trace_callMany(calls)

 - [NOT IMPLEMENTED]trace_filter(fromBlock, toBlock, toAddress, after, count)

 - [NOT IMPLEMENTED]trace_get(txHash, positions)

 - [NOT IMPLEMENTED]trace_rawTransaction(data, traceTypes)

 - trace_replayBlockTransactions(numberOrTag, traceTypes)

 - trace_replayTransaction(txHash, traceTypes)

 - trace_transaction(txHash)

txpool
^^^^^^

 - txpool_content()

 - txpool_inspect()

 - txpool_status()

web3
^^^^

 - web3_clientVersion()

 - web3_sha3(data)

