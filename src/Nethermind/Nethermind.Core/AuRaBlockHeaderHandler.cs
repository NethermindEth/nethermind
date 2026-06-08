// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core;

/// <summary>
/// Plugin-provided bridge for the AuRa-specific seal fields that no longer live on
/// <see cref="BlockHeader"/>.
/// </summary>
/// <remarks>
/// Registered by the AuRa plugin assembly through a <c>ModuleInitializer</c> so it is
/// available before <c>ChainSpecLoader</c> or <c>HeaderDecoder</c> ever touch an AuRa
/// header. Core code (ChainSpecLoader, RLP HeaderDecoder, BlockProcessor, RPC formatters,
/// test builders) routes through <see cref="AuRaBlockHeaderHandler.Instance"/> rather
/// than depending on the AuRa plugin directly.
/// </remarks>
public interface IAuRaBlockHeaderHandler
{
    /// <summary>
    /// Construct a new AuRa-typed BlockHeader. The seal fields are filled in via a
    /// subsequent <see cref="SetSeal"/> call; the caller fills the remaining base fields
    /// just like a regular <see cref="BlockHeader"/>.
    /// </summary>
    BlockHeader CreateBlockHeader(
        Hash256? parentHash,
        Hash256? unclesHash,
        Address? beneficiary,
        in UInt256 difficulty,
        long number,
        long gasLimit,
        ulong timestamp,
        byte[] extraData);

    /// <summary>
    /// Upgrade a base <see cref="BlockHeader"/> to the AuRa-typed subclass, copying all fields
    /// across. Returns the same instance if already AuRa-typed.
    /// </summary>
    BlockHeader UpgradeToAuRa(BlockHeader header);

    /// <summary>
    /// Set the AuRa seal on a header (upgrading via <see cref="UpgradeToAuRa"/> if needed).
    /// </summary>
    /// <returns>The AuRa-typed header carrying the seal.</returns>
    BlockHeader SetSeal(BlockHeader header, long step, byte[] signature);

    /// <summary>
    /// Read the AuRa seal fields off a header. Returns false for non-AuRa headers.
    /// </summary>
    bool TryGetSeal(BlockHeader header, out long step, out byte[]? signature);
}

/// <summary>
/// Static slot the AuRa plugin populates at assembly-load time.
/// </summary>
public static class AuRaBlockHeaderHandler
{
    /// <summary>
    /// The registered AuRa handler, or <c>null</c> if the AuRa plugin assembly has not
    /// been loaded. Code on the AuRa hot path can require this to be non-null; code on
    /// generic paths should null-check and fall back.
    /// </summary>
    public static IAuRaBlockHeaderHandler? Instance { get; set; }
}
