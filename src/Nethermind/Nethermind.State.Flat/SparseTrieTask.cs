// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Trie.Sparse;

namespace Nethermind.State.Flat;

/// <summary>
/// M4 (EXPERIMENTAL, shadow-only). Background task that consumes hashed state-change deltas
/// streamed from <c>WorldState.Commit(commitRoots:false)</c> during EVM execution, applies them
/// to a sparse trie, and computes the state root concurrently with execution rather than
/// synchronously at <c>RecalculateStateRoot</c>.
/// </summary>
/// <remarks>
/// This runs in SHADOW mode: the root it produces is compared against the authoritative
/// synchronous root but never trusted, so a bug here cannot affect consensus. Its only jobs are
/// (1) prove the streaming pipeline applies deltas in the right order and reaches the same root,
/// and (2) measure how much of the (already small, ~1-3ms warm) root computation can be hidden
/// behind execution.
///
/// Design follows the plan's M4.3 event loop but deliberately starts minimal: a single channel
/// of pre-hashed account/storage deltas, drained on a dedicated task, feeding the existing
/// <see cref="SparseRootComputer"/> M3 logic. Proof workers (M4.4) and a separate keccak thread
/// (M4.2) are layered on later once this shadow path is proven equal to sequential.
///
/// Thread-safety: the task owns its <see cref="SparseStateTrie"/> exclusively while running. The
/// producer (execution thread) only writes to the channel; it never touches the trie. Completion
/// is signalled by <see cref="Finish"/>, after which <see cref="GetRootAsync"/> returns the
/// computed root (or faults, which the caller treats as "fall back to synchronous").
///
/// WIRING STATUS (read before extending): this class is built, root-equivalence-tested against
/// the synchronous M3 path and Patricia, and gated behind SparseTrieParallelRoot â€” but it is NOT
/// yet constructed in FlatWorldStateScope, because the only place that would yield real overlap is
/// a per-tx hook inside WorldState.Commit(commitRoots:false), and that requires a non-destructive
/// "changed-since-last-commit-phase" delta cursor that PartialStorageProviderBase /
/// PersistentStorageProvider do not currently expose. Adding that cursor touches core state code
/// shared by ALL world-state modes (hash, halfpath, flat), so it is a wide-blast-radius change.
/// Profiling (this branch, realblocks) showed synchronous root compute is ~1-3 ms warm and root is
/// ~4% of block time, so the achievable overlap is &lt;=~3 ms best case â€” below run-to-run noise.
/// The decision was therefore to land the proven streaming core (this file) and DEFER the invasive
/// per-tx hook until either the cursor is needed for another reason or the root cost grows. Wiring
/// it at UpdateRootHash instead (after execution finishes) would add threading overhead with zero
/// overlap, so that shortcut was explicitly rejected.
/// </remarks>
public sealed class SparseTrieTask : IAsyncDisposable
{
    /// <summary>
    /// A single pre-hashed delta batch for one commit phase.
    /// </summary>
    /// <param name="AccountUpdates">Account leaf updates (last-writer-wins per address-hash).</param>
    /// <param name="StorageUpdates">Per-slot leaf updates.</param>
    /// <param name="WipedStorageAccounts">
    /// Address-hashes whose storage was cleared (self-destruct / account deletion) in this phase.
    /// The synchronous path calls <c>WipeStorage</c> on clear; the streamed path MUST do the same
    /// before applying any same-phase slot writes, otherwise writes land on top of stale retained
    /// storage from a previous block and produce a wrong storage root. Order within a delta is:
    /// wipe first, then slot writes (a self-destruct-then-redeploy in one block).
    /// </param>
    public readonly record struct HashedDelta(
        IReadOnlyList<(ValueHash256 AccountPath, LeafUpdate Update)> AccountUpdates,
        IReadOnlyList<(Hash256 AccountPath, Hash256 PreviousStorageRoot, ValueHash256 SlotPath, LeafUpdate Update)> StorageUpdates,
        IReadOnlyList<Hash256>? WipedStorageAccounts = null,
        IReadOnlyList<ValueHash256>? PrefetchAccounts = null,
        IReadOnlyList<(Hash256 AccountPath, Hash256 PreviousStorageRoot, ValueHash256 SlotPath)>? PrefetchSlots = null);

