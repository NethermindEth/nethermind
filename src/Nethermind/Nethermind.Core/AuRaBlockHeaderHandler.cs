// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
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
    /// across.
    /// </summary>
    /// <remarks>
    /// Identity-preserving when the input is already an AuRa header: returns the same instance
    /// (no clone) so callers can subsequently mutate fields without losing changes. Pass a
    /// freshly-constructed header when callers must not see later mutations.
    /// </remarks>
    BlockHeader UpgradeToAuRa(BlockHeader header);

    /// <summary>
    /// Set the AuRa seal on a header (upgrading via <see cref="UpgradeToAuRa"/> if needed).
    /// </summary>
    /// <remarks>A <c>null</c> signature is preserved — callers (notably block builders that
    /// fill the signature in later) can stamp only the step.</remarks>
    /// <returns>The AuRa-typed header carrying the seal.</returns>
    BlockHeader SetSeal(BlockHeader header, long step, byte[]? signature);

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
    // volatile so plain reads on the Instance getter observe the publication done via
    // Interlocked.CompareExchange in Register, independent of cache coherency assumptions.
    private static volatile IAuRaBlockHeaderHandler? _instance;

    /// <summary>
    /// The registered AuRa handler, or <c>null</c> if the AuRa plugin assembly has not
    /// been loaded. Code on the AuRa hot path can require this to be non-null; code on
    /// generic paths should null-check and fall back.
    /// </summary>
    public static IAuRaBlockHeaderHandler? Instance => _instance;

    /// <summary>
    /// Convenience shortcut for <see cref="IAuRaBlockHeaderHandler.TryGetSeal"/> that returns
    /// false when the handler has not been registered yet. Cheaper to call from generic code
    /// (HeaderDecoder, RPC formatters, comparers) than the <c>Instance is { } h &amp;&amp; h.TryGetSeal(...)</c>
    /// pattern.
    /// </summary>
    public static bool TryGetSeal(BlockHeader header, out long step, out byte[]? signature)
    {
        IAuRaBlockHeaderHandler? handler = _instance;
        if (handler is null)
        {
            step = 0;
            signature = null;
            return false;
        }
        return handler.TryGetSeal(header, out step, out signature);
    }

    /// <summary>
    /// Registers the AuRa handler exactly once. Subsequent calls with the same instance
    /// are tolerated (idempotent), but a different instance throws — the AuRa header shape
    /// is a global RLP invariant that two implementations cannot disagree on at runtime.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A different handler has already been registered.</exception>
    public static void Register(IAuRaBlockHeaderHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        IAuRaBlockHeaderHandler? prior = Interlocked.CompareExchange(ref _instance, handler, null);
        if (prior is not null && !ReferenceEquals(prior, handler))
        {
            throw new InvalidOperationException(
                $"An AuRa header handler ({prior.GetType().FullName}) is already registered; cannot replace with {handler.GetType().FullName}.");
        }
    }
}
