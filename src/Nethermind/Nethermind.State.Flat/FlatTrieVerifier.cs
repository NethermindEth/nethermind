// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat;

/// <summary>
/// Verifier for flat DB against trie state.
/// - Hashed mode: Single-pass co-iteration (flat and trie share same sort order)
/// - Preimage mode: Two-pass verification using PatriciaTree.Get() directly
/// </summary>
public class FlatTrieVerifier
{
    private const int StorageChannelCapacity = 16;
    private const int FlatKeyLength = 20;

    private readonly IFlatDbManager? _flatDbManager;
    private readonly IPersistence? _persistence;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;

    private long _accountCount;
    private long _slotCount;
    private long _mismatchedAccount;
    private long _mismatchedSlot;
    private long _missingInFlat;
    private long _missingInTrie;

    public FlatTrieVerifier(IFlatDbManager flatDbManager, IPersistence persistence, ILogManager logManager)
    {
        _flatDbManager = flatDbManager;
        _persistence = persistence;
        _logManager = logManager;
        _logger = logManager.GetClassLogger<FlatTrieVerifier>();
    }

    // Internal constructor for testing
    internal FlatTrieVerifier(ILogManager logManager)
    {
        _logManager = logManager;
        _logger = logManager.GetClassLogger<FlatTrieVerifier>();
    }

    public VerificationStats Stats => new(
        Interlocked.Read(ref _accountCount),
        Interlocked.Read(ref _slotCount),
        Interlocked.Read(ref _mismatchedAccount),
        Interlocked.Read(ref _mismatchedSlot),
        Interlocked.Read(ref _missingInFlat),
        Interlocked.Read(ref _missingInTrie));

    public bool Verify(BlockHeader stateAtBlock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(_persistence);
        ArgumentNullException.ThrowIfNull(_flatDbManager);

        StateId stateId = new StateId(stateAtBlock);
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        if (reader.CurrentState != stateId)
        {
            _logger.Warn($"With flat, only the persisted state can be verified. Will use current persisted state: {reader.CurrentState}");
            stateId = reader.CurrentState;
        }

        using ReadOnlySnapshotBundle bundle = _flatDbManager.GatherReadOnlySnapshotBundle(stateId);
        ReadOnlyStateTrieStoreAdapter trieStore = new(bundle);
        Hash256 stateRoot = stateAtBlock.StateRoot!;

        return VerifyCore(reader, trieStore, stateRoot, cancellationToken);
    }

    // Internal method for testing with direct components
    internal void Verify(IPersistence.IPersistenceReader reader, IScopedTrieStore trieStore, Hash256 stateRoot, CancellationToken cancellationToken)
    {
        VerifyCore(reader, trieStore, stateRoot, cancellationToken);
    }

    private bool VerifyCore(IPersistence.IPersistenceReader reader, IScopedTrieStore trieStore, Hash256 stateRoot, CancellationToken cancellationToken)
    {
        HashVerifyingTrieStore verifyingTrieStore = new(trieStore, _logger);
        VisitorProgressTracker progressTracker = new("Verify flat", _logManager, printNodes: false);

        Channel<StorageVerificationJob> channel = Channel.CreateBounded<StorageVerificationJob>(
            new BoundedChannelOptions(StorageChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait
            });

        int workerCount = Math.Max(1, Environment.ProcessorCount - 1);
        Task[] workers = new Task[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            workers[i] = Task.Run(() => ProcessStorageQueue(channel.Reader, reader, verifyingTrieStore, cancellationToken));
        }

        try
        {
            if (reader.IsPreimageMode)
            {
                VerifyPreimageMode(reader, verifyingTrieStore, stateRoot, channel.Writer, progressTracker, cancellationToken);
            }
            else
            {
                VerifyHashedMode(reader, verifyingTrieStore, stateRoot, channel.Writer, progressTracker, cancellationToken);
            }
        }
        finally
        {
            channel.Writer.Complete();
            Task.WaitAll(workers);
            progressTracker.Finish();
        }

        if (verifyingTrieStore.HashMismatchCount > 0)
        {
            if (_logger.IsError) _logger.Error($"Hash verification found {verifyingTrieStore.HashMismatchCount} mismatches");
        }

        bool isOk = Stats.MismatchedAccount == 0 && Stats.MismatchedSlot == 0 &&
                    Stats.MissingInFlat == 0 && Stats.MissingInTrie == 0 &&
                    verifyingTrieStore.HashMismatchCount == 0;

        if (!isOk)
        {
            if (_logger.IsWarn) _logger.Warn(
                $"Verification failed: {Stats.MismatchedAccount} mismatched accounts, {Stats.MismatchedSlot} mismatched slots, " +
                $"{Stats.MissingInFlat} missing in flat, {Stats.MissingInTrie} missing in trie");
        }

        if (_logger.IsInfo) _logger.Info($"Verification complete. {Stats}");

        return isOk;
    }

