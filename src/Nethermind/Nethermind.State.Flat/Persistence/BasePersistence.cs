// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
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
    public const int StoragePrefixPortion = 4;

    private static readonly byte[] CurrentStateKey = Keccak.Compute("CurrentState").BytesToArray();
    private static readonly byte[] LayoutKey = Keccak.Compute("Layout").BytesToArray();
    private static readonly byte[] SlotEncodingKey = Keccak.Compute("SlotEncoding").BytesToArray();
    private static readonly byte[] IngestMarkerKey = Keccak.Compute("SstIngestMarker").BytesToArray();

    /// <summary>Raw storage slot encoding: the stripped value bytes are stored verbatim. Legacy, deprecated.</summary>
    internal const byte SlotEncodingRaw = 0;

    /// <summary>RLP storage slot encoding: the stripped value is stored as an RLP byte string.</summary>
    internal const byte SlotEncodingRlp = 1;

    private const string RawSlotDeprecationMessage =
        "Flat DB uses the legacy raw storage slot encoding, which is deprecated and will be removed in a future release. Please resync to adopt the RLP slot encoding.";

    internal static StateId ReadCurrentState(IReadOnlyKeyValueStore kv)
    {
        byte[]? bytes = kv.Get(CurrentStateKey);
        return bytes is null || bytes.Length == 0
            ? new StateId(ulong.MaxValue, ValueKeccak.EmptyTreeHash)
            : new StateId(BinaryPrimitives.ReadUInt64BigEndian(bytes), new ValueHash256(bytes[8..]));
    }

    internal static void SetCurrentState(IWriteOnlyKeyValueStore kv, in StateId stateId)
    {
        Span<byte> bytes = stackalloc byte[8 + 32];
        BinaryPrimitives.WriteUInt64BigEndian(bytes[..8], stateId.BlockNumber);
        stateId.StateRoot.BytesAsSpan.CopyTo(bytes[8..]);
        kv.PutSpan(CurrentStateKey, bytes);
    }

    /// <summary>
    /// Durable redo marker for an SST-ingest persist: target state plus the staged file names. Written (WAL-synced)
    /// before the first ingest; cleared atomically with the <see cref="CurrentStateKey"/> advance. A marker found at
    /// startup means the process died mid-commit and the persist must be rolled forward.
    /// </summary>
    internal static void SetIngestMarker(IWriteOnlyKeyValueStore kv, in StateId to, IReadOnlyList<string> files)
    {
        int size = 8 + 32;
        foreach (string file in files) size += 2 + Encoding.UTF8.GetByteCount(Path.GetFileName(file));

        Span<byte> bytes = size <= 4096 ? stackalloc byte[size] : new byte[size];
        BinaryPrimitives.WriteUInt64BigEndian(bytes[..8], to.BlockNumber);
        to.StateRoot.BytesAsSpan.CopyTo(bytes[8..]);
        int offset = 8 + 32;
        foreach (string file in files)
        {
            int written = Encoding.UTF8.GetBytes(Path.GetFileName(file), bytes[(offset + 2)..]);
            BinaryPrimitives.WriteUInt16BigEndian(bytes[offset..], (ushort)written);
            offset += 2 + written;
        }

        kv.PutSpan(IngestMarkerKey, bytes);
    }

    internal static (StateId To, string[] Files)? ReadIngestMarker(IReadOnlyKeyValueStore kv)
    {
        byte[]? bytes = kv.Get(IngestMarkerKey);
        if (bytes is null || bytes.Length < 8 + 32) return null;

        StateId to = new(BinaryPrimitives.ReadUInt64BigEndian(bytes), new ValueHash256(bytes.AsSpan(8, 32)));
        List<string> files = [];
        int offset = 8 + 32;
        while (offset + 2 <= bytes.Length)
        {
            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset));
            offset += 2;
            if (offset + length > bytes.Length) throw new InvalidDataException("Flat DB SST ingest marker is truncated");
            files.Add(Encoding.UTF8.GetString(bytes, offset, length));
            offset += length;
        }

        return (to, files.ToArray());
    }

    internal static void ClearIngestMarker(IWriteOnlyKeyValueStore kv) => kv.Remove(IngestMarkerKey);

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

    internal static void ClearAllColumns(IColumnsDb<FlatDbColumns> db)
    {
        // Delete in bounded batches; a single batch over every key exhausts memory when wiping a large
        // partially-synced DB on restart. #11442
        const int batchSize = 10_000;

        IColumnsWriteBatch<FlatDbColumns> batch = db.StartWriteBatch();
        try
        {
            int count = 0;
            foreach (FlatDbColumns column in Enum.GetValues<FlatDbColumns>())
            {
                if (column == FlatDbColumns.Metadata)
                {
                    // Preserve the format markers; wiping them makes a re-synced RLP DB read back as raw. #11996
                    batch.GetColumnBatch(column).Remove(CurrentStateKey);
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
        }
        finally
        {
            batch.Dispose();
        }
    }

    internal static void CreateStorageRange(
        ReadOnlySpan<byte> accountPath,
        Span<byte> firstKey,
        Span<byte> lastKey)
    {
        accountPath[..StoragePrefixPortion].CopyTo(firstKey);
        accountPath[..StoragePrefixPortion].CopyTo(lastKey);
        firstKey[StoragePrefixPortion..].Clear();
        lastKey[StoragePrefixPortion..].Fill(0xff);
    }

    /// <summary>
    /// Scan a key range and delete all entries matching the expected key length.
    /// </summary>
    internal static void DeleteMatchingKeys(
        ISortedKeyValueStore snap,
        IWriteBatch batch,
        ReadOnlySpan<byte> firstKey,
        ReadOnlySpan<byte> lastKey,
        int expectedKeyLength)
    {
        using ISortedView view = snap.GetViewBetween(firstKey, lastKey);
        while (view.MoveNext())
        {
            if (view.CurrentKey.Length == expectedKeyLength)
                batch.Remove(view.CurrentKey);
        }
    }

    /// <summary>
    /// Scan a key range and delete entries matching the address suffix at the given offset.
    /// </summary>
    internal static void DeleteMatchingKeys(
        ISortedKeyValueStore snap,
        IWriteBatch batch,
        ReadOnlySpan<byte> firstKey,
        ReadOnlySpan<byte> lastKey,
        int suffixOffset,
        ReadOnlySpan<byte> expectedSuffix)
    {
        using ISortedView view = snap.GetViewBetween(firstKey, lastKey);
        while (view.MoveNext())
        {
            ReadOnlySpan<byte> key = view.CurrentKey;
            if (key.Length >= suffixOffset + expectedSuffix.Length && Bytes.AreEqual(key[suffixOffset..], expectedSuffix))
                batch.Remove(view.CurrentKey);
        }
    }

    public interface IHashedFlatReader
    {
        public int GetAccount(in ValueHash256 address, Span<byte> outBuffer);
        public bool TryGetStorage(in ValueHash256 address, in ValueHash256 slot, ref SlotValue outValue);
        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey);
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey);
        public bool IsPreimageMode { get; }
    }

    public interface IHashedFlatWriteBatch
    {
        public void SelfDestruct(in ValueHash256 address);

        public void RemoveAccount(in ValueHash256 address);

        public void SetAccount(in ValueHash256 address, ReadOnlySpan<byte> value);

        public void SetStorage(in ValueHash256 address, in ValueHash256 slotHash, in SlotValue? value);

        /// <summary>Writes a slot whose value is already the trie-leaf RLP byte string (<c>RLP(stripped)</c>).</summary>
        public void SetStorageEncoded(in ValueHash256 address, in ValueHash256 slotHash, scoped ReadOnlySpan<byte> rlpValue);

        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath);

        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath);
    }

    public interface IFlatReader
    {
        public Account? GetAccount(Address address);
        public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue);
        public byte[]? GetAccountRaw(in ValueHash256 addrHash);
        public bool TryGetSlotRaw(in ValueHash256 address, in ValueHash256 slotHash, ref SlotValue outValue);
        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey);
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey);
        public bool IsPreimageMode { get; }
    }

    public interface IFlatWriteBatch
    {
        public void SelfDestruct(Address addr);

        public void SetAccount(Address addr, Account? account);

        public void SetStorage(Address addr, in UInt256 slot, in SlotValue? value);

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
    }

    public interface ITrieWriteBatch
    {
        public void SelfDestruct(in ValueHash256 address);
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

        public void SetStorage(Address addr, in UInt256 slot, in SlotValue? value)
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
        bool useFlatAccount = true
    ) : IFlatReader
        where TFlatReader : struct, IHashedFlatReader
    {
        private readonly AccountDecoder _accountDecoder = useFlatAccount ? AccountDecoder.Slim : AccountDecoder.Instance;
        private readonly int _accountSpanBufferSize = 256;
        private TFlatReader _flatReader = flatReader;

        public Account? GetAccount(Address address)
        {
            Span<byte> valueBuffer = stackalloc byte[_accountSpanBufferSize];
            int responseSize = _flatReader.GetAccount(address.ToAccountPath, valueBuffer);
            if (responseSize == 0)
            {
                return null;
            }

            RlpReader ctx = new(valueBuffer[..responseSize]);
            return _accountDecoder.Decode(ref ctx);
        }

        public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue)
        {
            ValueHash256 slotHash = ValueKeccak.Zero;
            StorageTree.ComputeKeyWithLookup(slot, ref slotHash);

            return TryGetSlotRaw(address.ToAccountPath, slotHash, ref outValue);
        }

        public byte[]? GetAccountRaw(in ValueHash256 addrHash)
        {
            Span<byte> valueBuffer = stackalloc byte[_accountSpanBufferSize];
            int responseSize = _flatReader.GetAccount(addrHash, valueBuffer);
            return responseSize == 0 ? null : valueBuffer[..responseSize].ToArray();
        }

        public bool TryGetSlotRaw(in ValueHash256 address, in ValueHash256 slotHash, ref SlotValue outValue) =>
            _flatReader.TryGetStorage(address, slotHash, ref outValue);

        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) =>
            _flatReader.CreateAccountIterator(startKey, endKey);

        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) =>
            _flatReader.CreateStorageIterator(accountKey, startSlotKey, endSlotKey);

        public bool IsPreimageMode => _flatReader.IsPreimageMode;
    }

    public class Reader<TFlatReader, TTrieReader>(
        TFlatReader flatReader,
        TTrieReader trieReader,
        StateId currentState,
        IDisposable disposer)
        : IPersistence.IPersistenceReader
        where TFlatReader : struct, IFlatReader
        where TTrieReader : struct, ITrieReader
    {
        private TTrieReader _trieReader = trieReader;
        private TFlatReader _flatReader = flatReader;

        public StateId CurrentState { get; } = currentState;

        public void Dispose() => disposer.Dispose();

        public Account? GetAccount(Address address) =>
            _flatReader.GetAccount(address);

        public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue) =>
            _flatReader.TryGetSlot(address, in slot, ref outValue);

        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) =>
            _trieReader.TryLoadStateRlp(path, flags);

        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) =>
            _trieReader.TryLoadStorageRlp(address, path, flags);

        public byte[]? GetAccountRaw(in ValueHash256 addrHash) =>
            _flatReader.GetAccountRaw(addrHash);

        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref SlotValue value) =>
            _flatReader.TryGetSlotRaw(addrHash, slotHash, ref value);

        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) =>
            _flatReader.CreateAccountIterator(startKey, endKey);

        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) =>
            _flatReader.CreateStorageIterator(accountKey, startSlotKey, endSlotKey);

        public bool IsPreimageMode => _flatReader.IsPreimageMode;
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

        public void SetAccount(Address addr, Account? account) =>
            _flatWriter.SetAccount(addr, account);

        public void SetStorage(Address addr, in UInt256 slot, in SlotValue? value) =>
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
