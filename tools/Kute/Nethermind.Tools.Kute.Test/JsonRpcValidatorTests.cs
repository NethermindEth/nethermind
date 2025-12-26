// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nethermind.Tools.Kute.JsonRpcValidator;
using Nethermind.Tools.Kute.JsonRpcValidator.Eth;
using NUnit.Framework;

namespace Nethermind.Tools.Kute.Test;

public class JsonRpcValidatorTests
{
    private static IJsonRpcValidator _validator =
        new BatchJsonRpcValidator(
            new ComposedJsonRpcValidator(
                new NonErrorJsonRpcValidator(),
                new NewPayloadJsonRpcValidator()));

    [Test]
    public void IsInvalid_When_ResponseHasError()
    {
        var response = CreateErrorResponse();
        bool result = _validator.IsValid(CreateSingleRequest("eth_getBlockByNumber"), response);

        result.Should().BeFalse();
    }

    [Test]
    public void IsValid_When_MethodNameIsNull()
    {
        var response = CreateResponse(status: Status.VALID);
        bool result = _validator.IsValid(CreateSingleRequest(null), response);

        result.Should().BeTrue();
    }

    [Test]
    public void IsValid_When_MethodNameIsUnexpected()
    {
        var response = CreateResponse(status: Status.VALID);
        bool result = _validator.IsValid(CreateSingleRequest("eth_getBlockByNumber"), response);

        result.Should().BeTrue();
    }

    [Test]
    public void IsValid_NotNewPayload_When_ResponseHasNoError()
    {
        foreach (var status in new[] { Status.VALID, Status.INVALID })
        {
            var response = CreateResponse(status);
            bool result = _validator.IsValid(CreateSingleRequest("eth_getBlockByNumber"), response);

            result.Should().BeTrue();
        }
    }

    [Test]
    public void Validates_NewPayload_When_ResponseIsNotNull()
    {
        foreach (var status in new[] { Status.VALID, Status.INVALID })
        {
            foreach (var methodName in new[] { "engine_newPayloadV2", "engine_newPayloadV3", "engine_newPayloadV4" })
            {
                var response = CreateResponse(status);
                bool result = _validator.IsValid(CreateSingleRequest(methodName), response);

                result.Should().Be(status == Status.VALID);
            }
        }
    }

    [Test]
    public void IsValid_When_AllBatchItemsAreValid()
    {
        var response = CreateBatchResponse(CreateResponse(status: Status.VALID));
        bool result = _validator.IsValid(CreateBatchRequest(CreateSingleRequest("engine_newPayload")), response);

        result.Should().BeTrue();
    }

    [Test]
    public void IsInvalid_When_BatchResponseContainsInvalid()
    {
        var response = CreateBatchResponse(CreateResponse(status: Status.INVALID));
        bool result = _validator.IsValid(CreateBatchRequest(CreateSingleRequest("engine_newPayload")), response);

        result.Should().BeFalse();
    }

    [Test]
    public void IsInvalid_When_BatchHasMissingResponses()
    {
        var response = CreateBatchResponse(CreateResponse(status: Status.VALID, 1));
        bool result = _validator.IsValid(
            CreateBatchRequest(CreateSingleRequest("engine_newPayload", 1), CreateSingleRequest("eth_logs", 2)), response);

        result.Should().BeFalse();
    }

    [Test]
    public void IsInvalid_When_BatchHasExtraResponses()
    {
        var response = CreateBatchResponse(CreateResponse(status: Status.VALID, 1), CreateResponse(status: Status.VALID, 2));
        bool result = _validator.IsValid(CreateBatchRequest(CreateSingleRequest("engine_newPayload", 1)), response);

        result.Should().BeFalse();
    }

    [Test]
    public void IsInvalid_When_BatchHasMismatchedPairs()
    {
        var response = CreateBatchResponse(CreateResponse(status: Status.VALID, 2));
        bool result = _validator.IsValid(CreateBatchRequest(CreateSingleRequest("engine_newPayload", 1)), response);

        result.Should().BeFalse();
    }

    [Test]
    public void IsInvalid_When_BatchHasSingleResponse()
    {
        var response = CreateResponse(status: Status.VALID, 1);
        bool result = _validator.IsValid(CreateBatchRequest(CreateSingleRequest("engine_newPayload", 1)), response);

        result.Should().BeFalse();
    }

    private static JsonRpc.Request.Single CreateSingleRequest(string? method, int id = 1)
    {
        var methodJSON = method is null ? "null" : $"\"{method}\"";
        return new JsonRpc.Request.Single(
            JsonNode.Parse(
                $$"""{"jsonrpc":"2.0","id":{{id}},"method":{{methodJSON}},"params":[]}"""
            )!
        );
    }

    private static JsonRpc.Request.Batch CreateBatchRequest(params IEnumerable<JsonRpc.Request> items)
    {
        var sb = new StringBuilder("[");
        foreach (var item in items)
        {
            sb.Append(item.ToJsonString()).Append(",");
        }
        sb.Remove(sb.Length - 1, 1); // Remove the last comma
        sb.Append("]");

        var json = sb.ToString();

        return new JsonRpc.Request.Batch(JsonNode.Parse(json)!);
    }

    public enum Status
    {
        VALID,
        INVALID
    }

    private static JsonRpc.Response CreateResponse(Status status, int id = 1)
    {
        return new JsonRpc.Response(
            JsonNode.Parse(
                $$$"""{"jsonrpc":"2.0","id": {{{id}}},"result":{"status": "{{{status}}}"}}"""
        )!);
    }

    private static JsonRpc.Response CreateBatchResponse(params IEnumerable<JsonRpc.Response> items)
    {
        var sb = new StringBuilder("[");
        foreach (var item in items)
        {
            sb.Append(item.ToJsonString()).Append(",");
        }
        sb.Remove(sb.Length - 1, 1); // Remove the last comma
        sb.Append("]");

        var json = sb.ToString();

        return new JsonRpc.Response(JsonNode.Parse(json)!);
    }

    private static JsonRpc.Response CreateErrorResponse()
    {
        return new JsonRpc.Response(
            JsonNode.Parse(
                """{"jsonrpc":"2.0","id":1,"error":{"code":-32603,"message":"Internal error"}}"""
        )!);
    }
}
