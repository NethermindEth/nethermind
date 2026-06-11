// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus;

/// <summary>
/// Accessor for the AuRa <c>step</c> + <c>signature</c> seal fields. Implemented by
/// <c>AuRaBlockHeader</c> in the AuRa plugin so seal-agnostic components (RPC models, sync
/// logging) can interrogate the seal via pattern match without referencing the plugin.
/// </summary>
public interface IAuRaSealedHeader
{
    long AuRaStep { get; set; }

    /// <summary>The sealer's signature; null until the block is sealed.</summary>
    byte[]? AuRaSignature { get; set; }
}

public static class AuRaSealedHeaderExtensions
{
    public static bool TryGetAuRaSeal(this BlockHeader header, out long step, out byte[]? signature)
    {
        if (header is IAuRaSealedHeader aura)
        {
            step = aura.AuRaStep;
            signature = aura.AuRaSignature;
            return true;
        }

        step = 0;
        signature = null;
        return false;
    }
}
