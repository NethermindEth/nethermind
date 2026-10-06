// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Ethereum.ConsensusSpec.Test;

[TestFixture]
public class ForkDriverTests
{
    private static readonly string[] ProductionStateForks =
    [
        .. typeof(BeaconStateElectra).Assembly.GetTypes()
            .Where(static type => type is { IsClass: true, IsAbstract: false, Namespace: "Nethermind.BeaconChain.Types" })
            .Select(static type => Regex.Match(type.Name, "^BeaconState(?<fork>[A-Z][a-z]+)$"))
            .Where(static match => match.Success)
            .Select(static match => match.Groups["fork"].Value.ToLowerInvariant())
            .Order(StringComparer.Ordinal),
    ];

    [Test]
    public void Every_production_state_fork_is_extracted_and_has_a_driver()
    {
        Assert.That(ProductionStateForks, Is.Not.Empty, "fixture bug: no BeaconState container found");
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(ForkDriver.ByName.Keys.Order(StringComparer.Ordinal), Is.EqualTo(ProductionStateForks),
            "a production state container without a driver leaves its fork's vectors unrun; a driver without one cannot decode");
        Assert.That(ConsensusSpecArchive.StateTransitionForks.Order(StringComparer.Ordinal), Is.EqualTo(ProductionStateForks),
            "a fork not extracted enumerates no vectors, and zero vectors run green");
    }

    [Test]
    public void Every_extracted_fork_upgrade_fork_has_an_upgrade_and_vice_versa() =>
        Assert.That(ForkTests.UpgradesByFork.Keys.OrderBy(k => k, System.StringComparer.Ordinal),
            Is.EqualTo(ConsensusSpecArchive.ForkUpgradeForks.OrderBy(k => k, System.StringComparer.Ordinal)),
            "a fork extracted without an upgrade enumerates vectors nothing can run; an upgrade for an unextracted fork runs nothing");

    [Test]
    public void The_Electra_root_of_a_Fulu_working_state_ignores_the_lookahead_and_equals_a_plain_Electra_state()
    {
        BeaconStateElectra electra = MerkleizableElectra();
        electra.Slot = 12_345;
        electra.Eth1DepositIndex = 7;
        BeaconStateFulu working = new();
        ForkDriver.CopyElectraFields(electra, working);
        working.ProposerLookahead = Enumerable.Range(1, (int)(2 * Presets.SlotsPerEpoch)).Select(i => (ulong)i).ToArray();

        Hash256 electraRoot = ForkDriver.ElectraRoot(electra);
        Hash256 fuluRoot = FuluPipeline("fulu").StateRoot(working);
        Hash256 electraRootOfWorking = FuluPipeline("electra").StateRoot(working);

        Assert.Multiple(() =>
        {
            Assert.That(electraRootOfWorking, Is.EqualTo(electraRoot), "the Electra shape must not see proposer_lookahead");
            Assert.That(fuluRoot, Is.Not.EqualTo(electraRoot), "the Fulu shape does see it, so a Fulu-shaped root would fail every Electra vector");
            Assert.That(ForkDriver.ElectraRoot((BeaconStateElectra)FuluPipeline("electra").ForDiff(working)), Is.EqualTo(electraRoot),
                "the diff view must be the same Electra state the root was taken over");
        });
    }

    /// <summary>Every one of the Electra container's 37 fields carries a value no other field has, so a dropped or crossed field fails.</summary>
    [Test]
    public void Copying_Electra_fields_transfers_every_declared_field()
    {
        PropertyInfo[] fields = typeof(BeaconStateElectra).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        BeaconStateElectra source = new();
        for (int i = 0; i < fields.Length; i++)
            fields[i].SetValue(source, DistinctValue(fields[i].PropertyType, i));
        BeaconStateFulu target = new();

        ForkDriver.CopyElectraFields(source, target);

        Assert.That(fields, Has.Length.EqualTo(37), "the Electra BeaconState has 37 fields");
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        foreach (PropertyInfo field in fields)
        {
            object? expected = field.GetValue(source);
            Assert.That(field.GetValue(target), field.PropertyType.IsValueType ? Is.EqualTo(expected) : Is.SameAs(expected), field.Name);
        }

        Assert.That(target.ProposerLookahead, Is.Null, "the Fulu-only field is left for RefillProposerLookahead");
    }

