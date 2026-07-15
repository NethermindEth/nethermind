// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Utils;

namespace Nethermind.State.Pbt;

/// <summary>
/// A sealed diff layer covering the state transition <see cref="From"/> → <see cref="To"/>.
/// Content is immutable once sealed; the snapshot is leased by every bundle reading through it
/// and released when pruned.
/// </summary>
public class PbtSnapshot(in StateId from, in StateId to, PbtSnapshotContent content) : RefCountingDisposable
{
    public StateId From { get; } = from;
    public StateId To { get; } = to;
    public PbtSnapshotContent Content { get; } = content;

    public bool TryLease() => TryAcquireLease();

    protected override void CleanUp()
    {
    }
}
