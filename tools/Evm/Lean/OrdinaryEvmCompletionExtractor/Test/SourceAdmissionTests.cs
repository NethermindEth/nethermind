// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.OrdinaryEvmCompletionExtractor.Test;

[TestFixture, NonParallelizable]
public class SourceAdmissionTests
{
    private string _root = null!;
    private string _processor = null!;

    [OneTimeSetUp]
    public void Baseline()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, SourceAdmission.ProcessorPath))) directory = directory.Parent;
        _root = directory?.FullName ?? throw new InvalidOperationException("Cannot find repository root.");
        _processor = File.ReadAllText(Path.Combine(_root, SourceAdmission.ProcessorPath));
        SourceModel model = SourceAdmission.Read(_root);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(model.Compiler.Sources, Has.Length.EqualTo(157));
            Assert.That(model.Compiler.References, Has.Length.EqualTo(226));
            Assert.That(model.Compiler.Members, Has.Length.EqualTo(10));
            Assert.That(model.Plan.Expressions, Has.Length.EqualTo(43));
        }
    }

    public sealed record Mutation(string Name, string Before, string After, string Diagnostic);

    [Test]
    public void Artifacts_render_deterministically_in_memory_without_a_specification_evaluator_alias()
    {
        ArtifactDocument document = Artifacts.Audit(_root);
        (byte[] ir, byte[] lean, byte[] manifest) = Artifacts.Render(_root, document);
        (byte[] secondIr, byte[] secondLean, byte[] secondManifest) = Artifacts.Render(_root, document);
        string definition = System.Text.Encoding.UTF8.GetString(lean);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(secondIr, Is.EqualTo(ir));
            Assert.That(secondLean, Is.EqualTo(lean));
            Assert.That(secondManifest, Is.EqualTo(manifest));
            Assert.That(definition, Does.Not.Contain("@@"));
            Assert.That(definition, Does.Not.Contain("Specification.OrdinaryEvmCompletion.evaluate"));
            Assert.That(definition, Does.Contain("def evaluate (i : Input) : Result"));
            Assert.That(document.AcceptanceState, Is.EqualTo("accepted-conditional-source-audited-refinement"));
        }
    }

    [Test]
    public void Baseline_neutral_lowering_source_drift_changes_every_published_identity()
    {
        ArtifactDocument document = Artifacts.Audit(_root);
        string loweringPath = SourceAdmission.PackagePath + "/OperationLowering.cs";
        string original = File.ReadAllText(Path.Combine(_root, loweringPath));
        string alteredHash = CompilerReferences.Hash(System.Text.Encoding.UTF8.GetBytes(original + "\n// Baseline-neutral drift.\n"));
        SourceIdentity[] altered = document.Dependencies.Select(identity => identity.Path == loweringPath
            ? identity with { Sha256 = alteredHash } : identity).ToArray();
        Assert.That(altered.Single(identity => identity.Path == loweringPath).Sha256, Is.EqualTo(alteredHash));
        ArtifactDocument changed = document with { Dependencies = altered, SourceClosure = Artifacts.ComputeSourceClosure(altered, document.Source) };
        (byte[] ir, byte[] lean, byte[] manifest) = Artifacts.Render(_root, document);
        (byte[] changedIr, byte[] changedLean, byte[] changedManifest) = Artifacts.Render(_root, changed);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changed.SourceClosure, Is.Not.EqualTo(document.SourceClosure));
            Assert.That(changedIr, Is.Not.EqualTo(ir));
            Assert.That(changedLean, Is.Not.EqualTo(lean));
            Assert.That(changedManifest, Is.Not.EqualTo(manifest));
        }
    }

    private static IEnumerable<TestCaseData> Mutations()
    {
        Mutation[] mutations =
        [
            new("frame_copy_false_guard", "gasAvailable = state.Gas;", "if (false) { gasAvailable = state.Gas; }", "branch.direct.vm.gas-copy"),
            new("frame_copy_dynamic_guard", "gasAvailable = state.Gas;", "if (tracer.IsTracingAccess) { gasAvailable = state.Gas; }", "branch.direct.vm.gas-copy"),
            new("state_counter_false_guard", "_blockCumulativeStateGas += spentGas.BlockStateGas;", "if (false) { _blockCumulativeStateGas += spentGas.BlockStateGas; }", "branch.then.processor.state"),
            new("execution_counter_false_guard", "_blockCumulativeExecutionGas += spentGas.EffectiveBlockGas;", "if (false) { _blockCumulativeExecutionGas += spentGas.EffectiveBlockGas; }", "branch.then.processor.execution"),
            new("receipt_success_false_guard", "tracer.MarkAsSuccess(executingAccount, spentGas, substate.Output.AsReadOnlyArray(), logs, stateRoot);", "if (false) { tracer.MarkAsSuccess(executingAccount, spentGas, substate.Output.AsReadOnlyArray(), logs, stateRoot); }", "branch.else.receipt.success"),
            new("gas_from_intrinsic_instead_of_frame", "gasAvailable = state.Gas;", "gasAvailable = gas.Standard;", "site.vm.gas-copy"),
            new("beneficiary_redirected_to_sender", "WorldState.AddToBalanceAndCreateIfNotExists(header.GasBeneficiary!, fees, spec)", "WorldState.AddToBalanceAndCreateIfNotExists(tx.SenderAddress!, fees, spec)", "site.fees.beneficiary-credit"),
            new("collector_redirected_to_beneficiary", "WorldState.AddToBalanceAndCreateIfNotExists(spec.FeeCollector, collectedFees, spec)", "WorldState.AddToBalanceAndCreateIfNotExists(header.GasBeneficiary!, collectedFees, spec)", "site.fees.collector-credit"),
            new("collector_wrong_amount", "WorldState.AddToBalanceAndCreateIfNotExists(spec.FeeCollector, collectedFees, spec)", "WorldState.AddToBalanceAndCreateIfNotExists(spec.FeeCollector, fees, spec)", "site.fees.collector-credit"),
            new("blob_amount_overwrite", "collectedFees += blobBaseFee;", "collectedFees = blobBaseFee;", "site.fees.blob-add"),
            new("refund_floor_standard_swap", "gas.FloorGas, gas.Standard, postIntrinsicStateReservoir, topLevelCreateStateGasCharged)", "gas.Standard, gas.FloorGas, postIntrinsicStateReservoir, topLevelCreateStateGasCharged)", "site.refund.input"),
            new("refund_auth_count_zero", "(ulong)delegationRefunds, gas.FloorGas", "0, gas.FloorGas", "site.refund.input"),
            new("failure_and", "if (substate.ShouldRevert || substate.IsError)", "if (substate.ShouldRevert && substate.IsError)", "site.vm.failed"),
            new("base_price_max", "UInt256.Min(header.BaseFeePerGas, effectiveGasPrice)", "UInt256.Max(header.BaseFeePerGas, effectiveGasPrice)", "site.fees.base"),
            new("fee_uses_block_gas", "in substate, spentGas.SpentGas, premiumPerGas", "in substate, spentGas.EffectiveBlockGas, premiumPerGas", "site.processor.fees"),
            new("final_recipient_sender", "in senderReservedGasPayment, env.ExecutingAccount, in substate", "in senderReservedGasPayment, tx.SenderAddress!, in substate", "site.finalize.route"),
            new("receipt_failure_iserror", "if (statusCode == StatusCode.Failure)", "if (substate.IsError)", "site.finalize.failed"),
            new("conditional_refund", "gasConsumed = Refund(tx, header, spec, opts, in substate, gasAvailable, VirtualMachine.TxExecutionContext.GasPrice, (ulong)delegationRefunds, gas.FloorGas, gas.Standard, postIntrinsicStateReservoir, topLevelCreateStateGasCharged);", "if (tracer.IsTracingAccess) { gasConsumed = Refund(tx, header, spec, opts, in substate, gasAvailable, VirtualMachine.TxExecutionContext.GasPrice, (ulong)delegationRefunds, gas.FloorGas, gas.Standard, postIntrinsicStateReservoir, topLevelCreateStateGasCharged); }", "branch.direct.refund.input"),
            new("local_frame_receiver", "gasAvailable = state.Gas;", "VmState<TGasPolicy> other = state; gasAvailable = other.Gas;", "site.vm.gas-copy"),
            new("conditional_payfees", "        protected virtual void PayFees(", "        [System.Diagnostics.Conditional(\"COMPLETION_ERASE\")]\n        protected virtual void PayFees(", "callable.PayFees"),
            new("aliased_conditional_payfees", "        protected virtual void PayFees(", "        [global::System.Diagnostics.ConditionalAttribute(\"COMPLETION_ERASE\")]\n        protected virtual void PayFees(", "callable.PayFees"),
            new("checked_premium_product", "UInt256 fees = premiumPerGas * spentGas;", "UInt256 fees = checked(premiumPerGas * spentGas);", "role.fees.premium"),
        ];
        foreach (Mutation mutation in mutations) yield return new TestCaseData(mutation).SetName(mutation.Name);
    }

    [TestCaseSource(nameof(Mutations))]
    public void Compile_valid_mutation_is_rejected_by_named_semantic_guard(Mutation mutation)
    {
        Assert.That(_processor.Split(mutation.Before, StringSplitOptions.None), Has.Length.EqualTo(2), "Mutation target must be unique.");
        string mutant = _processor.Replace(mutation.Before, mutation.After, StringComparison.Ordinal);
        Dictionary<string, string> overrides = new(StringComparer.Ordinal) { [SourceAdmission.ProcessorPath] = mutant };
        SourceAdmission.RequireCompilationForTest(_root, overrides);
        AdmissionException? exception = Assert.Throws<AdmissionException>(() => SourceAdmission.ReadForTest(_root, overrides));
        Assert.That(exception!.Message, Is.EqualTo(mutation.Diagnostic));
    }

    [Test]
    public void By_value_refund_overload_cannot_replace_accepted_in_parameter_leaf()
    {
        const string anchor = "        protected virtual GasConsumed Refund(";
        const string overload = "        private GasConsumed Refund(Transaction tx, BlockHeader header, IReleaseSpec spec, ExecutionOptions opts, in TransactionSubstate substate, TGasPolicy unspentGas, in UInt256 gasPrice, ulong codeInsertRefunds, in TGasPolicy floorGas, in TGasPolicy intrinsicGasStandard, long postIntrinsicStateReservoir, bool topLevelCreateStateGasCharged = false) => default;\n";
        Dictionary<string, string> overrides = new(StringComparer.Ordinal)
        {
            [SourceAdmission.ProcessorPath] = _processor.Replace(anchor, overload + anchor, StringComparison.Ordinal),
        };
        SourceAdmission.RequireCompilationForTest(_root, overrides);
        AdmissionException? exception = Assert.Throws<AdmissionException>(() => SourceAdmission.ReadForTest(_root, overrides));
        Assert.That(exception!.Message, Is.EqualTo("signature.refund.input"));
    }

    [TestCase("caller-flags", "role.caller.restore")]
    [TestCase("rollback-polarity", "rollback.snapshot-before-ripemd")]
    [TestCase("copy-before-metrics", "order.vm.metrics.vm.flush")]
    [TestCase("same-typed-parameter-roles", "binding.method-signatures")]
    [TestCase("refund-before-dispose", "cleanup.frame-before-refund")]
    public void Structural_mutants_compile_and_fail_exact_guard(string kind, string diagnostic)
    {
        SyntaxNode root = CSharpSyntaxTree.ParseText(_processor).GetRoot();
        SyntaxNode changed;
        if (kind == "caller-flags")
        {
            MethodDeclarationSyntax method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(node => node.Identifier.Text == "Execute" && node.ParameterList.Parameters.Count == 6);
            VariableDeclaratorSyntax restore = method.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single(node => node.Identifier.Text == "restore");
            VariableDeclaratorSyntax commit = method.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single(node => node.Identifier.Text == "commit");
            changed = root.ReplaceNodes([restore, commit], (original, _) => original.WithInitializer(original == restore ? commit.Initializer : restore.Initializer));
        }
        else if (kind == "rollback-polarity")
        {
            IfStatementSyntax branch = root.DescendantNodes().OfType<IfStatementSyntax>().Single(node => node.Condition.ToString() == "substate.ShouldRevert || substate.IsError");
            changed = root.ReplaceNode(branch, branch.WithStatement(branch.Else!.Statement).WithElse(branch.Else.WithStatement(branch.Statement)));
        }
        else if (kind == "copy-before-metrics")
        {
            ExpressionStatementSyntax copy = root.DescendantNodes().OfType<ExpressionStatementSyntax>().Single(node => node.Expression.ToString() == "gasAvailable = state.Gas");
            ExpressionStatementSyntax metrics = root.DescendantNodes().OfType<ExpressionStatementSyntax>().Single(node => node.Expression.ToString() == "Metrics.IncrementOpCodes(VirtualMachine.OpCodeCount)");
            changed = root.ReplaceNodes([copy, metrics], (original, _) => original == copy ? metrics : copy);
        }
        else if (kind == "refund-before-dispose")
        {
            MethodDeclarationSyntax method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(node => node.Identifier.Text == "ExecuteEvmCall");
            UsingStatementSyntax frame = method.Body!.Statements.OfType<UsingStatementSyntax>().Single();
            ExpressionStatementSyntax refund = method.Body.Statements.OfType<ExpressionStatementSyntax>().Single(node => node.Expression.ToString().StartsWith("gasConsumed = Refund(", StringComparison.Ordinal));
            UsingStatementSyntax reordered = frame.WithStatement(((BlockSyntax)frame.Statement).AddStatements(refund));
            SyntaxList<StatementSyntax> statements = SyntaxFactory.List(method.Body.Statements.Where(statement => statement != refund).Select(statement => statement == frame ? reordered : statement));
            changed = root.ReplaceNode(method, method.WithBody(method.Body.WithStatements(statements)));
        }
        else
        {
            MethodDeclarationSyntax method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(node => node.Identifier.Text == "PayFees");
            ParameterSyntax premium = method.ParameterList.Parameters.Single(node => node.Identifier.Text == "premiumPerGas");
            ParameterSyntax effective = method.ParameterList.Parameters.Single(node => node.Identifier.Text == "effectiveGasPrice");
            changed = root.ReplaceNodes([premium, effective], (original, _) => original.WithIdentifier(original == premium ? effective.Identifier : premium.Identifier));
        }
        Dictionary<string, string> overrides = new(StringComparer.Ordinal) { [SourceAdmission.ProcessorPath] = changed.ToFullString() };
        SourceAdmission.RequireCompilationForTest(_root, overrides);
        AdmissionException? exception = Assert.Throws<AdmissionException>(() => SourceAdmission.ReadForTest(_root, overrides));
        Assert.That(exception!.Message, Is.EqualTo(diagnostic));
    }
}
