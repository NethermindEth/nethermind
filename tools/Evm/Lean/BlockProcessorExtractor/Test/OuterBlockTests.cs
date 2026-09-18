// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.BlockProcessorExtractor.Test;

[TestFixture]
public class OuterBlockTests
{
    private static readonly string[] Slices = [OuterBlockExtractor.Finite, OuterBlockExtractor.Publication];

    private static TestCaseData SliceCase(string name, params object?[] arguments) =>
        new TestCaseData([name, .. arguments]).SetCategory(name);
    private static IEnumerable<TestCaseData> SliceCases() => Slices.Select(name => SliceCase(name));
    private static IEnumerable<TestCaseData> EmptyTargetCases() =>
        Slices.SelectMany(name => new string?[] { null, "", " \t\r\n" }.Select(target => SliceCase(name, target)));
    private static IEnumerable<TestCaseData> RosterCases() =>
        Slices.SelectMany(name => new[] { "schemaVersion", "extractorVersion", "acceptanceState", "sources", "sourceHash",
            "dependencies", "operations", "duplicate", "unknown" }.Select(mutation => SliceCase(name, mutation)));
    private static IEnumerable<TestCaseData> DependencyCases() =>
        Slices.SelectMany(name => new[] { "template", "specification", "refinement", "extractor" }
            .SelectMany(kind => new[] { false, true }.Select(missing => SliceCase(name, kind, missing))));
    private static IEnumerable<TestCaseData> AtomicCases() =>
        Slices.SelectMany(name => new[] { false, true }.Select(blockManifest => SliceCase(name, blockManifest)));
    private static IEnumerable<TestCaseData> InventoryMutationCases() =>
        Slices.SelectMany(name => new[] { "missing", "duplicate", "unexpected", "descendant", "empty", "reordered", "aliased" }
            .Select(mutation => SliceCase(name, mutation)));

