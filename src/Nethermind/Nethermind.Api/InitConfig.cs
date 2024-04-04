// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Processing;
using Nethermind.Trie;

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
        public string? KzgSetupPath { get; set; } = null;
        public string LogDirectory { get; set; } = "logs";
        public string? LogRules { get; set; } = null;
        public bool StoreReceipts { get; set; } = true;
        public bool ReceiptsMigration { get; set; } = false;
        public DiagnosticMode DiagnosticMode { get; set; } = DiagnosticMode.None;
        public DumpOptions AutoDump { get; set; } = DumpOptions.Default;

        public string RpcDbUrl { get; set; } = String.Empty;
        public long? MemoryHint { get; set; }
        public long? BadBlocksStored { get; set; } = 100;
        public bool DisableGcOnNewPayload { get; set; } = true;
        public bool DisableMallocOpts { get; set; } = false;
        public INodeStorage.KeyScheme StateDbKeyScheme { get; set; } = INodeStorage.KeyScheme.Current;
        public long? ExitOnBlockNumber { get; set; } = null;
        public int BackgroundTaskConcurrency { get; set; } = 1;

        [Obsolete("Use DiagnosticMode with MemDb instead")]
        public bool UseMemDb
        {
            get => DiagnosticMode == DiagnosticMode.MemDb;
            // ReSharper disable once ValueParameterNotUsed
            set => DiagnosticMode = DiagnosticMode.MemDb;
        }
    }
}
