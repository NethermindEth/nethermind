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

/// <summary>
/// The Gloas arm of <see cref="ForkedStateTransition.Apply"/> around <see cref="GloasBlockProcessing.ProcessBlock"/>:
/// the slot advance through the caller's <see cref="EpochCache"/>, and the proposer signature check on an
/// untrusted proposer index.
/// </summary>
public class GloasBlockApplyTests
{
    /// <summary>
    /// The epoch processing a Gloas block's slot advance runs must use the caller's per-lineage cache: that cache
    /// carries the balance memo and shufflings across slots, and it is what refuses a memo built on another branch.
    /// A throwaway cache would hide such a lineage bug and redo the work on every block.
    /// </summary>
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

    /// <summary>
    /// <c>verify_block_signature</c> reads <c>state.validators[proposer_index]</c>, and the p2p <c>beacon_block</c> rule
    /// rejects an index outside the registry before the signature. An index the registry or the pubkey cache lacks
    /// must be refused as an invalid block, not escape as an out-of-range fault that stops the import worker.
    /// </summary>
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
