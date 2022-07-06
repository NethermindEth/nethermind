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

using System;
using Nethermind.Consensus.Processing;

namespace Nethermind.Api
{
    public class InitConfig : IInitConfig
    {
        public bool EnableUnsecuredDevWallet { get; set; } = false;
        public bool KeepDevWalletInMemory { get; set; } = false;
        public bool WebSocketsEnabled { get; set; } = true;
        public bool DiscoveryEnabled { get; set; } = true;
        public bool ProcessingEnabled { get; set; } = true;
        public bool PeerManagerEnabled { get; set; } = true;
        public bool IsMining { get; set; } = false;
        public string ChainSpecPath { get; set; } = "chainspec/foundation.json";
        public string HiveChainSpecPath { get; set; } = "chainspec/test.json";
        public string BaseDbPath { get; set; } = "db";
        public string LogFileName { get; set; } = "log.txt";
        public string? GenesisHash { get; set; }
        public string StaticNodesPath { get; set; } = "Data/static-nodes.json";
        public string LogDirectory { get; set; } = "logs";
        public string? LogRules { get; set; } = null;
        public bool StoreReceipts { get; set; } = true;
        public bool ReceiptsMigration { get; set; } = false;
        public DiagnosticMode DiagnosticMode { get; set; } = DiagnosticMode.None;
        public DumpOptions AutoDump { get; set; } = DumpOptions.Receipts;

        public string RpcDbUrl { get; set; } = String.Empty;
        public long? MemoryHint { get; set; }

        [Obsolete("Use DiagnosticMode with MemDb instead")]
        public bool UseMemDb
        {
            get => DiagnosticMode == DiagnosticMode.MemDb;
            // ReSharper disable once ValueParameterNotUsed
            set => DiagnosticMode = DiagnosticMode.MemDb;
        }
    }
}
