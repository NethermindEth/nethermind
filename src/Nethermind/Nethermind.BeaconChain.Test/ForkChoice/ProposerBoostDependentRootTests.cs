// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Test.StateTransition;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Test.ForkChoice;

[HardTimeout(60_000)]
public class ProposerBoostDependentRootTests
{
    private const ulong BoostSlot = 2 * Presets.SlotsPerEpoch;
    private const ulong DependentSlot = Presets.SlotsPerEpoch - 1;

    [Test]
    public void Block_with_another_dependent_root_is_not_boosted_and_does_not_take_the_head()
    {
        FuluChain chain = new();
        ForkChoiceRunner runner = chain.CreateRunner();
        FuluChain.Block head = chain.ImportAt(runner, chain.AnchorRoot, DependentSlot, 0x31);
        FuluChain.Block block = chain.ExtendOrdered(chain.AnchorRoot, BoostSlot, head.Root, above: false);

        TickTo(runner, BoostSlot);
        chain.Import(runner, block);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(runner.ProposerBoostRoot, Is.EqualTo(Hash256.Zero));
        Assert.That(runner.GetHead(), Is.EqualTo(head.Root));
    }

    [Test]
    public void Block_with_the_heads_dependent_root_is_boosted_and_takes_the_head()
    {
        FuluChain chain = new();
        ForkChoiceRunner runner = chain.CreateRunner();
        FuluChain.Block fork = chain.ImportAt(runner, chain.AnchorRoot, DependentSlot, 0x31);
        FuluChain.Block head = chain.ImportAt(runner, fork.Root, DependentSlot + 1, 0x40);
        Assert.That(runner.GetHead(), Is.EqualTo(head.Root), "fixture bug: the longer branch must be the head");
        FuluChain.Block block = chain.Extend(fork.Root, BoostSlot, 0x64);

        TickTo(runner, BoostSlot);
        chain.Import(runner, block);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(runner.ProposerBoostRoot, Is.EqualTo(block.Root));
        Assert.That(runner.GetHead(), Is.EqualTo(block.Root));
    }

    [Test]
    public void Dependent_root_is_compared_with_the_head_before_the_block()
    {
        FuluChain chain = new();
        ForkChoiceRunner runner = chain.CreateRunner();
        FuluChain.Block head = chain.ImportAt(runner, chain.AnchorRoot, DependentSlot, 0x31);
        FuluChain.Block block = chain.ExtendOrdered(chain.AnchorRoot, BoostSlot, head.Root, above: true);

        TickTo(runner, BoostSlot);
        chain.Import(runner, block);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(runner.ProposerBoostRoot, Is.EqualTo(Hash256.Zero));
        Assert.That(runner.GetHead(), Is.EqualTo(block.Root), "fixture bug: the head after the block must be the block");
    }

    [Test]
    public void Known_block_imported_again_is_not_boosted()
    {
        FuluChain chain = new();
        ForkChoiceRunner runner = chain.CreateRunner();
        FuluChain.Block head = chain.ImportAt(runner, chain.AnchorRoot, DependentSlot, 0x31);
        FuluChain.Block block = chain.ExtendOrdered(chain.AnchorRoot, BoostSlot, head.Root, above: true);
        TickTo(runner, BoostSlot);
        chain.Import(runner, block);
        Assert.That(runner.GetHead(), Is.EqualTo(block.Root), "fixture bug: the unboosted block must be the head");

        chain.Import(runner, block);

        Assert.That(runner.ProposerBoostRoot, Is.EqualTo(Hash256.Zero));
    }

    [Test]
    public void Block_in_the_first_two_epochs_compares_dependent_roots_at_the_anchor([Values(2ul, Presets.SlotsPerEpoch + 8)] ulong slot)
    {
        FuluChain chain = new();
        ForkChoiceRunner runner = chain.CreateRunner();
        FuluChain.Block head = chain.ImportAt(runner, chain.AnchorRoot, 1, 0x01);
        FuluChain.Block block = chain.Extend(chain.AnchorRoot, slot, 0x02);

        TickTo(runner, slot);
        Assert.That(runner.GetHead(), Is.EqualTo(head.Root), "fixture bug: the slot-1 block must be the head");
        chain.Import(runner, block);

        Assert.That(runner.ProposerBoostRoot, Is.EqualTo(block.Root));
    }

    [Test]
    public void Known_fulu_block_imported_again_skips_the_parent_checks()
    {
        FuluChain chain = new();
        ForkChoiceRunner runner = chain.CreateRunner();
        FuluChain.Block parent = chain.ImportAt(runner, chain.AnchorRoot, 1, 0x01, ExecutionStatus.Optimistic);
        FuluChain.Block block = chain.ImportAt(runner, parent.Root, 2, 0x02, ExecutionStatus.Optimistic);
        runner.OnInvalidExecutionPayload(parent.Root);

        Assert.DoesNotThrow(() => chain.Import(runner, block, ExecutionStatus.Optimistic));
    }

