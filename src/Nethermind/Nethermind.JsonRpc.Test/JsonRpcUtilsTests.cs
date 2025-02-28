// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Evm.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Runner.JsonRpc;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

public class JsonRpcUtilsTests
{
    [Test]
    public async Task MultiParseJsonDocument_CanParseMultipleJsonDocuments()
    {
        Pipe pipe = new Pipe();
        byte[] theJson = "{}{}{}"u8.ToArray();
        _ = Task.Run(async () =>
        {
            await pipe.Writer.WriteAsync(theJson);
            await pipe.Writer.CompleteAsync();
        });

        (await JsonRpcUtils.MultiParseJsonDocument(pipe.Reader, default).CountAsync()).Should().Be(3);
    }

    [Test]
    public async Task MultiParseJsonDocument_AdvanceReader_MidIteration()
    {
        Pipe pipe = new Pipe();
        byte[] theJson = "{}{}{}"u8.ToArray();
        CountingPipeReader reader = new CountingPipeReader(pipe.Reader);
        _ = pipe.Writer.WriteAsync(theJson);

        var enumerator = JsonRpcUtils.MultiParseJsonDocument(reader, default).GetAsyncEnumerator();
        await enumerator.MoveNextAsync();
        reader.Length.Should().Be(2);
    }
}
