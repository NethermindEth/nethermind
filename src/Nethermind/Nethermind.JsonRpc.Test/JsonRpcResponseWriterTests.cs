// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

public class JsonRpcResponseWriterTests
{
    private const string Expected = "{\"jsonrpc\":\"2.0\",\"result\":\"ok\",\"id\":1}";

    [Test]
    public void Consecutive_writes_match_regardless_of_previous_options()
    {
        string first = Write(Success("ok"), EthereumJsonSerializer.JsonOptions);
        string indented = Write(Success("ok"), EthereumJsonSerializer.JsonOptionsIndented);
        string second = Write(Success("ok"), EthereumJsonSerializer.JsonOptions);
        string indentedAgain = Write(Success("ok"), EthereumJsonSerializer.JsonOptionsIndented);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo(Expected));
            Assert.That(second, Is.EqualTo(Expected));
            Assert.That(indented, Does.Contain("\n"));
            Assert.That(indentedAgain, Is.EqualTo(indented));
        }
    }

    [Test]
    public void Failed_write_does_not_affect_next_write()
    {
        Assert.Throws<InvalidOperationException>(() => Write(Success(new ThrowingResult()), EthereumJsonSerializer.JsonOptions));

        Assert.That(Write(Success("ok"), EthereumJsonSerializer.JsonOptions), Is.EqualTo(Expected));
    }

    [Test]
    public void Nested_write_does_not_share_the_outer_writer()
    {
        Write(Success("warm-up"), EthereumJsonSerializer.JsonOptions);
        ReentrantResult result = new();

        string outer = Write(Success(result), EthereumJsonSerializer.JsonOptions);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outer, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"value\":\"outer\"},\"id\":1}"));
            Assert.That(result.Inner, Is.EqualTo(Expected));
        }
    }

    [Test]
    public void Repeated_write_reuses_the_writer()
    {
        using JsonRpcSuccessResponse response = Success("ok");
        ArrayBufferWriter<byte> buffer = new(256);
        JsonRpcResponseWriter.Write(buffer, response, EthereumJsonSerializer.JsonOptions);
        buffer.ResetWrittenCount();

        long before = GC.GetAllocatedBytesForCurrentThread();
        JsonRpcResponseWriter.Write(buffer, response, EthereumJsonSerializer.JsonOptions);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(allocated, Is.Zero);
    }

    [Test]
    public void Serialized_payload_holds_only_its_own_bytes_after_a_large_or_failed_one([Values] bool previousFailed)
    {
        if (previousFailed)
        {
            Assert.Throws<InvalidOperationException>(() => JsonRpcResponseWriter.SerializePayload(new ThrowingResult(), EthereumJsonSerializer.JsonOptions));
        }
        else
        {
            JsonRpcResponseWriter.SerializePayload(new string('a', 64 * 1024), EthereumJsonSerializer.JsonOptions);
        }

        Assert.That(Encoding.UTF8.GetString(JsonRpcResponseWriter.SerializePayload("ok", EthereumJsonSerializer.JsonOptions)), Is.EqualTo("\"ok\""));
    }

    [Test]
    public void Repeated_payload_serialization_allocates_only_the_payload()
    {
        JsonRpcResponseWriter.SerializePayload("ok", EthereumJsonSerializer.JsonOptions);

        long before = GC.GetAllocatedBytesForCurrentThread();
        JsonRpcResponseWriter.SerializePayload("ok", EthereumJsonSerializer.JsonOptions);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(allocated, Is.LessThan(128), "the payload array, not a fresh buffer and writer");
    }

    [Test]
    public void Cached_writer_does_not_keep_the_output_alive()
    {
        WeakReference output = WriteToUnreferencedBuffer();

        GC.Collect();

        Assert.That(output.IsAlive, Is.False);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference WriteToUnreferencedBuffer()
    {
        using JsonRpcSuccessResponse response = Success("ok");
        ArrayBufferWriter<byte> buffer = new();
        JsonRpcResponseWriter.Write(buffer, response, EthereumJsonSerializer.JsonOptions);
        return new WeakReference(buffer);
    }

    private static JsonRpcSuccessResponse Success(object result) => new() { Id = new JsonRpcId(1L), Result = result };

    private static string Write(JsonRpcResponse response, JsonSerializerOptions options)
    {
        using (response)
        {
            ArrayBufferWriter<byte> buffer = new();
            JsonRpcResponseWriter.Write(buffer, response, options);
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
    }

    private sealed class ThrowingResult
    {
        public string Value => throw new InvalidOperationException();
    }

    private sealed class ReentrantResult
    {
        public string? Inner { get; private set; }

        public string Value
        {
            get
            {
                Inner = Write(Success("ok"), EthereumJsonSerializer.JsonOptions);
                return "outer";
            }
        }
    }
}
