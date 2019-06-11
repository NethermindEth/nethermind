CLI
***

CLI access is not currently included in the Nethermind launcher but will be added very soon.

clique
^^^^^^

 - clique.getSigners() - 

 - clique.getSignersAnnotated() - 

 - clique.getSignersAtHash(hash) - 

 - clique.getSignersAtHashAnnotated(hash) - 

 - clique.getSignersAtNumber(number) - 

 - clique.getSnapshot() - 

 - clique.getSnapshotAtHash(hash) - 

 - clique.propose(address, vote) - 

 - clique.discard(address) - 

debug
^^^^^

 - debug.config(category, name) - 

 - debug.traceBlock(hash) - 

 - debug.traceBlockByHash(hash) - 

 - debug.traceBlockByNumber(number) - 

 - debug.traceTransactionByBlockAndIndex(hash) - 

 - debug.traceTransactionByBlockhashAndIndex(hash) - 

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

trace
^^^^^

 - trace.replayBlockTransactions(blockNumber, traceTypes) - 

 - trace.replayTransaction(txHash, traceTypes) - 

personal
^^^^^^^^

 - personal.listAccounts - 

 - personal.lockAccount(addressHex) - 

 - personal.newAccount(password) - 

 - personal.unlockAccount(addressHex, password) - 

web3
^^^^

 - web3.clientVersion - 

 - web3.sha3(data) - 

 - web3.toDecimal(hex) - 

