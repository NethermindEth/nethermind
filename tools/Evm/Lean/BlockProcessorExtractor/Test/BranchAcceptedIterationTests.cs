// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.BlockProcessorExtractor.Test;

[TestFixture]
public partial class BranchAcceptedIterationTests
{
    private static string Root()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, Extractor.BranchPath))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Cannot find the repository root.");
    }

    [Test]
    public void Source_only_audit_is_typed_deterministic_and_cannot_be_emitted_as_accepted()
    {
        string root = Root();
        BranchAcceptedIterationExtractor.Document first = BranchAcceptedIterationExtractor.AuditSourceForTest(root);
        BranchAcceptedIterationExtractor.Document second = BranchAcceptedIterationExtractor.AuditSourceForTest(root);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.AcceptanceState, Is.EqualTo("static-draft"));
            Assert.That(first.Sites.All(site => site.Target.Length > 0 && site.CfgBlock >= 0), Is.True);
            Assert.That(first.Sites.Single(site => site.Id == "checkpointReopen").TrueGuards,
                Does.Contain("true:isCommitPoint&&notReadOnly"));
            Assert.That(first.Sites.Single(site => site.Id == "countSuccess").Writes, Does.Contain("processedBlocksCount"));
            Assert.That(first.Program, Is.EqualTo(Enum.GetValues<BranchAcceptedIterationExtractor.Op>()));
            Assert.That(BranchAcceptedIterationExtractor.Serialize(first), Is.EqualTo(BranchAcceptedIterationExtractor.Serialize(second)));
            Assert.That(() => BranchAcceptedIterationExtractor.Emit(root, first), Throws.TypeOf<ExtractionException>());
            Assert.That(() => BranchAcceptedIterationExtractor.ReadDocument(BranchAcceptedIterationExtractor.Serialize(first)), Throws.TypeOf<ExtractionException>());
        }
    }

    [TestCaseSource(nameof(Mutations))]
    public void Compile_valid_branch_mutations_fail_source_admission(string before, string after, string anchor)
    {
        string root = Root();
        Assert.DoesNotThrow(() => BranchAcceptedIterationExtractor.AuditSourceForTest(root), "Unmutated source admission must pass first.");
        string source = File.ReadAllText(Path.Combine(root, Extractor.BranchPath)).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.That(source, Does.Contain(before), "Mutation anchor must exist in live source.");
        Dictionary<string, string> overrides = new(StringComparer.Ordinal)
        {
            [Extractor.BranchPath] = source.Replace(before, after, StringComparison.Ordinal),
        };
        Assert.DoesNotThrow(() => BranchAcceptedIterationExtractor.RequireCompilationForTest(root, overrides), "Mutation must compile before admission is tested.");
        Assert.That(() => BranchAcceptedIterationExtractor.AuditSourceForTest(root, overrides),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(BranchAcceptedIterationExtractor.AdmissionDiagnostic(anchor)));
    }

    private static IEnumerable<TestCaseData> Mutations()
    {
        yield return Case("bool checkInclusionList = !options.ContainsFlag(ProcessingOptions.NoValidation);", "bool checkInclusionList = options.ContainsFlag(ProcessingOptions.NoValidation);", "inclusionGuard", "invert_inclusion_guard");
        yield return Case("processedBlock.IsInclusionListSatisfied = inclusionListSatisfied;", "suggestedBlock.IsInclusionListSatisfied = inclusionListSatisfied;", "assignProcessedSignal", "omit_processed_signal_assignment");
        yield return Case("IsSatisfied(processedBlock, suggestedBlock, stateProvider)", "IsSatisfied(suggestedBlock, processedBlock, stateProvider)", "inclusion", "swap_inclusion_arguments");
        yield return Case("PreCommitBlock(suggestedBlock.Header);\n                processedBlocksCount = i + 1;", "processedBlocksCount = i + 1;\n                PreCommitBlock(suggestedBlock.Header);", "commitTree", "count_before_commit");
        yield return Case("PreCommitBlock(suggestedBlock.Header);", "PreCommitBlock(processedBlock.Header);", "commitTree", "wrong_commit_header");
        yield return Case("stateProvider.CommitTree(block.Number);", "stateProvider.CommitTree(block.Number + 1);", "helper.PreCommitBlock.commitTree", "wrong_commit_number");
        yield return Case("i % MaxUncommittedBlocks == 0 && isNotAtTheEdge", "i % MaxUncommittedBlocks != 0 && isNotAtTheEdge", "checkpointGuard", "invert_checkpoint");
        yield return Case("stateProvider.Reset();", "if (isCommitPoint) stateProvider.Reset();", "reset", "reset_only_at_checkpoint");
        yield return Case("BlockHeader previousBranchStateRoot = suggestedBlock.Header;", "BlockHeader previousBranchStateRoot = processedBlock.Header;", "checkpointHeader", "wrong_checkpoint_header");
        yield return Case("preBlockBaseBlock = processedBlock.Header;", "preBlockBaseBlock = suggestedBlock.Header;", "nextBase", "wrong_next_base");
        yield return Case("new BlockProcessedEventArgs(processedBlock, receipts)", "new BlockProcessedEventArgs(suggestedBlock, receipts)", "blockEvent", "wrong_event_block");
        yield return Case("task?.GetAwaiter().GetResult();\n            task = null;", "task = null;\n            task?.GetAwaiter().GetResult();", "helper.WaitAndClear", "clear_before_wait");
        yield return Case("WaitAndClear(ref preWarmTask);", "WaitForCacheClear();", "waitPrewarm", "wait_on_wrong_task");
        yield return Case("preWarmTask.ContinueWith(_clearCaches, TaskContinuationOptions.ExecuteSynchronously)", "preWarmTask.ContinueWith(_clearCaches)", "helper.QueueClearCaches", "change_clear_scheduling");
        yield return Case("return processedBlocks;", "return [suggestedBlock];", "return", "wrong_returned_array");
        yield return Case("blockProcessor.TransactionsExecuted -= CancelBackgroundWork;\n                worldStateCloser?.Dispose();", "worldStateCloser?.Dispose();\n                blockProcessor.TransactionsExecuted -= CancelBackgroundWork;", "finally.unsubscribeDispose", "dispose_before_unsubscribe");
        yield return Case("new BranchProcessingCompletedEventArgs(blocksProcessingEventArgs.Blocks, processedBlocksCount, processingException)", "new BranchProcessingCompletedEventArgs(blocksProcessingEventArgs.Blocks, 0, processingException)", "finally.completion", "erase_completion_count");
        yield return Case("if (suggestedBlock.Transactions.Length > 0)", "if (suggestedBlock.Transactions.Length == 0)", "scheduleHashes", "invert_hash_schedule_guard");
    }

    private static TestCaseData Case(string before, string after, string anchor, string name) => new TestCaseData(before, after, anchor).SetName("branch_" + name);

    [Test]
    public void Ir_rejects_draft_unknown_null_duplicates_and_wrong_operation_order()
    {
        string root = Root();
        BranchAcceptedIterationExtractor.Document draft = BranchAcceptedIterationExtractor.AuditSourceForTest(root);
        byte[] bytes = BranchAcceptedIterationExtractor.Serialize(draft);
        Assert.That(() => BranchAcceptedIterationExtractor.ReadDocument(bytes), Throws.TypeOf<ExtractionException>());
        JsonObject acceptedShape = JsonNode.Parse(bytes)!.AsObject();
        acceptedShape["acceptanceState"] = "source-admitted";
        Assert.DoesNotThrow(() => BranchAcceptedIterationExtractor.ReadDocument(Encoding.UTF8.GetBytes(acceptedShape.ToJsonString())),
            "The structural parser baseline must pass; this does not constitute source admission.");
        foreach (string field in new[] { "sources", "dependencies", "compilerClosure", "sites", "controlFlows", "program" })
        {
            JsonObject mutated = acceptedShape.DeepClone().AsObject();
            mutated[field] = null;
            Assert.That(() => BranchAcceptedIterationExtractor.ReadDocument(Encoding.UTF8.GetBytes(mutated.ToJsonString())), Throws.TypeOf<ExtractionException>());
        }
        JsonObject unknown = acceptedShape.DeepClone().AsObject();
        unknown["extra"] = true;
        Assert.That(() => BranchAcceptedIterationExtractor.ReadDocument(Encoding.UTF8.GetBytes(unknown.ToJsonString())), Throws.TypeOf<ExtractionException>());
        JsonObject operations = acceptedShape.DeepClone().AsObject();
        operations["program"]![0] = "countSuccess";
        Assert.That(() => BranchAcceptedIterationExtractor.ReadDocument(Encoding.UTF8.GetBytes(operations.ToJsonString())), Throws.TypeOf<ExtractionException>());
        foreach (string noncanonical in new[] { "Cancel", "CANCEL", "0" })
        {
            JsonObject mutated = acceptedShape.DeepClone().AsObject();
            mutated["program"]![0] = noncanonical;
            Assert.That(() => BranchAcceptedIterationExtractor.ReadDocument(Encoding.UTF8.GetBytes(mutated.ToJsonString())),
                Throws.TypeOf<ExtractionException>().With.Message.EqualTo("Branch operation names are not canonical."));
        }
        JsonObject sourceIdentity = acceptedShape.DeepClone().AsObject();
        sourceIdentity["sources"]![0]!["sha256"] = new string('0', 64);
        Assert.That(() => BranchAcceptedIterationExtractor.ReadDocument(Encoding.UTF8.GetBytes(sourceIdentity.ToJsonString())),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo("Branch semantic source identity changed."));
        foreach (bool remove in new[] { false, true })
        {
            JsonObject upstreamRoster = acceptedShape.DeepClone().AsObject();
            if (remove) upstreamRoster["dependencies"]!.AsArray().RemoveAt(0);
            else upstreamRoster["dependencies"]![0]!["path"] = "not/a/dependency.json";
            Assert.That(() => BranchAcceptedIterationExtractor.ReadDocument(Encoding.UTF8.GetBytes(upstreamRoster.ToJsonString())),
                Throws.TypeOf<ExtractionException>().With.Message.EqualTo("Branch complete dependency roster changed."));
        }
        byte[] duplicate = Encoding.UTF8.GetBytes(acceptedShape.ToJsonString().Insert(1, "\"schemaVersion\":1,"));
        Assert.That(() => BranchAcceptedIterationExtractor.ReadDocument(duplicate),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo("Duplicate/aliased branch JSON field."));
    }

    [Test]
    public void Ir_reader_and_emitter_reject_empty_site_targets([Values(null, "", " \t\r\n")] string? target)
    {
        string root = Root();
        BranchAcceptedIterationExtractor.Document structural = BranchAcceptedIterationExtractor.AuditSourceForTest(root)
            with { AcceptanceState = "source-admitted" };
        Assert.DoesNotThrow(() => BranchAcceptedIterationExtractor.ReadDocument(BranchAcceptedIterationExtractor.Serialize(structural)));
        Assert.DoesNotThrow(() => BranchAcceptedIterationExtractor.Emit(root, structural));
        BranchAcceptedIterationExtractor.Site[] sites = structural.Sites.ToArray();
        sites[0] = sites[0] with { Target = target! };
        BranchAcceptedIterationExtractor.Document mutated = structural with { Sites = sites };
        string diagnostic = "Branch IR site target is empty: " + sites[0].Id + ".";
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => BranchAcceptedIterationExtractor.ReadDocument(BranchAcceptedIterationExtractor.Serialize(mutated)),
                Throws.TypeOf<ExtractionException>().With.Message.EqualTo(diagnostic));
            Assert.That(() => BranchAcceptedIterationExtractor.Emit(root, mutated),
                Throws.TypeOf<ExtractionException>().With.Message.EqualTo(diagnostic));
        }
    }

    [Test]
    public void Publication_static_draft_is_not_a_branch_admission_witness([Values] bool extract)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "branch-draft-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(scratch, BranchAcceptedIterationExtractor.Publication + ".source-manifest.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{\"acceptanceState\":\"static-draft\"}");
            File.WriteAllText(Path.Combine(scratch, BranchAcceptedIterationExtractor.Publication + ".ir.json"), "{\"acceptanceState\":\"static-draft\"}");
            File.WriteAllText(Path.Combine(scratch, BranchAcceptedIterationExtractor.Publication + ".lean"), "");
            string output = Path.Combine(scratch, "uncreated-output");
            Assert.That(() =>
            {
                if (extract) BranchAcceptedIterationExtractor.Extract(scratch, output);
                else BranchAcceptedIterationExtractor.Check(scratch, output);
            }, Throws.TypeOf<ExtractionException>().With.Message.EqualTo(
                "Branch upstream validation failed: ProcessOne validated-publication artifacts are static drafts; serialized re-emission is required before validation can pass."));
            Assert.That(Directory.Exists(output), Is.False, "Upstream refusal must precede any branch output.");
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    [TestCaseSource(nameof(PublicationMutations))]
    public void Complete_upstream_validator_rejects_mutated_accepted_artifacts(string mutation) =>
        WithAcceptedPublicationFixture((root, directory) =>
        {
            string extension = mutation.StartsWith("ir_", StringComparison.Ordinal) ? ".ir.json" : ".source-manifest.json";
            string path = Path.Combine(directory, "ProcessOneValidatedPublication" + extension);
            JsonObject document = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
            string diagnostic;
            string? requiredField = null;
            switch (mutation)
            {
                case "ir_schema":
                case "manifest_schema":
                    document["schemaVersion"] = 999;
                    diagnostic = mutation == "ir_schema" ? "ProcessOne publication IR header is not admitted."
                        : "ProcessOne publication source manifest does not match its IR/artifacts.";
                    break;
                case "ir_version":
                case "manifest_version":
                    document["extractorVersion"] = "unsupported";
                    diagnostic = mutation == "ir_version" ? "ProcessOne publication IR header is not admitted."
                        : "ProcessOne publication source manifest does not match its IR/artifacts.";
                    break;
                case "ir_omitted_closure":
                case "manifest_omitted_closure":
                    document.Remove("sourceClosureSha256");
                    diagnostic = "Publication JSON schema failure: ";
                    requiredField = "sourceClosureSha256";
                    break;
                case "ir_stale_closure":
                    document["sourceClosureSha256"] = new string('0', 64);
                    diagnostic = "Publication source closure digest does not match its identities.";
                    break;
                case "manifest_stale_closure":
                    document["sourceClosureSha256"] = new string('0', 64);
                    diagnostic = "ProcessOne publication source manifest does not match its IR/artifacts.";
                    break;
                case "ir_missing_source":
                case "manifest_missing_source":
                    document["sources"]!.AsArray().RemoveAt(0);
                    diagnostic = mutation == "ir_missing_source" ? "ProcessOne publication IR source closure is not exact."
                        : "ProcessOne publication source manifest does not match its IR/artifacts.";
                    break;
                case "ir_missing_dependency":
                case "manifest_missing_dependency":
                    document["dependencies"]!.AsArray().RemoveAt(0);
                    diagnostic = mutation == "ir_missing_dependency" ? "ProcessOne publication order/effect/obligation schema drifted."
                        : "ProcessOne publication source manifest does not match its IR/artifacts.";
                    break;
                case "manifest_stale_dependency":
                    JsonObject dependency = document["dependencies"]![0]!.AsObject();
                    dependency["sha256"] = new string('0', 64);
                    diagnostic = "ProcessOne publication dependency changed: '" + dependency["path"]!.GetValue<string>() + "'.";
                    break;
                case "manifest_stale_ir_hash":
                    document["ir"]!["sha256"] = new string('0', 64);
                    diagnostic = "ProcessOne publication source manifest does not match its IR/artifacts.";
                    break;
                case "manifest_unaccepted_status":
                case "ir_unaccepted_status":
                    document["acceptanceState"] = "unaccepted";
                    diagnostic = mutation == "ir_unaccepted_status" ? "ProcessOne publication IR header is not admitted."
                        : "ProcessOne publication source manifest does not match its IR/artifacts.";
                    break;
                case "ir_duplicate":
                case "manifest_duplicate":
                    diagnostic = "Duplicate finalization JSON property: schemaVersion";
                    break;
                default:
                    throw new InvalidOperationException("Unregistered publication mutation: " + mutation);
            }
            string json = document.ToJsonString();
            if (mutation.EndsWith("_duplicate", StringComparison.Ordinal)) json = json.Insert(1, "\"schemaVersion\":1,");
            File.WriteAllText(path, json);
            ExtractionException? failure = Assert.Throws<ExtractionException>(() => BranchAcceptedIterationExtractor.ValidatePublication(root, directory));
            if (requiredField is null)
                Assert.That(failure!.Message, Is.EqualTo("Branch upstream validation failed: " + diagnostic));
            else
            {
                Assert.That(failure!.Message, Does.StartWith("Branch upstream validation failed: " + diagnostic));
                Assert.That(failure.Message, Does.Contain(requiredField));
            }
        });

    private static readonly string[] PublicationMutations =
    [
        "ir_schema", "manifest_schema", "ir_version", "manifest_version", "ir_omitted_closure", "manifest_omitted_closure",
        "ir_stale_closure", "manifest_stale_closure", "ir_missing_source", "manifest_missing_source",
        "ir_missing_dependency", "manifest_missing_dependency", "manifest_stale_dependency", "manifest_stale_ir_hash",
        "ir_unaccepted_status", "manifest_unaccepted_status", "ir_duplicate", "manifest_duplicate",
    ];

    [Test]
    public void Complete_upstream_validator_rejects_incomplete_or_stale_triplet(
        [Values(".ir.json", ".source-manifest.json", ".lean")] string extension, [Values] bool missing) =>
        WithAcceptedPublicationFixture((root, directory) =>
        {
            string path = Path.Combine(directory, "ProcessOneValidatedPublication" + extension);
            if (missing) File.Delete(path);
            else if (extension == ".lean") File.AppendAllText(path, "\n-- stale artifact\n");
            else
            {
                JsonObject document = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
                document["acceptanceState"] = "static-draft";
                File.WriteAllText(path, document.ToJsonString());
            }
            string diagnostic = missing
                ? "Checked-in ProcessOne validated-publication artifacts are incomplete; run serialized regeneration after upstream settlement."
                : extension == ".lean" ? "ProcessOne publication source manifest does not match its IR/artifacts."
                : "ProcessOne validated-publication artifacts are static drafts; serialized re-emission is required before validation can pass.";
            Assert.That(() => BranchAcceptedIterationExtractor.ValidatePublication(root, directory),
                Throws.TypeOf<ExtractionException>().With.Message.EqualTo("Branch upstream validation failed: " + diagnostic));
        });

    private static void WithAcceptedPublicationFixture(Action<string, string> test)
    {
        string root = Root();
        string scratch = Path.Combine(Path.GetTempPath(), "branch-publication-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(scratch);
            foreach (string extension in new[] { ".ir.json", ".source-manifest.json", ".lean" })
                File.Copy(Path.Combine(root, BranchAcceptedIterationExtractor.Publication + extension),
                    Path.Combine(scratch, "ProcessOneValidatedPublication" + extension));
            Assert.DoesNotThrow(() => BranchAcceptedIterationExtractor.ValidatePublication(root, scratch),
                "The complete unmutated upstream gate must pass first. Static drafts require serialized regeneration; this test must not skip or forge acceptance.");
            test(root, scratch);
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    [Test]
    public void Compiler_closure_rejects_inventory_identity_selection_and_support_drift(
        [Values("schema", "count", "selectedCount", "inventoryHash", "aggregate", "missing", "duplicatePath",
            "duplicateAssembly", "sha", "mvid", "selection", "support")] string mutation)
    {
        BranchAcceptedIterationExtractor.CompilerClosure baseline = BranchAcceptedIterationExtractor.AuditSourceForTest(Root()).CompilerClosure;
        Assert.DoesNotThrow(() => BranchAcceptedIterationExtractor.ValidateCompilerClosure(baseline));
        BranchAcceptedIterationExtractor.Reference[] references = baseline.References.ToArray();
        BranchAcceptedIterationExtractor.Reference first = references[0];
        BranchAcceptedIterationExtractor.CompilerClosure changed = mutation switch
        {
            "schema" => baseline with { SchemaVersion = 1 },
            "count" => baseline with { Count = 329 },
            "selectedCount" => baseline with { SelectedCount = 434 },
            "inventoryHash" => baseline with { Inventory = baseline.Inventory with { Sha256 = new string('0', 64) } },
            "aggregate" => baseline with { AggregateSha256 = new string('0', 64) },
            "missing" => baseline with { References = references[1..] },
            "support" => baseline with { SupportSources = baseline.SupportSources[1..] },
            _ => baseline with { References = references },
        };
        references[0] = mutation switch
        {
            "duplicatePath" => first with { Path = references[1].Path },
            "duplicateAssembly" => first with { AssemblyName = references[1].AssemblyName },
            "sha" => first with { Sha256 = new string('0', 64) },
            "mvid" => first with { Mvid = Guid.Empty.ToString("D") },
            "selection" => first with { Selected = !first.Selected },
            _ => first,
        };
        Assert.That(() => BranchAcceptedIterationExtractor.ValidateCompilerClosure(changed),
            Throws.TypeOf<ExtractionException>().With.Message.StartsWith("Branch compiler"));
    }

    [Test]
    public void Own_proof_dependencies_cannot_be_omitted(
        [Values("Specification/BranchAcceptedIteration.lean", "Refinement/BranchAcceptedIteration.lean",
            "Vectors/BranchAcceptedIterationVectors.lean", "Schema/branch-accepted-iteration.ir.schema.json",
            "Schema/branch-accepted-iteration.source-manifest.schema.json", "Verify-Branch.ps1",
            "Verify-BranchMutationGates.ps1", "Verify-BranchAxioms.ps1", "EXPORTED_BRANCH_THEOREMS.txt")] string omitted)
    {
        BranchAcceptedIterationExtractor.Document baseline = BranchAcceptedIterationExtractor.AuditSourceForTest(Root())
            with { AcceptanceState = "source-admitted" };
        Assert.DoesNotThrow(() => BranchAcceptedIterationExtractor.ReadDocument(BranchAcceptedIterationExtractor.Serialize(baseline)));
        BranchAcceptedIterationExtractor.Document changed = baseline with
        {
            Dependencies = baseline.Dependencies.Where(identity => identity.Path != BranchAcceptedIterationExtractor.Package + "/" + omitted).ToArray(),
        };
        Assert.That(() => BranchAcceptedIterationExtractor.ReadDocument(BranchAcceptedIterationExtractor.Serialize(changed)),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo("Branch own semantic/proof dependency roster changed."));
    }

    [Test]
    public void Atomic_artifact_failure_preserves_prior_file_and_cleans_temporary(
        [Values(".ir.json", ".lean", ".source-manifest.json")] string extension, [Values] bool existing)
    {
        string directory = Path.Combine(Path.GetTempPath(), "branch-atomic-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "BranchAcceptedIteration" + extension);
        byte[] original = [1, 3, 7];
        try
        {
            if (existing) File.WriteAllBytes(path, original);
            Assert.That(() => BranchAcceptedIterationExtractor.AtomicWrite(path, [2, 4, 8], () => throw new IOException("injected before replace")),
                Throws.TypeOf<IOException>().With.Message.EqualTo("injected before replace"));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(File.Exists(path), Is.EqualTo(existing));
                if (existing) Assert.That(File.ReadAllBytes(path), Is.EqualTo(original));
                Assert.That(Directory.GetFiles(directory, "*.tmp"), Is.Empty);
            }
            BranchAcceptedIterationExtractor.AtomicWrite(path, [2, 4, 8]);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(new byte[] { 2, 4, 8 }));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public void Lean_vectors_invoke_the_source_attached_theorem_and_preserve_unknown_escapes()
    {
        string root = Root();
        string vectors = File.ReadAllText(Path.Combine(root, BranchAcceptedIterationExtractor.Package, "Vectors/BranchAcceptedIterationVectors.lean"));
        string refinement = File.ReadAllText(Path.Combine(root, BranchAcceptedIterationExtractor.Package, "Refinement/BranchAcceptedIteration.lean"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(vectors, Does.Contain("R.generated_normal_refines_spec source (vector scenario) (adapter scenario)"));
            Assert.That(refinement, Does.Contain("U.generated_normal_refines_spec source.publicationSource"));
            Assert.That(refinement, Does.Contain("S.normalIteration (mapInput i) (mapResult (G.run i))"));
            Assert.That(vectors, Does.Contain("(failsAt .completionEvent)).returnedSlots = none"));
            Assert.That(vectors, Does.Contain(".checkpoint64"));
        }
    }
}
