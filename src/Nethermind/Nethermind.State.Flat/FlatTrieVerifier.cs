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
            workers[i] = Task.Run(() => ProcessStorageQueue(channel.Reader, reader, trieStore, cancellationToken));
        }

        try
        {
            if (reader.IsPreimageMode)
            {
                VerifyPreimageMode(reader, trieStore, stateRoot, channel.Writer, progressTracker, cancellationToken);
            }
            else
            {
                VerifyHashedMode(reader, trieStore, stateRoot, channel.Writer, progressTracker, cancellationToken);
            }
        }
        finally
        {
            channel.Writer.Complete();
            Task.WaitAll(workers);
            progressTracker.Finish();
        }

        bool isOk = Stats.MismatchedAccount == 0 && Stats.MismatchedSlot == 0 &&
                    Stats.MissingInFlat == 0 && Stats.MissingInTrie == 0;

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
        TrieLeafIterator trieIter = new(trieStore, stateRoot);

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
                VerifyAccountMatch(flatIter.CurrentValue, trieIter.CurrentLeaf!, flatIter.CurrentKey, trieIter.CurrentPath, reader.IsPreimageMode, storageWriter, cancellationToken);
                hasFlat = flatIter.MoveNext();
                hasTrie = trieIter.MoveNext();
            }
            else if (cmp < 0 || !hasTrie)
            {
                Interlocked.Increment(ref _accountCount);
                Interlocked.Increment(ref _missingInTrie);
                if (_logger.IsWarn) _logger.Warn($"Account in flat not found in trie. FlatKey: {flatIter.CurrentKey}");
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
        TrieLeafIterator trieIter = new(trieStore, stateRoot);
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
        TrieLeafIterator trieIter = new(storageTrieStore, job.StorageRoot);

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
                VerifySlotMatch(flatIter.CurrentValue, trieIter.CurrentLeaf!, job.FlatAccountKey, flatIter.CurrentKey);
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
        TrieLeafIterator trieIter = new(storageTrieStore, job.StorageRoot);
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

    private void VerifySlotMatch(ReadOnlySpan<byte> flatValue, TrieNode trieLeaf, in ValueHash256 accountKey, in ValueHash256 slotKey)
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
}
