JSON RPC
********

JSON RPC is available via HTTP and WS (needs to be explicitly switched on in the InitConfig).
Some of the methods listed below are not implemented by Nethermind (they are marked).

admin
^^^^^

 admin_addPeer(enode, addToStaticNodes)
  

 admin_dataDir()
  [NOT IMPLEMENTED] 

 admin_nodeInfo()
  [NOT IMPLEMENTED] 

 admin_peers()
  

 admin_removePeer(enode, removeFromStaticNodes)
  

 admin_setSolc()
  [NOT IMPLEMENTED] 

clique
^^^^^^

 clique_discard(signer)
  <description missing>

 clique_getSigners()
  <description missing>

 clique_getSignersAnnotated()
  <description missing>

 clique_getSignersAtHash(hash)
  <description missing>

 clique_getSignersAtHashAnnotated(hash)
  <description missing>

 clique_getSignersAtNumber(number)
  <description missing>

 clique_getSnapshot()
  <description missing>

 clique_getSnapshotAtHash(hash)
  <description missing>

 clique_propose(signer, vote)
  <description missing>

debug
^^^^^

 debug_deleteChainSlice(startNumber, endNumber)
  Deletes a slice of a chain from the tree on all branches (Nethermind specific).

 debug_dumpBlock(blockParameter)
  [NOT IMPLEMENTED] 

 debug_gcStats()
  [NOT IMPLEMENTED] 

 debug_getBlockRlp(number)
  Retrieves a block in the RLP-serialized form.

 debug_getBlockRlpByHash(hash)
  Retrieves a block in the RLP-serialized form.

 debug_getChainLevel(number)
  Retrieves a representation of tree branches on a given chain level (Nethermind specific).

 debug_getConfigValue(category, name)
  Retrieves the Nethermind configuration value, e.g. JsonRpc.Enabled

 debug_getFromDb(dbName, key)
  [NOT IMPLEMENTED] 

 debug_memStats(blockParameter)
  [NOT IMPLEMENTED] 

 debug_seedHash(blockParameter)
  [NOT IMPLEMENTED] 

 debug_setHead(blockParameter)
  [NOT IMPLEMENTED] 

 debug_traceBlock(blockRlp, options)
  

 debug_traceBlockByHash(blockHash, options)
  

 debug_traceBlockByNumber(number, options)
  

 debug_traceBlockFromFile(fileName, options)
  [NOT IMPLEMENTED] 

 debug_traceTransaction(transactionHash, options)
  

 debug_traceTransactionByBlockAndIndex(blockParameter, txIndex, options)
  

 debug_traceTransactionByBlockhashAndIndex(blockHash, txIndex, options)
  

 debug_traceTransactionInBlockByHash(blockRlp, transactionHash, options)
  

 debug_traceTransactionInBlockByIndex(blockRlp, txIndex, options)
  

