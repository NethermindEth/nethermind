CLI
***

CLI access is not currently included in the Nethermind launcher but will be added very soon.

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

 - debug.getBlockRlp(number) - 

 - debug.getBlockRlpByHash(hash) - 

 - debug.config(category, name) - 

 - debug.traceBlock(hash, options) - 

 - debug.traceBlockByHash(hash, options) - 

 - debug.traceBlockByNumber(number, options) - 

 - debug.traceTransaction(hash, options) - 

 - debug.traceTransactionByBlockAndIndex(hash, options) - 

 - debug.traceTransactionByBlockhashAndIndex(hash, options) - 

diag
^^^^

 - diag.cliVersion - 

eth
^^^

 - eth.blockNumber - 

 - eth.getBalance(address, blockParameter) - 

 - eth.getBlockByHash(hash, returnFullTransactionObjects) - 

 - eth.getBlockByNumber(blockParameter, returnFullTransactionObjects) - 

 - eth.getBlockTransactionCountByHash(hash) - 

 - eth.getBlockTransactionCountByNumber(blockParameter) - 

 - eth.getCode(address, blockParameter) - 

 - eth.getStorageAt(address, positionIndex, blockParameter) - 

 - eth.getTransactionByBlockNumberAndIndex(blockParameter, index) - 

 - eth.getTransactionReceipt(txHash) - 

 - eth.getUncleCountByBlockNumber(blockParameter) - 

 - eth.protocolVersion - 

 - eth.sendEth(from, to, amountInEth) - 

 - eth.sendWei(from, to, amountInWei) - 

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

 - trace.transaction(txHash) - Returns all traces of given transaction

web3
^^^^

 - web3.clientVersion - 

 - web3.sha3(data) - 

 - web3.toDecimal(hex) - 

