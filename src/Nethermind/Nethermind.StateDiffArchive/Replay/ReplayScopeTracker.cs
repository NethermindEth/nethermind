// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Evm.State;

namespace Nethermind.StateDiffArchive.Replay;

/// <summary>
/// Shares the main-processing world-state scope between the <see cref="ReplayScopeProvider"/> (which
/// opens it) and the <see cref="ReplayBlockProcessor"/> (which writes the recorded diff into it).
/// </summary>
/// <remarks>
/// The main block-processing path opens and uses a single scope on one thread at a time, so a plain
/// field is sufficient — no per-thread state needed.
/// </remarks>
public sealed class ReplayScopeTracker
{
    /// <summary>The scope opened for the block currently being processed, or null when none is open.</summary>
    public IWorldStateScopeProvider.IScope? Current { get; set; }

    /// <summary>The recorded state root the next commit must reproduce, or null to skip verification.</summary>
    public Hash256? ExpectedRoot { get; set; }
}
