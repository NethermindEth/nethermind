// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp;

/// <summary>
/// Plugin-provided factory for stamping the AuRa step + signature seal onto a header.
/// Used by <see cref="HeaderDecoder"/> on the receive path and by core test builders that need
/// the AuRa subclass without referencing the plugin.
/// </summary>
public interface IAuRaBlockHeaderHandler
{
    /// <summary>Upgrades the runtime type to the AuRa subclass when needed and stamps the seal. A null signature is preserved.</summary>
    BlockHeader SetSeal(BlockHeader header, long step, byte[]? signature);
}

public static class AuRaBlockHeaderHandler
{
    private static volatile IAuRaBlockHeaderHandler? _instance;

    public static IAuRaBlockHeaderHandler? Instance => _instance;

    /// <summary>Idempotent for the same instance; throws on conflicting registrations.</summary>
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
