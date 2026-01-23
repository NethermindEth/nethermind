// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// Go through the trie and check for the corresponding value in flat.
/// </summary>
public class FlatVerifyTrieVisitor : ITreeVisitor<FlatVerifyTrieVisitor.Context>
{
    private readonly ClockCache<ValueHash256, int> _existingCodeHash = new ClockCache<ValueHash256, int>(1024 * 8);
    private readonly IKeyValueStore _codeKeyValueStore;
    private readonly VisitorProgressTracker _progressTracker;

    private readonly ILogger _logger;
    private readonly CancellationToken _cancellationToken;
    private readonly IPersistence.IPersistenceReader _persistenceReader;

    // Combine both `TreePathContextWithStorage` and `OldStyleTrieVisitContext`
    public struct Context : INodeContext<Context>
    {
        private TreePathContextWithStorage PathContext;
        private OldStyleTrieVisitContext OldStyleTrieVisitContext;

        public readonly Hash256? Storage => PathContext.Storage;
        public readonly TreePath Path => PathContext.Path;
        public readonly bool IsStorage => OldStyleTrieVisitContext.IsStorage;
        public readonly int Level => OldStyleTrieVisitContext.Level;

        public readonly Context Add(ReadOnlySpan<byte> nibblePath)
        {
            return new Context()
            {
                PathContext = PathContext.Add(nibblePath),
                OldStyleTrieVisitContext = OldStyleTrieVisitContext.Add(nibblePath)
            };
        }

        public readonly Context Add(byte nibble)
        {
            return new Context()
            {
                PathContext = PathContext.Add(nibble),
                OldStyleTrieVisitContext = OldStyleTrieVisitContext.Add(nibble)
            };
        }

        public readonly Context AddStorage(in ValueHash256 storage)
        {
            return new Context()
            {
                PathContext = PathContext.AddStorage(storage),
                OldStyleTrieVisitContext = OldStyleTrieVisitContext.AddStorage(storage)
            };
        }
    }

    public bool ExpectAccounts => true;

    public FlatVerifyTrieVisitor(
        IKeyValueStore codeKeyValueStore,
        IPersistence.IPersistenceReader persistenceReader,
        ILogManager logManager,
        CancellationToken cancellationToken = default)
    {
        _persistenceReader = persistenceReader;
        _codeKeyValueStore = codeKeyValueStore ?? throw new ArgumentNullException(nameof(codeKeyValueStore));
        _logger = logManager.GetClassLogger();
        _cancellationToken = cancellationToken;
        _progressTracker = new VisitorProgressTracker("Trie->Flat Verify", logManager);
    }

    public TrieStats Stats { get; } = new();

    public bool IsFullDbScan => true;
    public void VisitTree(in Context nodeContext, in ValueHash256 rootHash)
    {
    }

    public bool ShouldVisit(in Context nodeContext, in ValueHash256 nextNode)
    {
        return true;
    }

    public void VisitMissingNode(in Context nodeContext, in ValueHash256 nodeHash)
    {
        if (nodeContext.IsStorage)
        {
            if (_logger.IsWarn) _logger.Warn($"Missing node. Storage: {nodeContext.Storage} Path: {nodeContext.Path} Hash: {nodeHash}");
            Interlocked.Increment(ref Stats._missingStorage);
        }
        else
        {
            if (_logger.IsWarn) _logger.Warn($"Missing node. Path: {nodeContext.Path} Hash: {nodeHash}");
            Interlocked.Increment(ref Stats._missingState);
        }

        IncrementLevel(nodeContext, isLeaf: false);
    }

    public void VisitBranch(in Context nodeContext, TrieNode node)
    {
        _cancellationToken.ThrowIfCancellationRequested();

        if (nodeContext.IsStorage)
        {
            Interlocked.Add(ref Stats._storageSize, node.FullRlp.Length);
            Interlocked.Increment(ref Stats._storageBranchCount);
        }
        else
        {
            Interlocked.Add(ref Stats._stateSize, node.FullRlp.Length);
            Interlocked.Increment(ref Stats._stateBranchCount);
        }

        IncrementLevel(nodeContext, isLeaf: false);
    }

