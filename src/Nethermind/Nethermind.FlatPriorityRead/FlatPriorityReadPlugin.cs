// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Core;
using Nethermind.Api.Extensions;

namespace Nethermind.FlatPriorityRead;

/// <summary>Serves the flat storage slots of configured contracts from an in-memory store.</summary>
public class FlatPriorityReadPlugin(IFlatPriorityReadConfig config) : INethermindPlugin
{
    public string Name => "FlatPriorityRead";
    public string Description => "Serves flat slot reads of chosen contracts from memory";
    public string Author => "Nethermind";

    public bool Enabled => config.Enabled;
    public IModule Module => new FlatPriorityReadModule(config);
}
