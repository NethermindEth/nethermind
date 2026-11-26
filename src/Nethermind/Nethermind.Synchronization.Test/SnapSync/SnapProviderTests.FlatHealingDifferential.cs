// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.Test.FastSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

/// <summary>
/// Flat trie-node healing over six generated roots, with two rounds abandoned part way, compared with a clean heal of
/// the last root. Each seed builds the same kinds of change on a different trie: a storage emptied and filled again,
/// an account deleted and recreated, a storage leaf found saved under a changed parent and then replaced, a free account
/// changed deep in its storage, and one of four abandoned-round shapes chosen by the seed, which the last root undoes.
/// </summary>
/// <remarks>
/// A case is replayed by its seed and layout. <c>NM_FLAT_HEAL_SEED</c> and <c>NM_FLAT_HEAL_COUNT</c> add consecutive seeds
/// to the fixed list, for a longer local run.
/// </remarks>
public partial class SnapProviderTests
{
    private const int GeneratedRounds = 6;
    private const int GeneratedRequestsPerRound = 256;
    private const int GeneratedRecentRequests = 12;

    private static readonly int[] GeneratedSeeds = [6700, 6701, 6702, 6703, 6704, 6705, 6706, 6707];

    /// <summary>A seed whose emptied storage, refilled at the next root, keeps its storage trie nodes without #16408.</summary>
    private const int StoreKeepingStorageNodesSeed = 6700;

    private static IEnumerable<TestCaseData> GeneratedHealingCases()
    {
        List<int> seeds = [.. GeneratedSeeds];
        if (int.TryParse(Environment.GetEnvironmentVariable("NM_FLAT_HEAL_SEED"), out int first))
        {
            int count = int.TryParse(Environment.GetEnvironmentVariable("NM_FLAT_HEAL_COUNT"), out int requested) && requested > 0 ? requested : 1;
            for (int seed = first; seed < first + count; seed++)
            {
                if (!seeds.Contains(seed)) seeds.Add(seed);
            }
        }

        foreach (int seed in seeds)
        {
            foreach (FlatLayout layout in (FlatLayout[])[FlatLayout.Flat, FlatLayout.FlatInTrie])
            {
                yield return new TestCaseData(seed, layout).SetName($"{{m}}(seed {seed}, {layout})");
            }
        }
    }

    [TestCaseSource(nameof(GeneratedHealingCases))]
    public async Task Flat_healing_over_generated_roots_with_abandoned_rounds_matches_a_clean_heal_of_the_last_root(int seed, FlatLayout layout)
    {
        await TestContext.Out.WriteLineAsync($"seed {seed}, layout {layout}");
        GeneratedHealing result = await HealGeneratedRoots(seed, layout);
        Assert.That(result.Failures, Is.Empty, result.Report);
    }

    /// <summary>
    /// Healing over the generated roots through a store that deletes only the slots of an emptied storage, as before
    /// #16408, ends with storage trie nodes and slots a clean heal does not have, so the generated roots still exercise
    /// the shape that change fixed.
    /// </summary>
    [Test]
    public async Task Flat_healing_over_generated_roots_tells_a_store_keeping_the_nodes_of_an_emptied_storage_from_a_clean_heal()
    {
        GeneratedHealing result = await HealGeneratedRoots(StoreKeepingStorageNodesSeed, FlatLayout.Flat,
            (persistence, store) => new SlotsOnlyStorageEmptying(store, persistence));
        Assert.That(result.ColumnDifferences, Is.Not.Null, result.Report);
    }

    private static IPersistence CreateFlatPersistence(IColumnsDb<FlatDbColumns> db, FlatLayout layout) => layout switch
    {
        FlatLayout.Flat => new RocksDbPersistence(db, LimboLogs.Instance, NullTrieNodeLog.Instance),
        FlatLayout.FlatInTrie => new FlatInTriePersistence(db, LimboLogs.Instance, NullTrieNodeLog.Instance),
        _ => throw new NotSupportedException($"Layout {layout}"),
    };

    /// <summary>
    /// Heals the roots generated from <paramref name="seed"/> one after another with the pivot moving, and the last root
    /// alone in another database, and compares the two.
    /// </summary>
    /// <remarks>A failure of the generator or the schedule fails the test; a difference between the heals is returned.</remarks>
    private static async Task<GeneratedHealing> HealGeneratedRoots(int seed, FlatLayout layout, Func<IPersistence, ITreeSyncStore, ITreeSyncStore>? decorateStore = null)
    {
        GeneratedRoots roots = GeneratedRoots.Generate(seed);
        BlockHeader[] headers = roots.Headers;
        TestSyncConfig syncConfig = new() { SnapSync = true };

        using SnapshotableMemColumnsDb<FlatDbColumns> cleanDb = new();
        IPersistence cleanPersistence = CreateFlatPersistence(cleanDb, layout);
        await HealWithTrieNodes(cleanPersistence, syncConfig, new HealingPivot(headers[^1], headers[^1]), roots.Store, maxRequestsPerRound: GeneratedRequestsPerRound);

        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        IPersistence persistence = CreateFlatPersistence(db, layout);
        GeneratedSchedule schedule = new(seed, roots, persistence);
        HealingPivot pivot = new(headers[0], headers) { CanFinalizeAt = header => header == headers[^1], OnUpdate = schedule.OnRound };
        schedule.Pivot = pivot;
        GeneratedHealing result = new(seed, layout, roots, schedule);
        try
        {
            await HealWithTrieNodes(persistence, syncConfig, pivot, roots.Store, schedule.AfterRequest, schedule.AnsweredCount,
                decorateStore is null ? null : store => decorateStore(persistence, store), GeneratedRequestsPerRound);
        }
        catch (Exception exception)
        {
            Assert.Fail($"healing over the generated roots failed: {exception.Message}{Environment.NewLine}{result.Report}{Environment.NewLine}{exception}");
        }

        schedule.Finish();
        if (schedule.ScheduleErrors.Count > 0) Assert.Fail($"the schedule did not run as planned: {string.Join("; ", schedule.ScheduleErrors)}{Environment.NewLine}{result.Report}");

        // A clean heal that does not verify points at the generator or a documented limit, not at moving pivots.
        string? cleanVerification = Verify(cleanPersistence, roots);
        if (cleanVerification is not null) Assert.Fail($"the clean heal of the last root does not verify: {cleanVerification}{Environment.NewLine}{result.Report}");
        string? cleanReads = ReadModel(cleanPersistence, roots);
        if (cleanReads is not null) Assert.Fail($"the clean heal of the last root does not hold the generated state: {cleanReads}{Environment.NewLine}{result.Report}");

        result.ColumnDifferences = DescribeColumnDifferences(ReadColumns(cleanDb), ReadColumns(db));
        if (result.ColumnDifferences is not null) result.Failures.Add($"columns differ from a clean heal of the last root:{Environment.NewLine}{result.ColumnDifferences}");
        if (!GeneratedSchedule.HoldsRoot(persistence, headers[^1])) result.Failures.Add("the state root stored does not hash to the last root");
        if (Verify(persistence, roots) is { } verification) result.Failures.Add($"flat against trie: {verification}");
        if (ReadModel(persistence, roots) is { } reads) result.Failures.Add($"reads of the generated state: {reads}");
        return result;
    }

