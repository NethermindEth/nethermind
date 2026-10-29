// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// A collection of classes to make combining persistence code easier.
/// Implementation accept generic of dependencies and the dependencies must be struct. This allow better inlining and devirtualized calls.
///
/// <see cref="IPersistence"/> implementation is expected to create <see cref="Reader{TFlatReader,TTrieReader}"/> and
/// <see cref="WriteBatch{TFlatWriteBatch,TTrieWriteBatch}"/> passing in the flat and trie implementations along with
/// some dispose logic.
///
/// Flat implementation is largely expected to implement <see cref="IHashedFlatReader"/> and <see cref="IHashedFlatWriteBatch"/>
/// which then get wrapped into <see cref="IFlatReader"/> and <see cref="IFlatWriteBatch"/> with <see cref="ToHashedFlatReader{TFlatReader}"/>
/// and <see cref="ToHashedWriteBatch{TWriteBatch}"/>. This allow preimage variation which does not hash the keys.
/// </summary>
public static class BasePersistence
{
    /// <summary>The index of sample <paramref name="sample"/> of <paramref name="sampleCount"/> spread evenly over <paramref name="length"/> keys.</summary>
    internal static int SampleIndex(int sample, int sampleCount, int length) => (int)((long)sample * length / sampleCount);

    /// <summary>How many keys per column decide a block's prefetch: a block with half of its sampled misses read from
    /// disk is decided disk with over 96% probability.</summary>
    public const int PrefetchSamples = 8;

    /// <summary>How many of those samples are probed while the previous block of the column was fully cached.</summary>
    private const int CachedRunSamples = 2;

    /// <summary>Copies up to <see cref="PrefetchSamples"/> keys spread evenly over <paramref name="keys"/>.</summary>
    /// <returns>How many samples were copied.</returns>
    public static int FillSamples<T>(ReadOnlySpan<T> keys, Span<T> samples)
    {
        int count = Math.Min(Math.Min(keys.Length, PrefetchSamples), samples.Length);
        for (int sample = 0; sample < count; sample++) samples[sample] = keys[SampleIndex(sample, count, keys.Length)];
        return count;
    }

    /// <summary>The longest read of one sampled key that still counts as answered from memory.</summary>
    /// <remarks>
    /// A key missing from the block cache can still be in the OS page cache, where a prefetch only repeats the read:
    /// measured at about 16 us per read there against about 160 us from a cloud disk and 60-100 us from a local NVMe
    /// drive. A read that the scheduler delays counts as a disk read, so noise only ever sends a prefetch.
    /// </remarks>
    internal static readonly TimeSpan MemoryReadLimit = TimeSpan.FromMicroseconds(40);

    internal static readonly long MemoryReadLimitTicks = (long)(MemoryReadLimit.TotalSeconds * Stopwatch.Frequency);

    /// <summary>Whether a read between the two <see cref="Stopwatch"/> timestamps took longer than memory allows.</summary>
    internal static bool IsDiskRead(long startTimestamp, long endTimestamp) => endTimestamp - startTimestamp > MemoryReadLimitTicks;

    public const int StoragePrefixPortion = 4;

    private static readonly byte[] CurrentStateKey = Keccak.Compute("CurrentState").BytesToArray();
    private static readonly byte[] LayoutKey = Keccak.Compute("Layout").BytesToArray();
    private static readonly byte[] SlotEncodingKey = Keccak.Compute("SlotEncoding").BytesToArray();
    private static readonly byte[] WipedForSyncKey = Keccak.Compute("WipedForSync").BytesToArray();
    private static readonly byte[] SnapCheckpointKey = Keccak.Compute("SnapCheckpoint").BytesToArray();

    /// <summary>Raw storage slot encoding: the stripped value bytes are stored verbatim. Legacy, deprecated.</summary>
    internal const byte SlotEncodingRaw = 0;

    /// <summary>RLP storage slot encoding: the stripped value is stored as an RLP byte string.</summary>
    internal const byte SlotEncodingRlp = 1;

    private const string RawSlotDeprecationMessage =
        "Flat DB uses the legacy raw storage slot encoding, which is deprecated and will be removed in a future release. Please resync to adopt the RLP slot encoding.";

    /// <summary>Reads the flat DB's persisted state pointer, or <see cref="StateId.PreGenesis"/> when none is persisted.</summary>
    public static StateId ReadCurrentState(IReadOnlyKeyValueStore kv)
    {
        byte[]? bytes = kv.Get(CurrentStateKey);
        return bytes is null || bytes.Length == 0
            ? new StateId(ulong.MaxValue, ValueKeccak.EmptyTreeHash)
            : new StateId(BinaryPrimitives.ReadUInt64BigEndian(bytes), new ValueHash256(bytes[8..]));
    }

    [SkipLocalsInit]
    internal static void SetCurrentState(IWriteOnlyKeyValueStore kv, in StateId stateId)
    {
        Span<byte> bytes = stackalloc byte[8 + 32];
        BinaryPrimitives.WriteUInt64BigEndian(bytes[..8], stateId.BlockNumber);
        stateId.StateRoot.BytesAsSpan.CopyTo(bytes[8..]);
        kv.PutSpan(CurrentStateKey, bytes);
        // A persisted state pointer means the sync that followed a wipe has completed.
        kv.Remove(WipedForSyncKey);
    }

