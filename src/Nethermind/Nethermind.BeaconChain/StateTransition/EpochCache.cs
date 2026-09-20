// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>
/// Caller-owned per-epoch caches used by the state transition: the memoized total active balance
/// and an LRU of committee shufflings.
/// </summary>
/// <remarks>
/// Not thread-safe: own one instance per state lineage (e.g. per block-processing context). The
/// balance memo enforces this at runtime (see <see cref="GetTotalActiveBalance(BeaconStateFulu)"/>):
/// a second, conflicting branch at the same epoch is refused rather than silently handed the first
/// branch's balance. The two caches are keyed by different roots because they depend on the chain
/// through different delays. The committee LRU keys on the shuffling decision root (the last block
/// of <c>epoch - MIN_SEED_LOOKAHEAD - 1</c>): a shuffling is a function of the RANDAO mix that block
/// finalizes and of an active set fixed <c>MAX_SEED_LOOKAHEAD</c> epochs ahead, so two branches
/// sharing that root really do share the shuffling. The balance memo keys on the epoch boundary
/// root (the last block before <c>epoch</c>'s first slot) because effective balances are recomputed
/// at every boundary from balances the epoch's own blocks moved: two branches that diverged inside
/// the previous epoch share the decision root yet can disagree on the total active balance, which
/// the decision-root key used to hand across silently. Same-slot siblings within one epoch share
/// both keys and both values legitimately; a separate instance per candidate is still the only
/// protection for the stateful <see cref="Hasher"/>.
/// </remarks>
public sealed class EpochCache
{
    private (ulong Epoch, ulong Balance, Hash256 BoundaryRoot)? _totalActiveBalance;
    private readonly CommitteeCacheLru _committees = new();

    /// <summary>
    /// The state hash-tree-root implementation used by slot processing and the post-state root
    /// check in <see cref="StateTransition.Apply"/>.
    /// </summary>
    /// <remarks>
    /// Defaults to the stateless <see cref="FullBeaconStateHasher"/>; install a
    /// <see cref="CachedBeaconStateHasher"/> for incremental per-slot roots. A stateful hasher
    /// follows one state lineage, matching this cache's ownership rules.
    /// </remarks>
    public IBeaconStateHasher Hasher { get; set; } = new FullBeaconStateHasher();

    /// <summary>
    /// Returns <c>get_total_active_balance(state)</c> — the total effective balance of validators
    /// active in the current epoch, floored at <c>EFFECTIVE_BALANCE_INCREMENT</c> — memoized per epoch.
    /// </summary>
    /// <exception cref="BeaconStateException">
    /// This cache already holds a balance for this epoch computed against a different branch (a
    /// different epoch boundary root, see the type remarks). Reusing an <see cref="EpochCache"/>
    /// across two candidate branches at the same epoch would otherwise silently hand one branch the
    /// other's balance; use a separate instance per branch instead.
    /// </exception>
    public ulong GetTotalActiveBalance(BeaconStateFulu state)
    {
        ulong epoch = state.GetCurrentEpoch();
        Hash256 boundaryRoot = GetEpochBoundaryRoot(state.Slot, s => state.GetBlockRootAtSlot(s));
        if (_totalActiveBalance is not { } cached || cached.Epoch != epoch)
        {
            ulong balance = state.GetTotalBalance(state.GetActiveValidatorIndices(epoch));
            _totalActiveBalance = cached = (epoch, balance, boundaryRoot);
        }
        else if (cached.BoundaryRoot != boundaryRoot)
        {
            throw Conflict(epoch, cached.BoundaryRoot, boundaryRoot);
        }
        return cached.Balance;
    }

    /// <summary>
    /// <see cref="GetTotalActiveBalance(BeaconStateFulu)"/> for a post-fork <see cref="BeaconStateGloas"/>.
    /// Shares this cache's single memo slot: an instance is owned per state lineage (see the type
    /// remarks), and a lineage that has crossed the Gloas fork never calls the Fulu overload again.
    /// </summary>
    /// <exception cref="BeaconStateException">See <see cref="GetTotalActiveBalance(BeaconStateFulu)"/>.</exception>
    public ulong GetTotalActiveBalance(BeaconStateGloas state)
    {
        ulong epoch = state.GetCurrentEpoch();
        Hash256 boundaryRoot = GetEpochBoundaryRoot(state.Slot, s => state.GetBlockRootAtSlot(s));
        if (_totalActiveBalance is not { } cached || cached.Epoch != epoch)
        {
            ulong balance = state.GetTotalBalance(state.GetActiveValidatorIndices(epoch));
            _totalActiveBalance = cached = (epoch, balance, boundaryRoot);
        }
        else if (cached.BoundaryRoot != boundaryRoot)
        {
            throw Conflict(epoch, cached.BoundaryRoot, boundaryRoot);
        }
        return cached.Balance;
    }

    /// <summary>
    /// Invalidates the total active balance memo. Call after mutations that change effective
    /// balances or the active set within the memoized epoch (e.g. effective balance updates).
    /// </summary>
    public void InvalidateTotalActiveBalance() => _totalActiveBalance = null;

    /// <summary>Returns the committee shuffling for <paramref name="epoch"/>, building and caching it if absent.</summary>
    public CommitteeCache GetCommitteeCache(BeaconStateFulu state, ulong epoch) => _committees.GetOrBuild(state, epoch);

    /// <summary><see cref="GetCommitteeCache(BeaconStateFulu, ulong)"/> for a post-fork <see cref="BeaconStateGloas"/>.</summary>
    public CommitteeCache GetCommitteeCache(BeaconStateGloas state, ulong epoch) => _committees.GetOrBuild(state, epoch);

    /// <summary>
    /// The root identifying everything the epoch boundary fixed for the state's current epoch: the
    /// latest block root as of the last slot before that epoch (zero in the genesis epoch, which no
    /// block precedes).
    /// </summary>
    internal static Hash256 GetEpochBoundaryRoot(ulong slot, System.Func<ulong, Hash256> blockRootAtSlot)
    {
        ulong startSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(BeaconStateAccessors.ComputeEpochAtSlot(slot));
        return startSlot == 0 ? Hash256.Zero : blockRootAtSlot(startSlot - 1);
    }

    private static BeaconStateException Conflict(ulong epoch, Hash256 memoized, Hash256 requested) => new(
        $"EpochCache reused across branches at epoch {epoch}: memoized balance was built for " +
        $"epoch boundary root {memoized}, this state's is {requested}; use a separate EpochCache per branch");
}
