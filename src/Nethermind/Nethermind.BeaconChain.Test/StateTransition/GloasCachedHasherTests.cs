// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Serialization.Ssz;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.StateTransition;

/// <summary>
/// <see cref="CachedBeaconStateHasher"/> over <see cref="BeaconStateGloas"/>, and the Gloas state-root
/// call sites that must hash through the caller's <see cref="IBeaconStateHasher"/>.
/// </summary>
public class GloasCachedHasherTests
{
    /// <summary>
    /// A stale cache entry is a wrong state root, which fails every block on the lineage. Each stage
    /// mutates one kind of field the way the state transition does, so a cache that misses the change
    /// diverges from the generated root at that stage.
    /// </summary>
    [Test]
    public void Cached_root_matches_full_root_through_mutation_sequence()
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        CachedBeaconStateHasher hasher = new();

        AssertRootsMatch(hasher, state, "initial");
        AssertRootsMatch(hasher, state, "repeated call without mutation");

        BeaconStateGloas uncached = state.Clone();
        ulong targetSlot = 2 * Presets.SlotsPerEpoch + 1;
        GloasSlotProcessing.ProcessSlots(state, targetSlot, new EpochCache { Hasher = hasher });
        GloasSlotProcessing.ProcessSlots(uncached, targetSlot, new EpochCache());
        Assert.That(state.StateRoots, Is.EqualTo(uncached.StateRoots), "state roots cached by slot processing across an epoch boundary");
        AssertRootsMatch(hasher, state, "after slot and epoch processing");

        int count = state.Validators!.Length;
        // One index in each progressive subtree, which starts at (4^k - 1) / 3.
        foreach (int i in (int[])[0, 1, 5, 21, 85, 341, 1365, count - 1])
        {
            state.Balances![i] += 7;
        }
        AssertRootsMatch(hasher, state, "scattered balance edits");

        for (int i = 0; i < 3; i++)
        {
            Validator appended = state.Validators[0].Clone();
            appended.Pubkey = Pubkey((byte)(0xE0 + i));
            state.Validators = [.. state.Validators, appended];
            state.Balances = [.. state.Balances!, 32 * Gwei];
            state.PreviousEpochParticipation = [.. state.PreviousEpochParticipation!, 0];
            state.CurrentEpochParticipation = [.. state.CurrentEpochParticipation!, 0];
            state.InactivityScores = [.. state.InactivityScores!, 0];
        }
        AssertRootsMatch(hasher, state, "validator appends");

        Validator replaced = state.Validators[2].Clone();
        replaced.ExitEpoch = 12345;
        state.Validators[2] = replaced;
        AssertRootsMatch(hasher, state, "validator replacement");

        state.CurrentEpochParticipation![0] |= 0b001;
        state.PreviousEpochParticipation![count - 1] |= 0b110;
        AssertRootsMatch(hasher, state, "participation edits");

        state.InactivityScores![3] += 4;
        AssertRootsMatch(hasher, state, "inactivity score edit");

        Builder builder = state.Builders![0];
        state.Builders = [builder, NewBuilder(builder, Pubkey(0xB1), balance: 9 * Gwei)];
        AssertRootsMatch(hasher, state, "builder append");

        state.Builders[0] = NewBuilder(builder, builder.Pubkey, balance: builder.Balance + Gwei);
        AssertRootsMatch(hasher, state, "builder replacement");

        state.Builders = state.Builders[..1];
        AssertRootsMatch(hasher, state, "builder registry shrink");

        PayloadTimelinessCommittee[] window = state.PtcWindow!;
        ulong[] indices = new ulong[Presets.PtcSize];
        indices.AsSpan().Fill(7);
        window[5] = new PayloadTimelinessCommittee { Indices = indices };
        AssertRootsMatch(hasher, state, "ptc window element replacement");

        int slotsPerEpoch = (int)Presets.SlotsPerEpoch;
        Array.Copy(window, slotsPerEpoch, window, 0, window.Length - slotsPerEpoch);
        AssertRootsMatch(hasher, state, "ptc window shift");

