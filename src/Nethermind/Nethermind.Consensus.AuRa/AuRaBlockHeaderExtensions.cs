// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
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
    /// <summary>Returns the AuRa step on <paramref name="header"/>, or <c>null</c> when the header is not AuRa-typed or the step is unset.</summary>
    public static long? GetAuRaStep(this BlockHeader header) => (header as AuRaBlockHeader)?.AuRaStep;

    /// <summary>Returns the AuRa step on <paramref name="header"/>, or <c>0</c> when the header is not AuRa-typed or the step is unset.</summary>
    public static long GetAuRaStepOrZero(this BlockHeader? header) => (header as AuRaBlockHeader)?.AuRaStep ?? 0;

    public static byte[]? GetAuRaSignature(this BlockHeader header) => (header as AuRaBlockHeader)?.AuRaSignature;

    /// <summary>
    /// Hard cast to the seal-accessor contract — use from AuRa-only code paths where the runtime
    /// type is guaranteed. Returns <see cref="IAuRaSealedHeader"/> rather than the concrete
    /// <see cref="AuRaBlockHeader"/> so callers can't accidentally lean on subclass-specific
    /// state; use a direct cast when the concrete type is genuinely needed.
    /// </summary>
    public static IAuRaSealedHeader AsAuRa(this BlockHeader header) => (IAuRaSealedHeader)header;

    /// <summary>
    /// Cast <paramref name="header"/> to <see cref="AuRaBlockHeader"/>, throwing a uniform
    /// <see cref="InvalidOperationException"/> when the header is not AuRa-typed. The optional
    /// <paramref name="operation"/> is included in the message (defaults to the calling method
    /// name) so the failure points to the actual call site.
    /// </summary>
    public static AuRaBlockHeader RequireAuRa(this BlockHeader header, [CallerMemberName] string? operation = null)
    {
        if (header is AuRaBlockHeader aura) return aura;
        throw new InvalidOperationException(
            $"{operation} requires an AuRa header (block {header.Number}, hash {header.Hash}).");
    }
}
