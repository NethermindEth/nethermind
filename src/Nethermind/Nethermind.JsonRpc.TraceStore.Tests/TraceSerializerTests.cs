using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Nethermind.Evm.Tracing.ParityStyle;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.TraceStore.Tests;

public class TraceSerializerTests
{
    [Test]
    public void can_deserialize_deep_graph()
    {
        List<ParityLikeTxTrace>? traces = Deserialize();
        traces?.Count.Should().Be(36);
    }

    [Test]
    public void cant_deserialize_deep_graph()
    {
        TraceSerializer.MaxDepth = 128;
        Func<List<ParityLikeTxTrace>?> traces = Deserialize;
        traces.Should().Throw<JsonReaderException>();
    }

    private List<ParityLikeTxTrace>? Deserialize()
    {
        Type type = GetType();
        using Stream stream = type.Assembly.GetManifestResourceStream(type.Namespace + ".xdai-17600039.json")!;
        List<ParityLikeTxTrace>? traces = TraceSerializer.Deserialize(stream);
        return traces;
    }
}
