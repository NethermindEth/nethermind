// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

using static Nethermind.BeaconChain.Test.ForkChoice.TestHashes;

namespace Nethermind.BeaconChain.Test.StateTransition;

public class CachedHasherTests
{
    private const ulong Gwei = 1_000_000_000;

    [TestCase(false, 64)]
    [TestCase(false, 10_000)]
    [TestCase(true, GloasTestFixtures.ValidatorCount)]
    [HardTimeout(60_000)]
    public void Cached_root_matches_full_root_through_mutation_sequence(bool gloas, int validatorCount)
    {
        dynamic state = gloas ? (object)GloasTestFixtures.CreateGloasState(out _, out _) : CreateState(validatorCount);
        CachedBeaconStateHasher hasher = new();
        AssertRootsMatch(hasher, state, "initial");
        AssertRootsMatch(hasher, state, "repeated call without mutation");
        if (gloas)
        {
            BeaconStateGloas uncached = GloasStateClone.Clone(state);
            ulong targetSlot = 2 * Presets.SlotsPerEpoch + 1;
            GloasSlotProcessing.ProcessSlots(state, targetSlot, new EpochCache { Hasher = hasher });
            GloasSlotProcessing.ProcessSlots(uncached, targetSlot, new EpochCache());
            Assert.That(state.StateRoots, Is.EqualTo(uncached.StateRoots), "state roots cached by slot processing across an epoch boundary");
            AssertRootsMatch(hasher, state, "after slot and epoch processing");
        }
        int count = state.Validators.Length;
        if (gloas)
        {
            // One index in each progressive subtree, which starts at (4^k - 1) / 3.
            foreach (int i in (int[])[0, 1, 5, 21, 85, 341, 1365, count - 1])
            {
                state.Balances![i] += 7;
            }
        }
        else
        {
            state.Balances![1] += 7;
            state.Balances[validatorCount / 2] -= 3;
            state.Balances[validatorCount - 1] = 2048 * Gwei;
        }
        AssertRootsMatch(hasher, state, "scattered balance edits");
        for (int i = 0; i < 3; i++)
        {
            if (gloas)
            {
                Validator appended = ((Validator)state.Validators[0]).Clone();
                appended.Pubkey = GloasTestFixtures.Pubkey((byte)(0xE0 + i));
                state.Validators = (Validator[])[.. (Validator[])state.Validators, appended];
                state.Balances = (ulong[])[.. (ulong[])state.Balances, 32 * Gwei];
                state.PreviousEpochParticipation = (byte[])[.. (byte[])state.PreviousEpochParticipation, 0];
                state.CurrentEpochParticipation = (byte[])[.. (byte[])state.CurrentEpochParticipation, 0];
                state.InactivityScores = (ulong[])[.. (ulong[])state.InactivityScores, 0];
            }
            else
            {
                ((BeaconStateFulu)state).AddValidatorToRegistry(Pubkey(validatorCount + i), FromFirstByte(0xAB), 32 * Gwei);
            }
        }
        AssertRootsMatch(hasher, state, "validator appends");

        Validator replaced = ((Validator)state.Validators[2]).Clone();
        replaced.ExitEpoch = 12345;
        state.Validators[2] = replaced;
        AssertRootsMatch(hasher, state, "validator replacement");

        state.CurrentEpochParticipation![0] |= 0b001;
        state.PreviousEpochParticipation![count - 1] |= 0b110;
        AssertRootsMatch(hasher, state, "participation edits");

        PayloadTimelinessCommittee[]? window = null;
        if (gloas)
        {
            state.InactivityScores![3] += 4;
            AssertRootsMatch(hasher, state, "inactivity score edit");

            Builder builder = state.Builders![0];
            state.Builders = (Builder[])[builder, GloasCachedHasherTests.NewBuilder(builder, GloasTestFixtures.Pubkey(0xB1), balance: 9 * Gwei)];
            AssertRootsMatch(hasher, state, "builder append");

            state.Builders[0] = GloasCachedHasherTests.NewBuilder(builder, builder.Pubkey, balance: builder.Balance + Gwei);
            AssertRootsMatch(hasher, state, "builder replacement");

            state.Builders = ((Builder[])state.Builders)[..1];
            AssertRootsMatch(hasher, state, "builder registry shrink");

            window = state.PtcWindow!;
            ulong[] indices = Enumerable.Repeat(7UL, (int)Presets.PtcSize).ToArray();
            window[5] = new PayloadTimelinessCommittee { Indices = indices };
            AssertRootsMatch(hasher, state, "ptc window element replacement");

            int slotsPerEpoch = (int)Presets.SlotsPerEpoch;
            Array.Copy(window, slotsPerEpoch, window, 0, window.Length - slotsPerEpoch);
            AssertRootsMatch(hasher, state, "ptc window shift");

            int availabilityIndex = (int)(state.Slot % Presets.SlotsPerHistoricalRoot);
            state.ExecutionPayloadAvailability![availabilityIndex] = !state.ExecutionPayloadAvailability[availabilityIndex];
            AssertRootsMatch(hasher, state, "execution payload availability bit flip");

            state.BuilderPendingPayments![3] = new BuilderPendingPayment { Weight = 5, ProposerIndex = 1, Withdrawal = new BuilderPendingWithdrawal { Amount = Gwei, BuilderIndex = 0 } };
            state.LatestBlockHash = GloasTestFixtures.Hash(0x5A);
            state.NextWithdrawalBuilderIndex = 1;
            AssertRootsMatch(hasher, state, "builder payment, latest block hash and builder sweep index");
        }
        state.RandaoMixes[7] = gloas ? GloasTestFixtures.Hash(0x77) : FromFirstByte(0x77);
        state.BlockRoots[1] = gloas ? GloasTestFixtures.Hash(0x11) : FromFirstByte(0x11);
        state.StateRoots[2] = gloas ? GloasTestFixtures.Hash(0x22) : FromFirstByte(0x22);
        AssertRootsMatch(hasher, state, "randao and root vector updates");
        state.Balances = (ulong[])[.. (ulong[])state.Balances];
        AssertRootsMatch(hasher, state, "balances array replaced with an equal copy");
        if (!gloas)
        {
            EpochProcessing.ProcessEpoch(state, new EpochCache());
            state.Slot++;
            AssertRootsMatch(hasher, state, "after ProcessEpoch");
        }
        Hash256 originalRoot = SszRoots.HashTreeRoot(state);
        dynamic clone = gloas ? (object)GloasStateClone.Clone(state) : BeaconStateClone.Clone(state);
        if (!gloas)
            Assert.That(SszRoots.HashTreeRoot(clone), Is.EqualTo(originalRoot), "clone preserves the root");
        clone.Balances![1] += 42;
        Validator cloneReplacement = ((Validator)clone.Validators[3]).Clone();
        cloneReplacement.Slashed = true;
        clone.Validators[3] = cloneReplacement;
        if (gloas)
        {
            clone.LatestBlockHeader.StateRoot = GloasTestFixtures.Hash(0x88);
            clone.ExecutionPayloadAvailability[0] = !clone.ExecutionPayloadAvailability[0];
            clone.PtcWindow[0] = window![5];
        }
        else
        {
            clone.RandaoMixes![9] = FromFirstByte(0x99);
            clone.LatestBlockHeader!.StateRoot = FromFirstByte(0x88); // The in-place header write ProcessSlot performs.
            clone.CurrentEpochParticipation![1] |= 0b010;
            clone.Slashings![0] += Gwei;
        }
        AssertRootsMatch(hasher, clone, "same hasher on the mutated clone");
        AssertRootsMatch(new CachedBeaconStateHasher(), clone, "fresh hasher on the mutated clone");
        Assert.That(SszRoots.HashTreeRoot(state), Is.EqualTo(originalRoot), "original state unchanged after mutating the clone");
        AssertRootsMatch(hasher, state, "same hasher back on the original lineage");
    }

