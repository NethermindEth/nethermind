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
        JsonRpcRequest request = new() { Id = "test\nrequest", Method = "eth\tmethod", Params = parameters.RootElement };
        JsonElement serialized = JsonSerializer.SerializeToElement(request);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.ToString(), Is.EqualTo(@"Id:test\nrequest, eth\tmethod(params omitted)"));
            Assert.That(serialized.GetProperty(nameof(JsonRpcRequest.Id)).GetString(), Is.EqualTo("test\nrequest"));
        }
    }
}