    public void VisitExtension(in Context nodeContext, TrieNode node)
    {
        if (nodeContext.IsStorage)
        {
            Interlocked.Add(ref Stats._storageSize, node.FullRlp.Length);
            Interlocked.Increment(ref Stats._storageExtensionCount);
        }
        else
        {
            Interlocked.Add(ref Stats._stateSize, node.FullRlp.Length);
            Interlocked.Increment(ref Stats._stateExtensionCount);
        }

        IncrementLevel(nodeContext, isLeaf: false);
    }

    public void VisitLeaf(in Context nodeContext, TrieNode node)
    {
        if (nodeContext.IsStorage)
        {
            Interlocked.Add(ref Stats._storageSize, node.FullRlp.Length);
            Interlocked.Increment(ref Stats._storageLeafCount);

            Hash256 fullPath = nodeContext.Path.Append(node.Key).Path.ToHash256();
            byte[]? nodeSlot = State.StorageTree.ZeroBytes;
            if (node.Value.IsNotNull)
            {
                Rlp.ValueDecoderContext ctx = node.Value.Span.AsRlpValueContext();
                nodeSlot = ctx.DecodeByteArray();
            }

            byte[]? flatSlot = _persistenceReader.GetStorageRaw(nodeContext.Storage!, fullPath);
            if (!Bytes.AreEqual(flatSlot, nodeSlot))
            {
                if (_logger.IsWarn) _logger.Warn($"Mismatched slot. AddressHash: {nodeContext.Storage}. SlotHash {fullPath}. Trie slot: {nodeSlot.ToHexString() ?? ""}, Flat slot; {flatSlot?.ToHexString()}");
                Interlocked.Increment(ref Stats._mismatchedSlot);
            }

        }
        else
        {
            Interlocked.Add(ref Stats._stateSize, node.FullRlp.Length);
            Interlocked.Increment(ref Stats._accountCount);

            Hash256 addrHash = nodeContext.Path.Append(node.Key).Path.ToHash256();
            byte[]? rawAccountBytes = _persistenceReader.GetAccountRaw(addrHash);
            Rlp.ValueDecoderContext ctx = new Rlp.ValueDecoderContext(rawAccountBytes);
            Account? flatAccount = AccountDecoder.Instance.Decode(ref ctx);

            ctx = node.Value.Span.AsRlpValueContext();
            Account? nodeAccount = AccountDecoder.Instance.Decode(ref ctx);

            if (nodeAccount != flatAccount)
            {
                if (_logger.IsWarn) _logger.Warn($"Mismatched account. AddressHash: {addrHash}. Trie account: {nodeAccount}, Flat account; {flatAccount}");
                Interlocked.Increment(ref Stats._mismatchedAccount);
            }
        }

        IncrementLevel(nodeContext, isLeaf: true);
    }

    public void VisitAccount(in Context nodeContext, TrieNode node, in AccountStruct account)
    {
        if (!account.HasCode) return;
        ValueHash256 key = account.CodeHash;
        bool codeExist = _existingCodeHash.TryGet(key, out int codeLength);
        if (!codeExist)
        {
            byte[]? code = _codeKeyValueStore[key.Bytes];
            codeExist = code is not null;
            if (codeExist)
            {
                codeLength = code!.Length;
                _existingCodeHash.Set(key, codeLength);
            }
        }

        if (codeExist)
        {
            Interlocked.Add(ref Stats._codeSize, codeLength);
            Interlocked.Increment(ref Stats._codeCount);
        }
        else
        {
            if (_logger.IsWarn) _logger.Warn($"Missing code. Hash: {account.CodeHash}");
            Interlocked.Increment(ref Stats._missingCode);
        }

        IncrementLevel(nodeContext, Stats._codeLevels);
    }

    private void IncrementLevel(Context context, bool isLeaf)
    {
        long[] levels = context.IsStorage ? Stats._storageLevels : Stats._stateLevels;
        IncrementLevel(context, levels);

        // Track all nodes for display; only state nodes used for progress calculation
        _progressTracker.OnNodeVisited(context.Path, context.IsStorage, isLeaf);
    }

