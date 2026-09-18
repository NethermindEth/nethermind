// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Evm.Lean.OrdinaryTransactionRefundAdapterExtractor.Test;

[TestFixture]
public class AdmissionTests
{
    private const string Processor = "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs";
    private const string Policy = "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs";
    private const string Contract = "src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs";
    private const string SyntaxDiagnostic = "Unadmitted complete source/support syntax closure:";
    private const string CallableDiagnostic = "Owned method is not an ordinary synchronous source body:";
    private const string ConditionalDiagnostic = "Owned method has direct or inherited ConditionalAttribute:";
    private const string LexicalDiagnostic = "Owned method contains an extra lexical callable or iterator:";
    private string _root = null!;

    internal static string Root()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, SourceAdmission.PinsPath))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Cannot find the repository root.");
    }

    [OneTimeSetUp]
    public void Require_live_compiled_baseline()
    {
        _root = Root();
        Assert.DoesNotThrow(() => SourceAdmission.Read(_root));
    }

    [Test]
    public void Baseline_retains_complete_compiler_typed_expression_and_halt_order_evidence()
    {
        SourceAdmission.AdmissionResult result = SourceAdmission.Read(_root);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Admission.Sources, Has.Length.EqualTo(157));
            Assert.That(result.Admission.References, Has.Length.EqualTo(226));
            Assert.That(result.Admission.Members, Has.Length.EqualTo(26));
            Assert.That(result.Plan.Expressions, Has.Length.EqualTo(50));
            Assert.That(result.Plan.HaltStages, Is.EqualTo(new[] { HaltStage.RefundState, HaltStage.ResetState, HaltStage.ClearExecution }));
            Assert.That(result.Admission.Sources.Count(source => source.Role == "semantic"), Is.EqualTo(11));
            Assert.That(result.Admission.Sources.Count(source => source.Role == "compiler-support"), Is.EqualTo(132));
        }
    }

    [TestCaseSource(nameof(Mutations))]
    public void Compile_valid_mutations_fail_named_semantic_admission(string[] edits, string diagnostic)
    {
        Dictionary<string, string> overrides = new(StringComparer.Ordinal);
        for (int index = 0; index < edits.Length; index += 3)
        {
            string path = edits[index];
            string source = overrides.TryGetValue(path, out string? current) ? current :
                File.ReadAllText(Path.Combine(_root, path)).Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.That(source, Does.Contain(edits[index + 1]), "Missing mutation anchor: " + path);
            overrides[path] = source.Replace(edits[index + 1], edits[index + 2], StringComparison.Ordinal);
        }
        Assert.DoesNotThrow(() => SourceAdmission.RequireCompilationForTest(_root, overrides),
            "A compiler diagnostic is not source-admission rejection evidence.");
        Assert.That(() => SourceAdmission.ReadForTest(_root, overrides),
            Throws.TypeOf<AdmissionException>().With.Message.StartsWith(diagnostic));
    }

    private static IEnumerable<TestCaseData> Mutations()
    {
        yield return Case("halt_clear_before_reset", SyntaxDiagnostic, Processor,
            "TGasPolicy.ResetForHalt(ref gas, postIntrinsicStateReservoir, postHaltIntrinsicStateGas);",
            "TGasPolicy.ClearExecutionGas(ref gas);\n            TGasPolicy.ResetForHalt(ref gas, postIntrinsicStateReservoir, postHaltIntrinsicStateGas);");
        yield return Case("halt_clear_erased", SyntaxDiagnostic, Processor,
            "TGasPolicy.ClearExecutionGas(ref gas);", "TGasPolicy.GetRemainingGas(in gas);");
        yield return Case("halt_floor_role_swap", SyntaxDiagnostic, Processor,
            "TGasPolicy.ResetForHalt(ref gas, postIntrinsicStateReservoir, postHaltIntrinsicStateGas);",
            "TGasPolicy.ResetForHalt(ref gas, postHaltIntrinsicStateGas, postIntrinsicStateReservoir);");
        yield return Case("reset_same_type_parameter_swap", SyntaxDiagnostic, Policy,
            "ResetForHalt(ref EthereumGasPolicy gas, long initialStateReservoir, long initialStateGasUsed)",
            "ResetForHalt(ref EthereumGasPolicy gas, long initialStateGasUsed, long initialStateReservoir)");
        yield return Case("reset_erases_refunded_spill", SyntaxDiagnostic, Policy,
            "gas.StateReservoir = initialStateReservoir;", "gas.StateGasSpillRefunded = 0;\n        gas.StateReservoir = initialStateReservoir;");
        yield return Case("clear_erases_reservoir", SyntaxDiagnostic, Policy,
            "ClearExecutionGas(ref EthereumGasPolicy gas) => gas.Value = 0;",
            "ClearExecutionGas(ref EthereumGasPolicy gas) { gas.Value = 0; gas.StateReservoir = 0; }");
        yield return Case("untracked_create_becomes_tracked", SyntaxDiagnostic, Processor,
            "stateGasFloor, trackSpillRefund: false);", "stateGasFloor, trackSpillRefund: true);");
        yield return Case("create_guard_ignores_revert", SyntaxDiagnostic, Processor,
            "spec.IsEip8037Enabled && substate.ShouldRevert && topLevelCreateStateGasCharged",
            "spec.IsEip8037Enabled && topLevelCreateStateGasCharged");
        yield return Case("error_falls_through_normal_settlement", SyntaxDiagnostic, Processor,
            "if (substate.IsError && spec.IsEip8037Enabled)", "if (false && substate.IsError && spec.IsEip8037Enabled)");
        yield return Case("settlement_same_type_operands_swapped", SyntaxDiagnostic, Processor,
            "tx.GasLimit,\n                preRefundGas,", "preRefundGas,\n                tx.GasLimit,");
        yield return Case("settlement_result_fields_swapped", SyntaxDiagnostic, Processor,
            "settlement.SpentGas,\n                settlement.OperationGas,", "settlement.OperationGas,\n                settlement.SpentGas,");
        yield return Case("halt_result_uses_normal_projection", SyntaxDiagnostic, Processor,
            "new GasConsumed(spentGas, spentGas, blockGas, (ulong)blockStateGas, spentGas, gasRefund)",
            "new GasConsumed(spentGas, blockGas, blockGas, (ulong)blockStateGas, spentGas, gasRefund)");
        yield return Case("payment_no_longer_uses_gas_price", SyntaxDiagnostic, Processor,
            "PayRefund(tx, (tx.GasLimit - settlement.SpentGas) * gasPrice, spec);",
            "PayRefund(tx, (tx.GasLimit - settlement.SpentGas) * UInt256.One, spec);");
        yield return Case("payment_wrong_receiver", SyntaxDiagnostic, Processor,
            "WorldState.AddToBalance(tx.SenderAddress!, refundAmount, spec);", "WorldState.AddToBalance(tx.To!, refundAmount, spec);");
        yield return Case("payment_zero_credit_unconditionally", SyntaxDiagnostic, Processor,
            "if (!refundAmount.IsZero)", "if (true || !refundAmount.IsZero)");
        yield return Case("pre_refund_uses_saturating_int64", SyntaxDiagnostic, Contract,
            "Int128 preRefundGas = (Int128)txGasLimit - remainingGas - stateReservoir;",
            "Int128 preRefundGas = (long)txGasLimit - (long)remainingGas - stateReservoir;");
        yield return Case("pre_refund_excludes_upper_bound", SyntaxDiagnostic, Contract,
            "preRefundGas <= ulong.MaxValue", "preRefundGas < ulong.MaxValue");
        yield return Case("pre_refund_wrong_fallback", SyntaxDiagnostic, Contract,
            "return inRange ? (ulong)preRefundGas : txGasLimit;", "return inRange ? (ulong)preRefundGas : 0;");
        yield return Case("legacy_code_refund_retained_in_8037", SyntaxDiagnostic, Policy,
            "if (spec.IsEip8037Enabled) return 0;", "if (false && spec.IsEip8037Enabled) return 0;");
        yield return Case("conditional_payment", ConditionalDiagnostic, Processor,
            "protected virtual void PayRefund(Transaction tx, UInt256 refundAmount, IReleaseSpec spec)",
            "[System.Diagnostics.Conditional(\"REFUND_DISABLED\")]\n        protected virtual void PayRefund(Transaction tx, UInt256 refundAmount, IReleaseSpec spec)");
        yield return Case("conditional_state_refill", ConditionalDiagnostic, Processor,
            "static void RefundRevertedExecutionStateGas(IReleaseSpec spec, long stateGasFloor, ref TGasPolicy gas)",
            "[System.Diagnostics.Conditional(\"REFUND_DISABLED\")]\n        static void RefundRevertedExecutionStateGas(IReleaseSpec spec, long stateGasFloor, ref TGasPolicy gas)");
        yield return Case("async_payment", CallableDiagnostic, Processor,
            "protected virtual void PayRefund(Transaction tx, UInt256 refundAmount, IReleaseSpec spec)",
            "protected virtual async void PayRefund(Transaction tx, UInt256 refundAmount, IReleaseSpec spec)");
        yield return Case("iterator_payment", LexicalDiagnostic, Processor,
            "protected virtual void PayRefund(Transaction tx, UInt256 refundAmount, IReleaseSpec spec)",
            "protected virtual System.Collections.Generic.IEnumerable<int> PayRefund(Transaction tx, UInt256 refundAmount, IReleaseSpec spec)",
            Processor, "if (!refundAmount.IsZero)", "yield return 0;\n            if (!refundAmount.IsZero)",
            "src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionProcessor.cs",
            "protected override void PayRefund(Transaction tx, UInt256 refundAmount, IReleaseSpec spec) { }",
            "protected override System.Collections.Generic.IEnumerable<int> PayRefund(Transaction tx, UInt256 refundAmount, IReleaseSpec spec) { yield break; }");
        yield return Case("local_payment_shadow", LexicalDiagnostic, Processor,
            "TGasPolicy gasAfterExecution = unspentGas;",
            "void PayRefund(Transaction candidate, UInt256 amount, IReleaseSpec rules) { }\n            TGasPolicy gasAfterExecution = unspentGas;");
        yield return Case("local_halt_shadow", LexicalDiagnostic, Processor,
            "TGasPolicy gasAfterExecution = unspentGas;",
            "GasConsumed CompleteEip8037Halt(Transaction candidate, IReleaseSpec rules, ExecutionOptions modes, ref TGasPolicy state, in UInt256 price, in TGasPolicy standard, ulong floor, long reservoir, ulong refund = 0) => 0;\n            TGasPolicy gasAfterExecution = unspentGas;");
        yield return Case("delegate_payment", LexicalDiagnostic, Processor,
            "PayRefund(tx, (tx.GasLimit - settlement.SpentGas) * gasPrice, spec);",
            "((Action)(() => PayRefund(tx, UInt256.One, spec)))();");
        yield return Case("metadata_callback_reentry", LexicalDiagnostic, Processor,
            "TGasPolicy gasAfterExecution = unspentGas;",
            "Array.Sort(new[] { 1, 2 }, (left, right) => { PayRefund(tx, UInt256.One, spec); return 0; });\n            TGasPolicy gasAfterExecution = unspentGas;");
        yield return Case("preprocessor_release_alternate", "Semantic source contains a directive or disabled text:", Contract,
            "return inRange ? (ulong)preRefundGas : txGasLimit;",
            "#if RELEASE\n        return 0;\n#else\n        return inRange ? (ulong)preRefundGas : txGasLimit;\n#endif");
    }

    private static TestCaseData Case(string name, string diagnostic, params string[] edits) =>
        new TestCaseData(edits, diagnostic).SetName("refund_admission_" + name);
}
