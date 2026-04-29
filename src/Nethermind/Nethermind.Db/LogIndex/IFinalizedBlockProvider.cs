// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db.LogIndex;

/// <summary>
/// Provides the current finalized block number so that subsystems can determine their reorg-safe window dynamically.
/// </summary>
public interface IFinalizedBlockProvider
{
    /// <summary>Returns the current finalized block number, or null if finalization has not yet been established.</summary>
    long? FinalizedBlockNumber { get; }
}
