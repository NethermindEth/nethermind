// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.DebugModule;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public class ProofWrapperDebugRpcModuleTests
{
    [Test]
    public async Task Fails_while_recording_is_disabled()
    {
        string response = await RpcTest.TestSerializedRequest<IProofWrapperDebugRpcModule>(
            new ProofWrapperDebugRpcModule(ProofWrapperEventFeed.Disabled), "debug_proofWrapperEvents");
        Assert.That(response, Does.Contain("Network.ProofWrapperEventBufferSize"));
    }

    [Test]
    public async Task Returns_events_after_the_cursor_within_the_limit()
    {
        ProofWrapperEventFeed feed = new(4);
        for (int i = 0; i < 6; i++)
            feed.Record(new() { Type = ProofWrapperEventTypes.WrapperRequested, Wrapper = TestItem.KeccakA, Peer = "peer", Message = "GetProofWrappers", RequestId = i });
        string response = await RpcTest.TestSerializedRequest<IProofWrapperDebugRpcModule>(
            new ProofWrapperDebugRpcModule(feed), "debug_proofWrapperEvents", "0x3", "0x2");
        JsonElement result = JsonDocument.Parse(response).RootElement.GetProperty("result");
        JsonElement events = result.GetProperty("events");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("latest").GetString(), Is.EqualTo("0x6"));
            Assert.That(result.GetProperty("missed").GetString(), Is.EqualTo("0x0"));
            Assert.That(result.GetProperty("capacity").GetInt32(), Is.EqualTo(4));
            Assert.That(events.GetArrayLength(), Is.EqualTo(2));
            Assert.That(events[0].GetProperty("seq").GetString(), Is.EqualTo("0x4"));
            Assert.That(events[0].GetProperty("type").GetString(), Is.EqualTo("wrapper-requested"));
            Assert.That(events[0].GetProperty("wrapper").GetString(), Is.EqualTo(TestItem.KeccakA.ToString()));
            Assert.That(events[0].GetProperty("requestId").GetString(), Is.EqualTo("0x3"));
            Assert.That(events[0].TryGetProperty("verdict", out _), Is.False, "unset fields are omitted");
        }
    }
}
