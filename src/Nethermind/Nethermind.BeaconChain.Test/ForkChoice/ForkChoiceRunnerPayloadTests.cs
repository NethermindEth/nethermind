// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.ForkChoice;

/// <summary>Children advance copied state without process_block: fork choice reads only checkpoints and registry.</summary>
[HardTimeout(60_000)]
public class ForkChoiceRunnerPayloadTests
{
    private const ulong EffectiveBalance = 32 * Gwei;

    private const ulong CommitteeSize = 64;

    [Test]
    public void Block_building_on_a_full_gloas_parent_waits_for_that_parent_payload([Values] bool buildsOnFull, [Values] bool parentPayloadVerified)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ulong childSlot = BoundarySlot + 1;
        ForkChoiceRunner runner = chain.CreateRunner();
        TickToSlot(runner, childSlot);
        runner.OnBlock(chain.First.Block, chain.First.PostState);
        if (parentPayloadVerified)
            runner.OnExecutionPayloadVerified(chain.First.Root);

        Hash256 bidParentBlockHash = buildsOnFull ? BidOf(chain.First.Block).BlockHash! : chain.First.PostState.LatestBlockHash!;
        SignedBeaconBlockGloas child = ChildOf(chain.First.PostState, childSlot, bidParentBlockHash, out BeaconStateGloas childState);
        Hash256 childRoot = SszRoots.HashTreeRoot(child.Message!);
        Assert.That(runner.IsParentNodeFull(child.Message!), Is.EqualTo(buildsOnFull), "fixture bug");