        int availabilityIndex = (int)(state.Slot % Presets.SlotsPerHistoricalRoot);
        state.ExecutionPayloadAvailability![availabilityIndex] = !state.ExecutionPayloadAvailability[availabilityIndex];
        AssertRootsMatch(hasher, state, "execution payload availability bit flip");

        state.BuilderPendingPayments![3] = new BuilderPendingPayment { Weight = 5, ProposerIndex = 1, Withdrawal = new BuilderPendingWithdrawal { Amount = Gwei, BuilderIndex = 0 } };
        state.LatestBlockHash = Hash(0x5A);
        state.NextWithdrawalBuilderIndex = 1;
        AssertRootsMatch(hasher, state, "builder payment, latest block hash and builder sweep index");

        state.RandaoMixes![7] = Hash(0x77);
        state.BlockRoots![1] = Hash(0x11);
        state.StateRoots![2] = Hash(0x22);
        AssertRootsMatch(hasher, state, "randao and root vector updates");

        state.Balances = [.. state.Balances!];
        AssertRootsMatch(hasher, state, "balances array replaced with an equal copy");

        Hash256 originalRoot = SszRoots.HashTreeRoot(state);
        BeaconStateGloas clone = state.Clone();
        clone.Balances![1] += 42;
        Validator cloneReplacement = clone.Validators![3].Clone();
        cloneReplacement.Slashed = true;
        clone.Validators[3] = cloneReplacement;
        clone.LatestBlockHeader!.StateRoot = Hash(0x88);
        clone.ExecutionPayloadAvailability![0] = !clone.ExecutionPayloadAvailability[0];
        clone.PtcWindow![0] = window[5];

