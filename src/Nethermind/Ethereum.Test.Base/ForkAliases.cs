// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace Ethereum.Test.Base;

/// <summary>Remaps the fork name a fixture declares onto the fork the loaded archive actually means.</summary>
/// <remarks>
/// Separate EEST release lines can define one fork name incompatibly, and the fixture carries no EIP list
/// to tell them apart — only the archive does. The FOCIL line's <c>Bogota</c> is Amsterdam plus EIP-7805;
/// the frame-transaction line's is Amsterdam plus EIP-8141, which is <c>Eip8141Prototype</c> here. Whoever
/// selects the archive therefore names the fork it means, rather than the parser guessing from a name that
/// is genuinely ambiguous. Aliases must be set before any fixture is loaded; they are read from every
/// parse worker afterwards.
/// </remarks>
public static class ForkAliases
{
    private static volatile FrozenDictionary<string, string> _aliases = FrozenDictionary<string, string>.Empty;

    /// <summary>Replaces the alias table with <paramref name="aliases"/>, each given as <c>From=To</c>.</summary>
    /// <exception cref="ArgumentException">An entry is not a single <c>From=To</c> pair.</exception>
    public static void Set(IReadOnlyList<string> aliases)
    {
        Dictionary<string, string> parsed = new(StringComparer.Ordinal);
        foreach (string alias in aliases)
        {
            string[] parts = alias.Split('=');
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            {
                throw new ArgumentException($"Fork alias '{alias}' is not of the form From=To", nameof(aliases));
            }

            parsed[parts[0]] = parts[1];
        }

        _aliases = parsed.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>Returns the fork name <paramref name="forkName"/> is aliased to, or itself when unaliased.</summary>
    public static string Resolve(string forkName) =>
        _aliases.TryGetValue(forkName, out string? target) ? target : forkName;
}
