[View code on GitHub](https://github.com/nethermindeth/nethermind/Nethermind.TxPool/TxBroadcaster.cs)

The `TxBroadcaster` class is responsible for notifying other peers about interesting transactions. It is an internal class in the `TxPool` namespace of the Nethermind project. The class is used to broadcast transactions to other peers in the network. It maintains a list of connected peers that can be notified about transactions. It also maintains two lists of transactions: `persistentTxs` and `accumulatedTemporaryTxs`. The former contains transactions published locally (initiated by this node users) or reorganised, while the latter contains transactions added by external peers between timer elapses.

The class has a `Broadcast` method that takes a transaction and a boolean flag indicating whether the transaction is persistent or not. If the transaction is persistent, it is added to the `persistentTxs` list and then broadcast to all connected peers. If the transaction is not persistent, it is added to the `accumulatedTemporaryTxs` list and then broadcast to all connected peers.

The class also has a `BroadcastOnce` method that takes a peer and an array of transactions. This method is used to broadcast transactions to a single peer. It is called when a new peer connects to the network or when a peer requests a list of transactions.

The `TxBroadcaster` class has a timer that fires every second. When the timer elapses, the `accumulatedTemporaryTxs` list is swapped with the `txsToSend` list, and the transactions in the `txsToSend` list are broadcast to all connected peers. The timer is started when the `TxBroadcaster` object is created.

The `TxBroadcaster` class also has methods for stopping the broadcast of a specific transaction and for ensuring that all transactions up to a certain nonce for a specific address are no longer broadcast.

The `TxBroadcaster` class uses a `SortedPool` data structure to store the `persistentTxs` list. The `SortedPool` is a generic class that takes three type parameters: `ValueKeccak`, `Transaction`, and `Address`. The `ValueKeccak` type is used as the key for the transactions in the pool. The `Transaction` type is the type of the transactions stored in the pool. The `Address` type is used to group transactions by sender.

Overall, the `TxBroadcaster` class is an important component of the Nethermind project's transaction pool. It is responsible for broadcasting transactions to other peers in the network and maintaining a list of connected peers that can be notified about transactions.
## Questions: 
 1. What is the purpose of the TxBroadcaster class?
- The TxBroadcaster class is responsible for notifying other peers about interesting transactions.

2. What is the difference between persistent and non-persistent transactions in this code?
- Persistent transactions are transactions published locally or reorganized, while non-persistent transactions are transactions added by external peers between timer elapses.

3. What is the purpose of the PeerNotificationThreshold property and how is it used?
- The PeerNotificationThreshold property is a declared max percent of transactions in persistent broadcast, which will be sent after processing of every block. It is used to throttle tx broadcast, particularly during forward sync where the head changes a lot which triggers a lot of broadcast.