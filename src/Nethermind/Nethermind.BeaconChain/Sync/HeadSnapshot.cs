// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Sync;

/// <summary>A head computation published as one value (fork-choice.md get_head).</summary>
/// <param name="Status">The status, never mutated after publication.</param>
/// <param name="FullHeadRoot">The FULL head root, otherwise null (gloas/fork-choice.md get_head).</param>
/// <param name="JustifiedRoot">The head computation's justified checkpoint root.</param>
/// <param name="ExecutionInSync">Whether the execution layer confirmed the head VALID.</param>
public sealed record HeadSnapshot(StatusMessageV2 Status, Hash256? FullHeadRoot, Hash256 JustifiedRoot, bool ExecutionInSync);

/// <summary>Publishes one completed get_head view to other threads (fork-choice.md).</summary>
/// <remarks><see cref="Current"/> stays <c>null</c> until the orchestrator is initialized.</remarks>
public sealed class HeadSnapshotHolder
{
    private HeadSnapshot? _current;

    public HeadSnapshot? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }
}
