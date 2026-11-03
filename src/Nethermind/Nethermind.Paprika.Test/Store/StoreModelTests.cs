// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using Paprika.Crypto;
using Paprika.Data;
using Paprika.Store;

namespace Paprika.Tests.Store;

public class StoreModelTests
{
    private const long Mb = 1024 * 1024;
    private const int Rounds = 16;
    private const int OperationsPerRound = 2_000;
    private const int HeldReaders = 3;

    /// <summary>
    /// The rounds that clear the store halfway through their batch. The pages of the first clear are reused from round 9 on,
    /// so the second one comes while readers hold some of them; the last round reuses the pages of the second.
    /// </summary>
    private static readonly int[] ClearRounds = [5, 11];

    /// <summary>The round after which the store is closed and reopened from its file, before the first clear's pages are reused.</summary>
    private const int ReopenRound = 8;

    [Test]
    public async Task Store_matches_a_dictionary_model([Values(1, 2, 3, 4)] int seed)
    {
        ModelRun run = new(new Random(seed));
        int largestContract = 0;
        Queue<(IReadOnlyBatch Reader, Model Snapshot, int Round)> held = new();

        using TempDirectory dir = new();
        PagedDb db = Open(dir);
        try
        {
            for (int round = 0; round < Rounds; round++)
            {
                using (IBatch batch = db.BeginNextBatch())
                {
                    for (int i = 0; i < OperationsPerRound; i++)
                    {
                        if (ClearRounds.Contains(round) && i == OperationsPerRound / 2)
                        {
                            run.Clear(batch);
                        }

                        run.ApplyRandomOperation(batch);
                    }

                    run.AssertMatches(batch, run.Current, $"in batch {round}");
                    await batch.Commit(CommitOptions.FlushDataAndRoot);
                }

                largestContract = Math.Max(largestContract, run.LargestContract);

                IReadOnlyBatch reader = db.BeginReadOnlyBatch();
                held.Enqueue((reader, run.Current.Clone(), round));
                reader.VerifyNoPagesMissing();
                Assert.That(TreeShapeVisitor.VisitRoot(db).PagesClaimedTwice, Is.Zero, $"after batch {round}");

                foreach ((IReadOnlyBatch heldReader, Model snapshot, int heldRound) in held)
                {
                    run.AssertMatches(heldReader, snapshot, $"for the reader of batch {heldRound} after batch {round}");
                }

                if (held.Count > HeldReaders)
                {
                    held.Dequeue().Reader.Dispose();
                }

                if (round == ReopenRound)
                {
                    // The store unmaps its file only once no reader is left.
                    DisposeReaders(held);
                    db.Dispose();
                    db = Open(dir);

                    using IReadOnlyBatch reopened = db.BeginReadOnlyBatch();
                    reopened.VerifyNoPagesMissing();
                    run.AssertMatches(reopened, run.Current, $"after reopening at batch {round}");
                }
            }

            Assert.That(largestContract, Is.GreaterThan(200), "Some contract should be large enough to have bottom page children");
        }
        finally
        {
            DisposeReaders(held);
            db.Dispose();
        }
    }

    private static PagedDb Open(TempDirectory dir) => PagedDb.MemoryMappedDb(64 * Mb, 2, dir.Path, accessObserver: null);

    private static void DisposeReaders(Queue<(IReadOnlyBatch Reader, Model Snapshot, int Round)> held)
    {
        while (held.TryDequeue(out (IReadOnlyBatch Reader, Model Snapshot, int Round) reader))
        {
            reader.Reader.Dispose();
        }
    }

    private sealed class Model
    {
        public Dictionary<Keccak, byte[]> Accounts { get; private init; } = [];

        public Dictionary<(Keccak Contract, Keccak Slot), byte[]> Slots { get; private init; } = [];

        public Model Clone() => new() { Accounts = new(Accounts), Slots = new(Slots) };
    }

    /// <summary>
    /// Applies random operations to a batch and to <see cref="Current"/>, and remembers every key it touched, so that
    /// deleted and never written keys are checked to be absent.
    /// </summary>
    private sealed class ModelRun(Random random)
    {
        private const int AccountValueSize = 80;
        private const int SlotValueSize = 33;

        /// <summary>Accounts start with one of these bytes, so the data pages under the state root page have children.</summary>
        private static readonly byte[] AccountClusters = [0x12, 0x34, 0xAB, 0xCD];

        /// <summary>Nibble counts of state prefix deletes. One or two nibbles wipe a whole cluster, so they are rare.</summary>
        private static readonly int[] StatePrefixLengths = [1, 2, 3, 3, 3, 4, 4, 4, 5, 5];

        /// <summary>The first two contracts get most of the slots; together with the rest they share two storage subtrees.</summary>
        private readonly Keccak[] _contracts = random.NextKeccaks(22);

        private readonly List<Keccak> _accounts = [];
        private readonly HashSet<Keccak> _accountSet = [];
        private readonly List<(Keccak Contract, Keccak Slot)> _slots = [];
        private readonly HashSet<(Keccak Contract, Keccak Slot)> _slotSet = [];

        public Model Current { get; } = new();

        public int LargestContract => Current.Slots.Count == 0 ? 0 : Current.Slots.Keys.CountBy(key => key.Contract).Max(count => count.Value);

