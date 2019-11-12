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

using System.Collections.Generic;
using System.Linq;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test
{
    public static class RpcTest
    {
        public static JsonRpcResponse TestRequest<T>(T module, string method, params string[] parameters) where T : class, IModule
        {
            IJsonRpcService service = BuildRpcService<T>(module);
            JsonRpcRequest request = GetJsonRequest(method, parameters);
            return service.SendRequestAsync(request).Result;
        }
        
        public static string TestSerializedRequest<T>(IReadOnlyCollection<JsonConverter> converters, T module, string method, params string[] parameters) where T : class, IModule
        {
            IJsonRpcService service = BuildRpcService(module);
            JsonRpcRequest request = GetJsonRequest(method, parameters);
            JsonRpcResponse response = service.SendRequestAsync(request).Result;
            JsonSerializerSettings settings = new JsonSerializerSettings();
            settings.ContractResolver = new CamelCasePropertyNamesContractResolver();
            settings.Converters = service.Converters.Union(converters).ToArray();
            string serialized = JsonConvert.SerializeObject(response, settings);
            TestContext.WriteLine(serialized.Replace("\"", "\\\""));
            return serialized;
        }
        
        public static string TestSerializedRequest<T>(T module, string method, params string[] parameters) where T : class, IModule
        {
            return TestSerializedRequest(new JsonConverter[0], module, method, parameters);
        }
        
        public static IJsonRpcService BuildRpcService<T>(T module) where T : class, IModule
        {
            var moduleProvider = new TestRpcModuleProvider<T>(module);
            moduleProvider.Register(new SingletonModulePool<T>(new SingletonFactory<T>(module), true));
            IJsonRpcService service = new JsonRpcService(moduleProvider, NullLogManager.Instance);
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
                JsonRpc = "2.0",
                Method = method,
                Params = parameters?.ToArray() ?? new string[0],
                Id = 67
            };

            return request;
        }
    }
}