    private static string Root()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, OuterBlockExtractor.ChainPath))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Cannot find outer source root.");
    }

    [TestCaseSource(nameof(SliceCases))]
    public void Source_only_drafts_are_deterministic_and_not_emittable(string name)
    {
        string root = Root();
        OuterBlockExtractor.Document first = OuterBlockExtractor.AuditSourceForTest(root, name);
        OuterBlockExtractor.Document second = OuterBlockExtractor.AuditSourceForTest(root, name);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.AcceptanceState, Is.EqualTo("static-draft"));
            Assert.That(first.Sources, Has.Length.EqualTo(23));
            Assert.That(first.Dependencies, Is.Empty);
            Assert.That(first.ControlFlows, Has.Length.EqualTo(6));
            Assert.That(first.Sites, Has.Length.EqualTo(51));
            Assert.That(first.Sites.All(site => !string.IsNullOrWhiteSpace(site.Target) && site.CfgBlocks.Length != 0), Is.True);
            Assert.That(OuterBlockExtractor.Serialize(first), Is.EqualTo(OuterBlockExtractor.Serialize(second)));
            Assert.That(() => OuterBlockExtractor.ReadDocument(OuterBlockExtractor.Serialize(first)),
                Throws.TypeOf<ExtractionException>().With.Message.EqualTo("Outer IR header/status/option domain changed."));
            Assert.That(() => OuterBlockExtractor.Emit(root, first),
                Throws.TypeOf<ExtractionException>().With.Message.EqualTo("Outer IR header/status/option domain changed."));
        }
    }

    [TestCaseSource(nameof(SourceMutations))]
    [Category("OuterShared")]
    public void Source_mutations_compile_before_the_specific_admission_rejection(string path, string before, string after, string anchor)
    {
        string root = Root();
        Assert.DoesNotThrow(() => OuterBlockExtractor.AuditSourceForTest(root, OuterBlockExtractor.Finite), "Unmutated admission is mandatory.");
        string source = File.ReadAllText(Path.Combine(root, path)).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.That(Regex.Matches(source, Regex.Escape(before)).Count, Is.EqualTo(1), "A mutation must identify exactly one source site.");
        Dictionary<string, string> overrides = new(StringComparer.Ordinal) { [path] = source.Replace(before, after, StringComparison.Ordinal) };
        Assert.DoesNotThrow(() => OuterBlockExtractor.RequireCompilationForTest(root, overrides), "A syntax/type/closure failure must not count as admission rejection.");
        Assert.That(() => OuterBlockExtractor.AuditSourceForTest(root, OuterBlockExtractor.Finite, overrides),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(OuterBlockExtractor.Diagnostic(anchor)));
    }

    private static IEnumerable<TestCaseData> SourceMutations()
    {
        yield return Branch("for (int i = 0; i < blocksCount; i++)", "for (int i = 0; i < blocksCount; i += 2)", "finite.increment");
        yield return Branch("Block[] processedBlocks = new Block[blocksCount];", "Block[] processedBlocks = new Block[blocksCount + 1];", "finite.allocate");
        yield return Branch("int processedBlocksCount = 0;", "int processedBlocksCount = 1;", "finite.zeroPrefix");
        yield return Branch("blocksProcessingEventArgs = new BlocksProcessingEventArgs(suggestedBlocks);",
            "blocksProcessingEventArgs = new BlocksProcessingEventArgs(new Block[] { suggestedBlock });", "finite.originalList");
        yield return Branch("suggestedBlock = suggestedBlocks[i];", "suggestedBlock = suggestedBlocks[0];", "finite.suggested");
        yield return Branch("if (i > 0)", "if (i >= 0)", "finite.refresh");
        yield return Branch(": options | ProcessingOptions.ForceSequentialBlockAccessList;", ": options | ProcessingOptions.NoValidation;", "finite.options");
        yield return Branch("return processedBlocks;", "return [];", "finite.return");
        yield return Branch("preWarmTask ??= PreWarmTransactions(suggestedBlock, preBlockBaseBlock, spec, backgroundCancellation.Token);",
            "preWarmTask ??= PreWarmTransactions(suggestedBlock, baseBlock, spec, backgroundCancellation.Token);", "finite.prewarm");
        yield return Chain("processingBranch.BlocksToProcess,\n                options,\n                tracer,", "processingBranch.Blocks,\n                options,\n                tracer,", "wrapper.call");
        yield return Chain("options,\n                tracer,\n                token);", "options,\n                tracer,\n                default);", "wrapper.call");
        yield return Chain("token);\n            error = null;", "token);\n            error = \"stale\";", "wrapper.error");
        yield return Chain("catch (InvalidBlockException ex)\n        {", "catch (InvalidBlockException ex) when (false)\n        {", "invalid.catch");
        yield return Chain("invalidBlockHash = ex.InvalidBlock.Hash;", "invalidBlockHash = processingBranch.BaseBlock?.Hash;", "invalid.hash");
        yield return Chain("processingBranch.BlocksToProcess.FirstOrDefault(b => b.Hash == invalidBlockHash)",
            "processingBranch.BlocksToProcess.LastOrDefault(b => b.Hash == invalidBlockHash)", "invalid.first");
        yield return Chain("invalidBlockHash is not null && !options.ContainsFlag(ProcessingOptions.ReadOnlyChain)",
            "invalidBlockHash is not null && options.ContainsFlag(ProcessingOptions.ReadOnlyChain)", "invalid.deleteGuard");
        yield return Chain("_blockTree.DeleteInvalidBlock(processingBranch.BlocksToProcess[i]);",
            "_blockTree.DeleteInvalidBlock(processingBranch.BlocksToProcess[0]);", "invalid.deleteEach");
        yield return Chain("lastProcessed = processedBlocks[^1];", "lastProcessed = processedBlocks[0];", "outer.last");
        yield return Chain("lastProcessed.Header.TotalDifficulty = suggestedBlock.TotalDifficulty;",
            "lastProcessed.Header.TotalDifficulty = totalDifficulty;", "outer.difficulty");
        yield return Chain("lastProcessed.Header.TotalDifficulty = suggestedBlock.TotalDifficulty;",
            "suggestedBlock.Header.TotalDifficulty = lastProcessed.TotalDifficulty;", "outer.difficulty");
        yield return Chain("TryUpdateMainChain(suggestedBlock.Header, wereProcessed: true, preloadedBlocks: processingBranch.Blocks.AsSpan())",
            "TryUpdateMainChain(lastProcessed!.Header, wereProcessed: true, preloadedBlocks: processingBranch.Blocks.AsSpan())", "outer.head");
        yield return Chain("wereProcessed: true, preloadedBlocks: processingBranch.Blocks.AsSpan()",
            "wereProcessed: false, preloadedBlocks: processingBranch.Blocks.AsSpan()", "outer.head");
        yield return Chain("wereProcessed: true, preloadedBlocks: processingBranch.Blocks.AsSpan()",
            "wereProcessed: true, forceUpdateHeadBlock: true, preloadedBlocks: processingBranch.Blocks.AsSpan()", "outer.head");
        yield return Chain("preloadedBlocks: processingBranch.Blocks.AsSpan()", "preloadedBlocks: processedBlocks.AsSpan()", "outer.head");
        yield return Chain("_blockTree.MarkChainAsProcessed(processingBranch.Blocks);", "_blockTree.MarkChainAsProcessed(processedBlocks);", "outer.mark");
        yield return Chain("if ((options & ProcessingOptions.MarkAsProcessed) == ProcessingOptions.MarkAsProcessed)",
            "if (!options.ContainsFlag(ProcessingOptions.DoNotUpdateHead))\n        if ((options & ProcessingOptions.MarkAsProcessed) == ProcessingOptions.MarkAsProcessed)", "outer.independentGuards");
        yield return Chain("Blocks.Dispose();\n            BlocksToProcess.Dispose();", "BlocksToProcess.Dispose();\n            Blocks.Dispose();", "outer.dispose");
        yield return Chain("_stopwatch.Stop();", "_stopwatch.Restart();", "outer.stop");
        yield return Chain("processingBranch.Blocks.Clear();", "processingBranch.BlocksToProcess.Clear();", "prepare.forceClear");
        yield return Chain("blocksToProcess.Add(suggestedBlock);", "blocksToProcess.Add(processingBranch.Blocks[0]);", "prepare.forceAdd");
        yield return Chain("ArrayPoolList<Block> blocksToProcess = processingBranch.BlocksToProcess;",
            "ArrayPoolList<Block> blocksToProcess = processingBranch.Blocks;", "prepare.inputs");
        yield return Chain("blocksToProcess.Add(block);", "blocksToProcess.Add(processingBranch.Blocks[0]);", "prepare.copy");
        yield return Chain("Block firstBlock = blocksToProcess[0];", "Block firstBlock = blocksToProcess[1];", "prepare.nonempty");
    }
    private static TestCaseData Branch(string before, string after, string anchor) => Mutation(Extractor.BranchPath, before, after, anchor);
    private static TestCaseData Chain(string before, string after, string anchor) => Mutation(OuterBlockExtractor.ChainPath, before, after, anchor);
    private static TestCaseData Mutation(string path, string before, string after, string anchor) =>
        new TestCaseData(path, before, after, anchor).SetName("outer_source_" + anchor + "_" +
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(after)))[..8]);

    private static OuterBlockExtractor.Document AcceptedDocument(string root, string name)
    {
        Assert.DoesNotThrow(() => OuterBlockExtractor.Check(root, Path.Combine(root, OuterBlockExtractor.Package, "Generated"), name),
            "Accepted-artifact tests require the real serialized regeneration chain; do not forge or skip a draft baseline.");
        return OuterBlockExtractor.ReadDocument(File.ReadAllBytes(Path.Combine(root, OuterBlockExtractor.Package, "Generated", name + ".ir.json")));
    }

    [TestCaseSource(nameof(EmptyTargetCases))]
    public void Reader_and_emitter_reject_empty_targets(string name, string? target)
    {
        string root = Root();
        OuterBlockExtractor.Document original = AcceptedDocument(root, name);
        Assert.DoesNotThrow(() => OuterBlockExtractor.Emit(root, original));
        OuterBlockExtractor.Site[] sites = [.. original.Sites];
        sites[0] = sites[0] with { Target = target! };
        OuterBlockExtractor.Document mutation = original with { Sites = sites };
        Assert.That(() => OuterBlockExtractor.Emit(root, mutation), Throws.TypeOf<ExtractionException>().With.Message.EqualTo("Outer IR invalid site/target/CFG/dataflow evidence."));
        Assert.That(() => OuterBlockExtractor.ReadDocument(OuterBlockExtractor.Serialize(mutation)), Throws.TypeOf<ExtractionException>().With.Message.EqualTo(
            target is null ? "Null outer JSON field." : "Outer IR invalid site/target/CFG/dataflow evidence."));
    }

    [TestCaseSource(nameof(RosterCases))]
    public void Exact_ir_rosters_fail_closed(string name, string mutation)
    {
        string root = Root();
        JsonObject json = JsonNode.Parse(OuterBlockExtractor.Serialize(AcceptedDocument(root, name)))!.AsObject();
        switch (mutation)
        {
            case "schemaVersion": json[mutation] = 0; break;
            case "extractorVersion": json[mutation] = "0.0.0"; break;
            case "acceptanceState": json[mutation] = "static-draft"; break;
            case "sources": case "dependencies": case "operations": json[mutation] = new JsonArray(); break;
            case "sourceHash": json["sources"]![0]!["sha256"] = new string('0', 64); break;
            case "unknown": json["surprise"] = true; break;
        }
        string text = json.ToJsonString();
        if (mutation == "duplicate") text = text.Insert(1, "\"schemaVersion\":1,");
        ExtractionException? failure = Assert.Throws<ExtractionException>(() => OuterBlockExtractor.ReadDocument(Encoding.UTF8.GetBytes(text)));
        if (mutation == "unknown")
        {
            Assert.That(failure!.Message, Does.StartWith("Invalid outer JSON: ").And.Contain("surprise"));
        }
        else
        {
            string diagnostic = mutation switch
            {
                "duplicate" => "Duplicate/aliased outer JSON field.",
                "sources" or "sourceHash" or "dependencies" or "operations" => "Outer IR exact source/dependency/operation roster changed.",
                _ => "Outer IR header/status/option domain changed.",
            };
            Assert.That(failure!.Message, Is.EqualTo(diagnostic));
        }
    }

    [Test]
    [Category("OuterShared")]
    public void Complete_upstream_validator_rejects_changed_triplets([Values("schema", "duplicate", "sources", "dependencies", "status", "lean", "missing")] string mutation)
    {
        string root = Root();
        string scratch = Path.Combine(Path.GetTempPath(), "outer-upstream-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(scratch);
            foreach (string extension in new[] { ".ir.json", ".lean", ".source-manifest.json" })
                File.Copy(Path.Combine(root, OuterBlockExtractor.Package, "Generated", "BranchAcceptedIteration" + extension),
                    Path.Combine(scratch, "BranchAcceptedIteration" + extension));
            Assert.DoesNotThrow(() => OuterBlockExtractor.ValidateUpstream(root, OuterBlockExtractor.Finite, scratch), "Full accepted upstream baseline must pass first.");
            string ir = Path.Combine(scratch, "BranchAcceptedIteration.ir.json");
            string diagnostic;
            if (mutation == "lean")
            {
                File.AppendAllText(Path.Combine(scratch, "BranchAcceptedIteration.lean"), "\n-- stale\n");
                diagnostic = "Branch Lean differs from the validated serialized IR.";
            }
            else if (mutation == "missing")
            {
                File.Delete(ir);
                Assert.That(() => OuterBlockExtractor.ValidateUpstream(root, OuterBlockExtractor.Finite, scratch), Throws.TypeOf<FileNotFoundException>());
                return;
            }
            else
            {
                JsonObject json = JsonNode.Parse(File.ReadAllText(ir))!.AsObject();
                if (mutation == "schema") json["schemaVersion"] = 0;
                if (mutation == "status") json["acceptanceState"] = "static-draft";
                if (mutation is "sources" or "dependencies") json[mutation] = new JsonArray();
                string text = json.ToJsonString();
                if (mutation == "duplicate") text = text.Insert(1, "\"schemaVersion\":1,");
                File.WriteAllText(ir, text);
                diagnostic = mutation == "duplicate" ? "Duplicate/aliased branch JSON field." : "Branch IR header/closed roster changed.";
            }
            Assert.That(() => OuterBlockExtractor.ValidateUpstream(root, OuterBlockExtractor.Finite, scratch), Throws.TypeOf<ExtractionException>().With.Message.EqualTo(diagnostic));
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }

    [TestCaseSource(nameof(DependencyCases))]
    public void Own_semantic_and_proof_dependencies_reject_actual_drift(string name, string kind, bool missing)
    {
        string root = Root();
        OuterBlockExtractor.Document document = AcceptedDocument(root, name);
        string relative = OuterBlockExtractor.Package + "/" + (kind switch
        {
            "template" => name + ".lean.template",
            "specification" => "Specification/" + name + ".lean",
            "refinement" => "Refinement/" + name + ".lean",
            _ => "OuterBlockExtractor.cs",
        });
        Assert.That(document.Dependencies.Count(identity => identity.Path == relative), Is.EqualTo(1));
        string scratch = Path.Combine(Path.GetTempPath(), "outer-dependency-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (OuterBlockExtractor.Identity identity in document.Dependencies)
            {
                string path = Path.Combine(scratch, identity.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(Path.Combine(root, identity.Path), path);
            }
            Assert.DoesNotThrow(() => OuterBlockExtractor.ValidateDependencies(scratch, document), "Unchanged dependency bytes must pass first.");
            Assert.DoesNotThrow(() => OuterBlockExtractor.Emit(scratch, document));
            string changed = Path.Combine(scratch, relative);
            if (missing) File.Delete(changed);
            else File.AppendAllText(changed, "\n-- dependency drift\n");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(() => OuterBlockExtractor.ValidateDependencies(scratch, document),
                    Throws.TypeOf<ExtractionException>().With.Message.EqualTo("Outer dependency changed or missing: '" + relative + "'."));
                Assert.That(() => OuterBlockExtractor.Emit(scratch, document),
                    Throws.TypeOf<ExtractionException>().With.Message.EqualTo("Outer dependency changed or missing: '" + relative + "'."));
            }
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }

    [TestCaseSource(nameof(AtomicCases))]
    public void Artifact_replacement_is_manifest_last_and_cleans_temporary_files(string name, bool blockManifest)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "outer-atomic-" + Guid.NewGuid().ToString("N"));
        try
        {
            OuterBlockExtractor.WriteArtifacts(scratch, name, [1], [2], [3]);
            string manifest = Path.Combine(scratch, name + ".source-manifest.json");
            if (blockManifest)
            {
                File.Delete(manifest);
                Directory.CreateDirectory(manifest);
                Exception? failure = Assert.Catch(() => OuterBlockExtractor.WriteArtifacts(scratch, name, [4, 5], [6, 7], [8, 9]));
                Assert.That(failure, Is.InstanceOf<IOException>().Or.InstanceOf<UnauthorizedAccessException>());
            }
            else OuterBlockExtractor.WriteArtifacts(scratch, name, [4, 5], [6, 7], [8, 9]);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(File.ReadAllBytes(Path.Combine(scratch, name + ".ir.json")), Is.EqualTo(new byte[] { 4, 5 }));
                Assert.That(File.ReadAllBytes(Path.Combine(scratch, name + ".lean")), Is.EqualTo(new byte[] { 6, 7 }));
                Assert.That(Directory.EnumerateFiles(scratch, "*.tmp"), Is.Empty);
                if (blockManifest) Assert.That(Directory.Exists(manifest), Is.True);
                else Assert.That(File.ReadAllBytes(manifest), Is.EqualTo(new byte[] { 8, 9 }));
            }
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }

    [TestCaseSource(nameof(SliceCases))]
    public void Frozen_compiler_inventory_has_the_exact_reviewed_identity(string name)
    {
        string root = Root();
        string package = Path.Combine(root, OuterBlockExtractor.Package);
        string[] actual = OuterBlockExtractor.ReadExportedTheorems(root, name,
            File.ReadAllBytes(Path.Combine(package, OuterBlockExtractor.InventoryFile(name))));
        JsonObject pin = JsonNode.Parse(File.ReadAllText(Path.Combine(package, OuterBlockExtractor.CensusPinFile(name))))!.AsObject();
        string[] upstream = File.ReadAllLines(Path.Combine(package,
            name == OuterBlockExtractor.Finite ? "EXPORTED_BRANCH_THEOREMS.txt" : "EXPORTED_FINITE_THEOREMS.txt"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Has.Length.EqualTo(pin["count"]!.GetValue<int>()));
            Assert.That(actual, Is.Ordered.Using<string>(StringComparer.Ordinal));
            Assert.That(actual.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(actual.Length));
            Assert.That(upstream.Except(actual, StringComparer.Ordinal), Is.Empty);
            Assert.That(actual.Any(theorem => theorem.StartsWith("BlockProcessorExtractor.Generated." + name + ".", StringComparison.Ordinal)), Is.True);
        }
    }

    [TestCaseSource(nameof(InventoryMutationCases))]
    public void Frozen_theorem_inventory_rejects_drift(string name, string mutation)
    {
        string root = Root();
        string path = Path.Combine(root, OuterBlockExtractor.Package, OuterBlockExtractor.InventoryFile(name));
        string[] names = OuterBlockExtractor.ReadExportedTheorems(root, name, File.ReadAllBytes(path));
        string[] changed = mutation switch
        {
            "missing" => names[1..],
            "duplicate" => [.. names, names[0]],
            "unexpected" => [.. names, "BlockProcessorExtractor.Refinement.BlockchainPublication.unexpected"],
            "descendant" => [.. names, "BlockProcessorExtractor.Generated." + name + ".Nested.Audit.indentedProof"],
            "empty" => [],
            "reordered" => names.Reverse().ToArray(),
            _ => [names[0].ToUpperInvariant(), .. names[1..]],
        };
        Assert.That(() => OuterBlockExtractor.ReadExportedTheorems(root, name, Encoding.UTF8.GetBytes(string.Join('\n', changed) + "\n")),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo("Outer exported-theorem inventory changed; explicit review is required."));
    }

    [TestCaseSource(nameof(SliceCases))]
    public void Templates_are_theorem_free_and_refinements_execute_upstream_proofs(string name)
    {
        string root = Root();
        string template = File.ReadAllText(Path.Combine(root, OuterBlockExtractor.Package, name + ".lean.template"));
        string proof = File.ReadAllText(Path.Combine(root, OuterBlockExtractor.Package, "Refinement", name + ".lean"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(template, Does.Not.Contain("theorem ").And.Not.Contain("axiom ").And.Not.Contain("sorry").And.Not.Contain("import BlockProcessorExtractor.Specification"));
            Assert.That(proof, Does.Contain("generated_normal_refines_spec"));
            Assert.That(proof, Does.Not.Contain("sorry").And.Not.Contain("axiom "));
        }
    }
}
