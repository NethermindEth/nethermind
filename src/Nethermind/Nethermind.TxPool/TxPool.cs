//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool.Collections;
using Timer = System.Timers.Timer;

[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]

namespace Nethermind.TxPool
{
    /// <summary>
    /// Stores all pending transactions. These will be used by block producer if this node is a miner / validator
    /// or simply for broadcasting and tracing in other cases.
    /// </summary>
    public class TxPool : ITxPool, IDisposable
    {

        private readonly object _locker = new();

        private readonly Dictionary<Address, AddressNonces> _nonces =
            new();

        private readonly LruKeyCache<Keccak> _hashCache = new(MemoryAllowance.TxHashCacheSize,
            Math.Min(1024 * 16, MemoryAllowance.TxHashCacheSize), "tx hashes");

        /// <summary>
        /// Number of blocks after which own transaction will not be resurrected any more
        /// </summary>
        private const long FadingTimeInBlocks = 64;

        /// <summary>
        /// Notification threshold randomizer seed
        /// </summary>
        private static int _seed = Environment.TickCount;

        /// <summary>
        /// Random number generator for peer notification threshold - no need to be securely random.
        /// </summary>
        private static readonly ThreadLocal<Random> Random =
            new(() => new Random(Interlocked.Increment(ref _seed)));

        private readonly TxDistinctSortedPool _transactions;

        private readonly IChainHeadSpecProvider _specProvider;
        private readonly ITxPoolConfig _txPoolConfig;
        private readonly IReadOnlyStateProvider _stateProvider;
        private readonly ITxValidator _validator;
        private readonly IEthereumEcdsa _ecdsa;
        private readonly IChainHeadInfoProvider _chainHeadInfoProvider;
        protected readonly ILogger _logger;

        /// <summary>
        /// Transactions published locally (initiated by this node users) or reorganised.
        /// </summary>
        private readonly SortedPool<Keccak, Transaction, Address> _persistentBroadcastTransactions;
        
        /// <summary>
        /// Long term storage for pending transactions.
        /// </summary>
        private readonly ITxStorage _txStorage;

        /// <summary>
        /// Connected peers that can be notified about transactions.
        /// </summary>
        private readonly ConcurrentDictionary<PublicKey, ITxPoolPeer> _peers =
            new();

        /// <summary>
        /// Timer for rebroadcasting pending own transactions.
        /// </summary>
        private readonly Timer _ownTimer;

        /// <summary>
        /// Indexes transactions
        /// </summary>
        private ulong _txIndex;

        /// <summary>
        /// This class stores all known pending transactions that can be used for block production
        /// (by miners or validators) or simply informing other nodes about known pending transactions (broadcasting).
        /// </summary>
        /// <param name="txStorage">Tx storage used to reject known transactions.</param>
        /// <param name="ecdsa">Used to recover sender addresses from transaction signatures.</param>
        /// <param name="specProvider">Used for retrieving information on EIPs that may affect tx signature scheme.</param>
        /// <param name="chainHeadInfoProvider"></param>
        /// <param name="txPoolConfig"></param>
        /// <param name="stateProvider"></param>
        /// <param name="transactionComparerProvider"></param>
        /// <param name="validator"></param>
        /// <param name="logManager"></param>
        /// <param name="comparer"></param>
        public TxPool(ITxStorage txStorage,
            IEthereumEcdsa ecdsa,
            IChainHeadInfoProvider chainHeadInfoProvider,
            ITxPoolConfig txPoolConfig,
            ITxValidator validator,
            ILogManager? logManager,
            IComparer<Transaction> comparer)
        {
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            _chainHeadInfoProvider = chainHeadInfoProvider ?? throw new ArgumentNullException(nameof(chainHeadInfoProvider));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _txStorage = txStorage ?? throw new ArgumentNullException(nameof(txStorage));
            _specProvider = _chainHeadInfoProvider.SpecProvider;
            _txPoolConfig = txPoolConfig;
            _stateProvider = _chainHeadInfoProvider.ReadOnlyStateProvider;
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));

