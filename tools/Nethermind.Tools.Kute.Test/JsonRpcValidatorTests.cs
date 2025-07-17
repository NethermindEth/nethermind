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
        public void IsValid_When_RequestIsBatch()
        {
            var response = CreateResponse(isValid: true);
            bool result = _validator.IsValid(CreateBatchRequest(CreateSingleRequest("engine_newPayload")), response);

            result.Should().BeTrue();
        }

        [Test]
        public void IsValid_When_MethodNameIsNull()
        {
            var response = CreateResponse(isValid: true);
            bool result = _validator.IsValid(CreateSingleRequest(null), response);

            result.Should().BeTrue();
        }

        [Test]
        public void IsValid_When_MethodNameIsUnexpected()
        {
            var response = CreateResponse(isValid: true);
            bool result = _validator.IsValid(CreateSingleRequest("eth_getBlockByNumber"), response);

            result.Should().BeTrue();
        }

        [Test]
        // TODO: Are we sure that this is the correct behavior?
        public void IsValid_When_ResposeIsNull()
        {
            JsonDocument? response = null;
            bool result = _validator.IsValid(CreateSingleRequest("eth_getBlockByNumber"), response);

            result.Should().BeTrue();
        }

        [Test]
        public void Validates_When_ResponseIsNotNull()
        {
            foreach (var isValid in new[] { true, false })
            {
                foreach (var methodName in new[] { "engine_newPayloadV2", "engine_newPayloadV3", "engine_newPayloadV4" })
                {
                    var response = CreateResponse(isValid);
                    bool result = _validator.IsValid(CreateSingleRequest(methodName), response);

                    result.Should().Be(isValid);
                }
            }
        }
    }

    public class NonErrorJsonRpcValidatorTests
    {
        private static readonly NonErrorJsonRpcValidator _validator = new();

        [Test]
        public void IsValid_When_RequestIsBatch()
        {
            var response = CreateResponse(isValid: true);
            bool result = _validator.IsValid(CreateBatchRequest(CreateSingleRequest("engine_newPayload")), response);

            result.Should().BeTrue();
        }

        [Test]
        public void IsInvalid_When_ResponseIsNull()
        {
            JsonDocument? response = null;
            bool result = _validator.IsValid(CreateSingleRequest("eth_getBlockByNumber"), response);

            result.Should().BeFalse();
        }

        [TestCase(true)]
        [TestCase(false)]
        public void IsValid_When_ResponseHasNoError(bool isValid)
        {
            var response = CreateResponse(isValid);
            bool result = _validator.IsValid(CreateSingleRequest("eth_getBlockByNumber"), response);

            result.Should().BeTrue();
        }

        [Test]
        public void IsInvalid_When_ResponseHasError()
        {
            var response = CreateErrorResponse();
            bool result = _validator.IsValid(CreateSingleRequest("eth_getBlockByNumber"), response);

            result.Should().BeFalse();
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

    private static JsonDocument CreateErrorResponse()
    {
        return JsonDocument.Parse(
            """{"jsonrpc":"2.0","id":1,"error":{"code":-32603,"message":"Internal error"}}"""
        );
    }
}
