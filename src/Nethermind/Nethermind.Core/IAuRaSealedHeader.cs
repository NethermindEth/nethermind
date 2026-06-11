// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// Read accessor for AuRa <c>step</c> + <c>signature</c> seal fields. Implemented by
/// <c>AuRaBlockHeader</c> in the AuRa plugin so core code can interrogate the seal via pattern
/// match without referencing the plugin.
/// </summary>
public interface IAuRaSealedHeader
{
    long? AuRaStep { get; set; }
    byte[]? AuRaSignature { get; set; }
}

public static class AuRaSealedHeaderExtensions
{
    /// <summary>Returns the seal only if both step and signature are stamped.</summary>
    public static bool TryGetAuRaSeal(this BlockHeader header, out long step, out byte[]? signature)
    {
        if (header is IAuRaSealedHeader { AuRaStep: { } s, AuRaSignature: { } sig })
        {
            step = s;
            signature = sig;
            return true;
        }

        step = 0;
        signature = null;
        return false;
    }

    public static bool IsAuRa(this BlockHeader header) => header is IAuRaSealedHeader;

    /// <summary>
    /// Copy step + signature from <paramref name="src"/> onto <paramref name="dst"/>. No-op if either
    /// header is not AuRa-typed. A partial seal (step set, signature still null) is preserved.
    /// </summary>
    public static void CopyAuRaSeal(BlockHeader src, BlockHeader dst)
    {
        if (src is IAuRaSealedHeader auraSrc && dst is IAuRaSealedHeader auraDst)
        {
            auraDst.AuRaStep = auraSrc.AuRaStep;
            auraDst.AuRaSignature = auraSrc.AuRaSignature;
        }
    }
}
