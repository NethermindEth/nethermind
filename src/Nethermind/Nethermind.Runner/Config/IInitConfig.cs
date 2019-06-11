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

namespace Nethermind.Runner.Config
{
    public interface IInitConfig : IConfig
    {
        [ConfigItem(Description = "If 'true' then it enables thewallet / key store in the application.", DefaultValue = "false")]
        bool EnableUnsecuredDevWallet { get; set; }
        
        [ConfigItem(Description = "If 'true' then any accounts created will be only valid during the session and deleted when application closes.", DefaultValue = "false")]
        bool KeepDevWalletInMemory{ get; set; }
        
        [ConfigItem(Description = "Defines whether the JSON RPC service is enabled on node startup at the 'HttpPort'", DefaultValue = "false")]
        bool JsonRpcEnabled { get; set; }
        
        [ConfigItem(Description = "Defines whether the JSON RPC service is enabled on node startup at the 'HttpPort'", DefaultValue = "\"Clique,Db,Debug,Eth,Net,Trace,TxPool,Web3\"")]
        string[] JsonRpcEnabledModules { get; set; }
        
        [ConfigItem(Description = "Defines whether the WebSockets service is enabled on node startup at the 'HttpPort'", DefaultValue = "false")]
        bool WebSocketsEnabled { get; set; }
        
        [ConfigItem(Description = "If 'false' then the node does not try to find nodes beyond the bootnodes configured.", DefaultValue = "true")]
        bool DiscoveryEnabled { get; set; }
        
        [ConfigItem(Description = "If 'false' then the node does not download/process new blocks..", DefaultValue = "true")]
        bool SynchronizationEnabled { get; set; }
        
        [ConfigItem(Description = "If 'false' then the node does not download/process new blocks..", DefaultValue = "true")]
        bool ProcessingEnabled { get; set; }
        bool PeerManagerEnabled { get; set; }
        bool IsMining { get; set; }
        string HttpHost { get; set; }
        int HttpPort { get; set; }
        int DiscoveryPort { get; set; }
        int P2PPort { get; set; }
        string ChainSpecPath { get; set; }
        string ChainSpecFormat { get; set; }
        string BaseDbPath { get; set; }
        string GenesisHash { get; set; }
        
        [ConfigItem(DefaultValue = "Data/static-nodes.json")]
        string StaticNodesPath { get; set; }
        bool RemovingLogFilesEnabled { get; set; }
        string LogFileName { get; set; }
        
        [ConfigItem(Description = "In case of null, the path is set to [applicationDirectiory]\\logs", DefaultValue = "null")]
        string LogDirectory { get; set; }
        bool StoreTraces { get; set; }
        bool StoreReceipts { get; set; }
    }
}