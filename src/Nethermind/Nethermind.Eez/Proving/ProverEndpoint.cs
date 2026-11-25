// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Eez.Proving;

/// <summary>One attester as configured: where it listens, the key it signs with and the proof system that verifies it.</summary>
public sealed record ProverEndpoint(Uri Url, Address Attester, Address ProofSystem)
{
    /// <summary>Parses <c>url=attester=proofSystem</c>, splitting from the right since a URL may hold <c>=</c>.</summary>
    /// <returns><see langword="null"/> when the entry is not in that form.</returns>
    public static ProverEndpoint? Parse(string entry)
    {
        int last = entry.LastIndexOf('=');
        int middle = last > 0 ? entry.LastIndexOf('=', last - 1) : -1;
        if (middle <= 0
            || !Uri.TryCreate(entry[..middle].Trim(), UriKind.Absolute, out Uri? url)
            || !Address.TryParse(entry[(middle + 1)..last].Trim(), out Address? attester)
            || !Address.TryParse(entry[(last + 1)..].Trim(), out Address? proofSystem)
            || attester == Address.Zero
            || proofSystem == Address.Zero)
        {
            return null;
        }

        return new ProverEndpoint(url, attester!, proofSystem!);
    }
}
