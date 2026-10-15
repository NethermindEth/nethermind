// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.IO;
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

[TestFixture("baseline-lookup")]
[TestFixture("baseline-static")]
public class RollupXRecordedExecutionTests(string recording)
{
    private const string DirectoryName = "rollupx/recorded-execution";
    private readonly string? _selectedDirectory;
    private int _blockCount;
    private EezStatelessBlockResult[] _executed = null!;
    private Validated[] _expected = null!;
    private Begin _anchor = null!;
    private OperationsPayload _operations = null!;

    internal RollupXRecordedExecutionTests(string recording, string selectedDirectory) : this(recording) => _selectedDirectory = selectedDirectory;

    internal int ExecutedBlockCount => _executed.Length;

    [OneTimeSetUp]
    public void ExecuteRecordedWindow()
    {
        using JsonDocument transcript = Read(recording + ".json");
        Assert.That(transcript.RootElement.GetProperty("format").GetInt32(), Is.EqualTo(2), "the recorded input format requires an explicit adapter");
        Assert.That(transcript.RootElement.GetProperty("case").GetString(), Is.EqualTo(recording), "the selected baseline must retain its identity");
        using JsonDocument genesis = ReadGenesis(InputDirectory(), transcript.RootElement);
        EezChainRules rules = EezChainRules.FromGenesis(genesis.RootElement);
        using MemoryStream genesisStream = new(JsonSerializer.SerializeToUtf8Bytes(genesis.RootElement));
        ChainSpec chainSpec = new GethGenesisLoader(new EthereumJsonSerializer()).Load(genesisStream);
        EezSpecProvider specs = new(new ChainSpecBasedSpecProvider(chainSpec), rules);
        Assert.That(rules.IsRollupX, Is.True, "precondition: execution uses the recorded RollupX chain profile");

        List<EezStatelessBlock> blocks = [];
        List<Validated> expected = [];
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
                    Assert.That(response.KindCase, Is.AnyOf(ServerFrame.KindOneofCase.Ready, ServerFrame.KindOneofCase.Validated, ServerFrame.KindOneofCase.Proof),
                        "the execution baseline adapter must explicitly support each response kind");
                    if (response.Validated is not null) expected.Add(response.Validated);
                    continue;
                }
                ClientFrame request = ClientFrame.Parser.ParseFrom(encoded);
                Assert.That(request.KindCase, Is.AnyOf(ClientFrame.KindOneofCase.Begin, ClientFrame.KindOneofCase.Block, ClientFrame.KindOneofCase.Finalize),
                    "the execution baseline adapter must explicitly support each request kind");
                if (request.Begin is not null)
                {
                    Assert.That(_anchor, Is.Null, "an execution baseline contains one anchor");
                    _anchor = request.Begin;
                }
                if (request.Block is not null)
                {
                    BlockWitness block = request.Block.Data;
                    blocks.Add(new EezStatelessBlock(block.Rlp.ToByteArray(), WindowAssembler.ToWitness(block.Witness)));
                }
                if (request.Finalize is not null)
                {
                    Assert.That(settlement, Is.Null, "a baseline contains one finalization range");
                    _blockCount = SelectedBlockCount(_anchor, request.Finalize);
                    settlement = request.Finalize.Settlement;
                }
            }
            Assert.That(blocks.Count, Is.EqualTo(_blockCount), "precondition: every captured block is executed");
            Assert.That(expected.Count, Is.EqualTo(_blockCount), "precondition: every execution has an independent expected hash and state root");
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
            Assert.That(_executed.Length, Is.EqualTo(_blockCount), "execution cannot omit a recorded block");
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

    private string InputDirectory() => SelectDirectory(_selectedDirectory ?? Environment.GetEnvironmentVariable("EEZ_ROLLUPX_EXECUTION_VECTOR_DIR"),
        Environment.GetEnvironmentVariable("EEZ_REQUIRE_UPSTREAM_VECTORS") == "true");

    internal static string SelectDirectory(string? selected, bool required)
    {
        if (!string.IsNullOrEmpty(selected)) return selected;
        if (required) throw new InvalidOperationException("the streaming CI job requires selected RollupX execution data");
        return StatelessFixtures.PathOf(DirectoryName, "");
    }

    internal static int SelectedBlockCount(Begin anchor, Finalize selection)
    {
        Assert.That(anchor.AnchorNumber, Is.LessThan(ulong.MaxValue), "the recorded anchor has a successor");
        Assert.That(selection.FromBlock, Is.EqualTo(anchor.AnchorNumber + 1), "the baseline starts immediately after its independent anchor");
        Assert.That(selection.ToBlock, Is.GreaterThanOrEqualTo(selection.FromBlock), "the selected range is nonempty");
        ulong span = selection.ToBlock - selection.FromBlock + 1;
        Assert.That(span, Is.InRange(1UL, 4096UL), "the independent finalization range bounds execution work");
        return (int)span;
    }

    internal static JsonDocument ReadGenesis(string directory, JsonElement transcript) =>
        ReadSelected(directory, transcript.GetProperty("fixture").GetString()! + ".genesis.json");

    private JsonDocument Read(string file) => ReadSelected(InputDirectory(), file);

    internal static JsonDocument ReadSelected(string directory, string file)
    {
        if (file.Contains('/') || file.Contains('\\')) throw new InvalidDataException("recorded execution files must be direct children of the selected directory");
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "provenance.json")));
        JsonElement entry = manifest.RootElement.GetProperty("files").GetProperty(file);
        int length = entry.GetProperty("length").GetInt32();
        Assert.That(length, Is.InRange(1, 8 * 1024 * 1024), "the recorded decompressed data has a bounded size");
        string path = Path.Combine(directory, file + ".gz");
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

