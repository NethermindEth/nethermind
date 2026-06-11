// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Core.Test.Builders;

/// <summary>
/// Looks up the AuRa block-header handler on first call to <c>WithAura</c> so test builders work
/// without an explicit setup hook. Test assemblies that use WithAura already reference
/// <c>Nethermind.Consensus.AuRa</c>, so the plugin assembly is loaded and the impl is reachable
/// by reflection.
/// </summary>
internal static class AuRaTestSupport
{
    public static IAuRaBlockHeaderHandler EnsureRegistered()
    {
        IAuRaBlockHeaderHandler? handler = AuRaBlockHeaderHandler.Instance;
        if (handler is not null) return handler;

        const string implFullName = "Nethermind.Consensus.AuRa.AuRaBlockHeaderHandlerImpl, Nethermind.Consensus.AuRa";
        Type? implType = Type.GetType(implFullName)
            ?? throw new InvalidOperationException(
                "AuRa plugin not loaded; reference Nethermind.Consensus.AuRa from the test project that uses WithAura.");

        implType.GetMethod("Register", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);

        return AuRaBlockHeaderHandler.Instance
            ?? throw new InvalidOperationException(
                $"{implFullName} did not register the AuRa block-header handler.");
    }
}
