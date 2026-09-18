// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.BlockProcessorExtractor.Test;

[TestFixture]
public class ExtractorTests
{
    [Test]
    public void Production_extraction_matches_checked_artifacts_and_is_deterministic()
    {
        using Fixture fixture = new(copySources: false);
        string root = RepoRoot();
        string first = Path.Combine(fixture.Path, "first");
        string second = Path.Combine(fixture.Path, "second");
        Extractor.Extract(root, first);
        Extractor.Extract(root, second);
        string checkedPath = Path.Combine(root, "tools/Evm/Lean/BlockProcessorExtractor/Generated");
        using (Assert.EnterMultipleScope())
        {
            foreach (string extension in new[] { ".lean", ".ir.json", ".source-manifest.json" })
            {
                string name = Extractor.ArtifactName + extension;
                Assert.That(File.ReadAllBytes(Path.Combine(first, name)), Is.EqualTo(File.ReadAllBytes(Path.Combine(second, name))), name);
                Assert.That(File.ReadAllBytes(Path.Combine(first, name)), Is.EqualTo(File.ReadAllBytes(Path.Combine(checkedPath, name))), name);
            }
        }
    }

    [Test]
    public void Generated_Lean_is_theorem_free_and_records_external_boundary()
    {
        string generated = Path.Combine(RepoRoot(), "tools/Evm/Lean/BlockProcessorExtractor/Generated");
        string lean = File.ReadAllText(Path.Combine(generated, Extractor.ArtifactName + ".lean"));
        string ir = File.ReadAllText(Path.Combine(generated, Extractor.ArtifactName + ".ir.json"));
        using (Assert.EnterMultipleScope())
        {
            foreach (string forbidden in new[] { "theorem ", "axiom ", "sorry", "admit " })
                Assert.That(lean, Does.Not.Contain(forbidden));
            Assert.That(lean, Does.Not.Contain("Eip803x"));
            Assert.That(lean, Does.Not.Contain("import "));
            Assert.That(ir, Does.Contain("externalObligations"));
            Assert.That(ir, Does.Contain("no C# semantic-model"));
            Assert.That(ir, Does.Contain("world-state rollback and database durability are not proved"));
        }
    }

    [TestCaseSource(nameof(Mutations))]
    public void Mutated_source_fails_closed(string path, string before, string after)
    {
        using Fixture fixture = new();
        fixture.Replace(path, before, after);
        string output = Path.Combine(fixture.Path, "output");
        Assert.That(() => Extractor.Extract(fixture.Path, output), Throws.TypeOf<ExtractionException>());
        Assert.That(Directory.Exists(output), Is.False, "Rejected sources must not emit partial accepted artifacts.");
    }

    [Test]
    public void Competing_partial_declaration_fails_closed()
    {
        using Fixture fixture = new();
        File.WriteAllText(Path.Combine(fixture.Path, Extractor.ProcessingDirectory, "Competing.cs"),
            "namespace Nethermind.Consensus.Processing; public partial class BlockProcessor { private object ProcessBlock(object value) => value; }");
        Assert.That(() => Extractor.Extract(fixture.Path, Path.Combine(fixture.Path, "output")),
            Throws.TypeOf<ExtractionException>().With.Message.Contains("Competing declaration"));
    }

