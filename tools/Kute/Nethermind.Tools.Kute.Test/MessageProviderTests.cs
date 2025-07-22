// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Tools.Kute.MessageProvider;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Tools.Kute.Test;

public class MessageProviderTests
{
    [Test]
    public async Task CanDeserializeJSONAsync()
    {
        var lines = """
        {"jsonrpc":"2.0","id":1,"result":"0x123"}
        {"jsonrpc":"2.0","id":2,"error":{"code":-32601,"message":"Method not found"}}
        [{"jsonrpc":"2.0","id":3,"result":"0x456"},{"jsonrpc":"2.0","id":4,"error":{"code":-32602,"message":"Invalid params"}}]
        """;

        var stringProvider = Substitute.For<IMessageProvider<string>>();
        stringProvider.Messages().Returns(lines.Split('\n').ToAsyncEnumerable());

        var provider = new JsonRpcMessageProvider(stringProvider);
        var jsonRpcs = await provider.Messages().ToListAsync();

        jsonRpcs.Should().HaveCount(3);
        jsonRpcs[0].Should().BeOfType<JsonRpc.Request.Single>();
        jsonRpcs[1].Should().BeOfType<JsonRpc.Request.Single>();
        jsonRpcs[2].Should().BeOfType<JsonRpc.Request.Batch>();
    }

    [Test]
    public async Task CanUnwrapBatches()
    {
        var lines = """
        {"jsonrpc":"2.0","id":3,"result":"0x789"}
        [{"jsonrpc":"2.0","id":1,"result":"0x123"},{"jsonrpc":"2.0","id":2,"result":"0x456"}]
        """;

        var stringProvider = Substitute.For<IMessageProvider<string>>();
        stringProvider.Messages().Returns(lines.Split('\n').ToAsyncEnumerable());

        var provider = new UnwrapBatchJsonRpcMessageProvider(new JsonRpcMessageProvider(stringProvider));
        var jsonRpcs = await provider.Messages().ToListAsync();

        jsonRpcs.Should().HaveCount(3);
        jsonRpcs[0].Should().BeOfType<JsonRpc.Request.Single>();
        jsonRpcs[1].Should().BeOfType<JsonRpc.Request.Single>();
        jsonRpcs[2].Should().BeOfType<JsonRpc.Request.Single>();
    }
}
