//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Linq;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.JsonRpc
{
    public class JsonRpcConfig : IJsonRpcConfig
    {
        private int? _webSocketsPort;
        public bool Enabled { get; set; }
        public string Host { get; set; }
        public int TracerTimeout { get; set; } = 20000;
        public string RpcRecorderBaseFilePath { get; set; } = "logs/rpc.{counter}.txt"; 
        public bool RpcRecorderEnabled { get; set; }
        public int Port { get; set; }
 
        public int WebSocketsPort
        {
            get => _webSocketsPort ?? Port;
            set => _webSocketsPort = value;
        }

        public string[] EnabledModules { get; set; } = Enum.GetValues(typeof(ModuleType)).OfType<ModuleType>().Select(mt => mt.ToString()).ToArray();
        public int FindLogBlockDepthLimit { get; set; } = 1000;
        public long? GasCap { get; set; } = 100000000;
        public int ReportIntervalSeconds { get; set; } = 300;
        public bool BufferResponses { get; set; }
        public string CallsFilterFilePath { get; set; } = "Data/jsonrpc.filter";
    }
}
