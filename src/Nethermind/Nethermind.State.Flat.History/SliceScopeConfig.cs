// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Exceptions;

namespace Nethermind.State.Flat.History;

/// <summary>One <c>Flat.HistorySliceAddresses</c> entry: an address, and optionally the bounded retention (in
/// blocks below the watermark) the operator wants for it. Absent retention means unbounded intent - the pruner
/// never advances that scope's floor on its own; deepening it below wherever it starts is a backfill concern,
/// out of this parser's scope. A range scope (one record covering several addresses), if ever needed, is a new
/// record kind under its own reserved-key prefix - not a retrofit of this point-scope shape.</summary>
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
                    $"Flat.HistorySliceAddresses entry '{token}' has retention '{retentionToken}', which is not a valid non-negative integer.", -1);
            }

            retentionBlocks = parsedRetention;
        }

        Address? address;
        try
        {
            if (!Address.TryParse(addressToken.ToString(), out address) || address is null)
            {
                address = null;
            }
        }
        catch (Exception)
        {
            address = null;
        }

        if (address is null)
        {
            throw new InvalidConfigurationException(
                $"Flat.HistorySliceAddresses entry '{token}' has address '{addressToken}', which is not a valid address.", -1);
        }

        return new SliceScopeEntry(address, retentionBlocks);
    }
}
