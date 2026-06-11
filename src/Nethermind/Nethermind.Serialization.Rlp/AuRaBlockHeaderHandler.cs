// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp;

/// <summary>
/// Plugin-provided factory for stamping an AuRa step + signature seal onto a
/// <see cref="BlockHeader"/>. Used by <see cref="HeaderDecoder"/> to construct the AuRa
/// subclass from wire bytes and by core test builders that need to materialise the same
/// subclass without referencing the AuRa plugin.
/// </summary>
public interface IAuRaBlockHeaderHandler
{
    /// <summary>
    /// Stamp the AuRa seal on a header, upgrading the runtime type to the AuRa subclass when
    /// the input is a base <see cref="BlockHeader"/>. A <c>null</c> signature is preserved.
    /// </summary>
    BlockHeader SetSeal(BlockHeader header, long step, byte[]? signature);
}

public static class AuRaBlockHeaderHandler
{
    // volatile so plain reads observe publication done via Interlocked.CompareExchange in Register.
    private static volatile IAuRaBlockHeaderHandler? _instance;

    public static IAuRaBlockHeaderHandler? Instance => _instance;

    /// <summary>
    /// Registers the AuRa handler exactly once. Re-registering the same instance is a no-op;
    /// registering a different instance throws — the AuRa header shape is a global RLP invariant.
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