    private static void IncrementLevel(Context context, long[] levels)
    {
        Interlocked.Increment(ref levels[context.Level]);
    }

    public void Finish()
    {
        _progressTracker.Finish();
    }

    public class TrieStats
    {
        private const int Levels = 128;
        internal long _stateBranchCount;
        internal long _stateExtensionCount;
        internal long _accountCount;
        internal long _storageBranchCount;
        internal long _storageExtensionCount;
        internal long _storageLeafCount;
        internal long _codeCount;
        internal long _missingState;
        internal long _missingCode;
        internal long _missingStorage;
        internal long _mismatchedAccount;
        internal long _mismatchedSlot;
        internal long _storageSize;
        internal long _codeSize;
        internal long _stateSize;
        internal readonly long[] _stateLevels = new long[Levels];
        internal readonly long[] _storageLevels = new long[Levels];
        internal readonly long[] _codeLevels = new long[Levels];

        public long StateBranchCount => _stateBranchCount;

        public long StateExtensionCount => _stateExtensionCount;

        public long AccountCount => _accountCount;

        public long StorageBranchCount => _storageBranchCount;

        public long StorageExtensionCount => _storageExtensionCount;

        public long StorageLeafCount => _storageLeafCount;

        public long CodeCount => _codeCount;

        public long MissingState => _missingState;

        public long MissingCode => _missingCode;

        public long MissingStorage => _missingStorage;

        public long MismatchedSlot => _mismatchedSlot;

        public long MismatchedAccount => _mismatchedAccount;

        public long MissingNodes => MissingCode + MissingState + MissingStorage;

        public long StorageCount => StorageLeafCount + StorageExtensionCount + StorageBranchCount;

        public long StateCount => AccountCount + StateExtensionCount + StateBranchCount;

        public long NodesCount => StorageCount + StateCount + CodeCount;

        public long StorageSize => _storageSize;

        public long CodeSize => _codeSize;

        public long StateSize => _stateSize;

        public long Size => StateSize + StorageSize + CodeSize;

        public long[] StateLevels => _stateLevels;
        public long[] StorageLevels => _storageLevels;
        public long[] CodeLevels => _codeLevels;
        public long[] AllLevels
        {
            get
            {
                long[] result = new long[Levels];
                for (int i = 0; i < result.Length; i++)
                {
                    result[i] = _stateLevels[i] + _storageLevels[i] + _codeLevels[i];
                }

                return result;
            }
        }

        public override string ToString()
        {
            StringBuilder builder = new();
            builder.AppendLine("TRIE STATS");
            builder.AppendLine($"  SIZE {Size} (STATE {StateSize}, CODE {CodeSize}, STORAGE {StorageSize})");
            builder.AppendLine($"  ALL NODES {NodesCount} ({StateBranchCount + StorageBranchCount}|{StateExtensionCount + StorageExtensionCount}|{AccountCount + StorageLeafCount})");
            builder.AppendLine($"  STATE NODES {StateCount} ({StateBranchCount}|{StateExtensionCount}|{AccountCount})");
            builder.AppendLine($"  STORAGE NODES {StorageCount} ({StorageBranchCount}|{StorageExtensionCount}|{StorageLeafCount})");
            builder.AppendLine($"  ACCOUNTS {AccountCount} OF WHICH ({CodeCount}) ARE CONTRACTS");
            builder.AppendLine($"  MISSING {MissingNodes} (STATE {MissingState}, CODE {MissingCode}, STORAGE {MissingStorage})");
            builder.AppendLine($"  MISMATCHED (ACCOUNT {MismatchedAccount}) (SLOT {MismatchedSlot})");
            builder.AppendLine($"  ALL LEVELS {string.Join(" | ", AllLevels)}");
            builder.AppendLine($"  STATE LEVELS {string.Join(" | ", StateLevels)}");
            builder.AppendLine($"  STORAGE LEVELS {string.Join(" | ", StorageLevels)}");
            builder.AppendLine($"  CODE LEVELS {string.Join(" | ", CodeLevels)}");
            return builder.ToString();
        }
    }
}
