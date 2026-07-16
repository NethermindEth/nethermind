// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
/// <param name="resourcePool">The pool <paramref name="content"/> was rented from.</param>
/// <param name="usage">The category <paramref name="content"/> was rented from.</param>
public class PbtSnapshot(in StateId from, in StateId to, PbtSnapshotContent content, IPbtResourcePool resourcePool, PbtResourcePool.Usage usage)
    : RefCountingDisposable
{
    public StateId From { get; } = from;
    public StateId To { get; } = to;
    public PbtSnapshotContent Content { get; } = content;

    public bool TryLease() => TryAcquireLease();

    protected override void CleanUp() => resourcePool.ReturnSnapshotContent(usage, Content);
}
