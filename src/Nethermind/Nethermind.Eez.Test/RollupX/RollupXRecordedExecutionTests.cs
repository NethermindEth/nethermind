// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Eez.Prove;
using Nethermind.Eez.Prove.V2;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;
using Block = Nethermind.Core.Block;

namespace Nethermind.Eez.Test.RollupX;

[TestFixture("baseline-lookup", "native-osaka-lookup", 100)]
[TestFixture("baseline-static", "native-osaka-changing-static", 115)]
public class RollupXRecordedExecutionTests(string recording, string fixture, int blockCount)
{
    private const string DirectoryName = "rollupx/recorded-execution";
    private EezStatelessBlockResult[] _executed = null!;
    private Validated[] _expected = null!;
    private Begin _anchor = null!;
    private OperationsPayload _operations = null!;

    [OneTimeSetUp]
    public void ExecuteRecordedWindow()
    {
        using JsonDocument genesis = Read(fixture + ".genesis.json");
        EezChainRules rules = EezChainRules.FromGenesis(genesis.RootElement);
        using MemoryStream genesisStream = new(JsonSerializer.SerializeToUtf8Bytes(genesis.RootElement));
        ChainSpec chainSpec = new GethGenesisLoader(new EthereumJsonSerializer()).Load(genesisStream);
        EezSpecProvider specs = new(new ChainSpecBasedSpecProvider(chainSpec), rules);
        Assert.That(rules.IsRollupX, Is.True, "precondition: execution uses the recorded RollupX chain profile");

        using JsonDocument transcript = Read(recording + ".json");
        Assert.That(transcript.RootElement.GetProperty("format").GetInt32(), Is.EqualTo(2), "the recorded input format requires an explicit adapter");
        List<EezStatelessBlock> blocks = new(blockCount);
        List<Validated> expected = new(blockCount);
        SettlementInput? settlement = null;
        try
        {
            foreach (JsonElement item in transcript.RootElement.GetProperty("events").EnumerateArray())
            {
                string? direction = item.GetProperty("direction").GetString();
                if (direction is not ("client" or "server")) continue;
                byte[] encoded = Convert.FromHexString(item.GetProperty("protobuf_hex").GetString()!);
                if (direction == "server")
                {
                    ServerFrame response = ServerFrame.Parser.ParseFrom(encoded);
                    if (response.Validated is not null) expected.Add(response.Validated);
                    continue;
                }
                ClientFrame request = ClientFrame.Parser.ParseFrom(encoded);
                if (request.Begin is not null) _anchor = request.Begin;
                if (request.Block is not null)
                {
                    BlockWitness block = request.Block.Data;
                    blocks.Add(new EezStatelessBlock(block.Rlp.ToByteArray(), WindowAssembler.ToWitness(block.Witness)));
                }
                if (request.Finalize is not null) settlement = request.Finalize.Settlement;
            }
            Assert.That(blocks.Count, Is.EqualTo(blockCount), "precondition: every captured block is executed");
            Assert.That(expected.Count, Is.EqualTo(blockCount), "precondition: every execution has an independent expected hash and state root");
            Assert.That(settlement, Is.Not.Null, "precondition: the recorded finalization carries the operations blobs");
            Block terminal = Rlp.Decode<Block>(blocks[^1].Rlp)!;
            RollupXCheckpointPlan plan = RollupXCheckpointPlan.Create(terminal);
            _executed = new EezStatelessExecutor(specs, rules, LimboLogs.Instance).Execute(blocks, plan.Positions);
            _expected = expected.ToArray();
            byte[][] blobs = settlement!.Blobs.Select(static blob => blob.ToByteArray()).ToArray();
            NativeStream stream = NativeStream.Decode(BlobStream.Unpack(blobs), _anchor.RollupId);
            _operations = OperationsPayload.Decode(stream.Operations);
        }
        finally
        {
            foreach (EezStatelessBlock block in blocks) block.Witness.Dispose();
        }
    }

    [Test]
    public void Execute_RecordedWitnesses_MatchesEveryPublishedHashAndStateRoot()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_executed.Length, Is.EqualTo(blockCount), "execution cannot omit a recorded block");
            Assert.That(_executed[0].Parent.Number, Is.EqualTo(_anchor.AnchorNumber), "the window starts at the recorded predecessor");
            Assert.That(_executed[0].Block.ParentHash, Is.EqualTo(new Hash256(_anchor.AnchorHash.Span)), "the first block builds on the independent anchor hash");
            Assert.That(_executed[0].PreStateRoot, Is.EqualTo(new Hash256(_anchor.AnchorStateRoot.Span)), "the witness starts at the independent anchor state root");
        }
        for (int i = 0; i < _executed.Length; i++)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That((ulong)_executed[i].Block.Number, Is.EqualTo(_expected[i].Number), $"block {i} has the captured execution height");
                Assert.That(_executed[i].Hash, Is.EqualTo(new Hash256(_expected[i].Hash.Span)), $"block {i} matches the independent consensus hash");
                Assert.That(_executed[i].PostStateRoot, Is.EqualTo(new Hash256(_expected[i].PostStateRoot.Span)), $"block {i} matches the independent executed state root");
            }
        }
    }

    [Test]
    public void Verify_RecordedOperations_MatchesExecutedOrdinaryTransactions() =>
        Assert.That(() => RollupXOperationsCheck.Verify(_operations, _executed), Throws.Nothing,
            "the exact published operations bind the executed window, including its terminal ordinary prefix");

    [Test]
    public void Verify_TruncatedExecution_RejectsOperations() =>
        Assert.That(() => RollupXOperationsCheck.Verify(_operations, _executed[..^1]),
            Throws.TypeOf<EezSettlementException>(), "a valid prefix does not cover the published operation count");

    private static JsonDocument Read(string file)
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(StatelessFixtures.PathOf(DirectoryName, "provenance.json")));
        JsonElement entry = manifest.RootElement.GetProperty("files").GetProperty(file);
        int length = entry.GetProperty("length").GetInt32();
        Assert.That(length, Is.InRange(1, 8 * 1024 * 1024), "the recorded decompressed data has a bounded size");
        string path = StatelessFixtures.PathOf(DirectoryName, file + ".gz");
        Assert.That(new FileInfo(path).Length, Is.InRange(1L, 8L * 1024 * 1024), "the compressed input also has a bounded size");
        using FileStream source = File.OpenRead(path);
        using GZipStream compressed = new(source, CompressionMode.Decompress);
        byte[] data = new byte[length];
        compressed.ReadExactly(data);
        Assert.That(compressed.ReadByte(), Is.EqualTo(-1), "the original length includes every decompressed byte");
        Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(entry.GetProperty("sha256").GetString()),
            "the recording retains the exact immutable upstream bytes");
        return JsonDocument.Parse(data);
    }
}