    internal static FlatLayout? ReadLayout(IReadOnlyKeyValueStore kv)
    {
        byte[]? bytes = kv.Get(LayoutKey);
        if (bytes is null || bytes.Length == 0) return null;
        if (!Enum.IsDefined((FlatLayout)bytes[0]))
            throw new InvalidConfigurationException(
                $"Flat DB metadata contains an unrecognized layout byte '{bytes[0]}'. The DB may be corrupt or was written by a newer version.",
                -1);
        return (FlatLayout)bytes[0];
    }

    /// <summary>
    /// Records the flat DB's on-disk format markers: the <see cref="FlatLayout"/> and the RLP slot encoding.
    /// Only for DBs on the current (RLP-wrapped) format; legacy raw DBs are never re-stamped.
    /// </summary>
    internal static void SetLayout(IWriteOnlyKeyValueStore kv, FlatLayout layout)
    {
        Span<byte> bytes = stackalloc byte[1];
        bytes[0] = (byte)layout;
        kv.PutSpan(LayoutKey, bytes);
        SetSlotEncoding(kv, SlotEncodingRlp);
    }

    /// <summary>
    /// Validates that <paramref name="layout"/> matches the value previously persisted in the flat DB's
    /// metadata column. Throws <see cref="InvalidConfigurationException"/> on mismatch.
    /// On a fresh DB (no stored value) the check is a no-op; the layout is written by the first write
    /// batch through <see cref="RecordLayoutOnFirstBatch"/>.
    /// </summary>
    internal static void ValidateLayout(IColumnsDb<FlatDbColumns> db, FlatLayout layout)
    {
        FlatLayout? stored = ReadLayout(db.GetColumnDb(FlatDbColumns.Metadata));
        if (stored is not null && stored != layout)
        {
            throw new InvalidConfigurationException(
                $"Flat DB was previously synced with layout '{stored}', but the configured layout is '{layout}'. " +
                $"Either set 'IFlatDbConfig.Layout' back to '{stored}', or wipe the flat DB and re-sync.",
                -1);
        }
    }

    /// <summary>
    /// Calls <see cref="ValidateLayout"/> and returns 0 for use as the initial value of the
    /// per-persistence "layout recorded" field (which is written on the first batch via
    /// <see cref="RecordLayoutOnFirstBatch"/>).
    /// </summary>
    internal static int ValidateLayoutReturnFlag(IColumnsDb<FlatDbColumns> db, FlatLayout layout)
    {
        ValidateLayout(db, layout);
        return 0;
    }

    /// <summary>
    /// On the first call, records the persistence's <see cref="FlatLayout"/> and slot encoding in the supplied
    /// batch's metadata column via <see cref="SetLayout"/>. Subsequent calls are no-ops. The write goes through
    /// the batch, so it is committed atomically with the rest of the batch's contents.
    /// </summary>
    internal static void RecordLayoutOnFirstBatch(IWriteOnlyKeyValueStore metadataBatch, ref int flag, FlatLayout layout)
    {
        if (Interlocked.CompareExchange(ref flag, 1, 0) == 0)
        {
            SetLayout(metadataBatch, layout);
        }
    }

    internal static byte? ReadSlotEncoding(IReadOnlyKeyValueStore kv)
    {
        byte[]? bytes = kv.Get(SlotEncodingKey);
        return bytes is null || bytes.Length == 0 ? null : bytes[0];
    }

    [SkipLocalsInit]
    internal static void SetSlotEncoding(IWriteOnlyKeyValueStore kv, byte version)
    {
        Span<byte> bytes = stackalloc byte[1];
        bytes[0] = version;
        kv.PutSpan(SlotEncodingKey, bytes);
    }

    /// <summary>
    /// Decides whether storage slot values are RLP-wrapped for this DB. Brand-new DBs always wrap; the recorded
    /// version of an existing DB always wins so its on-disk format is read back correctly.
    /// </summary>
    /// <remarks>
    /// An absent <see cref="SlotEncodingKey"/> is ambiguous: a brand-new DB and a pre-feature DB both lack it.
    /// A non-empty <paramref name="slotStore"/> pins the DB to raw (with a deprecation warning), as its slots
    /// are raw-encoded and would be misread as RLP. The <see cref="LayoutKey"/> is no discriminator: it
    /// postdates flat sync and tooling can bypass the metadata column, so legacy raw DBs may lack it too.
    /// </remarks>
    /// <param name="slotStore">The column that holds storage slot values for this layout.</param>
    internal static bool ResolveSlotEncoding(IColumnsDb<FlatDbColumns> db, ISortedKeyValueStore slotStore, ILogger logger)
    {
        IReadOnlyKeyValueStore meta = db.GetColumnDb(FlatDbColumns.Metadata);
        bool rlpWrap = ReadSlotEncoding(meta) switch
        {
            SlotEncodingRlp => true,
            SlotEncodingRaw => false,
            // No recorded version: brand-new (no slots) wraps; existing slots are legacy raw.
            null => slotStore.FirstKey is null,
            byte version => throw new InvalidConfigurationException(
                $"Flat DB metadata contains an unrecognized slot encoding version '{version}'. The DB may be corrupt or was written by a newer version.",
                -1),
        };

        if (!rlpWrap) WarnRawDeprecated(logger);
        return rlpWrap;
    }

    /// <summary>Warns that the DB is on the deprecated raw slot encoding and should be resynced.</summary>
    private static void WarnRawDeprecated(ILogger logger)
    {
        if (logger.IsWarn) logger.Warn(RawSlotDeprecationMessage);
    }

