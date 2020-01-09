JSON RPC
********

JSON RPC is available via HTTP and WS (needs to be explicitly switched on in the InitConfig).
Some of the methods listed below are not implemented by Nethermind (they are marked).

admin
^^^^^

 - admin_addPeer(enode, addToStaticNodes) - 

 - [NOT IMPLEMENTED]admin_dataDir() - 

 - [NOT IMPLEMENTED]admin_nodeInfo() - 

 - admin_peers() - 

 - admin_removePeer(enode, removeFromStaticNodes) - 

 - [NOT IMPLEMENTED]admin_setSolc() - 

clique
^^^^^^

 - clique_discard(signer) - 

 - clique_getSigners() - 

 - clique_getSignersAnnotated() - 

 - clique_getSignersAtHash(hash) - 

 - clique_getSignersAtHashAnnotated(hash) - 

 - clique_getSignersAtNumber(number) - 

 - clique_getSnapshot() - 

 - clique_getSnapshotAtHash(hash) - 

 - clique_propose(signer, vote) - 

debug
^^^^^

 - debug_deleteChainSlice(startNumber, endNumber) - Deletes a slice of a chain from the tree on all branches (Nethermind specific).

 - [NOT IMPLEMENTED]debug_dumpBlock(blockParameter) - 

 - [NOT IMPLEMENTED]debug_gcStats() - 

 - debug_getBlockRlp(number) - Retrieves a block in the RLP-serialized form.

 - debug_getBlockRlpByHash(hash) - Retrieves a block in the RLP-serialized form.

 - debug_getChainLevel(number) - Retrieves a representation of tree branches on a given chain level (Nethermind specific).

 - debug_getConfigValue(category, name) - Retrieves the Nethermind configuration value, e.g. JsonRpc.Enabled

 - [NOT IMPLEMENTED]debug_getFromDb(dbName, key) - 

 - [NOT IMPLEMENTED]debug_memStats(blockParameter) - 

 - [NOT IMPLEMENTED]debug_seedHash(blockParameter) - 

 - [NOT IMPLEMENTED]debug_setHead(blockParameter) - 

 - debug_traceBlock(blockRlp, options) - 

 - debug_traceBlockByHash(blockHash, options) - 

 - debug_traceBlockByNumber(number, options) - 

 - [NOT IMPLEMENTED]debug_traceBlockFromFile(fileName, options) - 

 - debug_traceTransaction(transactionHash, options) - 

 - debug_traceTransactionByBlockAndIndex(blockParameter, txIndex, options) - 

 - debug_traceTransactionByBlockhashAndIndex(blockHash, txIndex, options) - 

 - debug_traceTransactionInBlockByHash(blockRlp, transactionHash, options) - 

 - debug_traceTransactionInBlockByIndex(blockRlp, txIndex, options) - 

