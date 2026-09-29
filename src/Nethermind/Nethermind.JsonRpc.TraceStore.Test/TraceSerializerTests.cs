// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Logging;

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

    private List<ParityLikeTxTrace>? Deserialize(ITraceSerializer<ParityLikeTxTrace> serializer)
    {
        Type type = GetType();
        using Stream stream = type.Assembly.GetManifestResourceStream($"{type.Assembly.GetName().Name}.xdai-17600039.json")!;
        List<ParityLikeTxTrace>? traces = serializer.Deserialize(stream);
        return traces;
    }
}