    /// <summary>Whether <see cref="ClearAllColumns"/> has marked the flat DB metadata as wiped for a state sync.</summary>
    /// <remarks>
    /// Set by the first batch of a wipe and cleared when a state pointer is persisted, i.e. when the sync that follows
    /// completes. With a pre-genesis state pointer it tells a wiped DB awaiting its sync from one that was never used;
    /// with a state pointer still present it means the wipe itself was interrupted.
    /// </remarks>
    public static bool ReadWipedForSync(IReadOnlyKeyValueStore metadata) => metadata.Get(WipedForSyncKey) is { Length: > 0 };

    /// <summary>
    /// Durably marks the DB as wiped for a state sync ahead of <see cref="ClearAllColumns"/>, for a caller that drops
    /// data outside RocksDB first: a crash in between then reads back as an interrupted wipe rather than as a state
    /// whose nodes are missing.
    /// </summary>
    internal static void MarkWipeStarted(IColumnsDb<FlatDbColumns> db)
    {
        IDb metadata = db.GetColumnDb(FlatDbColumns.Metadata);
        metadata.PutSpan(WipedForSyncKey, [1]);
        metadata.Remove(SnapCheckpointKey);
        metadata.FlushOrThrow();
    }

    /// <summary>Reads the snap sync checkpoint saved by <see cref="WriteSnapCheckpoint"/>, or <c>null</c> when there is none.</summary>
    internal static byte[]? ReadSnapCheckpoint(IReadOnlyKeyValueStore metadata) => metadata.Get(SnapCheckpointKey) is { Length: > 0 } record ? record : null;

    /// <summary>Durably saves the snap sync checkpoint, or deletes it when <paramref name="record"/> is <c>null</c>. A wipe deletes it too.</summary>
    internal static void WriteSnapCheckpoint(IColumnsDb<FlatDbColumns> db, byte[]? record)
    {
        IDb metadata = db.GetColumnDb(FlatDbColumns.Metadata);
        if (record is null) metadata.Remove(SnapCheckpointKey);
        else metadata.PutSpan(SnapCheckpointKey, record);
        metadata.FlushOrThrow();
    }

    /// <summary>Wipes every data column and the state pointer, keeping the format markers, and marks the DB as wiped for a state sync.</summary>
    public static void ClearAllColumns(IColumnsDb<FlatDbColumns> db)
    {
        // Delete in bounded batches; a single batch over every key exhausts memory when wiping a large
        // partially-synced DB on restart. #11442
        const int batchSize = 10_000;

        IColumnsWriteBatch<FlatDbColumns> batch = db.StartWriteBatch();
        try
        {
            // The wipe marker precedes every delete and the state pointer reset closes the wipe, so a wipe that dies
            // midway reads back as "pointer and marker both present" and the next start redoes it.
            batch.GetColumnBatch(FlatDbColumns.Metadata).PutSpan(WipedForSyncKey, [1]);
            batch.GetColumnBatch(FlatDbColumns.Metadata).Remove(SnapCheckpointKey);

            int count = 0;
            foreach (FlatDbColumns column in Enum.GetValues<FlatDbColumns>())
            {
                if (column == FlatDbColumns.Metadata)
                {
                    continue;
                }

                foreach (byte[] key in db.GetColumnDb(column).GetAllKeys())
                {
                    batch.GetColumnBatch(column).Remove(key);
                    if (++count == batchSize)
                    {
                        IColumnsWriteBatch<FlatDbColumns> next = db.StartWriteBatch();
                        batch.Dispose(); // commit the chunk
                        batch = next;
                        count = 0;
                    }
                }
            }

            // Only the state pointer is reset; wiping the format markers makes a re-synced RLP DB read back as raw. #11996
            batch.GetColumnBatch(FlatDbColumns.Metadata).Remove(CurrentStateKey);
        }
        finally
        {
            batch.Dispose();
        }
    }

    internal static void CreateStorageRange(
        ReadOnlySpan<byte> accountPath,
        Span<byte> firstKey,
        Span<byte> lastKey,
        int prefixPortion)
    {
        accountPath[..prefixPortion].CopyTo(firstKey);
        accountPath[..prefixPortion].CopyTo(lastKey);
        firstKey[prefixPortion..].Clear();
        lastKey[prefixPortion..].Fill(0xff);
    }

    /// <summary>
    /// Scan a key range and delete all entries matching the expected key length.
    /// </summary>
    /// <param name="flags">The batch's write flags. Without the WAL a batch commits every few hundred writes, deletes included, which bounds a long scan.</param>
    internal static void DeleteMatchingKeys(
        ISortedKeyValueStore snap,
        IWriteBatch batch,
        ReadOnlySpan<byte> firstKey,
        ReadOnlySpan<byte> lastKey,
        int expectedKeyLength,
        WriteFlags flags)
    {
        using ISortedView view = snap.GetViewBetween(firstKey, lastKey);
        while (view.MoveNext())
        {
            if (view.CurrentKey.Length == expectedKeyLength)
                batch.Set(view.CurrentKey, null, flags);
        }
    }

    /// <summary>
    /// Scan a key range and delete entries matching the address suffix at the given offset.
    /// </summary>
    /// <param name="flags">The batch's write flags. Without the WAL a batch commits every few hundred writes, deletes included, which bounds a long scan.</param>
    internal static void DeleteMatchingKeys(
        ISortedKeyValueStore snap,
        IWriteBatch batch,
        ReadOnlySpan<byte> firstKey,
        ReadOnlySpan<byte> lastKey,
        int suffixOffset,
        ReadOnlySpan<byte> expectedSuffix,
        WriteFlags flags)
    {
        using ISortedView view = snap.GetViewBetween(firstKey, lastKey);
        while (view.MoveNext())
        {
            ReadOnlySpan<byte> key = view.CurrentKey;
            if (key.Length >= suffixOffset + expectedSuffix.Length && Bytes.AreEqual(key[suffixOffset..], expectedSuffix))
                batch.Set(view.CurrentKey, null, flags);
        }
    }

