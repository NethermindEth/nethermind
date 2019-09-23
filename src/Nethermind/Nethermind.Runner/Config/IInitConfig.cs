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
        [ConfigItem(Description = "If 'true' then it enables the wallet / key store in the application.", DefaultValue = "false")]
        bool EnableUnsecuredDevWallet { get; set; }
        
        [ConfigItem(Description = "If 'true' then any accounts created will be only valid during the session and deleted when application closes.", DefaultValue = "false")]
        bool KeepDevWalletInMemory{ get; set; }

        [ConfigItem(Description = "Defines whether the WebSockets service is enabled on node startup at the 'HttpPort'", DefaultValue = "false")]
        bool WebSocketsEnabled { get; set; }
        
        [ConfigItem(Description = "If 'false' then the node does not try to find nodes beyond the bootnodes configured.", DefaultValue = "true")]
        bool DiscoveryEnabled { get; set; }
        
        [ConfigItem(Description = "If 'false' then the node does not download/process new blocks..", DefaultValue = "true")]
        bool SynchronizationEnabled { get; set; }
        
        [ConfigItem(Description = "If 'false' then the node does not download/process new blocks..", DefaultValue = "true")]
        bool ProcessingEnabled { get; set; }
        
        [ConfigItem(Description = "If 'false' then the node does not connect to newly discovered peers..", DefaultValue = "true")]
        bool PeerManagerEnabled { get; set; }
        
        [ConfigItem(Description = "If 'true' then the node will try to seal/mine new blocks", DefaultValue = "false")]
        bool IsMining { get; set; }

        [ConfigItem(Description = "Path to the chain definition file (Parity chainspec or Geth genesis file).", DefaultValue = "null")]
        string ChainSpecPath { get; set; }
        
        [ConfigItem(Description = "Format of the chain definition file - genesis (Geth style - not tested recently / may fail) or chainspec (Parity style).", DefaultValue = "\"chainspec\"")]
        string ChainSpecFormat { get; set; }
        
        [ConfigItem(Description = "Base directoy path for all the nethermind databases.", DefaultValue = "\"db\"")]
        string BaseDbPath { get; set; }
        
        [ConfigItem(Description = "Hash of the genesis block - if the default null value is left then the genesis block validity will not be checked which is useful for ad hoc test/private networks.", DefaultValue = "null")]
        string GenesisHash { get; set; }
        
        [ConfigItem(Description = "Path to the file with a list of static nodes.", DefaultValue = "\"Data/static-nodes.json\"")]
        string StaticNodesPath { get; set; }
  
        [ConfigItem(Description = "Name of the log file generated (useful when launching multiple networks with the same log folder).", DefaultValue = "\"log.txt\"")]
        string LogFileName { get; set; }
        
        [ConfigItem(Description = "In case of null, the path is set to [applicationDirectiory]\\logs", DefaultValue = "null")]
        string LogDirectory { get; set; }
        
        [ConfigItem(Description = "If set to 'true' then the detailed VM trace data will be stored in teh DB (huge data sets).", DefaultValue = "false")]
        bool StoreTraces { get; set; }
        
        [ConfigItem(Description = "If set to 'false' then transaction receipts will not be stored in the database.", DefaultValue = "true")]
        bool StoreReceipts { get; set; }
        
        bool EnableRc7Fix { get; set; }
    }
}