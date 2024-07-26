// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Evm.Tracing.ParityStyle;

namespace Nethermind.JsonRpc.TraceStore;

public interface ITraceStoreConfig : IConfig
{
    [ConfigItem(Description = "Whether to enable the TraceStore plugin. If enabled, traces come from the database whenever possible.", DefaultValue = "false")]
    public bool Enabled { get; set; }

    [ConfigItem(Description = "The number of blocks to store, counting from the head. If `0`, all traces of the processed blocks are stored.", DefaultValue = "10000")]
    public int BlocksToKeep { get; set; }

    [ConfigItem(Description = "The type of traces to store.", DefaultValue = "Trace, Rewards")]
    public ParityTraceTypes TraceTypes { get; set; }

    [ConfigItem(Description = "Whether to verify all serialized elements.", DefaultValue = "false", HiddenFromDocs = true)]
    bool VerifySerialized { get; set; }

    [ConfigItem(Description = "The max depth allowed when deserializing traces.", DefaultValue = "3200", HiddenFromDocs = true)]
    int MaxDepth { get; set; }

    [ConfigItem(Description = "The max parallelization when deserialization requests the `trace_filter` method. `0` to use the number of logical processors. If you experience a resource shortage, set to a low number.", DefaultValue = "0")]
    int DeserializationParallelization { get; set; }
}