    /// <summary>
    /// Scans <c>[firstKey, lastKey)</c> and removes every key of <paramref name="keyLength"/> bytes whose 20-byte
    /// account is within <c>[fromAccount, toAccount]</c>. The account is read as <paramref name="prefixLength"/> bytes at
    /// <paramref name="prefixOffset"/> followed by the rest of its bytes at <paramref name="suffixOffset"/>.
    /// </summary>
    /// <param name="flags">The batch's write flags. Without the WAL a batch commits every few hundred writes, deletes included, which bounds a long scan.</param>
    [SkipLocalsInit]
    internal static void DeleteKeysOfAccountRange(
        ISortedKeyValueStore snap,
        IWriteBatch batch,
        scoped ReadOnlySpan<byte> firstKey,
        scoped ReadOnlySpan<byte> lastKey,
        int keyLength,
        int prefixOffset,
        int prefixLength,
        int suffixOffset,
        scoped ReadOnlySpan<byte> fromAccount,
        scoped ReadOnlySpan<byte> toAccount,
        WriteFlags flags)
    {
        const int accountLength = 20;
        Span<byte> account = stackalloc byte[accountLength];
        using ISortedView view = snap.GetViewBetween(firstKey, lastKey);
        while (view.MoveNext())
        {
            ReadOnlySpan<byte> key = view.CurrentKey;
            if (key.Length != keyLength) continue;

            key.Slice(prefixOffset, prefixLength).CopyTo(account);
            key.Slice(suffixOffset, accountLength - prefixLength).CopyTo(account[prefixLength..]);
            if (account.SequenceCompareTo(fromAccount) >= 0 && account.SequenceCompareTo(toAccount) <= 0)
                batch.Set(key, null, flags);
        }
    }

