// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>
/// Pins the two assumptions the Electra carry-through rests on that no spec vector states directly:
/// that the archive extracts exactly the forks a driver exists for, and that merkleizing the Fulu
/// working state as its Electra base yields the Electra root, lookahead and all excluded.
/// </summary>
[TestFixture]
public class ForkDriverTests
{
    [Test]
    public void Every_extracted_state_transition_fork_has_a_driver_and_vice_versa() =>
        Assert.That(ForkDriver.ByName.Keys.OrderBy(k => k, System.StringComparer.Ordinal),
            Is.EqualTo(ConsensusSpecArchive.StateTransitionForks.OrderBy(k => k, System.StringComparer.Ordinal)),
            "a fork extracted without a driver enumerates vectors nothing can run; a driver for an unextracted fork runs nothing");

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
        working.ProposerLookahead = Enumerable.Range(1, 64).Select(i => (ulong)i).ToArray();

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

    [Test]
    public void Copying_Electra_fields_transfers_every_declared_field()
    {
        BeaconStateElectra source = MerkleizableElectra();
        source.GenesisTime = 1;
        source.Slot = 2;
        source.Eth1DepositIndex = 3;
        source.NextWithdrawalIndex = 4;
        source.DepositBalanceToConsume = 5;
        source.Validators = [new Validator { EffectiveBalance = 32_000_000_000 }];
        source.Balances = [32_000_000_000];
        BeaconStateFulu target = new();

        ForkDriver.CopyElectraFields(source, target);

        Assert.Multiple(() =>
        {
            Assert.That(ForkDriver.ElectraRoot(target), Is.EqualTo(ForkDriver.ElectraRoot(source)));
            Assert.That(target.Validators, Is.SameAs(source.Validators), "fields are shared by reference, not deep-copied");
            Assert.That(target.ProposerLookahead, Is.Null, "the Fulu-only field is left for RefillProposerLookahead");
        });
    }

    // The withdrawals vectors whose every validator has fully exited decode into a state with no
    // active validator; Electra accepts them because nothing in process_withdrawals reads a proposer.
    [Test]
    public void Refilling_the_lookahead_over_an_empty_active_set_marks_every_slot_as_having_no_proposer()
    {
        BeaconStateFulu working = new()
        {
            Slot = 3 * 32,
            Validators = [new Validator { EffectiveBalance = 32_000_000_000, ActivationEpoch = 0, ExitEpoch = 1, WithdrawableEpoch = 2 }],
        };

        ForkDriver.RefillProposerLookahead(working);

        Assert.That(working.ProposerLookahead, Has.Length.EqualTo(64).And.All.EqualTo(ForkDriver.NoProposer),
            "a slot without a proposer must not name validator 0, which an operation reading it would then accept");
    }

    private static ForkDriver<BeaconStateFulu> FuluPipeline(string fork) => (ForkDriver<BeaconStateFulu>)ForkDriver.ByName[fork];

    /// <summary>Null lists and fixed-size fields merkleize as zero, but the generated merkleizer rejects a null variable-size container.</summary>
    private static BeaconStateElectra MerkleizableElectra() => new() { LatestExecutionPayloadHeader = new ExecutionPayloadHeader() };
}
