// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO;
using System.IO.Abstractions;
using System.IO.Pipelines;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using NSubstitute;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Runner.JsonRpc;
using Nethermind.Serialization.Json;
using NUnit.Framework;

#nullable enable

namespace Nethermind.Runner.Test.JsonRpc;

[TestFixture]
public class CountingWriterTests
{
    [Test]
    public async Task NonBufferedResponse_WritesViaHttpResponseBodyStream()
    {
        Startup startup = CreateStartup();
        JsonRpcUrl url = new("http", "localhost", 8545, RpcEndpoint.Http, false, ["eth"]);
        TrackingResponseBodyFeature responseBodyFeature = new();

        DefaultHttpContext context = CreateContext(responseBodyFeature, BuildSingleRequest("eth_testMethod"));

        await InvokeProcessJsonRpcRequestCoreAsync(startup, context, url);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        responseBodyFeature.StreamWriteCalls.Should().BeGreaterThan(0,
            "non-buffered responses should be streamed directly to HttpResponse.Body");
        responseBodyFeature.WriterAdvancedBytes.Should().Be(0,
            "non-buffered responses should not serialize through HttpResponse.BodyWriter");
    }

    [Test]
    public async Task NonBufferedResponse_ReturnsValidJsonRpcPayload()
    {
        Startup startup = CreateStartup();
        JsonRpcUrl url = new("http", "localhost", 8545, RpcEndpoint.Http, false, ["eth"]);
        TrackingResponseBodyFeature responseBodyFeature = new();

        DefaultHttpContext context = CreateContext(responseBodyFeature, BuildSingleRequest("eth_testMethod"));

        await InvokeProcessJsonRpcRequestCoreAsync(startup, context, url);

        string json = Encoding.UTF8.GetString(responseBodyFeature.GetStreamBytes());
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        root.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        root.GetProperty("id").GetInt64().Should().Be(1);
        root.GetProperty("result").GetString().Should().NotBeNullOrEmpty();
        responseBodyFeature.StreamLength.Should().BeGreaterThan(1_000_000);
    }

    private static string BuildSingleRequest(string method)
        => $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"{method}\",\"params\":[]}}";

    private static DefaultHttpContext CreateContext(TrackingResponseBodyFeature responseBodyFeature, string requestJson)
    {
        DefaultHttpContext context = new();
        context.Features.Set<IHttpResponseBodyFeature>(responseBodyFeature);

        byte[] requestBytes = Encoding.UTF8.GetBytes(requestJson);
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = requestBytes.Length;
        context.Request.Body = new MemoryStream(requestBytes);
        context.Connection.LocalIpAddress = IPAddress.Loopback;
        context.Connection.RemoteIpAddress = IPAddress.Loopback;

        return context;
    }

    private static Startup CreateStartup()
    {
        JsonRpcConfig jsonRpcConfig = new()
        {
            BufferResponses = false,
            MaxBatchResponseBodySize = int.MaxValue,
            Timeout = 30_000
        };

        IJsonRpcService processorService = Substitute.For<IJsonRpcService>();
        processorService.SendRequestAsync(Arg.Any<JsonRpcRequest>(), Arg.Any<JsonRpcContext>())
            .Returns(call =>
            {
                JsonRpcRequest request = call.Arg<JsonRpcRequest>();
                return new JsonRpcSuccessResponse
                {
                    Id = request.Id,
                    Result = new string('x', 1_200_000)
                };
            });
        processorService.GetErrorResponse(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<string?>())
            .Returns(call => new JsonRpcErrorResponse
            {
                Id = call.ArgAt<object?>(2),
                Error = new Error
                {
                    Code = call.ArgAt<int>(0),
                    Message = call.ArgAt<string>(1)
                }
            });
        processorService.GetErrorResponse(Arg.Any<int>(), Arg.Any<string>())
            .Returns(call => new JsonRpcErrorResponse
            {
                Error = new Error
                {
                    Code = call.ArgAt<int>(0),
                    Message = call.ArgAt<string>(1)
                }
            });

        JsonRpcProcessor jsonRpcProcessor = new(
            processorService,
            jsonRpcConfig,
            Substitute.For<IFileSystem>(),
            LimboLogs.Instance);

        JsonRpcService startupJsonRpcService = new(
            Substitute.For<IRpcModuleProvider>(),
            LimboLogs.Instance,
            jsonRpcConfig);

        Startup startup = new();
        SetPrivateField(startup, "_jsonRpcProcessor", jsonRpcProcessor);
        SetPrivateField(startup, "_jsonRpcService", startupJsonRpcService);
        SetPrivateField(startup, "_jsonRpcLocalStats", new NullJsonRpcLocalStats());
        SetPrivateField(startup, "_jsonSerializer", new EthereumJsonSerializer());
        SetPrivateField(startup, "_jsonRpcConfig", jsonRpcConfig);
        SetPrivateField(startup, "_logger", LimboLogs.Instance.GetClassLogger());

        return startup;
    }

    private static async Task InvokeProcessJsonRpcRequestCoreAsync(Startup startup, HttpContext context, JsonRpcUrl jsonRpcUrl)
    {
        MethodInfo method = typeof(Startup).GetMethod("ProcessJsonRpcRequestCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Cannot find ProcessJsonRpcRequestCoreAsync via reflection.");

        Task processTask = (Task)(method.Invoke(startup, [context, jsonRpcUrl])
            ?? throw new InvalidOperationException("ProcessJsonRpcRequestCoreAsync invocation returned null task."));

        await processTask;
    }

    private static void SetPrivateField<T>(Startup startup, string fieldName, T value)
    {
        FieldInfo field = typeof(Startup).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Cannot find field {fieldName} on Startup.");

        field.SetValue(startup, value);
    }

    private sealed class TrackingResponseBodyFeature : IHttpResponseBodyFeature
    {
        private readonly TrackingStream _stream = new();
        private readonly TrackingPipeWriter _writer;

        public TrackingResponseBodyFeature()
        {
            Pipe pipe = new(new PipeOptions(pauseWriterThreshold: long.MaxValue));
            _writer = new TrackingPipeWriter(pipe.Writer);
        }

        public Stream Stream => _stream;
        public PipeWriter Writer => _writer;

        public int StreamWriteCalls => _stream.WriteCalls;
        public long StreamLength => _stream.Length;
        public long WriterAdvancedBytes => _writer.AdvancedBytes;

        public void DisableBuffering()
        {
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task CompleteAsync() => Task.CompletedTask;

        public byte[] GetStreamBytes() => _stream.ToArray();
    }

    private sealed class TrackingStream : MemoryStream
    {
        public int WriteCalls { get; private set; }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            WriteCalls++;
            base.Write(buffer);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteCalls++;
            base.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            WriteCalls++;
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }
    }

    private sealed class TrackingPipeWriter(PipeWriter inner) : PipeWriter
    {
        public long AdvancedBytes { get; private set; }

        public override void Advance(int bytes)
        {
            inner.Advance(bytes);
            AdvancedBytes += bytes;
        }

        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
        public override bool CanGetUnflushedBytes => inner.CanGetUnflushedBytes;
        public override long UnflushedBytes => inner.UnflushedBytes;
    }
}