        AssertRootsMatch(hasher, clone, "same hasher on the mutated clone");
        AssertRootsMatch(new CachedBeaconStateHasher(), clone, "fresh hasher on the mutated clone");
        Assert.That(SszRoots.HashTreeRoot(state), Is.EqualTo(originalRoot), "original state unchanged after mutating the clone");
        AssertRootsMatch(hasher, state, "same hasher back on the original lineage");
    }

    /// <summary>Slot processing takes every per-slot state root through the lineage's hasher, never a full re-merkleization.</summary>
    [Test]
    public void Slot_processing_hashes_through_the_caches_hasher()
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        CountingHasher hasher = new();

        GloasSlotProcessing.ProcessSlots(state, state.Slot + 3, new EpochCache { Hasher = hasher });

        Assert.That(hasher.GloasCalls, Is.EqualTo(3));
    }

    /// <summary>The post-state root check of a Gloas block goes through the caller's cache hasher, after the one per advanced slot.</summary>
    [Test]
    public void Block_state_root_check_hashes_through_the_caches_hasher()
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        ulong blockSlot = state.Slot + 1;
        BeaconStateGloas post = state.Clone();
        GloasSlotProcessing.ProcessSlots(post, blockSlot, new EpochCache());
        SignedBeaconBlockGloas block = MinimalBlock(post, SelfBuildBid(post, post.LatestBlockHash!, Hash(0x9A)));
        ApplyBlock(post, block, new EpochCache());
        block.Message!.StateRoot = SszRoots.HashTreeRoot(post);
        CountingHasher hasher = new();

        ForkedStateTransition.Apply(
            new ForkedBeaconState.OfGloas(state), new ForkedSignedBeaconBlock.OfGloas(block), new EpochCache { Hasher = hasher }, new PubkeyCache(), new AcceptingNotifier(),
            UpgradeEpochSpec(), validateResult: true, verifySignatures: false);

        Assert.That(hasher.GloasCalls, Is.EqualTo(2));
    }

    /// <summary>
    /// EIP-7916 subtrees hold 1, 4, 16, 64, 256, ... chunks. The lengths step one element across the
    /// first chunk of each subtree for every packing (a validator or builder per chunk, 4 uint64 or
    /// 32 participation bytes per chunk), so both the patch path and the new-subtree path run, then
    /// shrink back to an empty list and grow again. The last lengths shrink by one element inside a
    /// subtree, where a patch instead of a rebuild keeps stale nodes past the new last chunk.
    /// </summary>
    [Test]
    public void Cached_root_matches_full_root_as_progressive_lists_cross_subtree_boundaries_and_shrink_to_empty()
    {
        int[] lengths =
        [
            1, 2, 4, 5, 6, 16, 20, 21, 22, 32, 33, 64, 84, 85, 86, 160, 161, 256, 340, 341, 342,
            672, 673, 1364, 1365, 1366, 2720, 2721, 5460, 5461, 5462, 10912, 10913, 10914,
            300, 85, 21, 5, 1, 0, 1, 0, 64, 9, 8, 7, 6, 12, 11, 25, 24, 23, 33, 32, 31, 257, 256, 255,
        ];
        int maxLength = 10914;
        BeaconStateGloas state = CreateGloasState(out _, out _);
        Validator[] validators = new Validator[maxLength];
        Builder[] builders = new Builder[maxLength];
        ulong[] balances = new ulong[maxLength];
        byte[] participation = new byte[maxLength];
        for (int i = 0; i < maxLength; i++)
        {
            validators[i] = state.Validators![0].Clone();
            validators[i].EffectiveBalance = (ulong)i;
            builders[i] = NewBuilder(state.Builders![0], state.Builders[0].Pubkey, balance: (ulong)i);
            balances[i] = 32 * Gwei + (ulong)i;
            participation[i] = (byte)(i % 8);
        }
        CachedBeaconStateHasher hasher = new();

        foreach (int length in lengths)
        {
            state.Validators = validators[..length];
            state.Builders = builders[..length];
            state.Balances = balances[..length];
            state.InactivityScores = balances[..length];
            state.PreviousEpochParticipation = participation[..length];
            state.CurrentEpochParticipation = participation[..length];
            AssertRootsMatch(hasher, state, $"{length} elements");

            if (length == 0)
                continue;
            int middle = length / 2;
            Validator replaced = state.Validators[middle].Clone();
            replaced.Slashed = true;
            state.Validators[middle] = replaced;
            state.Builders[middle] = NewBuilder(state.Builders[middle], state.Builders[middle].Pubkey, balance: Gwei);
            state.Balances[middle] ^= 1;
            state.InactivityScores[length - 1] += 1;
            state.CurrentEpochParticipation[middle] ^= 0b100;
            AssertRootsMatch(hasher, state, $"{length} elements, one entry of each list rewritten");
        }
    }

    /// <summary>
    /// Each of the 46 fields must reach its own position in the progressive container. The fixture
    /// leaves several fields equal (the withdrawal indices, the churn epochs, the empty pending
    /// lists), so only a change to one field alone shows a root taken from the wrong position.
    /// </summary>
    [TestCaseSource(nameof(GloasStateFields))]
    public void Cached_root_matches_full_root_after_one_field_changes(string field)
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        CachedBeaconStateHasher hasher = new();
        Hash256 before = hasher.HashTreeRoot(state);
        PropertyInfo property = typeof(BeaconStateGloas).GetProperty(field)!;
        byte salt = (byte)(property.GetCustomAttribute<SszFieldAttribute>()!.Index + 1);

        property.SetValue(state, Perturb(property, property.GetValue(state), salt));

        Hash256 full = SszRoots.HashTreeRoot(state);
        Assert.That(full, Is.Not.EqualTo(before), "the change reaches the generated root");
        Assert.That(hasher.HashTreeRoot(state), Is.EqualTo(full), "warm caches");
        Assert.That(new CachedBeaconStateHasher().HashTreeRoot(state), Is.EqualTo(full), "cold caches");
    }

    private static IEnumerable<string> GloasStateFields() =>
        typeof(BeaconStateGloas).GetProperties().Where(static p => p.GetCustomAttribute<SszFieldAttribute>() is not null).Select(static p => p.Name);

    private static object Perturb(PropertyInfo property, object? value, byte salt) =>
        Perturb(property.PropertyType, value, salt, property.GetCustomAttribute<SszVectorAttribute>()?.Length ?? 0);

    /// <summary>Returns a new value of <paramref name="type"/> that differs from <paramref name="value"/>, copying containers and arrays rather than writing them in place; a null or empty fixed vector is filled to <paramref name="vectorLength"/>.</summary>
    private static object Perturb(Type type, object? value, byte salt, int vectorLength = 0)
    {
        if (type == typeof(ulong))
            return (ulong)value! + salt * 0x1_0001UL;
        if (type == typeof(bool))
            return !(bool)value!;
        if (type == typeof(byte))
            return (byte)((byte)value! ^ salt);
        if (type == typeof(Hash256))
        {
            byte[] bytes = (value as Hash256)?.Bytes.ToArray() ?? new byte[Hash256.Size];
            bytes[0] ^= salt;
            return new Hash256(bytes);
        }
        if (type == typeof(BlsPublicKey))
        {
            byte[] bytes = ((BlsPublicKey)value!).Bytes.ToArray();
            bytes[0] ^= salt;
            return new BlsPublicKey(bytes);
        }
        if (type == typeof(BitArray))
        {
            BitArray bits = new((BitArray)value!);
            bits[0] = !bits[0];
            return bits;
        }
        if (type.IsArray)
        {
            Type elementType = type.GetElementType()!;
            Array source = (Array?)value ?? Array.CreateInstance(elementType, 0);
            // An empty list gains one element; any other array has its first element changed.
            Array copy = Array.CreateInstance(elementType, Math.Max(source.Length, Math.Max(vectorLength, 1)));
            Array.Copy(source, copy, source.Length);
            object? first = source.Length == 0 ? (elementType.IsValueType ? Activator.CreateInstance(elementType) : null) : source.GetValue(0);
            copy.SetValue(Perturb(elementType, first, salt), 0);
            return copy;
        }
        if (type.IsClass && type.GetConstructor(Type.EmptyTypes) is not null)
        {
            object container = Activator.CreateInstance(type)!;
            PropertyInfo[] properties = type.GetProperties().Where(static p => p.CanRead && p.CanWrite).ToArray();
            if (value is not null)
            {
                foreach (PropertyInfo p in properties)
                    p.SetValue(container, p.GetValue(value));
            }
            PropertyInfo target = properties.OrderBy(static p => p.PropertyType.IsArray || p.PropertyType.IsClass && p.PropertyType != typeof(Hash256) ? 1 : 0).First();
            target.SetValue(container, Perturb(target, target.GetValue(container), salt));
            return container;
        }
        throw new NotSupportedException($"No perturbation for {type}");
    }

    public enum MalformedField { ShortPendingPayments, LongPendingPayments, NullPendingPayments, NullBid, ShortAvailability, LongAvailability, ShortPtcWindow, LongPtcWindow, NullPtcWindow }

    /// <summary>A state the generated Merkleize rejects must not get a root from the cached hasher, cold or with warm caches.</summary>
    [Test]
    public void Cached_hasher_rejects_what_the_generated_merkleize_rejects([Values] MalformedField field, [Values] bool warm)
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        CachedBeaconStateHasher hasher = new();
        if (warm)
            hasher.HashTreeRoot(state);

        BuilderPendingPayment[] payments = state.BuilderPendingPayments!;
        int availabilityLength = (int)Presets.SlotsPerHistoricalRoot;
        switch (field)
        {
            case MalformedField.ShortPendingPayments: state.BuilderPendingPayments = payments[..^1]; break;
            case MalformedField.LongPendingPayments: state.BuilderPendingPayments = [.. payments, new BuilderPendingPayment { Withdrawal = new BuilderPendingWithdrawal() }]; break;
            case MalformedField.NullPendingPayments: state.BuilderPendingPayments = null; break;
            case MalformedField.NullBid: state.LatestExecutionPayloadBid = null; break;
            case MalformedField.ShortAvailability: state.ExecutionPayloadAvailability = new BitArray(availabilityLength - 1); break;
            case MalformedField.LongAvailability: state.ExecutionPayloadAvailability = new BitArray(availabilityLength + 1); break;
            case MalformedField.ShortPtcWindow: state.PtcWindow = state.PtcWindow![..^1]; break;
            case MalformedField.LongPtcWindow: state.PtcWindow = [.. state.PtcWindow!, state.PtcWindow![0]]; break;
            case MalformedField.NullPtcWindow: state.PtcWindow = null; break;
        }

        Assert.That(() => SszRoots.HashTreeRoot(state), Throws.TypeOf<InvalidDataException>(), "generated Merkleize");
        Assert.That(() => hasher.HashTreeRoot(state), Throws.TypeOf<InvalidDataException>(), "cached hasher");
    }

    /// <summary>
    /// After a reorg the lineage's hasher keeps hashing a sibling of the states it has seen. Each
    /// sibling crosses an epoch boundary (registry, balance, participation, ptc_window and
    /// availability updates) with its per-slot roots taken through the shared hasher, and must
    /// cache exactly the roots a full re-merkleization gives its twin.
    /// </summary>
    [Test]
    public void One_hasher_follows_sibling_states_after_a_reorg()
    {
        BeaconStateGloas parent = CreateGloasState(out Bls.SecretKey builderSk, out _);
        CachedBeaconStateHasher hasher = new();
        AssertRootsMatch(hasher, parent, "common parent");

        BeaconStateGloas builderBranch = parent.Clone();
        BeaconStateGloas builderTwin = parent.Clone();
        SignedExecutionPayloadBid bid = ValidBuilderBid(parent, builderSk, builderIndex: 0, value: 3 * Gwei);
        AdvanceSibling(builderBranch, builderTwin, bid, 2 * Presets.SlotsPerEpoch + 2, hasher, "builder-bid sibling");

        BeaconStateGloas selfBuildBranch = parent.Clone();
        BeaconStateGloas selfBuildTwin = parent.Clone();
        SignedExecutionPayloadBid selfBuild = SelfBuildBid(parent, parent.LatestBlockHash!, Hash(0x9B));
        AdvanceSibling(selfBuildBranch, selfBuildTwin, selfBuild, 2 * Presets.SlotsPerEpoch + 5, hasher, "self-build sibling after the reorg");

        GloasSlotProcessing.ProcessSlots(builderBranch, 3 * Presets.SlotsPerEpoch + 1, new EpochCache { Hasher = hasher });
        GloasSlotProcessing.ProcessSlots(builderTwin, 3 * Presets.SlotsPerEpoch + 1, new EpochCache());
        Assert.That(builderBranch.StateRoots, Is.EqualTo(builderTwin.StateRoots), "per-slot roots back on the first sibling");
        AssertRootsMatch(hasher, builderBranch, "first sibling after reorging back");
    }

    /// <summary>Local in-process measurement of <see cref="GloasSlotProcessing.ProcessSlot"/> on a mainnet-sized registry, cached against full re-merkleization.</summary>
    [Explicit("Mainnet-scale performance measurement; allocates several GB and runs for minutes")]
    [Test]
    public void Mainnet_scale_slot_processing_speedup()
    {
        const int validatorCount = 1_200_000;
        const int slots = 16;
        BeaconStateGloas state = CreateGloasState(out _, out _);
        Validator[] validators = new Validator[validatorCount];
        ulong[] balances = new ulong[validatorCount];
        byte[] previousParticipation = new byte[validatorCount];
        byte[] currentParticipation = new byte[validatorCount];
        ulong[] inactivityScores = new ulong[validatorCount];
        for (int i = 0; i < validatorCount; i++)
        {
            validators[i] = state.Validators![i % ValidatorCount].Clone();
            validators[i].EffectiveBalance = 32 * Gwei - (ulong)(i % 3) * Gwei;
            balances[i] = 32 * Gwei + (ulong)i;
            previousParticipation[i] = (byte)(i % 8);
            currentParticipation[i] = (byte)(i % 4);
            inactivityScores[i] = (ulong)(i % 5);
        }
        state.Validators = validators;
        state.Balances = balances;
        state.PreviousEpochParticipation = previousParticipation;
        state.CurrentEpochParticipation = currentParticipation;
        state.InactivityScores = inactivityScores;
        BeaconStateGloas uncached = state.Clone();
        CachedBeaconStateHasher hasher = new();

        Stopwatch stopwatch = Stopwatch.StartNew();
        hasher.HashTreeRoot(state);
        TestContext.Out.WriteLine($"Cached cold hash-tree-root:     {stopwatch.ElapsedMilliseconds} ms");

        double cachedMs = TimeSlots(state, hasher, slots);
        double fullMs = TimeSlots(uncached, new FullBeaconStateHasher(), slots);
        TestContext.Out.WriteLine($"ProcessSlot, cached hasher:     {cachedMs:F1} ms/slot");
        TestContext.Out.WriteLine($"ProcessSlot, full hasher:       {fullMs:F1} ms/slot");
        TestContext.Out.WriteLine($"Speed-up:                       {fullMs / cachedMs:F1}x");

        Assert.That(state.StateRoots, Is.EqualTo(uncached.StateRoots), "cached per-slot roots at mainnet scale");
    }

    private static double TimeSlots(BeaconStateGloas state, IBeaconStateHasher hasher, int slots)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < slots; i++)
        {
            GloasSlotProcessing.ProcessSlot(state, hasher);
            state.Slot++;
        }
        return stopwatch.Elapsed.TotalMilliseconds / slots;
    }

    private static void AdvanceSibling(BeaconStateGloas branch, BeaconStateGloas twin, SignedExecutionPayloadBid bid, ulong targetSlot, CachedBeaconStateHasher hasher, string stage)
    {
        ApplyBlock(branch, MinimalBlock(branch, bid), new EpochCache { Hasher = hasher });
        ApplyBlock(twin, MinimalBlock(twin, bid), new EpochCache());
        AssertRootsMatch(hasher, branch, $"{stage}: after its block");

        GloasSlotProcessing.ProcessSlots(branch, targetSlot, new EpochCache { Hasher = hasher });
        GloasSlotProcessing.ProcessSlots(twin, targetSlot, new EpochCache());
        Assert.That(branch.StateRoots, Is.EqualTo(twin.StateRoots), $"{stage}: per-slot roots across the epoch boundary");
        AssertRootsMatch(hasher, branch, $"{stage}: after the epoch boundary");
    }

    private static void AssertRootsMatch(CachedBeaconStateHasher hasher, BeaconStateGloas state, string stage) =>
        Assert.That(hasher.HashTreeRoot(state), Is.EqualTo(SszRoots.HashTreeRoot(state)), stage);

    private static Builder NewBuilder(Builder template, BlsPublicKey pubkey, ulong balance) => new()
    {
        Pubkey = pubkey,
        Version = template.Version,
        ExecutionAddress = template.ExecutionAddress,
        Balance = balance,
        DepositEpoch = template.DepositEpoch,
        WithdrawableEpoch = template.WithdrawableEpoch,
    };

    private sealed class CountingHasher : IBeaconStateHasher
    {
        public int GloasCalls { get; private set; }

        public Hash256 HashTreeRoot(BeaconStateFulu state) => SszRoots.HashTreeRoot(state);

        public Hash256 HashTreeRoot(BeaconStateGloas state)
        {
            GloasCalls++;
            return SszRoots.HashTreeRoot(state);
        }
    }
}
