// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Init.Steps;
using Nethermind.JsonRpc.Modules;
using Nethermind.Consensus;
using Nethermind.Init.Steps.Migrations;

namespace Nethermind.Taiko.Rpc;

public class RegisterTaikoRpcModules : RegisterRpcModules
{
    private readonly TaikoNethermindApi _api;
    private readonly IPoSSwitcher _poSSwitcher;

    public RegisterTaikoRpcModules(INethermindApi api, IPoSSwitcher poSSwitcher) : base(api, poSSwitcher)
    {
        _api = (TaikoNethermindApi)api;
        _poSSwitcher = poSSwitcher;
    }

}
