// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Tools.Kute.MessageProvider;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Tools.Kute.Test;

public class MessageProviderTests
{
    [Test]
    public async Task CanDeserializeJSONAsync()
    {
        string lines = """
        {"jsonrpc":"2.0","id":1,"result":"0x123"}
        {"jsonrpc":"2.0","id":2,"error":{"code":-32601,"message":"Method not found"}}
        [{"jsonrpc":"2.0","id":3,"result":"0x456"},{"jsonrpc":"2.0","id":4,"error":{"code":-32602,"message":"Invalid params"}}]
        """;

        IMessageProvider<string> stringProvider = Substitute.For<IMessageProvider<string>>();
        stringProvider.Messages().Returns(lines.Split('\n').ToAsyncEnumerable());

        JsonRpcMessageProvider provider = new(stringProvider);
        List<JsonRpc> jsonRpcs = await provider.Messages().ToListAsync();

        Assert.That(jsonRpcs, Has.Count.EqualTo(3));
        Assert.That(jsonRpcs[0], Is.TypeOf<JsonRpc.Request.Single>());
        Assert.That(jsonRpcs[1], Is.TypeOf<JsonRpc.Request.Single>());
        Assert.That(jsonRpcs[2], Is.TypeOf<JsonRpc.Request.Batch>());
    }

    [Test]
    public async Task CanUnwrapBatches()
    {
        string lines = """
        {"jsonrpc":"2.0","id":3,"result":"0x789"}
        [{"jsonrpc":"2.0","id":1,"result":"0x123"},{"jsonrpc":"2.0","id":2,"result":"0x456"}]
        """;

        IMessageProvider<string> stringProvider = Substitute.For<IMessageProvider<string>>();
        stringProvider.Messages().Returns(lines.Split('\n').ToAsyncEnumerable());

        UnwrapBatchJsonRpcMessageProvider provider = new(new JsonRpcMessageProvider(stringProvider));
        List<JsonRpc> jsonRpcs = await provider.Messages().ToListAsync();

        Assert.That(jsonRpcs, Has.Count.EqualTo(3));
        Assert.That(jsonRpcs[0], Is.TypeOf<JsonRpc.Request.Single>());
        Assert.That(jsonRpcs[1], Is.TypeOf<JsonRpc.Request.Single>());
        Assert.That(jsonRpcs[2], Is.TypeOf<JsonRpc.Request.Single>());
    }
}
