// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Consensus.Processing;
using System.ComponentModel;

namespace Nethermind.Api;

public interface IInitConfig : IConfig
{
    [ConfigItem(Description = "Whether to enable the in-app wallet/keystore.", DefaultValue = "false")]
    bool EnableUnsecuredDevWallet { get; set; }

    [ConfigItem(Description = "Whether to create session-only accounts and delete them on shutdown.", DefaultValue = "false")]
    bool KeepDevWalletInMemory { get; set; }

    [ConfigItem(Description = "Whether to enable WebSocket service for the default JSON-RPC port on startup.", DefaultValue = "true")]
    bool WebSocketsEnabled { get; set; }

    [ConfigItem(Description = "Whether to enable the node discovery. If disabled, Nethermind doesn't look for other nodes beyond the bootnodes specified.", DefaultValue = "true")]
    bool DiscoveryEnabled { get; set; }

    [ConfigItem(Description = "Whether to download/process new blocks.", DefaultValue = "true")]
    bool ProcessingEnabled { get; set; }

    [ConfigItem(Description = "Whether to connect to newly discovered peers.", DefaultValue = "true")]
    bool PeerManagerEnabled { get; set; }

    [ConfigItem(Description = "Whether to seal/mine new blocks.", DefaultValue = "false")]
    bool IsMining { get; set; }

    [ConfigItem(Description = "The path to the chain spec file.", DefaultValue = "chainspec/foundation.json")]
    string ChainSpecPath { get; set; }

    [ConfigItem(Description = "The base path for all Nethermind databases.", DefaultValue = "db")]
    string BaseDbPath { get; set; }

    [ConfigItem(Description = "The path to KZG trusted setup file.", DefaultValue = "null")]
    string? KzgSetupPath { get; set; }

    [ConfigItem(Description = "The hash of the genesis block. If not specified, the genesis block validity is not checked which is useful in the case of ad hoc test/private networks.", DefaultValue = "null")]
    string? GenesisHash { get; set; }

    [ConfigItem(Description = "The path to the static nodes file.", DefaultValue = "Data/static-nodes.json")]
    string StaticNodesPath { get; set; }

    [ConfigItem(Description = "The path to the trusted nodes file.", DefaultValue = "Data/trusted-nodes.json")]
    string TrustedNodesPath { get; set; }

    [ConfigItem(Description = "The name of the log file.", DefaultValue = "log.txt")]
    string LogFileName { get; set; }

    [ConfigItem(Description = "The path to the Nethermind logs directory.", DefaultValue = "logs")]
    string LogDirectory { get; set; }

    [ConfigItem(Description = "The logs format as `LogPath:LogLevel;*`", DefaultValue = "null")]
    string? LogRules { get; set; }

    [ConfigItem(Description = "Moved to ReceiptConfig.", DefaultValue = "true", HiddenFromDocs = true)]
    bool StoreReceipts { get; set; }

    [ConfigItem(Description = "Moved to ReceiptConfig.", DefaultValue = "false", HiddenFromDocs = true)]
    bool ReceiptsMigration { get; set; }

    [ConfigItem(Description = "The diagnostic mode.", DefaultValue = "None")]
    DiagnosticMode DiagnosticMode { get; set; }

    [ConfigItem(Description = "Auto-dump on bad blocks for diagnostics.", DefaultValue = nameof(DumpOptions.Default))]
    DumpOptions AutoDump { get; set; }

    [ConfigItem(Description = $"The URL of the remote node used as a database source when `{nameof(DiagnosticMode)}` is set to `RpcDb`.", DefaultValue = "")]
    string RpcDbUrl { get; set; }

    [ConfigItem(Description = "The hint on the max memory limit, in bytes, to configure the database and networking memory allocations.", DefaultValue = "null")]
    long? MemoryHint { get; set; }

    [ConfigItem(Description = "The maximum number of bad blocks observed on the network that will be stored on disk.", DefaultValue = "100")]
    long? BadBlocksStored { get; set; }

    [ConfigItem(Description = "[TECHNICAL] Disable garbage collector on newPayload", DefaultValue = "true", HiddenFromDocs = true)]
    bool DisableGcOnNewPayload { get; set; }

    [ConfigItem(Description = "[TECHNICAL] Disable setting malloc options. Set to true if using different memory allocator or manually setting malloc opts.", DefaultValue = "false", HiddenFromDocs = true)]
    bool DisableMallocOpts { get; set; }

    [ConfigItem(Description = "[TECHNICAL] Key scheme for state db. Only effect new db.", DefaultValue = "Current", HiddenFromDocs = true)]
    INodeStorage.KeyScheme StateDbKeyScheme { get; set; }

    [ConfigItem(Description = "[TECHNICAL] Exit when block number is reached. Useful for scripting and testing.", DefaultValue = "null", HiddenFromDocs = true)]
    long? ExitOnBlockNumber { get; set; }

    [ConfigItem(Description = "[TECHNICAL] Specify concurrency limit for background task.", DefaultValue = "1", HiddenFromDocs = true)]
    int BackgroundTaskConcurrency { get; set; }

    [ConfigItem(Description = "[TECHNICAL] Specify max number of background task.", DefaultValue = "1024", HiddenFromDocs = true)]
    int BackgroundTaskMaxNumber { get; set; }
}

public enum DiagnosticMode
{
    [Description("None.")]
    None,

    [Description("Uses an in-memory DB.")]
    MemDb,

    [Description("Uses a remote DB.")]
    RpcDb,

    [Description("Uses a read-only DB.")]
    ReadOnlyDb,

    [Description("Scans rewards for blocks and genesis.")]
    VerifyRewards,

    [Description("Scans and sums supply on all accounts.")]
    VerifySupply,

    [Description("Verifies if full state trie is stored.")]
    VerifyTrie
}
