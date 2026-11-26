// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

public class JsonRpcRequestLoggingTests
{
    [Test]
    public void keeps_request_diagnostics_on_one_line()
    {
        using JsonDocument parameters = JsonDocument.Parse("[]");
        const string id = "test + <é>\nrequest";
        JsonRpcRequest request = new() { Id = id, Method = "eth\tmethod", Params = parameters.RootElement };
        JsonElement serialized = JsonSerializer.SerializeToElement(request);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.ToString(), Is.EqualTo(@"Id:test + <é>\nrequest, eth\tmethod(params omitted)"));
            Assert.That(serialized.GetProperty(nameof(JsonRpcRequest.Id)).GetString(), Is.EqualTo(id));
        }
    }

    [Test]
    public void escapes_log_control_characters() =>
        Assert.That(JsonRpcRequest.EncodeLogText("first\u0085second\u2028third\u2029fourth"), Is.EqualTo(@"first\u0085second\u2028third\u2029fourth"));
}