    /// <summary>
    /// Hashed mode: Single-pass co-iteration since flat and trie share the same sort order.
    /// </summary>
    private void VerifyHashedMode(
        IPersistence.IPersistenceReader reader,
        IScopedTrieStore trieStore,
        Hash256 stateRoot,
        ChannelWriter<StorageVerificationJob> storageWriter,
        VisitorProgressTracker progressTracker,
        CancellationToken cancellationToken)
    {
        using IPersistence.IFlatIterator flatIter = reader.CreateAccountIterator();
        TrieLeafIterator trieIter = new(trieStore, stateRoot, LogTrieNodeException);

        bool hasFlat = flatIter.MoveNext();
        bool hasTrie = trieIter.MoveNext();

        TreePath progressPath = TreePath.Empty;

        while (hasFlat || hasTrie)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int cmp = CompareHashedKeys(
                hasFlat ? flatIter.CurrentKey : default,
                hasTrie ? trieIter.CurrentPath : default,
                hasFlat,
                hasTrie);

            if (cmp == 0)
            {
                Interlocked.Increment(ref _accountCount);
                if (trieIter.CurrentPath.Truncate(VisitorProgressTracker.Level3Depth) != progressPath)
                {
                    progressPath = trieIter.CurrentPath.Truncate(VisitorProgressTracker.Level3Depth);
                    progressTracker.OnNodeVisited(progressPath, isStorage: false);
                }
                VerifyAccountMatch(flatIter.CurrentValue, trieIter.CurrentLeaf!, flatIter.CurrentKey, trieIter.CurrentPath, reader.IsPreimageMode, trieStore, storageWriter, cancellationToken);
                hasFlat = flatIter.MoveNext();
                hasTrie = trieIter.MoveNext();
            }
            else if (cmp < 0 || !hasTrie)
            {
                Interlocked.Increment(ref _accountCount);
                Interlocked.Increment(ref _missingInTrie);
                if (_logger.IsWarn) _logger.Warn($"Account in flat not found in trie. FlatKey: {flatIter.CurrentKey}");
                TreePath targetPath = TreePath.FromPath(flatIter.CurrentKey.Bytes[..FlatKeyLength]);
                DiagnoseTriePath(trieStore, stateRoot, targetPath, flatIter.CurrentKey);
                hasFlat = flatIter.MoveNext();
            }
            else
            {
                Interlocked.Increment(ref _accountCount);
                Interlocked.Increment(ref _missingInFlat);
                if (trieIter.CurrentPath.Truncate(VisitorProgressTracker.Level3Depth) != progressPath)
                {
                    progressPath = trieIter.CurrentPath.Truncate(VisitorProgressTracker.Level3Depth);
                    progressTracker.OnNodeVisited(progressPath, isStorage: false);
                }
                if (_logger.IsWarn) _logger.Warn($"Account in trie not found in flat. TriePath: {trieIter.CurrentPath}");
                hasTrie = trieIter.MoveNext();
            }
        }
    }

    /// <summary>
    /// Preimage mode: Two-pass verification using PatriciaTree.Get() directly for RLP lookup.
    /// Pass 1: Iterate flat, lookup each in trie - detects mismatches and entries missing in trie
    /// Pass 2: Iterate trie, check against seen set - detects entries missing in flat
    /// </summary>
    private void VerifyPreimageMode(
        IPersistence.IPersistenceReader reader,
        IScopedTrieStore trieStore,
        Hash256 stateRoot,
        ChannelWriter<StorageVerificationJob> storageWriter,
        VisitorProgressTracker progressTracker,
        CancellationToken cancellationToken)
    {
        // PatriciaTree for direct RLP lookup
        PatriciaTree? tree = stateRoot != Keccak.EmptyTreeHash ? new(trieStore, _logManager) : null;

        // Build a set of verified trie paths to avoid double-counting in pass 2
        // Using first 8 bytes as ulong for reliable and efficient HashSet operations
        HashSet<ulong> verifiedTriePaths = [];

        TreePath progressPath = TreePath.Empty;

        // Pass 1: Flat -> Trie
        using (IPersistence.IFlatIterator flatIter = reader.CreateAccountIterator())
        {
            while (flatIter.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref _accountCount);

                // In preimage mode, flat key contains raw address bytes
                ValueHash256 flatKey = flatIter.CurrentKey;
                Hash256 trieHash = Keccak.Compute(flatKey.Bytes[..20]);
                ulong hashKey = BinaryPrimitives.ReadUInt64LittleEndian(trieHash.Bytes);

                // Direct RLP lookup using PatriciaTree.Get()
                ReadOnlySpan<byte> trieAccountRlp = tree is not null ? tree.Get(trieHash.Bytes, stateRoot) : [];

                if (trieAccountRlp.IsEmpty)
                {
                    Interlocked.Increment(ref _missingInTrie);
                    if (_logger.IsWarn) _logger.Warn($"Account in flat not found in trie. Address: {new Address(flatKey.Bytes[..20].ToArray())}");
                    TreePath targetPath = TreePath.FromPath(trieHash.Bytes);
                    DiagnoseTriePath(trieStore, stateRoot, targetPath, flatKey);
                    continue;
                }

                verifiedTriePaths.Add(hashKey);
                TreePath triePath = TreePath.FromPath(trieHash.Bytes);
                if (triePath.Truncate(VisitorProgressTracker.Level3Depth) != progressPath)
                {
                    progressPath = triePath.Truncate(VisitorProgressTracker.Level3Depth);
                    progressTracker.OnNodeVisited(progressPath, isStorage: false);
                }

                VerifyAccountMatchPreimageWithRlp(flatIter.CurrentValue, trieAccountRlp, flatKey, trieHash.ValueHash256, storageWriter, cancellationToken);
            }
        }

        // Pass 2: Trie -> Flat (check for entries in trie not in flat)
        TrieLeafIterator trieIter = new(trieStore, stateRoot, LogTrieNodeException);
        while (trieIter.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();

            ulong triePathKey = BinaryPrimitives.ReadUInt64LittleEndian(trieIter.CurrentPath.Path.Bytes);

            if (verifiedTriePaths.Contains(triePathKey))
                continue;

            Interlocked.Increment(ref _accountCount);
            Interlocked.Increment(ref _missingInFlat);
            if (trieIter.CurrentPath.Truncate(VisitorProgressTracker.Level3Depth) != progressPath)
            {
                progressPath = trieIter.CurrentPath.Truncate(VisitorProgressTracker.Level3Depth);
                progressTracker.OnNodeVisited(progressPath, isStorage: false);
            }
            if (_logger.IsWarn) _logger.Warn($"Account in trie not found in flat. TriePath: {trieIter.CurrentPath}");
        }
    }

    private static int CompareHashedKeys(in ValueHash256 flatKey, in TreePath triePath, bool hasFlat, bool hasTrie) =>
        (hasFlat, hasTrie) switch
        {
            (false, false) => 0,
            (false, true) => 1,
            (true, false) => -1,
            _ => Bytes.BytesComparer.Compare(flatKey.Bytes[..FlatKeyLength], triePath.Path.Bytes[..FlatKeyLength])
        };

    private void VerifyAccountMatch(
        ReadOnlySpan<byte> flatAccountRlp,
        TrieNode trieLeaf,
        in ValueHash256 flatKey,
        in TreePath triePath,
        bool isPreimageMode,
        IScopedTrieStore trieStore,
        ChannelWriter<StorageVerificationJob> storageWriter,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<byte> trieAccountRlp = trieLeaf.Value.Span;

        Rlp.ValueDecoderContext flatCtx = new(flatAccountRlp);
        Account? flatAccount = AccountDecoder.Slim.Decode(ref flatCtx);

        Rlp.ValueDecoderContext trieCtx = new(trieAccountRlp);
        Account? trieAccount = AccountDecoder.Instance.Decode(ref trieCtx);

        if (flatAccount != trieAccount)
        {
            Interlocked.Increment(ref _mismatchedAccount);
            if (_logger.IsWarn) _logger.Warn($"Mismatched account. Path: {triePath}. Flat: {flatAccount}, Trie: {trieAccount}");
        }

        if (trieAccount is not null && trieAccount.StorageRoot != Keccak.EmptyTreeHash)
        {
            Hash256 fullPath = triePath.Path.ToCommitment();
            StorageVerificationJob job = new(flatKey, fullPath, trieAccount.StorageRoot, isPreimageMode);
            storageWriter.WriteAsync(job, cancellationToken).AsTask().Wait(cancellationToken);
        }
    }

    private void VerifyAccountMatchPreimageWithRlp(
        ReadOnlySpan<byte> flatAccountRlp,
        ReadOnlySpan<byte> trieAccountRlp,
        in ValueHash256 flatKey,
        in ValueHash256 trieHash,
        ChannelWriter<StorageVerificationJob> storageWriter,
        CancellationToken cancellationToken)
    {
        Rlp.ValueDecoderContext flatCtx = new(flatAccountRlp);
        Account? flatAccount = AccountDecoder.Slim.Decode(ref flatCtx);

        Rlp.ValueDecoderContext trieCtx = new(trieAccountRlp);
        Account? trieAccount = AccountDecoder.Instance.Decode(ref trieCtx);

        if (flatAccount != trieAccount)
        {
            Interlocked.Increment(ref _mismatchedAccount);
            if (_logger.IsWarn) _logger.Warn($"Mismatched account. Hash: {trieHash}. Flat: {flatAccount}, Trie: {trieAccount}");
        }

        if (trieAccount is not null && trieAccount.StorageRoot != Keccak.EmptyTreeHash)
        {
            Hash256 fullPath = trieHash.ToCommitment();
            StorageVerificationJob job = new(flatKey, fullPath, trieAccount.StorageRoot, true);
            storageWriter.WriteAsync(job, cancellationToken).AsTask().Wait(cancellationToken);
        }
    }

    private async Task ProcessStorageQueue(
        ChannelReader<StorageVerificationJob> channelReader,
        IPersistence.IPersistenceReader reader,
        IScopedTrieStore trieStore,
        CancellationToken cancellationToken)
    {
        await foreach (StorageVerificationJob job in channelReader.ReadAllAsync(cancellationToken))
        {
            if (job.IsPreimageMode)
            {
                VerifyStoragePreimage(job, reader, trieStore, cancellationToken);
            }
            else
            {
                VerifyStorageHashed(job, reader, trieStore, cancellationToken);
            }
        }
    }

    private void VerifyStorageHashed(
        StorageVerificationJob job,
        IPersistence.IPersistenceReader reader,
        IScopedTrieStore trieStore,
        CancellationToken cancellationToken)
    {
        using IPersistence.IFlatIterator flatIter = reader.CreateStorageIterator(job.FlatAccountKey);
        IScopedTrieStore storageTrieStore = (IScopedTrieStore)trieStore.GetStorageTrieNodeResolver(job.TrieAccountPath);
        TrieLeafIterator trieIter = new(storageTrieStore, job.StorageRoot, LogTrieNodeException);

        bool hasFlat = flatIter.MoveNext();
        bool hasTrie = trieIter.MoveNext();

        while (hasFlat || hasTrie)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int cmp = CompareStorageKeys(
                hasFlat ? flatIter.CurrentKey : default,
                hasTrie ? trieIter.CurrentPath : default,
                hasFlat,
                hasTrie);

            if (cmp == 0)
            {
                Interlocked.Increment(ref _slotCount);
                VerifySlotMatch(flatIter.CurrentValue, trieIter.CurrentLeaf!, job.FlatAccountKey, flatIter.CurrentKey, trieIter.CurrentPath);
                hasFlat = flatIter.MoveNext();
                hasTrie = trieIter.MoveNext();
            }
            else if (cmp < 0 || !hasTrie)
            {
                Interlocked.Increment(ref _slotCount);
                if (!IsZeroValue(flatIter.CurrentValue))
                {
                    Interlocked.Increment(ref _missingInTrie);
                    if (_logger.IsWarn) _logger.Warn($"Storage slot in flat not in trie. Account: {job.FlatAccountKey}, Slot: {flatIter.CurrentKey}");
                }
                hasFlat = flatIter.MoveNext();
            }
            else
            {
                Interlocked.Increment(ref _slotCount);
                Interlocked.Increment(ref _missingInFlat);
                if (_logger.IsWarn) _logger.Warn($"Storage slot in trie not in flat. Account: {job.FlatAccountKey}, TriePath: {trieIter.CurrentPath}");
                hasTrie = trieIter.MoveNext();
            }
        }
    }

    private void VerifyStoragePreimage(
        StorageVerificationJob job,
        IPersistence.IPersistenceReader reader,
        IScopedTrieStore trieStore,
        CancellationToken cancellationToken)
    {
        IScopedTrieStore storageTrieStore = (IScopedTrieStore)trieStore.GetStorageTrieNodeResolver(job.TrieAccountPath);
        PatriciaTree storageTree = new(storageTrieStore, _logManager);

        HashSet<ulong> verifiedSlots = [];

        // Pass 1: Flat -> Trie
        using (IPersistence.IFlatIterator flatIter = reader.CreateStorageIterator(job.FlatAccountKey))
        {
            while (flatIter.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref _slotCount);

                // In preimage mode, flat key is raw slot bytes (big-endian UInt256)
                ValueHash256 flatSlotKey = flatIter.CurrentKey;
                Hash256 slotHash = Keccak.Compute(flatSlotKey.Bytes);
                ulong hashKey = BinaryPrimitives.ReadUInt64LittleEndian(slotHash.Bytes);

                // Direct RLP lookup using PatriciaTree.Get()
                ReadOnlySpan<byte> trieValueRlp = storageTree.Get(slotHash.Bytes, job.StorageRoot);

                if (trieValueRlp.IsEmpty)
                {
                    if (!IsZeroValue(flatIter.CurrentValue))
                    {
                        Interlocked.Increment(ref _missingInTrie);
                        if (_logger.IsWarn) _logger.Warn($"Storage slot in flat not in trie. Account: {job.FlatAccountKey}, Slot: {flatSlotKey}");
                    }
                    continue;
                }

                verifiedSlots.Add(hashKey);
                VerifySlotMatchPreimageWithRlp(flatIter.CurrentValue, trieValueRlp, job.FlatAccountKey, flatSlotKey);
            }
        }

        // Pass 2: Trie -> Flat (check for entries in trie not in flat)
        TrieLeafIterator trieIter = new(storageTrieStore, job.StorageRoot, LogTrieNodeException);
        while (trieIter.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();

            ulong triePathKey = BinaryPrimitives.ReadUInt64LittleEndian(trieIter.CurrentPath.Path.Bytes);

            if (verifiedSlots.Contains(triePathKey))
                continue;

            Interlocked.Increment(ref _slotCount);
            Interlocked.Increment(ref _missingInFlat);
            if (_logger.IsWarn) _logger.Warn($"Storage slot in trie not in flat. Account: {job.FlatAccountKey}, TriePath: {trieIter.CurrentPath}");
        }
    }

    private static int CompareStorageKeys(in ValueHash256 flatKey, in TreePath triePath, bool hasFlat, bool hasTrie) =>
        (hasFlat, hasTrie) switch
        {
            (false, false) => 0,
            (false, true) => 1,
            (true, false) => -1,
            _ => Bytes.BytesComparer.Compare(flatKey.Bytes, triePath.Path.Bytes)
        };

    private void VerifySlotMatch(ReadOnlySpan<byte> flatValue, TrieNode trieLeaf, in ValueHash256 accountKey, in ValueHash256 slotKey, in TreePath triePath)
    {
        ReadOnlySpan<byte> trieValue = trieLeaf.Value.Span;
        if (trieValue.IsEmpty)
        {
            if (IsZeroValue(flatValue)) return;
            Interlocked.Increment(ref _mismatchedSlot);
            if (_logger.IsWarn) _logger.Warn($"Mismatched slot (trie empty). Account: {accountKey}, Slot: {slotKey}");
            return;
        }

        Rlp.ValueDecoderContext ctx = new(trieValue);
        byte[] decodedTrieValue = ctx.DecodeByteArray();

        ReadOnlySpan<byte> flatTrimmed = flatValue.WithoutLeadingZeros();
        ReadOnlySpan<byte> trieTrimmed = decodedTrieValue.AsSpan().WithoutLeadingZeros();

        if (!Bytes.AreEqual(flatTrimmed, trieTrimmed))
        {
            Interlocked.Increment(ref _mismatchedSlot);
            if (_logger.IsWarn) _logger.Warn($"Mismatched slot. Account: {accountKey}, Slot: {slotKey}. Flat: {flatTrimmed.ToHexString()}, Trie: {trieTrimmed.ToHexString()}");
        }
    }

    private void VerifySlotMatchPreimageWithRlp(ReadOnlySpan<byte> flatValue, ReadOnlySpan<byte> trieValueRlp, in ValueHash256 accountKey, in ValueHash256 slotKey)
    {
        // Decode RLP to get the actual value
        Rlp.ValueDecoderContext ctx = new(trieValueRlp);
        byte[] decodedTrieValue = ctx.DecodeByteArray();

        ReadOnlySpan<byte> flatTrimmed = flatValue.WithoutLeadingZeros();
        ReadOnlySpan<byte> trieTrimmed = decodedTrieValue.AsSpan().WithoutLeadingZeros();

        if (!Bytes.AreEqual(flatTrimmed, trieTrimmed))
        {
            Interlocked.Increment(ref _mismatchedSlot);
            if (_logger.IsWarn) _logger.Warn($"Mismatched slot. Account: {accountKey}, Slot: {slotKey}. Flat: {flatTrimmed.ToHexString()}, Trie: {trieTrimmed.ToHexString()}");
        }
    }

    private static bool IsZeroValue(ReadOnlySpan<byte> value) =>
        value.IsEmpty || value.WithoutLeadingZeros().IsEmpty;

    private void LogTrieNodeException(TrieNodeException ex) =>
        _logger.Warn($"TrieLeafIterator encountered exception: {ex.Message}");

    /// <summary>
    /// Diagnostic traversal when flat entry exists but trie lookup fails.
    /// Walks the trie path showing node type, hash, and whether RLP is available/valid.
    /// </summary>
    private void DiagnoseTriePath(
        IScopedTrieStore trieStore,
        Hash256 stateRoot,
        TreePath targetPath,
        in ValueHash256 flatKey)
    {
        if (_logger.IsInfo) _logger.Info($"=== Diagnosing trie path for flat key {flatKey} ===");
        if (_logger.IsInfo) _logger.Info($"Target path: {targetPath}");

        TreePath currentPath = TreePath.Empty;
        Hash256? currentHash = stateRoot;

        while (currentHash is not null && currentHash != Keccak.EmptyTreeHash)
        {
            // Load RLP
            byte[]? rlp = trieStore.TryLoadRlp(currentPath, currentHash, ReadFlags.None);
            bool hashValid = rlp is not null && Keccak.Compute(rlp) == currentHash;

            if (rlp is null)
            {
                if (_logger.IsWarn) _logger.Warn($"  Path: {currentPath} | Hash: {currentHash.ToShortString()} | RLP: MISSING");
                if (_logger.IsWarn) _logger.Warn($"  -> Remaining nibbles to traverse: {targetPath.Length - currentPath.Length}");
                ScanRemainingPathWithZeroHash(trieStore, currentPath, targetPath);
                return;
            }

            // Decode node
            TrieNode node = new(NodeType.Unknown, currentHash, rlp);
            try
            {
                node.ResolveNode(trieStore, currentPath);
            }
            catch (TrieNodeException ex)
            {
                if (_logger.IsWarn) _logger.Warn($"  Path: {currentPath} | Failed to resolve: {ex.Message}");
                ScanRemainingPathWithZeroHash(trieStore, currentPath, targetPath);
                return;
            }

            if (_logger.IsInfo) _logger.Info($"  Path: {currentPath} | Type: {node.NodeType} | Hash: {currentHash.ToShortString()} | HashValid: {hashValid}");

            // Navigate based on node type
            switch (node.NodeType)
            {
                case NodeType.Branch:
                    // Get the nibble to follow - use target path if available, otherwise scan all children
                    int nibble;
                    if (currentPath.Length < targetPath.Length)
                    {
                        nibble = targetPath[currentPath.Length];
                    }
                    else
                    {
                        // Past target path - find first non-null child to continue exploring
                        nibble = -1;
                        for (int i = 0; i < 16; i++)
                        {
                            if (!node.IsChildNull(i))
                            {
                                nibble = i;
                                if (_logger.IsInfo) _logger.Info($"  -> Past target, following first non-null child {i:X}");
                                break;
                            }
                        }
                        if (nibble == -1)
                        {
                            if (_logger.IsInfo) _logger.Info($"  -> Branch has no children");
                            return;
                        }
                    }

                    currentPath = currentPath.Append(nibble);

                    if (node.IsChildNull(nibble))
                    {
                        if (_logger.IsWarn) _logger.Warn($"  -> Branch child {nibble:X} is null");
                        if (_logger.IsWarn) _logger.Warn($"  -> Remaining nibbles: {targetPath.Length - currentPath.Length}");
                        ScanRemainingPathWithZeroHash(trieStore, currentPath, targetPath);
                        return;
                    }

                    currentHash = node.GetChildHash(nibble);
                    break;

                case NodeType.Extension:
                    byte[]? key = node.Key;
                    if (_logger.IsInfo) _logger.Info($"  -> Extension key: {key?.ToHexString() ?? "null"}");

                    if (key is null)
                    {
                        if (_logger.IsWarn) _logger.Warn($"  -> Extension has null key");
                        ScanRemainingPathWithZeroHash(trieStore, currentPath, targetPath);
                        return;
                    }

                    // Check if path matches (only if we haven't passed target)
                    if (currentPath.Length < targetPath.Length)
                    {
                        for (int i = 0; i < key.Length && currentPath.Length + i < targetPath.Length; i++)
                        {
                            if (key[i] != targetPath[currentPath.Length + i])
                            {
                                if (_logger.IsWarn) _logger.Warn($"  -> Extension key mismatch at position {i}: expected {targetPath[currentPath.Length + i]:X}, got {key[i]:X}");
                                ScanRemainingPathWithZeroHash(trieStore, currentPath, targetPath);
                                return;
                            }
                        }
                    }

                    currentPath = currentPath.Append(key);
                    currentHash = node.GetChildHash(0);
                    break;

                case NodeType.Leaf:
                    if (_logger.IsInfo) _logger.Info($"  -> Found leaf with key: {node.Key?.ToHexString() ?? "null"}");
                    if (_logger.IsInfo) _logger.Info($"  -> Full leaf path: {currentPath.Append(node.Key ?? [])}");
                    return;

                default:
                    if (_logger.IsWarn) _logger.Warn($"  -> Unknown node type: {node.NodeType}");
                    ScanRemainingPathWithZeroHash(trieStore, currentPath, targetPath);
                    return;
            }
        }

        if (currentHash is null)
        {
            if (_logger.IsInfo) _logger.Info($"  -> Traversal ended with null hash at path {currentPath}");
        }
        else if (currentHash == Keccak.EmptyTreeHash)
        {
            if (_logger.IsInfo) _logger.Info($"  -> Traversal ended at empty tree hash at path {currentPath}");
        }

        // Continue scanning remaining path with zero hash to see what's stored
        ScanRemainingPathWithZeroHash(trieStore, currentPath, targetPath);
    }

    /// <summary>
    /// Scans remaining path nibbles using Keccak.Zero to see what data is stored
    /// at each position. This helps diagnose cases where hash-based lookup fails.
    /// </summary>
    private void ScanRemainingPathWithZeroHash(
        IScopedTrieStore trieStore,
        TreePath currentPath,
        TreePath targetPath)
    {
        if (currentPath.Length >= targetPath.Length)
            return;

        if (_logger.IsInfo) _logger.Info($"  -> Scanning remaining path with zero hash...");

        while (currentPath.Length < targetPath.Length)
        {
            int nibble = targetPath[currentPath.Length];
            currentPath = currentPath.Append(nibble);

            byte[]? zeroHashRlp = trieStore.TryLoadRlp(currentPath, Keccak.Zero, ReadFlags.None);
            if (zeroHashRlp is not null)
            {
                Hash256 actualHash = Keccak.Compute(zeroHashRlp);
                if (_logger.IsInfo) _logger.Info($"  Path: {currentPath} | ZeroHash lookup found data | ActualHash: {actualHash.ToShortString()}");

                // Try to decode and show node info
                TrieNode node = new(NodeType.Unknown, actualHash, zeroHashRlp);
                try
                {
                    node.ResolveNode(trieStore, currentPath);
                    if (_logger.IsInfo) _logger.Info($"    -> Type: {node.NodeType}, Key: {node.Key?.ToHexString() ?? "null"}");
                }
                catch (TrieNodeException ex)
                {
                    if (_logger.IsWarn) _logger.Warn($"    -> Failed to resolve: {ex.Message}");
                }
            }
            else
            {
                if (_logger.IsInfo) _logger.Info($"  Path: {currentPath} | ZeroHash lookup: nothing");
            }
        }
    }

    private readonly record struct StorageVerificationJob(
        ValueHash256 FlatAccountKey,
        Hash256 TrieAccountPath,
        Hash256 StorageRoot,
        bool IsPreimageMode);

    public readonly record struct VerificationStats(
        long AccountCount,
        long SlotCount,
        long MismatchedAccount,
        long MismatchedSlot,
        long MissingInFlat,
        long MissingInTrie)
    {
        public override string ToString() =>
            $"Accounts={AccountCount}, Slots={SlotCount}, MismatchedAccounts={MismatchedAccount}, " +
            $"MismatchedSlots={MismatchedSlot}, MissingInFlat={MissingInFlat}, MissingInTrie={MissingInTrie}";
    }

    /// <summary>
    /// Wrapper around IScopedTrieStore that verifies hashes of loaded RLP data.
    /// </summary>
    private sealed class HashVerifyingTrieStore(IScopedTrieStore inner, ILogger logger) : IScopedTrieStore
    {
        private long _hashMismatchCount;

        public long HashMismatchCount => Interlocked.Read(ref _hashMismatchCount);

        public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => inner.FindCachedOrUnknown(path, hash);

        public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
        {
            byte[]? rlp = inner.LoadRlp(path, hash, flags);
            if (rlp is not null)
            {
                VerifyHash(rlp, hash, path);
            }
            return rlp;
        }

        public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
        {
            byte[]? rlp = inner.TryLoadRlp(path, hash, flags);
            if (rlp is not null && hash != Keccak.Zero)
            {
                VerifyHash(rlp, hash, path);
            }
            return rlp;
        }

        private void VerifyHash(byte[] rlp, Hash256 expectedHash, in TreePath path)
        {
            Hash256 computed = Keccak.Compute(rlp);
            if (computed != expectedHash)
            {
                Interlocked.Increment(ref _hashMismatchCount);
                if (logger.IsError) logger.Error(
                    $"Hash mismatch at path {path}: expected {expectedHash.ToShortString()}, computed {computed.ToShortString()}");
            }
        }

        public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) =>
            address is null
                ? this
                : new HashVerifyingTrieStore((IScopedTrieStore)inner.GetStorageTrieNodeResolver(address), logger);

        public INodeStorage.KeyScheme Scheme => inner.Scheme;

        public ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
            inner.BeginCommit(root, writeFlags);

        public bool IsPersisted(in TreePath path, in ValueHash256 keccak) => inner.IsPersisted(path, keccak);
    }
}
