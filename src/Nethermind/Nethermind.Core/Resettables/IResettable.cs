// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Resettables;

/// <summary>
/// Defines a contract for objects that can be reset to their initial state.
/// </summary>
public interface IResettable
{
    /// <summary>
    /// Resets the object to its initial state.
    /// </summary>
    void Reset();
}
