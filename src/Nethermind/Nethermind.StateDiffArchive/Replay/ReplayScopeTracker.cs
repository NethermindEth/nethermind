// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Evm.State;

namespace Nethermind.StateDiffArchive.Replay;

/// <summary>
/// Shares the active world-state scope between the <see cref="ReplayScopeProvider"/> (which opens it) and
/// the <see cref="ReplayBlockProcessor"/> (which writes the recorded diff into it).
/// </summary>
/// <remarks>
/// Registered per processing scope, not as a singleton: the main processing env and the prewarmer (and any
/// other env) each get their own tracker, so a prewarmer scope opened through the same decorated provider
/// cannot clobber the scope the main env is mid-processing.
/// </remarks>
public sealed class ReplayScopeTracker
{
    /// <summary>The scope opened for the block being processed, or null when none is open.</summary>
    public IWorldStateScopeProvider.IScope? Current { get; set; }

    /// <summary>The recorded state root the next commit must reproduce, or null to skip verification.</summary>
    public Hash256? ExpectedRoot { get; set; }
}