eth
^^^

 - [NOT IMPLEMENTED]eth_accounts() - Returns accounts

 - eth_blockNumber() - Returns current block number

 - eth_call(transactionCall, blockParameter) - Executes a tx call (does not create a transaction)

 - eth_chainId() - Returns ChainID

 - [NOT IMPLEMENTED]eth_coinbase() - Returns miner's coinbase'

 - eth_estimateGas(transactionCall) - Executes a tx call and returns gas used (does not create a transaction)

 - [NOT IMPLEMENTED]eth_gasPrice() - Returns miner's gas price

 - eth_getBalance(address, blockParameter) - Returns account balance

 - eth_getBlockByHash(blockHash, returnFullTransactionObjects) - Retrieves a block by hash

 - eth_getBlockByNumber(blockParameter, returnFullTransactionObjects) - Retrieves a block by number

 - eth_getBlockTransactionCountByHash(blockHash) - Returns number of transactions in the block block hash

 - eth_getBlockTransactionCountByNumber(blockParameter) - Returns number of transactions in the block by block number

 - eth_getCode(address, blockParameter) - Returns account code at given address and block

 - eth_getFilterChanges(filterId) - Reads filter changes

 - eth_getFilterLogs(filterId) - Reads filter changes

 - eth_getLogs(filter) - Reads logs

 - eth_getProof(accountAddress, hashRate, blockParameter) - https://github.com/ethereum/EIPs/issues/1186

 - eth_getStorageAt(address, positionIndex, blockParameter) - Returns storage data at address. storage_index

 - eth_getTransactionByBlockHashAndIndex(blockHash, positionIndex) - Retrieves a transaction by block hash and index

 - eth_getTransactionByBlockNumberAndIndex(blockParameter, positionIndex) - Retrieves a transaction by block number and index

 - eth_getTransactionByHash(transactionHash) - Retrieves a transaction by hash

 - eth_getTransactionCount(address, blockParameter) - Returns number of transactions in the block

 - eth_getTransactionReceipt(txHashData) - Retrieves a transaction receipt by tx hash

 - eth_getUncleByBlockHashAndIndex(blockHashData, positionIndex) - Retrieves an uncle block header by block hash and uncle index

 - eth_getUncleByBlockNumberAndIndex(blockParameter, positionIndex) - Retrieves an uncle block header by block number and uncle index

 - eth_getUncleCountByBlockHash(blockHash) - Returns number of uncles in the block by block hash

 - eth_getUncleCountByBlockNumber(blockParameter) - Returns number of uncles in the block by block number

 - [NOT IMPLEMENTED]eth_getWork() - 

 - [NOT IMPLEMENTED]eth_hashrate() - Returns mining hashrate

 - [NOT IMPLEMENTED]eth_mining() - Returns mining status

 - eth_newBlockFilter() - Creates an update filter

 - eth_newFilter(filter) - Creates an update filter

 - eth_newPendingTransactionFilter() - Creates an update filter

 - eth_protocolVersion() - Returns ETH protocol version

 - eth_sendRawTransaction(transaction) - Send a raw transaction to the tx pool and broadcasting

 - eth_sendTransaction(transactionForRpc) - Send a transaction to the tx pool and broadcasting

 - [NOT IMPLEMENTED]eth_sign(addressData, message) - Signs a transaction

 - [NOT IMPLEMENTED]eth_snapshot() - Returns full state snapshot

 - [NOT IMPLEMENTED]eth_submitHashrate(hashRate, id) - 

 - [NOT IMPLEMENTED]eth_submitWork(nonce, headerPowHash, mixDigest) - 

 - eth_syncing() - Returns syncing status

 - eth_uninstallFilter(filterId) - Creates an update filter

net
^^^

 - net_listening() - 

 - net_localAddress() - 

 - net_localEnode() - 

 - net_peerCount() - 

 - net_version() - 

parity
^^^^^^

 - parity_getBlockReceipts(blockParameter) - 

 - parity_pendingTransactions() - 

personal
^^^^^^^^

 - [NOT IMPLEMENTED]personal_ecRecover(message, signature) - ecRecover returns the address associated with the private key that was used to calculate the signature in personal_sign

 - [NOT IMPLEMENTED]personal_importRawKey(keyData, passphrase) - 

 - personal_listAccounts() - 

 - personal_lockAccount(address) - 

 - personal_newAccount(passphrase) - 

 - [NOT IMPLEMENTED]personal_sendTransaction(transaction, passphrase) - 

 - [NOT IMPLEMENTED]personal_sign(message, address, passphrase) - The sign method calculates an Ethereum specific signature with: sign(keccack256("ƞthereum Signed Message:
" + len(message) + message))).

 - personal_unlockAccount(address, passphrase) - 

trace
^^^^^

 - trace_block(numberOrTag) - 

 - [NOT IMPLEMENTED]trace_call(message, traceTypes, numberOrTag) - 

 - [NOT IMPLEMENTED]trace_callMany(calls) - 

 - [NOT IMPLEMENTED]trace_filter(fromBlock, toBlock, toAddress, after, count) - 

 - [NOT IMPLEMENTED]trace_get(txHash, positions) - 

 - trace_rawTransaction(data, traceTypes) - Traces a call to eth_sendRawTransaction without making the call, returning the traces

 - trace_replayBlockTransactions(numberOrTag, traceTypes) - 

 - trace_replayTransaction(txHash, traceTypes) - 

 - trace_transaction(txHash) - 

txpool
^^^^^^

 - txpool_content() - 

 - txpool_inspect() - 

 - txpool_status() - 

web3
^^^^

 - web3_clientVersion() - 

 - web3_sha3(data) - 

