// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Nethermind.Eez.Attester;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class SettlementRpcReaderTests
{
    [TestCase("{", TestName = "Read_InvalidJson_IsUnavailable")]
    [TestCase("[]", TestName = "Read_ArrayEnvelope_IsUnavailable")]
    [TestCase("{\"jsonrpc\":\"1.0\",\"id\":1,\"result\":\"0x1\"}", TestName = "Read_WrongVersion_IsUnavailable")]
    [TestCase("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":\"0x1\"}", TestName = "Read_WrongId_IsUnavailable")]
    [TestCase("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":\"0x1\"}", TestName = "Read_WrongIdType_IsUnavailable")]
    [TestCase("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":null}", TestName = "Read_NullResult_IsUnavailable")]
    [TestCase("{\"jsonrpc\":\"2.0\",\"id\":1}", TestName = "Read_MissingResult_IsUnavailable")]
    [TestCase("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":\"0x1\",\"result\":\"0x2\"}", TestName = "Read_DuplicateResult_IsUnavailable")]
    [TestCase("{\"jsonrpc\":\"2.0\",\"id\":1,\"id\":1,\"result\":\"0x1\"}", TestName = "Read_DuplicateId_IsUnavailable")]
    [TestCase("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":\"0x1\",\"error\":{\"message\":\"private response\"}}", TestName = "Read_ErrorAndResult_IsUnavailable")]
    public async Task Read_InvalidEnvelope_DoesNotReturnEvidence(string body)
    {
        using HttpClient client = Client(new Handler(new StringContent(body)));
        SettlementRpcReader reader = new(client, TimeSpan.FromSeconds(5));
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => reader.Read("eth_chainId", [], CancellationToken.None)))!;
        Assert.That((exception.StatusCode, exception.Status.Detail), Is.EqualTo((StatusCode.Unavailable, "settlement evidence unavailable")), "raw source errors do not cross the wire");
    }

    [TestCase(false, TestName = "Read_DeclaredOversizeBody_IsUnavailable")]
    [TestCase(true, TestName = "Read_UndeclaredOversizeBody_IsUnavailable")]
    public async Task Read_BodyOverLimit_RejectsBeforeParsing(bool streaming)
    {
        byte[] body = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"result":"0x1"}""".PadRight(101));
        using HttpClient client = Client(new Handler(streaming ? new StreamContent(new UnmeasuredStream(body)) : new ByteArrayContent(body)));
        SettlementRpcReader reader = new(client, TimeSpan.FromSeconds(5));
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => reader.Read("eth_chainId", [], CancellationToken.None, 100)))!;
        Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.Unavailable), "the byte limit applies with and without Content-Length");
    }

    [Test]
    public async Task Read_ResponseBodyStalls_DeadlineCancelsBodyRead()
    {
        StreamingTransportTests.ManualTime time = new();
        BlockingStream stream = new();
        using HttpClient client = Client(new Handler(new StreamContent(stream)));
        SettlementRpcReader reader = new(client, TimeSpan.FromSeconds(5), time);
        Task<JsonElement> read = reader.Read("eth_chainId", [], CancellationToken.None);
        await stream.Started.Task;
        time.Advance(TimeSpan.FromSeconds(5));
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => read))!;
        Assert.That((exception.StatusCode, stream.Cancellation.IsCancellationRequested), Is.EqualTo((StatusCode.Unavailable, true)), "the deadline covers body reads, not only response headers");
    }

    [Test]
    public async Task Read_CallerCancellation_StopsBodyRead()
    {
        BlockingStream stream = new();
        using HttpClient client = Client(new Handler(new StreamContent(stream)));
        using CancellationTokenSource cancellation = new();
        Task<JsonElement> read = new SettlementRpcReader(client, TimeSpan.FromMinutes(1)).Read("eth_chainId", [], cancellation.Token);
        await stream.Started.Task;
        cancellation.Cancel();
        await Assert.CatchAsync<OperationCanceledException>(() => read);
        Assert.That(stream.Cancellation.IsCancellationRequested, Is.True, "caller cancellation reaches owned evidence I/O");
    }

    [TestCase("0x", TestName = "Quantity_Empty_IsUnavailable")]
    [TestCase("0x01", TestName = "Quantity_LeadingZero_IsUnavailable")]
    [TestCase("0x10000000000000000", TestName = "Quantity_Overflow_IsUnavailable")]
    [TestCase("1", TestName = "Quantity_MissingPrefix_IsUnavailable")]
    public void Quantity_NoncanonicalHex_IsUnavailable(string value)
    {
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        Assert.That(() => SettlementRpcReader.Quantity(document.RootElement), Throws.TypeOf<RpcException>(), "source quantities must use canonical Ethereum hex encoding");
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("http://source.invalid") };

    private sealed class Handler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    private sealed class UnmeasuredStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class BlockingStream : Stream
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Cancellation { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellation = default)
        {
            Cancellation = cancellation;
            Started.SetResult();
            await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(cancellation);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