    [Explicit("Mainnet-scale performance measurement; allocates several GB and runs for minutes")]
    [Test]
    public void Mainnet_scale_performance()
    {
        const int validatorCount = 2_300_000;
        BeaconStateFulu state = CreateState(validatorCount);
        CachedBeaconStateHasher hasher = new();
        Random random = new(42);

        Stopwatch stopwatch = Stopwatch.StartNew();
        Hash256 fullRoot = SszRoots.HashTreeRoot(state);
        TestContext.Progress.WriteLine($"Full hash-tree-root:                {stopwatch.ElapsedMilliseconds} ms");

        stopwatch.Restart();
        Hash256 coldRoot = hasher.HashTreeRoot(state);
        long coldMs = stopwatch.ElapsedMilliseconds;
        TestContext.Progress.WriteLine($"Cached cold hash-tree-root:         {coldMs} ms");
        Assert.That(coldRoot, Is.EqualTo(fullRoot), "cold cached root");

        // Per-block-like mutation: 10k balances, 32k participation bytes, 1 randao mix, 2 roots.
        for (int i = 0; i < 10_000; i++)
        {
            state.Balances![random.Next(validatorCount)] += 1;
        }
        for (int i = 0; i < 32_000; i++)
        {
            state.CurrentEpochParticipation![random.Next(validatorCount)] |= 0b111;
        }
        state.RandaoMixes![123] = FromFirstByte(0x55);
        state.BlockRoots![45] = FromFirstByte(0x66);
        state.StateRoots![46] = FromFirstByte(0x67);
        stopwatch.Restart();
        hasher.HashTreeRoot(state);
        long warmBlockMs = stopwatch.ElapsedMilliseconds;
        TestContext.Progress.WriteLine($"Cached warm root (block-like diff): {warmBlockMs} ms");

        // Epoch-like mutation: every balance rewritten.
        for (int i = 0; i < validatorCount; i++)
        {
            state.Balances![i] += 3;
        }
        stopwatch.Restart();
        Hash256 epochRoot = hasher.HashTreeRoot(state);
        long warmEpochMs = stopwatch.ElapsedMilliseconds;
        TestContext.Progress.WriteLine($"Cached warm root (epoch-like diff): {warmEpochMs} ms");
        Assert.That(epochRoot, Is.EqualTo(SszRoots.HashTreeRoot(state)), "warm cached root after full balance rewrite");

        stopwatch.Restart();
        BeaconStateFulu clone = state.Clone();
        long cloneMs = stopwatch.ElapsedMilliseconds;
        TestContext.Progress.WriteLine($"Clone:                              {cloneMs} ms");
        Assert.That(clone.Balances![0], Is.EqualTo(state.Balances![0]));

        Assert.That(warmBlockMs, Is.LessThan(500), "warm per-block hash budget");
        Assert.That(cloneMs, Is.LessThan(200), "clone budget");
    }