    [Test]
    public void Block_with_an_invalid_justified_block_is_refused_only_when_timely([Values] bool timely)
    {
        FuluChain chain = new();
        ForkChoiceRunner runner = chain.CreateRunner();
        FuluChain.Block justified = chain.ImportAt(runner, chain.AnchorRoot, 1, 0x01, ExecutionStatus.Optimistic);
        FuluChain.Block child = chain.Extend(justified.Root, Presets.SlotsPerEpoch + 1, 0x02);
        BeaconStateFulu doctored = child.PostState.Clone();
        doctored.CurrentJustifiedCheckpoint = new Checkpoint { Epoch = 1, Root = justified.Root };
        TickTo(runner, Presets.SlotsPerEpoch + 1);
        chain.Import(runner, child with { PostState = doctored }, ExecutionStatus.Optimistic);
        Assert.That(runner.JustifiedCheckpoint.Root, Is.EqualTo(justified.Root), "fixture bug: the child must justify its parent");
        runner.OnInvalidExecutionPayload(justified.Root);
        FuluChain.Block block = chain.Extend(chain.AnchorRoot, Presets.SlotsPerEpoch + 2, 0x03);
        TickTo(runner, Presets.SlotsPerEpoch + 2);
        if (!timely)
            runner.OnTick(runner.GenesisTime + (Presets.SlotsPerEpoch + 3) * Presets.SecondsPerSlot - 1);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        if (timely)
            Assert.That(() => chain.Import(runner, block), Throws.TypeOf<ForkChoiceException>());
        else
            Assert.That(() => chain.Import(runner, block), Throws.Nothing);
        Assert.That(runner.ContainsBlock(block.Root), Is.EqualTo(!timely));
        Assert.That(runner.ProposerBoostRoot, Is.EqualTo(Hash256.Zero));
    }

    [Test]
    public void Known_gloas_block_imported_again_skips_the_parent_checks()
    {
        const ulong GloasBoostSlot = 3 * Presets.SlotsPerEpoch;
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = chain.CreateRunner();
        TickTo(runner, chain.First.Block.Message!.Slot);
        runner.OnBlock(chain.First.Block, chain.First.PostState);
        (SignedBeaconBlockGloas block, _, BeaconStateGloas postState) = GloasBlock(chain.First.PostState.Clone(), GloasBoostSlot);
        TickTo(runner, GloasBoostSlot);
        runner.OnBlock(block, postState);
        runner.OnInvalidExecutionPayload(chain.First.Root);

        Assert.DoesNotThrow(() => runner.OnBlock(block, postState));
    }

    [Test]
    public void Only_the_first_timely_block_of_the_slot_is_boosted()
    {
        FuluChain chain = new();
        ForkChoiceRunner runner = chain.CreateRunner();
        FuluChain.Block head = chain.ImportAt(runner, chain.AnchorRoot, DependentSlot, 0x31);
        FuluChain.Block first = chain.Extend(head.Root, BoostSlot, 0x64);
        FuluChain.Block second = chain.Extend(head.Root, BoostSlot, 0x65);

        TickTo(runner, BoostSlot);
        chain.Import(runner, first);
        chain.Import(runner, second);

        Assert.That(runner.ProposerBoostRoot, Is.EqualTo(first.Root));
    }

    [Test]
    public void Gloas_block_is_boosted_only_with_the_heads_dependent_root([Values] bool onHeadBranch)
    {
        const ulong GloasBoostSlot = 3 * Presets.SlotsPerEpoch;
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = chain.CreateRunner();
        TickTo(runner, chain.First.Block.Message!.Slot);
        runner.OnBlock(chain.First.Block, chain.First.PostState);
        ForkCrossingChain.ChainBlock head = chain.Voting[0];
        TickTo(runner, head.Block.Message!.Slot);
        runner.OnBlock(head.Block, head.PostState);
        Assert.That(runner.GetHead(), Is.EqualTo(head.Root), "fixture bug: the epoch-2 block must be the head");

        (SignedBeaconBlockGloas block, Hash256 root, BeaconStateGloas postState) = onHeadBranch
            ? GloasBlock(chain.First.PostState.Clone(), GloasBoostSlot)
            : GloasBlock(chain.UpgradedAnchor(), GloasBoostSlot);
        TickTo(runner, GloasBoostSlot);
        runner.OnBlock(block, postState);

        Assert.That(runner.ProposerBoostRoot, Is.EqualTo(onHeadBranch ? root : Hash256.Zero));
    }

    private static (SignedBeaconBlockGloas Block, Hash256 Root, BeaconStateGloas PostState) GloasBlock(BeaconStateGloas state, ulong slot)
    {
        EpochCache cache = new();
        GloasSlotProcessing.ProcessSlots(state, slot, cache);
        SignedBeaconBlockGloas block = GloasTestFixtures.MinimalBlock(state, GloasTestFixtures.SelfBuildBid(state, state.LatestBlockHash!, GloasTestFixtures.Hash(0x96)));
        GloasTestFixtures.ApplyBlock(state, block, cache);
        block.Message!.StateRoot = SszRoots.HashTreeRoot(state);
        return (block, SszRoots.HashTreeRoot(block.Message), state);
    }

