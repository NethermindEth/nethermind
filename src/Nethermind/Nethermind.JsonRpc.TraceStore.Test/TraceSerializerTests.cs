// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Logging;
using Nethermind.Core;
using Nethermind.Serialization.Json;
using Nethermind.Int256;

using NUnit.Framework;

namespace Nethermind.JsonRpc.TraceStore.Test;

public class TraceSerializerTests
{
    [Test]
    public void can_deserialize_deep_graph()
    {
        List<ParityLikeTxTrace>? traces = Deserialize(new ParityLikeTraceSerializer(LimboLogs.Instance));
        Assert.That(traces?.Count, Is.EqualTo(36));
    }

    [Test]
    public void cant_deserialize_deep_graph()
    {
        Func<List<ParityLikeTxTrace>?> traces = () => Deserialize(new ParityLikeTraceSerializer(LimboLogs.Instance, 128));
        Assert.That(traces, Throws.TypeOf<JsonException>());
    }

    [Test]
    public void round_trips_reverted_frame_result()
    {
        ParityLikeTraceSerializer serializer = new(LimboLogs.Instance);
        ParityLikeTxTrace trace = new()
        {
            Action = new ParityTraceAction
            {
                Type = "create",
                CallType = "create",
                Error = "Reverted",
                Result = new ParityTraceResult { GasUsed = 0x11, Output = [0xde, 0xad, 0xbe, 0xef] },
            },
        };

        ParityTraceAction action = serializer.Deserialize(serializer.Serialize([trace]))![0].Action!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(action.Error, Is.EqualTo("Reverted"));
            Assert.That(action.Result?.GasUsed, Is.EqualTo(0x11UL));
            Assert.That(action.Result?.Output, Is.EqualTo(new byte[] { 0xde, 0xad, 0xbe, 0xef }));
            Assert.That(action.Result?.Address, Is.Null);
        }
    }

    [Test]
    public void round_trips_stored_trace_families([Values("trace", "empty-state", "vm", "vm-op", "state", "state-storage")] string family)
    {
        ParityLikeTraceSerializer serializer = new(LimboLogs.Instance);
        ParityLikeTxTrace trace = new()
        {
            Output = [],
            Action = new ParityTraceAction
            {
                Type = "call",
                CallType = "call",
                From = Address.Zero,
                To = Address.Zero,
                Result = new ParityTraceResult { GasUsed = 1, Output = [] },
            },
        };
        if (family is "vm" or "vm-op")
        {
            trace.VmTrace = new ParityVmTrace { Code = [0x00], Operations = [] };
            if (family == "vm-op")
            {
                trace.VmTrace.Operations = [new ParityVmOperationTrace
                {
                    Cost = 3, Pc = 0, Used = 2,
                    Memory = new ParityMemoryChangeTrace { Offset = 1, Data = [0x01] },
                    Push = [[0x02]],
                    Store = new ParityStorageChangeTrace { Key = [0x03], Value = [0x04] },
                    Sub = new ParityVmTrace { Code = [0x05], Operations = [] },
                }, new ParityVmOperationTrace { Cost = 1, Pc = 1, Halted = true }];
            }
        }
        if (family == "empty-state")
        {
            trace.StateChanges = [];
        }
        if (family is "state" or "state-storage")
        {
            trace.StateChanges = new Dictionary<Address, ParityAccountStateChange>
            {
                [Address.Zero] = new()
                {
                    Code = new ParityStateChange<byte[]>([0x00], [0x01]),
                    Balance = new ParityStateChange<UInt256?>(new UInt256(1), new UInt256(2)),
                    Nonce = new ParityStateChange<UInt256?>(new UInt256(3), new UInt256(4)),
                    Storage = family == "state-storage" ? new Dictionary<UInt256, ParityStateChange<byte[]>>
                    {
                        [new UInt256(1)] = new([0x01], [0x02]),
                    } : null,
                },
            };
        }

        byte[] stored = serializer.Serialize([trace]);
        List<ParityLikeTxTrace>? restored = serializer.Deserialize(stored);
        EthereumJsonSerializer json = new();
        Assert.That(json.Serialize(restored), Is.EqualTo(json.Serialize(new[] { trace })));
    }

    private List<ParityLikeTxTrace>? Deserialize(ITraceSerializer<ParityLikeTxTrace> serializer)
    {
        Type type = GetType();
        using Stream stream = type.Assembly.GetManifestResourceStream($"{type.Assembly.GetName().Name}.xdai-17600039.json")!;
        List<ParityLikeTxTrace>? traces = serializer.Deserialize(stream);
        return traces;
    }
}
