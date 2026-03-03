// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// Provides a 64-bit hash code for high-performance caching with reduced collision probability.
/// </summary>
/// <remarks>
/// Types implementing this interface can be used with caches that require extended hash bits
/// for collision resistance (e.g., PreWarmCache with 50-bit collision detection).
/// The 64-bit hash should have good distribution across all bits.
/// </remarks>
public interface IHash64
{
    /// <summary>
    /// Returns a 64-bit hash code for the current instance.
    /// </summary>
    /// <returns>A 64-bit hash code with good distribution across all bits.</returns>
    long GetHashCode64();
}
