// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Test.ForkChoice;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.Crypto;

/// <summary>The Fulu proposer signature check on an untrusted proposer index.</summary>
public class ProposerSignatureBoundsTests
{
    /// <summary>
    /// <c>verify_block_signature</c> reads <c>state.validators[proposer_index]</c>, and the p2p <c>beacon_block</c> rule
    /// rejects an index outside the registry before the signature. An index the registry or the pubkey cache lacks
    /// must be refused as an invalid block, not escape as an out-of-range fault that stops the import worker.
    /// </summary>
    [TestCase(-1, false, null, TestName = "last_validator_with_a_cached_key_verifies")]
    [TestCase(0, false, "is not a validator index", TestName = "index_past_the_registry_is_refused")]
    [TestCase(-1, true, "has no cached public key", TestName = "index_past_the_pubkey_cache_is_refused")]
    public void Fulu_proposer_signature_check_bounds_the_proposer_index(int indexFromRegistryEnd, bool cacheLacksLastValidator, string? refusal)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        BeaconStateFulu state = chain.AnchorState;
        Validator[] validators = state.Validators!;
        PubkeyCache pubkeys = new();
        pubkeys.Build(cacheLacksLastValidator ? validators[..^1] : validators);

        int proposerIndex = validators.Length + indexFromRegistryEnd;
        BeaconBlock block = TestChain.CreateBlock(1, chain.AnchorRoot).Message!;
        block.ProposerIndex = (ulong)proposerIndex;
        Hash256 domain = state.GetDomain(DomainType.BeaconProposer, 0);
        BlsSignature signature = Sign(ValidatorKey(proposerIndex), Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(block), domain));

        if (refusal is null)
            Assert.That(SignatureSets.VerifyProposerSignature(state, block, signature, pubkeys), Is.True);
        else
            Assert.That(() => SignatureSets.VerifyProposerSignature(state, block, signature, pubkeys),
                Throws.TypeOf<BeaconStateException>().With.Message.Contains(refusal));
    }
}
