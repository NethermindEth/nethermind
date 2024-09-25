// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System.Diagnostics;
using System.Threading.Tasks.Dataflow;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.VerkleMigration.Cli;

public sealed class VerkleTreeMigrator(
    VerkleStateTree verkleStateTree,
    IStateReader stateReader,
    ILogManager logManager,
    IDb? preImageDb = null,
    EventHandler<VerkleTreeMigrator.ProgressEventArgs>? progressChanged = null)
    : ITreeVisitor<TreePathContext>
{
    private const int StateTreeCommitThreshold = 10000;

    private readonly Dictionary<Address, Account> _accountChange = new();

    private readonly AccountDecoder _decoder = new();
    private readonly ILogger _logger = logManager?.GetClassLogger<VerkleTreeMigrator>()
                                       ?? throw new ArgumentNullException(nameof(logManager));

    private readonly Dictionary<StorageCell, byte[]> _toSetStorage = new();
    public readonly VerkleStateTree VerkleStateTree = verkleStateTree;
    private Account? _lastAccount;
    private Address? _lastAddress;
    private DateTime _lastUpdateTime = DateTime.UtcNow;
    private int _leafNodeCounter;


    private ActionBlock<KeyValuePair<Address, Account>>? _setStateAction;
    private ActionBlock<KeyValuePair<StorageCell, byte[]>>? _setStorageAction;

    public bool IsFullDbScan => true;

    public bool ShouldVisit(in TreePathContext ctx, Hash256 nextNode)
    {
        return true;
    }

    public void VisitTree(in TreePathContext nodeContext, Hash256 rootHash, TrieVisitContext trieVisitContext)
    {
        _logger.Debug($"Starting migration from Merkle tree with root: {rootHash}");
        _lastAddress = null;
        _lastAccount = null;
    }

    public void VisitMissingNode(in TreePathContext nodeContext, Hash256 nodeHash, TrieVisitContext trieVisitContext)
    {
        _logger.Warn($"Warning: Missing node encountered: {nodeHash}");
    }

    public void VisitBranch(in TreePathContext nodeContext, TrieNode node, TrieVisitContext trieVisitContext)
    {
    }

    public void VisitExtension(in TreePathContext nodeContext, TrieNode node, TrieVisitContext trieVisitContext)
    {
    }

    public void VisitLeaf(in TreePathContext nodeContext, TrieNode node, TrieVisitContext trieVisitContext,
        ReadOnlySpan<byte> value)
    {
        TreePath path = nodeContext.Path.Append(node.Key);
        Span<byte> pathBytes = path.Path.BytesAsSpan;

        (var progress, Address? currentAddress) = CalculateProgress(pathBytes);
        DateTime now = DateTime.UtcNow;
        TimeSpan timeSinceLastUpdate = now - _lastUpdateTime;
        _lastUpdateTime = now;

        if (!trieVisitContext.IsStorage)
        {
            var nodeValueBytes = node.Value.ToArray();
            if (nodeValueBytes is null)
                return;
            Account? account = _decoder.Decode(new RlpStream(nodeValueBytes));
            if (account is null)
                return;

            // Reconstruct the full keccak hash
            var addressBytes = RetrievePreimage(pathBytes);
            if (addressBytes is not null)
            {
                var address = new Address(addressBytes);

                // Update code size if account has code
                if (account.HasCode)
                {
                    var code = stateReader.GetCode(account.CodeHash);
                    if (code is not null)
                    {
                        account.CodeSize = (UInt256)code.Length;
                        MigrateContractCode(address, code);
                    }
                }

                MigrateAccount(address, account);

                _lastAddress = address;
                _lastAccount = account;
            }
        }
        else
        {
            if (_lastAddress is null || _lastAccount is null)
            {
                _logger.Warn($"No address or account detected for storage node: {node}");
                return;
            }

            // Reconstruct the full keccak hash
            var storageSlotBytes = RetrievePreimage(pathBytes);
            if (storageSlotBytes is null)
            {
                _logger.Warn($"Storage slot is null for node: {node} with key: {pathBytes.ToHexString()}");
                return;
            }

            UInt256 storageSlot = new(storageSlotBytes);
            var storageValue = value.ToArray();
            MigrateAccountStorage(_lastAddress, storageSlot, storageValue);
        }

        CommitIfThresholdReached(progress, timeSinceLastUpdate, currentAddress, trieVisitContext.IsStorage);
    }


    public void VisitCode(in TreePathContext nodeContext, Hash256 codeHash, TrieVisitContext trieVisitContext)
    {
    }

    public event EventHandler<ProgressEventArgs>? ProgressChanged = progressChanged;

    private void BulkSetStorage(Dictionary<StorageCell, byte[]> storageChange)
    {
        void SetStateKV(KeyValuePair<StorageCell, byte[]> keyValuePair)
        {
            VerkleStateTree.SetStorage(keyValuePair.Key, keyValuePair.Value);
        }

        if (storageChange.Count == 1)
        {
            foreach (KeyValuePair<StorageCell, byte[]> keyValuePair in storageChange) SetStateKV(keyValuePair);

            return;
        }

        _setStorageAction = new ActionBlock<KeyValuePair<StorageCell, byte[]>>(
            SetStateKV,
            new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount
            });

        foreach (KeyValuePair<StorageCell, byte[]> keyValuePair in storageChange) _setStorageAction.Post(keyValuePair);


        _setStorageAction.Complete();
    }

    private void BulkSet(Dictionary<Address, Account> accountChange)
    {
        void SetStateKV(KeyValuePair<Address, Account> keyValuePair)
        {
            VerkleStateTree.Set(keyValuePair.Key, keyValuePair.Value);
        }

        if (accountChange.Count == 1)
        {
            foreach (KeyValuePair<Address, Account> keyValuePair in accountChange) SetStateKV(keyValuePair);

            return;
        }

        _setStateAction = new ActionBlock<KeyValuePair<Address, Account>>(
            SetStateKV,
            new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount
            });


        foreach (KeyValuePair<Address, Account> keyValuePair in accountChange) _setStateAction.Post(keyValuePair);

        _setStateAction.Complete();
    }

    private void CommitTree()
    {
        var watch = Stopwatch.StartNew();
        _setStateAction?.Completion.Wait();
        _setStorageAction?.Completion.Wait();
        TimeSpan timeToCompletePrevCommit = watch.Elapsed;
        watch.Restart();
        VerkleStateTree.Commit();
        TimeSpan timeToCommit = watch.Elapsed;
        watch.Restart();
        VerkleStateTree.CommitTree(0);
        TimeSpan timeToCommitTree = watch.Elapsed;
        watch.Restart();
        BulkSet(_accountChange);
        TimeSpan timeToBulkSetAccount = watch.Elapsed;
        watch.Restart();
        BulkSetStorage(_toSetStorage);
        TimeSpan timeToBulkSetStorage = watch.Elapsed;
        _logger.Info(
            $"timeToCompletePrevCommit:{timeToCompletePrevCommit} timeToBulkSetAccount:{_accountChange.Count}:{timeToBulkSetAccount} timeToBulkSetStorage:{_toSetStorage.Count}:{timeToBulkSetStorage} timeToCommit:{timeToCommit} timeToCommitTree:{timeToCommitTree}");
        _accountChange.Clear();
        _toSetStorage.Clear();
    }

    private void CommitIfThresholdReached(decimal progress, TimeSpan timeSinceLastUpdate, Address? currentAddress,
        bool isStorage)
    {
        _leafNodeCounter++;
        if (_leafNodeCounter >= StateTreeCommitThreshold)
        {
            OnProgressChanged(progress, timeSinceLastUpdate, currentAddress, isStorage);
            CommitTree();
            _leafNodeCounter = 0;
        }
    }

    private byte[]? RetrievePreimage(Span<byte> key)
    {
        if (preImageDb is null)
            return key[..20].ToArray();
        return preImageDb.Get(key);
    }


    private void MigrateAccount(Address address, Account account)
    {
        _accountChange[address] = account;
        // _verkleStateTree.Set(address, account);
    }

    private void MigrateContractCode(Address address, byte[] code)
    {
        // TODO: need to add code migration as well
        // _verkleStateTree.SetCode(address, code);
    }

    private void MigrateAccountStorage(Address address, UInt256 index, byte[] value)
    {
        var storageKey = new StorageCell(address, index);
        _toSetStorage[storageKey] = value;
        // _verkleStateTree.SetStorage(storageKey, value);
    }

    public void FinalizeMigration(long blockNumber)
    {
        // Commit any remaining changes
        if (_leafNodeCounter > 0)
            CommitTree();
        VerkleStateTree.CommitTree(blockNumber);

        // Ensure we report 100% progress at the end
        DateTime now = DateTime.UtcNow;
        TimeSpan timeSinceLastUpdate = now - _lastUpdateTime;
        OnProgressChanged(100, timeSinceLastUpdate, null, false);

        _logger.Info("Migration completed");
    }

    private static (decimal Progress, Address? CurrentAddress) CalculateProgress(Span<byte> prefix)
    {
        var maxValue = new UInt256(Bytes.FromHexString("0xffffffffffffffffffffffffffffffffffffffff").AsSpan());
        var currentValue = new UInt256(prefix[..20]);
        currentValue.Multiply(10000, out UInt256 progress);
        progress.Divide(maxValue, out UInt256 progressValue);
        var calculatedProgress = progressValue.ToDecimal(null) / 100;

        Address? currentAddress = null;
        if (prefix.Length >= 20) currentAddress = new Address(prefix[..20].ToArray());
        return (calculatedProgress, currentAddress);
    }

    private void OnProgressChanged(decimal progress, TimeSpan timeSinceLastUpdate, Address? currentAddress,
        bool isStorage)
    {
        ProgressChanged?.Invoke(this, new ProgressEventArgs(progress, timeSinceLastUpdate, currentAddress, isStorage));
    }

    public class ProgressEventArgs : EventArgs
    {
        public ProgressEventArgs(decimal progress, TimeSpan timeSinceLastUpdate, Address? currentAddress,
            bool isStorage)
        {
            Progress = progress;
            TimeSinceLastUpdate = timeSinceLastUpdate;
            CurrentAddress = currentAddress;
            IsStorage = isStorage;
        }

        public decimal Progress { get; }
        public TimeSpan TimeSinceLastUpdate { get; }
        public Address? CurrentAddress { get; }
        public bool IsStorage { get; }
    }
}
