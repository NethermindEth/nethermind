// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

[TestFixture]
public class RlpExecutionWitnessJsonConverterTests
{
    private static IEnumerable<TestCaseData> ResolverCases()
    {
        yield return new TestCaseData(new JsonSerializerOptions(EthereumJsonSerializer.JsonOptions)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        }).SetName("reflection resolver");
        yield return new TestCaseData(new JsonSerializerOptions(EthereumJsonSerializer.JsonOptions)
        {
            TypeInfoResolver = EngineApiJsonContext.Default
        }).SetName("engine source-generated resolver");
    }

    [TestCaseSource(nameof(ResolverCases))]
    public void Witness_is_written_as_rlp_hex_under_the_witness_property(JsonSerializerOptions options)
    {
        using NewPayloadWithWitnessV1Result result = MakeResult(
            headers: [[0xc2, 0x01, 0x02]],
            codes: [[0x60, 0x00]],
            state: [[0xde, 0xad]],
            keys: [[0xaa]]);

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(result, options));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(document.RootElement.TryGetProperty("executionWitness", out _), Is.False,
                "JSON uses witness DATA only");
            Assert.That(document.RootElement.GetProperty("witness").GetString(),
                Is.EqualTo("0xccc3c20102c3826000c382dead"));
        }
    }

    [TestCaseSource(nameof(ResolverCases))]
    public void Missing_witness_omits_the_property(JsonSerializerOptions options)
    {
        using NewPayloadWithWitnessV1Result result = new()
        {
            Status = PayloadStatus.Syncing
        };

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(result, options));

        Assert.That(document.RootElement.TryGetProperty("witness", out _), Is.False);
    }

    [Test]
    public void Empty_witness_encodes_three_empty_lists()
    {
        using NewPayloadWithWitnessV1Result result = MakeResult(headers: [], codes: [], state: [], keys: []);

        Assert.That(WitnessHex(result), Is.EqualTo("0xc3c0c0c0"));
    }

    [Test]
    public void Headers_are_spliced_as_nested_rlp_values()
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(7).TestObject;
        byte[] headerRlp = new HeaderDecoder().EncodeAsBytes(header);

        using NewPayloadWithWitnessV1Result result = MakeResult(
            headers: [headerRlp],
            codes: [[0x60, 0x00]],
            state: [[0xde, 0xad]],
            keys: []);

        RlpReader reader = new(Bytes.FromHexString(WitnessHex(result)));
        reader.ReadSequenceLength();

        int headersContentLength = reader.ReadSequenceLength();
        byte[] decodedHeader = reader.Read(headersContentLength).ToArray();
        byte[][] codes = reader.DecodeByteArrays();
        byte[][] state = reader.DecodeByteArrays();
        reader.CheckEnd();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decodedHeader, Is.EqualTo(headerRlp));
            Assert.That(codes, Is.EqualTo(new[] { new byte[] { 0x60, 0x00 } }));
            Assert.That(state, Is.EqualTo(new[] { new byte[] { 0xde, 0xad } }));
        }
    }

    private static string WitnessHex(NewPayloadWithWitnessV1Result result)
    {
        using JsonDocument document = JsonDocument.Parse(new EthereumJsonSerializer().Serialize(result));
        return document.RootElement.GetProperty("witness").GetString()!;
    }

    private static NewPayloadWithWitnessV1Result MakeResult(
        byte[][] headers, byte[][] codes, byte[][] state, byte[][] keys) => new()
        {
            Status = PayloadStatus.Valid,
            LatestValidHash = TestItem.KeccakA,
            ExecutionWitness = new Witness
            {
                Headers = new ArrayPoolList<byte[]>(headers),
                Codes = new ArrayPoolList<byte[]>(codes),
                State = new ArrayPoolList<byte[]>(state),
                Keys = new ArrayPoolList<byte[]>(keys)
            }
        };
}
