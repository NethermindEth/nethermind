// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;

namespace Nethermind.State.Pbt;

/// <summary>A sealed canonical EIP-8297 diff covering <see cref="From"/> to <see cref="To"/>.</summary>
public class PbtSnapshot(
    in StateId from,
    in StateId to,
    in ValueHash256 treeRoot,
    PbtSnapshotContent content,
    IPbtResourcePool resourcePool,
    PbtResourcePool.Usage usage) : RefCountingDisposable
{
    private PbtSnapshotPayloadSize? _payloadSize;

    public StateId From { get; } = from;
    public StateId To { get; } = to;
    public ValueHash256 TreeRoot { get; } = treeRoot;
    public PbtSnapshotContent Content { get; } = content;

    internal PbtSnapshotPayloadSize PayloadSize => _payloadSize ??= Content.GetPayloadSize();

    public bool TryLease() => TryAcquireLease();

    protected override void CleanUp() => resourcePool.ReturnSnapshotContent(usage, Content);
}
