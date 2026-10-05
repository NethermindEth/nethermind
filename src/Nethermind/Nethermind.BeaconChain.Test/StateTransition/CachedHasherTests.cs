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

    [TestCase(64)]
    [TestCase(10_000)]
    public void Cached_root_matches_full_root_through_mutation_sequence(int validatorCount)
    {
        BeaconStateFulu state = CreateState(validatorCount);
        CachedBeaconStateHasher hasher = new();

        AssertRootsMatch(hasher, state, "initial");
        AssertRootsMatch(hasher, state, "repeated call without mutation");

        state.Balances![1] += 7;
        state.Balances[validatorCount / 2] -= 3;
        state.Balances[validatorCount - 1] = 2048 * Gwei;
        AssertRootsMatch(hasher, state, "scattered balance edits");

        // Appends grow the registry lists (and cross a power-of-two boundary for the 64 case).
        for (int i = 0; i < 3; i++)
        {
            state.AddValidatorToRegistry(Pubkey(validatorCount + i), FromFirstByte(0xAB), 32 * Gwei);
        }
        AssertRootsMatch(hasher, state, "validator appends");

        Validator replaced = state.Validators![2].Clone();
        replaced.ExitEpoch = 12345;
        state.Validators[2] = replaced;
        AssertRootsMatch(hasher, state, "validator replacement");

        state.CurrentEpochParticipation![0] |= 0b001;
        state.PreviousEpochParticipation![validatorCount - 1] |= 0b110;
        AssertRootsMatch(hasher, state, "participation edits");

        state.RandaoMixes![7] = FromFirstByte(0x77);
        state.BlockRoots![1] = FromFirstByte(0x11);
        state.StateRoots![2] = FromFirstByte(0x22);
        AssertRootsMatch(hasher, state, "randao and root vector updates");

        // A value-identical array replacement must be recognized as unchanged.
        state.Balances = [.. state.Balances];
        AssertRootsMatch(hasher, state, "balances array replaced with an equal copy");

        EpochProcessing.ProcessEpoch(state, new EpochCache());
        state.Slot++;
        AssertRootsMatch(hasher, state, "after ProcessEpoch");

        Hash256 originalRoot = SszRoots.HashTreeRoot(state);
        BeaconStateFulu clone = state.Clone();
        Assert.That(SszRoots.HashTreeRoot(clone), Is.EqualTo(originalRoot), "clone preserves the root");

        clone.Balances![1] += 42;
        Validator cloneReplacement = clone.Validators![3].Clone();
        cloneReplacement.Slashed = true;
        clone.Validators[3] = cloneReplacement;
        clone.RandaoMixes![9] = FromFirstByte(0x99);
        clone.LatestBlockHeader!.StateRoot = FromFirstByte(0x88); // The in-place header write ProcessSlot performs.
        clone.CurrentEpochParticipation![1] |= 0b010;
        clone.Slashings![0] += Gwei;

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

    private static void AssertRootsMatch(CachedBeaconStateHasher hasher, BeaconStateFulu state, string stage) =>
        Assert.That(hasher.HashTreeRoot(state), Is.EqualTo(SszRoots.HashTreeRoot(state)), stage);

    internal static BeaconStateFulu CreateState(int validatorCount)
    {
        Validator[] validators = new Validator[validatorCount];
        ulong[] balances = new ulong[validatorCount];
        byte[] previousParticipation = new byte[validatorCount];
        byte[] currentParticipation = new byte[validatorCount];
        ulong[] inactivityScores = new ulong[validatorCount];
        for (int i = 0; i < validatorCount; i++)
        {
            validators[i] = GloasTestFixtures.CreateActiveValidator(Pubkey(i));
            balances[i] = 32 * Gwei + (ulong)i;
            previousParticipation[i] = (byte)(i % 8);
            currentParticipation[i] = (byte)(i % 4);
            inactivityScores[i] = (ulong)(i % 5);
        }

        Hash256[] blockRoots = new Hash256[(int)Presets.SlotsPerHistoricalRoot];
        Hash256[] stateRoots = new Hash256[(int)Presets.SlotsPerHistoricalRoot];
        for (int i = 0; i < blockRoots.Length; i++)
        {
            blockRoots[i] = FromFirstByte(0x0B);
            stateRoots[i] = FromFirstByte(0x0C);
        }
        Hash256[] randaoMixes = Enumerable.Repeat(FromFirstByte(0x42), (int)Presets.EpochsPerHistoricalVector).ToArray();

        BlsPublicKey[] committee = new BlsPublicKey[512];
        for (int i = 0; i < committee.Length; i++)
        {
            committee[i] = validators[i % validatorCount].Pubkey;
        }
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
