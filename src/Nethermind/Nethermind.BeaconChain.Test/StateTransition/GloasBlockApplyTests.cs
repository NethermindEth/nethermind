// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.StateTransition;

[HardTimeout(60_000)]
public class GloasBlockApplyTests
{
    // The caller's lineage cache must expose branch-conflicting balance memos; a fresh cache would hide them.
    [Test]
    public void Slot_advance_runs_epoch_processing_through_the_callers_cache([Values] bool cacheHoldsAnotherBranch)
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        ulong blockSlot = state.Slot + Presets.SlotsPerEpoch;
        BeaconStateGloas atBlockSlot = state.Clone();
        GloasSlotProcessing.ProcessSlots(atBlockSlot, blockSlot, new EpochCache());
        SignedBeaconBlockGloas block = MinimalBlock(atBlockSlot, SelfBuildBid(atBlockSlot, atBlockSlot.LatestBlockHash!, Hash(0x9A)));

        EpochCache cache = new();
        if (cacheHoldsAnotherBranch)
        {
            BeaconStateGloas sibling = state.Clone();
            sibling.BlockRoots![(int)((state.Slot - 1) % Presets.SlotsPerHistoricalRoot)] = Hash(0xAB);
            cache.GetTotalActiveBalance(sibling);
        }

        Action apply = () => ForkedStateTransition.Apply(
            new ForkedBeaconState.OfGloas(state), new ForkedSignedBeaconBlock.OfGloas(block), cache, new PubkeyCache(), new AcceptingNotifier(),
            UpgradeEpochSpec(), validateResult: false, verifySignatures: false);

        Assert.That(apply, cacheHoldsAnotherBranch
            ? Throws.TypeOf<BeaconStateException>().With.Message.Contains("reused across branches")
            : Throws.Nothing);
    }

    [TestCase(-1, false, null, TestName = "last_validator_with_a_cached_key_verifies")]
    [TestCase(0, false, "is not a validator index", TestName = "index_past_the_registry_is_refused")]
    [TestCase(-1, true, "has no cached public key", TestName = "index_past_the_pubkey_cache_is_refused")]
    public void Proposer_signature_check_bounds_the_proposer_index(int indexFromRegistryEnd, bool cacheLacksLastValidator, string? refusal)
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        PubkeyCache pubkeys = InstallRealValidatorKeys(state);
        Validator[] validators = state.Validators!;
        if (cacheLacksLastValidator)
        {
            pubkeys = new PubkeyCache();
            pubkeys.Build(validators[..^1]);
        }

        int proposerIndex = validators.Length + indexFromRegistryEnd;
        SignedBeaconBlockGloas block = MinimalBlock(state, SelfBuildBid(state, state.LatestBlockHash!, Hash(0x9B)));
        block.Message!.ProposerIndex = (ulong)proposerIndex;
        Hash256 domain = state.GetDomain(DomainType.BeaconProposer, BeaconStateAccessors.ComputeEpochAtSlot(block.Message.Slot));
        block.Signature = Sign(ValidatorKey(proposerIndex), Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(block.Message), domain));

        if (refusal is null)
            Assert.That(GloasBlockProcessing.VerifyProposerSignature(state, block, pubkeys), Is.True);
        else
            Assert.That(() => GloasBlockProcessing.VerifyProposerSignature(state, block, pubkeys),
                Throws.TypeOf<BeaconStateException>().With.Message.Contains(refusal));
    }
}