    // All validators exit at epoch 2: Fulu's lookahead samples that empty epoch, which Electra must not read.
    [Test]
    public void Electra_epoch_processing_does_not_sample_proposers_two_epochs_ahead()
    {
        ulong[] balances = [.. Enumerable.Repeat(32 * Gwei, 8)];
        BeaconStateFulu state = WorkingStateAtEndOfEpochZero(effectiveBalances: [.. balances], balances, exitEpoch: 2);

        Assert.That(() => FuluPipeline("electra").ProcessSlots(state, Presets.SlotsPerEpoch, FuluPipeline("electra").NewCache()), Throws.Nothing);
        Assert.That(state.ProposerLookahead![(int)Presets.SlotsPerEpoch..], Is.All.EqualTo(ForkDriver.NoProposer), "epoch 2 has no active validator");
    }

    // Balance changes invalidate advance sampling; Electra must refill epoch-1 proposers after the boundary.
    [Test]
    public void Electra_lookahead_is_resampled_after_effective_balances_change_at_the_epoch_boundary()
    {
        ulong[] balances = [.. Enumerable.Range(0, 8).Select(static i => i % 2 == 0 ? 32 * Gwei : 1 * Gwei)];
        BeaconStateFulu state = WorkingStateAtEndOfEpochZero(effectiveBalances: [.. Enumerable.Repeat(32 * Gwei, 8)], balances, exitEpoch: Presets.FarFutureEpoch);
        ulong[] sampledDuringEpochZero = state.ProposerLookahead![(int)Presets.SlotsPerEpoch..];

        FuluPipeline("electra").ProcessSlots(state, Presets.SlotsPerEpoch, FuluPipeline("electra").NewCache());

        ulong[] electraProposers = state.ComputeProposerIndices(1);
        Assert.That(electraProposers, Is.Not.EqualTo(sampledDuringEpochZero), "fixture bug: the balance change must move a proposer");
        Assert.That(state.ProposerLookahead![..(int)Presets.SlotsPerEpoch], Is.EqualTo(electraProposers));
    }

    // The withdrawals vectors whose every validator has fully exited decode into a state with no
    // active validator; Electra accepts them because nothing in process_withdrawals reads a proposer.
    [Test]
    public void Refilling_the_lookahead_over_an_empty_active_set_marks_every_slot_as_having_no_proposer()
    {
        BeaconStateFulu working = new()
        {
            Slot = 3 * Presets.SlotsPerEpoch,
            Validators = [new Validator { EffectiveBalance = 32_000_000_000, ActivationEpoch = 0, ExitEpoch = 1, WithdrawableEpoch = 2 }],
        };

        ForkDriver.RefillProposerLookahead(working);

        Assert.That(working.ProposerLookahead, Has.Length.EqualTo(2 * Presets.SlotsPerEpoch).And.All.EqualTo(ForkDriver.NoProposer),
            "a slot without a proposer must not name validator 0, which an operation reading it would then accept");
    }

    private const ulong Gwei = 1_000_000_000;

    private static ForkDriver<BeaconStateFulu> FuluPipeline(string fork) => (ForkDriver<BeaconStateFulu>)ForkDriver.ByName[fork];

    private static object DistinctValue(Type type, int index) => type switch
    {
        _ when type == typeof(ulong) => (ulong)index + 1,
        _ when type == typeof(Hash256) => new Hash256([.. Enumerable.Repeat((byte)(index + 1), 32)]),
        _ when type == typeof(BitArray) => new BitArray(index + 1),
        { IsArray: true } => Array.CreateInstance(type.GetElementType()!, index + 1),
        _ => Activator.CreateInstance(type)!,
    };


    private static BeaconStateFulu WorkingStateAtEndOfEpochZero(ulong[] effectiveBalances, ulong[] balances, ulong exitEpoch)
    {
        int count = effectiveBalances.Length;
        Validator[] validators = new Validator[count];
        for (int i = 0; i < count; i++)
        {
            validators[i] = new Validator
            {
                Pubkey = new BlsPublicKey([.. Enumerable.Repeat((byte)(i + 1), BlsPublicKey.Length)]),
                WithdrawalCredentials = Hash256.Zero,
                EffectiveBalance = effectiveBalances[i],
                ExitEpoch = exitEpoch,
                WithdrawableEpoch = exitEpoch == Presets.FarFutureEpoch ? Presets.FarFutureEpoch : exitEpoch + 256,
            };
        }

        BeaconStateFulu state = new()
        {
            GenesisValidatorsRoot = Hash256.Zero,
            Slot = Presets.SlotsPerEpoch - 1,
            Fork = new Fork { PreviousVersion = new byte[4], CurrentVersion = new byte[4], Epoch = 0 },
            LatestBlockHeader = new BeaconBlockHeader { ParentRoot = Hash256.Zero, StateRoot = Hash256.Zero, BodyRoot = Hash256.Zero },
            BlockRoots = [.. Enumerable.Repeat(Hash256.Zero, (int)Presets.SlotsPerHistoricalRoot)],
            StateRoots = [.. Enumerable.Repeat(Hash256.Zero, (int)Presets.SlotsPerHistoricalRoot)],
            HistoricalRoots = [],
            Eth1Data = new Eth1Data { DepositRoot = Hash256.Zero, BlockHash = Hash256.Zero },
            Eth1DataVotes = [],
            Validators = validators,
            Balances = balances,
            RandaoMixes = [.. Enumerable.Repeat(Hash256.Zero, (int)Presets.EpochsPerHistoricalVector)],
            Slashings = new ulong[Presets.EpochsPerSlashingsVector],
            PreviousEpochParticipation = new byte[count],
            CurrentEpochParticipation = new byte[count],
            JustificationBits = new BitArray(4),
            PreviousJustifiedCheckpoint = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
            CurrentJustifiedCheckpoint = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
            FinalizedCheckpoint = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
            InactivityScores = new ulong[count],
            LatestExecutionPayloadHeader = new ExecutionPayloadHeader(),
            HistoricalSummaries = [],
            PendingDeposits = [],
            PendingPartialWithdrawals = [],
            PendingConsolidations = [],
        };
        ForkDriver.RefillProposerLookahead(state);
        return state;
    }

