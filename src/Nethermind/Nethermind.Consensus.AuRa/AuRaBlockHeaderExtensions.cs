// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.AuRa;

/// <summary>
/// Convenience accessors for the AuRa seal fields on a base <see cref="BlockHeader"/> reference.
/// </summary>
/// <remarks>
/// AuRa code paths always receive an <see cref="AuRaBlockHeader"/> at runtime (built by the
/// chainspec loader, the header decoder, or the local sealer) but the type system often only
/// guarantees a base <see cref="BlockHeader"/>. These helpers pattern-match without throwing,
/// so they're safe to use from boundary code that might also see a non-AuRa header.
/// </remarks>
public static class AuRaBlockHeaderExtensions
{
    public static long? GetAuRaStep(this BlockHeader header) => (header as AuRaBlockHeader)?.AuRaStep;

    public static byte[]? GetAuRaSignature(this BlockHeader header) => (header as AuRaBlockHeader)?.AuRaSignature;

    /// <summary>
    /// Hard cast — use from AuRa-only code paths where the runtime type is guaranteed.
    /// </summary>
    public static AuRaBlockHeader AsAuRa(this BlockHeader header) => (AuRaBlockHeader)header;
}
