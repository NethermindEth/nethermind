// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Text;
using System.Text.Json;
using Nethermind.JsonRpc;
using Nethermind.LightClient.Rpc;
using Nethermind.Serialization.Json;

namespace Nethermind.LightClient.Test;

public class BoundedJsonTests
{
    [Test]
    public void Hex_result_accepts_the_exact_byte_limit_and_rejects_the_next_byte([Values] bool oversized)
    {
        byte[] value = new byte[(BoundedJson.ResultLimit - 4) / 2 + (oversized ? 1 : 0)];
        if (oversized)
        {
            BoundedJson.Serialize(Array.Empty<byte>());
            long before = GC.GetAllocatedBytesForCurrentThread();
            RpcException? error = Assert.Throws<RpcException>(() => BoundedJson.Serialize(value));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(error!.Code, Is.EqualTo(-32005));
                Assert.That(allocated, Is.LessThan(256 * 1024),
                    "Reject the hex token before renting or materializing its encoded bytes.");
            }
        }
        else
        {
            Assert.That(BoundedJson.Serialize(value).Utf8Json, Has.Length.EqualTo(BoundedJson.ResultLimit));
        }
    }

    [Test]
    public void Guarded_hex_writers_preserve_existing_json([Values(0, 1, 2)] int shape)
    {
        byte[] bytes = [0, 0x12, 0xab, 0xff];
        object value = shape switch
        {
            0 => bytes,
            1 => new ReadOnlyMemory<byte>(bytes),
            _ => new byte[]?[] { bytes, [], null },
        };
        string expected = TypeInfoJsonSerializer.Serialize(value, EthereumJsonSerializer.JsonOptions);
        Assert.That(Encoding.UTF8.GetString(BoundedJson.Serialize(value).Utf8Json), Is.EqualTo(expected));
    }

    [Test]
    public void Serialized_result_preserves_the_compact_envelope_bytes()
    {
        object payload = new Dictionary<string, string?> { ["<name>"] = "α<&>\n", ["empty"] = null };
        using ResultWrapper<JsonElement> element = ResultWrapper<JsonElement>.Success(JsonSerializer.SerializeToElement(payload));
        using ResultWrapper<SerializedJson> justBytes = ResultWrapper<SerializedJson>.Success(BoundedJson.Serialize(payload));
        element.Id = justBytes.Id = new JsonRpcId("λ<&>");
        ArrayBufferWriter<byte> expected = new();
        ArrayBufferWriter<byte> actual = new();
        JsonRpcResponseWriter.Write(expected, element, EthereumJsonSerializer.JsonOptions);
        JsonRpcResponseWriter.Write(actual, justBytes, EthereumJsonSerializer.JsonOptions);

        Assert.That(actual.WrittenSpan.SequenceEqual(expected.WrittenSpan), Is.True);
    }

    [Test]
    public void Buffer_bounds_committed_bytes_and_releases_after_failure()
    {
        using BoundedJsonBuffer buffer = new(8);
        buffer.GetSpan(8)[..8].Fill(1);
        buffer.Advance(8);
        Assert.That(buffer.WrittenMemory.Length, Is.EqualTo(8));
        Assert.Throws<RpcException>(() => buffer.Advance(1));
        Assert.Throws<RpcException>(() => buffer.GetMemory(4097));
        buffer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => buffer.GetMemory());
    }
}