            MemoryAllowance.MemPoolSize = txPoolConfig.Size;
            ThisNodeInfo.AddInfo("Mem est tx   :",
                $"{(LruCache<Keccak, object>.CalculateMemorySize(32, MemoryAllowance.TxHashCacheSize) + LruCache<Keccak, Transaction>.CalculateMemorySize(4096, MemoryAllowance.MemPoolSize)) / 1000 / 1000}MB"
                    .PadLeft(8));

            _transactions = new TxDistinctSortedPool(MemoryAllowance.MemPoolSize, comparer, logManager);
            _persistentBroadcastTransactions = new TxDistinctSortedPool(MemoryAllowance.MemPoolSize, comparer, logManager);
            _chainHeadInfoProvider.HeadChanged += OnHeadChange;
            _ownTimer = new Timer(500);
            _ownTimer.Elapsed += OwnTimerOnElapsed;
            _ownTimer.AutoReset = false;
            _ownTimer.Start();
        }

        public uint FutureNonceRetention  => _txPoolConfig.FutureNonceRetention;
        
        internal long? BlockGasLimit { get; set; } = null;

        private UInt256 CurrentBaseFee { get; set; } = 0;

        public Transaction[] GetPendingTransactions() => _transactions.GetSnapshot();
        
        public int GetPendingTransactionsCount() => _transactions.Count;

        public IDictionary<Address, Transaction[]> GetPendingTransactionsBySender() =>
            _transactions.GetBucketSnapshot();

        public Transaction[] GetOwnPendingTransactions() => _persistentBroadcastTransactions.GetSnapshot();

        private void OnHeadChange(object? sender, BlockReplacementEventArgs e)
        {
            // we don't want this to be on main processing thread
            Task.Run(() => OnHeadChange(e.Block!, e.PreviousBlock))
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        if (_logger.IsError) _logger.Error($"Couldn't correctly add or remove transactions from txpool after processing block {e.Block.ToString(Block.Format.FullHashAndNumber)}.", t.Exception);
                    }
                });
        }
        
        private void OnHeadChange(Block block, Block? previousBlock)
        {
            BlockGasLimit = block.GasLimit;
            CurrentBaseFee = block.Header.BaseFeePerGas;
            RemoveProcessedTransactions(block.Transactions);
            UpdateBuckets();
            ReAddReorganisedTransactions(previousBlock);
        }

        private void ReAddReorganisedTransactions(Block? previousBlock)
        {
            if (previousBlock is not null)
            {
                bool isEip155Enabled = _specProvider.GetSpec(previousBlock.Number).IsEip155Enabled;
                for (int i = 0; i < previousBlock.Transactions.Length; i++)
                {
                    Transaction tx = previousBlock.Transactions[i];
                    AddTransaction(tx, (isEip155Enabled ? TxHandlingOptions.None : TxHandlingOptions.PreEip155Signing) | TxHandlingOptions.Reorganisation);
                }
            }
        }

        private void RemoveProcessedTransactions(IReadOnlyList<Transaction> blockTransactions)
        {
            long transactionsInBlock = blockTransactions.Count;
            long discoveredForPendingTxs = 0;
            long discoveredForHashCache = 0;
            
            for (int i = 0; i < transactionsInBlock; i++)
            {
                Keccak txHash = blockTransactions[i].Hash;
                
                if (!IsKnown(txHash))
                {
                    discoveredForHashCache++;
                }

                if (!RemoveTransaction(txHash))
                {
                    discoveredForPendingTxs++;
                }
            }
            
            Metrics.DarkPoolRatioLevel1 = transactionsInBlock == 0 ? 0 : (float)discoveredForHashCache / transactionsInBlock;
            Metrics.DarkPoolRatioLevel2 = transactionsInBlock == 0 ? 0 : (float)discoveredForPendingTxs / transactionsInBlock;
        }

        public void AddPeer(ITxPoolPeer peer)
        {
            PeerInfo peerInfo = new(peer);
            if (_peers.TryAdd(peer.Id, peerInfo))
            {
                foreach (var transaction in _transactions.GetSnapshot())
                {
                    Notify(peerInfo, transaction, false);
                }

                if (_logger.IsTrace) _logger.Trace($"Added a peer to TX pool: {peer}");
            }
        }

        public void RemovePeer(PublicKey nodeId)
        {
            if (!_peers.TryRemove(nodeId, out _))
            {
                return;
            }

            if (_logger.IsTrace) _logger.Trace($"Removed a peer from TX pool: {nodeId}");
        }

        public AddTxResult AddTransaction(Transaction tx, TxHandlingOptions handlingOptions)
        {
            if (tx.Hash is null)
            {
                throw new ArgumentException($"{nameof(tx.Hash)} not set on {nameof(Transaction)}");
            }
            
            tx.PoolIndex = Interlocked.Increment(ref _txIndex);

            NewDiscovered?.Invoke(this, new TxEventArgs(tx));

            bool managedNonce = (handlingOptions & TxHandlingOptions.ManagedNonce) == TxHandlingOptions.ManagedNonce;
            bool isPersistentBroadcast = (handlingOptions & TxHandlingOptions.PersistentBroadcast) ==
                                         TxHandlingOptions.PersistentBroadcast;
            bool isReorg = (handlingOptions & TxHandlingOptions.Reorganisation) == TxHandlingOptions.Reorganisation;

            if (_logger.IsTrace)
                _logger.Trace(
                    $"Adding transaction {tx.ToString("  ")} - managed nonce: {managedNonce} | persistent broadcast {isPersistentBroadcast}");

            return FilterTransaction(tx, managedNonce) ?? AddCore(tx, isPersistentBroadcast, isReorg);
        }
        
        protected virtual AddTxResult? FilterTransaction(Transaction tx, in bool managedNonce)
        {
            if (tx.Hash is null)
            {
                return AddTxResult.Invalid;
            }

            IReleaseSpec spec = _specProvider.GetSpec();
            Metrics.PendingTransactionsReceived++;
            
            if (!_validator.IsWellFormed(tx, spec))
            {
                // It may happen that other nodes send us transactions that were signed for another chain or don't have enough gas.
                Metrics.PendingTransactionsDiscarded++;
                if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, invalid transaction.");
                return AddTxResult.Invalid;
            }
            
            long gasLimit = Math.Min(BlockGasLimit ?? long.MaxValue, _txPoolConfig.GasLimit ?? long.MaxValue);
            if (tx.GasLimit > gasLimit)
            {
                if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, gas limit exceeded.");
                return AddTxResult.GasLimitExceeded;
            }
            
            /* We have encountered multiple transactions that do not resolve sender address properly.
             * We need to investigate what these txs are and why the sender address is resolved to null.
             * Then we need to decide whether we really want to broadcast them.
             */
            if (tx.SenderAddress is null)
            {
                tx.SenderAddress = _ecdsa.RecoverAddress(tx);
                if (tx.SenderAddress is null)
                {
                    if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, no sender.");
                    return AddTxResult.PotentiallyUseless;
                }
            }

            // As we have limited number of transaction that we store in mem pool its fairly easy to fill it up with
            // high-priority garbage transactions. We need to filter them as much as possible to use the tx pool space
            // efficiently. One call to get account from state is not that costly and it only happens after previous checks.
            // This was modeled by OpenEthereum behavior.
            Account account = _stateProvider.GetAccount(tx.SenderAddress);
            UInt256 currentNonce = account?.Nonce ?? UInt256.Zero;
            if (tx.Nonce < currentNonce)
            {
                if (_logger.IsTrace)
                    _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, nonce already used.");
                return AddTxResult.OldNonce;
            }
            
            int numberOfSenderTxsInPending = _transactions.GetBucketCount(tx.SenderAddress);
            bool isTxPoolFull = _transactions.IsFull();
            bool isTxNonceNextInOrder = tx.Nonce <= (long)currentNonce + numberOfSenderTxsInPending;
            bool isTxNonceTooFarInFuture = tx.Nonce > currentNonce + FutureNonceRetention;
            if (isTxPoolFull && !isTxNonceNextInOrder
                || isTxNonceTooFarInFuture)
            {
                if (_logger.IsTrace)
                    _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, nonce in future.");
                return AddTxResult.FutureNonce;
            }

            UInt256 balance = account?.Balance ?? UInt256.Zero;
            UInt256 payableGasPrice = tx.CalculatePayableGasPrice(spec.IsEip1559Enabled, CurrentBaseFee, balance);

            bool overflow = spec.IsEip1559Enabled && UInt256.AddOverflow(tx.MaxPriorityFeePerGas, tx.MaxFeePerGas, out _);
            overflow |= UInt256.MultiplyOverflow(payableGasPrice, (UInt256) tx.GasLimit, out UInt256 cost);
            overflow |= UInt256.AddOverflow(cost, tx.Value, out cost);
            if (overflow)
            {
                if (_logger.IsTrace)
                    _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, cost overflow.");
                return AddTxResult.BalanceOverflow;
            }
            else if (balance < cost)
            {
                if (_logger.IsTrace)
                    _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, insufficient funds.");
                return AddTxResult.InsufficientFunds;
            }
                
            if (isTxPoolFull
                && _transactions.TryGetLast(out var lastTx)
                && payableGasPrice <= lastTx?.GasBottleneck)
            {
                if (_logger.IsTrace)
                    _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, too low payable gas price.");
                return AddTxResult.FeeTooLow;
            }

            if (managedNonce && CheckOwnTransactionAlreadyUsed(tx, currentNonce))
            {
                if (_logger.IsTrace)
                    _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, nonce already used.");
                return AddTxResult.OwnNonceAlreadyUsed;
            }
            
            return null;
        }

        private AddTxResult AddCore(Transaction tx, bool isPersistentBroadcast, bool isReorg)
        {
            bool isInHashCache = _hashCache.Get(tx.Hash);
            // !!! do not change it to |=
            bool isKnown = !isReorg && isInHashCache;

            if (!isInHashCache)
            {
                _hashCache.Set(tx.Hash);
            }

            /*
             * we need to make sure that the sender is resolved before adding to the distinct tx pool
             * as the address is used in the distinct value calculation
             */
            if (!isKnown)
            {
                lock (_locker)
                {
                    tx.GasBottleneck = tx.CalculateEffectiveGasPrice(_specProvider.GetSpec().IsEip1559Enabled, CurrentBaseFee);
                    bool inserted = _transactions.TryInsert(tx.Hash, tx, out Transaction? removed);
                    if (inserted)
                    {
                        _transactions.UpdateGroup(tx.SenderAddress, UpdateBucketWithAddedTransaction);
                        ForgetHashOfRemovedTransaction(removed?.Hash);
                    }
                    isKnown |= !inserted;
                }
            }

            if (!isKnown)
            {
                isKnown |= !isReorg && (_txStorage.Get(tx.Hash) is not null);
            }

            if (isKnown)
            {
                // If transaction is a bit older and already known then it may be stored in the persistent storage.
                Metrics.PendingTransactionsKnown++;
                if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, already known.");
                return AddTxResult.AlreadyKnown;
            }
            
            HandleOwnTransaction(tx, isPersistentBroadcast);
            NotifySelectedPeers(tx);
            StoreTx(tx);
            NewPending?.Invoke(this, new TxEventArgs(tx));
            return AddTxResult.Added;
        }

        private void ForgetHashOfRemovedTransaction(Keccak? hash)
        {
            if (hash is not null)
            {
                // transaction which was on last position in sorted TxPool and was deleted to give
                // a place for a newly added tx (with higher priority) is now removed from hashCache
                // to give it opportunity to come back to TxPool in the future, when fees drops
                _hashCache.Delete(hash);
            }
        }

        private IEnumerable<(Transaction Tx, Action<Transaction> Change)> UpdateBucketWithAddedTransaction(Address address, ICollection<Transaction> transactions)
        {
            if (transactions.Count != 0)
            {
                Account? account = _stateProvider.GetAccount(address);
                UInt256 balance = account?.Balance ?? UInt256.Zero;
                long currentNonce = (long)(account?.Nonce ?? UInt256.Zero);

                foreach (var changedTx in UpdateGasBottleneck(transactions, currentNonce, balance))
                {
                    yield return changedTx;
                }
            }
        }

        private IEnumerable<(Transaction Tx, Action<Transaction> Change)> UpdateGasBottleneck(ICollection<Transaction> transactions, long currentNonce, UInt256 balance)
        {
            UInt256 previousTxBottleneck = UInt256.MaxValue;
            int i = 0;
            
            foreach (Transaction tx in transactions)
            {
                UInt256 gasBottleneck = 0;

                if (tx.Nonce < currentNonce)
                {
                    if (tx.GasBottleneck != gasBottleneck)
                    {
                        yield return (tx, SetGasBottleneckChange(gasBottleneck));
                    }
                }
                else
                {
                    if (previousTxBottleneck == UInt256.MaxValue)
                    {
                        previousTxBottleneck = tx.CalculatePayableGasPrice(_specProvider.GetSpec().IsEip1559Enabled, CurrentBaseFee, balance);
                    }

                    if (tx.Nonce == currentNonce + i)
                    {
                        UInt256 effectiveGasPrice = tx.CalculateEffectiveGasPrice(_specProvider.GetSpec().IsEip1559Enabled, CurrentBaseFee);
                        gasBottleneck = UInt256.Min(effectiveGasPrice, previousTxBottleneck);
                    }

                    if (tx.GasBottleneck != gasBottleneck)
                    {
                        yield return (tx, SetGasBottleneckChange(gasBottleneck));
                    }
                
                    previousTxBottleneck = gasBottleneck;
                    i++;
                }
            }
        }

        private static Action<Transaction> SetGasBottleneckChange(UInt256 gasBottleneck)
        {
            return t => t.GasBottleneck = gasBottleneck;
        }

        private void UpdateBuckets()
        {
            lock (_locker)
            {
                _transactions.UpdatePool(UpdateBucket);
            }
        }

        private IEnumerable<(Transaction Tx, Action<Transaction> Change)> UpdateBucket(Address address, ICollection<Transaction> transactions)
        {
            if (transactions.Count != 0)
            {
                Account? account = _stateProvider.GetAccount(address);
                UInt256 balance = account?.Balance ?? UInt256.Zero;
                long currentNonce = (long)(account?.Nonce ?? UInt256.Zero);
                Transaction tx = transactions.FirstOrDefault(t => t.Nonce == currentNonce);
                bool shouldBeDumped = false;
                
                if (tx is null)
                {
                    shouldBeDumped = true;
                }
                else if (balance < tx.Value)
                {
                    shouldBeDumped = true;
                }
                else if (!tx.IsEip1559)
                {
                    shouldBeDumped = UInt256.MultiplyOverflow(tx.GasPrice, (UInt256)tx.GasLimit, out UInt256 cost);
                    shouldBeDumped |= UInt256.AddOverflow(cost, tx.Value, out cost);
                    shouldBeDumped |= balance < cost;
                }
                
                if (shouldBeDumped)
                {
                    foreach (Transaction transaction in transactions)
                    {
                        yield return (transaction, SetGasBottleneckChange(0));
                    }
                }
                else
                {
                    foreach (var changedTx in UpdateGasBottleneck(transactions, currentNonce, balance))
                    {
                        yield return changedTx;
                    }
                }
            }
        }

        private void HandleOwnTransaction(Transaction tx, bool isOwn)
        {
            if (isOwn)
            {
                lock (_locker)
                {
                    _persistentBroadcastTransactions.TryInsert(tx.Hash, tx);
                }

                _ownTimer.Enabled = true;
                if (_logger.IsDebug) _logger.Debug($"Broadcasting own transaction {tx.Hash} to {_peers.Count} peers");
                if (_logger.IsTrace) _logger.Trace($"Broadcasting transaction {tx.ToString("  ")}");
            }
        }

        private bool CheckOwnTransactionAlreadyUsed(Transaction transaction, UInt256 currentNonce)
        {
            Address address = transaction.SenderAddress;
            lock (_locker)
            {
                if (!_nonces.TryGetValue(address, out var addressNonces))
                {
                    addressNonces = new AddressNonces(currentNonce);
                    _nonces.TryAdd(address, addressNonces);
                }

                if (!addressNonces.Nonces.TryGetValue(transaction.Nonce, out var nonce))
                {
                    nonce = new Nonce(transaction.Nonce);
                    addressNonces.Nonces.TryAdd(transaction.Nonce, new Nonce(transaction.Nonce));
                }

                if (!(nonce.TransactionHash is null && nonce.TransactionHash != transaction.Hash))
                {
                    // Nonce conflict
                    if (_logger.IsDebug)
                        _logger.Debug(
                            $"Nonce: {nonce.Value} was already used in transaction: '{nonce.TransactionHash}' and cannot be reused by transaction: '{transaction.Hash}'.");

                    return true;
                }

                nonce.SetTransactionHash(transaction.Hash!);
            }

            return false;
        }
        
        public bool RemoveTransaction(Keccak? hash)
        {
            if (hash is null)
            {
                return false;
            }
            
            bool isKnown;
            lock (_locker)
            {
                isKnown = _transactions.TryRemove(hash, out Transaction transaction);
                if (isKnown)
                {
                    Address address = transaction.SenderAddress;
                    if (_nonces.TryGetValue(address, out AddressNonces addressNonces))
                    {
                        addressNonces.Nonces.TryRemove(transaction.Nonce, out _);
                        if (addressNonces.Nonces.IsEmpty)
                        {
                            _nonces.Remove(address, out _);
                        }
                    }
                    
                    RemovedPending?.Invoke(this, new TxEventArgs(transaction));
                }
                
                if (_persistentBroadcastTransactions.Count != 0)
                {
                    bool ownIncluded = _persistentBroadcastTransactions.TryRemove(hash, out Transaction _);
                    if (ownIncluded)
                    {
                        if (_logger.IsInfo)
                            _logger.Trace($"Transaction {hash} created on this node was included in the block");
                    }
                }
            }

            _txStorage.Delete(hash);
            if (_logger.IsTrace) _logger.Trace($"Deleted a transaction: {hash}");
            
            return isKnown;
        }

        public bool IsKnown(Keccak hash)
        {
            return _hashCache.Get(hash);
        }

        public bool TryGetPendingTransaction(Keccak hash, out Transaction transaction)
        {
            lock (_locker)
            {
                if (!_transactions.TryGetValue(hash, out transaction))
                {
                    // commented out as it puts too much pressure on the database
                    // and it not really required in any scenario
                    // * tx recovery usually will fetch from pending
                    // * get tx via RPC usually will fetch from block or from pending
                    // * internal tx pool scenarios are handled directly elsewhere
                    // transaction = _txStorage.Get(hash);
                }
            }

            return transaction != null;
        }

        // TODO: Ensure that nonce is always valid in case of sending own transactions from different nodes.
        public UInt256 ReserveOwnTransactionNonce(Address address)
        {
            lock (_locker)
            {
                if (!_nonces.TryGetValue(address, out var addressNonces))
                {
                    var currentNonce = _stateProvider.GetNonce(address);
                    addressNonces = new AddressNonces(currentNonce);
                    _nonces.TryAdd(address, addressNonces);

                    return currentNonce;
                }

                Nonce incrementedNonce = addressNonces.ReserveNonce();

                return incrementedNonce.Value;
            }
        }

        public void Dispose()
        {
            _ownTimer.Dispose();
            _chainHeadInfoProvider.HeadChanged -= OnHeadChange;
        }

        public event EventHandler<TxEventArgs>? NewDiscovered;
        public event EventHandler<TxEventArgs>? NewPending;
        public event EventHandler<TxEventArgs>? RemovedPending;
        
        private void Notify(ITxPoolPeer peer, Transaction tx, bool isPriority)
        {
            try
            {
                if (peer.SendNewTransaction(tx, isPriority))
                {
                    Metrics.PendingTransactionsSent++;
                    if (_logger.IsTrace) _logger.Trace($"Notified {peer} about a transaction: {tx.Hash}");
                }
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"Failed to notify {peer} about a transaction: {tx.Hash}", e);
            }
        }

        private void NotifyAllPeers(Transaction tx)
        {
            Task.Run(() =>
            {
                foreach ((_, ITxPoolPeer peer) in _peers)
                {
                    Notify(peer, tx, true);
                }
            });
        }

        private void NotifySelectedPeers(Transaction tx)
        {
            Task.Run(() =>
            {
                foreach ((_, ITxPoolPeer peer) in _peers)
                {
                    if (tx.DeliveredBy == null)
                    {
                        Notify(peer, tx, true);
                        continue;
                    }

                    if (tx.DeliveredBy.Equals(peer.Id))
                    {
                        continue;
                    }

                    if (_txPoolConfig.PeerNotificationThreshold < Random.Value.Next(1, 100))
                    {
                        continue;
                    }

                    Notify(peer, tx, 3 < Random.Value.Next(1, 10));
                }
            });
        }

        private void StoreTx(Transaction tx)
        {
            _txStorage.Add(tx);
            if (_logger.IsTrace) _logger.Trace($"Added a transaction: {tx.Hash}");
        }

        private void OwnTimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            if (_persistentBroadcastTransactions.Count > 0)
            {
                foreach (Transaction tx in _persistentBroadcastTransactions.GetSnapshot())
                {
                    NotifyAllPeers(tx);
                }

                // we only reenable the timer if there are any transaction pending
                // otherwise adding own transaction will reenable the timer anyway
                _ownTimer.Enabled = true;
            }
        }

        private class AddressNonces
        {
            private Nonce _currentNonce;

            public ConcurrentDictionary<UInt256, Nonce> Nonces { get; } = new();

            public AddressNonces(UInt256 startNonce)
            {
                _currentNonce = new Nonce(startNonce);
                Nonces.TryAdd(_currentNonce.Value, _currentNonce);
            }

            public Nonce ReserveNonce()
            {
                var nonce = _currentNonce.Increment();
                Interlocked.Exchange(ref _currentNonce, nonce);
                Nonces.TryAdd(nonce.Value, nonce);

                return nonce;
            }
        }

        private class Nonce
        {
            public UInt256 Value { get; }
            public Keccak? TransactionHash { get; private set; }

            public Nonce(UInt256 value)
            {
                Value = value;
            }

            public void SetTransactionHash(Keccak transactionHash)
            {
                TransactionHash = transactionHash;
            }

            public Nonce Increment() => new(Value + 1);
        }

        private class PeerInfo : ITxPoolPeer
        {
            private ITxPoolPeer Peer { get; }

            private LruKeyCache<Keccak> NotifiedTransactions { get; } = new(MemoryAllowance.MemPoolSize, "notifiedTransactions");

            public PeerInfo(ITxPoolPeer peer)
            {
                Peer = peer;
            }

            public PublicKey Id => Peer.Id;

            public bool SendNewTransaction(Transaction tx, bool isPriority)
            {
                if (!NotifiedTransactions.Get(tx.Hash))
                {
                    NotifiedTransactions.Set(tx.Hash);
                    return Peer.SendNewTransaction(tx, isPriority);                     
                }

                return false;
            }

            public override string ToString() => Peer.Enode;
        }
    }
}