    /// <summary>Null lists and fixed-size fields merkleize as zero, but the generated merkleizer rejects a null variable-size container.</summary>
    private static BeaconStateElectra MerkleizableElectra() => new() { LatestExecutionPayloadHeader = new ExecutionPayloadHeader() };

    /// <summary>A vector whose incremental hasher misses a change would still pass on the full root alone, so the differential must fail it.</summary>
    [Test]
    public void The_differential_hasher_fails_when_the_incremental_root_misses_an_in_place_change()
    {
        ulong[] balances = [.. Enumerable.Repeat(32 * Gwei, 8)];
        BeaconStateFulu state = WorkingStateAtEndOfEpochZero(effectiveBalances: [.. balances], balances, exitEpoch: Presets.FarFutureEpoch);
        DifferentialBeaconStateHasher hasher = new(new FirstRootHasher(), new FullBeaconStateHasher());
        Hash256 first = hasher.HashTreeRoot(state);

        state.Balances![3] += Gwei;

        Assert.That(new FullBeaconStateHasher().HashTreeRoot(state), Is.Not.EqualTo(first), "fixture bug: the change must move the root");
        Assert.That(() => hasher.HashTreeRoot(state), Throws.TypeOf<HasherDivergenceException>());
    }

    [Test]
    public void The_differential_hasher_returns_the_full_root_when_the_cached_hasher_follows_in_place_changes()
    {
        ulong[] balances = [.. Enumerable.Repeat(32 * Gwei, 8)];
        BeaconStateFulu state = WorkingStateAtEndOfEpochZero(effectiveBalances: [.. balances], balances, exitEpoch: Presets.FarFutureEpoch);
        DifferentialBeaconStateHasher hasher = new();
        Hash256 before = hasher.HashTreeRoot(state);

        state.Balances![3] += Gwei;
        state.Slot++;

        Hash256 after = hasher.HashTreeRoot(state);
        Assert.That(after, Is.Not.EqualTo(before));
        Assert.That(after, Is.EqualTo(new FullBeaconStateHasher().HashTreeRoot(state)));
    }

    private sealed class FirstRootHasher : IBeaconStateHasher
    {
        private Hash256? _first;

        public Hash256 HashTreeRoot(BeaconStateFulu state) => _first ??= new FullBeaconStateHasher().HashTreeRoot(state);
    }

    // Vectors omit INVALID_BLOCK_HASH/ACCEPTED; Engine API treats them as invalid/unverified respectively.
    [TestCase("VALID", ExecutionStatus.Valid)]
    [TestCase("SYNCING", ExecutionStatus.Optimistic)]
    [TestCase("ACCEPTED", ExecutionStatus.Optimistic)]
    [TestCase("INVALID", ExecutionStatus.Invalid)]
    [TestCase("INVALID_BLOCK_HASH", ExecutionStatus.Invalid)]
    public void Payload_info_status_maps_to_the_execution_status_fork_choice_records(string status, ExecutionStatus expected) =>
        Assert.That(new ForkChoiceStepDriver.PayloadInfo(status, null).ExecutionStatus, Is.EqualTo(expected));

    [Test]
    public void Payload_info_with_an_unknown_status_is_refused_rather_than_guessed() =>
        Assert.That(() => new ForkChoiceStepDriver.PayloadInfo("VALIDISH", null).ExecutionStatus, Throws.TypeOf<InvalidDataException>());
}