public class RollupXExecutionDataTests
{
    [Test]
    public void ReadSelected_DifferentUpstreamBytes_UsesSelectedData()
    {
        using TempPath directory = TempPath.GetTempDirectory();
        Write(directory.Path, "baseline-lookup.json", "{\"selected\":77}");
        string selected = RollupXRecordedExecutionTests.SelectDirectory(directory.Path, true);

        using JsonDocument data = RollupXRecordedExecutionTests.ReadSelected(selected, "baseline-lookup.json");

        Assert.That(data.RootElement.GetProperty("selected").GetInt32(), Is.EqualTo(77), "selected upstream bytes replace bundled data even under a known baseline name");
    }

    [Test]
    public void ExecuteRecordedWindow_SelectedTwoBlockRecording_UsesSelectedFixtureRangeAndOracles()
    {
        string snapshot = StatelessFixtures.PathOf("rollupx/recorded-execution", "");
        using JsonDocument original = RollupXRecordedExecutionTests.ReadSelected(snapshot, "baseline-lookup.json");
        using JsonDocument genesis = RollupXRecordedExecutionTests.ReadGenesis(snapshot, original.RootElement);
        JsonArray events = [];
        BlockWitness? terminal = null;
        int blocks = 0;
        int validated = 0;
        foreach (JsonElement item in original.RootElement.GetProperty("events").EnumerateArray())
        {
            string? direction = item.GetProperty("direction").GetString();
            if (direction is not ("client" or "server")) continue;
            string hex = item.GetProperty("protobuf_hex").GetString()!;
            byte[] bytes = Convert.FromHexString(hex);
            if (direction == "client")
            {
                ClientFrame frame = ClientFrame.Parser.ParseFrom(bytes);
                if (frame.Block is not null)
                {
                    if (blocks++ >= 2) continue;
                    terminal = frame.Block.Data;
                }
                if (frame.Finalize is not null)
                {
                    frame.Finalize.ToBlock = 2;
                    frame.Finalize.TerminalHash = terminal!.Hash;
                    hex = Convert.ToHexStringLower(frame.ToByteArray());
                }
            }
            else
            {
                ServerFrame frame = ServerFrame.Parser.ParseFrom(bytes);
                if (frame.Proof is not null || (frame.Validated is not null && validated++ >= 2)) continue;
            }
            events.Add(new JsonObject { ["direction"] = direction, ["protobuf_hex"] = hex });
        }
        JsonObject selected = new() { ["format"] = 2, ["case"] = "baseline-lookup", ["fixture"] = "selected-new-native", ["events"] = events };
        using TempPath directory = TempPath.GetTempDirectory();
        WriteFiles(directory.Path, ("baseline-lookup.json", Encoding.UTF8.GetBytes(selected.ToJsonString())),
            ("selected-new-native.genesis.json", Encoding.UTF8.GetBytes(genesis.RootElement.GetRawText())));
        RollupXRecordedExecutionTests replay = new("baseline-lookup", directory.Path);

        replay.ExecuteRecordedWindow();
        replay.Execute_RecordedWitnesses_MatchesEveryPublishedHashAndStateRoot();

        Assert.That(replay.ExecutedBlockCount, Is.EqualTo(2), "the real adapter executes the selected two blocks instead of the bundled 100-block recording");
    }

