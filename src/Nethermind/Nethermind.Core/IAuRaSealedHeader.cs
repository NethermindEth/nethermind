// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// Marker + accessor contract for headers carrying an AuRa <c>step</c> + <c>signature</c> seal.
/// Implemented by <c>AuRaBlockHeader</c> in the AuRa plugin so core code (RPC formatters,
/// BlockProcessor, sync server, test comparators) can interrogate the seal via a pattern
/// match without taking a hard dependency on the AuRa assembly.
/// </summary>
/// <remarks>
/// The factory side — constructing an AuRa header or upgrading a base one — still flows through
/// <see cref="AuRaBlockHeaderHandler"/> because <c>HeaderDecoder</c> and <c>ChainSpecLoader</c>
/// need a way to materialise the subclass without a plugin reference. The static handler is
/// the residual seam; this interface covers everything else.
/// </remarks>
public interface IAuRaSealedHeader
{
    /// <summary>AuRa step number (analogous to Ethash <c>nonce</c>). Null while the seal is partial.</summary>
    long? AuRaStep { get; set; }

    /// <summary>AuRa signature over the header (analogous to Ethash <c>mixHash</c>). Null while the seal is partial.</summary>
    byte[]? AuRaSignature { get; set; }
}

/// <summary>Convenience helpers over <see cref="IAuRaSealedHeader"/>.</summary>
public static class AuRaSealedHeaderExtensions
{
    /// <summary>
    /// Return the AuRa seal if both step and signature are stamped.
    /// </summary>
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

    /// <summary>Whether the header carries the AuRa subclass, regardless of whether the seal has been stamped.</summary>
    public static bool IsAuRa(this BlockHeader header) => header is IAuRaSealedHeader;

    /// <summary>
    /// Copy step + signature from <paramref name="src"/> onto <paramref name="dst"/>. No-op if either
    /// header is not AuRa-typed. Preserves a partial seal (step set, signature still null) — required
    /// by block-production paths that rebuild the header between <c>PrepareBlock</c> (step) and
    /// <c>SealBlock</c> (signature).
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
