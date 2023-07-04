// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Logging;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.TraceStore.Tests;

public class TraceSerializerTests
{
    [Test]
    public void can_deserialize_deep_graph()
    {
        List<ParityLikeTxTrace>? traces = Deserialize(new ParityLikeTraceSerializer(LimboLogs.Instance));
        traces?.Count.Should().Be(36);
    }

    [Test]
    public void cant_deserialize_deep_graph()
    {
        Func<List<ParityLikeTxTrace>?> traces = () => Deserialize(new ParityLikeTraceSerializer(LimboLogs.Instance, 128));
        traces.Should().Throw<JsonReaderException>();
    }

    private List<ParityLikeTxTrace>? Deserialize(ITraceSerializer<ParityLikeTxTrace> serializer)
    {
        Type type = GetType();
        using Stream stream = type.Assembly.GetManifestResourceStream($"{type.Assembly.GetName().Name}.xdai-17600039.json")!;
        List<ParityLikeTxTrace>? traces = serializer.Deserialize(stream);
        return traces;
    }
}
