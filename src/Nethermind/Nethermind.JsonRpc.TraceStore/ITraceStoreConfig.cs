// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Evm.Tracing.ParityStyle;

namespace Nethermind.JsonRpc.TraceStore;

public interface ITraceStoreConfig : IConfig
{
    [ConfigItem(Description = "Defines whether the TraceStore plugin is enabled, if 'true' traces will come from DB if possible.", DefaultValue = "false")]
    public bool Enabled { get; set; }

    [ConfigItem(Description = "Defines how many blocks counting from head are kept in the TraceStore, if '0' all traces of processed blocks will be kept.", DefaultValue = "10000")]
    public int BlocksToKeep { get; set; }

    [ConfigItem(Description = "Defines what kind of traces are saved and kept in TraceStore. Available options are: Trace, Rewards, VmTrace, StateDiff or just All.", DefaultValue = "Trace, Rewards")]
    public ParityTraceTypes TraceTypes { get; set; }

    [ConfigItem(Description = "Verifies all the serialized elements.", DefaultValue = "false", HiddenFromDocs = true)]
    bool VerifySerialized { get; set; }

    [ConfigItem(Description = "Depth to deserialize traces.", DefaultValue = "1024", HiddenFromDocs = true)]
    int MaxDepth { get; set; }

    [ConfigItem(Description = "Maximum parallelization when deserializing requests for trace_filter. 0 defaults to logical cores, set to something low if you experience too big resource usage.", DefaultValue = "0")]
    int DeserializationParallelization { get; set; }
}
