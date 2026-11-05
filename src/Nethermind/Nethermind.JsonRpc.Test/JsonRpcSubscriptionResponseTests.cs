// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc.Modules.Subscribe;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

/// <summary>
/// Subscription payloads are declared as the base RPC model but are routinely a plugin-supplied subtype
/// (an engine-specific <c>BlockForRpc</c>, say), so the writer has to serialize at the runtime type the
/// way <see cref="ResultWrapper{T}"/> does for method results.
/// </summary>
[TestFixture]
public class JsonRpcSubscriptionResponseTests
{
    private class Payload
    {
        public string Declared { get; set; } = "declared";
    }

    private sealed class DerivedPayload : Payload
    {
        public string Added { get; set; } = "added";
    }

    [Test]
    public void Derived_payload_keeps_the_properties_its_subtype_adds()
    {
        JsonRpcSubscriptionResponse<Payload> response = new()
        {
            MethodName = SubscriptionMethodName.EthSubscription,
            Params = new JsonRpcSubscriptionResult<Payload> { Result = new DerivedPayload(), Subscription = "0x1" }
        };

        string serialized = RpcTest.SerializeResponse(response);

        Assert.That(serialized, Does.Contain("\"added\":\"added\""));
        Assert.That(serialized, Does.Contain("\"declared\":\"declared\""));
    }

    [Test]
    public void Payload_of_the_declared_type_is_unaffected()
    {
        JsonRpcSubscriptionResponse<Payload> response = new()
        {
            MethodName = SubscriptionMethodName.EthSubscription,
            Params = new JsonRpcSubscriptionResult<Payload> { Result = new Payload(), Subscription = "0x1" }
        };

        Assert.That(RpcTest.SerializeResponse(response), Does.Contain("\"result\":{\"declared\":\"declared\"}"));
    }
}
