// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.ForkChoice;

/// <summary>The fast confirmation rule from consensus-specs v1.7.0-beta.3, phase0/fast-confirmation.md.</summary>
/// <remarks>Owned by the fork-choice import thread. Confirmations assume slot synchrony and at most 25 percent Byzantine stake.</remarks>
internal sealed class FastConfirmation(
    BeaconChainSpec spec,
    ForkChoiceStore store,
    ProtoArrayForkChoice tree,
    IReadOnlySet<ulong> equivocators,
    Func<CheckpointRef, ForkedBeaconState> checkpointState,
    Func<Hash256, ForkedBeaconState> blockState,
    Func<Hash256, ulong, ForkedBeaconState> pulledUpHeadState)
{
    private BeaconChainSpec Spec => spec;
    private ForkChoiceStore Store => store;
    private ProtoArrayForkChoice Tree => tree;
    private IReadOnlySet<ulong> Equivocators => equivocators;
    private BalanceSource? _pulledUpHeadBalances;
    private (Hash256 Root, ulong Epoch)? _pulledUpHeadKey;
    private const ulong ByzantineThreshold = 25;
    private CheckpointRef _previousObserved = store.FinalizedCheckpoint;
    private CheckpointRef _currentObserved = store.FinalizedCheckpoint;
    private CheckpointRef _greatestUnrealized = store.FinalizedCheckpoint;
    private Hash256 _previousHead = store.FinalizedCheckpoint.Root;
    private Hash256 _currentHead = store.FinalizedCheckpoint.Root;
    private BalanceSource? _previousBalances;
    private BalanceSource? _currentBalances;
    private readonly Dictionary<(ulong Epoch, Hash256 Decision), CommitteeCache> _committees = [];

    internal Hash256 ConfirmedRoot { get; private set; } = store.FinalizedCheckpoint.Root;
    internal CheckpointRef PreviousEpochObservedJustifiedCheckpoint => _previousObserved;
    internal CheckpointRef CurrentEpochObservedJustifiedCheckpoint => _currentObserved;
    internal CheckpointRef PreviousEpochGreatestUnrealizedCheckpoint => _greatestUnrealized;
    internal Hash256 PreviousSlotHead => _previousHead;
    internal Hash256 CurrentSlotHead => _currentHead;

    internal void Reset()
    {
        ConfirmedRoot = store.FinalizedCheckpoint.Root;
        _previousObserved = _currentObserved = _greatestUnrealized = store.FinalizedCheckpoint;
        _previousHead = _currentHead = ConfirmedRoot;
        _previousBalances = _currentBalances = null;
        _pulledUpHeadBalances = null;
        _pulledUpHeadKey = null;
        _committees.Clear();
    }

    internal void OnSlot(Hash256 head)
    {
        _previousHead = _currentHead;
        _currentHead = head;
        foreach ((ulong Epoch, Hash256 Decision) key in _committees.Keys)
            if (key.Epoch + 2 < store.CurrentEpoch) _committees.Remove(key);
        if ((store.CurrentSlot + 1) % spec.SlotsPerEpoch == 0)
            _greatestUnrealized = store.UnrealizedJustifiedCheckpoint;
        if (IsEpochStart)
        {
            _previousObserved = _currentObserved;
            _previousBalances = _currentBalances;
            _currentObserved = _greatestUnrealized;
            _currentBalances = null;
        }
        Evaluate(head);
    }

    internal void Evaluate(Hash256 head)
    {
        // A finalized root may have replaced a pruned confirmation or observed checkpoint.
        if (!tree.ContainsBlock(ConfirmedRoot) || !tree.ContainsBlock(_currentObserved.Root) || !tree.ContainsBlock(_previousHead))
        {
            Reset();
            return;
        }
        Context context = new(this, head);
        Hash256 confirmed = ConfirmedRoot;
        if (Epoch(confirmed) + 1 < store.CurrentEpoch || !tree.IsDescendant(confirmed, head)
            || (IsEpochStart && !IsConfirmedChainSafe(context, confirmed)))
            confirmed = store.FinalizedCheckpoint.Root;

        if (IsEpochStart && Epoch(_currentObserved.Root) + 1 == store.CurrentEpoch
            && _currentObserved == Node(head).UnrealizedJustifiedCheckpoint && Slot(confirmed) < Slot(_currentObserved.Root))
            confirmed = _currentObserved.Root;

        if (Epoch(confirmed) + 1 >= store.CurrentEpoch)
            confirmed = FindLatestConfirmedDescendant(context, confirmed);
        ConfirmedRoot = confirmed;
    }

    private Hash256 FindLatestConfirmedDescendant(Context context, Hash256 confirmed)
    {
        ulong epoch = store.CurrentEpoch;
        if (Epoch(confirmed) + 1 == epoch && VotingSource(_previousHead).Epoch + 2 >= epoch
            && (IsEpochStart || (context.NoConflictingCheckpoint &&
                (Node(_previousHead).UnrealizedJustifiedCheckpoint!.Value.Epoch + 1 >= epoch
                    || Node(context.Head).UnrealizedJustifiedCheckpoint!.Value.Epoch + 1 >= epoch))))
        {
            foreach (Hash256 root in Ancestors(context.Head, confirmed))
            {
                if (Epoch(root) == epoch || !tree.IsDescendant(root, _previousHead) || !IsOneConfirmed(context, CurrentBalances, root))
                    break;
                confirmed = root;
            }
        }
        if (IsEpochStart || Node(context.Head).UnrealizedJustifiedCheckpoint!.Value.Epoch + 1 >= epoch)
        {
            Hash256 tentative = confirmed;
            foreach (Hash256 root in Ancestors(context.Head, confirmed))
            {
                if ((Epoch(root) > Epoch(tentative) && !context.CurrentTargetWillBeJustified)
                    || !IsOneConfirmed(context, CurrentBalances, root))
                    break;
                tentative = root;
            }
            if (Epoch(tentative) == epoch || (VotingSource(tentative).Epoch + 2 >= epoch
                && (IsEpochStart || context.NoConflictingCheckpoint)))
                confirmed = tentative;
        }
        return confirmed;
    }

    private bool IsConfirmedChainSafe(Context context, Hash256 confirmed)
    {
        if (Checkpoint(confirmed, _currentObserved.Epoch) != _currentObserved)
            return false;
        Hash256 start = _currentObserved.Root;
        if (_currentObserved.Epoch + 1 < store.CurrentEpoch)
        {
            Hash256 ancestor = tree.GetAncestor(confirmed, (store.CurrentEpoch - 1) * spec.SlotsPerEpoch)!;
            start = Epoch(ancestor) + 1 == store.CurrentEpoch ? Parent(ancestor).Root : ancestor;
        }
        foreach (Hash256 root in Ancestors(confirmed, start))
        {
            _previousBalances ??= new BalanceSource(checkpointState(_previousObserved), spec);
            if (!IsOneConfirmed(context, _previousBalances, root))
                return false;
        }
        return true;
    }

    private bool IsOneConfirmed(Context context, BalanceSource source, Hash256 root)
    {
        if (Node(root).ExecutionStatus != ExecutionStatus.Valid)
            return false;
        ProtoNode parent = Parent(root);
        ulong slot = Slot(root);
        ulong end = store.CurrentSlot - 1;
        ulong maximum = EstimateCommitteeWeight(source.Total, parent.Slot + 1, end, spec.SlotsPerEpoch);
        ulong proposerScore = source.Total / spec.SlotsPerEpoch * ProtoArrayForkChoice.DefaultProposerScoreBoostPercent / 100;
        ulong discount = 0;
        if (parent.Slot + 1 < slot)
        {
            ForkChoicePayloadStatus status = Node(root).IsGloas ? Node(root).ParentPayloadStatus : ForkChoicePayloadStatus.Pending;
            ulong support = context.SupportBetween(source, parent.Root, status, parent.Slot + 1, slot - 1);
            discount = SaturatingSubtract(support, context.AdversarialWeight(source, parent.Slot + 1, slot - 1));
        }
        ulong start = Epoch(root) > parent.Slot / spec.SlotsPerEpoch ? Epoch(root) * spec.SlotsPerEpoch : slot;
        UInt128 threshold = (UInt128)maximum + proposerScore + 2 * (UInt128)context.AdversarialWeight(source, start, end);
        threshold = threshold > discount ? (threshold - discount) / 2 : 0;
        return context.Score(source, root) > threshold;
    }

    private BalanceSource CurrentBalances => _currentBalances ??= new BalanceSource(checkpointState(_currentObserved), spec);
    private BalanceSource HeadBalances(Hash256 head)
    {
        if (_pulledUpHeadKey != (head, store.CurrentEpoch))
        {
            _pulledUpHeadBalances = new BalanceSource(pulledUpHeadState(head, store.CurrentEpoch), spec);
            _pulledUpHeadKey = (head, store.CurrentEpoch);
        }
        return _pulledUpHeadBalances!;
    }

    private CommitteeCache Committees(Hash256 head, ulong epoch)
    {
        ulong decisionSlot = epoch <= Presets.MinSeedLookahead ? 0 : (epoch - Presets.MinSeedLookahead) * spec.SlotsPerEpoch - 1;
        Hash256 decision = tree.GetAncestor(head, decisionSlot) ?? store.FinalizedCheckpoint.Root;
        if (!_committees.TryGetValue((epoch, decision), out CommitteeCache? committees))
        {
            committees = blockState(head) switch
            {
                ForkedBeaconState.OfFulu fulu => CommitteeCache.Build(fulu.State, epoch),
                ForkedBeaconState.OfGloas gloas => CommitteeCache.Build(gloas.State, epoch),
                _ => throw new NotSupportedException(),
            };
            if (_committees.Count >= 6) _committees.Clear();
            _committees[(epoch, decision)] = committees;
        }
        return committees;
    }
    private bool IsEpochStart => store.CurrentSlot % spec.SlotsPerEpoch == 0;
    private ulong Slot(Hash256 root) => Node(root).Slot;
    private ulong Epoch(Hash256 root) => Slot(root) / spec.SlotsPerEpoch;
    private ProtoNode Node(Hash256 root) => tree.Nodes[tree.IndexOf(root) ?? throw new ForkChoiceException($"Unknown fast confirmation block {root}")];
    private ProtoNode Parent(Hash256 root) => Node(root).Parent is int index ? tree.Nodes[index]
        : throw new ForkChoiceException($"Fast confirmation block {root} has no retained parent");
    private CheckpointRef VotingSource(Hash256 root) => Epoch(root) < store.CurrentEpoch
        ? Node(root).UnrealizedJustifiedCheckpoint!.Value : Node(root).JustifiedCheckpoint;
    private CheckpointRef Checkpoint(Hash256 root, ulong epoch) => new(epoch,
        tree.GetAncestor(root, epoch * spec.SlotsPerEpoch) ?? throw new ForkChoiceException($"No retained checkpoint for {root} at epoch {epoch}"));

    private List<Hash256> Ancestors(Hash256 root, Hash256 terminal)
    {
        List<Hash256> roots = [];
        foreach (ProtoNode node in tree.EnumerateAncestorNodes(root))
        {
            if (node.Root == terminal)
            {
                roots.Reverse();
                return roots;
            }
            if (node.Slot <= Slot(terminal)) break;
            roots.Add(node.Root);
        }
        return [];
    }

    internal static ulong AdjustCommitteeWeight(ulong estimate) => checked(((estimate + 999) / 1000) * 1005);

    internal static ulong EstimateCommitteeWeight(ulong total, ulong start, ulong end, ulong slotsPerEpoch)
    {
        if (start > end) return 0;
        if ((start + slotsPerEpoch - 1) / slotsPerEpoch < (end + 1) / slotsPerEpoch) return total;
        ulong committeeWeight = total / slotsPerEpoch;
        if (start / slotsPerEpoch == end / slotsPerEpoch) return committeeWeight * (end - start + 1);
        ulong endSlots = end % slotsPerEpoch + 1;
        ulong startSlots = slotsPerEpoch - start % slotsPerEpoch;
        return AdjustCommitteeWeight(committeeWeight * startSlots / slotsPerEpoch * (slotsPerEpoch - endSlots) + committeeWeight * endSlots);
    }

    private static ulong SaturatingSubtract(ulong value, ulong discount) => value > discount ? value - discount : 0;

    private sealed class BalanceSource
    {
        internal readonly ulong[] Balances;
        internal readonly bool[] Slashed;
        internal readonly ulong Total;

        internal BalanceSource(ForkedBeaconState state, BeaconChainSpec spec)
        {
            Validator[] validators = state switch
            {
                ForkedBeaconState.OfFulu fulu => fulu.State.Validators!,
                ForkedBeaconState.OfGloas gloas => gloas.State.Validators!,
                _ => throw new NotSupportedException(),
            };
            ulong epoch = state.Slot / spec.SlotsPerEpoch;
            Balances = new ulong[validators.Length];
            Slashed = new bool[validators.Length];
            for (int i = 0; i < validators.Length; i++)
            {
                Validator validator = validators[i];
                Slashed[i] = validator.Slashed;
                if (validator.IsActiveValidator(epoch))
                {
                    Balances[i] = validator.EffectiveBalance;
                    Total = checked(Total + validator.EffectiveBalance);
                }
            }
            Total = Math.Max(Total, Presets.EffectiveBalanceIncrement);
        }
    }

    private sealed class Context(FastConfirmation rule, Hash256 head)
    {
        internal readonly Hash256 Head = head;
        private readonly Dictionary<BalanceSource, ulong[]> _scores = [];
        private readonly Dictionary<(ulong Start, ulong End), HashSet<int>> _participants = [];
        private readonly Dictionary<ulong, int[]> _duties = [];
        private BalanceSource? _headBalances;
        private ulong? _honestFfgSupport;
        private BalanceSource HeadBalances => _headBalances ??= rule.HeadBalances(Head);
        internal bool NoConflictingCheckpoint => rule.Checkpoint(Head, rule.Store.CurrentEpoch) == rule.Store.UnrealizedJustifiedCheckpoint
            || 3 * (UInt128)HonestFfgSupport > HeadBalances.Total;
        internal bool CurrentTargetWillBeJustified => 3 * (UInt128)HonestFfgSupport >= 2 * (UInt128)HeadBalances.Total;

        private ulong HonestFfgSupport
        {
            get
            {
                if (_honestFfgSupport is ulong cached) return cached;
                BalanceSource source = HeadBalances;
                CheckpointRef target = rule.Checkpoint(Head, rule.Store.CurrentEpoch);
                ulong support = 0;
                for (int i = 0; i < source.Balances.Length; i++)
                {
                    if (!source.Slashed[i] && source.Balances[i] != 0 && !rule.Equivocators.Contains((ulong)i)
                        && rule.Tree.LatestVote((ulong)i) is VoteTracker vote && rule.Tree.ContainsBlock(vote.NextRoot)
                        && rule.Checkpoint(vote.NextRoot, vote.NextEpoch) == target)
                        support += source.Balances[i];
                }
                ulong start = rule.Store.CurrentEpoch * rule.Spec.SlotsPerEpoch;
                ulong end = rule.Store.CurrentSlot - 1;
                ulong remaining = source.Total - EstimateCommitteeWeight(source.Total, start, end, rule.Spec.SlotsPerEpoch);
                ulong honest = SaturatingSubtract(support, AdversarialWeight(source, start, end)) + remaining / 100 * (100 - ByzantineThreshold);
                _honestFfgSupport = honest;
                return honest;
            }
        }

        internal ulong Score(BalanceSource source, Hash256 root)
        {
            if (!_scores.TryGetValue(source, out ulong[]? scores))
            {
                scores = new ulong[rule.Tree.Nodes.Count];
                for (int i = 0; i < source.Balances.Length; i++)
                {
                    if (!source.Slashed[i] && source.Balances[i] != 0 && !rule.Equivocators.Contains((ulong)i)
                        && rule.Tree.LatestVote((ulong)i) is VoteTracker vote && rule.Tree.IndexOf(vote.NextRoot) is int index)
                        scores[index] += source.Balances[i];
                }
                for (int i = scores.Length - 1; i >= 0; i--)
                    if (rule.Tree.Nodes[i].Parent is int parent) scores[parent] += scores[i];
                _scores[source] = scores;
            }
            return scores[rule.Tree.IndexOf(root)!.Value];
        }

        internal ulong SupportBetween(BalanceSource source, Hash256 root, ForkChoicePayloadStatus status, ulong start, ulong end)
        {
            ulong support = 0;
            foreach (int i in Participants(start, end))
            {
                if (i >= source.Balances.Length) continue;
                if (!source.Slashed[i] && source.Balances[i] != 0 && !rule.Equivocators.Contains((ulong)i)
                    && rule.Tree.LatestVote((ulong)i) is VoteTracker vote && vote.NextRoot == root && vote.NextStatus == status)
                    support += source.Balances[i];
            }
            return support;
        }

        internal ulong AdversarialWeight(BalanceSource source, ulong start, ulong end)
        {
            ulong equivocation = 0;
            if (start <= end && rule.Equivocators.Count != 0)
            {
                foreach (ulong index in rule.Equivocators)
                {
                    if (index >= (ulong)source.Balances.Length || source.Balances[index] == 0) continue;
                    for (ulong epoch = start / rule.Spec.SlotsPerEpoch; epoch <= end / rule.Spec.SlotsPerEpoch; epoch++)
                    {
                        int[] duties = Duties(epoch);
                        if (index >= (ulong)duties.Length || duties[index] < 0) continue;
                        ulong slot = epoch * rule.Spec.SlotsPerEpoch + (ulong)duties[index];
                        if (slot >= start && slot <= end)
                        {
                            equivocation += source.Balances[index];
                            break;
                        }
                    }
                }
            }
            return SaturatingSubtract(EstimateCommitteeWeight(source.Total, start, end, rule.Spec.SlotsPerEpoch) / 100 * ByzantineThreshold, equivocation);
        }

        private int[] Duties(ulong epoch)
        {
            if (_duties.TryGetValue(epoch, out int[]? cached)) return cached;
            CommitteeCache committees = rule.Committees(Head, epoch);
            int length = 0;
            foreach (int index in committees.ShuffledIndices) length = Math.Max(length, index + 1);
            int[] duties = new int[length];
            Array.Fill(duties, -1);
            ulong firstSlot = epoch * rule.Spec.SlotsPerEpoch;
            for (ulong offset = 0; offset < rule.Spec.SlotsPerEpoch; offset++)
                for (int committee = 0; committee < committees.CommitteesPerSlot; committee++)
                    foreach (int index in committees.GetBeaconCommittee(firstSlot + offset, committee)) duties[index] = (int)offset;
            _duties[epoch] = duties;
            return duties;
        }

        private HashSet<int> Participants(ulong start, ulong end)
        {
            if (_participants.TryGetValue((start, end), out HashSet<int>? cached)) return cached;
            HashSet<int> participants = [];
            if (start <= end)
            {
                for (ulong slot = start; slot <= end; slot++)
                {
                    ulong epoch = slot / rule.Spec.SlotsPerEpoch;
                    CommitteeCache committees = rule.Committees(Head, epoch);
                    for (int committee = 0; committee < committees.CommitteesPerSlot; committee++)
                        foreach (int index in committees.GetBeaconCommittee(slot, committee)) participants.Add(index);
                }
            }
            _participants[(start, end)] = participants;
            return participants;
        }
    }
}
