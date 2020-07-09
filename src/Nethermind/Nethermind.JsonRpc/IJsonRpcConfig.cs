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

using Nethermind.Config;

namespace Nethermind.JsonRpc
{
    public interface IJsonRpcConfig : IConfig
    {
        [ConfigItem(Description = "Defines whether the JSON RPC service is enabled on node startup. Configure host nad port if default values do not work for you.", DefaultValue = "false")]
        bool Enabled { get; set; }

        [ConfigItem(Description = "Host for JSON RPC calls. Ensure the firewall is configured when enabling JSON RPC. If it does not work with 117.0.0.1 try something like 10.0.0.4 or 192.168.0.1", DefaultValue = "\"127.0.0.1\"")]
        string Host { get; set; }

        [ConfigItem(Description = "JSON RPC tracers' timeout value given in miliseconds.", DefaultValue = "20000")] 
        int TracerTimeout { get; set; }

        [ConfigItem(Description = "Base file path for diagnostic JSON RPC recorder.", DefaultValue = "\"logs/rpc.log_1.txt\"")]
        string RpcRecorderBaseFilePath { get; set; }

        [ConfigItem(Description = "Defines whether the JSON RPC diagnostic recording is enabled on node startup. Do not enable unless you are a DEV diagnosing issues with JSON RPC.", DefaultValue = "false")]
        bool RpcRecorderEnabled { get; set; }

        [ConfigItem(Description = "Port number for JSON RPC calls. Ensure the firewall is configured when enabling JSON RPC.", DefaultValue = "8545")]
        int Port { get; set; }
        
        [ConfigItem(Description = "Port number for JSON RPC web sockets calls. By default same port is used as regular JSON RPC. Ensure the firewall is configured when enabling JSON RPC.", DefaultValue = "8545")]
        int WebSocketsPort { get; set; }
        
        [ConfigItem(Description = "Defines which RPC modules should be enabled.", DefaultValue = "all")]
        string[] EnabledModules { get; set; }
        
        [ConfigItem(Description = "Defines block depth when finding logs.", DefaultValue = "1000")]
        int FindLogBlockDepthLimit { get; set; }
        
        [ConfigItem(Description = "Gas limit for eth_call and eth_estimateGas", DefaultValue = "100000000")]
        long? GasCap { get; set; }
        
        [ConfigItem(Description = "Interval between the JSON RPC stats report log", DefaultValue = "300")]
        public int ReportIntervalSeconds { get; set; }
        
        [ConfigItem(Description = "Buffer responses before sending them to client. This allows to set Content-Length in response instead of using Transfer-Encoding: chunked. This may degrade performance on big responses.", DefaultValue = "false")]
        public bool BufferResponses { get; set; }
    }
}
