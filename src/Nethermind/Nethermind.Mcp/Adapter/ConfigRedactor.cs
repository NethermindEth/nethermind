// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Nethermind.Mcp.Adapter;

public sealed partial class ConfigRedactor
{
    private const string RedactedMarker = "[REDACTED]";

    [GeneratedRegex("(key|secret|password|jwt|signer)", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveKeyRegex();

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Redact(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> sections)
    {
        Dictionary<string, IReadOnlyDictionary<string, object?>> result = new(sections.Count);
        foreach (KeyValuePair<string, IReadOnlyDictionary<string, object?>> section in sections)
        {
            result[section.Key] = RedactInner(section.Value);
        }
        return result;
    }

    private static IReadOnlyDictionary<string, object?> RedactInner(IReadOnlyDictionary<string, object?> input)
    {
        Dictionary<string, object?> result = new(input.Count);
        foreach (KeyValuePair<string, object?> entry in input)
        {
            if (SensitiveKeyRegex().IsMatch(entry.Key))
            {
                result[entry.Key] = RedactedMarker;
                continue;
            }

            result[entry.Key] = entry.Value switch
            {
                IReadOnlyDictionary<string, object?> nested => RedactInner(nested),
                _ => entry.Value,
            };
        }
        return result;
    }
}
