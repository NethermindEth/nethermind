// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json.Nodes;
using Nethermind.Tools.Kute.JsonRpcValidator;
using Nethermind.Tools.Kute.JsonRpcValidator.Eth;
using NUnit.Framework;

namespace Nethermind.Tools.Kute.Test;

public class JsonRpcValidatorTests
{
    private static readonly IJsonRpcValidator _validator =
        new BatchJsonRpcValidator(
            new ComposedJsonRpcValidator(
                new NonErrorJsonRpcValidator(),
                new NewPayloadJsonRpcValidator()));

    [Test]
    public void IsInvalid_When_ResponseHasError()
    {
        JsonRpc.Response response = CreateErrorResponse();
        bool result = _validator.IsValid(CreateSingleRequest("eth_getBlockByNumber"), response);

        Assert.That(result, Is.False);
    }

    [Test]
    public void IsValid_When_MethodNameIsNull()
    {
        JsonRpc.Response response = CreateResponse(status: Status.VALID);
        bool result = _validator.IsValid(CreateSingleRequest(null), response);

        Assert.That(result, Is.True);
    }

    [Test]
    public void IsValid_When_MethodNameIsUnexpected()
    {
        JsonRpc.Response response = CreateResponse(status: Status.VALID);
        bool result = _validator.IsValid(CreateSingleRequest("eth_getBlockByNumber"), response);

        Assert.That(result, Is.True);
    }

    [Test]
    public void IsValid_NotNewPayload_When_ResponseHasNoError()
    {
        foreach (Status status in new[] { Status.VALID, Status.INVALID })
        {
            JsonRpc.Response response = CreateResponse(status);
            bool result = _validator.IsValid(CreateSingleRequest("eth_getBlockByNumber"), response);

            Assert.That(result, Is.True);
        }
    }

    [Test]
    public void Validates_NewPayload_When_ResponseIsNotNull()
    {
        foreach (Status status in new[] { Status.VALID, Status.INVALID })
        {
            foreach (string? methodName in new[] { "engine_newPayloadV2", "engine_newPayloadV3", "engine_newPayloadV4" })
            {
                JsonRpc.Response response = CreateResponse(status);
                bool result = _validator.IsValid(CreateSingleRequest(methodName), response);

                Assert.That(result, Is.EqualTo(status == Status.VALID));
            }
        }
    }

    [Test]
    public void IsValid_When_AllBatchItemsAreValid()
    {
        JsonRpc.Response response = CreateBatchResponse(CreateResponse(status: Status.VALID));
        bool result = _validator.IsValid(CreateBatchRequest(CreateSingleRequest("engine_newPayload")), response);

        Assert.That(result, Is.True);
    }

    [Test]
    public void IsInvalid_When_BatchResponseContainsInvalid()
    {
        JsonRpc.Response response = CreateBatchResponse(CreateResponse(status: Status.INVALID));
        bool result = _validator.IsValid(CreateBatchRequest(CreateSingleRequest("engine_newPayload")), response);

        Assert.That(result, Is.False);
    }

    [Test]
    public void IsInvalid_When_BatchHasMissingResponses()
    {
        JsonRpc.Response response = CreateBatchResponse(CreateResponse(status: Status.VALID, 1));
        bool result = _validator.IsValid(
            CreateBatchRequest(CreateSingleRequest("engine_newPayload", 1), CreateSingleRequest("eth_logs", 2)), response);

        Assert.That(result, Is.False);
    }

    [Test]
    public void IsInvalid_When_BatchHasExtraResponses()
    {
        JsonRpc.Response response = CreateBatchResponse(CreateResponse(status: Status.VALID, 1), CreateResponse(status: Status.VALID, 2));
        bool result = _validator.IsValid(CreateBatchRequest(CreateSingleRequest("engine_newPayload", 1)), response);

        Assert.That(result, Is.False);
    }

    [Test]
    public void IsInvalid_When_BatchHasMismatchedPairs()
    {
        JsonRpc.Response response = CreateBatchResponse(CreateResponse(status: Status.VALID, 2));
        bool result = _validator.IsValid(CreateBatchRequest(CreateSingleRequest("engine_newPayload", 1)), response);

        Assert.That(result, Is.False);
    }

    [Test]
    public void IsInvalid_When_BatchHasSingleResponse()
    {
        JsonRpc.Response response = CreateResponse(status: Status.VALID, 1);
        bool result = _validator.IsValid(CreateBatchRequest(CreateSingleRequest("engine_newPayload", 1)), response);

        Assert.That(result, Is.False);
    }

    private static JsonRpc.Request.Single CreateSingleRequest(string? method, int id = 1)
    {
        string methodJSON = method is null ? "null" : $"\"{method}\"";
        return new JsonRpc.Request.Single(
            JsonNode.Parse(
                $$"""{"jsonrpc":"2.0","id":{{id}},"method":{{methodJSON}},"params":[]}"""
            )!
        );
    }

    private static JsonRpc.Request.Batch CreateBatchRequest(params IEnumerable<JsonRpc.Request> items)
    {
        StringBuilder sb = new("[");
        foreach (JsonRpc.Request item in items)
        {
            sb.Append(item.ToJsonString()).Append(',');
        }
        sb.Remove(sb.Length - 1, 1); // Remove the last comma
        sb.Append(']');

        string json = sb.ToString();

        return new JsonRpc.Request.Batch(JsonNode.Parse(json)!);
    }

    public enum Status
    {
        VALID,
        INVALID
    }

    private static JsonRpc.Response CreateResponse(Status status, int id = 1) => new(
            JsonNode.Parse(
                $$$"""{"jsonrpc":"2.0","id": {{{id}}},"result":{"status": "{{{status}}}"}}"""
        )!);

    private static JsonRpc.Response CreateBatchResponse(params IEnumerable<JsonRpc.Response> items)
    {
        StringBuilder sb = new("[");
        foreach (JsonRpc.Response item in items)
        {
            sb.Append(item.ToJsonString()).Append(',');
        }
        sb.Remove(sb.Length - 1, 1); // Remove the last comma
        sb.Append(']');

        string json = sb.ToString();

        return new JsonRpc.Response(JsonNode.Parse(json)!);
    }

    private static JsonRpc.Response CreateErrorResponse() => new(
            JsonNode.Parse(
                """{"jsonrpc":"2.0","id":1,"error":{"code":-32603,"message":"Internal error"}}"""
        )!);
}
