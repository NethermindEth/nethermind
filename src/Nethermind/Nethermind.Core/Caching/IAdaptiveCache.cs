// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Caching;

/// <summary>
/// Exposes a cache whose memory capacity can be changed while the node is running.
/// </summary>
public interface IAdaptiveCache
{
    /// <summary>The cache name used in diagnostics.</summary>
    string Name { get; }

    /// <summary>The current memory capacity in bytes.</summary>
    long Capacity { get; }

    /// <summary>The cache's current memory usage in bytes.</summary>
    long Usage { get; }

    /// <summary>The minimum supported memory capacity in bytes.</summary>
    long MinimumCapacity { get; }

    /// <summary>The maximum supported memory capacity in bytes.</summary>
    long MaximumCapacity { get; }

    /// <summary>Changes the memory capacity to <paramref name="capacity"/> bytes.</summary>
    /// <param name="capacity">The new capacity in bytes.</param>
    void SetCapacity(long capacity);
}

/// <summary>
/// Registers caches with the process-wide adaptive cache budget.
/// </summary>
public interface IAdaptiveCacheManager
{
    /// <summary>Adds <paramref name="cache"/> to the process-wide adaptive cache budget.</summary>
    /// <param name="cache">A resizeable cache.</param>
    void Register(IAdaptiveCache cache);
}
