// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Resettables;

/// <summary>
/// Defines a contract for objects that can be returned to a pool or resettable resource manager.
/// Implementations should ensure that <see cref="Return"/> releases or resets the object for reuse.
/// </summary>
public interface IReturnable
{
    /// <summary>
    /// Returns the object to its pool or resource manager, making it available for reuse.
    /// Implementations should ensure the object is properly reset or cleaned up.
    /// </summary>
    void Return();
}
