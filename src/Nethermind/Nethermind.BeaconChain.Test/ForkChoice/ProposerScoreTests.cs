// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Test.ForkChoice;

public class ProposerScoreTests
{
    [TestCase(AnchorChange.SlashFirst, 6_400_000_000ul, TestName = "slashed_validator_counts_towards_the_total")]
    [TestCase(AnchorChange.ExitFirst, 6_000_000_000ul, TestName = "exited_validator_is_not_in_the_total")]
    [TestCase(AnchorChange.ZeroBalances, 12_500_000ul, TestName = "zero_total_is_floored_at_one_increment")]
    public void Boosted_block_weight_is_the_proposer_score(AnchorChange change, ulong expectedScore)
    {
        UnsignedChain chain = UnsignedChain.Create();
        Assert.That(chain.Anchor.AnchorState.Validators!, Has.Length.EqualTo(16), "fixture bug: the registry must be 16 validators");
        ForkChoiceRunner runner = new(chain.Spec, chain.Anchor.AnchorState, chain.Anchor.AnchorBlock.Message!, new JustifiedAnchorOverride(chain, change), chain.Anchor.Pubkeys);
        UnsignedChain.ChainBlock block = chain.Extend(chain.AnchorRoot, slot: 1, payloadHashByte: 0xa1);
        runner.OnTick(runner.GenesisTime + Presets.SecondsPerSlot);
        runner.OnBlock(block.Block, block.PostState, ExecutionStatus.Valid, (IReadOnlyList<DataColumnSidecar>?)null);
        Assert.That(runner.ProposerBoostRoot, Is.EqualTo(block.Root), "fixture bug: the block must hold the boost");

        runner.GetHead();

        Assert.That(runner.Snapshot().Nodes.Single(n => n.Root == block.Root).Weight, Is.EqualTo(expectedScore));
    }

    [Test]
    public void Slashed_voters_add_no_weight()
    {
        UnsignedChain chain = UnsignedChain.Create();
        ForkChoiceRunner runner = new(chain.Spec, chain.Anchor.AnchorState, chain.Anchor.AnchorBlock.Message!, new JustifiedAnchorOverride(chain, AnchorChange.SlashAll), chain.Anchor.Pubkeys);
        UnsignedChain.ChainBlock block = chain.Extend(chain.AnchorRoot, slot: 1, payloadHashByte: 0xa1);
        runner.OnTick(runner.GenesisTime + Presets.SecondsPerSlot);
        runner.OnBlock(block.Block, block.PostState, ExecutionStatus.Valid, (IReadOnlyList<DataColumnSidecar>?)null);
        runner.OnTick(runner.GenesisTime + 2 * Presets.SecondsPerSlot);
        runner.OnAttestation(chain.Vote(1, block.Root), verifySignature: false);
        Assert.That(runner.ProposerBoostRoot, Is.EqualTo(Hash256.Zero), "fixture bug: the boost must be reset");

        runner.GetHead();

        Assert.That(runner.Snapshot().Nodes.Single(n => n.Root == block.Root).Weight, Is.Zero);
    }

    public enum AnchorChange { SlashFirst, ExitFirst, ZeroBalances, SlashAll }

    private sealed class JustifiedAnchorOverride(UnsignedChain chain, AnchorChange change) : IForkChoiceStateProvider
    {
        private readonly BeaconStateFulu _anchor = Override(chain.Anchor.AnchorState, change);

        public BeaconStateFulu? GetBlockState(Hash256 blockRoot) => blockRoot == chain.AnchorRoot ? _anchor : chain.GetBlockState(blockRoot);

        public BeaconStateFulu? CopyBlockState(Hash256 blockRoot) => GetBlockState(blockRoot)?.Clone();

        private static BeaconStateFulu Override(BeaconStateFulu anchor, AnchorChange change)
        {
            BeaconStateFulu state = anchor.Clone();
            Validator[] validators = state.Validators!;
            for (int i = 0; i < validators.Length; i++)
            {
                if ((change is AnchorChange.SlashFirst or AnchorChange.ExitFirst) && i != 0) continue;
                Validator validator = validators[i].Clone();
                switch (change)
                {
                    case AnchorChange.SlashFirst or AnchorChange.SlashAll: validator.Slashed = true; break;
                    case AnchorChange.ExitFirst: validator.ExitEpoch = 0; break;
                    default: validator.EffectiveBalance = 0; break;
                }
                validators[i] = validator;
            }

            return state;
        }
    }
}
