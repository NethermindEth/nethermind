// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;

namespace Nethermind.State.Pbt;

/// <summary>
/// A sealed diff layer covering the state transition <see cref="From"/> → <see cref="To"/>.
/// Content is immutable once sealed; the snapshot is leased by every bundle reading through it
/// and released when pruned.
/// </summary>
/// <remarks>
/// The snapshot owns <paramref name="content"/> from construction: it is returned to
/// <paramref name="resourcePool"/> when the last lease drops, so nothing may read the content after
/// that. <paramref name="usage"/> is captured here rather than read back from whoever releases the
/// last lease, because a content returned to a category it was not rented from is never detected —
/// it just starves one pool and inflates another.
/// </remarks>
public class PbtSnapshot(in StateId from, in StateId to, in ValueHash256 treeRoot, PbtSnapshotContent content, IPbtResourcePool resourcePool, PbtResourcePool.Usage usage)
    : RefCountingDisposable
{
    public StateId From { get; } = from;
    public StateId To { get; } = to;

    /// <summary>The EIP-8297 root of the state at <see cref="To"/>, as opposed to the header root <see cref="StateId.StateRoot"/> keys it by.</summary>
    public ValueHash256 TreeRoot { get; } = treeRoot;

    public PbtSnapshotContent Content { get; } = content;

    public bool TryLease() => TryAcquireLease();

    protected override void CleanUp() => resourcePool.ReturnSnapshotContent(usage, Content);
}
