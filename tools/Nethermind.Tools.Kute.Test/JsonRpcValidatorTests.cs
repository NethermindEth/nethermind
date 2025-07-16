// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Nethermind.Tools.Kute.JsonRpcValidator.Eth;
using NUnit.Framework;

namespace Nethermind.Tools.Kute.Test;

public class JsonRpcValidatorTests
{
    public class NewPayloadValidatorTests
    {
        private static readonly NewPayloadJsonRpcValidator _validator = new();

        [Test]
        public void IsValid_When_MethodIsNull()
        {
            var response = CreateResponse(isValid: true);
            bool result = _validator.IsValid(CreateSingleRequest(null), response);

            result.Should().BeTrue();
        }

        [Test]
        public void IsValid_When_MethodIsBatch()
        {
            var response = CreateResponse(isValid: true);
            bool result = _validator.IsValid(CreateBatchRequest(CreateSingleRequest("engine_newPayload")), response);

            result.Should().BeTrue();
        }

        [Test]
        public void IsValid_When_MethodIsUnexpected()
        {
            var response = CreateResponse(isValid: true);
            bool result = _validator.IsValid(CreateSingleRequest("eth_getBlockByNumber"), response);

            result.Should().BeTrue();
        }

        [Test]
        public void IsValid_When_ResposeIsNull()
        {
            JsonDocument? response = null;
            bool result = _validator.IsValid(CreateSingleRequest("eth_getBlockByNumber"), response);

            result.Should().BeTrue();
        }
    }

    private static JsonRpc.SingleJsonRpc CreateSingleRequest(string? method)
    {
        var methodJSON = method is null ? "null" : $"\"{method}\"";
        return new JsonRpc.SingleJsonRpc(
            JsonDocument.Parse(
                $$"""{"jsonrpc":"2.0","id":1,"method":{{methodJSON}},"params":[]}"""
            )
        );
    }

    private static JsonRpc.BatchJsonRpc CreateBatchRequest(params IEnumerable<JsonRpc.SingleJsonRpc> items)
    {
        var sb = new StringBuilder("[");
        foreach (var item in items)
        {
            sb.Append(item.ToJsonString()).Append(",");
        }
        sb.Remove(sb.Length - 1, 1); // Remove the last comma
        sb.Append("]");

        return new JsonRpc.BatchJsonRpc(JsonDocument.Parse(sb.ToString()));
    }

    private static JsonDocument CreateResponse(bool isValid)
    {
        string status = isValid ? "VALID" : "INVALID";
        return JsonDocument.Parse(
            $$$"""{"jsonrpc":"2.0","id": 1,"result":{"status": "{{{status}}}"}}"""
        );
    }
}