eth
^^^

 eth_accounts()
  [NOT IMPLEMENTED] Returns accounts

 eth_blockNumber()
  Returns current block number

 eth_call(transactionCall, blockParameter)
  Executes a tx call (does not create a transaction)

 eth_chainId()
  Returns ChainID

 eth_coinbase()
  [NOT IMPLEMENTED] Returns miner's coinbase'

 eth_estimateGas(transactionCall)
  Executes a tx call and returns gas used (does not create a transaction)

 eth_gasPrice()
  [NOT IMPLEMENTED] Returns miner's gas price

 eth_getBalance(address, blockParameter)
  Returns account balance

 eth_getBlockByHash(blockHash, returnFullTransactionObjects)
  Retrieves a block by hash

 eth_getBlockByNumber(blockParameter, returnFullTransactionObjects)
  Retrieves a block by number

 eth_getBlockTransactionCountByHash(blockHash)
  Returns number of transactions in the block block hash

 eth_getBlockTransactionCountByNumber(blockParameter)
  Returns number of transactions in the block by block number

 eth_getCode(address, blockParameter)
  Returns account code at given address and block

 eth_getFilterChanges(filterId)
  Reads filter changes

 eth_getFilterLogs(filterId)
  Reads filter changes

 eth_getLogs(filter)
  Reads logs

 eth_getProof(accountAddress, hashRate, blockParameter)
  https://github.com/ethereum/EIPs/issues/1186

 eth_getStorageAt(address, positionIndex, blockParameter)
  Returns storage data at address. storage_index

 eth_getTransactionByBlockHashAndIndex(blockHash, positionIndex)
  Retrieves a transaction by block hash and index

 eth_getTransactionByBlockNumberAndIndex(blockParameter, positionIndex)
  Retrieves a transaction by block number and index

 eth_getTransactionByHash(transactionHash)
  Retrieves a transaction by hash

 eth_getTransactionCount(address, blockParameter)
  Returns account nonce (number of trnsactions from the account since genesis) at the given block number

 eth_getTransactionReceipt(txHashData)
  Retrieves a transaction receipt by tx hash

 eth_getUncleByBlockHashAndIndex(blockHashData, positionIndex)
  Retrieves an uncle block header by block hash and uncle index

 eth_getUncleByBlockNumberAndIndex(blockParameter, positionIndex)
  Retrieves an uncle block header by block number and uncle index

 eth_getUncleCountByBlockHash(blockHash)
  Returns number of uncles in the block by block hash

 eth_getUncleCountByBlockNumber(blockParameter)
  Returns number of uncles in the block by block number

 eth_getWork()
  [NOT IMPLEMENTED] 

 eth_hashrate()
  [NOT IMPLEMENTED] Returns mining hashrate

 eth_mining()
  [NOT IMPLEMENTED] Returns mining status

 eth_newBlockFilter()
  Creates an update filter

 eth_newFilter(filter)
  Creates an update filter

 eth_newPendingTransactionFilter()
  Creates an update filter

 eth_pendingTransactions()
  Returns the pending transactions list

 eth_protocolVersion()
  Returns ETH protocol version

 eth_sendRawTransaction(transaction)
  Send a raw transaction to the tx pool and broadcasting

 eth_sendTransaction(transactionForRpc)
  Send a transaction to the tx pool and broadcasting

 eth_sign(addressData, message)
  [NOT IMPLEMENTED] Signs a transaction

 eth_snapshot()
  [NOT IMPLEMENTED] Returns full state snapshot

 eth_submitHashrate(hashRate, id)
  [NOT IMPLEMENTED] 

 eth_submitWork(nonce, headerPowHash, mixDigest)
  [NOT IMPLEMENTED] 

 eth_syncing()
  Returns syncing status

 eth_uninstallFilter(filterId)
  Creates an update filter

net
^^^

 net_listening()
  <description missing>

 net_localAddress()
  <description missing>

 net_localEnode()
  <description missing>

 net_peerCount()
  <description missing>

 net_version()
  <description missing>

parity
^^^^^^

 parity_getBlockReceipts(blockParameter)
  <description missing>

 parity_pendingTransactions()
  <description missing>

personal
^^^^^^^^

 personal_ecRecover(message, signature)
  [NOT IMPLEMENTED] ecRecover returns the address associated with the private key that was used to calculate the signature in personal_sign

 personal_importRawKey(keyData, passphrase)
  [NOT IMPLEMENTED] 

 personal_listAccounts()
  <description missing>

 personal_lockAccount(address)
  <description missing>

 personal_newAccount(passphrase)
  <description missing>

 personal_sendTransaction(transaction, passphrase)
  [NOT IMPLEMENTED] 

 personal_sign(message, address, passphrase)
  [NOT IMPLEMENTED] The sign method calculates an Ethereum specific signature with: sign(keccack256("ƞthereum Signed Message:
" + len(message) + message))).

 personal_unlockAccount(address, passphrase)
  <description missing>

proof
^^^^^

 proof_call(tx, blockParameter)
  [NOT IMPLEMENTED] This function returns the same result as `eth_getTransactionByHash` and also a tx proof and a serialized block header.

 proof_getTransactionByHash(txHash, includeHeader)
  This function returns the same result as `eth_getTransactionReceipt` and also a tx proof, receipt proof and serialized block headers.

 proof_getTransactionReceipt(txHash, includeHeader)
  This function should return the same result as `eth_call` and also proofs of all USED accunts and their storages and serialized block headers

trace
^^^^^

 trace_block(numberOrTag)
  

 trace_call(message, traceTypes, numberOrTag)
  [NOT IMPLEMENTED] 

 trace_callMany(calls)
  [NOT IMPLEMENTED] 

 trace_filter(fromBlock, toBlock, toAddress, after, count)
  [NOT IMPLEMENTED] 

 trace_get(txHash, positions)
  [NOT IMPLEMENTED] 

 trace_rawTransaction(data, traceTypes)
  Traces a call to eth_sendRawTransaction without making the call, returning the traces

 trace_replayBlockTransactions(numberOrTag, traceTypes)
  

 trace_replayTransaction(txHash, traceTypes)
  

 trace_transaction(txHash)
  

txpool
^^^^^^

 txpool_content()
  <description missing>

 txpool_inspect()
  <description missing>

 txpool_status()
  <description missing>

web3
^^^^

 web3_clientVersion()
  <description missing>

 web3_sha3(data)
  <description missing>