        public void ApplyRandomOperation(IBatch batch)
        {
            switch (random.Next(1_000))
            {
                case < 280:
                    SetAccount(batch, _accounts.Count > 0 && random.Next(10) < 3 ? PickAccount() : NewAccount(), RandomValue(AccountValueSize));
                    break;
                case < 760:
                    (Keccak Contract, Keccak Slot) slot = _slots.Count > 0 && random.Next(10) < 3 ? PickSlot() : (PickContract(), random.NextKeccak());
                    SetSlot(batch, slot, RandomValue(SlotValueSize));
                    break;
                case < 840 when _accounts.Count > 0:
                    SetAccount(batch, PickAccount(), null);
                    break;
                case < 960 when _slots.Count > 0:
                    SetSlot(batch, PickSlot(), null);
                    break;
                case < 975:
                    SetAccount(batch, NewAccount(), null);
                    break;
                case < 990:
                    SetSlot(batch, (random.Next(2) == 0 ? PickContract() : random.NextKeccak(), random.NextKeccak()), null);
                    break;
                case < 993:
                    Keccak contract = _contracts[random.Next(_contracts.Length)];
                    batch.DeleteByPrefix(Key.StorageCell(NibblePath.FromKey(contract), NibblePath.Empty));
                    RemoveSlots(key => key.Contract == contract);
                    break;
                case < 997 when _slots.Count > 0:
                    (Keccak prefixContract, Keccak prefixSource) = PickSlot();
                    int slotNibbles = random.Next(1, 4);
                    batch.DeleteByPrefix(Key.StorageCell(NibblePath.FromKey(prefixContract), NibblePath.FromKey(prefixSource).SliceTo(slotNibbles)));
                    RemoveSlots(key => key.Contract == prefixContract && StartsWith(key.Slot, prefixSource, slotNibbles));
                    break;
                case >= 997 when _accounts.Count > 0:
                    Keccak accountSource = PickAccount();
                    int accountNibbles = StatePrefixLengths[random.Next(StatePrefixLengths.Length)];
                    batch.DeleteByPrefix(Key.Account(NibblePath.FromKey(accountSource).SliceTo(accountNibbles)));
                    foreach (Keccak account in Current.Accounts.Keys.Where(key => StartsWith(key, accountSource, accountNibbles)).ToArray())
                    {
                        Current.Accounts.Remove(account);
                    }

                    break;
            }
        }

        public void Clear(IBatch batch)
        {
            batch.Clear();
            Current.Accounts.Clear();
            Current.Slots.Clear();
        }

        public void AssertMatches(IReadOnlyBatch read, Model expected, string when)
        {
            List<string> mismatches = [];

            foreach (Keccak account in _accounts)
            {
                bool found = read.TryGet(Key.Account(account), out ReadOnlySpan<byte> value);
                Compare(found, value, expected.Accounts.GetValueOrDefault(account), $"account {account}", mismatches);
            }

            foreach ((Keccak contract, Keccak slot) in _slots)
            {
                bool found = read.TryGet(Key.StorageCell(NibblePath.FromKey(contract), slot), out ReadOnlySpan<byte> value);
                Compare(found, value, expected.Slots.GetValueOrDefault((contract, slot)), $"slot {slot} of {contract}", mismatches);
            }

            Assert.That(mismatches.Take(5), Is.Empty, $"{mismatches.Count} keys differ {when}");
        }

        /// <summary>An empty value is a tombstone, so it reads as absent.</summary>
        private static void Compare(bool found, ReadOnlySpan<byte> actual, byte[]? expected, string key, List<string> mismatches)
        {
            bool present = found && !actual.IsEmpty;
            if (present != expected is not null || present && !actual.SequenceEqual(expected))
            {
                mismatches.Add(key);
            }
        }

        private static bool StartsWith(in Keccak key, in Keccak prefixSource, int nibbles) =>
            NibblePath.FromKey(key).StartsWith(NibblePath.FromKey(prefixSource).SliceTo(nibbles));

        private void SetAccount(IBatch batch, in Keccak account, byte[]? value)
        {
            batch.SetRaw(Key.Account(account), value);
            if (_accountSet.Add(account))
            {
                _accounts.Add(account);
            }

            if (value is null)
            {
                Current.Accounts.Remove(account);
            }
            else
            {
                Current.Accounts[account] = value;
            }
        }

        private void SetSlot(IBatch batch, (Keccak Contract, Keccak Slot) slot, byte[]? value)
        {
            batch.SetRaw(Key.StorageCell(NibblePath.FromKey(slot.Contract), slot.Slot), value);
            if (_slotSet.Add(slot))
            {
                _slots.Add(slot);
            }

            if (value is null)
            {
                Current.Slots.Remove(slot);
            }
            else
            {
                Current.Slots[slot] = value;
            }
        }

        private void RemoveSlots(Func<(Keccak Contract, Keccak Slot), bool> predicate)
        {
            foreach ((Keccak Contract, Keccak Slot) slot in Current.Slots.Keys.Where(predicate).ToArray())
            {
                Current.Slots.Remove(slot);
            }
        }

        private Keccak NewAccount()
        {
            Keccak account = random.NextKeccak();
            account.BytesAsSpan[0] = AccountClusters[random.Next(AccountClusters.Length)];
            return account;
        }

        private Keccak PickAccount() => _accounts[random.Next(_accounts.Count)];

        private (Keccak Contract, Keccak Slot) PickSlot() => _slots[random.Next(_slots.Count)];

        private Keccak PickContract() => random.Next(10) < 7 ? _contracts[random.Next(2)] : _contracts[2 + random.Next(_contracts.Length - 2)];

        private byte[] RandomValue(int size)
        {
            byte[] value = new byte[size];
            random.NextBytes(value);
            return value;
        }
    }
}
