// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Int256;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.State;
using Nethermind.Logging;

namespace Nethermind.VerkleMigration.Cli;

public class VerkleTreeMigrator : ITreeVisitor<TreePathContext>
{
    public readonly VerkleStateTree _verkleStateTree;
    private readonly IStateReader _stateReader;
    private readonly IDb? _preImageDb;
    private Address? _lastAddress;
    private Account? _lastAccount;
    private readonly ILogger _logger;
    private int _leafNodeCounter = 0;

    public event EventHandler<ProgressEventArgs>? _progressChanged;


    public class ProgressEventArgs : EventArgs
    {
        public decimal Progress { get; }

        public ProgressEventArgs(decimal progress)
        {
            Progress = progress;
        }
    }


    private const int StateTreeCommitThreshold = 1000;

    public VerkleTreeMigrator(VerkleStateTree verkleStateTree, IStateReader stateReader, ILogManager logManager, IDb? preImageDb = null, EventHandler<ProgressEventArgs>? progressChanged = null)
    {
        _verkleStateTree = verkleStateTree;
        _stateReader = stateReader;
        _preImageDb = preImageDb;
        _progressChanged = progressChanged;
        _logger = logManager?.GetClassLogger<VerkleTreeMigrator>()
            ?? throw new ArgumentNullException(nameof(logManager));
    }

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

    private readonly AccountDecoder decoder = new();

    public void VisitLeaf(in TreePathContext nodeContext, TrieNode node, TrieVisitContext trieVisitContext, ReadOnlySpan<byte> value)
    {
        TreePath path = nodeContext.Path.Append(node.Key);
        Span<byte> pathBytes = path.Path.BytesAsSpan;

        var progress = CalculateProgress(pathBytes);
        OnProgressChanged(progress);

        if (!trieVisitContext.IsStorage)
        {
            var nodeValueBytes = node.Value.ToArray();
            if (nodeValueBytes is null)
                return;
            Account? account = decoder.Decode(new RlpStream(nodeValueBytes));
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
                    var code = _stateReader.GetCode(account.CodeHash);
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

        CommitIfThresholdReached();
    }

    private void CommitIfThresholdReached()
    {
        _leafNodeCounter++;
        if (_leafNodeCounter >= StateTreeCommitThreshold)
        {
            _verkleStateTree.Commit();
            _leafNodeCounter = 0;
        }
    }


    public void VisitCode(in TreePathContext nodeContext, Hash256 codeHash, TrieVisitContext trieVisitContext) { }

    private byte[]? RetrievePreimage(Span<byte> key)
    {
        if (_preImageDb is null)
        {
            return key[..20].ToArray();
        }
        else
        {
            return _preImageDb.Get(key);
        }
    }


    private void MigrateAccount(Address address, Account account)
    {
        _verkleStateTree.Set(address, account);
    }

    private void MigrateContractCode(Address address, byte[] code)
    {
        _verkleStateTree.SetCode(address, code);
    }

    private void MigrateAccountStorage(Address address, UInt256 index, byte[] value)
    {
        var storageKey = new StorageCell(address, index);
        _verkleStateTree.SetStorage(storageKey, value);
    }

    public void FinalizeMigration(long blockNumber)
    {
        // Commit any remaining changes
        if (_leafNodeCounter > 0)
            _verkleStateTree.Commit();
        _verkleStateTree.CommitTree(blockNumber);

        // Ensure we report 100% progress at the end
        OnProgressChanged(100);

        _logger.Info($"Migration completed");
    }

    private static decimal CalculateProgress(Span<byte> prefix)
    {
        var maxValue = new UInt256(Bytes.FromHexString("0xffffffffffffffffffffffffffffffffffffffff").AsSpan());
        var currentValue = new UInt256(prefix);
        currentValue.Multiply(10000, out UInt256 progress);
        progress.Divide(maxValue, out UInt256 progressValue);
        return progressValue.ToDecimal(null) / 100;
    }

    protected virtual void OnProgressChanged(decimal progress)
    {
        _progressChanged?.Invoke(this, new ProgressEventArgs(progress));
    }
}
