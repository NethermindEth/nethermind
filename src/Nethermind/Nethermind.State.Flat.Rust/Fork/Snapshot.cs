// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Utils;
using Nethermind.State.Flat.Rust.Interop;

namespace Nethermind.State.Flat.Rust.Fork;

/// <summary>
/// The writes between state <see cref="From"/> and state <see cref="To"/> a <see cref="SnapshotBundle"/> committed: a
/// native snapshot, with the C# trie nodes it holds for the <c>IsPersisted</c> replay.
/// </summary>
/// <remarks>
/// The content is native only, so the class has no content members. Releasing the last lease releases the native
/// snapshot, then prunes and returns the trie nodes nobody took with <see cref="TakeNodes"/>.
/// </remarks>
public sealed class Snapshot : RefCountingDisposable
{
    private CapturedNodes? _nodes;

    /// <param name="handle">The native snapshot, which the snapshot releases.</param>
    /// <param name="nodes">The trie nodes the commit set, which the snapshot disposes unless they are taken.</param>
    internal Snapshot(in StateId from, in StateId to, SnapshotHandle handle, CapturedNodes? nodes)
    {
        From = from;
        To = to;
        Handle = handle;
        _nodes = nodes;
    }

    public StateId From { get; }

    public StateId To { get; }

    internal SnapshotHandle Handle { get; }

    public bool TryAcquire() => TryAcquireLease();

    /// <summary>Hands the trie nodes over to the caller, who then disposes them; null once taken.</summary>
    internal CapturedNodes? TakeNodes() => Interlocked.Exchange(ref _nodes, null);

    protected override void CleanUp()
    {
        Handle.Dispose();
        TakeNodes()?.PruneAndDispose();
    }
}
