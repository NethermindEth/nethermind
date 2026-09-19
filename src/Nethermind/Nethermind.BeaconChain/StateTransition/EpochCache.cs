// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>
/// Caller-owned per-epoch caches used by the state transition: the memoized total active balance
/// and an LRU of committee shufflings.
/// </summary>
/// <remarks>
/// Not thread-safe and not fork-aware for the balance memo: own one instance per state lineage
/// (e.g. per block-processing context) and do not share it across conflicting forks. The committee
/// LRU is fork-safe because it is keyed by the shuffling decision root.
/// </remarks>
public sealed class EpochCache
{
    private (ulong Epoch, ulong Balance)? _totalActiveBalance;
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
    public ulong GetTotalActiveBalance(BeaconStateFulu state)
    {
        ulong epoch = state.GetCurrentEpoch();
        if (_totalActiveBalance is not { } cached || cached.Epoch != epoch)
        {
            ulong balance = state.GetTotalBalance(state.GetActiveValidatorIndices(epoch));
            _totalActiveBalance = cached = (epoch, balance);
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
}
