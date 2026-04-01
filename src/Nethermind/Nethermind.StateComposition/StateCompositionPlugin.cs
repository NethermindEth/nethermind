// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;

namespace Nethermind.StateComposition;

public class StateCompositionPlugin : INethermindPlugin
{
    private INethermindApi? _api;

    public string Name => "StateComposition";
    public string Description => "State composition metrics";
    public string Author => "Nethermind";
    public bool MustInitialize => false;
    public bool Enabled => _api?.Config<IStateCompositionConfig>().Enabled ?? true;
    public IModule Module => new StateCompositionModule();

    public Task Init(INethermindApi nethermindApi)
    {
        _api = nethermindApi;
        return Task.CompletedTask;
    }
}
