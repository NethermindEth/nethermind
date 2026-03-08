// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Blockchain;

/// <summary>
/// Interface for BlockTree implementations that support resetting internal state for testing.
/// Implement this interface explicitly on classes that need state reset capability.
/// Usage: <code>(blockTree as IResettableBlockTree)?.ResetInternalState()</code>
/// </summary>
public interface IResettableBlockTree
{
    /// <summary>
    /// Resets internal in-memory state (Head, BestSuggestedHeader, etc.).
    /// Used in test scenarios where the BlockTree needs to be reinitialized
    /// without recreating the entire DI container.
    /// </summary>
    void ResetInternalState();
}
