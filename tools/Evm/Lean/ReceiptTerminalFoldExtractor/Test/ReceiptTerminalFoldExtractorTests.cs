// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.ReceiptTerminalFoldExtractor.Test;

[TestFixture]
[NonParallelizable]
public sealed class ReceiptTerminalFoldExtractorTests
{
    [Test]
    public void Production_sources_extract_deterministically()
    {
        string root = FindRepoRoot();
        using TemporaryDirectory first = new("receipt-terminal-fold-output");
        using TemporaryDirectory second = new("receipt-terminal-fold-output");

        ExtractionResult firstResult = Extract(root, first.Path);
        ExtractionResult secondResult = Extract(root, second.Path);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstResult.SourceCount, Is.EqualTo(12));
            Assert.That(firstResult.MemberCount, Is.EqualTo(56));
            Assert.That(File.ReadAllBytes(firstResult.IrPath), Is.EqualTo(File.ReadAllBytes(secondResult.IrPath)));
            Assert.That(File.ReadAllBytes(firstResult.ManifestPath), Is.EqualTo(File.ReadAllBytes(secondResult.ManifestPath)));
            Assert.That(File.ReadAllBytes(firstResult.LeanPath), Is.EqualTo(File.ReadAllBytes(secondResult.LeanPath)));
        }
    }

    [Test]
    public void Manifest_records_complete_compiler_reference_closure()
    {
        JsonObject manifest = ParseCheckedManifest();
        JsonArray references = manifest["compilerReferences"]!.AsArray();
        int referenceCount = manifest["compilerReferenceCount"]!.GetValue<int>();
        string referenceAggregate = manifest["compilerReferenceAggregateSha256"]!.GetValue<string>();
        JsonObject inventory = JsonNode.Parse(File.ReadAllBytes(Path.Combine(
            FindRepoRoot(), ReceiptTerminalFoldProfile.CompilerReferenceInventoryRelativePath)))!.AsObject();
        string[] names = references
            .Where(node => node!.AsObject()["selected"]!.GetValue<bool>())
            .Select(node => node!.AsObject()["assemblyName"]!.GetValue<string>())
            .ToArray();
        string[] expectedReferences = inventory["references"]!.AsArray()
            .Select(node => node!.ToJsonString())
            .ToArray();
        string[] actualReferences = references.Select(static node => node!.ToJsonString()).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(references, Has.Count.GreaterThan(0));
            Assert.That(referenceCount, Is.EqualTo(434));
            Assert.That(referenceCount, Is.EqualTo(references.Count));
            Assert.That(referenceAggregate, Is.EqualTo("6fdfa102a4190083ae62080355aa6691b5f46acc32d4f9da2212012686d9c2e2"));
            Assert.That(actualReferences, Is.EqualTo(expectedReferences));
            Assert.That(names, Does.Contain("Nethermind.Int256"));
            Assert.That(names, Does.Contain("Collections.Pooled"));
            Assert.That(names, Does.Contain("Microsoft.Extensions.ObjectPool"));
            Assert.That(ReceiptTerminalFoldProfile.RequiredCompilerAssemblyNames.All(
                required => names.Contains(required, StringComparer.OrdinalIgnoreCase)), Is.True);
            Assert.That(names.Distinct(StringComparer.OrdinalIgnoreCase).Count(), Is.EqualTo(names.Length));
            Assert.That(names, Has.Length.EqualTo(329));
            Assert.That(references.All(node =>
                node!.AsObject()["path"]!.GetValue<string>().EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                node.AsObject()["sha256"]!.GetValue<string>().Length == 64 &&
                Guid.TryParseExact(node.AsObject()["mvid"]!.GetValue<string>(), "D", out _)), Is.True);
        }
    }

    [TestCase("duplicate-path")]
    [TestCase("duplicate-selection")]
    [TestCase("missing-selection")]
    [TestCase("invalid-mvid")]
    [TestCase("empty-mvid")]
    [TestCase("malformed-hash")]
    [TestCase("wrong-required-path")]
    public void Compiler_reference_identity_mutations_are_rejected(string mutation)
    {
        JsonArray inventory = ParseCheckedManifest()["compilerReferences"]!.AsArray();
        CompilerReferenceIdentity[] references = inventory.Deserialize<CompilerReferenceIdentity[]>(
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        ReceiptTerminalFoldProfile.ValidateCompilerReferenceIdentities(references);
        int selected = Array.FindIndex(references, static reference => reference.AssemblyName == "Nethermind.Core" && reference.Selected);
        int duplicate = Array.FindIndex(references, static reference => reference.AssemblyName == "Nethermind.Core" && !reference.Selected);
        Assert.That(selected, Is.GreaterThanOrEqualTo(0));
        Assert.That(duplicate, Is.GreaterThanOrEqualTo(0));
        switch (mutation)
        {
            case "duplicate-path": references[duplicate] = references[selected]; break;
            case "duplicate-selection": references[duplicate] = references[duplicate] with { Selected = true }; break;
            case "missing-selection": references[selected] = references[selected] with { Selected = false }; break;
            case "invalid-mvid": references[selected] = references[selected] with { Mvid = "not-a-guid" }; break;
            case "empty-mvid": references[selected] = references[selected] with { Mvid = Guid.Empty.ToString("D") }; break;
            case "malformed-hash": references[selected] = references[selected] with { Sha256 = "not-a-hash" }; break;
            case "wrong-required-path":
                references[selected] = references[selected] with { Selected = false };
                references[duplicate] = references[duplicate] with { Selected = true };
                break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        Assert.That(() => ReceiptTerminalFoldProfile.ValidateCompilerReferenceIdentities(references),
            Throws.TypeOf<ExtractionException>());
    }

    [TestCase("addition")]
    [TestCase("removal")]
    [TestCase("platform-addition")]
    [TestCase("unselected-copy-addition")]
    public void Compiler_reference_path_set_changes_are_rejected(string mutation)
    {
        using TemporaryDirectory temporary = new("receipt-terminal-fold-reference-paths");
        string directory = Path.Combine(temporary.Path, "closure");
        Directory.CreateDirectory(directory);
        string referencePath = Path.Combine(directory, "Compiler.dll");
        File.WriteAllBytes(referencePath, []);
        CompilerReferenceInventory inventory = new(2, 1, new string('0', 64),
            [new("closure/Compiler.dll", "Compiler", new string('0', 64), Guid.NewGuid().ToString("D"), true)]);
        ReceiptTerminalFoldProfile.ValidateReferenceInventoryPaths(inventory, temporary.Path, [], [directory]);
        string[] platform = [];
        List<string> directories = [directory];
        switch (mutation)
        {
            case "addition": File.WriteAllBytes(Path.Combine(directory, "Extra.dll"), []); break;
            case "removal": File.Delete(referencePath); break;
            case "platform-addition": platform = [Path.Combine(temporary.Path, "Runtime.dll")]; break;
            case "unselected-copy-addition":
                string copyDirectory = Path.Combine(temporary.Path, "copies");
                Directory.CreateDirectory(copyDirectory);
                File.WriteAllBytes(Path.Combine(copyDirectory, "Compiler.dll"), []);
                directories.Add(copyDirectory);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        Assert.That(() => ReceiptTerminalFoldProfile.ValidateReferenceInventoryPaths(
            inventory, temporary.Path, platform, directories.ToArray()), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Delegated_Lean_closure_matches_transitive_imports()
    {
        SourceIdentity[] dependencies = ReceiptTerminalFoldProfile.LeanDependencies.ToArray();
        HashSet<string> expected = dependencies.Select(static dependency => dependency.Path).ToHashSet(StringComparer.Ordinal);
        HashSet<string> found = new(StringComparer.Ordinal);
        Queue<string> remaining = new(new[]
        {
            "tools/Evm/Lean/Eip803x/Generated/BlockReceiptGasAccountingKernel.lean",
            "tools/Evm/Lean/Eip803x/Refinement/BlockReceiptGasAccounting.lean",
        });
        while (remaining.TryDequeue(out string? path))
        {
            if (!found.Add(path)) continue;
            foreach (string line in File.ReadAllLines(Path.Combine(FindRepoRoot(), path)))
            {
                if (line.StartsWith("import Eip803x.", StringComparison.Ordinal))
                    remaining.Enqueue("tools/Evm/Lean/" + line[7..].Trim().Replace('.', '/') + ".lean");
            }
        }
        Assert.That(found.SetEquals(expected), Is.True);
    }

    private static IEnumerable<string> LeanDependencyPaths() =>
        ReceiptTerminalFoldProfile.LeanDependencies.ToArray().Select(static dependency => dependency.Path);

    [Test]
    public void Missing_or_changed_Lean_dependency_is_rejected(
        [ValueSource(nameof(LeanDependencyPaths))] string dependency,
        [Values] bool remove)
    {
        using Fixture fixture = new();
        string path = Path.Combine(fixture.Root, dependency);
        if (remove) File.Delete(path);
        else File.AppendAllText(path, "\n-- changed dependency\n");
        Assert.That(() => ReceiptTerminalFoldProfile.ValidateLeanDependencies(fixture.Root), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Checked_artifacts_match_fresh_extraction()
    {
        string root = FindRepoRoot();
        using TemporaryDirectory temporary = new("receipt-terminal-fold-output");
        ExtractionResult result = Extract(root, temporary.Path);
        string checkedDirectory = Path.Combine(root, ReceiptTerminalFoldProfile.DefaultOutputRelativePath);
        string checkedLean = Path.Combine(root, ReceiptTerminalFoldProfile.DefaultLeanRelativePath);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(result.IrPath), Is.EqualTo(File.ReadAllBytes(
                Path.Combine(checkedDirectory, ReceiptTerminalFoldProfile.IrFileName))));
            Assert.That(File.ReadAllBytes(result.ManifestPath), Is.EqualTo(File.ReadAllBytes(
                Path.Combine(checkedDirectory, ReceiptTerminalFoldProfile.ManifestFileName))));
            Assert.That(File.ReadAllBytes(result.LeanPath), Is.EqualTo(File.ReadAllBytes(checkedLean)));
        }
    }

    [Test]
    public void Generated_Lean_is_theorem_free_and_accounting_kernel_is_imported()
    {
        string root = FindRepoRoot();
        string source = File.ReadAllText(Path.Combine(root, ReceiptTerminalFoldProfile.DefaultLeanRelativePath));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source, Does.Not.Match(@"(?m)^\s*theorem\s"));
            Assert.That(source, Does.Not.Match(@"(?m)^\s*axiom\s"));
            Assert.That(source, Does.Not.Match(@"\b(sorry|admit|example)\b"));
            Assert.That(source, Does.Contain("Eip803x.Generated.BlockReceiptGasAccountingKernel"));
            Assert.That(source, Does.Contain("def markAsSuccess"));
            Assert.That(source, Does.Contain("def markAsFailed"));
            Assert.That(source, Does.Contain("def restorePrefix"));
            Assert.That(source, Does.Contain("structure FinalizationObservation"));
            Assert.That(source, Does.Contain("def transactionResult"));
            Assert.That(source, Does.Contain("evmExceptionType : Option EvmExceptionOracle"));
            Assert.That(source, Does.Contain("vmError : ErrorOracle"));
            Assert.That(source, Does.Contain("blockGasUsed : Nat"));
            Assert.That(source, Does.Contain("executionGasUsed : Nat"));
            Assert.That(source, Does.Contain("storageGasUsed : Nat"));
            Assert.That(source, Does.Contain("def isContractCreation (transaction : TransactionInput) : Bool"));
            Assert.That(source, Does.Not.Contain("isContractCreation : Bool"));
            Assert.That(source, Does.Not.Contain("blockGasUsed : Option Nat"));
            Assert.That(source, Does.Not.Contain("revertOutput"));
            Assert.That(source, Does.Not.Contain("ReturnValue"));
        }
    }

    [Test]
    public void Ir_declares_complete_operation_and_mapping_surface()
    {
        IrDocument document = ReadCheckedIr();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(document.Operations.Select(static operation => operation.Name), Is.EqualTo(new[]
            {
                "markAsSuccess", "markAsFailed", "buildFailedReceipt", "buildReceipt",
                "updateCumulativeGasTracking", "takeSnapshot", "restore", "finalizeTransaction",
            }));
            Assert.That(document.Mappings.Select(static mapping => mapping.Source).ToArray(), Has.Length.EqualTo(17));
            Assert.That(document.Inputs.GasFields, Has.Length.EqualTo(8));
            Assert.That(document.Inputs.ReceiptFields, Has.Length.EqualTo(18));
            Assert.That(document.Inputs.TransactionFields, Has.Length.EqualTo(5));
            Assert.That(document.Inputs.BlockFields, Has.Length.EqualTo(3));
            Assert.That(document.Inputs.HeaderFields, Has.Length.EqualTo(4));
            Assert.That(document.Inputs.FinalizeFields, Has.Length.EqualTo(9));
            Assert.That(document.Operations.SelectMany(static operation => operation.Body.Invocations)
                .All(invocation => !string.IsNullOrWhiteSpace(invocation.SymbolId) &&
                    !string.IsNullOrWhiteSpace(invocation.ReturnType) &&
                    !string.IsNullOrWhiteSpace(invocation.ReceiverSymbolId) &&
                    !string.IsNullOrWhiteSpace(invocation.ReceiverTypeName)), Is.True);
            Assert.That(document.Operations.SelectMany(static operation => operation.Body.Assignments)
                .All(assignment => assignment.TargetExpression is not null), Is.True);
            Assert.That(document.Operations.Single(static operation => operation.Name == "finalizeTransaction").Outputs,
                Is.EqualTo(new[] { "terminalTracerCall", "TransactionResult", "FinalizationObservation" }));
            Assert.That(document.Operations.Single(static operation => operation.Name == "updateCumulativeGasTracking").Inputs,
                Is.EqualTo(new[] { "previousBlockTotals", "cumulativeReceiptGas", "gasConsumed" }));
            Assert.That(document.Operations.Single(static operation => operation.Name == "takeSnapshot").Inputs,
                Is.EqualTo(new[] { "receipts" }));
            Assert.That(document.Operations.Single(static operation => operation.Name == "restore").Inputs,
                Is.EqualTo(new[] { "snapshotPosition", "receipts", "gasHistory" }));
            Assert.That(document.Operations.Single(static operation => operation.Name == "finalizeTransaction").Inputs,
                Is.EqualTo(new[] { "statusCode", "substate", "executingAccount", "spentGas", "tracer" }));
            Assert.That(document.Operations.Single(static operation => operation.Name == "finalizeTransaction").OrderedEffects,
                Does.Contain("eip8037AccountReapBeforeTerminal"));
            Assert.That(document.Mappings.Select(static mapping => mapping.Source), Does.Contain(
                "FinalizeTransaction.substate.EvmExceptionType"));
            Assert.That(document.Mappings.Select(static mapping => mapping.Source), Does.Contain(
                "FinalizeTransaction.substate.SubstateError"));
            Assert.That(document.AccountingKernel.GeneratedLeanPath,
                Is.EqualTo("tools/Evm/Lean/Eip803x/Generated/BlockReceiptGasAccountingKernel.lean"));
        }
    }

    [TestCase("update-argument")]
    [TestCase("restore-argument")]
    [TestCase("restore-assignment")]
    [TestCase("receipt-field")]
    [TestCase("terminal-payload")]
    [TestCase("status-constant")]
    [TestCase("failure-error")]
    [TestCase("result-error")]
    public void Type_preserving_semantic_ir_mutations_change_and_typecheck_Lean(string mutation)
    {
        IrDocument document = ReadCheckedIr();
        byte[] baseline = ReceiptTerminalFoldLeanEmitter.Emit(
            document,
            ReceiptTerminalFoldProfile.ExtractorVersion,
            "test-compiler",
            ReceiptTerminalFoldProfile.TracerPath,
            new string('0', 64),
            ReceiptTerminalFoldProfile.Sha256(Encoding.UTF8.GetBytes("baseline")));

        IrDocument alteredDocument = MutateSemanticIr(document, mutation);
        byte[] altered = EmitSemanticMutation(alteredDocument);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(altered, Is.Not.EqualTo(baseline));
        }
        AssertLeanCompiles(altered);
    }

    [TestCase("update-argument")]
    [TestCase("restore-argument")]
    [TestCase("restore-assignment")]
    [TestCase("receipt-field")]
    [TestCase("terminal-payload")]
    [TestCase("status-constant")]
    [TestCase("failure-error")]
    [TestCase("result-error")]
    public void Type_preserving_semantic_ir_mutations_fail_independent_refinement_graph(string mutation)
    {
        byte[] altered = EmitSemanticMutation(MutateSemanticIr(ReadCheckedIr(), mutation));
        AssertIndependentGraphRejects(altered);
    }

    [Test]
    public void Detached_forwarding_order_is_rejected_by_source_branch_paths()
    {
        ExtractionException exception = Assert.Throws<ExtractionException>(() =>
            EmitSemanticMutation(SwapTerminalForwardingOrder(ReadCheckedIr())))!;
        Assert.That(exception.Message, Does.Contain("RECEIPT_BRANCH_PATH"));
    }

    private static IEnumerable<TestCaseData> BranchCases()
    {
        yield return new("markAsSuccess", "_currentTxTracer.IsTracingReceipt", "MarkAsSuccess", ReceiptTerminalFoldProfile.TracerPath);
        yield return new("markAsFailed", "_currentTxTracer.IsTracingReceipt", "MarkAsFailed", ReceiptTerminalFoldProfile.TracerPath);
        yield return new("buildReceipt", "gasConsumed.BlockGas > 0", "BlockGasUsed", ReceiptTerminalFoldProfile.TracerPath);
        yield return new("buildReceipt", "gasConsumed.BlockStateGas > 0", "StorageGasUsed", ReceiptTerminalFoldProfile.TracerPath);
        yield return new("updateCumulativeGasTracking", "!parallel", "Header.GasUsed", ReceiptTerminalFoldProfile.TracerPath);
        yield return new("restore", "numToRemove > 0", "RemoveRange", ReceiptTerminalFoldProfile.TracerPath);
        yield return new("finalizeTransaction", "error is null && substate.EvmExceptionType is not EvmExceptionType.None", "FastToString", ReceiptTerminalFoldProfile.CallerPath);
        yield return new("finalizeTransaction", "!spec.IsEip658Enabled", "stateRoot =", ReceiptTerminalFoldProfile.CallerPath);
    }

    private static IEnumerable<TestCaseData> EarlyExitCases()
    {
        yield return new("markAsSuccess", "_txReceipts.Add", "if (gasSpent.SpentGas == 0) { return; }");
        yield return new("markAsSuccess", "otherTxTracer.MarkAsSuccess", "if (gasSpent.SpentGas == 0) { return; }");
        yield return new("markAsSuccess", "_currentTxTracer.MarkAsSuccess", "if (gasSpent.SpentGas == 0) { return; }");
        yield return new("markAsFailed", "_txReceipts.Add", "if (gasSpent.SpentGas == 0) { return; }");
        yield return new("markAsFailed", "_currentTxTracer.MarkAsFailed", "if (gasSpent.SpentGas == 0) { return; }");
        yield return new("buildFailedReceipt", "TxReceipt receipt", "if (gasSpent.SpentGas == 0) { return null!; }");
        yield return new("buildReceipt", "ulong cumulativeReceiptGas", "if (gasConsumed.SpentGas == 0) { return null!; }");
        yield return new("buildReceipt", "txReceipt.BlockGasUsed", "if (gasConsumed.SpentGas == 0) { return txReceipt; }");
        yield return new("updateCumulativeGasTracking", "_cumulativeBlockGasPerTx.Add", "if (gasConsumed.SpentGas == 0) { return 0; }");
        yield return new("takeSnapshot", "", "if (_txReceipts.Count == 0) { return 1; }");
        yield return new("restore", "_txReceipts.RemoveRange", "if (snapshot == 0) { return; }");
        yield return new("restore", "Block.Header.GasUsed", "if (snapshot == 0) { return; }");
        yield return new("finalizeTransaction", "WorldState.RecalculateStateRoot", "if (statusCode == 0) { return TransactionResult.Ok; }");
        yield return new("finalizeTransaction", "error = substate.EvmExceptionType", "if (statusCode == 0) { return TransactionResult.Ok; }");
        yield return new("finalizeTransaction", "tracer.MarkAsFailed", "if (statusCode == 0) { return TransactionResult.Ok; }");
        yield return new("finalizeTransaction", "tracer.MarkAsSuccess", "if (statusCode == 0) { return TransactionResult.Ok; }");
    }

    [TestCase("return receipt;", "return null!;")]
    [TestCase("return txReceipt;", "return null!;")]
    public void Compile_valid_builder_return_substitution_reaches_return_binding(string original, string replacement)
    {
        using Fixture fixture = new();
        fixture.Replace(ReceiptTerminalFoldProfile.TracerPath, original, replacement);
        ExtractionException exception = Assert.Throws<ExtractionException>(() => fixture.ExtractSemanticMutation())!;
        Assert.That(exception.Message, Does.StartWith("RECEIPT_RETURN_BINDING:"));
    }

    [Test]
    public void Compile_valid_conditional_receipt_construction_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(ReceiptTerminalFoldProfile.TracerPath, "TxReceipt txReceipt = new()", "TxReceipt txReceipt = true ? null! : new()");
        ExtractionException exception = Assert.Throws<ExtractionException>(() => fixture.ExtractSemanticMutation())!;
        Assert.That(exception.Message, Does.StartWith("RECEIPT_RETURN_BINDING: BuildReceipt:"));
    }

    [Test]
    public void Coordinated_conditional_receipt_construction_is_rejected()
    {
        IrDocument document = ReadCheckedIr();
        OperationDescriptor operation = document.Operations.Single(candidate => candidate.Name == "buildReceipt");
        SemanticAssignment construction = operation.Body.Assignments.Single(assignment => assignment.Target == "txReceipt");
        SemanticExpression replacement = new("ConditionalExpression", "true ? null : new()", [
            new("TrueLiteralExpression", "true", [], null, "bool", construction.Position),
            new("NullLiteralExpression", "null", [], null, construction.Value.TypeName, construction.Position),
            construction.Value,
        ], null, construction.Value.TypeName, construction.Position);
        OperationDescriptor altered = operation with
        {
            Steps = operation.Steps.Select(step => step.DeclaredTarget?.Text == "txReceipt" ? step with { Expression = replacement } : step).ToArray(),
            Body = operation.Body with
            {
                Assignments = operation.Body.Assignments.Select(assignment => assignment == construction ? assignment with { Value = replacement } : assignment).ToArray(),
            },
        };
        ExtractionException exception = Assert.Throws<ExtractionException>(() =>
            ReceiptTerminalFoldProfile.ValidateSerializedIr(SerializeIr(ReplaceOperation(document, operation.Name, altered))))!;
        Assert.That(exception.Message, Does.StartWith("RECEIPT_RETURN_BINDING: BuildReceipt:"));
    }

    [TestCase("UpdateCumulativeGasTracking")]
    [TestCase("Restore")]
    public void Compile_valid_unconsumed_call_reaches_effect_ledger(string member)
    {
        using Fixture fixture = new();
        fixture.ReplaceStatement(ReceiptTerminalFoldProfile.TracerPath, member, "Debug.Assert", "_txReceipts.Clear();");
        ExtractionException exception = Assert.Throws<ExtractionException>(() => fixture.ExtractSemanticMutation())!;
        Assert.That(exception.Message, Does.StartWith($"RECEIPT_EFFECT_LEDGER: {member}:"));
    }

    private static IEnumerable<TestCaseData> AdditionalEffectCases()
    {
        yield return new(ReceiptTerminalFoldProfile.TracerPath, "MarkAsSuccess", "_currentTxTracer.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot)", "_currentTxTracer.MarkAsSuccess(recipient, gasSpent, Array.Empty<byte>(), logs, stateRoot)");
        yield return new(ReceiptTerminalFoldProfile.TracerPath, "MarkAsFailed", "_currentTxTracer.MarkAsFailed(recipient, gasSpent, output, error, stateRoot)", "_currentTxTracer.MarkAsFailed(recipient, gasSpent, Array.Empty<byte>(), error, stateRoot)");
        yield return new(ReceiptTerminalFoldProfile.TracerPath, "BuildFailedReceipt", "receipt.Error = error;", "receipt.Error = string.Concat(error);");
        yield return new(ReceiptTerminalFoldProfile.TracerPath, "BuildReceipt", "Transaction transaction = CurrentTx!;", "Transaction transaction = CurrentTx ?? new Transaction();");
        yield return new(ReceiptTerminalFoldProfile.TracerPath, "TakeSnapshot", "int TakeSnapshot() => _txReceipts.Count;", "int TakeSnapshot() => _txReceipts.EnsureCapacity(0);");
        yield return new(ReceiptTerminalFoldProfile.CallerPath, "FinalizeTransaction", "tracer.MarkAsFailed(executingAccount, spentGas, output, error, stateRoot)", "tracer.MarkAsFailed(executingAccount, spentGas, Array.Empty<byte>(), error, stateRoot)");
        yield return new(ReceiptTerminalFoldProfile.EthereumGasPolicyPath, "CombineBlockGas", "BlockGasAccountingKernel.Combine(blockExecutionGas, blockStateGas)", "BlockGasAccountingKernel.Combine(Math.Max(blockExecutionGas, blockStateGas), blockStateGas)");
        yield return new(ReceiptTerminalFoldProfile.TransactionGasInitializationKernelPath, "Combine", "Math.Max(blockExecutionGas, blockStateGas)", "Math.Max(Math.Min(blockExecutionGas, blockStateGas), blockStateGas)");
        yield return new(ReceiptTerminalFoldProfile.TransactionExtensionsPath, "CalculateEffectiveGasPrice", "!eip1559Enabled ? tx.MaxPriorityFeePerGas", "!(eip1559Enabled = false) ? tx.MaxPriorityFeePerGas");
        yield return new(ReceiptTerminalFoldProfile.AccountingKernelPath, "Accumulate", "unchecked(previousExecutionGas + transactionExecutionGas)", "unchecked(previousExecutionGas + System.Math.Max(transactionExecutionGas, 1UL))");
        yield return new(ReceiptTerminalFoldProfile.AccountingKernelPath, "FromTotals", "EthereumGasPolicy.CombineBlockGas(cumulativeExecutionGas, cumulativeStateGas)", "EthereumGasPolicy.CombineBlockGas(System.Math.Max(cumulativeExecutionGas, cumulativeStateGas), cumulativeStateGas)");
    }

    [TestCaseSource(nameof(AdditionalEffectCases))]
    public void Compile_valid_additional_effects_cover_every_remaining_method(string path, string member, string original, string replacement)
    {
        using Fixture fixture = new();
        fixture.Replace(path, original, replacement);
        ExtractionException exception = Assert.Throws<ExtractionException>(() => fixture.ExtractSemanticMutation())!;
        Assert.That(exception.Message, Does.StartWith($"RECEIPT_EFFECT_LEDGER: {member}:"));
    }

    [TestCase("buildReceipt")]
    [TestCase("buildFailedReceipt")]
    public void Coordinated_builder_return_substitution_reaches_return_binding(string operationName)
    {
        IrDocument document = ReadCheckedIr();
        OperationDescriptor operation = document.Operations.Single(candidate => candidate.Name == operationName);
        SemanticExpression original = operation.Body.ReturnExpression!;
        SemanticExpression replacement = new("NullLiteralExpression", "null", [], null, original.TypeName, original.Start);
        OperationDescriptor altered = operation with
        {
            Steps = operation.Steps.Select(step => step.Kind == "ReturnStatement" ? step with { Expression = replacement, Text = "return null;" } : step).ToArray(),
            Body = operation.Body with { ReturnExpression = replacement },
        };
        ExtractionException exception = Assert.Throws<ExtractionException>(() =>
            ReceiptTerminalFoldProfile.ValidateSerializedIr(SerializeIr(ReplaceOperation(document, operationName, altered))))!;
        Assert.That(exception.Message, Does.StartWith("RECEIPT_RETURN_BINDING:"));
    }

    [TestCase("updateCumulativeGasTracking", false)]
    [TestCase("updateCumulativeGasTracking", true)]
    [TestCase("restore", false)]
    [TestCase("restore", true)]
    public void Coordinated_unconsumed_call_or_write_reaches_effect_ledger(string operationName, bool write)
    {
        IrDocument document = ReadCheckedIr();
        OperationDescriptor operation = document.Operations.Single(candidate => candidate.Name == operationName);
        SemanticInvocation original = operation.Body.Invocations.Single(call => call.Receiver == "Debug");
        SemanticExpression replacement;
        SemanticInvocation[] calls = operation.Body.Invocations.Where(call => call != original).ToArray();
        SemanticAssignment[] assignments = operation.Body.Assignments;
        if (write)
        {
            SemanticAssignment prior = assignments.Single(assignment => assignment.Target == "_cumulativeReceiptGas");
            SemanticExpression target = prior.TargetExpression! with { Start = original.Position };
            SemanticExpression zero = new("NumericLiteralExpression", "0", [], null, target.TypeName, original.Position + 1);
            replacement = new("SimpleAssignmentExpression", "_cumulativeReceiptGas = 0", [target, zero], null, target.TypeName, original.Position);
            assignments = [.. assignments, new("_cumulativeReceiptGas", zero, [], "assignment", original.Position, target)];
            assignments = assignments.OrderBy(assignment => assignment.Position).ToArray();
        }
        else
        {
            SemanticInvocation sample = document.Operations[0].Body.Invocations[0];
            SemanticExpression member = sample.Expression.Children[0];
            string symbol = sample.SymbolId!.Replace(".Add:Add", ".Clear:Clear", StringComparison.Ordinal);
            member = member with
            {
                Text = "_txReceipts.Clear", SymbolId = symbol, Start = original.Position,
                Children = [member.Children[0] with { Start = original.Position }, member.Children[1] with { Text = "Clear", SymbolId = symbol, Start = original.Position + 12 }],
            };
            replacement = original.Expression with { Text = "_txReceipts.Clear()", SymbolId = symbol, Children = [member, new("ArgumentList", "()", [], null, null, original.Position + 16)] };
            SemanticInvocation extra = sample with { Method = "Clear", Arguments = [], Expression = replacement, Position = original.Position, SymbolId = symbol };
            calls = [.. calls, extra];
            calls = calls.OrderBy(call => call.Position).ToArray();
        }
        SemanticStep Rewrite(SemanticStep step) => step.Expression?.Start == original.Position
            ? step with { Expression = replacement, Text = replacement.Text + ";" }
            : step with { Children = step.Children.Select(Rewrite).ToArray() };
        OperationDescriptor altered = operation with
        {
            Steps = operation.Steps.Select(Rewrite).ToArray(),
            Body = operation.Body with { Guards = operation.Body.Guards.Select(Rewrite).ToArray(), Invocations = calls, Assignments = assignments },
        };
        ExtractionException exception = Assert.Throws<ExtractionException>(() =>
            ReceiptTerminalFoldProfile.ValidateSerializedIr(SerializeIr(ReplaceOperation(document, operationName, altered))))!;
        Assert.That(exception.Message, Does.StartWith($"RECEIPT_EFFECT_LEDGER: {operation.Member}:"));
    }

    [TestCase("markAsSuccess", "invocation")]
    [TestCase("buildReceipt", "assignment")]
    public void Omitted_effect_facts_are_rejected(string operationName, string kind)
    {
        IrDocument document = ReadCheckedIr();
        OperationDescriptor operation = document.Operations.Single(candidate => candidate.Name == operationName);
        OperationDescriptor altered = operation with
        {
            Body = kind == "invocation"
                ? operation.Body with { Invocations = operation.Body.Invocations.Skip(1).ToArray() }
                : operation.Body with { Assignments = operation.Body.Assignments.Skip(1).ToArray() },
        };
        ExtractionException exception = Assert.Throws<ExtractionException>(() =>
            ReceiptTerminalFoldProfile.ValidateSerializedIr(SerializeIr(ReplaceOperation(document, operationName, altered))))!;
        Assert.That(exception.Message, Does.StartWith($"RECEIPT_EFFECT_LEDGER: {operation.Member}:"));
    }

    [TestCaseSource(nameof(EarlyExitCases))]
    public void Compile_valid_early_exits_reach_control_flow_admission(string operationName, string marker, string statement)
    {
        OperationDescriptor operation = ReadCheckedIr().Operations.Single(candidate => candidate.Name == operationName);
        using Fixture fixture = new();
        fixture.InsertBefore(operation.SourcePath, operation.Member, marker, statement);
        ExtractionException exception = Assert.Throws<ExtractionException>(() => fixture.ExtractSemanticMutation())!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.StartWith("RECEIPT_CONTROL_FLOW:"));
            Assert.That(exception.Message, Does.Contain(operation.Member));
            Assert.That(Directory.Exists(fixture.Output), Is.False);
        }
    }

    [TestCaseSource(nameof(EarlyExitCases))]
    public void Coordinated_early_exits_in_both_ir_trees_reach_control_flow_admission(string operationName, string marker, string _)
    {
        IrDocument document = ReadCheckedIr();
        OperationDescriptor operation = document.Operations.Single(candidate => candidate.Name == operationName);
        bool inserted = false;
        SemanticStep[] Insert(SemanticStep[] steps)
        {
            List<SemanticStep> result = [];
            foreach (SemanticStep step in steps)
            {
                if (!inserted && step.Text.StartsWith(marker, StringComparison.Ordinal))
                {
                    int start = step.Start;
                    SemanticStep exit = new("ReturnStatement", "return;", operation.Body.ReturnExpression, [], start - 2, start - 1);
                    SemanticExpression condition = new("TrueLiteralExpression", "true", [], null, "bool", start - 3);
                    result.Add(new("IfStatement", "if (true) { return; }", condition,
                        [new("Block", "{ return; }", null, [exit], start - 2, start - 1)], start - 4, start - 1));
                    inserted = true;
                }
                result.Add(step with { Children = Insert(step.Children) });
            }
            return result.ToArray();
        }
        SemanticStep[] changed = Insert(operation.Steps);
        Assert.That(inserted, Is.True, marker);
        static IEnumerable<SemanticStep> Guards(IEnumerable<SemanticStep> steps)
        {
            foreach (SemanticStep step in steps)
            {
                if (step.Kind == "IfStatement") yield return step;
                foreach (SemanticStep guard in Guards(step.Children)) yield return guard;
            }
        }
        OperationDescriptor altered = operation with { Steps = changed, Body = operation.Body with { Guards = Guards(changed).ToArray() } };
        ExtractionException exception = Assert.Throws<ExtractionException>(() => ReceiptTerminalFoldProfile.ValidateSerializedIr(
            SerializeIr(ReplaceOperation(document, operationName, altered))))!;
        Assert.That(exception.Message, Does.StartWith("RECEIPT_CONTROL_FLOW:"));
    }

    [TestCase("if (gasSpent.SpentGas == 0) { throw new InvalidOperationException(); }")]
    [TestCase("while (gasSpent.SpentGas == 0) { }")]
    [TestCase("for (; gasSpent.SpentGas == 0;) { }")]
    [TestCase("do { } while (gasSpent.SpentGas == 0);")]
    [TestCase("if (gasSpent.SpentGas == 0) { }")]
    [TestCase("{ goto receipt_exit; receipt_exit: return; }")]
    [TestCase("try { } finally { }")]
    [TestCase("_ = gasSpent.SpentGas == 0 ? throw new InvalidOperationException() : 0;")]
    public void Compile_valid_unsupported_control_flow_is_rejected(string statement)
    {
        using Fixture fixture = new();
        fixture.InsertBefore(ReceiptTerminalFoldProfile.TracerPath, "MarkAsSuccess", "_txReceipts.Add", statement);
        ExtractionException exception = Assert.Throws<ExtractionException>(() => fixture.ExtractSemanticMutation())!;
        Assert.That(exception.Message, Does.StartWith("RECEIPT_CONTROL_FLOW: MarkAsSuccess:"));
    }

    [TestCase(ReceiptTerminalFoldProfile.EthereumGasPolicyPath, "CombineBlockGas", "", "if (blockExecutionGas == 0) { return 1; }")]
    [TestCase(ReceiptTerminalFoldProfile.TransactionGasInitializationKernelPath, "Combine", "", "if (blockExecutionGas == 0) { return 1; }")]
    [TestCase(ReceiptTerminalFoldProfile.TransactionExtensionsPath, "CalculateEffectiveGasPrice", "", "if (eip1559Enabled) { return UInt256.Zero; }")]
    [TestCase(ReceiptTerminalFoldProfile.AccountingKernelPath, "Accumulate", "ulong cumulativeExecutionGas", "if (previousExecutionGas == 0) { return default; }")]
    [TestCase(ReceiptTerminalFoldProfile.AccountingKernelPath, "FromTotals", "", "if (cumulativeExecutionGas == 0) { return default; }")]
    public void Compile_valid_dependency_early_exits_are_rejected(string sourcePath, string member, string marker, string statement)
    {
        using Fixture fixture = new();
        fixture.InsertBefore(sourcePath, member, marker, statement);
        ExtractionException exception = Assert.Throws<ExtractionException>(() => fixture.ExtractSemanticMutation())!;
        Assert.That(exception.Message, Does.StartWith($"RECEIPT_CONTROL_FLOW: {member}:"));
    }

    [TestCaseSource(nameof(BranchCases))]
    public void Compile_valid_source_arm_flips_reach_polarity_admission(string _, string condition, string marker, string sourcePath)
    {
        using Fixture fixture = new();
        fixture.FlipTrueArm(sourcePath, condition, marker);
        ExtractionException exception = Assert.Throws<ExtractionException>(() => fixture.ExtractSemanticMutation())!;
        Assert.That(exception.Message, Does.Contain("RECEIPT_BRANCH_POLARITY"));
    }

    [TestCaseSource(nameof(BranchCases))]
    public void Serialized_arm_flips_in_both_trees_are_rejected(string operationName, string condition, string marker, string _)
    {
        foreach ((bool updateFacts, bool forgedEmptyArmPosition) in new[] { (false, false), (true, false), (false, true) })
        {
            IrDocument document = ReadCheckedIr();
            OperationDescriptor operation = document.Operations.Single(candidate => candidate.Name == operationName);
            SemanticStep conditional = operation.Body.Guards.Single(guard => guard.Expression?.Text == condition && guard.Text.Contains(marker, StringComparison.Ordinal));
            SemanticStep Flip(SemanticStep step)
            {
                if (step.Kind == "IfStatement" && step.Start == conditional.Start)
                {
                    SemanticStep original = step.Children[0];
                    SemanticStep moved = original with { Start = original.Start + 1 };
                    return step with { Children =
                    [
                        new("Block", "{}", null, [], original.Start, original.Start + 1),
                        new("ElseClause", "else " + original.Text, null, [moved], moved.Start, moved.End),
                    ] };
                }
                return step with { Children = step.Children.Select(Flip).ToArray() };
            }
            SemanticBranch[] FlipFacts(SemanticBranch[] branches) => branches.Select(branch =>
                updateFacts && branch.Position == conditional.Start ? branch with { WhenTrue = false } : branch).ToArray();
            int FlipPosition(int position, SemanticBranch[] branches) => forgedEmptyArmPosition &&
                branches.Any(branch => branch.Position == conditional.Start) ? conditional.Children[0].Start : position;
            OperationDescriptor altered = operation with
            {
                Steps = operation.Steps.Select(Flip).ToArray(),
                Body = operation.Body with
                {
                    Guards = operation.Body.Guards.Select(Flip).ToArray(),
                    Invocations = operation.Body.Invocations.Select(invocation => invocation with
                    {
                        Branches = FlipFacts(invocation.Branches), Position = FlipPosition(invocation.Position, invocation.Branches),
                    }).ToArray(),
                    Assignments = operation.Body.Assignments.Select(assignment => assignment with
                    {
                        Branches = FlipFacts(assignment.Branches), Position = FlipPosition(assignment.Position, assignment.Branches),
                    }).ToArray(),
                },
            };
            byte[] json = SerializeIr(ReplaceOperation(document, operationName, altered));
            ExtractionException exception = Assert.Throws<ExtractionException>(() => ReceiptTerminalFoldProfile.ValidateSerializedIr(json))!;
            Assert.That(exception.Message, Does.Contain(updateFacts ? "RECEIPT_BRANCH_POLARITY" : "RECEIPT_BRANCH_PATH"));
        }
    }

    [Test]
    public void Incomplete_branch_source_terms_are_rejected([Values] bool guardTree)
    {
        IrDocument document = ReadCheckedIr();
        OperationDescriptor operation = document.Operations.Single(candidate => candidate.Name == "finalizeTransaction");
        static SemanticStep BreakDeclaration(SemanticStep step) => step with
        {
            DeclaredTarget = step.DeclaredTarget is null ? null : step.DeclaredTarget with { Children = null! },
            Children = step.Children.Select(BreakDeclaration).ToArray(),
        };
        OperationDescriptor altered = guardTree
            ? operation with { Body = operation.Body with { Guards = [operation.Body.Guards[0] with { Children = null! }] } }
            : operation with { Steps = operation.Steps.Select(BreakDeclaration).ToArray() };
        ExtractionException exception = Assert.Throws<ExtractionException>(() => ReceiptTerminalFoldProfile.ValidateSerializedIr(
            SerializeIr(ReplaceOperation(document, operation.Name, altered))))!;
        Assert.That(exception.Message, Does.Contain(guardTree ? "incomplete semantic step" : "incomplete semantic expression"));
    }

    private static byte[] SerializeIr(IrDocument document) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }).ReplaceLineEndings("\n") + "\n");

    [Test]
    public void Artifact_publication_preserves_open_previous_generation()
    {
        using TemporaryDirectory temporary = new("receipt-terminal-fold-publication");
        (string[] paths, byte[][] previous, byte[][] next) = PublicationFiles(temporary.Path);
        using FileStream original = new(paths[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Publish(paths, next);
        using MemoryStream retained = new();
        original.CopyTo(retained);
        Assert.That(retained.ToArray(), Is.EqualTo(previous[0]));
        AssertPublication(paths, next, temporary.Path);
    }

    [Test]
    public void Artifact_publication_failure_preserves_whole_files_and_manifest(
        [Range(0, 2)] int beforeIndex, [Values] bool cancellation)
    {
        using TemporaryDirectory temporary = new("receipt-terminal-fold-publication");
        (string[] paths, byte[][] previous, byte[][] next) = PublicationFiles(temporary.Path);
        Exception failure = cancellation ? new OperationCanceledException("publication interrupted") : new IOException("publication failed");
        Exception? actual = Assert.Catch(() => Publish(paths, next, index => { if (index == beforeIndex) throw failure; }));
        Assert.That(actual, Is.SameAs(failure));
        byte[][] expected = previous.Select((bytes, index) => index < beforeIndex ? next[index] : bytes).ToArray();
        AssertPublication(paths, expected, temporary.Path);
        Assert.That(File.ReadAllBytes(paths[2]), Is.EqualTo(previous[2]));
        Publish(paths, next);
        AssertPublication(paths, next, temporary.Path);
    }

    [Test]
    public void Artifact_staging_failure_preserves_all_previous_files()
    {
        using TemporaryDirectory temporary = new("receipt-terminal-fold-publication");
        (string[] paths, byte[][] previous, byte[][] next) = PublicationFiles(temporary.Path);
        string[] invalid = [paths[0], Path.Combine(temporary.Path, "missing", "kernel.lean"), paths[2]];
        Assert.That(() => Publish(invalid, next), Throws.TypeOf<DirectoryNotFoundException>());
        AssertPublication(paths, previous, temporary.Path);
    }

    [Test]
    public void Artifact_replace_failure_leaves_the_manifest_unpublished()
    {
        using TemporaryDirectory temporary = new("receipt-terminal-fold-publication");
        (string[] paths, byte[][] previous, byte[][] next) = PublicationFiles(temporary.Path);
        string occupied = Path.Combine(temporary.Path, "occupied");
        Directory.CreateDirectory(occupied);
        Assert.That(() => Publish([paths[0], occupied, paths[2]], next), Throws.TypeOf<IOException>());
        AssertPublication(paths, [next[0], previous[1], previous[2]], temporary.Path);
        Assert.That(Directory.Exists(occupied), Is.True);
    }

    private static (string[] Paths, byte[][] Previous, byte[][] Next) PublicationFiles(string directory)
    {
        string[] paths = [Path.Combine(directory, "kernel.ir.json"), Path.Combine(directory, "kernel.lean"), Path.Combine(directory, "kernel.source-manifest.json")];
        byte[][] previous = [Encoding.UTF8.GetBytes("old IR"), Encoding.UTF8.GetBytes("old Lean"), Encoding.UTF8.GetBytes("old manifest")];
        byte[][] next = [Encoding.UTF8.GetBytes("new IR with different length"), Encoding.UTF8.GetBytes("new Lean"), Encoding.UTF8.GetBytes("new manifest")];
        for (int index = 0; index < paths.Length; index++) File.WriteAllBytes(paths[index], previous[index]);
        return (paths, previous, next);
    }

    private static void Publish(string[] paths, byte[][] bytes, Action<int>? beforeReplace = null) =>
        ReceiptTerminalFoldArtifacts.Publish(paths[0], bytes[0], paths[1], bytes[1], paths[2], bytes[2], beforeReplace);

    private static void AssertPublication(string[] paths, byte[][] expected, string directory)
    {
        using (Assert.EnterMultipleScope())
        {
            for (int index = 0; index < paths.Length; index++) Assert.That(File.ReadAllBytes(paths[index]), Is.EqualTo(expected[index]));
            Assert.That(Directory.GetFiles(directory), Is.EquivalentTo(paths));
        }
    }

    [TestCase("update-argument")]
    [TestCase("restore-argument")]
    public void Paired_source_mutations_are_rebound_into_typed_ir_and_typecheck_Lean(string mutation)
    {
        string root = FindRepoRoot();
        string baselineLean = File.ReadAllText(Path.Combine(root, ReceiptTerminalFoldProfile.DefaultLeanRelativePath));
        using Fixture fixture = new();

        switch (mutation)
        {
            case "update-argument":
                fixture.Replace(
                    ReceiptTerminalFoldProfile.TracerPath,
                    "            gasConsumed.BlockStateGas,\n            gasConsumed.SpentGas);",
                    "            gasConsumed.BlockStateGas,\n            gasConsumed.OperationGas);");
                break;
            case "restore-argument":
                fixture.Replace(
                    ReceiptTerminalFoldProfile.TracerPath,
                    "            cumulativeReceipt);",
                    "            cumulativeReceipt + 1);");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        ExtractionResult result = fixture.ExtractSemanticMutation();
        IrDocument changed = ReceiptTerminalFoldProfile.DeserializeIr(File.ReadAllBytes(result.IrPath));
        string changedLean = File.ReadAllText(result.LeanPath);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changedLean, Is.Not.EqualTo(baselineLean));
            Assert.That(changed.Operations.SelectMany(static operation => operation.Body.Invocations)
                .Any(invocation => invocation.Expression.Text.Contains("OperationGas", StringComparison.Ordinal) ||
                    invocation.Expression.Text.Contains("cumulativeReceipt + 1", StringComparison.Ordinal)), Is.True);
            Assert.That(changed.Operations.SelectMany(static operation => operation.Body.Invocations)
                .All(invocation => !string.IsNullOrWhiteSpace(invocation.SymbolId) &&
                    !string.IsNullOrWhiteSpace(invocation.ReturnType)), Is.True);
        }

        AssertLeanCompiles(File.ReadAllBytes(result.LeanPath));
    }

    [TestCase(ReceiptTerminalFoldProfile.CallerPath, "WorldState.ResetTransient();", "WorldState.ResetTransientMissing();")]
    [TestCase(ReceiptTerminalFoldProfile.CallerPath, "WorldState.ReapEmptyAccounts();", "WorldState.ReapEmptyAccountsMissing();")]
    [TestCase(ReceiptTerminalFoldProfile.CallerPath, "byte[] output", "MissingOutputType[] output")]
    [TestCase(ReceiptTerminalFoldProfile.TracerPath, "gasConsumed.SpentGas", "gasConsumed.UnknownGas")]
    [TestCase(ReceiptTerminalFoldProfile.ReceiptPath, "[AllowNull]", "[MissingAttribute]")]
    [TestCase(ReceiptTerminalFoldProfile.TransactionPath, "nameof(", "missingNameof(")]
    public void Semantic_source_mutations_reject_compiler_errors_and_unresolved_bindings(
        string path,
        string original,
        string replacement)
    {
        using Fixture fixture = new();
        fixture.Replace(path, original, replacement);

        ExtractionException exception = Assert.Throws<ExtractionException>(() => fixture.ExtractSemanticMutation())!;
        Assert.That(exception.Message, Does.Contain("compiler/reference closure produced"));
    }

    [TestCase(ReceiptTerminalFoldProfile.TracerPath, "if (numToRemove > 0)", "if (numToRemove >= 0)", "RECEIPT_EFFECT_LEDGER: Restore:")]
    [TestCase(ReceiptTerminalFoldProfile.CallerPath, "if (tracer.IsTracingReceipt)", "if (!tracer.IsTracingReceipt)", "RECEIPT_BRANCH_POLARITY: finalization")]
    public void Semantic_source_guard_mutations_reject_lowering_contract(
        string path,
        string original,
        string replacement,
        string diagnostic)
    {
        using Fixture fixture = new();
        fixture.Replace(path, original, replacement);

        ExtractionException exception = Assert.Throws<ExtractionException>(() => fixture.ExtractSemanticMutation())!;
        Assert.That(exception.Message, Does.StartWith(diagnostic));
    }

    [Test]
    public void Missing_trusted_platform_metadata_is_rejected_before_binding()
    {
        object? original = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        try
        {
            AppContext.SetData("TRUSTED_PLATFORM_ASSEMBLIES", null);
            using Fixture fixture = new();
            ExtractionException exception = Assert.Throws<ExtractionException>(() => fixture.ExtractSemanticMutation())!;
            Assert.That(exception.Message, Does.Contain("trusted platform assembly list"));
        }
        finally
        {
            AppContext.SetData("TRUSTED_PLATFORM_ASSEMBLIES", original);
        }
    }

    [TestCase("operations", "operator/order mutation")]
    [TestCase("mapping", "mapping mutation")]
    [TestCase("scope", "scope mutation")]
    [TestCase("fields", "emitter field-lowering escape")]
    [TestCase("effects", "emitter operation-lowering escape")]
    [TestCase("contract", "emitter operation contract escape")]
    [TestCase("inputs", "emitter operation input contract escape")]
    [TestCase("outputs", "emitter operation output contract escape")]
    [TestCase("forwarding-guards", "success/failure forwarding guard mismatch")]
    [TestCase("receipt-forwarding-link", "receipt/forwarding payload mismatch")]
    [TestCase("receipt-status-link", "receipt status call mismatch")]
    [TestCase("nonempty-collection", "non-empty collection lowering escape")]
    [TestCase("bindings", "semantic binding escape")]
    [TestCase("body", "unsupported source expression escape")]
    public void Mutated_ir_is_rejected(string mutation, string _)
    {
        JsonObject document = ParseCheckedIr();
        switch (mutation)
        {
            case "operations":
            {
                JsonArray operations = document["operations"]!.AsArray();
                JsonNode first = operations[0]!.DeepClone();
                operations[0] = operations[1]!.DeepClone();
                operations[1] = first;
                break;
            }
            case "mapping":
                document["mappings"]!.AsArray()[0]!.
                    AsObject()["semantics"] = "arbitrary mapping";
                break;
            case "scope":
                document["scope"]!.AsObject()["executionContext"] = "parallel and BAL";
                break;
            case "fields":
                document["inputs"]!.AsObject()["finalizeFields"]!.AsArray()[0] = "ChangedStatus";
                break;
            case "effects":
                document["operations"]!.AsArray()[0]!.AsObject()["orderedEffects"]!.AsArray()[0] = "changedEffect";
                break;
            case "contract":
                document["operations"]!.AsArray()[0]!.AsObject()["forwardedOrProjected"]!.AsArray()[0] = "changedProjection";
                break;
            case "inputs":
                document["operations"]!.AsArray()[0]!.AsObject()["inputs"]!.AsArray()[0] = "changedInput";
                break;
            case "outputs":
                document["operations"]!.AsArray()[0]!.AsObject()["outputs"]!.AsArray()[0] = "changedOutput";
                break;
            case "forwarding-guards":
            {
                JsonObject failure = document["operations"]!.AsArray()
                    .Select(node => node!.AsObject())
                    .Single(operation => operation["name"]!.GetValue<string>() == "markAsFailed");
                JsonObject nestedGuard = failure["body"]!["guards"]!.AsArray()[0]!.AsObject();
                nestedGuard["expression"] = new JsonObject
                {
                    ["kind"] = "FalseLiteralExpression",
                    ["text"] = "false",
                    ["children"] = new JsonArray(),
                    ["symbolId"] = null,
                    ["typeName"] = "global::System.Boolean",
                };
                break;
            }
            case "receipt-forwarding-link":
            {
                JsonObject success = document["operations"]!.AsArray()
                    .Select(node => node!.AsObject())
                    .Single(operation => operation["name"]!.GetValue<string>() == "markAsSuccess");
                JsonObject build = success["body"]!["invocations"]!.AsArray()
                    .Select(node => node!.AsObject())
                    .Single(invocation => invocation["method"]!.GetValue<string>() == "BuildReceipt");
                build["arguments"]!.AsArray()[3]!.
                    AsObject()["text"] = "output";
                break;
            }
            case "receipt-status-link":
            {
                JsonObject success = document["operations"]!.AsArray()
                    .Select(node => node!.AsObject())
                    .Single(operation => operation["name"]!.GetValue<string>() == "markAsSuccess");
                JsonObject build = success["body"]!["invocations"]!.AsArray()
                    .Select(node => node!.AsObject())
                    .Single(invocation => invocation["method"]!.GetValue<string>() == "BuildReceipt");
                JsonObject status = build["arguments"]!.AsArray()[2]!.AsObject();
                status["text"] = "StatusCode.Failure";
                status["children"]!.AsArray()[1]!.
                    AsObject()["text"] = "Failure";
                break;
            }
            case "nonempty-collection":
            {
                JsonObject finalize = document["operations"]!.AsArray()
                    .Select(node => node!.AsObject())
                    .Single(operation => operation["name"]!.GetValue<string>() == "finalizeTransaction");
                JsonObject output = finalize["body"]!["assignments"]!.AsArray()
                    .Select(node => node!.AsObject())
                    .Single(assignment => assignment["target"]!.GetValue<string>() == "output" &&
                        assignment["form"]!.GetValue<string>() == "initializer");
                JsonObject outputExpression = output["value"]!.AsObject();
                JsonObject emptyCollection = outputExpression["children"]!.AsArray()[2]!.AsObject();
                emptyCollection["children"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["kind"] = "NumericLiteralExpression",
                        ["text"] = "1",
                        ["children"] = new JsonArray(),
                        ["symbolId"] = null,
                        ["typeName"] = "global::System.Int32",
                    },
                };
                JsonObject binding = document["lowering"]!["finalization"]!.AsArray()
                    .Select(node => node!.AsObject())
                    .Single(candidate => candidate["target"]!.GetValue<string>() == "terminal.output");
                binding["expression"] = outputExpression.DeepClone();
                break;
            }
            case "bindings":
                document["semanticBindings"]!.AsArray()[0] = "changed binding";
                break;
            case "body":
            {
                JsonObject update = document["operations"]!.AsArray()
                    .Select(node => node!.AsObject())
                    .Single(operation => operation["name"]!.GetValue<string>() == "updateCumulativeGasTracking");
                JsonObject accumulate = update["body"]!["invocations"]!.AsArray()[0]!.AsObject();
                accumulate["arguments"]!.AsArray()[5]!["text"] = "unknownGas";
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        Assert.That(() => ReceiptTerminalFoldProfile.ValidateSerializedIr(
                Encoding.UTF8.GetBytes(document.ToJsonString())),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Mutated_manifest_identity_and_aggregate_are_rejected()
    {
        JsonObject manifest = ParseCheckedManifest();

        JsonObject member = manifest["members"]!.AsArray()[0]!.AsObject();
        member["id"] = "mutated.member";
        Assert.That(() => ReceiptTerminalFoldProfile.ValidateSerializedManifest(
                Encoding.UTF8.GetBytes(manifest.ToJsonString())),
            Throws.TypeOf<ExtractionException>());

        manifest = ParseCheckedManifest();
        manifest["combinedMemberSha256"] = new string('0', 64);
        Assert.That(() => ReceiptTerminalFoldProfile.ValidateSerializedManifest(
                Encoding.UTF8.GetBytes(manifest.ToJsonString())),
            Throws.TypeOf<ExtractionException>());

        manifest = ParseCheckedManifest();
        manifest["accountingKernel"]!.AsObject()["generatedLeanSha256"] = new string('0', 64);
        Assert.That(() => ReceiptTerminalFoldProfile.ValidateSerializedManifest(
                Encoding.UTF8.GetBytes(manifest.ToJsonString())),
            Throws.TypeOf<ExtractionException>());

        manifest = ParseCheckedManifest();
        JsonArray compilerReferences = manifest["compilerReferences"]!.AsArray();
        int int256Index = Enumerable.Range(0, compilerReferences.Count)
            .Single(index => compilerReferences[index]!.AsObject()["assemblyName"]!.GetValue<string>() == "Nethermind.Int256" &&
                compilerReferences[index]!.AsObject()["selected"]!.GetValue<bool>());
        compilerReferences.RemoveAt(int256Index);
        Assert.That(() => ReceiptTerminalFoldProfile.ValidateSerializedManifest(
                Encoding.UTF8.GetBytes(manifest.ToJsonString())),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Serialized_documents_reject_unknown_duplicate_and_noncanonical_forms()
    {
        byte[] ir = File.ReadAllBytes(Path.Combine(
            FindRepoRoot(), ReceiptTerminalFoldProfile.DefaultOutputRelativePath,
            ReceiptTerminalFoldProfile.IrFileName));
        byte[] manifest = File.ReadAllBytes(Path.Combine(
            FindRepoRoot(), ReceiptTerminalFoldProfile.DefaultOutputRelativePath,
            ReceiptTerminalFoldProfile.ManifestFileName));

        JsonObject irObject = JsonNode.Parse(ir)!.AsObject();
        irObject["unexpected"] = true;
        Assert.That(() => ReceiptTerminalFoldProfile.ValidateSerializedIr(
                Encoding.UTF8.GetBytes(irObject.ToJsonString())),
            Throws.TypeOf<ExtractionException>());

        string duplicate = Encoding.UTF8.GetString(ir).Replace(
            "{\n  \"schemaVersion\":", "{\n  \"schemaVersion\": 1,\n  \"schemaVersion\":",
            StringComparison.Ordinal);
        Assert.That(() => ReceiptTerminalFoldProfile.ValidateSerializedIr(Encoding.UTF8.GetBytes(duplicate)),
            Throws.TypeOf<ExtractionException>());

        JsonObject manifestObject = JsonNode.Parse(manifest)!.AsObject();
        string reordered = manifestObject.ToJsonString();
        Assert.That(() => ReceiptTerminalFoldProfile.ValidateSerializedManifest(
                Encoding.UTF8.GetBytes(reordered)),
            Throws.TypeOf<ExtractionException>());
    }

    [TestCase(ReceiptTerminalFoldProfile.TracerPath, "parallel = false", "parallel = true")]
    [TestCase(ReceiptTerminalFoldProfile.TracerPath, "_txReceipts.Add(BuildReceipt", "_txReceipts.Add(BuildFailedReceipt")]
    [TestCase(ReceiptTerminalFoldProfile.AccountingKernelPath,
        "unchecked(previousReceiptGas + transactionPaidGas)",
        "unchecked(previousReceiptGas + transactionPaidGas + 1)")]
    [TestCase(ReceiptTerminalFoldProfile.CallerPath,
        "substate.ShouldRevert ? substate.Output.AsReadOnlyArray() : []",
        "substate.ShouldRevert ? substate.Output.AsReadOnlyArray() : [1]")]
    [TestCase(ReceiptTerminalFoldProfile.TracerPath,
        "public bool IsTracingReceipt => true",
        "public bool IsTracingReceipt => false")]
    [TestCase(ReceiptTerminalFoldProfile.StatusCodePath,
        "public const byte Success = 1",
        "public const byte Success = 2")]
    [TestCase(ReceiptTerminalFoldProfile.EthereumGasPolicyPath,
        "BlockGasAccountingKernel.Combine(blockExecutionGas, blockStateGas)",
        "BlockGasAccountingKernel.Combine(blockExecutionGas, blockStateGas + 1)")]
    [TestCase(ReceiptTerminalFoldProfile.TransactionGasInitializationKernelPath,
        "Math.Max(blockExecutionGas, blockStateGas)",
        "Math.Min(blockExecutionGas, blockStateGas)")]
    public void Source_operator_and_order_mutations_are_rejected(string path, string original, string replacement)
    {
        using Fixture fixture = new();
        fixture.Replace(path, original, replacement);

        ExtractionException exception = Assert.Throws<ExtractionException>(() => fixture.Extract())!;
        Assert.That(exception.Message, Does.Contain("Pinned source changed"));
    }

    [Test]
    public void Vectors_are_proved_and_instantiate_the_production_refinement_domain()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(),
            "tools/Evm/Lean/ReceiptTerminalFoldExtractor/Vectors/ReceiptTerminalFoldVectors.lean"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source, Does.Not.Contain("def checks"));
            Assert.That(source, Does.Not.Contain("String × Bool"));
            Assert.That(source, Does.Not.Contain("native_decide"));
            Assert.That(source, Does.Contain("decide"));
            Assert.That(source, Does.Contain("AccountingValid"));
            Assert.That(source, Does.Contain("ProductionFinalizeDomain"));
            Assert.That(source, Does.Contain("transaction_isContractCreation_iff_to_isNone"));
            Assert.That(source, Does.Contain("generatedFinalizeTransaction_refines_spec"));
        }
    }

    [Test]
    public void Missing_source_and_missing_delegated_kernel_are_rejected()
    {
        using Fixture fixture = new();
        File.Delete(Path.Combine(fixture.Root, ReceiptTerminalFoldProfile.TracerPath));
        Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());

        using Fixture kernelFixture = new();
        File.Delete(Path.Combine(kernelFixture.Root,
            "tools/Evm/Lean/Eip803x/Generated/BlockReceiptGasAccountingKernel.lean"));
        Assert.That(() => kernelFixture.ExtractSemanticMutation(), Throws.TypeOf<ExtractionException>());
    }

    private static ExtractionResult Extract(string root, string output) => ReceiptTerminalFoldProfile.Extract(
        root, output, Path.Combine(output, "ReceiptTerminalFoldKernel.lean"));

    private static IrDocument MutateSemanticIr(IrDocument document, string mutation) => CoordinateSemanticTrees(mutation switch
        {
            "update-argument" => ReplaceInvocationArgument(document, "updateCumulativeGasTracking", "Accumulate", "BlockReceiptGasAccountingKernel", 5,
                static invocation => invocation.Arguments[4]),
            "restore-argument" => ReplaceInvocationArgument(document, "restore", "FromTotals", "BlockReceiptGasAccountingKernel", 2,
                static invocation => new SemanticExpression("AddExpression", "cumulativeReceipt + 1", [
                    invocation.Arguments[2],
                    new SemanticExpression("NumericLiteralExpression", "1", [], null, invocation.Arguments[2].TypeName),
                ], null, invocation.Arguments[2].TypeName)),
            "restore-assignment" => ReplaceRestoreAssignment(document),
            "receipt-field" => ReplaceReceiptField(document),
            "terminal-payload" => ReplaceBothTerminalPayloads(document),
            "terminal-order" => SwapTerminalForwardingOrder(document),
            "status-constant" => document with
            {
                Lowering = document.Lowering with
                {
                    StatusCode = document.Lowering.StatusCode with
                    {
                        Success = document.Lowering.StatusCode.Success with { Text = "2" },
                    },
                },
            },
            "failure-error" => ReplaceFailureErrorInitializer(document),
            "result-error" => ReplaceResultError(document),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        });

    private static IrDocument CoordinateSemanticTrees(IrDocument document) => document with
    {
        Operations = document.Operations.Select(operation =>
        {
            SemanticExpression Rewrite(SemanticExpression expression)
            {
                if (expression.Kind == "InvocationExpression")
                {
                    SemanticInvocation invocation = operation.Body.Invocations.Single(candidate => candidate.Position == expression.Start);
                    SemanticExpression argumentList = invocation.Expression.Children[1];
                    return invocation.Expression with
                    {
                        Children = [invocation.Expression.Children[0], argumentList with
                        {
                            Children = argumentList.Children.Select((argument, index) => argument with
                            {
                                Children = [Rewrite(invocation.Arguments[index])],
                            }).ToArray(),
                        }],
                    };
                }
                if (expression.Kind == "SimpleAssignmentExpression")
                {
                    SemanticAssignment assignment = operation.Body.Assignments.Single(candidate => candidate.Form == "assignment" && candidate.Position == expression.Start);
                    return expression with { Children = [expression.Children[0], Rewrite(assignment.Value)] };
                }
                return expression with { Children = expression.Children.Select(Rewrite).ToArray() };
            }
            SemanticStep Step(SemanticStep step)
            {
                SemanticExpression? expression = step.Expression;
                if (step.Kind == "LocalDeclarationStatement")
                    expression = operation.Body.Assignments.Single(assignment => assignment.Form == "initializer" && assignment.Position == expression!.Start).Value;
                if (step.Kind is "ReturnStatement" or "ArrowExpressionClause") expression = operation.Body.ReturnExpression;
                return step with { Expression = expression is null ? null : Rewrite(expression), Children = step.Children.Select(Step).ToArray() };
            }
            return operation with
            {
                Steps = operation.Steps.Select(Step).ToArray(),
                Body = operation.Body with
                {
                    Guards = operation.Body.Guards.Select(Step).ToArray(),
                    Invocations = operation.Body.Invocations.Select(invocation => invocation with { Expression = Rewrite(invocation.Expression) }).ToArray(),
                    Assignments = operation.Body.Assignments.Select(assignment => assignment with { Value = Rewrite(assignment.Value) }).ToArray(),
                    ReturnExpression = operation.Body.ReturnExpression is null ? null : Rewrite(operation.Body.ReturnExpression),
                },
            };
        }).ToArray(),
    };

    private static byte[] EmitSemanticMutation(IrDocument document) => ReceiptTerminalFoldLeanEmitter.Emit(
        document,
        ReceiptTerminalFoldProfile.ExtractorVersion,
        "test-compiler",
        ReceiptTerminalFoldProfile.TracerPath,
        new string('0', 64),
        ReceiptTerminalFoldProfile.Sha256(Encoding.UTF8.GetBytes("baseline")));

    private static IrDocument ReplaceInvocationArgument(
        IrDocument document,
        string operationName,
        string method,
        string receiver,
        int argumentIndex,
        Func<SemanticInvocation, SemanticExpression> replacement)
    {
        OperationDescriptor operation = document.Operations.Single(candidate => candidate.Name == operationName);
        SemanticInvocation invocation = operation.Body.Invocations.Single(candidate => candidate.Method == method && candidate.Receiver == receiver);
        SemanticExpression[] arguments = invocation.Arguments.ToArray();
        arguments[argumentIndex] = replacement(invocation);
        SemanticInvocation[] invocations = operation.Body.Invocations.ToArray();
        int invocationIndex = Array.IndexOf(invocations, invocation);
        SemanticInvocation changed = invocation with { Arguments = arguments };
        invocations[invocationIndex] = changed;
        SemanticAssignment[] assignments = operation.Body.Assignments
            .Select(assignment => SameExpression(assignment.Value, invocation.Expression)
                ? assignment with { Value = changed.Expression }
                : assignment)
            .ToArray();
        return ReplaceOperation(document, operationName, operation with
        {
            Body = operation.Body with { Invocations = invocations, Assignments = assignments },
        });
    }

    private static IrDocument ReplaceAssignmentValue(
        IrDocument document,
        string operationName,
        string target,
        SemanticExpression value)
    {
        OperationDescriptor operation = document.Operations.Single(candidate => candidate.Name == operationName);
        SemanticAssignment[] assignments = operation.Body.Assignments.ToArray();
        int assignmentIndex = Array.FindIndex(assignments,
            assignment => assignment.Target == target && assignment.Form == "assignment");
        Assert.That(assignmentIndex, Is.GreaterThanOrEqualTo(0));
        assignments[assignmentIndex] = assignments[assignmentIndex] with { Value = value };
        return ReplaceOperation(document, operationName, operation with
        {
            Body = operation.Body with { Assignments = assignments },
        });
    }

    private static IrDocument ReplaceReceiptField(IrDocument document)
    {
        OperationDescriptor operation = document.Operations.Single(candidate => candidate.Name == "buildReceipt");
        SemanticAssignment source = operation.Body.Assignments.Single(candidate => candidate.Target == "TxType");
        SemanticAssignment[] assignments = operation.Body.Assignments.ToArray();
        int assignmentIndex = Array.FindIndex(assignments,
            assignment => assignment.Target == "BlockNumber" && assignment.Form == "assignment");
        Assert.That(assignmentIndex, Is.GreaterThanOrEqualTo(0));
        assignments[assignmentIndex] = assignments[assignmentIndex] with { Value = source.Value };
        OperationDescriptor changedOperation = operation with
        {
            Body = operation.Body with { Assignments = assignments },
        };
        OperationDescriptor[] operations = document.Operations.ToArray();
        int operationIndex = Array.FindIndex(operations, candidate => candidate.Name == "buildReceipt");
        operations[operationIndex] = changedOperation;
        LoweringBinding[] fields = document.Lowering.ReceiptFields.ToArray();
        int bindingIndex = Array.FindIndex(fields, binding => binding.Target == "receipt.BlockNumber");
        Assert.That(bindingIndex, Is.GreaterThanOrEqualTo(0));
        fields[bindingIndex] = fields[bindingIndex] with { Expression = source.Value };
        return document with
        {
            Operations = operations,
            Lowering = document.Lowering with { ReceiptFields = fields },
        };
    }

    private static IrDocument ReplaceRestoreAssignment(IrDocument document)
    {
        OperationDescriptor operation = document.Operations.Single(candidate => candidate.Name == "restore");
        SemanticExpression replacement = operation.Body.Assignments
            .Single(assignment => assignment.Target == "_cumulativeReceiptGas")
            .Value;
        return ReplaceAssignmentValue(document, "restore", "Block.Header.GasUsed", replacement);
    }

    private static IrDocument ReplaceBothTerminalPayloads(IrDocument document)
    {
        OperationDescriptor operation = document.Operations.Single(candidate => candidate.Name == "markAsSuccess");
        SemanticInvocation[] invocations = operation.Body.Invocations.ToArray();
        SemanticExpression originalOutput = operation.Body.Invocations
            .Single(candidate => candidate.Method == "MarkAsSuccess" && candidate.Receiver == "otherTxTracer")
            .Arguments[2];
        SemanticExpression changedOutput = new(
            "ConditionalExpression",
            "true ? emptyBytes : output",
            [
                new SemanticExpression("TrueLiteralExpression", "true", [], null, "global::System.Boolean"),
                new SemanticExpression("IdentifierName", "emptyBytes", [], null, originalOutput.TypeName),
                originalOutput,
            ],
            null,
            originalOutput.TypeName);
        for (int index = 0; index < invocations.Length; index++)
        {
            SemanticInvocation invocation = invocations[index];
            if (invocation.Method == "MarkAsSuccess" && invocation.Receiver is "otherTxTracer" or "_currentTxTracer")
            {
                SemanticExpression[] arguments = invocation.Arguments.ToArray();
                arguments[2] = changedOutput;
                invocations[index] = invocation with { Arguments = arguments };
            }
        }

        return ReplaceOperation(document, "markAsSuccess", operation with
        {
            Body = operation.Body with { Invocations = invocations },
        });
    }

    private static IrDocument SwapTerminalForwardingOrder(IrDocument document)
    {
        IrDocument changed = SwapTerminalForwardingOrder(document, "markAsSuccess", "MarkAsSuccess");
        return SwapTerminalForwardingOrder(changed, "markAsFailed", "MarkAsFailed");
    }

    private static IrDocument SwapTerminalForwardingOrder(IrDocument document, string operationName, string method)
    {
        OperationDescriptor operation = document.Operations.Single(candidate => candidate.Name == operationName);
        SemanticInvocation nested = operation.Body.Invocations.Single(candidate => candidate.Method == method && candidate.Receiver == "otherTxTracer");
        SemanticInvocation current = operation.Body.Invocations.Single(candidate => candidate.Method == method && candidate.Receiver == "_currentTxTracer");
        SemanticInvocation[] invocations = operation.Body.Invocations.ToArray();
        int nestedIndex = Array.IndexOf(invocations, nested);
        int currentIndex = Array.IndexOf(invocations, current);
        invocations[nestedIndex] = nested with { Position = current.Position };
        invocations[currentIndex] = current with { Position = nested.Position };
        return ReplaceOperation(document, operationName, operation with
        {
            Body = operation.Body with { Invocations = invocations },
        });
    }

    private static IrDocument ReplaceFailureErrorInitializer(IrDocument document)
    {
        OperationDescriptor operation = document.Operations.Single(candidate => candidate.Name == "finalizeTransaction");
        SemanticAssignment initializer = operation.Body.Assignments.Single(candidate => candidate.Target == "error" && candidate.Form == "initializer");
        SemanticAssignment[] assignments = operation.Body.Assignments.ToArray();
        int assignmentIndex = Array.IndexOf(assignments, initializer);
        assignments[assignmentIndex] = initializer with
        {
            Value = document.Lowering.Result.ExceptionError with { Start = initializer.Value.Start },
        };
        IrDocument changed = ReplaceOperation(document, "finalizeTransaction", operation with
        {
            Body = operation.Body with { Assignments = assignments },
        });
        LoweringBinding[] finalization = changed.Lowering.Finalization.ToArray();
        int bindingIndex = Array.FindIndex(finalization, binding => binding.Target == "terminal.error");
        Assert.That(bindingIndex, Is.GreaterThanOrEqualTo(0));
        finalization[bindingIndex] = finalization[bindingIndex] with { Expression = document.Lowering.Result.ExceptionError };
        return changed with { Lowering = changed.Lowering with { Finalization = finalization } };
    }

    private static IrDocument ReplaceResultError(IrDocument document)
    {
        OperationDescriptor operation = document.Operations.Single(candidate => candidate.Name == "finalizeTransaction");
        SemanticExpression returnExpression = operation.Body.ReturnExpression!;
        SemanticExpression invocation = Child(returnExpression, 1);
        SemanticExpression argumentList = Child(invocation, 1);
        SemanticExpression[] arguments = argumentList.Children.ToArray();
        SemanticExpression replacement = document.Lowering.Finalization
            .Single(binding => binding.Target == "terminal.error").Expression!;
        arguments[1] = arguments[1] with { Children = [replacement] };
        SemanticExpression changedArgumentList = argumentList with { Children = arguments };
        SemanticExpression[] invocationChildren = invocation.Children.ToArray();
        invocationChildren[1] = changedArgumentList;
        SemanticExpression changedInvocation = invocation with { Children = invocationChildren };
        SemanticExpression[] returnChildren = returnExpression.Children.ToArray();
        returnChildren[1] = changedInvocation;
        SemanticExpression changedReturn = returnExpression with { Children = returnChildren };
        IrDocument changed = ReplaceOperation(document, "finalizeTransaction", operation with
        {
            Body = operation.Body with
            {
                ReturnExpression = changedReturn,
                Invocations = operation.Body.Invocations.Select(call => call.Position == invocation.Start
                    ? call with { Expression = changedInvocation, Arguments = [call.Arguments[0], replacement] }
                    : call).ToArray(),
            },
        });

        LoweringBinding[] finalization = changed.Lowering.Finalization.ToArray();
        int bindingIndex = Array.FindIndex(finalization, binding => binding.Target == "finalization.result.error");
        Assert.That(bindingIndex, Is.GreaterThanOrEqualTo(0));
        finalization[bindingIndex] = finalization[bindingIndex] with { Expression = replacement };
        return changed with
        {
            Lowering = changed.Lowering with
            {
                Finalization = finalization,
                Result = changed.Lowering.Result with { ExceptionError = replacement },
            },
        };
    }

    private static SemanticExpression Child(SemanticExpression expression, int index)
    {
        Assert.That(expression.Children.Length, Is.GreaterThan(index));
        return expression.Children[index];
    }

    private static IrDocument ReplaceOperation(IrDocument document, string name, OperationDescriptor replacement)
    {
        OperationDescriptor[] operations = document.Operations.ToArray();
        int operationIndex = Array.FindIndex(operations, operation => operation.Name == name);
        operations[operationIndex] = replacement;
        return document with { Operations = operations };
    }

    private static bool SameExpression(SemanticExpression left, SemanticExpression right) =>
        left.Kind == right.Kind && left.Text == right.Text && left.SymbolId == right.SymbolId &&
        left.TypeName == right.TypeName && left.Children.Length == right.Children.Length &&
        left.Children.Zip(right.Children).All(pair => SameExpression(pair.First, pair.Second));

    private static void AssertLeanCompiles(byte[] source)
    {
        string root = FindRepoRoot();
        string package = Path.Combine(root, "tools/Evm/Lean/ReceiptTerminalFoldExtractor");
        using TemporaryDirectory temporary = new("receipt-terminal-fold-lean-mutation");
        string path = Path.Combine(temporary.Path, "ReceiptTerminalFoldMutation.lean");
        File.WriteAllBytes(path, source);

        (int exitCode, string output) = RunLean(package, path);
        Assert.That(exitCode, Is.EqualTo(0), output);
    }

    private static void AssertIndependentGraphRejects(byte[] replacement)
    {
        string root = FindRepoRoot();
        string package = Path.Combine(root, "tools/Evm/Lean/ReceiptTerminalFoldExtractor");
        string leanSourceRoot = Path.GetFullPath(Path.Combine(package, ".."));
        using TemporaryDirectory temporary = new("receipt-terminal-fold-lean-replacement");
        File.Copy(Path.Combine(package, "lean-toolchain"), Path.Combine(temporary.Path, "lean-toolchain"));
        string moduleRoot = Path.Combine(temporary.Path, "ReceiptTerminalFoldExtractor");
        foreach (string relativePath in new[]
        {
            "Specification/ReceiptTerminalFold.lean",
            "Refinement/ReceiptTerminalFold.lean",
            "Vectors/ReceiptTerminalFoldVectors.lean",
        })
        {
            string destination = Path.Combine(moduleRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(package, relativePath), destination);
        }

        string generated = Path.Combine(moduleRoot, "Generated/ReceiptTerminalFoldKernel.lean");
        Directory.CreateDirectory(Path.GetDirectoryName(generated)!);
        File.Copy(Path.Combine(root, ReceiptTerminalFoldProfile.DefaultLeanRelativePath), generated, overwrite: true);

        string main = Path.Combine(temporary.Path, "ReplacementMain.lean");
        File.WriteAllText(main,
            "import ReceiptTerminalFoldExtractor.Vectors.ReceiptTerminalFoldVectors\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        string lakefile = Path.Combine(temporary.Path, "lakefile.toml");
        File.WriteAllText(lakefile,
            $"name = \"ReceiptTerminalFoldMutation\"\nversion = \"0.1.0\"\n\n" +
            "[[lean_lib]]\nname = \"Eip803x\"\n" +
            $"srcDir = \"{leanSourceRoot.Replace('\\', '/')}\"\n\n" +
            "[[lean_lib]]\nname = \"ReceiptTerminalFoldExtractor\"\nsrcDir = \".\"\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        (int baselineExitCode, string baselineOutput) = RunLean(temporary.Path, main);
        Assert.That(baselineExitCode, Is.EqualTo(0), baselineOutput);
        string lakeDirectory = Path.Combine(temporary.Path, ".lake");
        if (Directory.Exists(lakeDirectory))
        {
            Directory.Delete(lakeDirectory, recursive: true);
        }

        File.WriteAllBytes(generated, replacement);

        (int exitCode, string output) = RunLean(temporary.Path, main);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.Not.EqualTo(0), output);
            Assert.That(output, Does.Contain("error:"), output);
            Assert.That(output, Does.Contain(temporary.Path), output);
            Assert.That(output, Does.Match(
                @"(?i)(Refinement[/\\].*ReceiptTerminalFold\.lean|Vectors[/\\].*ReceiptTerminalFoldVectors\.lean)"), output);
            Assert.That(output, Does.Not.Contain("unknown module"), output);
        }
    }

    private static (int ExitCode, string Output) RunLean(string workingDirectory, string sourcePath)
    {
        string elanHome = "D:\\tmp\\formal-lean-toolchain\\home";
        string elanBin = Path.Combine(elanHome, "bin");
        string lake = Environment.GetEnvironmentVariable("LAKE") ??
            (File.Exists(Path.Combine(elanBin, "lake.exe")) ? Path.Combine(elanBin, "lake.exe") : "lake");
        ProcessStartInfo start = new(lake)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--wfail");
        start.ArgumentList.Add("build");
        start.ArgumentList.Add("ReceiptTerminalFoldExtractor.Vectors.ReceiptTerminalFoldVectors");
        if (Directory.Exists(elanBin))
        {
            start.Environment["ELAN_HOME"] = elanHome;
            string existingPath = start.Environment.TryGetValue("Path", out string? configuredPath)
                ? configuredPath ?? string.Empty
                : Environment.GetEnvironmentVariable("Path") ?? string.Empty;
            start.Environment["Path"] = elanBin + Path.PathSeparator + existingPath;
        }
        (int buildExit, string buildOutput) = Run(start);
        if (buildExit != 0) return (buildExit, buildOutput);
        start.ArgumentList.Clear();
        start.ArgumentList.Add("env");
        start.ArgumentList.Add("lean");
        start.ArgumentList.Add("-DwarningAsError=true");
        start.ArgumentList.Add(sourcePath);
        return Run(start);
    }

    private static (int ExitCode, string Output) Run(ProcessStartInfo start)
    {
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start lake for Lean mutation validation.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000))
        {
            process.Kill(entireProcessTree: true);
            throw new AssertionException("Lean mutation validation timed out.");
        }

        Task.WaitAll(standardOutput, standardError);
        string output = standardOutput.Result + standardError.Result;
        return (process.ExitCode, output);
    }

    private static IrDocument ReadCheckedIr() => ReceiptTerminalFoldProfile.DeserializeIr(File.ReadAllBytes(
        Path.Combine(FindRepoRoot(), ReceiptTerminalFoldProfile.DefaultOutputRelativePath,
            ReceiptTerminalFoldProfile.IrFileName)));

    private static JsonObject ParseCheckedIr() => JsonNode.Parse(File.ReadAllBytes(Path.Combine(
        FindRepoRoot(), ReceiptTerminalFoldProfile.DefaultOutputRelativePath,
        ReceiptTerminalFoldProfile.IrFileName)))!.AsObject();

    private static JsonObject ParseCheckedManifest() => JsonNode.Parse(File.ReadAllBytes(Path.Combine(
        FindRepoRoot(), ReceiptTerminalFoldProfile.DefaultOutputRelativePath,
        ReceiptTerminalFoldProfile.ManifestFileName)))!.AsObject();

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, ReceiptTerminalFoldProfile.TracerPath)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Nethermind repository root.");
    }

    private sealed class Fixture : IDisposable
    {
        internal Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(),
                "receipt-terminal-fold-fixtures", Guid.NewGuid().ToString("N"));
            Output = Path.Combine(Root, "generated");
            string productionRoot = FindRepoRoot();
            ReferenceRoot = productionRoot;

            foreach (string relativePath in ReceiptTerminalFoldProfile.SourceRelativePaths)
            {
                Copy(productionRoot, relativePath);
            }

            foreach (string relativePath in ReceiptTerminalFoldProfile.BindingSourceRelativePaths)
            {
                Copy(productionRoot, relativePath);
            }

            foreach (string relativePath in new[]
            {
                "tools/Evm/Lean/Eip803x/Generated/BlockReceiptGasAccountingKernel.lean",
                "tools/Evm/Lean/Eip803x/Refinement/BlockReceiptGasAccounting.lean",
                "tools/Evm/Lean/Extractor/Generated/BlockReceiptGasAccountingKernel.ir.json",
                "tools/Evm/Lean/Extractor/Generated/BlockReceiptGasAccountingKernel.source-manifest.json",
            })
            {
                Copy(productionRoot, relativePath);
            }
            foreach (SourceIdentity dependency in ReceiptTerminalFoldProfile.LeanDependencies)
            {
                if (!File.Exists(Path.Combine(Root, dependency.Path))) Copy(productionRoot, dependency.Path);
            }
        }

        internal string Root { get; }
        internal string Output { get; }
        internal string ReferenceRoot { get; }

        internal void Extract() => ReceiptTerminalFoldProfile.Extract(
            Root, Output, Path.Combine(Output, "ReceiptTerminalFoldKernel.lean"));

        internal ExtractionResult ExtractSemanticMutation() => ReceiptTerminalFoldProfile.ExtractForSemanticMutationTest(
            Root, Output, Path.Combine(Output, "ReceiptTerminalFoldKernel.lean"), ReferenceRoot);

        internal void Replace(string relativePath, string original, string replacement)
        {
            string path = Path.Combine(Root, relativePath);
            string source = File.ReadAllText(path);
            Assert.That(source, Does.Contain(original));
            Write(relativePath, source.Replace(original, replacement, StringComparison.Ordinal));
        }

        internal void FlipTrueArm(string relativePath, string condition, string marker)
        {
            SyntaxNode root = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(Root, relativePath))).GetRoot();
            IfStatementSyntax conditional = root.DescendantNodes().OfType<IfStatementSyntax>().Single(candidate =>
                candidate.Condition.NormalizeWhitespace().ToFullString() == condition &&
                candidate.Statement.ToString().Contains(marker, StringComparison.Ordinal));
            Assert.That(conditional.Else, Is.Null);
            IfStatementSyntax changed = conditional.WithStatement(SyntaxFactory.Block())
                .WithElse(SyntaxFactory.ElseClause(conditional.Statement));
            Write(relativePath, root.ReplaceNode(conditional, changed).NormalizeWhitespace().ToFullString());
        }

        internal void InsertBefore(string relativePath, string methodName, string marker, string statement)
        {
            SyntaxNode root = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(Root, relativePath))).GetRoot();
            MethodDeclarationSyntax method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(candidate => candidate.Identifier.ValueText == methodName);
            StatementSyntax inserted = SyntaxFactory.ParseStatement(statement);
            MethodDeclarationSyntax changed;
            if (method.ExpressionBody is not null)
            {
                changed = method.WithBody(SyntaxFactory.Block(inserted, SyntaxFactory.ReturnStatement(method.ExpressionBody.Expression)))
                    .WithExpressionBody(null).WithSemicolonToken(default);
            }
            else
            {
                StatementSyntax target = method.DescendantNodes().OfType<StatementSyntax>()
                    .Single(candidate => candidate.ToString().StartsWith(marker, StringComparison.Ordinal));
                Assert.That(target.Parent, Is.TypeOf<BlockSyntax>());
                changed = method.InsertNodesBefore(target, [inserted]);
            }
            Write(relativePath, root.ReplaceNode(method, changed).NormalizeWhitespace().ToFullString());
        }

        internal void ReplaceStatement(string relativePath, string methodName, string marker, string statement)
        {
            SyntaxNode root = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(Root, relativePath))).GetRoot();
            MethodDeclarationSyntax method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(candidate => candidate.Identifier.ValueText == methodName);
            StatementSyntax target = method.DescendantNodes().OfType<StatementSyntax>()
                .Single(candidate => candidate.ToString().StartsWith(marker, StringComparison.Ordinal));
            Write(relativePath, root.ReplaceNode(target, SyntaxFactory.ParseStatement(statement)).NormalizeWhitespace().ToFullString());
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private void Copy(string productionRoot, string relativePath)
        {
            string source = Path.Combine(productionRoot, relativePath);
            string destination = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }

        private void Write(string relativePath, string source)
        {
            string path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory(string prefix)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                prefix, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