    /// <summary>Verifies flat against the trie at the last root, expecting the accounts and slots generated there.</summary>
    private static string? Verify(IPersistence persistence, GeneratedRoots roots)
    {
        GeneratedState?[] last = roots.States[^1];
        FlatTrieVerifier.VerificationStats expected = new(last.Count(static state => state is not null), last.Sum(static state => state?.Slots.Count ?? 0), 0, 0, 0, 0);
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        TestErrorLogManager errors = new();
        FlatTrieVerifier verifier = new(errors);
        verifier.Verify(reader, new FlatLocalDbContext.ReadOnlyTrieStore(reader), roots.Headers[^1].StateRoot!, default);
        if (verifier.Stats == expected && errors.Errors.Count == 0) return null;
        return $"expected {expected}, got {verifier.Stats}; errors: {string.Join("; ", errors.Errors.Select(static error => error.Text))}";
    }

    /// <summary>Reads every generated account and slot at the last root, expecting the generated values, or nothing.</summary>
    private static string? ReadModel(IPersistence persistence, GeneratedRoots roots)
    {
        List<string> wrong = [];
        using IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);
        for (int i = 0; i < roots.Accounts.Length; i++)
        {
            GeneratedAccount account = roots.Accounts[i];
            GeneratedState? state = roots.States[^1][i];
            byte[]? raw = reader.GetAccountRaw(account.Path);
            Account? stored = raw is null ? null : AccountDecoder.Slim.Decode(raw);
            if (state is null)
            {
                if (stored is not null) wrong.Add($"{account.Name} deleted, but read");
            }
            else if (stored is null) wrong.Add($"{account.Name} not read");
            else if (stored.Balance != state.Balance || stored.Nonce != state.Nonce || stored.StorageRoot != roots.StorageRoots[^1][i] || stored.CodeHash != state.CodeHash)
                wrong.Add($"{account.Name} read as {stored}");

            foreach (ValueHash256 slot in account.Slots)
            {
                UInt256 value = default;
                bool found = reader.TryGetStorageRaw(account.Path, slot, ref value);
                byte[]? expected = null;
                state?.Slots.TryGetValue(slot, out expected);
                if (expected is null)
                {
                    if (found) wrong.Add($"{account.Name} slot {Short(slot)} absent, but read as {value}");
                }
                else if (!found || value != new UInt256(expected, isBigEndian: true)) wrong.Add($"{account.Name} slot {Short(slot)} {(found ? $"read as {value}" : "not read")}");
            }
        }

