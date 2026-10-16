// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using Autofac.Core;

namespace Nethermind.Core.Container;

/// <summary>The kinds of read-only query an environment serves.</summary>
[Flags]
public enum ReadOnlyQueryKind
{
    None = 0,
    /// <summary>
    /// Single calls - eth_call, eth_estimateGas and eth_createAccessList, with and without overrides - and every other
    /// user of the shared read-only transaction processor source.
    /// </summary>
    Call = 1,
    /// <summary>eth_simulateV1.</summary>
    Simulate = 2,
    /// <summary>debug_* and trace_* re-execution.</summary>
    Trace = 4,
    All = Call | Simulate | Trace,
}

/// <summary>
/// Marker for modules that configure only the environments serving read-only queries, whose results never feed block
/// processing. Never applied to main processing, block production, prewarming, validation or sync.
/// </summary>
public interface IReadOnlyQueryModule : IModule
{
    /// <summary>The query environments the module configures.</summary>
    ReadOnlyQueryKind AppliesTo => ReadOnlyQueryKind.All;
}

public static class ReadOnlyQueryModuleExtensions
{
    /// <summary>Registers the modules among <paramref name="modules"/> that apply to <paramref name="kind"/>.</summary>
    public static ContainerBuilder AddReadOnlyQueryModules(this ContainerBuilder builder, IReadOnlyList<IReadOnlyQueryModule>? modules, ReadOnlyQueryKind kind)
    {
        if (modules is null)
            return builder;
        foreach (IReadOnlyQueryModule module in modules)
        {
            if ((module.AppliesTo & kind) != 0)
                builder.RegisterModule(module);
        }

        return builder;
    }
}
