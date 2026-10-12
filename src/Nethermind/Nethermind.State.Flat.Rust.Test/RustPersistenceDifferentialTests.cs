// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>
/// One seeded random op stream through the C# persistence stack and <see cref="RustPersistence"/>, each on a database
/// of its own: after every batch each column must be byte-identical, and every read must return the same, or fail the
/// same way.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class RustPersistenceDifferentialTests
{
    private static readonly FlatLayout[] Layouts = GoldenVectorGenerator.Layouts;

    [Test]
    public async Task Rust_persistence_matches_the_csharp_stack(
        [ValueSource(nameof(Layouts))] FlatLayout layout,
        [Values] bool rlpSlots,
        [Values] bool carryForward,
        [Values] FlatTestDbs.Kind dbKind)
    {
        RustNativeLibrary.Require();
        using TempPath csharpPath = TempPath.GetTempDirectory();
        using TempPath rustPath = TempPath.GetTempDirectory();
        using IColumnsDb<FlatDbColumns> csharpDb = FlatTestDbs.Open(dbKind, csharpPath.Path);
        using IColumnsDb<FlatDbColumns> rustDb = FlatTestDbs.Open(dbKind, rustPath.Path);
        FlatTestDbs.ForceSlotEncoding(csharpDb, rlpSlots);
        FlatTestDbs.ForceSlotEncoding(rustDb, rlpSlots);

        IPersistence csharp = FlatTestDbs.CreateCSharpStack(csharpDb, layout, carryForward);
        RustPersistence rust = new(rustDb, new FlatDbConfig { Layout = layout, EnableCarryForwardCache = carryForward }, LimboLogs.Instance);
        try
        {
            int seed = 20261012 + (int)layout * 8 + (rlpSlots ? 4 : 0) + (carryForward ? 2 : 0) + (int)dbKind;
            new Scenario(new Random(seed), layout, csharp, rust, csharpDb, rustDb).Run();
        }
        finally
        {
            rust.Dispose();
            await ((IAsyncDisposable)csharp).DisposeAsync();
        }
    }

    private sealed class Scenario(Random random, FlatLayout layout, IPersistence csharp, IPersistence rust, IColumnsDb<FlatDbColumns> csharpDb, IColumnsDb<FlatDbColumns> rustDb)
    {
        private const int BatchCount = 16;
        private const int MaxOpsPerBatch = 60;
        // Enough writes without the write-ahead log for the batch to write them out every 256 on the way.
        private const int LargeBatchIndex = 1;
        private const int LargeBatchOps = 1000;
        private const int ReadsPerStep = 60;

        private static readonly ValueHash256 MaxKey = new("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

        private readonly bool _preimage = layout is FlatLayout.PreimageFlatV1 or FlatLayout.PreimageFlat;
        private readonly Address[] _addresses = CreateAddresses(random);
        private readonly UInt256[] _slots = [0, 1, 2, 3, 7, 255, 256, 1_000_000, UInt256.MaxValue, new UInt256(Bytes.FromHexString("0x8000000000000000000000000000000000000000000000000000000000000001"), isBigEndian: true)];
        private readonly TreePath[] _paths = CreatePaths(random);
        private ValueHash256[] _accountKeys = [];
        private ValueHash256[] _slotKeys = [];
        private StateId _current = StateId.PreGenesis;
        private string _step = "";

        public void Run()
        {
            _accountKeys = [.. _addresses.Select(AccountKey), .. Enumerable.Range(0, 4).Select(_ => RandomHash()), ValueKeccak.Zero, MaxKey];
            _slotKeys = [.. _slots.Select(SlotKey), .. Enumerable.Range(0, 4).Select(_ => RandomHash()), ValueKeccak.Zero, MaxKey];

            Read();
            for (int i = 0; i < BatchCount; i++)
            {
                _step = $"batch {i}";
                Batch(i);
                AssertSameColumns();
                Read();

                if (i == BatchCount / 2)
                {
                    _step = "flush";
                    Same(static persistence => { persistence.Flush(); return 0; });
                }

                if (i == BatchCount * 3 / 4)
                {
                    _step = "clear";
                    Same(static persistence => { persistence.Clear(); return 0; });
                    _current = StateId.PreGenesis;
                    AssertSameColumns();
                    Read();
                }
            }
        }

        private void Batch(int index)
        {
            StateId from = _current;
            StateId to = new((ulong)index + 1, RandomHash());
            switch (random.Next(8))
            {
                case 0:
                    from = StateId.Sync;
                    to = StateId.Sync;
                    break;
                case 1:
                    // A sync source state never moves the state pointer, whatever the target.
                    from = StateId.Sync;
                    break;
                case 2:
                    StateId wrongBase = new(_current.BlockNumber + 7, RandomHash());
                    (IPersistence.IWriteBatch? csharpWrong, IPersistence.IWriteBatch? rustWrong) = Same(persistence => persistence.CreateWriteBatch(wrongBase, to), "a batch off the persisted state");
                    Assert.That(csharpWrong, Is.Null, "a batch off the persisted state is refused");
                    rustWrong?.Dispose();
                    break;
            }

            bool large = index == LargeBatchIndex;
            WriteFlags flags = large || random.Next(2) != 0 ? WriteFlags.DisableWAL : WriteFlags.None;
            (IPersistence.IWriteBatch? csharpBatch, IPersistence.IWriteBatch? rustBatch) = Same(persistence => persistence.CreateWriteBatch(from, to, flags));
            using (csharpBatch)
            using (rustBatch)
            {
                int opCount = large ? LargeBatchOps : random.Next(MaxOpsPerBatch);
                for (int op = 0; op < opCount; op++)
                {
                    (string name, Action<IPersistence.IWriteBatch> apply) = RandomOp();
                    Exception? csharpError = Catch(() => apply(csharpBatch!));
                    Exception? rustError = Catch(() => apply(rustBatch!));
                    AssertSameFailure(csharpError, rustError, $"op {op} {name}");
                }
            }

            if (from != StateId.Sync && to != StateId.Sync) _current = to;
        }

        private (string, Action<IPersistence.IWriteBatch>) RandomOp()
        {
            Address address = RandomAddress();
            UInt256 slot = RandomSlot();
            TreePath path = RandomPath();
            ValueHash256 accountKey = RandomAccountKey();
            switch (random.Next(14))
            {
                case 0:
                    return ($"SelfDestruct({address})", batch => batch.SelfDestruct(address));
                case 1:
                case 2:
                    Account account = RandomAccount();
                    return ($"SetAccount({address})", batch => batch.SetAccount(address, account));
                case 3:
                    return ($"SetAccount({address}, null)", batch => batch.SetAccount(address, null));
                case 4:
                case 5:
                    UInt256 value = RandomValue();
                    return ($"SetStorage({address}, {slot}, {value})", batch => batch.SetStorage(address, slot, value));
                case 6:
                    return ($"SetStorage({address}, {slot}, null)", batch => batch.SetStorage(address, slot, null));
                case 7:
                    byte[] stateRlp = RandomRlp();
                    return ($"SetStateTrieNode({path})", batch => batch.SetStateTrieNode(path, stateRlp));
                case 8:
                    Hash256 owner = new(RandomOwner());
                    byte[] storageRlp = RandomRlp();
                    return ($"SetStorageTrieNode({owner}, {path})", batch => batch.SetStorageTrieNode(owner, path, storageRlp));
                case 9:
                    ValueHash256 slotKey = RandomSlotKey();
                    byte[] encoded = RandomEncodedSlot();
                    return ($"SetStorageRawEncoded({accountKey}, {slotKey}, {encoded.ToHexString()})", batch => batch.SetStorageRawEncoded(accountKey, slotKey, encoded));
                case 10:
                    Account rawAccount = RandomAccount();
                    return ($"SetAccountRaw({accountKey})", batch => batch.SetAccountRaw(accountKey, rawAccount));
                case 11:
                    (ValueHash256 fromKey, ValueHash256 toKey) = RandomRange(_accountKeys);
                    return random.Next(2) == 0
                        ? ($"DeleteAccountRange({fromKey}, {toKey})", batch => batch.DeleteAccountRange(fromKey, toKey))
                        : ($"DeleteStorageRange({accountKey}, {fromKey}, {toKey})", batch => batch.DeleteStorageRange(accountKey, fromKey, toKey));
                case 12:
                    (ValueHash256 fromPath, ValueHash256 toPath) = RandomNodeRange();
                    return ($"DeleteStateTrieNodeRange({fromPath}, {toPath})", batch => batch.DeleteStateTrieNodeRange(fromPath, toPath));
                default:
                    ValueHash256 nodeOwner = RandomOwner();
                    (ValueHash256 fromStoragePath, ValueHash256 toStoragePath) = RandomNodeRange();
                    return ($"DeleteStorageTrieNodeRange({nodeOwner}, {fromStoragePath}, {toStoragePath})",
                        batch => batch.DeleteStorageTrieNodeRange(nodeOwner, fromStoragePath, toStoragePath));
            }
        }

        private void Read()
        {
            ReaderFlags flags = (ReaderFlags)random.Next(3);
            (IPersistence.IPersistenceReader? csharpReader, IPersistence.IPersistenceReader? rustReader) = Same(persistence => persistence.CreateReader(flags), $"CreateReader({flags})");
            using (csharpReader)
            using (rustReader)
            {
                Same(csharpReader!, rustReader!, static reader => reader.CurrentState, "CurrentState");
                Same(csharpReader!, rustReader!, static reader => reader.IsPreimageMode, "IsPreimageMode");
                for (int i = 0; i < ReadsPerStep; i++)
                {
                    (string name, Func<IPersistence.IPersistenceReader, string> read) = RandomRead();
                    Same(csharpReader!, rustReader!, read, $"read {i} {name}");
                    // Again, so a carry-forward cache answers it from what the first read cached.
                    Same(csharpReader!, rustReader!, read, $"read {i} {name} again");
                }
            }
        }

        private (string, Func<IPersistence.IPersistenceReader, string>) RandomRead()
        {
            Address address = RandomAddress();
            UInt256 slot = RandomSlot();
            TreePath path = RandomPath();
            ReadFlags readFlags = random.Next(3) switch { 0 => ReadFlags.None, 1 => ReadFlags.HintCacheMiss, _ => ReadFlags.HintReadAhead };
            ValueHash256 accountKey = RandomAccountKey();
            switch (random.Next(8))
            {
                case 0:
                    return ($"GetAccount({address})", reader => Render(reader.GetAccount(address)));
                case 1:
                    return ($"TryGetSlot({address}, {slot})", reader =>
                    {
                        UInt256 value = 12345;
                        bool found = reader.TryGetSlot(address, slot, ref value);
                        return $"{found} {value}";
                    }
                    );
                case 2:
                    return ($"TryLoadStateRlp({path}, {readFlags})", reader => Render(reader.TryLoadStateRlp(path, readFlags)));
                case 3:
                    Hash256 owner = new(RandomOwner());
                    return ($"TryLoadStorageRlp({owner}, {path}, {readFlags})", reader => Render(reader.TryLoadStorageRlp(owner, path, readFlags)));
                case 4:
                    return ($"GetAccountRaw({accountKey})", reader => Render(reader.GetAccountRaw(accountKey)));
                case 5:
                    ValueHash256 slotKey = RandomSlotKey();
                    return ($"TryGetStorageRaw({accountKey}, {slotKey})", reader =>
                    {
                        UInt256 value = 12345;
                        bool found = reader.TryGetStorageRaw(accountKey, slotKey, ref value);
                        return $"{found} {value}";
                    }
                    );
                case 6:
                    (ValueHash256 start, ValueHash256 end) = RandomRange(_accountKeys);
                    return ($"CreateAccountIterator({start}, {end})", reader => Render(reader.CreateAccountIterator(start, end)));
                default:
                    (ValueHash256 startSlot, ValueHash256 endSlot) = RandomRange(_slotKeys);
                    return ($"CreateStorageIterator({accountKey}, {startSlot}, {endSlot})", reader => Render(reader.CreateStorageIterator(accountKey, startSlot, endSlot)));
            }
        }

        private (T? CSharp, T? Rust) Same<T>(Func<IPersistence, T> run, string? what = null)
        {
            T? csharpResult = default;
            T? rustResult = default;
            Exception? csharpError = Catch(() => csharpResult = run(csharp));
            Exception? rustError = Catch(() => rustResult = run(rust));
            AssertSameFailure(csharpError, rustError, what ?? "");
            return (csharpResult, rustResult);
        }

        private void Same<T>(IPersistence.IPersistenceReader csharpReader, IPersistence.IPersistenceReader rustReader, Func<IPersistence.IPersistenceReader, T> read, string what)
        {
            T? csharpResult = default;
            T? rustResult = default;
            Exception? csharpError = Catch(() => csharpResult = read(csharpReader));
            Exception? rustError = Catch(() => rustResult = read(rustReader));
            AssertSameFailure(csharpError, rustError, what);
            Assert.That(rustResult, Is.EqualTo(csharpResult), $"{_step}: {what}");
        }

        private void AssertSameFailure(Exception? csharpError, Exception? rustError, string what)
        {
            Assert.That(rustError?.GetType(), Is.EqualTo(csharpError?.GetType()), $"{_step}: {what} failed differently: C# {csharpError}, Rust {rustError}");
            Assert.That(rustError?.Message, Is.EqualTo(csharpError?.Message), $"{_step}: {what}");
        }

        private void AssertSameColumns()
        {
            foreach (FlatDbColumns column in Enum.GetValues<FlatDbColumns>())
            {
                Assert.That(FlatTestDbs.Dump(rustDb, column), Is.EqualTo(FlatTestDbs.Dump(csharpDb, column)), $"{column} after {_step}");
            }
        }

        private static Exception? Catch(Action action)
        {
            try
            {
                action();
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        private static string Render(Account? account) =>
            account is null ? "null" : $"{account.Nonce} {account.Balance} {account.StorageRoot} {account.CodeHash}";

        private static string Render(byte[]? bytes) => bytes?.ToHexString() ?? "null";

        private static string Render(IPersistence.IFlatIterator iterator)
        {
            using (iterator)
            {
                List<string> entries = [];
                while (iterator.MoveNext()) entries.Add($"{iterator.CurrentKey}={iterator.CurrentValue.ToHexString()}");
                return string.Join(",", entries);
            }
        }

        /// <summary>The flat account key: the address hash, or in the preimage layouts the zero-padded address.</summary>
        private ValueHash256 AccountKey(Address address)
        {
            if (!_preimage) return address.ToAccountPath;
            ValueHash256 key = ValueKeccak.Zero;
            address.Bytes.CopyTo(key.BytesAsSpan);
            return key;
        }

        /// <summary>The flat slot key: the slot hash, or in the preimage layouts the big-endian slot.</summary>
        private ValueHash256 SlotKey(UInt256 slot)
        {
            ValueHash256 key = ValueKeccak.Zero;
            slot.ToBigEndian(key.BytesAsSpan);
            return _preimage ? key : ValueKeccak.Compute(key.Bytes);
        }

        /// <summary>Addresses, two of them sharing the 4-byte storage key prefix of the preimage layouts.</summary>
        private static Address[] CreateAddresses(Random random) =>
        [
            new("0xaaaaaaaa00000000000000000000000000000001"),
            new("0xaaaaaaaa00000000000000000000000000000002"),
            new("0x00000000000000000000000000000000000000bb"),
            .. Enumerable.Range(0, 5).Select(_ => new Address(RandomBytes(random, Address.Size))),
        ];

        /// <summary>Paths of every length, many of them prefixes of each other so range deletes cut through subtrees.</summary>
        private static TreePath[] CreatePaths(Random random)
        {
            string nibbles = string.Concat(Enumerable.Range(0, 64).Select(_ => "0123456789abcdef"[random.Next(16)]));
            string other = string.Concat(Enumerable.Range(0, 64).Select(_ => "0123456789abcdef"[random.Next(16)]));
            return
            [
                .. Enumerable.Range(0, 65).Select(length => TreePath.FromHexString(nibbles[..length])),
                .. Enumerable.Range(0, 65).Where(length => length % 3 == 0).Select(length => TreePath.FromHexString(other[..length])),
            ];
        }

        private Address RandomAddress() => _addresses[random.Next(_addresses.Length)];

        private UInt256 RandomSlot() => _slots[random.Next(_slots.Length)];

        private TreePath RandomPath() => _paths[random.Next(_paths.Length)];

        private ValueHash256 RandomAccountKey() => _accountKeys[random.Next(_accountKeys.Length)];

        private ValueHash256 RandomSlotKey() => _slotKeys[random.Next(_slotKeys.Length)];

        /// <summary>A storage trie owner: an address hash, or one sharing the 4-byte key prefix of the first address hash.</summary>
        private ValueHash256 RandomOwner()
        {
            ValueHash256 owner = _addresses[random.Next(_addresses.Length)].ToAccountPath;
            if (random.Next(4) != 0) return owner;

            byte[] twin = _addresses[0].ToAccountPath.Bytes.ToArray();
            for (int i = BasePersistence.StoragePrefixPortion; i < twin.Length; i++) twin[i] ^= (byte)random.Next(1, 256);
            return new ValueHash256(twin);
        }

        private (ValueHash256, ValueHash256) RandomRange(ValueHash256[] keys)
        {
            ValueHash256 first = keys[random.Next(keys.Length)];
            ValueHash256 second = random.Next(4) == 0 ? first : keys[random.Next(keys.Length)];
            return first.CompareTo(second) <= 0 ? (first, second) : (second, first);
        }

        /// <summary>A node range from subtree bounds: whole subtrees, exact bounds and partial overlaps.</summary>
        private (ValueHash256, ValueHash256) RandomNodeRange()
        {
            TreePath first = RandomPath();
            TreePath second = RandomPath();
            ValueHash256 from = first.ToLowerBoundPath();
            ValueHash256 to = random.Next(3) == 0 ? first.ToUpperBoundPath() : second.ToUpperBoundPath();
            return from.CompareTo(to) <= 0 ? (from, to) : (to, from);
        }

        private Account RandomAccount() => new(
            random.Next(3) switch { 0 => 0, 1 => 1, _ => (ulong)random.NextInt64() },
            random.Next(3) switch { 0 => UInt256.Zero, 1 => UInt256.MaxValue, _ => new UInt256(RandomBytes(random, 1 + random.Next(32)), isBigEndian: true) },
            random.Next(2) == 0 ? Keccak.EmptyTreeHash : new Hash256(RandomHash()),
            random.Next(2) == 0 ? Keccak.OfAnEmptyString : new Hash256(RandomHash()));

        private UInt256 RandomValue() => random.Next(5) switch
        {
            0 => UInt256.Zero,
            1 => 1,
            2 => UInt256.MaxValue,
            3 => new UInt256(Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000100"), isBigEndian: true),
            _ => new UInt256(RandomBytes(random, 32), isBigEndian: true),
        };

        /// <summary>Node RLP shorter than a hash, so inlined by its parent, or at least as long.</summary>
        private byte[] RandomRlp() => RandomBytes(random, random.Next(2) == 0 ? 1 + random.Next(31) : 32 + random.Next(600));

        /// <summary>A trie-leaf slot value: RLP of the stripped value, sometimes with trailing bytes or malformed.</summary>
        private byte[] RandomEncodedSlot() => random.Next(6) switch
        {
            0 => Bytes.FromHexString("0x8105"),
            1 => [.. Rlp.Encode(RandomBytes(random, 1 + random.Next(32))).Bytes, 0xde, 0xad],
            2 => Bytes.FromHexString("0x80"),
            _ => Rlp.Encode(RandomStripped()).Bytes,
        };

        /// <summary>A slot value as the trie stores it: big-endian without leading zeros.</summary>
        private byte[] RandomStripped()
        {
            byte[] value = RandomBytes(random, 1 + random.Next(32));
            value[0] |= 1;
            return value;
        }

        private ValueHash256 RandomHash() => new(RandomBytes(random, ValueHash256.MemorySize));

        private static byte[] RandomBytes(Random random, int length)
        {
            byte[] bytes = new byte[length];
            random.NextBytes(bytes);
            return bytes;
        }
    }
}
