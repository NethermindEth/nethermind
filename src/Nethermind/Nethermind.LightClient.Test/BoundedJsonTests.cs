// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
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
            BoundedJson.ToElement(Array.Empty<byte>());
            long before = GC.GetAllocatedBytesForCurrentThread();
            RpcException? error = Assert.Throws<RpcException>(() => BoundedJson.ToElement(value));
            Assert.That(error!.Code, Is.EqualTo(-32005));
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.LessThan(256 * 1024),
                "Reject the hex token before renting or materializing its encoded bytes.");
        }
        else
        {
            JsonElement result = BoundedJson.ToElement(value);
            Assert.That(result.GetRawText().Length, Is.EqualTo(BoundedJson.ResultLimit));
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
        Assert.That(BoundedJson.ToElement(value).GetRawText(), Is.EqualTo(expected));
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
