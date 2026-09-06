// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

[TestFixture]
public class ByteArrayArrayConverterTests
{
    [Test]
    public void Engine_context_reads_and_writes_hex_arrays()
    {
        JsonTypeInfo typeInfo = EngineApiJsonContext.Default.GetTypeInfo(typeof(byte[][]))!;

        Assert.That(typeInfo.Converter, Is.TypeOf<ByteArrayArrayConverter>());

        byte[][]? decoded = JsonSerializer.Deserialize("""["0x0102","0x",null,"0x0f"]"""u8, typeInfo) as byte[][];
        Assert.That(decoded, Is.Not.Null);
        Assert.That(decoded!, Has.Length.EqualTo(4));
        Assert.That(decoded![0], Is.EqualTo(new byte[] { 0x01, 0x02 }));
        Assert.That(decoded[1], Is.EqualTo(System.Array.Empty<byte>()));
        Assert.That(decoded[2], Is.Null);
        Assert.That(decoded[3], Is.EqualTo(new byte[] { 0x0f }));

        string json = JsonSerializer.Serialize((object)decoded, typeInfo);
        Assert.That(json, Is.EqualTo("""["0x0102","0x",null,"0x0f"]"""));
    }

    [Test]
    public void ExecutionPayloadV3_roundtrips_transactions_with_engine_context_options()
    {
        JsonSerializerOptions options = new(EthereumJsonSerializer.JsonOptions)
        {
            TypeInfoResolver = EngineApiJsonContext.Default
        };

        ExecutionPayloadV3 payload = new()
        {
            BlobGasUsed = 0,
            ExcessBlobGas = 0,
            ParentBeaconBlockRoot = Keccak.Zero,
            Transactions =
            [
                [0x01, 0x02],
                []
            ],
            Withdrawals = []
        };

        string json = JsonSerializer.Serialize(payload, options);
        ExecutionPayloadV3? decoded = JsonSerializer.Deserialize<ExecutionPayloadV3>(json, options);

        Assert.That(decoded, Is.Not.Null);
        Assert.That(decoded!.Transactions, Has.Length.EqualTo(2));
        Assert.That(decoded.Transactions[0], Is.EqualTo(new byte[] { 0x01, 0x02 }));
        Assert.That(decoded.Transactions[1], Is.EqualTo(System.Array.Empty<byte>()));
    }
}
