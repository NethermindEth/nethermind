// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Core.Specs;

/// <summary>
/// Extended interface for spec providers that support fork-based resolution
/// </summary>
public interface IForkAwareSpecProvider : ISpecProvider
{
    /// <summary>
    /// Gets available fork names for this spec provider
    /// </summary>
    IEnumerable<string> AvailableForks { get; }

    /// <summary>
    /// Attempts to resolve a fork specification by name
    /// </summary>
    /// <param name="forkName">Name of the fork (case-insensitive)</param>
    /// <param name="spec">The resolved spec if successful</param>
    /// <returns>True if the fork was found and resolved successfully</returns>
    bool TryGetForkSpec(string forkName, out IReleaseSpec? spec);
}
