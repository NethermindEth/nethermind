// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>
/// Runs one fixed op stream through the C# persistence stack and records every write, read result and column dump
/// as the golden vector the Rust persistence is replayed against.
/// </summary>
/// <remarks>
/// The stack is built as FlatWorldStateModule builds it: layout, then <see cref="CachedReaderPersistence"/>, then
/// optionally <see cref="CarryForwardCachingPersistence"/>. It runs over a <see cref="SnapshotableMemColumnsDb{TKey}"/>,
/// as the persistences snapshot the DB and a plain MemColumnsDb cannot. The slot encoding is forced by writing the
/// metadata record that BasePersistence.ResolveSlotEncoding reads before the persistence is created.
/// </remarks>
internal static class GoldenVectorGenerator
{
    public static readonly FlatLayout[] Layouts = [FlatLayout.Flat, FlatLayout.FlatInTrie, FlatLayout.PreimageFlatV1, FlatLayout.PreimageFlat];

    private const int Seed = 20261012;
    private const int ManySlotsCount = 12;

    private static readonly Address A = new("0xaaaaaaaa00000000000000000000000000000001");
    // Shares A's 4-byte storage key prefix in the preimage layouts, where the key is the address itself.
    private static readonly Address A2 = new("0xaaaaaaaa00000000000000000000000000000002");
    private static readonly Address B = new("0x00000000000000000000000000000000000000bb");
    private static readonly Address C = new("0xcccccccccccccccccccccccccccccccccccccccc");
    private static readonly Address D = new("0xdddddddddddddddddddddddddddddddddddddddd");
    private static readonly Address Absent = new("0x0000000000000000000000000000000000000404");
    private static readonly Address[] Addresses = [A, A2, B, C, D, Absent];

    private static readonly Hash256 HashA = new(A.ToAccountPath);
    private static readonly Hash256 HashC = new(C.ToAccountPath);
    private static readonly Hash256 HashAPrefixTwin = SharesPrefixWith(HashA);
    private static readonly Hash256 HashCPrefixTwin = SharesPrefixWith(HashC);

