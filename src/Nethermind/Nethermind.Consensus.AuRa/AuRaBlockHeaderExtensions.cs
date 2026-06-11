// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;

namespace Nethermind.Consensus.AuRa;

public static class AuRaBlockHeaderExtensions
{
    public static long? GetAuRaStep(this BlockHeader header) => (header as AuRaBlockHeader)?.AuRaStep;

    public static long GetAuRaStepOrZero(this BlockHeader? header) => (header as AuRaBlockHeader)?.AuRaStep ?? 0;

    public static byte[]? GetAuRaSignature(this BlockHeader header) => (header as AuRaBlockHeader)?.AuRaSignature;

    /// <summary>Hard cast to the seal accessor; restricts callers to the interface so subclass-private state isn't reached for by accident.</summary>
    public static IAuRaSealedHeader AsAuRa(this BlockHeader header) => (IAuRaSealedHeader)header;

    /// <summary>Cast or throw a uniform <see cref="InvalidOperationException"/>; <paramref name="operation"/> defaults to the calling method.</summary>
    public static AuRaBlockHeader RequireAuRa(this BlockHeader header, [CallerMemberName] string? operation = null)
    {
        if (header is AuRaBlockHeader aura) return aura;
        throw new InvalidOperationException(
            $"{operation} requires an AuRa header (block {header.Number}, hash {header.Hash}).");
    }
}
