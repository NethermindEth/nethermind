// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Merge.Plugin.SszRest;
using Nethermind.Serialization.Ssz;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.StateTransition;

[HardTimeout(60_000)]
public class GloasCachedHasherTests
{
    [Test]
    public void Slot_processing_hashes_through_the_caches_hasher()
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        GloasLineageHasherTests.CountingStateHasher hasher = new(new FullBeaconStateHasher());

        GloasSlotProcessing.ProcessSlots(state, state.Slot + 3, new EpochCache { Hasher = hasher });

        Assert.That(hasher.GloasCalls, Is.EqualTo(3));
    }

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
        GloasLineageHasherTests.CountingStateHasher hasher = new(new FullBeaconStateHasher());

        ForkedStateTransition.Apply(
            new ForkedBeaconState.OfGloas(state), new ForkedSignedBeaconBlock.OfGloas(block), new EpochCache { Hasher = hasher }, new PubkeyCache(), new AcceptingNotifier(),
            UpgradeEpochSpec(), validateResult: true, verifySignatures: false);

        Assert.That(hasher.GloasCalls, Is.EqualTo(2));
    }

    // EIP-7916 subtree sizes are 1, 4, 16, ... chunks. Cross packing/subtree boundaries, then shrink within a subtree to expose stale nodes.
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

    // Mutate one field at a time: equal-valued fixture fields otherwise hide a wrong progressive-container position.
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

    private static object Perturb(Type type, object? value, byte salt, int vectorLength = 0)
    {
        if (type == typeof(ulong))
            return (ulong)value! + salt * 0x1_0001UL;
        if (type == typeof(bool))
            return !(bool)value!;
        if (type == typeof(byte))
            return (byte)((byte)value! ^ salt);
        if (value is SszWithdrawal withdrawal)
            return withdrawal with { Index = withdrawal.Index + salt * 0x1_0001UL, Address = withdrawal.Address ?? Address.Zero };
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

    internal static Builder NewBuilder(Builder template, BlsPublicKey pubkey, ulong balance) => new()
    {
        Pubkey = pubkey,
        Version = template.Version,
        ExecutionAddress = template.ExecutionAddress,
        Balance = balance,
        DepositEpoch = template.DepositEpoch,
        WithdrawableEpoch = template.WithdrawableEpoch,
    };
}