        bool accepted = !buildsOnFull || parentPayloadVerified;
        Assert.That(() => runner.OnBlock(child, childState),
            accepted ? Throws.Nothing : Throws.TypeOf<ForkChoiceException>().With.Message.Contains("which is not verified"));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(runner.ContainsBlock(childRoot), Is.EqualTo(accepted));
        Assert.That(runner.ProposerBoostRoot, Is.EqualTo(accepted ? childRoot : Hash256.Zero), "the timely child takes the boost only if accepted");
        Assert.That(runner.GetParentBlockHash(childRoot), Is.EqualTo(accepted ? bidParentBlockHash : null));
    }

    [Test]
    public void First_gloas_block_building_on_the_fulu_payload_needs_no_envelope()
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = chain.CreateRunner();
        TickToSlot(runner, BoundarySlot);
        BeaconStateGloas state = chain.UpgradedAnchor();
        SignedBeaconBlockGloas block = MinimalBlock(state, SelfBuildBid(state, runner.GetExecutionBlockHash(chain.AnchorRoot)!, Hash(0xE2)));
        Assert.That(runner.IsParentNodeFull(block.Message!), Is.True, "fixture bug: the block must build on the anchor's payload");

        runner.OnBlock(block, state);

        Assert.That(runner.ContainsBlock(SszRoots.HashTreeRoot(block.Message!)), Is.True);
    }

    [Test]
    public void Payload_verification_is_recorded_only_for_known_gloas_blocks()
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = chain.CreateRunner();
        TickToSlot(runner, BoundarySlot + 1);
        runner.OnBlock(chain.First.Block, chain.First.PostState);
        Hash256 unknown = Hash(0xEE);
        bool firstVerifiedBefore = runner.IsPayloadVerified(chain.First.Root);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => runner.OnExecutionPayloadVerified(chain.AnchorRoot), Throws.TypeOf<ForkChoiceException>().With.Message.Contains("before the Gloas fork"));
            Assert.That(() => runner.OnExecutionPayloadVerified(unknown), Throws.TypeOf<ForkChoiceException>().With.Message.Contains("unknown"));
        }

        runner.OnExecutionPayloadVerified(chain.First.Root);
        Assert.That(() => runner.OnExecutionPayloadVerified(chain.First.Root), Throws.Nothing);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(firstVerifiedBefore, Is.False);
        Assert.That(runner.IsPayloadVerified(chain.First.Root), Is.True);
        Assert.That(runner.IsPayloadVerified(chain.AnchorRoot), Is.True);
        Assert.That(runner.IsPayloadVerified(unknown), Is.False);
        Assert.That(runner.GetParentBlockHash(chain.First.Root), Is.EqualTo(BidOf(chain.First.Block).ParentBlockHash));
        Assert.That(runner.GetParentBlockHash(chain.AnchorRoot), Is.Null);
    }

    [Test]
    public void Prune_drops_the_bid_parent_hash_of_every_pruned_block()
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        const ulong FinalizedEpoch = 9;
        ulong finalizedSlot = FinalizedEpoch * Presets.SlotsPerEpoch;
        ulong lastSlot = finalizedSlot + 1;
        ForkChoiceRunner runner = chain.CreateRunner();
        TickToSlot(runner, lastSlot);
        runner.OnBlock(chain.First.Block, chain.First.PostState);

        SignedBeaconBlockGloas template = ChildOf(chain.First.PostState, BoundarySlot + 1, chain.First.PostState.LatestBlockHash!, out _);
        Hash256 parentRoot = chain.First.Root;
        Hash256 finalizedRoot = Hash256.Zero;
        for (ulong slot = BoundarySlot + 1; slot <= lastSlot; slot++)
        {
            SignedBeaconBlockGloas block = WithSlotAndParent(template, slot, parentRoot);
            BeaconStateGloas postState = chain.First.PostState;
            if (slot == lastSlot)
            {
                postState = chain.First.PostState.Clone();
                postState.CurrentJustifiedCheckpoint = new Checkpoint { Epoch = FinalizedEpoch, Root = finalizedRoot };
                postState.FinalizedCheckpoint = new Checkpoint { Epoch = FinalizedEpoch, Root = finalizedRoot };
            }

            runner.OnBlock(block, postState);
            parentRoot = SszRoots.HashTreeRoot(block.Message!);
            if (slot == finalizedSlot)
                finalizedRoot = parentRoot;
        }

        Assert.That(runner.FinalizedCheckpoint, Is.EqualTo(new CheckpointRef(FinalizedEpoch, finalizedRoot)), "fixture bug");
        runner.Prune();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(runner.ContainsBlock(chain.First.Root), Is.False, "fixture bug: the proto-array must have pruned");
        Assert.That(runner.GetParentBlockHash(chain.First.Root), Is.Null);
        Assert.That(runner.GetParentBlockHash(parentRoot), Is.Not.Null, "the unpruned tip keeps its record");
        Assert.That(runner.GetPtcVotes(chain.First.Root), Is.Null);
        Assert.That(runner.GetPtcVotes(parentRoot), Is.Not.Null, "the unpruned tip keeps its PTC votes");
    }

    public readonly record struct PayloadVote(ulong Index, bool SameSlot, bool PayloadVerified, string? Refusal)
    {
        public override string ToString() => $"index {Index}, {(SameSlot ? "same" : "later")} slot, payload {(PayloadVerified ? "verified" : "unverified")}";
    }

    private static IEnumerable<PayloadVote> PayloadVotes() =>
    [
        new(Index: 0, SameSlot: true, PayloadVerified: false, Refusal: null),
        new(Index: 0, SameSlot: false, PayloadVerified: false, Refusal: null),
        new(Index: 1, SameSlot: false, PayloadVerified: true, Refusal: null),
        new(Index: 1, SameSlot: false, PayloadVerified: false, Refusal: "which is not verified"),
        new(Index: 1, SameSlot: true, PayloadVerified: true, Refusal: "from its own slot"),
        new(Index: 2, SameSlot: false, PayloadVerified: true, Refusal: "is not a payload status"),
    ];

    [Test]
    public void Vote_payload_status_index_is_validated(
        [ValueSource(nameof(PayloadVotes))] PayloadVote vote, [Values] bool isFromBlock, [Values] bool fuluContainer)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = chain.CreateRunner();
        TickToSlot(runner, BoundarySlot + 2);
        runner.OnBlock(chain.First.Block, chain.First.PostState);
        if (vote.PayloadVerified)
            runner.OnExecutionPayloadVerified(chain.First.Root);

        AttestationData data = new()
        {
            Slot = vote.SameSlot ? BoundarySlot : BoundarySlot + 1,
            Index = vote.Index,
            BeaconBlockRoot = chain.First.Root,
            Source = new Checkpoint { Epoch = 0, Root = chain.AnchorRoot },
            Target = new Checkpoint { Epoch = ForkCrossingChain.ForkEpoch, Root = chain.First.Root },
        };
        AttestationGloas attestation = CommitteeAttestation(
            chain.First.PostState, data, new EpochCache().GetCommitteeCache(chain.First.PostState, ForkCrossingChain.ForkEpoch), 0, sign: false);
        Action attest = fuluContainer
            ? () => runner.OnAttestation(ToFuluAttestation(attestation), isFromBlock, verifySignature: false)
            : () => runner.OnAttestation(attestation, isFromBlock, verifySignature: false);

        Assert.That(attest, vote.Refusal is null ? Throws.Nothing : Throws.TypeOf<ForkChoiceException>().With.Message.Contains(vote.Refusal));
        Assert.That(Weight(runner, chain.First.Root), Is.EqualTo(vote.Refusal is null ? CommitteeSize * EffectiveBalance : 0));
    }

    [Test]
    public void Index_one_vote_for_a_pre_gloas_block_is_refused([Values(0ul, 1ul)] ulong index, [Values] bool isFromBlock)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = chain.CreateRunner();
        TickToSlot(runner, BoundarySlot + 2);
        AttestationData data = new()
        {
            Slot = BoundarySlot + 1,
            Index = index,
            BeaconBlockRoot = chain.AnchorRoot,
            Source = new Checkpoint { Epoch = 0, Root = chain.AnchorRoot },
            Target = new Checkpoint { Epoch = ForkCrossingChain.ForkEpoch, Root = chain.AnchorRoot },
        };
        AttestationGloas attestation = CommitteeAttestation(
            chain.First.PostState, data, new EpochCache().GetCommitteeCache(chain.First.PostState, ForkCrossingChain.ForkEpoch), 0, sign: false);

        Assert.That(() => runner.OnAttestation(attestation, isFromBlock, verifySignature: false),
            index == 1 ? Throws.TypeOf<ForkChoiceException>().With.Message.Contains("which is not verified") : Throws.Nothing);
    }

    [Test]
    public void A_vote_derived_from_a_pre_gloas_head_is_accepted()
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = chain.CreateRunner();
        TickToSlot(runner, BoundarySlot + 1);
        ForkChoiceNode head = runner.GetHeadNode();
        Assert.That(head.Root, Is.EqualTo(chain.AnchorRoot), "fixture bug");

        AttestationData data = new()
        {
            Slot = BoundarySlot + 1,
            Index = head.PayloadStatus == ForkChoicePayloadStatus.Full ? 1ul : 0ul,
            BeaconBlockRoot = head.Root,
            Source = new Checkpoint { Epoch = 0, Root = chain.AnchorRoot },
            Target = new Checkpoint { Epoch = ForkCrossingChain.ForkEpoch, Root = chain.AnchorRoot },
        };
        AttestationGloas attestation = CommitteeAttestation(
            chain.First.PostState, data, new EpochCache().GetCommitteeCache(chain.First.PostState, ForkCrossingChain.ForkEpoch), 0, sign: false);

        Assert.That(() => runner.OnAttestation(attestation, isFromBlock: true, verifySignature: false), Throws.Nothing);
    }

    private static ExecutionPayloadBid BidOf(SignedBeaconBlockGloas block) => block.Message!.Body!.SignedExecutionPayloadBid!.Message!;

    private static SignedBeaconBlockGloas ChildOf(BeaconStateGloas parentPostState, ulong slot, Hash256 bidParentBlockHash, out BeaconStateGloas postState)
    {
        postState = parentPostState.Clone();
        GloasSlotProcessing.ProcessSlots(postState, slot, new EpochCache());
        return MinimalBlock(postState, SelfBuildBid(postState, bidParentBlockHash, Hash(0xE1)));
    }

    private static SignedBeaconBlockGloas WithSlotAndParent(SignedBeaconBlockGloas template, ulong slot, Hash256 parentRoot)
    {
        BeaconBlockGloas message = template.Message!;
        return new SignedBeaconBlockGloas
        {
            Message = new BeaconBlockGloas
            {
                Slot = slot,
                ProposerIndex = message.ProposerIndex,
                ParentRoot = parentRoot,
                StateRoot = message.StateRoot,
                Body = message.Body,
            },
            Signature = template.Signature,
        };
    }

    private static void TickToSlot(ForkChoiceRunner runner, ulong slot) =>
        runner.OnTick(runner.GenesisTime + slot * Presets.SecondsPerSlot);

    private static ulong Weight(ForkChoiceRunner runner, Hash256 root)
    {
        runner.GetHead();
        return runner.Snapshot().Nodes.Single(n => n.Root == root).Weight;
    }

    private abstract record PayloadStep;
    private sealed record Child(int Id, int Parent, ulong Offset, byte Salt, int? PayloadAncestor = null) : PayloadStep;
    private sealed record Import(int Id, bool MustNotThrow = false) : PayloadStep;
    private sealed record Verify(int Id) : PayloadStep;
    private sealed record Invalidate(int Id, int Payload, int? LatestValid = null, bool Refused = false, bool ZeroLatestValid = false) : PayloadStep;
    private sealed record Validate(int Id, int Payload, bool Refused = false) : PayloadStep;
    private sealed record Verified(int Id, bool Expected) : PayloadStep;
    private sealed record Status(int Id, ExecutionStatus Expected) : PayloadStep;
    private sealed record Head(int Id) : PayloadStep;
    private sealed record PayloadValid(int Id, bool Expected) : PayloadStep;
    private sealed record OptimisticPayloads(int[] Ids) : PayloadStep;
    private sealed record PayloadCase(string Name, PayloadStep[] Steps);
    private static readonly PayloadCase[] PayloadScenarios =
    [
        FullHeadInvalidation(false),
        FullHeadInvalidation(true),
        new("Latest_valid_hash_walk_skips_a_block_whose_payload_the_chain_bypassed",
            [new Child(1, 0, 1, 0xA1), new Import(1), new Verify(1), new Child(2, 1, 2, 0xB1, 0), new Import(2), new Verify(2), new Child(3, 2, 3, 0xE1, 0), new Import(3),
             new Invalidate(2, 2, 0), new Verified(2, false), new Verified(1, true), new Status(1, ExecutionStatus.Optimistic), new Status(2, ExecutionStatus.Optimistic), new Status(3, ExecutionStatus.Optimistic)]),
        NamedPayloadInvalidation(true),
        NamedPayloadInvalidation(false),
        new("Invalid_verdict_on_a_payload_called_valid_is_refused",
            [new Child(1, 0, 1, 0xB1), new Import(1), new Verify(1), new Validate(1, 1), new Invalidate(1, 1, Refused: true), new Verified(1, true)]),
        new("Valid_verdict_promotes_the_head_and_its_ancestors_only",
            [new Child(1, 0, 1, 0xA1), new Child(2, 0, 1, 0xD1), new Import(1), new Import(2), new Child(3, 1, 2, 0xE1, 0), new Import(3), new Validate(3, 0),
             new Status(3, ExecutionStatus.Valid), new Status(1, ExecutionStatus.Valid), new Status(0, ExecutionStatus.Valid), new Status(2, ExecutionStatus.Optimistic)]),
        new("Valid_verdict_on_an_invalid_payload_is_refused_without_promoting_its_block",
            [new Invalidate(0, 0), new Validate(0, 0, Refused: true), new Status(0, ExecutionStatus.Optimistic)]),
        new("Zero_latest_valid_hash_is_refused_when_it_would_invalidate_the_valid_anchor",
            [new Invalidate(0, 0, Refused: true, ZeroLatestValid: true), new Verified(0, true)]),
        SnapshotPayloadValidity(false),
        SnapshotPayloadValidity(true),
    ];

    private static PayloadCase FullHeadInvalidation(bool latestValidIsParentPayload) =>
        new($"Invalid_verdict_on_a_full_head_removes_only_its_payload({latestValidIsParentPayload})",
            [new Child(1, 0, 1, 0xB1), new Import(1), new Verify(1), new Child(2, 1, 2, 0xE1, 0), new Child(3, 1, 2, 0xF1), new Import(2), new Import(3), new Verify(3),
             new Child(4, 3, 3, 0xF2), new Import(4), new Child(5, 2, 3, 0xE2, 0), new Invalidate(1, 1, latestValidIsParentPayload ? 0 : null), new Verify(1), new Import(5, MustNotThrow: true),
             new Verified(1, false), new Status(1, ExecutionStatus.Optimistic), new Status(2, ExecutionStatus.Optimistic), new Status(3, ExecutionStatus.Invalid), new Status(4, ExecutionStatus.Invalid), new Head(5)]);

    private static PayloadCase NamedPayloadInvalidation(bool emptyHead)
    {
        List<PayloadStep> steps = [];
        int ancestor = 0;
        if (!emptyHead)
        {
            steps.AddRange([new Child(1, 0, 1, 0xA1), new Import(1), new Verify(1)]);
            ancestor = 1;
        }
        ulong offset = emptyHead ? 1UL : 2UL;
        steps.AddRange([new Child(2, ancestor, offset, 0xB1), new Import(2), new Verify(2), new Child(3, 2, offset + 1, 0xE1, ancestor), new Import(3),
            emptyHead ? new Invalidate(3, ancestor) : new Invalidate(2, 2, 0), new Verified(ancestor, false), new Status(ancestor, ExecutionStatus.Optimistic),
            new Status(2, ExecutionStatus.Invalid), new Status(3, ExecutionStatus.Invalid), new Head(ancestor)]);
        return new(emptyHead ? "Invalid verdict on an EMPTY head removes the ancestor payload its hash names" : "Invalid verdict with an older latest valid hash removes every payload after it", [.. steps]);
    }

    private static PayloadCase SnapshotPayloadValidity(bool ownVerdict) =>
        new($"Snapshot_reports_an_envelope_payload_valid_only_when_a_verdict_covers_it({ownVerdict})",
            [new Child(1, 0, 1, 0xA1), new Import(1), new Verify(1), new Child(2, 1, 2, 0xB1), new Import(2), new Verify(2), new Child(3, 2, 3, 0xE1, 1), new Import(3), new Verify(3),
             new OptimisticPayloads([0, 1, 2, 3]), new Validate(3, ownVerdict ? 3 : 1), new PayloadValid(3, ownVerdict), new Status(2, ExecutionStatus.Valid),
             new PayloadValid(2, false), new PayloadValid(1, true), new PayloadValid(0, true)]);

    private static IEnumerable<TestCaseData> PayloadCases()
    {
        for (int i = 0; i < PayloadScenarios.Length; i++) yield return new TestCaseData(i).SetName(PayloadScenarios[i].Name);
    }

    /// <summary>An invalid FULL payload leaves EMPTY viable; execution verdicts follow payload ancestry (optimistic-sync.md).</summary>
    [TestCaseSource(nameof(PayloadCases))]
    public void Execution_verdict_preserves_payload_branches_and_ancestor_validity(int index)
    {
        GloasForkChoiceHarness harness = new();
        Dictionary<int, GloasForkChoiceHarness.Block> blocks = new() { [0] = ImportVerifiedFirst(harness) };
        bool IsPayloadValid(int id) => harness.Runner.Snapshot().Nodes.Single(n => n.Root == blocks[id].Root).PayloadValid;
        foreach (PayloadStep step in PayloadScenarios[index].Steps)
        {
            switch (step)
            {
                case Child child:
                    GloasForkChoiceHarness.Block parent = blocks[child.Parent];
                    blocks[child.Id] = child.PayloadAncestor is { } ancestor
                        ? harness.Child(parent, blocks[0].Slot + child.Offset, blocks[ancestor].BidBlockHash, child.Salt)
                        : harness.Child(parent, blocks[0].Slot + child.Offset, full: true, child.Salt);
                    break;
                case Import import:
                    if (import.MustNotThrow) Assert.That(() => harness.Import(blocks[import.Id]), Throws.Nothing);
                    else harness.Import(blocks[import.Id]);
                    break;
                case Verify verify:
                    harness.Runner.OnExecutionPayloadVerified(blocks[verify.Id].Root);
                    break;
                case Invalidate invalid:
                    void InvalidatePayload() => harness.Runner.InvalidateExecutionChain(blocks[invalid.Id].Root, blocks[invalid.Payload].BidBlockHash,
                        invalid.ZeroLatestValid ? Hash256.Zero : invalid.LatestValid is { } latest ? blocks[latest].BidBlockHash : null);
                    if (invalid.Refused) Assert.That(InvalidatePayload, Throws.TypeOf<ProtoArrayException>());
                    else InvalidatePayload();
                    break;
                case Validate valid:
                    void ValidatePayload() => harness.Runner.ValidateExecutionChain(blocks[valid.Id].Root, blocks[valid.Payload].BidBlockHash);
                    if (valid.Refused) Assert.That(ValidatePayload, Throws.TypeOf<ProtoArrayException>());
                    else ValidatePayload();
                    break;
                case Verified verified:
                    Assert.That(harness.Runner.IsPayloadVerified(blocks[verified.Id].Root), Is.EqualTo(verified.Expected));
                    break;
                case Status status:
                    Assert.That(harness.Runner.GetBlockExecutionStatus(blocks[status.Id].Root), Is.EqualTo(status.Expected));
                    break;
                case Head head:
                    Assert.That(harness.Runner.GetHeadNode(), Is.EqualTo(new ForkChoiceNode(blocks[head.Id].Root, ForkChoicePayloadStatus.Empty)));
                    break;
                case PayloadValid payload:
                    Assert.That(IsPayloadValid(payload.Id), Is.EqualTo(payload.Expected));
                    break;
                case OptimisticPayloads optimistic:
                    Assert.That(optimistic.Ids.Select(IsPayloadValid), Is.All.False);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(step));
            }
        }
    }
    [Test]
    public void Valid_verdict_stops_at_a_pre_gloas_carrier([Values] bool throughEmptyHead)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = chain.CreateRunner();
        Hash256 headRoot = chain.AnchorRoot;
        Hash256 payloadHash = runner.GetExecutionBlockHash(chain.AnchorRoot)!;
        if (throughEmptyHead)
        {
            TickToSlot(runner, BoundarySlot);
            BeaconStateGloas state = chain.UpgradedAnchor();
            SignedBeaconBlockGloas block = MinimalBlock(state, SelfBuildBid(state, payloadHash, Hash(0xE2)));
            runner.OnBlock(block, state);
            headRoot = SszRoots.HashTreeRoot(block.Message!);
        }

        ProtoNode carrier = runner.EnumerateAncestors(chain.AnchorRoot).First();
        Assert.That(carrier.ExecutionStatus, Is.EqualTo(ExecutionStatus.Valid));
        // An unreadable parent detects traversal past a validated carrier without a chain-depth timing assertion.
        carrier.Parent = int.MaxValue;

        Hash256? payloadRoot = runner.ValidateExecutionChainAndGetPayloadRoot(headRoot, payloadHash);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(payloadRoot, Is.Null, "a pre-Gloas payload needs no envelope verdict and must end the carrier search");
        Assert.That(runner.GetBlockExecutionStatus(headRoot), Is.EqualTo(ExecutionStatus.Valid));
    }

    private static GloasForkChoiceHarness.Block ImportVerifiedFirst(GloasForkChoiceHarness harness)
    {
        GloasForkChoiceHarness.Block first = harness.First;
        harness.TickTo(first.Slot + 3);
        harness.Import(first);
        harness.Runner.OnExecutionPayloadVerified(first.Root);
        return first;
    }
}