    private readonly Channel<HashedDelta> _channel;
    private readonly SparseRootComputer _computer;
    private readonly ILogger _logger;
    private readonly CancellationToken _ct;
    private readonly Task _drainTask;

    // Set if the drain loop hit cancellation or any fault. Once poisoned, GetRootAsync refuses to
    // return a root computed from a partial/inconsistent accumulation â€” the caller must fall back
    // to the synchronous path. This matters the moment this task stops being shadow-only.
    private volatile bool _poisoned;

    // Accumulated per-block changes, applied to the computer when Finish() is signalled.
    // Account updates are last-writer-wins per key (a later commit phase supersedes an earlier
    // one for the same account); storage is grouped per contract. This mirrors how the
    // synchronous path builds its update dictionaries before ComputeStateRoot.
    private readonly Dictionary<ValueHash256, LeafUpdate> _accounts = [];
    private readonly Dictionary<Hash256, (Hash256 PrevRoot, Dictionary<ValueHash256, LeafUpdate> Slots)> _storage = [];
    // Contracts wiped this block, in case a wipe arrives before this contract's first slot write
    // (or with no later writes at all). Applied in GetRootAsync before the slot dictionaries.
    private readonly HashSet<Hash256> _wiped = [];

    public SparseTrieTask(SparseRootComputer computer, ILogger logger, CancellationToken ct)
    {
        _computer = computer;
        _logger = logger;
        _ct = ct;
        _channel = Channel.CreateUnbounded<HashedDelta>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false, // multiple commit phases may enqueue; execution is still serial per block
        });
        _drainTask = Task.Run(DrainLoopAsync);
    }

    /// <summary>Producer side: enqueue a pre-hashed delta batch. Non-blocking (unbounded channel).</summary>
    public void Enqueue(in HashedDelta delta) => _channel.Writer.TryWrite(delta);

    /// <summary>Signals that no further deltas will arrive for this block.</summary>
    public void Finish() => _channel.Writer.TryComplete();

    private async Task DrainLoopAsync()
    {
        try
        {
            // Accumulate every streamed delta. We deliberately do NOT apply to the trie
            // incrementally yet: the M3 computer's reveal-update-retry loop expects the full
            // change set so it can batch proof reads. The win here is that hashing + channel
            // transfer overlap execution; the actual root compute still happens once at the end.
            // Incremental apply (true overlap of reveal/update with execution) is the next layer.
            await foreach (HashedDelta delta in _channel.Reader.ReadAllAsync(_ct))
            {
                foreach ((ValueHash256 acc, LeafUpdate upd) in delta.AccountUpdates)
                    _accounts[acc] = upd;

                // Wipes first: a contract cleared this phase must drop any slot writes accumulated
                // for it from EARLIER phases (self-destruct mid-block), and be marked so the
                // pre-existing retained storage trie is wiped before same/later-phase writes apply.
                if (delta.WipedStorageAccounts is { Count: > 0 } wipes)
                {
                    foreach (Hash256 wiped in wipes)
                    {
                        _wiped.Add(wiped);
                        if (_storage.TryGetValue(wiped, out (Hash256 PrevRoot, Dictionary<ValueHash256, LeafUpdate> Slots) e))
                            e.Slots.Clear();
                    }
                }

                foreach ((Hash256 accPath, Hash256 prevRoot, ValueHash256 slot, LeafUpdate upd) in delta.StorageUpdates)
                {
                    ref (Hash256 PrevRoot, Dictionary<ValueHash256, LeafUpdate> Slots) entry =
                        ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_storage, accPath, out bool exists);
                    if (!exists)
                    {
                        entry.PrevRoot = prevRoot;
                        entry.Slots = [];
                    }
                    entry.Slots[slot] = upd;
                }

                // Prefetch targets (Reth on_prewarm_targets): insert a Touched marker ONLY where no
                // real update exists yet, so the key's proof is fetched+revealed in the normal flow
                // without overriding an actual write. A Touched leaf is a no-op on the root (neither
                // changes nor deletes a value), so prefetch can never alter the result - it only
                // warms the trie. TryAdd ensures a real write already present is never clobbered; a
                // real update arriving in a LATER delta overwrites the Touched via the branches
                // above (last-writer-wins).
                if (delta.PrefetchAccounts is { Count: > 0 } pAccts)
                {
                    foreach (ValueHash256 acc in pAccts)
                        _accounts.TryAdd(acc, LeafUpdate.Touched());
                }
                if (delta.PrefetchSlots is { Count: > 0 } pSlots)
                {
                    foreach ((Hash256 accPath, Hash256 prevRoot, ValueHash256 slot) in pSlots)
                    {
                        ref (Hash256 PrevRoot, Dictionary<ValueHash256, LeafUpdate> Slots) entry =
                            ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_storage, accPath, out bool exists);
                        if (!exists)
                        {
                            entry.PrevRoot = prevRoot;
                            entry.Slots = [];
                        }
                        entry.Slots.TryAdd(slot, LeafUpdate.Touched());
                        // Touch the account leaf too so its path is revealed for the storage-root
                        // update, mirroring Reth's "touch corresponding account leaf".
                        _accounts.TryAdd(accPath.ValueHash256, LeafUpdate.Touched());
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation => caller is falling back to the synchronous path. Poison so a
            // subsequent GetRootAsync cannot hand back a root from a partially-drained stream.
            _poisoned = true;
        }
        catch (Exception ex)
        {
            // Any drain fault leaves the accumulation inconsistent; poison and let the caller fall
            // back. Logged because once this is more than shadow-only, a silent fault would be a
            // consensus hazard.
            _poisoned = true;
            if (_logger.IsWarn) _logger.Warn($"SparseTrieTask drain faulted, root poisoned: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Awaits all streamed deltas, applies them through the M3 <see cref="SparseRootComputer"/>,
    /// and returns the computed state root.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the drain loop was cancelled or faulted (the accumulation is then incomplete and
    /// any root computed from it would be wrong). Callers MUST treat this as "fall back to the
    /// synchronous root" â€” never trust a poisoned result.
    /// </exception>
    public async Task<Hash256> GetRootAsync()
    {
        await _drainTask; // ensures all deltas accumulated (or poisoned)

        if (_poisoned)
            throw new InvalidOperationException(
                "SparseTrieTask result is poisoned (drain cancelled or faulted); caller must use the synchronous root.");

        // Apply self-destruct/clear wipes before any slot writes, mirroring the synchronous
        // FlatStorageTree path. Without this, slot writes land on stale cross-block retained
        // storage and produce a wrong storage root.
        foreach (Hash256 wiped in _wiped)
            _computer.Trie.WipeStorage(wiped);

        foreach (KeyValuePair<Hash256, (Hash256 PrevRoot, Dictionary<ValueHash256, LeafUpdate> Slots)> kvp in _storage)
        {
            _computer.AddStorageChanges(kvp.Key, kvp.Value.PrevRoot, kvp.Value.Slots);
            _computer.ComputeStorageRoot(kvp.Key);
        }
        _computer.SetAccountChanges(_accounts);
        return _computer.ComputeStateRoot();
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        // Best-effort drain on dispose; any fault is already reflected in _poisoned (set by the
        // drain loop's catch blocks), so GetRootAsync would reject the result. Swallowing here is
        // safe precisely because the poison flag, not this await, gates result validity.
        try { await _drainTask; } catch { /* poison flag already set by drain loop */ }
    }
}
