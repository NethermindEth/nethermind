// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;

namespace Nethermind.JsonRpc.Modules.Rpc;

/// Replicate https://github.com/ethereum/go-ethereum/blob/4860e50e057b0fb0fa7ff9672fcdd737ac137d1c/rpc/server.go#L139
/// so that `geth attach` would work with nethermind. Redundant name, but consistent with other module.
public class RpcRpcModule : IRpcRpcModule
{
    private readonly IDictionary<string, string> _enabledModules;

    public RpcRpcModule(IReadOnlyCollection<string> enabledModules)
    {
        // Geth seems to fix version at 1.0
        _enabledModules = enabledModules.ToDictionary((s => s), s => "1.0");
    }

    public ResultWrapper<IDictionary<string, string>> rpc_modules()
    {
        return ResultWrapper<IDictionary<string, string>>.Success(_enabledModules);
    }
}