    public interface IHashedFlatReader
    {
        public int GetAccount(in ValueHash256 address, Span<byte> outBuffer);
        public bool TryGetStorage(in ValueHash256 address, in ValueHash256 slot, ref UInt256 outValue);
        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey);
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey);
        public bool IsPreimageMode { get; }

        /// <summary>Starts reading the hashed keys that <paramref name="keys"/> produces ahead of use; does nothing by default.</summary>
        public void Prefetch<TKeys>(ref TKeys keys, CancellationToken cancellationToken) where TKeys : struct, IHashedPrefetchKeys, allows ref struct { }

        /// <summary>Whether <see cref="Prefetch{TKeys}"/> reads anything ahead; false by default, where it does nothing.</summary>
        public bool SupportsPrefetch => false;

        /// <summary>Whether reading this account is answered from memory; true when the store cannot prefetch.</summary>
        public bool IsAccountCached(in ValueHash256 account) => true;

        /// <summary>Whether reading this slot is answered from memory; true when the store cannot prefetch.</summary>
        public bool IsSlotCached(in ValueHash256 account, in ValueHash256 slot) => true;
    }

    /// <summary>The hashed keys of one prefetch, computed as they are encoded so no hashed copy is kept.</summary>
    public interface IHashedPrefetchKeys
    {
        /// <summary>The number of accounts; valid account indexes are 0 to <see cref="AccountCount"/> - 1.</summary>
        public int AccountCount { get; }

        /// <summary>The number of slots; valid slot indexes are 0 to <see cref="SlotCount"/> - 1.</summary>
        public int SlotCount { get; }

        /// <summary>Gets the hashed key of the account at <paramref name="index"/>.</summary>
        /// <param name="index">The account index, from 0 to <see cref="AccountCount"/> - 1.</param>
        /// <param name="account">The account path hash.</param>
        public void GetAccount(int index, out ValueHash256 account);

        /// <summary>Gets the hashed account and slot keys of the slot at <paramref name="index"/>.</summary>
        /// <param name="index">The slot index, from 0 to <see cref="SlotCount"/> - 1.</param>
        /// <param name="account">The path hash of the account that owns the slot.</param>
        /// <param name="slot">The storage key hash of the slot.</param>
        /// <remarks>
        /// Any index may be read in any order. Ascending order is cheapest: one account's slots are adjacent, and an
        /// implementation may reuse the account hash of the previous call.
        /// </remarks>
        public void GetSlot(int index, out ValueHash256 account, out ValueHash256 slot);
    }

    public interface IHashedFlatWriteBatch
    {
        public void SelfDestruct(in ValueHash256 address);

        /// <inheritdoc cref="IPersistence.IWriteBatch.SelfDestructRange"/>
        public void SelfDestructRange(in ValueHash256 fromPath, in ValueHash256 toPath);

        public void RemoveAccount(in ValueHash256 address);

        public void SetAccount(in ValueHash256 address, ReadOnlySpan<byte> value);

        public void SetStorage(in ValueHash256 address, in ValueHash256 slotHash, in UInt256? value);

        /// <summary>Writes a slot whose value is already the trie-leaf RLP byte string (<c>RLP(stripped)</c>).</summary>
        public void SetStorageEncoded(in ValueHash256 address, in ValueHash256 slotHash, scoped ReadOnlySpan<byte> rlpValue);

        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath);

        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath);
    }

    public interface IFlatReader
    {
        public Account? GetAccount(Address address);
        public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue);
        public byte[]? GetAccountRaw(in ValueHash256 addrHash);
        public bool TryGetSlotRaw(in ValueHash256 address, in ValueHash256 slotHash, ref UInt256 outValue);
        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey);
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey);
        public bool IsPreimageMode { get; }

        /// <summary>Prefetches no column by default; see <see cref="IPersistence.IPrefetchingReader.PlanPrefetch"/>.</summary>
        public IPersistence.PrefetchColumns PlanPrefetch(Span<Address> accountSamples, Span<StorageCell> slotSamples) => IPersistence.PrefetchColumns.None;

        /// <summary>Starts reading accounts and slots ahead of use; does nothing by default.</summary>
        /// <remarks>The spans are the caller's scratch: readers may filter them in place.</remarks>
        public void Prefetch(IPersistence.PrefetchColumns columns, Span<Address> accounts, Span<StorageCell> slots, CancellationToken cancellationToken) { }

        /// <summary>Whether <see cref="Prefetch"/> reads anything ahead; false by default, where it does nothing.</summary>
        public bool SupportsPrefetch => false;
    }

    public interface IFlatWriteBatch
    {
        public void SelfDestruct(Address addr);

        /// <inheritdoc cref="IPersistence.IWriteBatch.SelfDestructRange"/>
        public void SelfDestructRange(in ValueHash256 fromPath, in ValueHash256 toPath);

        public void SetAccount(Address addr, Account? account);

        public void SetStorage(Address addr, in UInt256 slot, in UInt256? value);

        /// <summary>Writes a slot whose value is already the trie-leaf RLP byte string (<c>RLP(stripped)</c>).</summary>
        public void SetStorageRawEncoded(in ValueHash256 addrHash, in ValueHash256 slotHash, scoped ReadOnlySpan<byte> rlpValue);

        public void SetAccountRaw(in ValueHash256 addrHash, Account account);

        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath);

        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath);
    }

    public interface ITrieReader
    {
        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags);
        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags);
        /// <summary>Passes the RLP of the state trie node at <paramref name="path"/> to <paramref name="action"/>, borrowed for the duration of the call.</summary>
        /// <remarks><paramref name="action"/> runs synchronously, at most once, and only when the node is found.</remarks>
        /// <returns>Whether the node was found and passed.</returns>
        public bool TryLoadStateRlp<TArg>(in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct;

        /// <summary>Passes the RLP of the node at <paramref name="path"/> in the storage trie of <paramref name="address"/> to <paramref name="action"/>, borrowed for the duration of the call.</summary>
        /// <inheritdoc cref="TryLoadStateRlp{TArg}(in TreePath, ReadFlags, ReadOnlySpanAction{byte, TArg}, TArg)"/>
        public bool TryLoadStorageRlp<TArg>(Hash256 address, in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct;
    }

    public interface ITrieWriteBatch
    {
        public void SelfDestruct(in ValueHash256 address);

        /// <inheritdoc cref="IPersistence.IWriteBatch.SelfDestructRange"/>
        public void SelfDestructRange(in ValueHash256 fromPath, in ValueHash256 toPath);

        public void SetStateTrieNode(in TreePath path, scoped ReadOnlySpan<byte> rlp);
        public void SetStorageTrieNode(Hash256 address, in TreePath path, scoped ReadOnlySpan<byte> rlp);
        public void DeleteStateTrieNodeRange(in ValueHash256 from, in ValueHash256 to);
        public void DeleteStorageTrieNodeRange(in ValueHash256 addressHash, in ValueHash256 from, in ValueHash256 to);
    }

    public struct ToHashedWriteBatch<TWriteBatch>(
        TWriteBatch flatWriteBatch,
        bool useFlatAccount = true
    ) : IFlatWriteBatch
        where TWriteBatch : struct, IHashedFlatWriteBatch
    {
        private readonly AccountDecoder _accountDecoder = useFlatAccount ? AccountDecoder.Slim : AccountDecoder.Instance;
        private TWriteBatch _flatWriteBatch = flatWriteBatch;

        public void SelfDestruct(Address addr) => _flatWriteBatch.SelfDestruct(addr.ToAccountPath);

        public void SelfDestructRange(in ValueHash256 fromPath, in ValueHash256 toPath) =>
            _flatWriteBatch.SelfDestructRange(fromPath, toPath);

        public void SetAccount(Address addr, Account? account)
        {
            if (account is null)
            {
                _flatWriteBatch.RemoveAccount(addr.ToAccountPath);
                return;
            }

            using ArrayPoolSpan<byte> rlp = _accountDecoder.EncodeToArrayPoolSpan(account);
            _flatWriteBatch.SetAccount(addr.ToAccountPath, rlp);
        }

        public void SetStorage(Address addr, in UInt256 slot, in UInt256? value)
        {
            ValueHash256 hashBuffer = ValueKeccak.Zero;
            StorageTree.ComputeKeyWithLookup(slot, ref hashBuffer);
            _flatWriteBatch.SetStorage(addr.ToAccountPath, hashBuffer, value);
        }

        public void SetStorageRawEncoded(in ValueHash256 addrHash, in ValueHash256 slotHash, scoped ReadOnlySpan<byte> rlpValue) =>
            _flatWriteBatch.SetStorageEncoded(addrHash, slotHash, rlpValue);

        public void SetAccountRaw(in ValueHash256 addrHash, Account account)
        {
            using ArrayPoolSpan<byte> rlp = _accountDecoder.EncodeToArrayPoolSpan(account);
            _flatWriteBatch.SetAccount(addrHash, rlp);
        }

        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath) =>
            _flatWriteBatch.DeleteAccountRange(fromPath, toPath);

        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath) =>
            _flatWriteBatch.DeleteStorageRange(addressHash, fromPath, toPath);
    }

    public struct ToHashedFlatReader<TFlatReader>(
        TFlatReader flatReader,
        bool useFlatAccount = true,
        PrefetchSampling? sampling = null
    ) : IFlatReader
        where TFlatReader : struct, IHashedFlatReader
    {
        private readonly AccountDecoder _accountDecoder = useFlatAccount ? AccountDecoder.Slim : AccountDecoder.Instance;
        private const int AccountSpanBufferSize = 256;
        private TFlatReader _flatReader = flatReader;

        [SkipLocalsInit]
        public Account? GetAccount(Address address)
        {
            Span<byte> valueBuffer = stackalloc byte[AccountSpanBufferSize];
            int responseSize = _flatReader.GetAccount(address.ToAccountPath, valueBuffer);
            if (responseSize == 0)
            {
                return null;
            }

            RlpReader ctx = new(valueBuffer[..responseSize]);
            return _accountDecoder.Decode(ref ctx);
        }

        public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue)
        {
            ValueHash256 slotHash = ValueKeccak.Zero;
            StorageTree.ComputeKeyWithLookup(slot, ref slotHash);

            return TryGetSlotRaw(address.ToAccountPath, slotHash, ref outValue);
        }

        [SkipLocalsInit]
        public byte[]? GetAccountRaw(in ValueHash256 addrHash)
        {
            Span<byte> valueBuffer = stackalloc byte[AccountSpanBufferSize];
            int responseSize = _flatReader.GetAccount(addrHash, valueBuffer);
            return responseSize == 0 ? null : valueBuffer[..responseSize].ToArray();
        }

        public bool TryGetSlotRaw(in ValueHash256 address, in ValueHash256 slotHash, ref UInt256 outValue) =>
            _flatReader.TryGetStorage(address, slotHash, ref outValue);

        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) =>
            _flatReader.CreateAccountIterator(startKey, endKey);

        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) =>
            _flatReader.CreateStorageIterator(accountKey, startSlotKey, endSlotKey);

        public bool IsPreimageMode => _flatReader.IsPreimageMode;

        public bool SupportsPrefetch => _flatReader.SupportsPrefetch;

        public IPersistence.PrefetchColumns PlanPrefetch(Span<Address> accountSamples, Span<StorageCell> slotSamples)
        {
            IPersistence.PrefetchColumns columns = IPersistence.PrefetchColumns.None;
            if (!accountSamples.IsEmpty && AccountsNeedPrefetch(accountSamples)) columns |= IPersistence.PrefetchColumns.Accounts;
            if (!slotSamples.IsEmpty && SlotsNeedPrefetch(slotSamples)) columns |= IPersistence.PrefetchColumns.Slots;
            if (columns == IPersistence.PrefetchColumns.None) Db.Metrics.IncrementPrefetchesSkippedCached();
            return columns;
        }

        /// <summary>Sends the keys of the <paramref name="columns"/> that <see cref="PlanPrefetch"/> chose.</summary>
        public void Prefetch(IPersistence.PrefetchColumns columns, Span<Address> accounts, Span<StorageCell> slots, CancellationToken cancellationToken)
        {
            if ((columns & IPersistence.PrefetchColumns.Accounts) == 0) accounts = default;
            if ((columns & IPersistence.PrefetchColumns.Slots) == 0) slots = default;
            if (cancellationToken.IsCancellationRequested || (accounts.IsEmpty && slots.IsEmpty)) return;
            HashingPrefetchKeys keys = new(accounts, slots);
            _flatReader.Prefetch(ref keys, cancellationToken);
        }

        /// <summary>Hashes each key when the reader encodes it; one account's adjacent slots share one account hash.</summary>
        private ref struct HashingPrefetchKeys(ReadOnlySpan<Address> accounts, ReadOnlySpan<StorageCell> slots) : IHashedPrefetchKeys
        {
            private readonly ReadOnlySpan<Address> _accounts = accounts;
            private readonly ReadOnlySpan<StorageCell> _slots = slots;
            private Address? _slotAccount;
            private ValueHash256 _slotAccountHash;

            public readonly int AccountCount => _accounts.Length;
            public readonly int SlotCount => _slots.Length;

            public readonly void GetAccount(int index, out ValueHash256 account) => account = _accounts[index].ToAccountPath;

            public void GetSlot(int index, out ValueHash256 account, out ValueHash256 slot)
            {
                ref readonly StorageCell cell = ref _slots[index];
                if (_slotAccount is null || !cell.Address.Equals(_slotAccount))
                {
                    _slotAccount = cell.Address;
                    _slotAccountHash = cell.Address.ToAccountPath;
                }

                account = _slotAccountHash;
                slot = ValueKeccak.Zero;
                StorageTree.ComputeKeyWithLookup(cell.Index, ref slot);
            }
        }

        // A sampled key that misses the block cache is cold only if reading it takes disk time: a key in the OS page
        // cache reads in microseconds, and prefetching it would only repeat the read. The column remembers the outcome
        // (see PrefetchSampling), so a run of disk-cold or page-cache blocks does not time a read on every block.
        [SkipLocalsInit]
        private bool AccountsNeedPrefetch(ReadOnlySpan<Address> samples)
        {
            PrefetchSampling.Column? column = sampling?.Accounts;
            Span<byte> value = stackalloc byte[AccountSpanBufferSize];
            MissTimings misses = default;
            int sentinel = -1;
            // Under a page-cache outcome one probe is kept; only a slow read of it makes the block decide in full. That
            // read warms its key, so it is counted here and not read again.
            if (column?.TrustMissesInMemory() == true)
            {
                sentinel = samples.Length / 2;
                if (!IsSlowAccountMiss(samples[sentinel].ToAccountPath, value)) return false;
                misses.AddSlowSentinel();
            }

            int probes = column?.TryProbeFewer() == true ? Math.Min(samples.Length, CachedRunSamples) : samples.Length;
            for (int i = 0; i < probes; i++)
            {
                int index = SampleIndex(i, probes, samples.Length);
                if (index == sentinel) continue;
                ValueHash256 path = samples[index].ToAccountPath;
                if (_flatReader.IsAccountCached(path)) continue;
                if (misses.Missed++ == 0 && column?.TrustMissAsDiskRead() == true) return true;

                long start = Timestamp();
                int length = _flatReader.GetAccount(path, value);
                long end = Timestamp();
                // An absent key, such as a new account, is answered by the filter and says nothing about where the
                // existing keys live, so its time is not counted.
                if (length != 0 && misses.Add(IsDiskRead(start, end))) break;
            }

            return Decide(column, misses, probes == samples.Length);
        }

        private bool IsSlowAccountMiss(in ValueHash256 path, Span<byte> value)
        {
            if (_flatReader.IsAccountCached(path)) return false;
            long start = Timestamp();
            int length = _flatReader.GetAccount(path, value);
            return length != 0 && IsDiskRead(start, Timestamp());
        }

        private bool SlotsNeedPrefetch(ReadOnlySpan<StorageCell> samples)
        {
            PrefetchSampling.Column? column = sampling?.Slots;
            MissTimings misses = default;
            int sentinel = -1;
            // Under a page-cache outcome one probe is kept; only a slow read of it makes the block decide in full. That
            // read warms its key, so it is counted here and not read again.
            if (column?.TrustMissesInMemory() == true)
            {
                sentinel = samples.Length / 2;
                if (!IsSlowSlotMiss(samples[sentinel])) return false;
                misses.AddSlowSentinel();
            }

            int probes = column?.TryProbeFewer() == true ? Math.Min(samples.Length, CachedRunSamples) : samples.Length;
            for (int i = 0; i < probes; i++)
            {
                int index = SampleIndex(i, probes, samples.Length);
                if (index == sentinel) continue;
                ref readonly StorageCell cell = ref samples[index];
                ValueHash256 path = cell.Address.ToAccountPath;
                ValueHash256 slotHash = ValueKeccak.Zero;
                StorageTree.ComputeKeyWithLookup(cell.Index, ref slotHash);
                if (_flatReader.IsSlotCached(path, slotHash)) continue;
                if (misses.Missed++ == 0 && column?.TrustMissAsDiskRead() == true) return true;

                UInt256 value = default;
                long start = Timestamp();
                bool found = _flatReader.TryGetStorage(path, slotHash, ref value);
                long end = Timestamp();
                // An absent key, such as a slot written for the first time, is answered by the filter and says nothing
                // about where the existing keys live, so its time is not counted.
                if (found && misses.Add(IsDiskRead(start, end))) break;
            }

            return Decide(column, misses, probes == samples.Length);
        }

        private bool IsSlowSlotMiss(in StorageCell cell)
        {
            ValueHash256 path = cell.Address.ToAccountPath;
            ValueHash256 slotHash = ValueKeccak.Zero;
            StorageTree.ComputeKeyWithLookup(cell.Index, ref slotHash);
            if (_flatReader.IsSlotCached(path, slotHash)) return false;
            UInt256 value = default;
            long start = Timestamp();
            bool found = _flatReader.TryGetStorage(path, slotHash, ref value);
            return found && IsDiskRead(start, Timestamp());
        }

        private readonly long Timestamp() => sampling?.GetTimestamp() ?? Stopwatch.GetTimestamp();
    }

    private static bool Decide(PrefetchSampling.Column? column, in MissTimings misses, bool allSamples)
    {
        if (misses.IsDisk)
        {
            column?.RecordDiskRead();
            return true;
        }

        if (misses.Timed != 0) column?.RecordPageCacheReads();
        else column?.RecordNoTimedMiss(fullyCached: allSamples && misses.Missed == 0);
        return false;
    }

    /// <summary>
    /// The sampled misses of one column and the timed reads of the existing ones. Two slow reads mean disk, and so does
    /// one slow read that is at least a quarter of the timed misses, as in a partly cold block. One slow read among
    /// many fast ones is a scheduler delay or a large page-cache block, and a wrong disk outcome is trusted for
    /// <see cref="PrefetchSampling.RecheckInterval"/> blocks with a miss.
    /// </summary>
    private struct MissTimings
    {
        private const int SlowReadsForDisk = 2;
        private const int MissesPerSlowRead = 4;

        /// <summary>The sampled keys that missed the block cache, absent ones included.</summary>
        public int Missed;

        public int Timed { get; private set; }
        private int _slow;

        /// <summary>Counts the page-cache probe whose slow read made the block decide in full.</summary>
        public void AddSlowSentinel()
        {
            Missed++;
            Add(slow: true);
        }

        /// <returns>True when the outcome is decided as disk and no further sample needs a read.</returns>
        public bool Add(bool slow)
        {
            Timed++;
            if (slow) _slow++;
            return _slow == SlowReadsForDisk;
        }

        public readonly bool IsDisk => _slow >= SlowReadsForDisk || (_slow == 1 && Timed <= MissesPerSlowRead);
    }

    public class Reader<TFlatReader, TTrieReader>(
        TFlatReader flatReader,
        TTrieReader trieReader,
        StateId currentState,
        IDisposable disposer)
        : IPersistence.IPersistenceReader, IPersistence.IPrefetchingReader
        where TFlatReader : struct, IFlatReader
        where TTrieReader : struct, ITrieReader
    {
        private TTrieReader _trieReader = trieReader;
        private TFlatReader _flatReader = flatReader;

        public StateId CurrentState { get; } = currentState;

        public void Dispose() => disposer.Dispose();

        public Account? GetAccount(Address address) =>
            _flatReader.GetAccount(address);

        public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue) =>
            _flatReader.TryGetSlot(address, in slot, ref outValue);

        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) =>
            _trieReader.TryLoadStateRlp(path, flags);

        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) =>
            _trieReader.TryLoadStorageRlp(address, path, flags);

        public bool TryLoadStateRlp<TArg>(in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct =>
            _trieReader.TryLoadStateRlp(path, flags, action, arg);

        public bool TryLoadStorageRlp<TArg>(Hash256 address, in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct =>
            _trieReader.TryLoadStorageRlp(address, path, flags, action, arg);

        public byte[]? GetAccountRaw(in ValueHash256 addrHash) =>
            _flatReader.GetAccountRaw(addrHash);

        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref UInt256 value) =>
            _flatReader.TryGetSlotRaw(addrHash, slotHash, ref value);

        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) =>
            _flatReader.CreateAccountIterator(startKey, endKey);

        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) =>
            _flatReader.CreateStorageIterator(accountKey, startSlotKey, endSlotKey);

        public bool IsPreimageMode => _flatReader.IsPreimageMode;

        public bool SupportsPrefetch => _flatReader.SupportsPrefetch;

        public IPersistence.PrefetchColumns PlanPrefetch(Span<Address> accountSamples, Span<StorageCell> slotSamples) =>
            _flatReader.PlanPrefetch(accountSamples, slotSamples);

        public void Prefetch(IPersistence.PrefetchColumns columns, Span<Address> accounts, Span<StorageCell> slots, CancellationToken cancellationToken) =>
            _flatReader.Prefetch(columns, accounts, slots, cancellationToken);
    }

    public class WriteBatch<TFlatWriteBatch, TTrieWriteBatch>(
        in TFlatWriteBatch flatWriteBatch,
        TTrieWriteBatch trieWriteBatch,
        IDisposable disposer)
        : IPersistence.IWriteBatch
        where TFlatWriteBatch : struct, IFlatWriteBatch
        where TTrieWriteBatch : struct, ITrieWriteBatch
    {
        private TFlatWriteBatch _flatWriter = flatWriteBatch;
        private TTrieWriteBatch _trieWriteBatch = trieWriteBatch;

        public void Dispose() => disposer.Dispose();

        public void SelfDestruct(Address addr)
        {
            _flatWriter.SelfDestruct(addr);
            _trieWriteBatch.SelfDestruct(addr.ToAccountPath);
        }

        public void SelfDestructRange(in ValueHash256 fromPath, in ValueHash256 toPath)
        {
            _flatWriter.SelfDestructRange(fromPath, toPath);
            _trieWriteBatch.SelfDestructRange(fromPath, toPath);
        }

        public void SetAccount(Address addr, Account? account) =>
            _flatWriter.SetAccount(addr, account);

        public void SetStorage(Address addr, in UInt256 slot, in UInt256? value) =>
            _flatWriter.SetStorage(addr, slot, value);

        public void SetStateTrieNode(in TreePath path, scoped ReadOnlySpan<byte> rlp) =>
            _trieWriteBatch.SetStateTrieNode(path, rlp);

        public void SetStorageTrieNode(Hash256 address, in TreePath path, scoped ReadOnlySpan<byte> rlp) =>
            _trieWriteBatch.SetStorageTrieNode(address, path, rlp);

        public void SetStorageRawEncoded(in ValueHash256 addrHash, in ValueHash256 slotHash, scoped ReadOnlySpan<byte> rlpValue) =>
            _flatWriter.SetStorageRawEncoded(addrHash, slotHash, rlpValue);

        public void SetAccountRaw(in ValueHash256 addrHash, Account account) =>
            _flatWriter.SetAccountRaw(addrHash, account);

        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath) =>
            _flatWriter.DeleteAccountRange(fromPath, toPath);

        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath) =>
            _flatWriter.DeleteStorageRange(addressHash, fromPath, toPath);

        public void DeleteStateTrieNodeRange(in ValueHash256 from, in ValueHash256 to) =>
            _trieWriteBatch.DeleteStateTrieNodeRange(from, to);

        public void DeleteStorageTrieNodeRange(in ValueHash256 addressHash, in ValueHash256 from, in ValueHash256 to) =>
            _trieWriteBatch.DeleteStorageTrieNodeRange(addressHash, from, to);
    }
}
