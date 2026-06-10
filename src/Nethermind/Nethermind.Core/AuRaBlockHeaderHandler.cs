// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Core;

/// <summary>
/// Plugin-provided factory for stamping an AuRa step + signature seal onto a
/// <see cref="BlockHeader"/>. Used only by core test builders (<c>BlockHeaderBuilder.WithAura</c>,
/// <c>BlockBuilder.WithAura</c>) that need to materialise the AuRa subclass without taking a
/// dependency on the AuRa plugin assembly.
/// </summary>
/// <remarks>
/// Production read paths use <see cref="IAuRaSealedHeader"/> pattern matching instead. Production
/// write paths (decoder, ChainSpec interceptor, BlockProcessor, BlockProducer) construct
/// <c>AuRaBlockHeader</c> directly inside the AuRa plugin.
/// </remarks>
public interface IAuRaBlockHeaderHandler
{
    /// <summary>
    /// Stamp the AuRa seal on a header, upgrading the runtime type to the AuRa subclass when
    /// the input is a base <see cref="BlockHeader"/>.
    /// </summary>
    /// <remarks>A <c>null</c> signature is preserved — callers (notably block builders that fill
    /// the signature in later) can stamp only the step.</remarks>
    BlockHeader SetSeal(BlockHeader header, long step, byte[]? signature);
}

/// <summary>
/// Static slot the AuRa plugin populates at assembly-load time so core test builders can
/// produce AuRa-shaped headers without referencing the plugin.
/// </summary>
public static class AuRaBlockHeaderHandler
{
    // volatile so plain reads on the Instance getter observe the publication done via
    // Interlocked.CompareExchange in Register, independent of cache coherency assumptions.
    private static volatile IAuRaBlockHeaderHandler? _instance;

    public static IAuRaBlockHeaderHandler? Instance => _instance;

    /// <summary>
    /// Registers the AuRa handler exactly once. Subsequent calls with the same instance
    /// are tolerated (idempotent), but a different instance throws — the AuRa header shape
    /// is a global RLP invariant that two implementations cannot disagree on at runtime.
    /// </summary>
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
