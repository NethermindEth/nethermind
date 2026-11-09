// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Blockchain.Tracing.ParityStyle;

namespace Nethermind.JsonRpc.TraceStore;

public interface ITraceStoreConfig : IConfig
{
    [ConfigItem(Description = "Whether to enable the TraceStore plugin. If enabled, traces come from the database whenever possible.", DefaultValue = "false")]
    public bool Enabled { get; set; }

    [ConfigItem(Description = "The number of blocks to store, counting from the head. If `0`, all traces of the processed blocks are stored.", DefaultValue = "10000")]
    public ulong BlocksToKeep { get; set; }

    [ConfigItem(Description = "The types of traces to store. Requests for unrecorded types fall back to live tracing and require available historical state. `trace_block` and `trace_filter` require both `Trace` and `Rewards` to use the store. Transaction replays requesting `Rewards` always trace live because stored transaction traces omit block rewards.", DefaultValue = "Trace, Rewards")]
    public ParityTraceTypes TraceTypes { get; set; }

    /// <summary>Whether to enable persistent OTS address indexing. Defaults to false.</summary>
    [ConfigItem(Description = "Whether to build a separate trace-address index for OTS history. Requires BlocksToKeep=0 and TraceTypes including Trace. Uncovered history is scanned.", DefaultValue = "false")]
    bool AddressIndexEnabled { get; set; }

    /// <summary>The first indexed block number, inclusive. Defaults to 1.</summary>
    [ConfigItem(Description = "The first block to index. Earlier history is scanned, not assumed empty.", DefaultValue = "1")]
    int AddressIndexFromBlock { get; set; }

    /// <summary>The indexing delay behind the head, measured in blocks. Defaults to 64.</summary>
    [ConfigItem(Description = "The number of blocks behind the head at which trace-address indexing stops. This is not a finality guarantee.", DefaultValue = "64")]
    int AddressIndexLag { get; set; }

    /// <summary>Whether to clear only the address index on startup. Defaults to false.</summary>
    [ConfigItem(Description = "Whether to reset only the trace-address index on startup. Use once after checkpoint invalidation, then disable it.", DefaultValue = "false")]
    bool AddressIndexReset { get; set; }

    [ConfigItem(Description = "Whether to verify all serialized elements.", DefaultValue = "false", HiddenFromDocs = true)]
    bool VerifySerialized { get; set; }

    [ConfigItem(Description = "The max depth allowed when deserializing traces.", DefaultValue = "3200", HiddenFromDocs = true)]
    int MaxDepth { get; set; }

    [ConfigItem(Description = "The number of concurrent instances of the OTS RPC module. Values below 1 use 1. Raising this limit increases concurrent historical scans and their CPU, I/O and memory load.", DefaultValue = "2")]
    int OtsModuleConcurrentInstances { get; set; }

    [ConfigItem(Description = "The max parallelization when deserialization requests the `trace_filter` method. `0` to use the number of logical processors. If you experience a resource shortage, set to a low number.", DefaultValue = "0")]
    int DeserializationParallelization { get; set; }
}
