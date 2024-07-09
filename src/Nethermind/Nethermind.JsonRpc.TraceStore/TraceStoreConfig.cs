// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.TraceStore;

public class TraceStoreConfig : ITraceStoreConfig
{
    public bool Enabled { get; set; }
    public int BlocksToKeep { get; set; } = 10000;
    public ParityTraceTypes TraceTypes { get; set; } = ParityTraceTypes.Trace | ParityTraceTypes.Rewards;
    public bool VerifySerialized { get; set; } = false;
    public int MaxDepth { get; set; } = ParityLikeTraceSerializer.DefaultDepth;
    public int DeserializationParallelization { get; set; } = 0;
}
