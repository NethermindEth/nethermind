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

using Nethermind.Config;

namespace Nethermind.JsonRpc
{
    public interface IJsonRpcConfig : IConfig
    {
        [ConfigItem(Description = "Defines whether the JSON RPC service is enabled on node startuo. Configure host nad port if default values do not work for you.", DefaultValue = "false")]
        bool Enabled { get; set; }

        [ConfigItem(Description = "Host for JSON RPC calls. Ensure the firewall is configured when enabling JSON RPC.", DefaultValue = "\"127.0.0.1\"")]
        string Host { get; set; }
        
        [ConfigItem(Description = "Port number for JSON RPC calls. Ensure the firewall is configured when enabling JSON RPC.", DefaultValue = "8545")]
        int Port { get; set; }
        
        [ConfigItem(Description = "Defines which RPC modules should be enabled.", DefaultValue = "all")]
        string[] EnabledModules { get; set; }
    }
}