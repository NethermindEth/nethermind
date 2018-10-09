/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Linq;
using Nethermind.Config;
using Nethermind.Core.Logging;
using Nethermind.JsonRpc.DataModel;
using Nethermind.JsonRpc.Module;
using NSubstitute;

namespace Nethermind.JsonRpc.Test
{
    public static class RpcTest
    {
        public static JsonRpcResponse TestRequest<T>(T module, string method, params string[] parameters) where T : class, IModule
        {
            IJsonRpcService service = BuildRpcService<T>(module);
            JsonRpcRequest request = GetJsonRequest("debug_getFromDb", parameters);
            return service.SendRequest(request);
        }
        
        public static IJsonRpcService BuildRpcService<T>(T module) where T : class, IModule
        {
            var moduleProvider = new TestModuleProvider<T>(module);
            IJsonRpcService service = new JsonRpcService(moduleProvider, Substitute.For<IConfigProvider>(), NullLogManager.Instance);
            return service;
        }
        
        //{
        //    "jsonrpc": "2.0",
        //    "method": "eth_getBlockByNumber",
        //    "params": [ "0x1b4", true ],
        //    "id": 67
        //}
        public static JsonRpcRequest GetJsonRequest(string method, params string[] parameters)
        {
            var request = new JsonRpcRequest()
            {
                Jsonrpc = "2.0",
                Method = method,
                Params = parameters?.ToArray() ?? new string[0],
                Id = 67
            };

            return request;
        }
    }
}