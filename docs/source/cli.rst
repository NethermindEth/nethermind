CLI
***

CLI access is not currently included in the Nethermind launcher but will be added very soon.

admin
^^^^^

 - admin.addPeer(enode, addToStaticNodes) - 

 - admin.peers - 

 - admin.removePeer(enode, removeFromStaticNodes) - 

clique
^^^^^^

 - clique.discard(address) - 

 - clique.getSigners() - 

 - clique.getSignersAnnotated() - 

 - clique.getSignersAtHash(hash) - 

 - clique.getSignersAtHashAnnotated(hash) - 

 - clique.getSignersAtNumber(number) - 

 - clique.getSnapshot() - 

 - clique.getSnapshotAtHash(hash) - 

 - clique.propose(address, vote) - 

debug
^^^^^

 - debug.deleteChainSlice(startNumber, endNumber) - 

 - debug.getBlockRlp(number) - 

 - debug.getBlockRlpByHash(hash) - 

 - debug.getChainlevel(number) - 

 - debug.config(category, name) - 

 - debug.traceBlock(rlp, options) - 

 - debug.traceBlockByHash(hash, options) - 

 - debug.traceBlockByNumber(number, options) - 

 - debug.traceTransaction(hash, options) - 

 - debug.traceTransactionByBlockAndIndex(hash, options) - 

 - debug.traceTransactionByBlockhashAndIndex(hash, options) - 

 - debug.traceTransactionInBlockByHash(rlp, hash, options) - 

 - debug.traceTransactionInBlockByIndex(rlp, index, options) - 

diag
^^^^

 - diag.cliVersion - 

eth
^^^

 - eth.blockNumber - 

 - eth.call(tx, blockParameter) - 

 - eth.chainId - 

 - eth.getBalance(address, blockParameter) - 

 - eth.getBlockByHash(hash, returnFullTransactionObjects) - 

 - eth.getBlockByNumber(blockParameter, returnFullTransactionObjects) - 

 - eth.getBlockTransactionCountByHash(hash) - 

 - eth.getBlockTransactionCountByNumber(blockParameter) - 

 - eth.getCode(address, blockParameter) - 

 - eth.getLogs(json) - 

 - eth.getStorageAt(address, positionIndex, blockParameter) - 

 - eth.getTransactionByBlockNumberAndIndex(blockParameter, index) - 

 - eth.getTransactionCount(address, blockParameter) - 

 - eth.getTransactionReceipt(txHash) - 

 - eth.getUncleCountByBlockNumber(blockParameter) - 

 - eth.protocolVersion - 

 - eth.sendEth(from, to, amountInEth) - 

 - eth.sendWei(from, to, amountInWei) - 

 - eth.sendRawTransaction(txRlp) - 

net
^^^

 - net.localEnode - 

 - net.peerCount - 

 - net.version - 

node
^^^^

 - node.address - 

 - node.enode - 

 - node.setNodeKey(key) - 

 - node.switch(uri) - 

 - node.switchLocal(uri) - 

 - node.uri - 

parity
^^^^^^

 - parity.getBlockReceipts(blockParameter) - Returns receipts from all transactions from particular block

 - parity.pendingTransactions() - Returns the pending transactions using Parity format

personal
^^^^^^^^

 - personal.listAccounts - 

 - personal.lockAccount(addressHex) - 

 - personal.newAccount(password) - 

 - personal.unlockAccount(addressHex, password) - 

system
^^^^^^

 - system.getVariable(name, defaultValue) - 

 - system.memory - 

trace
^^^^^

 - trace.replayBlockTransactions(blockNumber, traceTypes) - Replays all transactions in a block returning the requested traces for each transaction.

 - trace.replayTransaction(txHash, traceTypes) - Replays a transaction, returning the traces.

 - trace.block(blockNumber) - Returns traces created at given block.

 - trace.rawTransaction(txData, traceTypes) - Traces a call to eth_sendRawTransaction without making the call, returning the traces

 - trace.transaction(txHash) - Returns all traces of given transaction

txpool
^^^^^^

 - txpool.content - 

 - txpool.inspect - 

 - txpool.status - 

web3
^^^^

 - web3.clientVersion - 

 - web3.sha3(data) - 

 - web3.toDecimal(hex) - 

web3.py
^^^^^^^

It is possible to connect to the Nethermind node using web3.py (python web3.js implementation).
You will need to have web3.py installed using following guides:

- https://github.com/ethereum/web3.py
- https://web3py.readthedocs.io/en/stable/quickstart.html

You may use below script in order to check your connection.
Please make sure you have enabled JSON RPC module, this can be done by passing flag ``--JsonRpc.Enabled true``

::

 from web3.auto import w3

 connected = w3.isConnected()
 print(connected)

 if connected and w3.clientVersion.startswith('Nethermind'):
     client = w3.clientVersion
     print(client)

 else:
     client = None
     print(client)

You should see the following output (depends on the node version):

::

 True
 Nethermind/v1.4.8-13-5c66dcdf6-20200120/X64-Linux 5.3.2-050302-generic/Core3.1.1