    [Test]
    public void Trivia_changes_are_accepted_but_change_source_identity()
    {
        using Fixture fixture = new();
        string first = Path.Combine(fixture.Path, "first");
        Extractor.Extract(fixture.Path, first);
        fixture.Replace(Extractor.BlockPath, "ApplyDaoTransition(suggestedBlock);", "ApplyDaoTransition( /* reviewed trivia */ suggestedBlock); ");
        string second = Path.Combine(fixture.Path, "second");
        Extractor.Extract(fixture.Path, second);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(Path.Combine(first, Extractor.ArtifactName + ".lean")),
                Is.EqualTo(File.ReadAllBytes(Path.Combine(second, Extractor.ArtifactName + ".lean"))));
            Assert.That(File.ReadAllBytes(Path.Combine(first, Extractor.ArtifactName + ".source-manifest.json")),
                Is.Not.EqualTo(File.ReadAllBytes(Path.Combine(second, Extractor.ArtifactName + ".source-manifest.json"))));
        }
    }

    [Test]
    public void Reviewed_checkpoint_constant_change_flows_from_source_through_ir_into_Lean()
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.BranchPath, "MaxUncommittedBlocks = 64", "MaxUncommittedBlocks = 128");
        string template = File.ReadAllText(Path.Combine(RepoRoot(),
            "tools/Evm/Lean/BlockProcessorExtractor/Admission/BranchProcessor.cs.txt"));
        Assert.That(template, Does.Contain("MaxUncommittedBlocks = 64"));
        Dictionary<string, string> reviewedTemplates = new(StringComparer.Ordinal)
        {
            ["BranchProcessor"] = template.Replace("MaxUncommittedBlocks = 64", "MaxUncommittedBlocks = 128", StringComparison.Ordinal),
        };
        string output = Path.Combine(fixture.Path, "output");
        Extractor.Extract(fixture.Path, output, reviewedTemplates);
        using JsonDocument ir = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, Extractor.ArtifactName + ".ir.json")));
        string? checkpoint = ir.RootElement.GetProperty("checkpoint").GetString();
        string lean = File.ReadAllText(Path.Combine(output, Extractor.ArtifactName + ".lean"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(checkpoint, Does.Contain("(index % 128)").And.Not.Contain("(index % 64)"));
            Assert.That(lean, Does.Contain("(index % 128)").And.Not.Contain("(index % 64)"));
        }
    }

    [Test]
    public void Serialized_ir_rejects_unknown_malformed_top_and_nested_null()
    {
        byte[] bytes = CheckedBytes(".ir.json");
        JsonObject unknown = ParseObject(bytes);
        unknown["unknown"] = true;
        Assert.That(() => Extractor.DeserializeIr(Encoding.UTF8.GetBytes(unknown.ToJsonString())),
            Throws.TypeOf<ExtractionException>());
        Assert.That(() => Extractor.DeserializeIr("{"u8.ToArray()), Throws.TypeOf<ExtractionException>());
        Assert.That(() => Extractor.DeserializeIr("null"u8.ToArray()), Throws.TypeOf<ExtractionException>());
        Assert.That(() => Extractor.DeserializeIr(null!), Throws.TypeOf<ExtractionException>());

        JsonObject topNull = ParseObject(bytes);
        topNull["phases"] = null;
        Assert.That(() => Extractor.DeserializeIr(Encoding.UTF8.GetBytes(topNull.ToJsonString())),
            Throws.TypeOf<ExtractionException>());
        JsonObject nestedNull = ParseObject(bytes);
        nestedNull["phases"]!.AsArray()[0] = null;
        Assert.That(() => Extractor.DeserializeIr(Encoding.UTF8.GetBytes(nestedNull.ToJsonString())),
            Throws.TypeOf<ExtractionException>());
        JsonObject nestedFieldNull = ParseObject(bytes);
        nestedFieldNull["phases"]!.AsArray()[0]!["guards"] = null;
        Assert.That(() => Extractor.DeserializeIr(Encoding.UTF8.GetBytes(nestedFieldNull.ToJsonString())),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Serialized_ir_rejects_case_aliases_and_omitted_required_default_fields()
    {
        byte[] bytes = CheckedBytes(".ir.json");
        JsonObject caseAlias = ParseObject(bytes);
        JsonNode schema = caseAlias["schemaVersion"]!.DeepClone();
        caseAlias.Remove("schemaVersion");
        caseAlias["SchemaVersion"] = schema;
        Assert.That(() => Extractor.DeserializeIr(Encoding.UTF8.GetBytes(caseAlias.ToJsonString())),
            Throws.TypeOf<ExtractionException>());

        JsonObject nestedCaseAlias = ParseObject(bytes);
        JsonObject phase = nestedCaseAlias["phases"]!.AsArray()[0]!.AsObject();
        JsonNode name = phase["name"]!.DeepClone();
        phase.Remove("name");
        phase["Name"] = name;
        Assert.That(() => Extractor.DeserializeIr(Encoding.UTF8.GetBytes(nestedCaseAlias.ToJsonString())),
            Throws.TypeOf<ExtractionException>());

        JsonObject omittedTop = ParseObject(bytes);
        omittedTop.Remove("schemaVersion");
        Assert.That(() => Extractor.DeserializeIr(Encoding.UTF8.GetBytes(omittedTop.ToJsonString())),
            Throws.TypeOf<ExtractionException>());
        JsonObject omittedNested = ParseObject(bytes);
        omittedNested["phases"]!.AsArray()[0]!.AsObject().Remove("line");
        Assert.That(() => Extractor.DeserializeIr(Encoding.UTF8.GetBytes(omittedNested.ToJsonString())),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Every_serialized_ir_field_is_bound_to_the_source_derived_document()
    {
        byte[] bytes = CheckedBytes(".ir.json");
        Extractor.Document expected = Extractor.DeserializeIr(bytes);
        Action<JsonObject>[] mutations =
        [
            root => root["schemaVersion"] = 2,
            root => root["phases"] = new JsonArray(),
            root => root["receiptEvents"] = new JsonArray(),
            root => root["blockEvents"] = new JsonArray(),
            root => root["branchEvents"] = new JsonArray(),
            root => root["commitTarget"] = root["phases"]!.AsArray()[0]!.DeepClone(),
            root => root["finalization"] = new JsonArray(),
            root => root["cleanup"] = new JsonArray(),
            root => root["headerFields"] = new JsonArray(),
            root => root["artifacts"] = new JsonArray(),
            root => root["checkpoint"] = "false",
            root => root["externalObligations"]!.AsArray()[0] = "changed obligation",
            root => root["phases"]!.AsArray()[0]!["name"] = "changed",
            root => root["phases"]!.AsArray()[0]!["source"] = "changed",
            root => root["phases"]!.AsArray()[0]!["method"] = "changed",
            root => root["phases"]!.AsArray()[0]!["line"] = 1,
            root => root["phases"]!.AsArray()[0]!["syntax"] = "changed",
            root => root["phases"]!.AsArray()[0]!["guards"] = new JsonArray("changed"),
            root => root["headerFields"]!.AsArray()[0]!["target"] = "changed",
            root => root["headerFields"]!.AsArray()[0]!["value"] = "changed",
        ];

        foreach (Action<JsonObject> mutate in mutations)
        {
            JsonObject actualJson = ParseObject(bytes);
            mutate(actualJson);
            Assert.That(() => Extractor.ValidateIr(
                    Extractor.DeserializeIr(Encoding.UTF8.GetBytes(actualJson.ToJsonString())), expected),
                Throws.TypeOf<ExtractionException>());
        }
    }

    [Test]
    public void Serialized_manifest_is_strict_and_binds_identities_admissions_and_fingerprints()
    {
        byte[] bytes = CheckedBytes(".source-manifest.json");
        Extractor.Manifest expected = Extractor.DeserializeManifest(bytes);

        JsonObject unknown = ParseObject(bytes);
        unknown["unknown"] = true;
        Assert.That(() => Extractor.DeserializeManifest(Encoding.UTF8.GetBytes(unknown.ToJsonString())),
            Throws.TypeOf<ExtractionException>());
        Assert.That(() => Extractor.DeserializeManifest("{"u8.ToArray()), Throws.TypeOf<ExtractionException>());
        Assert.That(() => Extractor.DeserializeManifest("null"u8.ToArray()), Throws.TypeOf<ExtractionException>());
        Assert.That(() => Extractor.DeserializeManifest(null!), Throws.TypeOf<ExtractionException>());

        Action<JsonObject>[] mutations =
        [
            root => root["schemaVersion"] = 2,
            root => root["sourceIdentityAuthority"] = "changed",
            root => root["sources"]!.AsArray()[0]!["path"] = "changed",
            root => root["sources"]!.AsArray()[0]!["sha256"] = new string('0', 64),
            root => root["admittedMethods"]!.AsArray()[0]!["key"] = "changed",
            root => root["admittedMethods"]!.AsArray()[0]!["templateSha256"] = new string('0', 64),
            root => root["admittedMethods"]!.AsArray()[0]!["sourceSyntaxSha256"] = new string('0', 64),
            root => root["irSha256"] = new string('0', 64),
            root => root["leanSha256"] = new string('0', 64),
        ];
        foreach (Action<JsonObject> mutate in mutations)
        {
            JsonObject actualJson = ParseObject(bytes);
            mutate(actualJson);
            Assert.That(() => Extractor.ValidateManifest(
                    Extractor.DeserializeManifest(Encoding.UTF8.GetBytes(actualJson.ToJsonString())), expected),
                Throws.TypeOf<ExtractionException>());
        }

        JsonObject duplicate = ParseObject(bytes);
        duplicate["admittedMethods"]!.AsArray()[1] = duplicate["admittedMethods"]!.AsArray()[0]!.DeepClone();
        Assert.That(() => Extractor.DeserializeManifest(Encoding.UTF8.GetBytes(duplicate.ToJsonString())),
            Throws.TypeOf<ExtractionException>().With.Message.Contains("Duplicate owner-qualified admission identity"));

        JsonObject nestedNull = ParseObject(bytes);
        nestedNull["sources"]!.AsArray()[0] = null;
        Assert.That(() => Extractor.DeserializeManifest(Encoding.UTF8.GetBytes(nestedNull.ToJsonString())),
            Throws.TypeOf<ExtractionException>());
        JsonObject caseAlias = ParseObject(bytes);
        JsonObject admission = caseAlias["admittedMethods"]!.AsArray()[0]!.AsObject();
        JsonNode key = admission["key"]!.DeepClone();
        admission.Remove("key");
        admission["Key"] = key;
        Assert.That(() => Extractor.DeserializeManifest(Encoding.UTF8.GetBytes(caseAlias.ToJsonString())),
            Throws.TypeOf<ExtractionException>());
        JsonObject omitted = ParseObject(bytes);
        omitted.Remove("schemaVersion");
        Assert.That(() => Extractor.DeserializeManifest(Encoding.UTF8.GetBytes(omitted.ToJsonString())),
            Throws.TypeOf<ExtractionException>());
    }

    private static IEnumerable<TestCaseData> Mutations()
    {
        yield return Mutation("dao_reordered", Extractor.BlockPath,
            "ApplyDaoTransition(suggestedBlock);\n        Block block = PrepareBlockForProcessing(suggestedBlock);",
            "Block block = PrepareBlockForProcessing(suggestedBlock);\n        ApplyDaoTransition(suggestedBlock);");
        yield return Mutation("beacon_missing", Extractor.BlockPath,
            "_systemContractHandler.StoreBeaconRoot(block, spec, NullTxTracer.Instance);", "");
        yield return Mutation("beacon_duplicate", Extractor.BlockPath,
            "_systemContractHandler.StoreBeaconRoot(block, spec, NullTxTracer.Instance);",
            "_systemContractHandler.StoreBeaconRoot(block, spec, NullTxTracer.Instance); _systemContractHandler.StoreBeaconRoot(block, spec, NullTxTracer.Instance);");
        yield return Mutation("beacon_wrong_target", Extractor.BlockPath,
            "_systemContractHandler.StoreBeaconRoot(block, spec, NullTxTracer.Instance);",
            "beaconBlockRootHandler.StoreBeaconRoot(block, spec, NullTxTracer.Instance);");
        yield return Mutation("alias_escape", Extractor.BlockPath,
            "BlockHeader header = block.Header;", "BlockHeader header = block.Header; EscapedHeader = header;");
        yield return Mutation("alias_substitution", Extractor.BlockPath,
            "BlockHeader header = block.Header;", "BlockHeader header = block.Header.Clone();");
        yield return Mutation("closure_escape", Extractor.BlockPath,
            "_balManager.Setup(block);", "System.Action hidden = () => _balManager.Setup(block);");
        yield return Mutation("early_return", Extractor.BlockPath,
            "_balManager.Setup(block);", "_balManager.Setup(block); if (block.IsGenesis) return [];");
        yield return Mutation("receipt_order", Extractor.BlockPath,
            "CalculateBlooms(receipts);\n            header.ReceiptsRoot = CalculateReceiptsRoot(receipts, spec, block);",
            "header.ReceiptsRoot = CalculateReceiptsRoot(receipts, spec, block);\n            CalculateBlooms(receipts);");
        yield return Mutation("receipt_wrong_input", Extractor.BlockPath,
            "header.ReceiptsRoot = CalculateReceiptsRoot(receipts, spec, block);",
            "header.ReceiptsRoot = CalculateReceiptsRoot([], spec, block);");
        yield return Mutation("withdrawal_commit_missing", Extractor.BlockPath,
            "CommitState(spec);\n\n            _systemContractHandler.ProcessExecutionRequests", "_systemContractHandler.ProcessExecutionRequests");
        yield return Mutation("requests_wrong_state", Extractor.BlockPath,
            "ProcessExecutionRequests(block, _stateProvider, receipts, spec)", "ProcessExecutionRequests(block, null, receipts, spec)");
        yield return Mutation("storage_roots_disabled", Extractor.BlockPath,
            "_stateProvider.Commit(spec, commitRoots: true);", "_stateProvider.Commit(spec, commitRoots: false);");
        yield return Mutation("state_root_wrong_target", Extractor.BlockPath,
            "header.StateRoot = _stateProvider.StateRoot;", "header.ReceiptsRoot = _stateProvider.StateRoot;");
        yield return Mutation("bal_missing", Extractor.BlockPath,
            "_balManager.SetBlockAccessList(block);", "");
        yield return Mutation("hash_missing", Extractor.BlockPath, "header.Hash = header.CalculateHash();", "");
        yield return Mutation("cleanup_inverted", Extractor.BlockPath,
            "if (!processed) block.DisposeAccountChanges();", "if (processed) block.DisposeAccountChanges();");
        yield return Mutation("validation_bypassed", Extractor.BlockPath,
            "!options.ContainsFlag(ProcessingOptions.NoValidation) && !blockValidator.ValidateProcessedBlock",
            "options.ContainsFlag(ProcessingOptions.NoValidation) && !blockValidator.ValidateProcessedBlock");
        yield return Mutation("artifact_wrong_target", Extractor.BlockPath,
            "suggestedBlock.ExecutionRequests = processedBlock.ExecutionRequests;",
            "processedBlock.ExecutionRequests = suggestedBlock.ExecutionRequests;");
        yield return Mutation("commit_wrong_block", Extractor.BranchPath,
            "PreCommitBlock(suggestedBlock.Header);", "PreCommitBlock(preBlockBaseBlock);");
        yield return Mutation("commit_gated_readonly", Extractor.BranchPath,
            "PreCommitBlock(suggestedBlock.Header);", "if (notReadOnly) PreCommitBlock(suggestedBlock.Header);");
        yield return Mutation("commit_duplicate", Extractor.BranchPath,
            "stateProvider.CommitTree(block.Number);", "stateProvider.CommitTree(block.Number); stateProvider.CommitTree(block.Number);");
        yield return Mutation("scope_reset_before_commit", Extractor.BranchPath,
            "PreCommitBlock(suggestedBlock.Header);", "stateProvider.Reset(); PreCommitBlock(suggestedBlock.Header);");
        yield return Mutation("scope_finally_missing", Extractor.BranchPath,
            "blockProcessor.TransactionsExecuted -= CancelBackgroundWork;\n                worldStateCloser?.Dispose();",
            "blockProcessor.TransactionsExecuted -= CancelBackgroundWork;");
        yield return Mutation("checkpoint_changed", Extractor.BranchPath,
            "i % MaxUncommittedBlocks == 0 && isNotAtTheEdge", "i % MaxUncommittedBlocks == 1 && isNotAtTheEdge");
        yield return Mutation("checkpoint_constant_changed", Extractor.BranchPath,
            "MaxUncommittedBlocks = 64", "MaxUncommittedBlocks = 128");
        yield return Mutation("result_invalid_classification", Extractor.ChainPath,
            "processedBlocks = null;", "processedBlocks = [];");
        yield return Mutation("head_update_gate_inverted", Extractor.ChainPath,
            "!options.ContainsFlag(ProcessingOptions.DoNotUpdateHead)", "options.ContainsFlag(ProcessingOptions.DoNotUpdateHead)");
        yield return Mutation("finalization_target_changed", Extractor.ChainPath,
            "lastProcessed.Header.TotalDifficulty = suggestedBlock.TotalDifficulty;", "suggestedBlock.Header.TotalDifficulty = lastProcessed.TotalDifficulty;");
        yield return Mutation("header_projection_removed", Extractor.HeaderPath,
            "dst.RequestsHash = RequestsHash;", "");
        yield return Mutation("header_projection_wrong_source", Extractor.HeaderPath,
            "dst.BlobGasUsed = BlobGasUsed;", "dst.BlobGasUsed = ExcessBlobGas;");
        yield return Mutation("competing_overload", Extractor.BlockPath,
            "    private void CommitState(IReleaseSpec spec)", "    private void CommitState(object spec) { }\n\n    private void CommitState(IReleaseSpec spec)");
        yield return Mutation("using_alias", Extractor.BlockPath,
            "using System;", "using System;\nusing IWorldState = Other.WorldState;");
        yield return Mutation("namespace_using_alias", Extractor.BlockPath,
            "namespace Nethermind.Consensus.Processing;",
            "namespace Nethermind.Consensus.Processing;\nusing IWorldState = Other.WorldState;");
    }

    private static TestCaseData Mutation(string name, string path, string before, string after) =>
        new TestCaseData(path, before, after).SetName("Rejects_" + name);

    private static byte[] CheckedBytes(string extension) => File.ReadAllBytes(Path.Combine(
        RepoRoot(), "tools/Evm/Lean/BlockProcessorExtractor/Generated", Extractor.ArtifactName + extension));

    private static JsonObject ParseObject(byte[] bytes) => JsonNode.Parse(bytes)!.AsObject();

    private static string RepoRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, Extractor.BlockPath))) return directory.FullName;
        throw new InvalidOperationException("Cannot find repository root.");
    }

    private sealed class Fixture : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "block-extractor-" + Guid.NewGuid().ToString("N"));

        internal Fixture(bool copySources = true)
        {
            Directory.CreateDirectory(Path);
            if (!copySources) return;
            string root = RepoRoot();
            foreach (string source in Extractor.Paths)
            {
                string destination = System.IO.Path.Combine(Path, source);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
                File.Copy(System.IO.Path.Combine(root, source), destination);
            }
        }

        internal void Replace(string source, string before, string after)
        {
            string destination = System.IO.Path.Combine(Path, source);
            string text = File.ReadAllText(destination).Replace("\r\n", "\n");
            Assert.That(text, Does.Contain(before), "Mutation target must exist.");
            File.WriteAllText(destination, text.Replace(before, after, StringComparison.Ordinal));
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
