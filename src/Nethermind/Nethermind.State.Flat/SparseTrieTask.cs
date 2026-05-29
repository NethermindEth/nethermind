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
/// </remarks>
public sealed class SparseTrieTask : IAsyncDisposable
{
    /// <summary>A single pre-hashed delta batch for one commit phase.</summary>
    public readonly record struct HashedDelta(
        IReadOnlyList<(ValueHash256 AccountPath, LeafUpdate Update)> AccountUpdates,
        IReadOnlyList<(Hash256 AccountPath, Hash256 PreviousStorageRoot, ValueHash256 SlotPath, LeafUpdate Update)> StorageUpdates);

    private readonly Channel<HashedDelta> _channel;
    private readonly SparseRootComputer _computer;
    private readonly ILogger _logger;
    private readonly CancellationToken _ct;
    private readonly Task _drainTask;

    // Accumulated per-block changes, applied to the computer when Finish() is signalled.
    // Account updates are last-writer-wins per key (a later commit phase supersedes an earlier
    // one for the same account); storage is grouped per contract. This mirrors how the
    // synchronous path builds its update dictionaries before ComputeStateRoot.
    private readonly Dictionary<ValueHash256, LeafUpdate> _accounts = [];
    private readonly Dictionary<Hash256, (Hash256 PrevRoot, Dictionary<ValueHash256, LeafUpdate> Slots)> _storage = [];

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
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation => caller is falling back to the synchronous path; nothing to do.
        }
    }

    /// <summary>
    /// Awaits all streamed deltas, applies them through the M3 <see cref="SparseRootComputer"/>,
    /// and returns the computed state root. Faults (or returns null) are the caller's signal to
    /// trust the synchronous root instead.
    /// </summary>
    public async Task<Hash256> GetRootAsync()
    {
        await _drainTask; // ensures all deltas accumulated

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
        try { await _drainTask; } catch { /* best-effort */ }
    }
}
