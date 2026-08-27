// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Exceptions;

namespace Nethermind.Db;

/// <summary>One <c>FlatDb.HistorySliceAddresses</c> entry: an address, and optionally the bounded retention (in
/// blocks below the watermark) the operator wants for it. Absent retention means unbounded intent.</summary>
public readonly record struct SliceScopeEntry(Address Address, ulong? RetentionBlocks);

public static class SliceScopeConfig
{
    public static IReadOnlyList<SliceScopeEntry> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];

        List<SliceScopeEntry> entries = [];
        foreach (Range tokenRange in raw.AsSpan().Split(','))
        {
            ReadOnlySpan<char> token = raw.AsSpan(tokenRange).Trim();
            if (token.IsEmpty) continue;
            entries.Add(ParseToken(token));
        }

        return entries;
    }

    private static SliceScopeEntry ParseToken(ReadOnlySpan<char> token)
    {
        int separatorIndex = token.IndexOf(':');
        ReadOnlySpan<char> addressToken = separatorIndex < 0 ? token : token[..separatorIndex];
        ulong? retentionBlocks = null;

        if (separatorIndex >= 0)
        {
            ReadOnlySpan<char> retentionToken = token[(separatorIndex + 1)..];
            if (!ulong.TryParse(retentionToken, out ulong parsedRetention))
            {
                throw new InvalidConfigurationException(
                    $"FlatDb.HistorySliceAddresses entry '{token}' has retention '{retentionToken}', which is not a valid non-negative integer.", -1);
            }

            retentionBlocks = parsedRetention;
        }

        Address? address;
        try
        {
            // Address.TryParse only guards IndexOutOfRangeException; malformed hex escapes it as a FormatException,
            // which belongs to the operator as the configuration error below, not to the caller as a crash.
            Address.TryParse(addressToken.ToString(), out address);
        }
        catch (FormatException)
        {
            address = null;
        }

        if (address is null)
        {
            throw new InvalidConfigurationException(
                $"FlatDb.HistorySliceAddresses entry '{token}' has address '{addressToken}', which is not a valid address.", -1);
        }

        return new SliceScopeEntry(address, retentionBlocks);
    }
}
