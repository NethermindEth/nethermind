// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Caching;

/// <summary>
/// Interface for components that maintain an in-memory cache that can be cleared.
/// Implement this interface explicitly on classes that have caches.
/// Usage: <code>(store as IClearableCache)?.ClearCache()</code>
/// </summary>
public interface IClearableCache
{
    /// <summary>
    /// Clears the in-memory cache. Does not affect underlying persistent storage.
    /// </summary>
    void ClearCache();
}
