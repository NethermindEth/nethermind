//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.JsonRpc.Modules.Rpc;

/// Replicate https://github.com/ethereum/go-ethereum/blob/4860e50e057b0fb0fa7ff9672fcdd737ac137d1c/rpc/server.go#L139
/// so that `geth attach` would work with nethermind. Redundant name, but consistent with other module.
public class RpcRpcModule: IRpcRpcModule
{
    private readonly IDictionary<string, string> _enabledModules;

    public RpcRpcModule(IReadOnlyCollection<string> enabledModules)
    {
        // Geth seems to fix version at 1.0t 
        _enabledModules = enabledModules.ToDictionary((s => s), s => "1.0");;
    }
    
    public ResultWrapper<IDictionary<string, string>> rpc_modules()
    {
        return ResultWrapper<IDictionary<string, string>>.Success(_enabledModules);
    }
}