    [Test]
    public void ReadGenesis_ChangedFixtureIdentity_UsesMatchingSelectedGenesis()
    {
        using TempPath directory = TempPath.GetTempDirectory();
        Write(directory.Path, "new-native.genesis.json", "{\"selectedGenesis\":42}");
        using JsonDocument transcript = JsonDocument.Parse("{\"fixture\":\"new-native\"}");

        using JsonDocument genesis = RollupXRecordedExecutionTests.ReadGenesis(directory.Path, transcript.RootElement);

        Assert.That(genesis.RootElement.GetProperty("selectedGenesis").GetInt32(), Is.EqualTo(42), "the selected recording determines its matching genesis");
    }

    [TestCase(null, TestName = "SelectDirectory_MissingRequiredSelection_Rejects")]
    [TestCase("", TestName = "SelectDirectory_EmptyRequiredSelection_Rejects")]
    public void SelectDirectory_RequiredSelectionMissing_Rejects(string? selected) =>
        Assert.That(() => RollupXRecordedExecutionTests.SelectDirectory(selected, true), Throws.TypeOf<InvalidOperationException>(),
            "a broken CI download cannot silently revert to bundled data");

    [TestCase(5UL, 6UL, 9UL, 4, TestName = "SelectedBlockCount_ChangedUpstreamWindow_UsesDeclaredSpan")]
    [TestCase(9UL, 10UL, 10UL, 1, TestName = "SelectedBlockCount_OneBlockWindow_UsesDeclaredSpan")]
    public void SelectedBlockCount_ChangedFinalizationRange_UsesIndependentBounds(ulong anchor, ulong from, ulong to, int expected) =>
        Assert.That(RollupXRecordedExecutionTests.SelectedBlockCount(new Begin { AnchorNumber = anchor }, new Finalize { FromBlock = from, ToBlock = to }),
            Is.EqualTo(expected), "updated recordings need no fixed 100/115-block counts in the adapter");

    [TestCase(5UL, 7UL, 9UL, TestName = "SelectedBlockCount_GapAfterAnchor_Rejects")]
    [TestCase(5UL, 6UL, 5UL, TestName = "SelectedBlockCount_ReversedRange_Rejects")]
    [TestCase(5UL, 6UL, 4102UL, TestName = "SelectedBlockCount_OverlargeRange_Rejects")]
    [TestCase(ulong.MaxValue, 0UL, 1UL, TestName = "SelectedBlockCount_AnchorOverflow_Rejects")]
    public void SelectedBlockCount_InvalidRange_RejectsBeforeExecution(ulong anchor, ulong from, ulong to) =>
        Assert.That(() => RollupXRecordedExecutionTests.SelectedBlockCount(new Begin { AnchorNumber = anchor }, new Finalize { FromBlock = from, ToBlock = to }),
            Throws.TypeOf<AssertionException>(), "malformed baseline ranges cannot change or expand execution coverage");

    [TestCase("../other.json", TestName = "ReadSelected_ParentPath_Rejects")]
    [TestCase("sub/other.json", TestName = "ReadSelected_Subdirectory_Rejects")]
    [TestCase("sub\\other.json", TestName = "ReadSelected_BackslashPath_Rejects")]
    public void ReadSelected_UnsafeFileName_RejectsBeforeReading(string file) =>
        Assert.That(() => RollupXRecordedExecutionTests.ReadSelected("unused", file), Throws.TypeOf<InvalidDataException>(),
            "fixture identities cannot escape the selected input directory");

    private static void Write(string directory, string file, string json) => WriteFiles(directory, (file, Encoding.UTF8.GetBytes(json)));

    private static void WriteFiles(string directory, params (string File, byte[] Data)[] inputs)
    {
        Directory.CreateDirectory(directory);
        Dictionary<string, object> files = new(inputs.Length);
        foreach ((string file, byte[] data) in inputs)
        {
            File.WriteAllBytes(Path.Combine(directory, file + ".gz"), GZipStreamBytes(data));
            files.Add(file, new { length = data.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(data)) });
        }
        File.WriteAllBytes(Path.Combine(directory, "provenance.json"), JsonSerializer.SerializeToUtf8Bytes(new { files }));
    }

    private static byte[] GZipStreamBytes(byte[] data)
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionMode.Compress, leaveOpen: true)) gzip.Write(data);
        return output.ToArray();
    }
}
