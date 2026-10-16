// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Autofac;
using Autofac.Core;

namespace Nethermind.Core.Container;

/// <summary>The kinds of read-only query an environment serves.</summary>
[Experimental("NMEVM0001")]
[Flags]
public enum ReadOnlyQueryKind
{
    None = 0,
    /// <summary>
    /// Single calls - eth_call, eth_estimateGas and eth_createAccessList, with and without overrides - and every other
    /// user of the shared read-only transaction processor source.
    /// </summary>
    Call = 1,
}

/// <summary>Marker for modules that configure only the environments serving read-only queries.</summary>
/// <remarks>
/// <para>Register the module itself in the root container as an <see cref="IReadOnlyQueryModule"/>.</para>
/// <para>
/// The modules are never applied to main block processing, prewarming, validation or sync. A
/// <see cref="ReadOnlyQueryKind.Call"/> module configures every environment of the shared read-only transaction
/// processor source, though, and other users also run calls there whose results they act on: Taiko builds its transaction
/// lists there, Shutter reads its eon keys and validator registry there, and the Flashbots rbuilder module serves its
/// calls there. A module must therefore leave every result exactly as it would be without it.
/// </para>
/// </remarks>
[Experimental("NMEVM0001")]
public interface IReadOnlyQueryModule : IModule
{
    /// <summary>The query environments the module configures.</summary>
    ReadOnlyQueryKind AppliesTo => ReadOnlyQueryKind.Call;
}

[Experimental("NMEVM0001")]
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