        return wrong.Count == 0 ? null : string.Join("; ", wrong);
    }

    private static string Short(in ValueHash256 path) => path.Bytes[..4].ToHexString();

    /// <summary>The outcome of <see cref="HealGeneratedRoots"/>: the differences found, and how to read them.</summary>
    private sealed class GeneratedHealing(int seed, FlatLayout layout, GeneratedRoots roots, GeneratedSchedule schedule)
    {
        public List<string> Failures { get; } = [];

        public string? ColumnDifferences { get; set; }

        public string Report
        {
            get
            {
                StringBuilder text = new();
                text.AppendLine($"seed {seed}, layout {layout} (replay: NM_FLAT_HEAL_SEED={seed}, or this case by name)");
                foreach (string failure in Failures) text.AppendLine(failure);
                text.Append(roots.Describe());
                text.Append(schedule.Describe());
                return text.ToString();
            }
        }
    }

    /// <summary>An account the generator can change: its path, and the slots its storage can hold.</summary>
    private sealed record GeneratedAccount(string Name, ValueHash256 Path, ValueHash256[] Slots, bool Free, bool CanBeDeleted);

    /// <summary>An account at one root.</summary>
    private sealed class GeneratedState
    {
        public static readonly Comparer<ValueHash256> PathOrder = Comparer<ValueHash256>.Create(static (left, right) => left.Bytes.SequenceCompareTo(right.Bytes));

        public UInt256 Balance { get; set; }

        public ulong Nonce { get; set; }

        public byte[]? Code { get; set; }

        public Hash256 CodeHash => Code is null ? Keccak.OfAnEmptyString : Keccak.Compute(Code);

        public SortedDictionary<ValueHash256, byte[]> Slots { get; private init; } = [with(PathOrder)];

        public GeneratedState Clone() => new() { Balance = Balance, Nonce = Nonce, Code = Code, Slots = new SortedDictionary<ValueHash256, byte[]>(Slots, PathOrder) };

        public bool SameAs(GeneratedState? other) =>
            other is not null && Balance == other.Balance && Nonce == other.Nonce && Code == other.Code
            && Slots.Count == other.Slots.Count && Slots.All(slot => other.Slots.TryGetValue(slot.Key, out byte[]? value) && value.AsSpan().SequenceEqual(slot.Value));
    }

    /// <summary>
    /// Six roots built from a seed. Every root changes what the schedule below needs, and one to three free accounts at
    /// random; a counter account changes at every root, so no two roots are the same.
    /// </summary>
    /// <remarks>
    /// <list type="table">
    /// <item><term>0</term><description>The refill owner has two slots under one storage branch and one outside it.</description></item>
    /// <item><term>1</term><description>The refill storage is emptied, its account kept; another account with storage is deleted;
    /// of two slots of the cache owner, one stays and the other changes, so healing finds the leaf of the first saved under
    /// a changed parent; a storage branch becomes an extension, with slots gone on both sides of it.</description></item>
    /// <item><term>2</term><description>The refill storage is back: all of it for an even seed, the subtree under its branch for an odd
    /// one. The deleted account comes back with another balance and some other slots. The leaf found saved changes.</description></item>
    /// <item><term>3</term><description>A slot of an account changes below a storage branch, and the round moves on after its first,
    /// second or third request, chosen by the seed, with one of its requests answered in part and out of order.</description></item>
    /// <item><term>4</term><description>One of four shapes, by the seed modulo four, and the round moves on once healing handled its
    /// node, with what that node depends on still to come: 0, a leaf at storage path <c>ac</c> changed with a leaf under
    /// <c>ad</c>; 1, the storage node at <c>a</c> becoming an extension to <c>ace</c>, skipping the branch at <c>ac</c>;
    /// 2, the owner's state leaf moving up as its neighbor goes, with its storage changed; 3, the same, with its storage
    /// emptied and a code healing does not have.</description></item>
    /// <item><term>5</term><description>The owner and neighbor of root 3 are back, and the leaf found saved has its value of root 0.
    /// Healing completes here.</description></item>
    /// </list>
    /// The nibbles above are placeholders the seed picks.
    /// </remarks>
    private sealed class GeneratedRoots
    {
        // The accounts by role, in the order of Accounts.
        private const int Refill = 0;
        private const int Cache = 1;
        private const int Owner = 2;
        private const int Neighbor = 3;
        private const int Witness = 4;
        private const int Recreated = 5;
        private const int Counter = 6;
        private const int Deep = 7;
        private const int Collapse = 18;

        public required int Seed { get; init; }

        public required int Shape { get; init; }

        public required int NeighborDepth { get; init; }

        public required TestRawTrieStore Store { get; init; }

        public required GeneratedAccount[] Accounts { get; init; }

        public required GeneratedState?[][] States { get; init; }

        public required Hash256[][] StorageRoots { get; init; }

        public required BlockHeader[] Headers { get; init; }

        public required List<string>[] Changes { get; init; }

        /// <summary>The node whose handling ends the round at root 4: its owner for a storage node, its path and its hash.</summary>
        public (Hash256? Owner, TreePath Path, Hash256 Hash) Root4Target { get; private set; }

        public string Describe()
        {
            StringBuilder text = new();
            text.AppendLine($"shape {Shape} at root 4, neighbor depth {NeighborDepth}");
            for (int i = 0; i < Accounts.Length; i++) text.AppendLine($"  account {Accounts[i].Name} {Accounts[i].Path}");
            for (int round = 0; round < Headers.Length; round++)
                text.AppendLine($"root {round} {Headers[round].StateRoot}: {string.Join("; ", Changes[round])}");
            text.AppendLine($"root 4 target: {(Root4Target.Owner is null ? "state" : $"storage of {Root4Target.Owner}")} node at '{Root4Target.Path}' {Root4Target.Hash}");
            return text.ToString();
        }

        public static GeneratedRoots Generate(int seed)
        {
            Random random = new(seed);
            int shape = seed % 4;
            int neighborDepth = random.Next(2, 4);
            int[] top = Permutation(random);
            int[] nibble = Permutation(random);
            int a = nibble[0], b = nibble[1], c = nibble[2], d = nibble[3], e = nibble[4], f = nibble[5], g = nibble[6], h = nibble[7];

            List<GeneratedAccount> accounts = [];
            ValueHash256 owner = RandomPath(random, top[2]);
            ValueHash256 pair9 = RandomPath(random, top[7]);
            ValueHash256 pair = RandomPath(random, top[8]);
            ValueHash256 deepWitnessSlot = RandomPath(random, c);
            ValueHash256 sharedSlot0 = RandomPath(random, a);
            ValueHash256 sharedSlot1 = RandomPath(random, b);
            accounts.Add(new("refill", RandomPath(random, top[0]), [RandomPath(random, a, c), RandomPath(random, a, d), RandomPath(random, b)], false, false));
            accounts.Add(new("cache", RandomPath(random, top[1]), [RandomPath(random, a), RandomPath(random, b)], false, false));
            accounts.Add(new("owner", owner, shape switch
            {
                0 => [RandomPath(random, a, c), RandomPath(random, a, d, e), RandomPath(random, a, d, f), RandomPath(random, b)],
                1 => [RandomPath(random, a, c, e, g), RandomPath(random, a, c, f), RandomPath(random, a, d), RandomPath(random, b), RandomPath(random, a, c, e, h)],
                _ => [RandomPath(random, a), RandomPath(random, b)],
            }, false, false));
            accounts.Add(new("neighbor", Diverge(random, owner, neighborDepth - 1), [], false, false));
            // A slot pair sharing 16 nibbles puts a storage node in the fallback column.
            accounts.Add(new("witness", RandomPath(random, top[3]), [RandomPath(random, a), RandomPath(random, b), deepWitnessSlot, Diverge(random, deepWitnessSlot, 16)], false, false));
            accounts.Add(new("recreated", RandomPath(random, top[4]), [RandomPath(random, a), RandomPath(random, b), RandomPath(random, c), RandomPath(random, d)], false, false));
            accounts.Add(new("counter", RandomPath(random, top[5]), [], false, false));
            accounts.Add(new("deep", RandomPath(random, top[6]), [RandomPath(random, e, f), RandomPath(random, e, g), RandomPath(random, h)], false, false));
            // Two accounts sharing 9 nibbles, so the first four bytes their storage is keyed by, with the same slots, and
            // two sharing 16, putting a state node in the fallback column.
            accounts.Add(new("pair9a", pair9, [sharedSlot0, sharedSlot1], true, false));
            accounts.Add(new("pair9b", Diverge(random, pair9, 9), [sharedSlot0, sharedSlot1], true, false));
            accounts.Add(new("pair16a", pair, [], true, false));
            accounts.Add(new("pair16b", Diverge(random, pair, 16), [], true, false));
            // Free accounts in three groups, so that creating and deleting them adds and collapses branches.
            ValueHash256 free0 = RandomPath(random, top[9]);
            ValueHash256 free2 = RandomPath(random, top[10]);
            ValueHash256 free4 = RandomPath(random, top[11]);
            accounts.Add(new("free0", free0, FreeSlots(random), true, true));
            accounts.Add(new("free1", Diverge(random, free0, 2), FreeSlots(random), true, true));
            accounts.Add(new("free2", free2, FreeSlots(random), true, true));
            accounts.Add(new("free3", Diverge(random, free2, 1), FreeSlots(random), true, true));
            accounts.Add(new("free4", free4, FreeSlots(random), true, true));
            accounts.Add(new("free5", Diverge(random, free4, 5), FreeSlots(random), true, true));
            // Its storage node at p becomes an extension to pr at root 1, which healing saves, deleting the slots on
            // both sides of the extension, below pr and above it.
            int[] sides = [nibble[10], nibble[11], nibble[12]];
            Array.Sort(sides);
            int p = nibble[8], below = sides[0], r = sides[1], above = sides[2];
            accounts.Add(new("collapse", RandomPath(random, top[12]),
                [RandomPath(random, nibble[9]), RandomPath(random, p, below), RandomPath(random, p, r, nibble[13]), RandomPath(random, p, r, nibble[14]), RandomPath(random, p, above)], false, false));
            GeneratedAccount[] all = [.. accounts];
            Assert.That(all[Collapse].Name, Is.EqualTo("collapse"));

            GeneratedState?[][] states = new GeneratedState?[GeneratedRounds][];
            List<string>[] changes = new List<string>[GeneratedRounds];
            for (int round = 0; round < GeneratedRounds; round++) changes[round] = [];

            // Root 0.
            GeneratedState?[] state = new GeneratedState?[all.Length];
            for (int i = 0; i < all.Length; i++)
            {
                GeneratedAccount account = all[i];
                bool alive = !account.CanBeDeleted || random.Next(10) < 6;
                if (!alive) continue;
                GeneratedState created = new() { Balance = (UInt256)(random.Next(1, 1000)), Nonce = (ulong)random.Next(3) };
                if (account.Free)
                {
                    int slots = account.Slots.Length == 0 ? 0 : random.Next(account.Slots.Length + 1);
                    foreach (int slot in Permutation(random, account.Slots.Length).Take(slots)) created.Slots[account.Slots[slot]] = Value(random, random.Next(2) == 0);
                }
                else
                {
                    for (int slot = 0; slot < account.Slots.Length; slot++) created.Slots[account.Slots[slot]] = Value(random, wide: i is Witness or Deep);
                }

                state[i] = created;
            }

            // Shape 1 adds its last slot at root 4 only; the recreated account has no slot d before.
            if (shape == 1) state[Owner]!.Slots.Remove(all[Owner].Slots[4]);
            state[Recreated]!.Slots.Remove(all[Recreated].Slots[3]);
            state[Counter]!.Balance = 1;
            states[0] = state;
            changes[0].Add("initial");

            for (int round = 1; round < GeneratedRounds; round++)
            {
                state = [.. states[round - 1].Select(static account => account?.Clone())];
                List<string> log = changes[round];
                state[Counter]!.Balance = (UInt256)(round + 1);
                switch (round)
                {
                    case 1:
                        state[Refill]!.Slots.Clear();
                        log.Add("refill storage emptied");
                        state[Recreated] = null;
                        log.Add("recreated deleted");
                        ChangeSlot(random, state[Cache]!, all[Cache].Slots[1], wide: false);
                        log.Add("cache: second slot changed, first kept");
                        state[Collapse]!.Slots.Remove(all[Collapse].Slots[1]);
                        state[Collapse]!.Slots.Remove(all[Collapse].Slots[4]);
                        log.Add("collapse: the slots below and above pr deleted, so p is an extension to pr");
                        break;
                    case 2:
                        state[Refill] = states[0][Refill]!.Clone();
                        if (seed % 2 == 1) ChangeSlot(random, state[Refill]!, all[Refill].Slots[2], wide: false);
                        log.Add(seed % 2 == 0 ? "refill storage of root 0 back" : "refill storage of root 0 back, the slot outside its branch changed");
                        GeneratedState recreated = states[0][Recreated]!.Clone();
                        recreated.Balance += 7;
                        recreated.Slots.Remove(all[Recreated].Slots[0]);
                        ChangeSlot(random, recreated, all[Recreated].Slots[1], wide: false);
                        recreated.Slots[all[Recreated].Slots[3]] = Value(random, wide: false);
                        state[Recreated] = recreated;
                        log.Add("recreated with another balance, slot a gone, slot b changed, slot d new");
                        ChangeSlot(random, state[Cache]!, all[Cache].Slots[0], wide: false);
                        log.Add("cache: first slot changed");
                        break;
                    case 3:
                        ChangeSlot(random, state[Deep]!, all[Deep].Slots[0], wide: true);
                        log.Add("deep: slot ef changed");
                        break;
                    case 4:
                        GeneratedState ownerState = state[Owner]!;
                        GeneratedAccount ownerAccount = all[Owner];
                        switch (shape)
                        {
                            case 0:
                                ChangeSlot(random, ownerState, ownerAccount.Slots[0], wide: false);
                                ChangeSlot(random, ownerState, ownerAccount.Slots[1], wide: false);
                                log.Add("owner: slots ac and ade changed");
                                break;
                            case 1:
                                ownerState.Slots.Remove(ownerAccount.Slots[1]);
                                ownerState.Slots.Remove(ownerAccount.Slots[2]);
                                ownerState.Slots[ownerAccount.Slots[4]] = Value(random, wide: false);
                                log.Add("owner: slots acf and ad gone, slot aceh new, so a is an extension to ace");
                                break;
                            case 2:
                                state[Neighbor] = null;
                                ChangeSlot(random, ownerState, ownerAccount.Slots[0], wide: false);
                                log.Add("neighbor deleted, so the owner leaf moves up; owner slot a changed");
                                break;
                            default:
                                state[Neighbor] = null;
                                ownerState.Slots.Clear();
                                ownerState.Code = [0x60, (byte)seed, 0x60, (byte)(seed >> 8), 0x00];
                                log.Add("neighbor deleted, so the owner leaf moves up; owner storage emptied, code new");
                                break;
                        }

                        break;
                    case 5:
                        state[Owner] = states[3][Owner]!.Clone();
                        state[Neighbor] = states[3][Neighbor]?.Clone();
                        if (shape <= 1) ChangeSlot(random, state[Owner]!, all[Owner].Slots[3], wide: false);
                        log.Add(shape <= 1 ? "owner storage of root 3 back, slot b changed" : "owner and neighbor of root 3 back");
                        state[Cache]!.Slots[all[Cache].Slots[0]] = states[0][Cache]!.Slots[all[Cache].Slots[0]];
                        ChangeSlot(random, state[Cache]!, all[Cache].Slots[1], wide: false);
                        log.Add("cache: first slot of root 0 back, second changed");
                        break;
                }

                int freeChanges = random.Next(1, 4);
                for (int change = 0; change < freeChanges; change++) log.Add(ChangeFreeAccount(random, all, states, state, round));
                states[round] = state;
            }

            TestRawTrieStore store = new(new TestMemDb());
            Hash256[][] storageRoots = new Hash256[GeneratedRounds][];
            BlockHeader[] headers = new BlockHeader[GeneratedRounds];
            for (int round = 0; round < GeneratedRounds; round++)
            {
                headers[round] = Commit(store, all, states[round], (ulong)(round + 1), out storageRoots[round]);
            }

            GeneratedRoots roots = new()
            {
                Seed = seed,
                Shape = shape,
                NeighborDepth = neighborDepth,
                Store = store,
                Accounts = all,
                States = states,
                StorageRoots = storageRoots,
                Headers = headers,
                Changes = changes,
            };
            (Hash256? targetOwner, TreePath targetPath) = shape switch
            {
                0 => (owner.ToCommitment(), TreePath.FromNibble([(byte)a, (byte)c])),
                1 => (owner.ToCommitment(), TreePath.FromNibble([(byte)a])),
                _ => ((Hash256?)null, TreePath.FromNibble([(byte)top[2]])),
            };
            Hash256 targetRoot = targetOwner is null ? headers[4].StateRoot! : storageRoots[4][Owner];
            (Hash256 Hash, NodeType Type)? target = NodeAt(store, targetOwner, targetRoot, targetPath);
            Assert.That(target, Is.Not.Null, $"seed {seed}: no node at '{targetPath}' at root 4 to end the round on");
            roots.Root4Target = (targetOwner, targetPath, target!.Value.Hash);
            roots.CheckShapes(a, c, d, e, p, top[2]);
            return roots;
        }

        /// <summary>Checks that the roots have the shapes the schedule relies on.</summary>
        private void CheckShapes(int a, int c, int d, int e, int p, int ownerNibble)
        {
            string context = $"seed {Seed}, shape {Shape}";
            Hash256? StorageNode(int round, int account, params int[] path) =>
                StorageRoots[round][account] == Keccak.EmptyTreeHash ? null : NodeAt(Store, Accounts[account].Path.ToCommitment(), StorageRoots[round][account], TreePath.FromNibble([.. path.Select(static n => (byte)n)]))?.Hash;
            NodeType? StorageNodeType(int round, int account, params int[] path) =>
                NodeAt(Store, Accounts[account].Path.ToCommitment(), StorageRoots[round][account], TreePath.FromNibble([.. path.Select(static n => (byte)n)]))?.Type;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Headers.Select(static header => header.StateRoot).Distinct().Count(), Is.EqualTo(GeneratedRounds), $"{context}: distinct roots");
                Assert.That(StorageRoots[1][Refill], Is.EqualTo(Keccak.EmptyTreeHash), $"{context}: refill storage empty at root 1");
                Assert.That(States[1][Refill], Is.Not.Null, $"{context}: refill account kept at root 1");
                if (Seed % 2 == 0) Assert.That(StorageRoots[2][Refill], Is.EqualTo(StorageRoots[0][Refill]), $"{context}: refill storage root of root 0 back");
                else
                {
                    Assert.That(StorageRoots[2][Refill], Is.Not.EqualTo(StorageRoots[0][Refill]), $"{context}: refill storage root changed");
                    Assert.That(StorageNode(2, Refill, a), Is.EqualTo(StorageNode(0, Refill, a)).And.Not.Null, $"{context}: refill branch of root 0 back");
                }

                Assert.That(States[1][Recreated], Is.Null, $"{context}: recreated deleted at root 1");
                Assert.That(StorageRoots[1][Cache], Is.Not.EqualTo(StorageRoots[0][Cache]), $"{context}: cache storage changed at root 1");
                Assert.That(StorageNode(1, Cache, a), Is.EqualTo(StorageNode(0, Cache, a)).And.Not.Null, $"{context}: cache leaf kept at root 1");
                Assert.That(StorageNode(2, Cache, a), Is.Not.EqualTo(StorageNode(1, Cache, a)), $"{context}: cache leaf changed at root 2");
                Assert.That(StorageNode(5, Cache, a), Is.EqualTo(StorageNode(0, Cache, a)), $"{context}: cache leaf of root 0 back at root 5");
                Assert.That(StorageRoots[5][Cache], Is.Not.EqualTo(StorageRoots[0][Cache]), $"{context}: cache storage differs from root 0 at root 5");
                Assert.That(StorageNode(3, Deep, e), Is.Not.EqualTo(StorageNode(2, Deep, e)), $"{context}: deep storage branch changed at root 3");
                Assert.That(StorageNodeType(0, Collapse, p), Is.EqualTo(NodeType.Branch), $"{context}: collapse branch at p at root 0");
                Assert.That(StorageNodeType(1, Collapse, p), Is.EqualTo(NodeType.Extension), $"{context}: collapse extension at p at root 1");
                switch (Shape)
                {
                    case 0:
                        Assert.That(StorageNode(4, Owner, a, c), Is.Not.EqualTo(StorageNode(3, Owner, a, c)), $"{context}: owner leaf ac changed at root 4");
                        Assert.That(StorageNodeType(4, Owner, a, d), Is.EqualTo(NodeType.Branch), $"{context}: owner branch at ad at root 4");
                        Assert.That(StorageNode(5, Owner, a), Is.EqualTo(StorageNode(3, Owner, a)), $"{context}: owner node at a of root 3 back");
                        break;
                    case 1:
                        Assert.That(StorageNodeType(3, Owner, a, c), Is.EqualTo(NodeType.Branch), $"{context}: owner branch at ac at root 3");
                        Assert.That(StorageNodeType(4, Owner, a), Is.EqualTo(NodeType.Extension), $"{context}: owner extension at a at root 4");
                        Assert.That(StorageNode(5, Owner, a, c), Is.EqualTo(StorageNode(3, Owner, a, c)), $"{context}: owner node at ac of root 3 back");
                        break;
                    default:
                        TreePath oldLeafPath = TreePath.FromPath(Accounts[Owner].Path.Bytes).Truncate(NeighborDepth);
                        Assert.That(NodeAt(Store, null, Headers[3].StateRoot!, oldLeafPath)?.Type, Is.EqualTo(NodeType.Leaf), $"{context}: owner leaf at depth {NeighborDepth} at root 3");
                        Assert.That(NodeAt(Store, null, Headers[4].StateRoot!, TreePath.FromNibble([(byte)ownerNibble]))?.Type, Is.EqualTo(NodeType.Leaf), $"{context}: owner leaf at depth 1 at root 4");
                        Assert.That(NodeAt(Store, null, Headers[5].StateRoot!, oldLeafPath)?.Hash, Is.EqualTo(NodeAt(Store, null, Headers[3].StateRoot!, oldLeafPath)?.Hash), $"{context}: owner leaf of root 3 back");
                        break;
                }
            }
        }

        private static BlockHeader Commit(TestRawTrieStore store, GeneratedAccount[] accounts, GeneratedState?[] states, ulong blockNumber, out Hash256[] storageRoots)
        {
            storageRoots = new Hash256[accounts.Length];
            using (store.BeginBlockCommit(blockNumber))
            {
                StateTree stateTree = new(store, LimboLogs.Instance);
                for (int i = 0; i < accounts.Length; i++)
                {
                    storageRoots[i] = Keccak.EmptyTreeHash;
                    if (states[i] is not { } state) continue;
                    if (state.Slots.Count > 0)
                    {
                        StorageTree storageTree = new(store.GetTrieStore(accounts[i].Path.ToCommitment()), LimboLogs.Instance);
                        foreach ((ValueHash256 slot, byte[] value) in state.Slots) storageTree.Set(slot, value);
                        storageTree.Commit();
                        storageRoots[i] = storageTree.RootHash;
                    }

                    stateTree.Set(accounts[i].Path, new Account(state.Nonce, state.Balance, storageRoots[i], state.CodeHash));
                }

                stateTree.Commit();
                return Build.A.BlockHeader.WithNumber(blockNumber).WithStateRoot(stateTree.RootHash).TestObject;
            }
        }

        /// <summary>One change to one of the free accounts, chosen among those that apply, described for the report.</summary>
        private static string ChangeFreeAccount(Random random, GeneratedAccount[] accounts, GeneratedState?[][] states, GeneratedState?[] state, int round)
        {
            int[] free = [.. Enumerable.Range(0, accounts.Length).Where(i => accounts[i].Free)];
            for (int attempt = 0; attempt < 64; attempt++)
            {
                int i = free[random.Next(free.Length)];
                GeneratedAccount account = accounts[i];
                GeneratedState? current = state[i];
                switch (random.Next(6))
                {
                    case 0 when current is not null:
                        if (random.Next(2) == 0) current.Balance += (UInt256)random.Next(1, 100);
                        else current.Nonce++;
                        return $"{account.Name}: balance or nonce changed";
                    case 1 when current is not null && account.Slots.Length > 0:
                        ValueHash256 slot = account.Slots[random.Next(account.Slots.Length)];
                        ChangeSlot(random, current, slot, wide: random.Next(2) == 0);
                        return $"{account.Name}: slot {Short(slot)} set";
                    case 2 when current is not null && current.Slots.Count > 0:
                        ValueHash256 deleted = current.Slots.Keys.ElementAt(random.Next(current.Slots.Count));
                        current.Slots.Remove(deleted);
                        return $"{account.Name}: slot {Short(deleted)} deleted";
                    case 3 when current is null:
                        GeneratedState created = new() { Balance = (UInt256)random.Next(1, 1000) };
                        foreach (int index in Permutation(random, account.Slots.Length).Take(random.Next(Math.Min(4, account.Slots.Length + 1))))
                            created.Slots[account.Slots[index]] = Value(random, random.Next(2) == 0);
                        state[i] = created;
                        return $"{account.Name}: created with {created.Slots.Count} slots";
                    case 4 when current is not null && account.CanBeDeleted:
                        state[i] = null;
                        return $"{account.Name}: deleted";
                    case 5 when account.CanBeDeleted:
                        int earlier = random.Next(round);
                        GeneratedState? restored = states[earlier][i];
                        if (restored is null ? current is null : restored.SameAs(current)) continue;
                        state[i] = restored?.Clone();
                        return $"{account.Name}: as at root {earlier}";
                }
            }

            throw new InvalidOperationException("no change applied to a free account");
        }

        private static void ChangeSlot(Random random, GeneratedState state, in ValueHash256 slot, bool wide)
        {
            byte[] value;
            do value = Value(random, wide);
            while (state.Slots.TryGetValue(slot, out byte[]? current) && current.AsSpan().SequenceEqual(value));
            state.Slots[slot] = value;
        }

        /// <summary>A slot value without leading zeros; a wide one makes the leaf of a shallow slot too long to inline.</summary>
        private static byte[] Value(Random random, bool wide)
        {
            byte[] value = new byte[wide ? 32 : random.Next(1, 4)];
            random.NextBytes(value);
            if (value[0] == 0) value[0] = 1;
            return value;
        }

        private static ValueHash256[] FreeSlots(Random random)
        {
            ValueHash256 first = RandomPath(random);
            ValueHash256 second = Diverge(random, first, 1);
            ValueHash256 fourth = RandomPath(random);
            return [first, second, Diverge(random, second, 2), fourth, Diverge(random, fourth, 3)];
        }

        private static int[] Permutation(Random random, int count = 16)
        {
            int[] values = [.. Enumerable.Range(0, count)];
            for (int i = count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (values[i], values[j]) = (values[j], values[i]);
            }

            return values;
        }

        /// <summary>A random path starting with <paramref name="nibbles"/>.</summary>
        private static ValueHash256 RandomPath(Random random, params int[] nibbles)
        {
            byte[] bytes = new byte[32];
            random.NextBytes(bytes);
            for (int i = 0; i < nibbles.Length; i++) SetNibble(bytes, i, nibbles[i]);
            return new ValueHash256(bytes);
        }

        /// <summary>A random path sharing exactly its first <paramref name="sharedNibbles"/> nibbles with <paramref name="path"/>.</summary>
        private static ValueHash256 Diverge(Random random, in ValueHash256 path, int sharedNibbles)
        {
            byte[] bytes = new byte[32];
            random.NextBytes(bytes);
            for (int i = 0; i < sharedNibbles; i++) SetNibble(bytes, i, GetNibble(path.Bytes, i));
            SetNibble(bytes, sharedNibbles, (GetNibble(path.Bytes, sharedNibbles) + random.Next(1, 16)) % 16);
            return new ValueHash256(bytes);
        }

        private static int GetNibble(ReadOnlySpan<byte> path, int index) => index % 2 == 0 ? path[index / 2] >> 4 : path[index / 2] & 0x0f;

        private static void SetNibble(Span<byte> path, int index, int nibble) =>
            path[index / 2] = index % 2 == 0 ? (byte)((path[index / 2] & 0x0f) | (nibble << 4)) : (byte)((path[index / 2] & 0xf0) | nibble);
    }

    /// <summary>The node at <paramref name="path"/> of the trie at <paramref name="root"/> in the source, if a node is stored there.</summary>
    private static (Hash256 Hash, NodeType Type)? NodeAt(TestRawTrieStore source, Hash256? owner, Hash256 root, in TreePath path)
    {
        IScopedTrieStore store = source.GetTrieStore(owner);
        Hash256 hash = root;
        TreePath at = TreePath.Empty;
        while (true)
        {
            TrieNode node = new(NodeType.Unknown, store.LoadRlp(at, hash)!);
            node.ResolveNode(NullTrieNodeResolver.Instance, at);
            if (at.Length == path.Length) return at == path ? (hash, node.NodeType) : null;
            switch (node.NodeType)
            {
                case NodeType.Branch:
                    int nibble = path[at.Length];
                    if (node.GetChildHash(nibble) is not { } child) return null;
                    at = at.Append(nibble);
                    hash = child;
                    break;
                case NodeType.Extension:
                    byte[] key = node.Key!;
                    if (at.Length + key.Length > path.Length) return null;
                    for (int i = 0; i < key.Length; i++)
                    {
                        if (path[at.Length + i] != key[i]) return null;
                    }

                    TreePath childPath = at;
                    if (node.GetChild(NullTrieNodeResolver.Instance, ref childPath, 0)?.Keccak is not { } next) return null;
                    at = at.Append(key);
                    hash = next;
                    break;
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Drives healing over the generated roots: it moves the pivot on from root 3 after a set number of requests, and from
    /// root 4 once the node of its shape is handled, answers one request of root 3 in part and out of order, and records
    /// what healing was asked for.
    /// </summary>
    private sealed class GeneratedSchedule
    {
        private readonly GeneratedRoots _roots;
        private readonly IPersistence _persistence;
        private readonly Random _random;
        private readonly Queue<string> _recentRequests = new();
        private readonly bool[] _roundCompleted = new bool[GeneratedRounds];
        private int _round = -1;
        private int _roundRequests;
        private string? _partialAnswer;
        private string? _root3Cut;
        private string? _root4Cut;

        public GeneratedSchedule(int seed, GeneratedRoots roots, IPersistence persistence)
        {
            _roots = roots;
            _persistence = persistence;
            _random = new Random(seed ^ 0x51A7);
            Root3RequestsBeforeMovingOn = _random.Next(1, 4);
        }

        public HealingPivot Pivot { get; set; } = null!;

        public int Root3RequestsBeforeMovingOn { get; }

        public List<string> ScheduleErrors { get; } = [];

        public void OnRound(int round)
        {
            if (round >= GeneratedRounds) throw new InvalidOperationException($"healing started round {round}, past the last root");
            if (round > 0) _roundCompleted[round - 1] = HoldsRoot(_persistence, _roots.Headers[round - 1]);
            _round = round;
            _roundRequests = 0;
        }

        public int AnsweredCount(int request, StateSyncBatch batch)
        {
            if (batch.StateRoot != _roots.Headers[_round].StateRoot) throw new InvalidOperationException($"request {request} is for root {batch.StateRoot}, not for root {_round}");
            IList<StateSyncItem> items = batch.RequestedNodes!;
            if (items.FirstOrDefault(static item => item.NodeDataType == NodeDataType.Code) is { } code)
                throw new InvalidOperationException($"request {request} at root {_round} asks for code {code.Hash}, which healing should not reach");

            int answered = items.Count;
            if (_round == 3 && _partialAnswer is null && items.Count >= 2)
            {
                for (int i = items.Count - 1; i > 0; i--)
                {
                    int j = _random.Next(i + 1);
                    (items[i], items[j]) = (items[j], items[i]);
                }

                int half = (items.Count + 1) / 2;
                answered = half + _random.Next(items.Count - half);
                _partialAnswer = $"request {request} answered {answered} of {items.Count}, reordered";
            }

            _recentRequests.Enqueue($"  request {request} at root {_round}, answered {answered} of {items.Count}: {string.Join(", ", items.Take(8).Select(Describe))}{(items.Count > 8 ? ", ..." : "")}");
            if (_recentRequests.Count > GeneratedRecentRequests) _recentRequests.Dequeue();
            return answered;
        }

        public void AfterRequest(int request, StateSyncBatch batch)
        {
            _roundRequests++;
            if (_round == 3 && _root3Cut is null && _roundRequests == Root3RequestsBeforeMovingOn)
            {
                _root3Cut = MoveOn(request, "after its request");
            }
            else if (_round == 4 && _root4Cut is null && batch.RequestedNodes!.Any(IsRoot4Target))
            {
                _root4Cut = MoveOn(request, "after the request with its target");
            }
        }

        private bool IsRoot4Target(StateSyncItem item) =>
            item.Address == _roots.Root4Target.Owner && item.Path == _roots.Root4Target.Path && item.Hash == _roots.Root4Target.Hash
            && item.NodeDataType == (_roots.Root4Target.Owner is null ? NodeDataType.State : NodeDataType.Storage);

        private string MoveOn(int request, string when)
        {
            bool complete = HoldsRoot(_persistence, _roots.Headers[_round]);
            if (complete) ScheduleErrors.Add($"root {_round} was complete when its round moved on");
            Pivot.Header = _roots.Headers[_round + 1];
            return $"round {_round} moved on {when}, request {request}, its {_roundRequests}{(_roundRequests == 1 ? "st" : _roundRequests == 2 ? "nd" : _roundRequests == 3 ? "rd" : "th")} at that root, with the root {(complete ? "complete" : "incomplete")}";
        }

        public void Finish()
        {
            if (_round != GeneratedRounds - 1) ScheduleErrors.Add($"healing ended at round {_round}");
            else _roundCompleted[_round] = HoldsRoot(_persistence, _roots.Headers[_round]);
            if (_root3Cut is null) ScheduleErrors.Add("round 3 never moved on");
            if (_root4Cut is null) ScheduleErrors.Add("round 4 never reached its target");
            for (int round = 0; round < GeneratedRounds; round++)
            {
                bool expected = round is not (3 or 4);
                if (_roundCompleted[round] != expected) ScheduleErrors.Add($"round {round} {(expected ? "did not complete" : "completed")}");
            }
        }

        public string Describe()
        {
            StringBuilder text = new();
            text.AppendLine($"rounds completed: {string.Join(", ", _roundCompleted.Select((completed, round) => $"{round} {(completed ? "yes" : "no")}"))}");
            text.AppendLine($"round 3 planned to move on after request {Root3RequestsBeforeMovingOn} at that root: {_root3Cut ?? "did not"}");
            text.AppendLine($"round 4: {_root4Cut ?? "did not move on"}");
            text.AppendLine($"partial answer: {_partialAnswer ?? "none"}");
            text.AppendLine("last requests:");
            foreach (string request in _recentRequests) text.AppendLine(request);
            return text.ToString();
        }

        private static string Describe(StateSyncItem item) =>
            $"{(item.Address is null ? "state" : $"storage {item.Address.Bytes[..4].ToHexString()}")} '{item.Path}' {item.Hash.Bytes[..4].ToHexString()}";

        /// <summary>Whether the state root node stored hashes to the root of <paramref name="header"/>.</summary>
        public static bool HoldsRoot(IPersistence persistence, BlockHeader header)
        {
            using IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);
            ValueHash256 stored = default;
            bool found = reader.TryLoadStateRlp(TreePath.Empty, ReadFlags.None, static (data, result) => result[0] = ValueKeccak.Compute(data), new Span<ValueHash256>(ref stored));
            return found && stored == header.StateRoot!.ValueHash256;
        }
    }

    /// <summary>A store that deletes only the slots of an emptied storage, keeping its storage trie nodes, as before #16408.</summary>
    private sealed class SlotsOnlyStorageEmptying(ITreeSyncStore inner, IPersistence persistence) : ITreeSyncStore
    {
        public bool NodeExists(Hash256? address, in TreePath path, in ValueHash256 hash) => inner.NodeExists(address, path, hash);

        public void RemoveMismatchedNode(Hash256? address, in TreePath path, in ValueHash256 hash) => inner.RemoveMismatchedNode(address, path, hash);

        public void SaveNode(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> data) => inner.SaveNode(address, path, hash, data);

        public void EnsureStorageEmpty(in ValueHash256 address)
        {
            using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
            batch.DeleteStorageRange(address, ValueKeccak.Zero, ValueKeccak.MaxValue);
        }

        public void FinalizeSync(BlockHeader pivotHeader) => inner.FinalizeSync(pivotHeader);

        public ITreeSyncVerificationContext CreateVerificationContext(byte[] rootNodeData) => inner.CreateVerificationContext(rootNodeData);
    }
}