    private static void AssertRootsMatch(CachedBeaconStateHasher hasher, dynamic state, string stage) =>
        Assert.That(hasher.HashTreeRoot(state), Is.EqualTo(SszRoots.HashTreeRoot(state)), stage);

    internal static BeaconStateFulu CreateState(int validatorCount)
    {
        Validator[] validators = Enumerable.Range(0, validatorCount).Select(static i => GloasTestFixtures.CreateActiveValidator(Pubkey(i))).ToArray();
        ulong[] balances = Enumerable.Range(0, validatorCount).Select(static i => 32 * Gwei + (ulong)i).ToArray();
        byte[] previousParticipation = Enumerable.Range(0, validatorCount).Select(static i => (byte)(i % 8)).ToArray();
        byte[] currentParticipation = Enumerable.Range(0, validatorCount).Select(static i => (byte)(i % 4)).ToArray();
        ulong[] inactivityScores = Enumerable.Range(0, validatorCount).Select(static i => (ulong)(i % 5)).ToArray();

        Hash256[] blockRoots = Enumerable.Range(0, (int)Presets.SlotsPerHistoricalRoot).Select(static _ => FromFirstByte(0x0B)).ToArray();
        Hash256[] stateRoots = Enumerable.Range(0, (int)Presets.SlotsPerHistoricalRoot).Select(static _ => FromFirstByte(0x0C)).ToArray();
        Hash256[] randaoMixes = Enumerable.Repeat(FromFirstByte(0x42), (int)Presets.EpochsPerHistoricalVector).ToArray();

        BlsPublicKey[] committee = Enumerable.Range(0, 512).Select(i => validators[i % validatorCount].Pubkey).ToArray();
        SyncCommittee syncCommittee = new() { Pubkeys = committee, AggregatePubkey = Pubkey(0) };

        Eth1Data eth1Data = new() { DepositRoot = FromFirstByte(0x01), DepositCount = 16, BlockHash = FromFirstByte(0x02) };
        ulong[] slashings = new ulong[(int)Presets.EpochsPerSlashingsVector];
        slashings[0] = Gwei;

        return new BeaconStateFulu
        {
            GenesisTime = 1_600_000_000,
            GenesisValidatorsRoot = FromFirstByte(0x10),
            Slot = 6 * Presets.SlotsPerEpoch - 1, // Last slot of epoch 5.
            Fork = new Fork { PreviousVersion = new byte[4], CurrentVersion = [0, 0, 0, 1], Epoch = 0 },
            LatestBlockHeader = new BeaconBlockHeader { Slot = 100, ParentRoot = FromFirstByte(0x20), StateRoot = FromFirstByte(0x21), BodyRoot = FromFirstByte(0x22) },
            BlockRoots = blockRoots,
            StateRoots = stateRoots,
            HistoricalRoots = [FromFirstByte(0x30), FromFirstByte(0x31)],
            Eth1Data = eth1Data,
            Eth1DataVotes = [eth1Data],
            Eth1DepositIndex = 16,
            Validators = validators,
            Balances = balances,
            RandaoMixes = randaoMixes,
            Slashings = slashings,
            PreviousEpochParticipation = previousParticipation,
            CurrentEpochParticipation = currentParticipation,
            JustificationBits = new BitArray(4) { [0] = true, [1] = true },
            PreviousJustifiedCheckpoint = new Checkpoint { Epoch = 3, Root = FromFirstByte(0x0B) },
            CurrentJustifiedCheckpoint = new Checkpoint { Epoch = 4, Root = FromFirstByte(0x0B) },
            FinalizedCheckpoint = new Checkpoint { Epoch = 4, Root = FromFirstByte(0x0B) },
            InactivityScores = inactivityScores,
            CurrentSyncCommittee = syncCommittee,
            NextSyncCommittee = syncCommittee,
            LatestExecutionPayloadHeader = new ExecutionPayloadHeader { BlockNumber = 7, BlockHash = FromFirstByte(0x40), ExtraData = [] },
            NextWithdrawalIndex = 5,
            NextWithdrawalValidatorIndex = 9,
            HistoricalSummaries = [new HistoricalSummary { BlockSummaryRoot = FromFirstByte(0x50), StateSummaryRoot = FromFirstByte(0x51) }],
            DepositRequestsStartIndex = Presets.UnsetDepositRequestsStartIndex,
            // Slot > 0 with the Eth1 bridge still active makes ProcessPendingDeposits stop at this
            // entry without verifying its (synthetic) signature.
            PendingDeposits = [new PendingDeposit { Pubkey = Pubkey(0), WithdrawalCredentials = Hash256.Zero, Amount = Gwei, Slot = 1000 }],
            PendingPartialWithdrawals = [new PendingPartialWithdrawal { ValidatorIndex = 1, Amount = Gwei, WithdrawableEpoch = 1000 }],
            PendingConsolidations = [new PendingConsolidation { SourceIndex = 1, TargetIndex = 2 }],
            ProposerLookahead = new ulong[(int)Presets.ProposerLookaheadSlots],
        };
    }

    private static BlsPublicKey Pubkey(int index)
    {
        byte[] bytes = new byte[BlsPublicKey.Length];
        bytes[0] = 0xA0;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), index);
        return new BlsPublicKey(bytes);
    }
}
