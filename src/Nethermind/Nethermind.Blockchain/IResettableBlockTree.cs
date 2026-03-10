// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Blockchain;

/// <summary>
/// Interface for BlockTree implementations that support resetting internal state for testing.
/// Implement this interface explicitly on classes that need state reset capability.
/// Usage: <code>(blockTree as IResettableBlockTree)?.ResetInternalState()</code>
/// </summary>
/// <remarks>
/// <para>
/// <b>Thread Safety:</b> Implementations are NOT thread-safe. Callers MUST ensure no concurrent
/// block processing, sync operations, or production are occurring before calling <see cref="ResetInternalState"/>.
/// </para>
/// </remarks>
public interface IResettableBlockTree
{
    /// <summary>
    /// Resets internal in-memory state (Head, BestSuggestedHeader, BestSuggestedBeaconHeader,
    /// FinalizedHash, SafeHash, etc.). Used in test scenarios where the BlockTree needs to be
    /// reinitialized without recreating the entire DI container.
    /// </summary>
    /// <remarks>
    /// <b>Preconditions:</b> No active block processing, sync, or production operations.
    /// </remarks>
    void ResetInternalState();
}