    private static void TickTo(ForkChoiceRunner runner, ulong slot) =>
        runner.OnTick(runner.GenesisTime + slot * Presets.SecondsPerSlot);

    /// <summary>Read proposers from slot lookahead so this fixture can build past epoch 1.</summary>
    private sealed class FuluChain : IForkChoiceStateProvider
    {
        private static readonly BlsSignature Unsigned = new(SignatureSets.G2PointAtInfinity);

        private readonly ImportableBlobBlock _anchor = ImportableBlobBlock.CreateWithoutBlobs();
        private readonly Dictionary<Hash256, BeaconStateFulu> _states = [];
        private readonly Dictionary<(Hash256 Parent, ulong Slot), BeaconStateFulu> _advanced = [];

        public FuluChain() => _states[AnchorRoot] = _anchor.AnchorState;

        public sealed record Block(SignedBeaconBlock Signed, Hash256 Root, BeaconStateFulu PostState);

        public Hash256 AnchorRoot => _anchor.AnchorRoot;
        public ForkChoiceRunner CreateRunner() => new(_anchor.Spec, _anchor.AnchorState, _anchor.AnchorBlock.Message!, this, _anchor.Pubkeys);
        public BeaconStateFulu? GetBlockState(Hash256 blockRoot) => _states.GetValueOrDefault(blockRoot);
        public BeaconStateFulu? CopyBlockState(Hash256 blockRoot) => GetBlockState(blockRoot)?.Clone();

        public void Import(ForkChoiceRunner runner, Block block, ExecutionStatus executionStatus = ExecutionStatus.Valid) =>
            runner.OnBlock(block.Signed, block.PostState, executionStatus, (IReadOnlyList<DataColumnSidecar>?)null);

        public Block ImportAt(ForkChoiceRunner runner, Hash256 parentRoot, ulong slot, byte payloadHashByte, ExecutionStatus executionStatus = ExecutionStatus.Valid)
        {
            Block block = Extend(parentRoot, slot, payloadHashByte);
            TickTo(runner, slot);
            Import(runner, block, executionStatus);
            return block;
        }

        public Block ExtendOrdered(Hash256 parentRoot, ulong slot, Hash256 other, bool above)
        {
            for (int payloadHashByte = 1; payloadHashByte <= byte.MaxValue; payloadHashByte++)
            {
                Block block = Extend(parentRoot, slot, (byte)payloadHashByte);
                if (block.Root.CompareTo(other) > 0 == above)
                    return block;
            }

            throw new InvalidOperationException("Fixture bug: no payload hash byte orders the root as required");
        }

        /// <summary>Distinct payload hash bytes keep otherwise identical block roots distinct.</summary>
        public Block Extend(Hash256 parentRoot, ulong slot, byte payloadHashByte)
        {
            BeaconStateFulu parentState = _states[parentRoot];
            if (!_advanced.TryGetValue((parentRoot, slot), out BeaconStateFulu? advanced))
            {
                advanced = parentState.Clone();
                SlotProcessing.ProcessSlots(advanced, slot, new EpochCache());
                _advanced[(parentRoot, slot)] = advanced;
            }

            BeaconBlock block = TestChain.CreateBlock(slot, parentRoot).Message!;
            block.ProposerIndex = advanced.GetBeaconProposerIndex();
            BeaconBlockBody body = block.Body!;
            body.RandaoReveal = Unsigned;
            body.Attestations = [];
            body.AttesterSlashings = [];
            ExecutionPayload payload = body.ExecutionPayload!;
            payload.ParentHash = parentState.LatestExecutionPayloadHeader!.BlockHash;
            payload.PrevRandao = advanced.GetRandaoMix(advanced.GetCurrentEpoch());
            payload.Timestamp = parentState.GenesisTime + slot * Presets.SecondsPerSlot;
            payload.BlockNumber = parentState.LatestExecutionPayloadHeader.BlockNumber + 1;
            payload.BlockHash = GloasTestFixtures.Hash(payloadHashByte);

            SignedBeaconBlock signed = new() { Message = block, Signature = Unsigned };
            BeaconStateFulu postState = advanced.Clone();
            BlockProcessing.ProcessBlock(postState, block, new EpochCache(), _anchor.Pubkeys, new GloasTestFixtures.AcceptingNotifier(), _anchor.Spec.MaxBlobsPerBlockElectra, verifySignatures: false);
            block.StateRoot = SszRoots.HashTreeRoot(postState);
            Hash256 root = SszRoots.HashTreeRoot(block);
            _states[root] = postState;
            return new Block(signed, root, postState);
        }
    }
}