    // Account and storage hashes written through the raw (sync) operations.
    private static readonly ValueHash256 Raw1 = new("0x5000000000000000000000000000000000000000000000000000000000000001");
    private static readonly ValueHash256 Raw2 = new("0x5555555555555555555555555555555555555555555555555555555555555555");
    private static readonly ValueHash256 Raw3 = new("0x6000000000000000000000000000000000000000000000000000000000000000");
    private static readonly ValueHash256 Raw4 = new("0x7000000000000000000000000000000000000000000000000000000000000007");
    private static readonly ValueHash256 RawStorage = new("0x77777777aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private static readonly ValueHash256 RawStorageTwin = new("0x77777777bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
    private static readonly ValueHash256 RawSlot1 = new("0x0100000000000000000000000000000000000000000000000000000000000000");
    private static readonly ValueHash256 RawSlot2 = new("0x0200000000000000000000000000000000000000000000000000000000000000");
    private static readonly ValueHash256 RawSlot3 = new("0x0300000000000000000000000000000000000000000000000000000000000000");

    private static readonly ValueHash256 ZeroKey = ValueKeccak.Zero;
    private static readonly ValueHash256 MaxKey = new("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");
    private static readonly ValueHash256 UnusedKey = new("0xfffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffe");

    // Lengths 0-5 land in StateTopNodes, 6-15 in StateNodes, 16+ in FallbackNodes.
    private static readonly TreePath[] StatePaths =
    [
        P(""), P("1"), P("12"), P("123"), P("12345"), P("123456"), P("1234567"),
        P("123456789abcdef"), P("123456789abcdef0"), P("123456789abcdef01"),
        P("123456789abcdef0123456789abcdef"), P("123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
        P("123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0"),
        P("13"), P("f"), P("fe"), P("fedcba9"), P("fedcba9876543210fedc"),
        P("a5"), P("a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5"),
    ];

    private static readonly TreePath[] StoragePaths = [P(""), P("3"), P("7f"), P("8"), P("0123456789abcdef"), P("80000000000000000000")];
    private static readonly Hash256[] StorageNodeOwners = [HashA, HashAPrefixTwin, HashC, HashCPrefixTwin];

    private static readonly UInt256[] ManySlots = [.. Enumerable.Range(0, ManySlotsCount).Select(static i => (UInt256)i), 100];

    /// <summary>Returns the golden vector of one case as JSON, indented by one space to stay small, with a trailing newline.</summary>
    public static async Task<string> Generate(FlatLayout layout, bool rlpSlots, bool carryForward)
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        FlatTestDbs.ForceSlotEncoding(db, rlpSlots);
        IPersistence persistence = FlatTestDbs.CreateCSharpStack(db, layout, carryForward);

        Recorder recorder = new(db, persistence);
        try
        {
            new Scenario(recorder, IsPreimage(layout), rlpSlots).Run();
        }
        finally
        {
            await ((IAsyncDisposable)persistence).DisposeAsync();
        }

        JsonObject vector = new()
        {
            ["layout"] = layout.ToString(),
            ["slotEncoding"] = SlotEncodingName(rlpSlots),
            ["carryForward"] = carryForward,
            ["steps"] = recorder.Steps,
        };
        return vector.ToJsonString(new JsonSerializerOptions { WriteIndented = true, IndentSize = 1, NewLine = "\n" }) + "\n";
    }

    public static string FileName(FlatLayout layout, bool rlpSlots, bool carryForward) =>
        $"{layout}-{SlotEncodingName(rlpSlots)}-cf{(carryForward ? 1 : 0)}.json";

    private static string SlotEncodingName(bool rlpSlots) => rlpSlots ? "rlp" : "raw";

    private static bool IsPreimage(FlatLayout layout) => layout is FlatLayout.PreimageFlatV1 or FlatLayout.PreimageFlat;

    private static TreePath P(string nibbles) => TreePath.FromHexString(nibbles);

    private static Hash256 SharesPrefixWith(Hash256 hash)
    {
        byte[] bytes = hash.BytesToArray();
        for (int i = BasePersistence.StoragePrefixPortion; i < bytes.Length; i++) bytes[i] ^= 0xff;
        return new Hash256(bytes);
    }

    /// <summary>The op stream; every op the layout or slot encoding rejects is left out rather than recorded as an error.</summary>
    private sealed class Scenario(Recorder recorder, bool preimage, bool rlpSlots)
    {
        private readonly Random _random = new(Seed);

        public void Run()
        {
            StateId block0 = new(0, RandomHash());
            StateId block1 = new(1, RandomHash());
            StateId block2 = new(2, RandomHash());
            StateId block3 = new(3, RandomHash());

            recorder.Batch(StateId.PreGenesis, block0, WriteFlags.None, Populate);
            recorder.Read(ReaderFlags.None, ReadAll);

            recorder.Batch(block0, block1, WriteFlags.DisableWAL, batch =>
            {
                batch.SelfDestruct(A);
                batch.SetAccount(A, null);
                batch.SetAccount(D, null);
                batch.SetAccount(B, new Account(1, 0));
                for (int i = 0; i < 4; i++) batch.SetStorage(C, ManySlots[i], RandomValue());
                for (int i = 4; i < 7; i++) batch.SetStorage(C, ManySlots[i], null);
                batch.SetStorage(C, ManySlots[7], UInt256.Zero);
                batch.SetStorage(A2, 1, new UInt256(Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000100"), isBigEndian: true));
                batch.SetStateTrieNode(P(""), RandomBytes(90));
                batch.SetStateTrieNode(P("123"), RandomBytes(4));
            });
            recorder.Read(ReaderFlags.Sync, ReadAll);

            recorder.Batch(block1, block2, WriteFlags.None, batch =>
            {
                // Re-created after its self-destruct in the previous batch.
                batch.SetAccount(A, new Account(0, 5));
                batch.SetStorage(A, 0, 9);
                batch.SetStorage(A, 1, 1);

                // Re-created in the same batch: the self-destruct deletes what the batch's snapshot holds, the write after it stays.
                batch.SelfDestruct(A2);
                batch.SetStorage(A2, 2, 3);

                // Written before the range delete in the same batch, so not in the snapshot the delete scans.
                batch.SetStateTrieNode(P("fedc"), RandomBytes(40));
                batch.DeleteStateTrieNodeRange(StatePaths[^1].Path, StatePaths[^1].Path);
                batch.DeleteStateTrieNodeRange(
                    new ValueHash256("0xfe00000000000000000000000000000000000000000000000000000000000000"),
                    new ValueHash256("0xfeffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"));
                // From the lower bound of 123456: its subtree goes, the partially overlapping 12345 stays.
                batch.DeleteStateTrieNodeRange(
                    P("123456").ToLowerBoundPath(),
                    new ValueHash256("0x1234ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"));
                // Ends on the upper bound of 7f, so 7f goes while its sibling 8 and the root stay.
                batch.DeleteStorageTrieNodeRange(HashC, ZeroKey, P("7f").ToUpperBoundPath());
                batch.SetStateTrieNode(P("fe"), RandomBytes(32));
            });
            recorder.Read(ReaderFlags.FullScan, ReadAll);

            recorder.Batch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL, batch =>
            {
                if (!preimage)
                {
                    batch.DeleteAccountRange(Raw1, Raw2);
                    batch.DeleteAccountRange(Raw3, Raw3);
                    batch.SetAccountRaw(Raw4, new Account(4, 4, RandomHash(), RandomHash()));
                    ValueHash256[] slotKeys = SortedSlotKeys();
                    batch.DeleteStorageRange(HashC.ValueHash256, slotKeys[2], slotKeys[5]);
                    batch.DeleteStorageRange(RawStorage, RawSlot2, RawSlot3);
                    if (rlpSlots) batch.SetStorageRawEncoded(HashC.ValueHash256, SlotKey(0), Bytes.FromHexString("0x820102"));
                }

                batch.SetStorage(C, 100, 42);
            });
            // A sync source state never moves the state pointer, whatever the target.
            recorder.Batch(StateId.Sync, new StateId(9, RandomHash()), WriteFlags.None, batch => batch.SetAccount(D, new Account(7, 7)));
            recorder.Read(ReaderFlags.None, ReadAll);

            recorder.Batch(block2, block3, WriteFlags.DisableWAL, batch =>
            {
                batch.SetStorage(C, ManySlots[8], RandomValue());
                batch.SetStateTrieNode(StatePaths[^1], RandomBytes(33));
                batch.SetStorageTrieNode(HashC, P("7f"), RandomBytes(31));
            });
            recorder.Flush();
            recorder.Read(ReaderFlags.None, ReadAll);

            // No self-destruct, raw or range write, so the carry-forward cache keeps what the read above cached but
            // the keys written here.
            recorder.Batch(block3, new StateId(4, Keccak.Compute("4")), WriteFlags.None, batch =>
            {
                batch.SetAccount(B, new Account(2, 2));
                batch.SetAccount(D, null);
                batch.SetStorage(A, 0, null);
                batch.SetStorage(C, ManySlots[0], 77);
            });
            recorder.Read(ReaderFlags.None, ReadCached);

            recorder.Clear();
            recorder.Batch(StateId.PreGenesis, new StateId(10, RandomHash()), WriteFlags.None, batch =>
            {
                batch.SetAccount(A, new Account(1, 1));
                batch.SetStorage(A, 0, 1);
                batch.SetStateTrieNode(P(""), RandomBytes(50));
            });
            recorder.Read(ReaderFlags.None, ReadAll);
        }

        private void Populate(BatchRecorder batch)
        {
            batch.SetAccount(A, new Account(1, 1000, RandomHash(), RandomHash()));
            batch.SetAccount(A2, new Account(0, 0));
            batch.SetAccount(B, new Account(0, UInt256.MaxValue));
            batch.SetAccount(C, new Account(1, 0, RandomHash(), RandomHash()));
            batch.SetAccount(D, new Account(5, 1));
            batch.SetAccount(Absent, null);

            batch.SetStorage(A, 0, 1);
            batch.SetStorage(A, 1, new UInt256(Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000ff00"), isBigEndian: true));
            batch.SetStorage(A, UInt256.MaxValue, new UInt256(Bytes.FromHexString("0x8000000000000000000000000000000000000000000000000000000000000001"), isBigEndian: true));
            batch.SetStorage(A, 2, UInt256.Zero);
            batch.SetStorage(A, 3, null);
            batch.SetStorage(A2, 0, 7);
            foreach (UInt256 slot in ManySlots) batch.SetStorage(C, slot, RandomValue());

            // Inline (< 32 bytes) and hash-referenced node RLP.
            int[] rlpLengths = [1, 31, 32, 33];
            for (int i = 0; i < StatePaths.Length; i++) batch.SetStateTrieNode(StatePaths[i], RandomBytes(rlpLengths[i % rlpLengths.Length]));
            foreach (Hash256 owner in StorageNodeOwners)
            {
                foreach (TreePath path in StoragePaths) batch.SetStorageTrieNode(owner, path, RandomBytes(rlpLengths[_random.Next(rlpLengths.Length)]));
            }

            if (preimage) return;

            batch.SetAccountRaw(Raw1, new Account(1, 1));
            batch.SetAccountRaw(Raw2, new Account(2, 2, RandomHash(), RandomHash()));
            batch.SetAccountRaw(Raw3, new Account(3, 3));
            if (!rlpSlots) return;

            // A self-destruct of A must keep the slot of an address hash sharing its 4-byte key prefix.
            batch.SetStorageRawEncoded(HashAPrefixTwin.ValueHash256, RawSlot1, Bytes.FromHexString("0x05"));
            // Trailing bytes after the RLP item are not stored.
            batch.SetStorageRawEncoded(RawStorage, RawSlot1, Bytes.FromHexString("0x8204d2dead"));
            batch.SetStorageRawEncoded(RawStorage, RawSlot2, Bytes.FromHexString("0x80"));
            batch.SetStorageRawEncoded(RawStorage, RawSlot3, Bytes.FromHexString("0xa0ff00000000000000000000000000000000000000000000000000000000000001"));
            batch.SetStorageRawEncoded(RawStorageTwin, RawSlot2, Bytes.FromHexString("0x07"));
        }

        private void ReadAll(ReadRecorder reader)
        {
            reader.CurrentState();
            reader.IsPreimageMode();

            foreach (Address address in Addresses) reader.GetAccount(address);

            foreach (UInt256 slot in (UInt256[])[0, 1, 2, 3, UInt256.MaxValue]) reader.TryGetSlot(A, slot);
            foreach (UInt256 slot in (UInt256[])[0, 1, 2]) reader.TryGetSlot(A2, slot);
            foreach (UInt256 slot in ManySlots) reader.TryGetSlot(C, slot);
            reader.TryGetSlot(Absent, 0);

            foreach (TreePath path in StatePaths) reader.TryLoadStateRlp(path, ReadFlags.None);
            reader.TryLoadStateRlp(P(""), ReadFlags.HintReadAhead);
            reader.TryLoadStateRlp(P("fedc"), ReadFlags.None);
            reader.TryLoadStateRlp(P("999"), ReadFlags.None);
            foreach (Hash256 owner in StorageNodeOwners)
            {
                foreach (TreePath path in StoragePaths) reader.TryLoadStorageRlp(owner, path, ReadFlags.None);
            }
            reader.TryLoadStorageRlp(HashC, P("0123456789abcdef"), ReadFlags.HintReadAhead);

            reader.TryGetStorageRaw(AccountKey(A), SlotKey(0));
            reader.TryGetStorageRaw(AccountKey(C), SlotKey(ManySlots[^1]));
            if (!preimage)
            {
                foreach (ValueHash256 addressHash in (ValueHash256[])[A.ToAccountPath, C.ToAccountPath, Raw1, Raw2, Raw3, Raw4]) reader.GetAccountRaw(addressHash);
                reader.TryGetStorageRaw(HashAPrefixTwin.ValueHash256, RawSlot1);
                foreach (ValueHash256 slot in (ValueHash256[])[RawSlot1, RawSlot2, RawSlot3]) reader.TryGetStorageRaw(RawStorage, slot);
                reader.TryGetStorageRaw(RawStorageTwin, RawSlot2);
            }

            reader.AccountIterator(ZeroKey, MaxKey);
            reader.AccountIterator(UnusedKey, UnusedKey);
            ValueHash256[] slotKeys = SortedSlotKeys();
            reader.StorageIterator(AccountKey(C), ZeroKey, MaxKey);
            reader.StorageIterator(AccountKey(C), slotKeys[3], slotKeys[8]);
            reader.StorageIterator(AccountKey(C), UnusedKey, UnusedKey);
            reader.StorageIterator(AccountKey(A), ZeroKey, MaxKey);
            reader.StorageIterator(AccountKey(A2), ZeroKey, MaxKey);
            reader.StorageIterator(AccountKey(Absent), ZeroKey, MaxKey);
        }

        /// <summary>The reads the carry-forward cache serves, twice, so that the second read of each is a cache hit.</summary>
        private static void ReadCached(ReadRecorder reader)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (Address address in Addresses) reader.GetAccount(address);
                foreach (UInt256 slot in (UInt256[])[0, 1]) reader.TryGetSlot(A, slot);
                foreach (UInt256 slot in ManySlots[..2]) reader.TryGetSlot(C, slot);
            }
        }

        /// <summary>The flat account key: the address hash, or in the preimage layouts the zero-padded address.</summary>
        private ValueHash256 AccountKey(Address address)
        {
            if (!preimage) return address.ToAccountPath;
            ValueHash256 key = ValueKeccak.Zero;
            address.Bytes.CopyTo(key.BytesAsSpan);
            return key;
        }

        /// <summary>The flat slot key: the slot hash, or in the preimage layouts the big-endian slot.</summary>
        private ValueHash256 SlotKey(in UInt256 slot)
        {
            ValueHash256 key = ValueKeccak.Zero;
            if (preimage) slot.ToBigEndian(key.BytesAsSpan);
            else StorageTree.ComputeKeyWithLookup(slot, ref key);
            return key;
        }

        private ValueHash256[] SortedSlotKeys() => [.. ManySlots.Select(slot => SlotKey(slot)).Order()];

        private byte[] RandomBytes(int length)
        {
            byte[] bytes = new byte[length];
            _random.NextBytes(bytes);
            return bytes;
        }

        private Hash256 RandomHash() => new(RandomBytes(32));

        /// <summary>A value of 1 to 32 significant bytes, so the stripped encodings vary in length.</summary>
        private UInt256 RandomValue() => new(RandomBytes(_random.Next(1, 33)), isBigEndian: true);
    }

    /// <summary>Applies steps to the persistence and records them, with the column dump after each write.</summary>
    private sealed class Recorder(IColumnsDb<FlatDbColumns> db, IPersistence persistence)
    {
        public JsonArray Steps { get; } = [];

        public void Batch(in StateId from, in StateId to, WriteFlags flags, Action<BatchRecorder> write)
        {
            BatchRecorder recorder;
            using (IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(from, to, flags))
            {
                recorder = new BatchRecorder(batch);
                write(recorder);
            }

            Steps.Add(new JsonObject
            {
                ["kind"] = "batch",
                ["from"] = Json(from),
                ["to"] = Json(to),
                ["writeFlags"] = (int)flags,
                ["ops"] = recorder.Ops,
                ["columns"] = DumpColumns(),
            });
        }

        public void Read(ReaderFlags flags, Action<ReadRecorder> read)
        {
            ReadRecorder recorder;
            using (IPersistence.IPersistenceReader reader = persistence.CreateReader(flags))
            {
                recorder = new ReadRecorder(reader);
                read(recorder);
            }

            Steps.Add(new JsonObject { ["kind"] = "read", ["readerFlags"] = (int)flags, ["expect"] = recorder.Expect });
        }

        public void Flush()
        {
            persistence.Flush();
            Steps.Add(new JsonObject { ["kind"] = "flush" });
        }

        public void Clear()
        {
            persistence.Clear();
            Steps.Add(new JsonObject { ["kind"] = "clear", ["columns"] = DumpColumns() });
        }

        private JsonObject DumpColumns()
        {
            JsonObject columns = [];
            foreach (FlatDbColumns column in Enum.GetValues<FlatDbColumns>())
            {
                IEnumerable<KeyValuePair<byte[], byte[]>> entries = db.GetColumnDb(column).GetAll().OrderBy(static entry => entry.Key, Bytes.Comparer);
                columns[column.ToString()] = new JsonArray([.. entries.Select(static entry => Pair(entry.Key, entry.Value))]);
            }
            return columns;
        }
    }

    /// <summary>Forwards every <see cref="IPersistence.IWriteBatch"/> call and records it under the method's camelCase name.</summary>
    private sealed class BatchRecorder(IPersistence.IWriteBatch batch)
    {
        public JsonArray Ops { get; } = [];

        public void SelfDestruct(Address address)
        {
            batch.SelfDestruct(address);
            Ops.Add(new JsonObject { ["op"] = "selfDestruct", ["address"] = Hex(address.Bytes) });
        }

        public void SetAccount(Address address, Account? account)
        {
            batch.SetAccount(address, account);
            Ops.Add(new JsonObject { ["op"] = "setAccount", ["address"] = Hex(address.Bytes), ["account"] = Json(account) });
        }

        public void SetStorage(Address address, in UInt256 slot, in UInt256? value)
        {
            batch.SetStorage(address, slot, value);
            Ops.Add(new JsonObject
            {
                ["op"] = "setStorage",
                ["address"] = Hex(address.Bytes),
                ["slot"] = Hex32(slot),
                ["value"] = value is { } set ? Hex32(set) : null,
            });
        }

        public void SetStateTrieNode(in TreePath path, byte[] rlp)
        {
            batch.SetStateTrieNode(path, rlp);
            Ops.Add(new JsonObject { ["op"] = "setStateTrieNode", ["path"] = Json(path), ["rlp"] = Hex(rlp) });
        }

        public void SetStorageTrieNode(Hash256 addressHash, in TreePath path, byte[] rlp)
        {
            batch.SetStorageTrieNode(addressHash, path, rlp);
            Ops.Add(new JsonObject { ["op"] = "setStorageTrieNode", ["addressHash"] = Hex(addressHash.Bytes), ["path"] = Json(path), ["rlp"] = Hex(rlp) });
        }

        public void SetStorageRawEncoded(in ValueHash256 addressHash, in ValueHash256 slotHash, byte[] rlpValue)
        {
            batch.SetStorageRawEncoded(addressHash, slotHash, rlpValue);
            Ops.Add(new JsonObject
            {
                ["op"] = "setStorageRawEncoded",
                ["addressHash"] = Hex(addressHash.Bytes),
                ["slotHash"] = Hex(slotHash.Bytes),
                ["rlpValue"] = Hex(rlpValue),
            });
        }

        public void SetAccountRaw(in ValueHash256 addressHash, Account account)
        {
            batch.SetAccountRaw(addressHash, account);
            Ops.Add(new JsonObject { ["op"] = "setAccountRaw", ["addressHash"] = Hex(addressHash.Bytes), ["account"] = Json(account) });
        }

        public void DeleteAccountRange(in ValueHash256 from, in ValueHash256 to)
        {
            batch.DeleteAccountRange(from, to);
            Ops.Add(new JsonObject { ["op"] = "deleteAccountRange", ["from"] = Hex(from.Bytes), ["to"] = Hex(to.Bytes) });
        }

        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 from, in ValueHash256 to)
        {
            batch.DeleteStorageRange(addressHash, from, to);
            Ops.Add(new JsonObject { ["op"] = "deleteStorageRange", ["addressHash"] = Hex(addressHash.Bytes), ["from"] = Hex(from.Bytes), ["to"] = Hex(to.Bytes) });
        }

        public void DeleteStateTrieNodeRange(in ValueHash256 from, in ValueHash256 to)
        {
            batch.DeleteStateTrieNodeRange(from, to);
            Ops.Add(new JsonObject { ["op"] = "deleteStateTrieNodeRange", ["from"] = Hex(from.Bytes), ["to"] = Hex(to.Bytes) });
        }

        public void DeleteStorageTrieNodeRange(Hash256 addressHash, in ValueHash256 from, in ValueHash256 to)
        {
            batch.DeleteStorageTrieNodeRange(addressHash.ValueHash256, from, to);
            Ops.Add(new JsonObject { ["op"] = "deleteStorageTrieNodeRange", ["addressHash"] = Hex(addressHash.Bytes), ["from"] = Hex(from.Bytes), ["to"] = Hex(to.Bytes) });
        }
    }

    /// <summary>Runs every <see cref="IPersistence.IPersistenceReader"/> call and records its inputs and <c>result</c>.</summary>
    private sealed class ReadRecorder(IPersistence.IPersistenceReader reader)
    {
        public JsonArray Expect { get; } = [];

        public void CurrentState() => Expect.Add(new JsonObject { ["op"] = "currentState", ["result"] = Json(reader.CurrentState) });

        public void IsPreimageMode() => Expect.Add(new JsonObject { ["op"] = "isPreimageMode", ["result"] = reader.IsPreimageMode });

        public void GetAccount(Address address) =>
            Expect.Add(new JsonObject { ["op"] = "getAccount", ["address"] = Hex(address.Bytes), ["result"] = Json(reader.GetAccount(address)) });

        public void TryGetSlot(Address address, in UInt256 slot)
        {
            UInt256 value = UInt256.Zero;
            bool found = reader.TryGetSlot(address, slot, ref value);
            Expect.Add(new JsonObject { ["op"] = "tryGetSlot", ["address"] = Hex(address.Bytes), ["slot"] = Hex32(slot), ["result"] = Slot(found, value) });
        }

        public void TryLoadStateRlp(in TreePath path, ReadFlags flags) =>
            Expect.Add(new JsonObject
            {
                ["op"] = "tryLoadStateRlp",
                ["path"] = Json(path),
                ["readFlags"] = (int)flags,
                ["result"] = HexOrNull(reader.TryLoadStateRlp(path, flags)),
            });

        public void TryLoadStorageRlp(Hash256 addressHash, in TreePath path, ReadFlags flags) =>
            Expect.Add(new JsonObject
            {
                ["op"] = "tryLoadStorageRlp",
                ["addressHash"] = Hex(addressHash.Bytes),
                ["path"] = Json(path),
                ["readFlags"] = (int)flags,
                ["result"] = HexOrNull(reader.TryLoadStorageRlp(addressHash, path, flags)),
            });

        public void GetAccountRaw(in ValueHash256 addressHash) =>
            Expect.Add(new JsonObject { ["op"] = "getAccountRaw", ["addressHash"] = Hex(addressHash.Bytes), ["result"] = HexOrNull(reader.GetAccountRaw(addressHash)) });

        public void TryGetStorageRaw(in ValueHash256 addressHash, in ValueHash256 slotHash)
        {
            UInt256 value = UInt256.Zero;
            bool found = reader.TryGetStorageRaw(addressHash, slotHash, ref value);
            Expect.Add(new JsonObject
            {
                ["op"] = "tryGetStorageRaw",
                ["addressHash"] = Hex(addressHash.Bytes),
                ["slotHash"] = Hex(slotHash.Bytes),
                ["result"] = Slot(found, value),
            });
        }

        public void AccountIterator(in ValueHash256 start, in ValueHash256 end)
        {
            using IPersistence.IFlatIterator iterator = reader.CreateAccountIterator(start, end);
            Expect.Add(new JsonObject { ["op"] = "accountIterator", ["start"] = Hex(start.Bytes), ["end"] = Hex(end.Bytes), ["result"] = Drain(iterator) });
        }

        public void StorageIterator(in ValueHash256 accountKey, in ValueHash256 start, in ValueHash256 end)
        {
            using IPersistence.IFlatIterator iterator = reader.CreateStorageIterator(accountKey, start, end);
            Expect.Add(new JsonObject
            {
                ["op"] = "storageIterator",
                ["accountKey"] = Hex(accountKey.Bytes),
                ["start"] = Hex(start.Bytes),
                ["end"] = Hex(end.Bytes),
                ["result"] = Drain(iterator),
            });
        }

        private static JsonArray Drain(IPersistence.IFlatIterator iterator)
        {
            JsonArray entries = [];
            while (iterator.MoveNext())
            {
                ValueHash256 key = iterator.CurrentKey;
                entries.Add(Pair(key.Bytes, iterator.CurrentValue));
            }
            return entries;
        }

        private static JsonObject Slot(bool found, in UInt256 value) => new() { ["found"] = found, ["value"] = Hex32(value) };
    }

    internal static string Hex(ReadOnlySpan<byte> bytes) => bytes.ToHexString(true);

    internal static string? HexOrNull(byte[]? bytes) => bytes is null ? null : Hex(bytes);

    internal static string Hex32(in UInt256 value) => Hex(value.ToBigEndian());

    /// <summary>A nonce or balance: its big-endian bytes without leading zeros, zero being <c>0x00</c>.</summary>
    private static string Quantity(in UInt256 value) => Hex(value.ToBigEndian().WithoutLeadingZeros());

    internal static JsonArray Pair(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value) => [Hex(key), Hex(value)];

    internal static JsonObject Json(in StateId stateId)
    {
        ValueHash256 root = stateId.StateRoot;
        return new JsonObject { ["block"] = stateId.BlockNumber, ["root"] = Hex(root.Bytes) };
    }

    internal static JsonObject Json(in TreePath path) => new() { ["path"] = Hex(path.Path.Bytes), ["length"] = path.Length };

    internal static JsonObject? Json(Account? account) => account is null
        ? null
        : new JsonObject
        {
            ["nonce"] = Quantity(account.Nonce),
            ["balance"] = Quantity(account.Balance),
            ["storageRoot"] = Hex(account.StorageRoot.Bytes),
            ["codeHash"] = Hex(account.CodeHash.Bytes),
        };
}
