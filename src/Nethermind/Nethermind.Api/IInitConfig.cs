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

using Nethermind.Config;

namespace Nethermind.Api
{
    public interface IInitConfig : IConfig
    {
        [ConfigItem(Description = "If 'true' then it enables the wallet / key store in the application.", DefaultValue = "false")]
        bool EnableUnsecuredDevWallet { get; set; }
        
        [ConfigItem(Description = "If 'true' then any accounts created will be only valid during the session and deleted when application closes.", DefaultValue = "false")]
        bool KeepDevWalletInMemory{ get; set; }

        [ConfigItem(Description = "Defines whether the WebSockets service is enabled on node startup at the 'HttpPort' - e.g. ws://localhost:8545/ws/json-rpc", DefaultValue = "false")]
        bool WebSocketsEnabled { get; set; }
        
        [ConfigItem(Description = "If 'false' then the node does not try to find nodes beyond the bootnodes configured.", DefaultValue = "true")]
        bool DiscoveryEnabled { get; set; }

        [ConfigItem(Description = "If 'false' then the node does not download/process new blocks..", DefaultValue = "true")]
        bool ProcessingEnabled { get; set; }
        
        [ConfigItem(Description = "If 'false' then the node does not connect to newly discovered peers..", DefaultValue = "true")]
        bool PeerManagerEnabled { get; set; }
        
        [ConfigItem(Description = "If 'true' then the node will try to seal/mine new blocks", DefaultValue = "false")]
        bool IsMining { get; set; }

        [ConfigItem(Description = "Path to the chain definition file (Parity chainspec or Geth genesis file).", DefaultValue = "chainspec/foundation.json")]
        string ChainSpecPath { get; set; }

        [ConfigItem(Description = "Path to the chain definition file created by Hive for test purpouse", DefaultValue="chainspec/test.json")]
        string HiveChainSpecPath { get; set; }

        [ConfigItem(Description = "Base directoy path for all the nethermind databases.", DefaultValue = "\"db\"")]
        string BaseDbPath { get; set; }
        
        [ConfigItem(Description = "Hash of the genesis block - if the default null value is left then the genesis block validity will not be checked which is useful for ad hoc test/private networks.", DefaultValue = "null")]
        string? GenesisHash { get; set; }
        
        [ConfigItem(Description = "Path to the file with a list of static nodes.", DefaultValue = "\"Data/static-nodes.json\"")]
        string StaticNodesPath { get; set; }
  
        [ConfigItem(Description = "Name of the log file generated (useful when launching multiple networks with the same log folder).", DefaultValue = "\"log.txt\"")]
        string LogFileName { get; set; }
        
        [ConfigItem(Description = "In case of null, the path is set to [applicationDirectiory]\\logs", DefaultValue = "logs")]
        string LogDirectory { get; set; }

        [ConfigItem(Description = "If set to 'false' then transaction receipts will not be stored in the database after a new block is processed. This setting is independent from downloading receipts in fast sync mode.", DefaultValue = "true")]
        bool StoreReceipts { get; set; }
        
        [ConfigItem(Description = "If set to 'true' then receipts db will be migrated to new schema.", DefaultValue = "false")]
        bool ReceiptsMigration { get; set; }
        
        [ConfigItem(Description = "Diagnostics modes", DefaultValue = "None")]
        DiagnosticMode DiagnosticMode { get; set; }
        
        [ConfigItem(Description = "Url for remote node that will be used as DB source when 'DiagnosticMode' is set to'RpcDb'", DefaultValue = "")]
        string RpcDbUrl { get; set; }
        
        [ConfigItem(Description = "A hint for the max memory that will allow us to configure the DB and Netty memory allocations.", DefaultValue = "null")]
        long? MemoryHint { get; set; }
    }
    
    public enum DiagnosticMode
    {
        None,
        [ConfigItem(Description = "Diagnostics mode which uses an in-memory DB")]
        MemDb,
        [ConfigItem(Description = "Diagnostics mode which uses a remote DB")]
        RpcDb,
        [ConfigItem(Description = "Diagnostics mode which uses a read-only DB")]
        ReadOnlyDb,
        [ConfigItem(Description = "Just scan rewards for blocks + genesis")]
        VerifyRewards,
        [ConfigItem(Description = "Just scan and sum supply on all accounts")]
        VerifySupply,
        [ConfigItem(Description = "Verifies if full state is stored")]
        VerifyTrie
    }
}
