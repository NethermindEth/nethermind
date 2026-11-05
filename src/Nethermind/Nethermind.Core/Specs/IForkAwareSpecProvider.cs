// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Core.Specs;

/// <summary>
/// Extends <see cref="ISpecProvider"/> with the ability to look up a named fork's release spec.
/// Implementations may vary per network (Mainnet, AuRa, Gnosis, etc.).
/// </summary>
public interface IForkAwareSpecProvider : ISpecProvider
{
    IEnumerable<string> AvailableForks { get; }
    bool TryGetForkSpec(string forkName, out IReleaseSpec? spec);
}
