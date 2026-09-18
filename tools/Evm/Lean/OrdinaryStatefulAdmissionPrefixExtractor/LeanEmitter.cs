// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Globalization;
using System.Security.Cryptography;

namespace Nethermind.Evm.Lean.OrdinaryStatefulAdmissionPrefixExtractor;

/// <summary>Emits the theorem-free, source-bound Lean transition for the admitted prefix.</summary>
/// <remarks>
/// The emitted program intentionally does not contain a proof. It is a deterministic transcription
/// of the IR shape after this extractor has rejected any unrecognised source route or branch order.
/// The separately handwritten model and refinement live outside this generated file.
/// </remarks>
internal static class LeanEmitter
{
    private static readonly string[] RequiredStageIds =
    [
        "validateStatic",
        "calculateEffectiveGasPrice",
        "updateMetrics",
        "recoverSenderIfNeeded",
        "validateSender",
        "buyGas",
        "incrementNonce",
    ];

    private static readonly string[] RequiredBranchIds =
    [
        "validateStaticReturn",
        "recoverSenderCreatesAccount",
        "recoverSenderThrows",
        "validateSenderContract",
        "buyGasPremiumBelowBaseFee",
        "buyGasReservedPaymentOverflow",
        "buyGasMaximumFeeOverflow",
        "buyGasValueOverflow",
        "buyGasBlobMaximumFeeOverflow",
        "buyGasBlobFeeCalculationOverflow",
        "buyGasBlobFeeCapBelowBaseFee",
        "buyGasBlobPaymentOverflow",
        "buyGasInsufficientBalanceWarmup",
        "buyGasInsufficientBalanceReturn",
        "buyGasDebit",
        "incrementNonceMismatch",
        "incrementNonceSet",
        "combinedAdmissionFailureRestore",
        "continueAfterAdmissionPrefix",
    ];

    private static readonly BranchTerminalKind[] RequiredBranchTerminalKinds =
    [
        BranchTerminalKind.Return,
        BranchTerminalKind.Continue,
        BranchTerminalKind.Throw,
        BranchTerminalKind.Return,
        BranchTerminalKind.Return,
        BranchTerminalKind.Return,
        BranchTerminalKind.Return,
        BranchTerminalKind.Return,
        BranchTerminalKind.Return,
        BranchTerminalKind.Return,
        BranchTerminalKind.Return,
        BranchTerminalKind.Return,
        BranchTerminalKind.Continue,
        BranchTerminalKind.Return,
        BranchTerminalKind.Continue,
        BranchTerminalKind.Return,
        BranchTerminalKind.Continue,
        BranchTerminalKind.Reset,
        BranchTerminalKind.Continue,
    ];

    internal static byte[] Emit(IrDocument document, string irSha256)
    {
        ValidateSourceLoweredShape(document);
        WidthShape uint64 = RequireWidth(document, "uint64", NumericWidth.UInt64, 64);
        WidthShape uint256 = RequireWidth(document, "uint256", NumericWidth.UInt256, 256);
        WidthShape options = RequireWidth(document, "executionOptions", NumericWidth.Int32, 32);
        SemanticOperation normalizeUInt64 = RequireOperation(document, "normalizeUInt64", SemanticFormula.NormalizeUnsigned,
            [NumericWidth.UInt64], NumericWidth.UInt64, [SemanticEffect.NormalizeInput]);
        SemanticOperation normalizeUInt256 = RequireOperation(document, "normalizeUInt256", SemanticFormula.NormalizeUnsigned,
            [NumericWidth.UInt256], NumericWidth.UInt256, [SemanticEffect.NormalizeInput]);
        SemanticOperation checkedAdd = RequireOperation(document, "checkedAdd256", SemanticFormula.CheckedAdd,
            [NumericWidth.UInt256, NumericWidth.UInt256], NumericWidth.UInt256, [SemanticEffect.PreserveWrappedOutValue]);
        SemanticOperation checkedMultiply = RequireOperation(document, "checkedMultiply256", SemanticFormula.CheckedMultiply,
            [NumericWidth.UInt256, NumericWidth.UInt256], NumericWidth.UInt256, [SemanticEffect.PreserveWrappedOutValue]);
        SemanticOperation effectivePrice = RequireOperation(document, "effectiveGasPrice", SemanticFormula.EffectiveGasPrice,
            [NumericWidth.UInt256, NumericWidth.UInt256], NumericWidth.UInt256, []);
        SemanticOperation premium = RequireOperation(document, "premiumPerGas", SemanticFormula.PremiumPerGas,
            [NumericWidth.UInt256, NumericWidth.UInt256], NumericWidth.UInt256, []);
        SemanticOperation metrics = RequireMetricsOperation(document);
        SemanticOperation recoveryDecision = RequireOperation(document, "recoveryDecision", SemanticFormula.RecoveryDecision,
            [NumericWidth.Int32, NumericWidth.UInt256], NumericWidth.UInt256, []);
        SemanticOperation recoveryApplication = RequireOperation(document, "recoveryApplication", SemanticFormula.RecoveryApplication,
            [NumericWidth.Int32, NumericWidth.UInt256], NumericWidth.UInt256,
            [SemanticEffect.AppendRecoveryLog, SemanticEffect.ReplaceTransactionSender,
             SemanticEffect.CreateZeroAccount, SemanticEffect.PreserveThrowState]);
        SemanticOperation feeReservation = RequireOperation(document, "feeReservation", SemanticFormula.FeeReservation,
            [NumericWidth.UInt64, NumericWidth.UInt256], NumericWidth.UInt256,
            [SemanticEffect.PreserveWrappedOutValue, SemanticEffect.DebitEffectiveSender]);
        SemanticOperation nonceAdvance = RequireOperation(document, "nonceAdvance", SemanticFormula.NonceAdvance,
            [NumericWidth.UInt64], NumericWidth.UInt64,
            [SemanticEffect.AppendValidationLog, SemanticEffect.SetEffectiveSenderNonce]);
        SemanticOperation combinedReset = RequireOperation(document, "combinedFailureReset", SemanticFormula.CombinedFailureReset,
            [NumericWidth.Int32], NumericWidth.Int32, [SemanticEffect.ResetJournalAfterCombinedFailure]);
        SemanticOperation admissionPrefix = RequireOperation(document, "admissionPrefix", SemanticFormula.AdmissionPrefix,
            [], NumericWidth.UInt256, [SemanticEffect.ContinueAfterPrefix]);
        SemanticOperation gasPerBlobConstant = RequireOperation(document, "gasPerBlobConstant", SemanticFormula.SourceConstant,
            [], NumericWidth.UInt64, [SemanticEffect.NormalizeInput]);
        SemanticOperation blobGas = RequireOperation(document, "blobGas", SemanticFormula.BlobGas,
            [NumericWidth.UInt64], NumericWidth.UInt64, [SemanticEffect.NormalizeInput]);
        BranchShape staticFailure = RequireBranch(document, "validateStaticReturn");
        BranchShape recoveryCreate = RequireBranch(document, "recoverSenderCreatesAccount");
        BranchShape recoveryMissingSender = RequireBranch(document, "recoverSenderThrows");
        BranchShape invalidContractSender = RequireBranch(document, "validateSenderContract");
        BranchShape premiumBelowBaseFee = RequireBranch(document, "buyGasPremiumBelowBaseFee");
        BranchShape reservedPaymentOverflow = RequireBranch(document, "buyGasReservedPaymentOverflow");
        BranchShape maximumFeeOverflow = RequireBranch(document, "buyGasMaximumFeeOverflow");
        BranchShape valueOverflow = RequireBranch(document, "buyGasValueOverflow");
        BranchShape blobMaximumFeeOverflow = RequireBranch(document, "buyGasBlobMaximumFeeOverflow");
        BranchShape blobFeeCalculationFailure = RequireBranch(document, "buyGasBlobFeeCalculationOverflow");
        BranchShape blobFeeCapBelowBaseFee = RequireBranch(document, "buyGasBlobFeeCapBelowBaseFee");
        BranchShape blobPaymentOverflow = RequireBranch(document, "buyGasBlobPaymentOverflow");
        BranchShape warmupInsufficientBalance = RequireBranch(document, "buyGasInsufficientBalanceWarmup");
        BranchShape insufficientBalance = RequireBranch(document, "buyGasInsufficientBalanceReturn");
        BranchShape nonZeroReservedPayment = RequireBranch(document, "buyGasDebit");
        BranchShape nonceMismatch = RequireBranch(document, "incrementNonceMismatch");
        BranchShape setNonce = RequireBranch(document, "incrementNonceSet");
        BranchShape combinedResetBranch = RequireBranch(document, "combinedAdmissionFailureRestore");
        BranchShape continuationBranch = RequireBranch(document, "continueAfterAdmissionPrefix");
        string inputAdapterCoherence = InputAdapterCoherenceBody(document);

        string source = LeanSource
            .Replace("__IR_SHA256__", irSha256, StringComparison.Ordinal)
            .Replace("__LOWERED_WIDTHS__", LoweredWidths(document.Semantics.Widths), StringComparison.Ordinal)
            .Replace("__LOWERED_OPERATIONS__", LoweredOperations(document.Semantics.Operations), StringComparison.Ordinal)
            .Replace("__LOWERED_BRANCHES__", LoweredBranches(document.Branches), StringComparison.Ordinal)
            .Replace("__LOWERED_EFFECTS__", LoweredEffects(document.Effects), StringComparison.Ordinal)
            .Replace("__LOWERED_ADAPTERS__", LoweredAdapters(document.Semantics.AdapterPremises), StringComparison.Ordinal)
            .Replace("__LOWERED_ROUTE__", LoweredBindings(document.Route.Bindings), StringComparison.Ordinal)
            .Replace("__UINT64_MODULUS__", Modulus(uint64), StringComparison.Ordinal)
            .Replace("__UINT256_MODULUS__", Modulus(uint256), StringComparison.Ordinal)
            .Replace("__EXECUTION_OPTIONS_INT_LIMIT__", SignedPositiveLimit(options), StringComparison.Ordinal)
            .Replace("__NORMALIZE_UINT64__", NormalizeExpression(normalizeUInt64, "uint64Modulus"), StringComparison.Ordinal)
            .Replace("__NORMALIZE_UINT256__", NormalizeExpression(normalizeUInt256, "uint256Modulus"), StringComparison.Ordinal)
            .Replace("__CHECKED_ADD_BODY__", CheckedOperationBody(checkedAdd, "sum", "+"), StringComparison.Ordinal)
            .Replace("__CHECKED_MULTIPLY_BODY__", CheckedOperationBody(checkedMultiply, "product", "*"), StringComparison.Ordinal)
            .Replace("__GAS_PER_BLOB__", GasPerBlobBody(gasPerBlobConstant), StringComparison.Ordinal)
            .Replace("__BLOB_GAS_BODY__", BlobGasBody(blobGas), StringComparison.Ordinal)
            .Replace("__OPTION_COMMIT__", document.Options.Commit.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__OPTION_RESTORE__", document.Options.Restore.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__OPTION_SKIP_VALIDATION__", document.Options.SkipValidation.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__OPTION_WARMUP__", document.Options.Warmup.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__OPTION_BUILD_UP__", document.Options.BuildUp.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__EFFECTIVE_GAS_PRICE_BODY__", EffectiveGasPriceBody(effectivePrice), StringComparison.Ordinal)
            .Replace("__PREMIUM_PER_GAS_BODY__", PremiumPerGasBody(premium), StringComparison.Ordinal)
            .Replace("__METRICS_GATE__", MetricsGate(metrics), StringComparison.Ordinal)
            .Replace("__RECOVERY_CREATE_GUARD__", RecoveryCreateGuard(recoveryDecision, recoveryCreate), StringComparison.Ordinal)
            .Replace("__RECOVERY_ACCOUNT_BODY__", RecoveryAccountBody(recoveryApplication), StringComparison.Ordinal)
            .Replace("__INPUT_ADAPTER_COHERENCE_BODY__", inputAdapterCoherence, StringComparison.Ordinal)
            .Replace("__STATIC_FAILURE_GUARD__", StaticFailureGuard(staticFailure), StringComparison.Ordinal)
            .Replace("__RECOVERY_MISSING_SENDER_GUARD__", RecoveryMissingSenderGuard(recoveryMissingSender), StringComparison.Ordinal)
            .Replace("__INVALID_CONTRACT_SENDER_GUARD__", InvalidContractSenderGuard(invalidContractSender), StringComparison.Ordinal)
            .Replace("__PREMIUM_BELOW_BASE_FEE_GUARD__", PremiumBelowBaseFeeGuard(premiumBelowBaseFee), StringComparison.Ordinal)
            .Replace("__RESERVED_PAYMENT_OVERFLOW_GUARD__", OverflowGuard(reservedPaymentOverflow, "reservedOverflow"), StringComparison.Ordinal)
            .Replace("__MAXIMUM_FEE_OVERFLOW_GUARD__", OverflowGuard(maximumFeeOverflow, "maximumBalanceCheck.overflow"), StringComparison.Ordinal)
            .Replace("__VALUE_OVERFLOW_GUARD__", OverflowGuard(valueOverflow, "balanceAfterValue.overflow"), StringComparison.Ordinal)
            .Replace("__BLOB_MAXIMUM_FEE_MULTIPLY_OVERFLOW_GUARD__", BlobMaximumFeeOverflowOperand(blobMaximumFeeOverflow, 0, "maximumBlobFee"), StringComparison.Ordinal)
            .Replace("__BLOB_MAXIMUM_FEE_ADD_OVERFLOW_GUARD__", BlobMaximumFeeOverflowOperand(blobMaximumFeeOverflow, 1, "balanceAfterBlobCap"), StringComparison.Ordinal)
            .Replace("__BLOB_FEE_CALCULATION_FAILURE_GUARD__", BlobFeeCalculationFailureGuard(blobFeeCalculationFailure), StringComparison.Ordinal)
            .Replace("__BLOB_FEE_CAP_BELOW_BASE_FEE_GUARD__", BlobFeeCapGuard(blobFeeCapBelowBaseFee), StringComparison.Ordinal)
            .Replace("__BLOB_PAYMENT_OVERFLOW_GUARD__", OverflowGuard(blobPaymentOverflow, "reservedWithBlob.overflow"), StringComparison.Ordinal)
            .Replace("__WARMUP_INSUFFICIENT_BALANCE_GUARD__", WarmupGuard(warmupInsufficientBalance), StringComparison.Ordinal)
            .Replace("__INSUFFICIENT_BALANCE_GUARD__", InsufficientBalanceGuard(insufficientBalance), StringComparison.Ordinal)
            .Replace("__NON_ZERO_RESERVED_PAYMENT_GUARD__", NonZeroReservedPaymentGuard(nonZeroReservedPayment), StringComparison.Ordinal)
            .Replace("__NONCE_MISMATCH_GUARD__", NonceMismatchGuard(nonceMismatch), StringComparison.Ordinal)
            .Replace("__RESERVE_PAYMENT_BODY__", ReservePaymentBody(feeReservation), StringComparison.Ordinal)
            .Replace("__DEBIT_BODY__", DebitBody(feeReservation), StringComparison.Ordinal)
            .Replace("__NEXT_NONCE_BODY__", NextNonceBody(nonceAdvance, setNonce), StringComparison.Ordinal)
            .Replace("__COMBINED_RESET_BODY__", CombinedResetBody(combinedReset, combinedResetBranch), StringComparison.Ordinal)
            .Replace("__CONTINUE_BODY__", ContinueBody(admissionPrefix, continuationBranch), StringComparison.Ordinal)
            .Replace("__ORDERED_STAGE_IDS__", QuotedIds(document.Stages.Select(static stage => stage.Id)), StringComparison.Ordinal)
            .Replace("__ORDERED_BRANCH_IDS__", QuotedIds(document.Branches.Select(static branch => branch.Id)), StringComparison.Ordinal) + "\n";
        if (source.Contains("__", StringComparison.Ordinal))
        {
            throw new ExtractionException("Lean emission retained an unlowered semantic placeholder.");
        }
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(source);
    }

    private static void ValidateSourceLoweredShape(IrDocument document)
    {
        if (document.Stages.Length != RequiredStageIds.Length || document.Branches.Length != RequiredBranchIds.Length)
        {
            throw new ExtractionException("The IR shape is not the admitted ordinary stateful-prefix shape.");
        }

        for (int index = 0; index < RequiredStageIds.Length; index++)
        {
            if (document.Stages[index].Id != RequiredStageIds[index] || document.Stages[index].Ordinal != index + 1)
            {
                throw new ExtractionException("The IR stage order changed before Lean emission.");
            }

            ValidateBinding(document.Stages[index].Binding);
        }

        for (int index = 0; index < RequiredBranchIds.Length; index++)
        {
            if (document.Branches[index].Id != RequiredBranchIds[index] || document.Branches[index].Ordinal != index + 1 ||
                document.Branches[index].TerminalKind != RequiredBranchTerminalKinds[index])
            {
                throw new ExtractionException("The IR branch order changed before Lean emission.");
            }

            BranchShape branch = document.Branches[index];
            if (string.IsNullOrWhiteSpace(branch.Condition) || string.IsNullOrWhiteSpace(branch.Terminal) ||
                branch.PartialEffects is null || branch.Effects is null ||
                !branch.PartialEffects.SequenceEqual(branch.Effects.Select(static effect => effect.ToString())) ||
                branch.Condition != branch.Binding.CanonicalSyntax || !Extractor.SourceDerivedBranchMatches(branch) ||
                !Extractor.SourceExpressionMatchesBinding(branch.Binding, branch.ConditionAst.Normalized, branch.ConditionAst))
            {
                throw new ExtractionException("A branch is not a complete source-lowered terminal/effect node.");
            }

            ValidateBinding(branch.Binding);
            ValidateExpressionTree(branch.ConditionAst);
            if (!branch.Condition.Contains(branch.ConditionAst.Normalized, StringComparison.Ordinal))
            {
                throw new ExtractionException("A branch condition tree no longer belongs to its admitted source node.");
            }
        }

        foreach (EffectShape effect in document.Effects)
        {
            if (effect.Effects is null || effect.Reads is null || effect.Writes is null ||
                string.IsNullOrWhiteSpace(effect.FailureVisibility))
            {
                throw new ExtractionException("An effect is not a complete source-lowered effect node.");
            }

            ValidateBinding(effect.Binding);
        }

        foreach (WidthShape width in document.Semantics.Widths)
        {
            if (width.SourceDefinition != width.Binding.CanonicalSyntax)
            {
                throw new ExtractionException("A width no longer carries its exact source projection.");
            }

            ValidateExpectedWidthSource(width);
            ValidateBinding(width.Binding);
        }

        for (int index = 0; index < document.Semantics.Operations.Length; index++)
        {
            SemanticOperation operation = document.Semantics.Operations[index];
            if (operation.Ordinal != index + 1)
            {
                throw new ExtractionException("A source-lowered semantic operation ordinal changed.");
            }

            if (operation.ExpressionAst is null || operation.Expression != operation.ExpressionAst.Normalized ||
                !operation.Binding.CanonicalSyntax.Contains(operation.Expression, StringComparison.Ordinal) ||
                operation.SourceMember != operation.Binding.Owner + "." + operation.Binding.Member ||
                !Extractor.SourceExpressionMatchesBinding(operation.Binding, operation.Expression, operation.ExpressionAst) ||
                !Extractor.MatchesFormulaGrammar(operation.Formula, operation.ExpressionAst))
            {
                throw new ExtractionException("An operation no longer carries its exact source expression and owner.");
            }

            ValidateExpectedOperationSource(operation);
            ValidateBinding(operation.Binding);
            ValidateExpressionTree(operation.ExpressionAst);
        }

        foreach (AdapterPremise premise in document.Semantics.AdapterPremises)
        {
            bool isGrammarAssumption = premise.Kind == AdapterKind.SourceGrammarSemanticsAssumption;
            if (premise.Bindings is null || premise.ProjectionAsts is null ||
                (isGrammarAssumption
                    ? premise.Bindings.Length != 0 || premise.ProjectionAsts.Length != 0
                    : premise.Bindings.Length == 0 || premise.Bindings.Length != premise.ProjectionAsts.Length) ||
                string.IsNullOrWhiteSpace(premise.DomainPredicate))
            {
                throw new ExtractionException("An adapter premise is not source-bound.");
            }

            ValidateExpectedAdapterBindings(premise);
            if (isGrammarAssumption)
            {
                continue;
            }

            for (int index = 0; index < premise.Bindings.Length; index++)
            {
                SourceBinding binding = premise.Bindings[index];
                ValidateBinding(binding);
                SourceExpression projectionAst = premise.ProjectionAsts[index];
                if (!Extractor.SourceExpressionMatchesBinding(binding, projectionAst.Normalized, projectionAst))
                {
                    throw new ExtractionException("An adapter projection is no longer source-derived.");
                }

                ValidateExpressionTree(projectionAst);
            }
        }

        foreach (SourceBinding binding in document.Route.Bindings)
        {
            ValidateBinding(binding);
        }
    }

    private static WidthShape RequireWidth(IrDocument document, string id, NumericWidth width, int bits)
    {
        WidthShape shape = document.Semantics.Widths.SingleOrDefault(candidate => candidate.Id == id)
            ?? throw new ExtractionException($"Missing semantic width {id}.");
        if (shape.Width != width || shape.Bits != bits)
        {
            throw new ExtractionException($"Semantic width {id} is not admitted.");
        }

        ValidateExpectedWidthSource(shape);
        ValidateBinding(shape.Binding);

        return shape;
    }

    private static SemanticOperation RequireOperation(
        IrDocument document,
        string id,
        SemanticFormula formula,
        NumericWidth[] inputWidths,
        NumericWidth outputWidth,
        SemanticEffect[] effects)
    {
        SemanticOperation operation = document.Semantics.Operations.SingleOrDefault(candidate => candidate.Id == id)
            ?? throw new ExtractionException($"Missing semantic operation {id}.");
        if (operation.Formula != formula || operation.OutputWidth != outputWidth ||
            !operation.InputWidths.SequenceEqual(inputWidths) || !operation.Effects.SequenceEqual(effects))
        {
            throw new ExtractionException($"Semantic operation {id} cannot be lowered by this emitter.");
        }

        if (operation.ExpressionAst is null || operation.Expression != operation.ExpressionAst.Normalized ||
            !operation.Binding.CanonicalSyntax.Contains(operation.Expression, StringComparison.Ordinal) ||
            operation.SourceMember != operation.Binding.Owner + "." + operation.Binding.Member ||
            !Extractor.SourceExpressionMatchesBinding(operation.Binding, operation.Expression, operation.ExpressionAst) ||
            !Extractor.MatchesFormulaGrammar(operation.Formula, operation.ExpressionAst))
        {
            throw new ExtractionException($"Semantic operation {id} lost its source node binding.");
        }

        ValidateExpectedOperationSource(operation);
        return operation;
    }

    private static SemanticOperation RequireMetricsOperation(IrDocument document)
    {
        SemanticOperation operation = document.Semantics.Operations.SingleOrDefault(candidate => candidate.Id == "metricsGate")
            ?? throw new ExtractionException("Missing semantic operation metricsGate.");
        if (operation.Formula is not (SemanticFormula.MetricsCommitOrNone or SemanticFormula.MetricsCommitOnly) ||
            operation.OutputWidth != NumericWidth.UInt256 ||
            !operation.InputWidths.SequenceEqual([NumericWidth.Int32, NumericWidth.UInt256]) ||
            !operation.Effects.SequenceEqual([SemanticEffect.AppendMetric]))
        {
            throw new ExtractionException("Semantic UpdateMetrics gate cannot be lowered by this emitter.");
        }

        if (operation.ExpressionAst is null || operation.Expression != operation.ExpressionAst.Normalized ||
            !operation.Binding.CanonicalSyntax.Contains(operation.Expression, StringComparison.Ordinal) ||
            !Extractor.SourceExpressionMatchesBinding(operation.Binding, operation.Expression, operation.ExpressionAst) ||
            !Extractor.MatchesFormulaGrammar(operation.Formula, operation.ExpressionAst))
        {
            throw new ExtractionException("Semantic UpdateMetrics gate lost its source condition tree.");
        }

        ValidateExpectedOperationSource(operation);
        ValidateExpressionTree(operation.ExpressionAst);
        return operation;
    }

    /// <summary>
    /// Keeps a coordinated edit to an operation's descriptive and binding fields from changing
    /// which source member the fixed bounded lowering purports to represent.
    /// </summary>
    private static void ValidateExpectedOperationSource(SemanticOperation operation)
    {
        (string path, string owner, string member) expected = operation.Id switch
        {
            "normalizeUInt64" => (Extractor.TransactionPath, "Transaction", "GasLimit"),
            "normalizeUInt256" => (Extractor.TransactionPath, "Transaction", "MaxFeePerBlobGas"),
            "checkedAdd256" => (Extractor.TransactionProcessorPath, "TransactionProcessorBase<TGasPolicy>", "UInt256.AddOverflow"),
            "checkedMultiply256" => (Extractor.TransactionProcessorPath, "TransactionProcessorBase<TGasPolicy>", "UInt256.MultiplyOverflow"),
            "effectiveGasPrice" => (Extractor.TransactionExtensionsPath, "TransactionExtensions", "CalculateEffectiveGasPrice"),
            "premiumPerGas" => (Extractor.TransactionExtensionsPath, "TransactionExtensions", "TryCalculatePremiumPerGas"),
            "metricsGate" => (Extractor.TransactionProcessorPath, "TransactionProcessorBase<TGasPolicy>", "UpdateMetrics"),
            "recoveryDecision" or "recoveryApplication" =>
                (Extractor.TransactionProcessorPath, "TransactionProcessorBase<TGasPolicy>", "RecoverSenderIfNeeded"),
            "feeReservation" => (Extractor.TransactionProcessorPath, "TransactionProcessorBase<TGasPolicy>", "BuyGas"),
            "nonceAdvance" => (Extractor.TransactionProcessorPath, "TransactionProcessorBase<TGasPolicy>", "IncrementNonce"),
            "combinedFailureReset" => (Extractor.TransactionProcessorPath, "TransactionProcessorBase<TGasPolicy>", "WorldState.Reset"),
            "admissionPrefix" => (Extractor.TransactionProcessorPath, "TransactionProcessorBase<TGasPolicy>", "Execute"),
            "gasPerBlobConstant" => (Extractor.Eip4844ConstantsPath, "Eip4844Constants", "GasPerBlob"),
            "blobGas" => (Extractor.BlobGasCalculatorPath, "BlobGasCalculator", "CalculateBlobGas"),
            _ => throw new ExtractionException("An unknown operation has no admitted source owner."),
        };
        if (operation.Binding.Path != expected.path || operation.Binding.Owner != expected.owner ||
            operation.Binding.Member != expected.member || operation.SourceMember != expected.owner + "." + expected.member)
        {
            throw new ExtractionException($"Semantic operation {operation.Id} is bound to an unadmitted source member.");
        }
    }

    private static void ValidateExpectedWidthSource(WidthShape width)
    {
        (string path, string owner, string member) expected = width.Id switch
        {
            "uint64" => (Extractor.TransactionPath, "Transaction", "GasLimit"),
            "uint256" => (Extractor.TransactionPath, "Transaction", "MaxFeePerBlobGas"),
            "executionOptions" => (Extractor.OptionsPath, "ExecutionOptions", "enum"),
            _ => throw new ExtractionException("An unknown width has no admitted source owner."),
        };
        if (width.Binding.Path != expected.path || width.Binding.Owner != expected.owner || width.Binding.Member != expected.member)
        {
            throw new ExtractionException($"Semantic width {width.Id} is bound to an unadmitted source member.");
        }
    }

    private static void ValidateExpectedAdapterBindings(AdapterPremise premise)
    {
        string[] expected = premise.Id switch
        {
            "sourceGrammarSemantics" => [],
            "transactionFields" =>
            [
                AdapterBindingKey(Extractor.TransactionPath, "Transaction", "SenderAddress"),
                AdapterBindingKey(Extractor.TransactionPath, "Transaction", "GasLimit"),
                AdapterBindingKey(Extractor.TransactionPath, "Transaction", "Nonce"),
                AdapterBindingKey(Extractor.TransactionPath, "Transaction", "MaxFeePerBlobGas"),
                AdapterBindingKey(Extractor.TransactionPath, "Transaction", "BlobVersionedHashes"),
            ],
            "transactionType" =>
            [
                AdapterBindingKey(Extractor.TransactionPath, "Transaction", "Supports1559"),
                AdapterBindingKey(Extractor.TransactionPath, "Transaction", "SupportsBlobs"),
                AdapterBindingKey(Extractor.TxTypePath, "TxType", "enum"),
                AdapterBindingKey(Extractor.TxTypeExtensionsPath, "TxTypeExtensions", "Supports1559"),
                AdapterBindingKey(Extractor.TxTypeExtensionsPath, "TxTypeExtensions", "SupportsBlobs"),
            ],
            "blobGas" =>
            [
                AdapterBindingKey(Extractor.TransactionExtensionsPath, "TransactionExtensions.TransactionAccessor", "GetBlobCount"),
                AdapterBindingKey(Extractor.BlobGasCalculatorPath, "BlobGasCalculator", "CalculateBlobGas"),
                AdapterBindingKey(Extractor.BlobGasCalculatorPath, "BlobGasCalculator", "CalculateBlobGas"),
                AdapterBindingKey(Extractor.BlobGasCalculatorPath, "BlobGasCalculator", "CalculateBlobGas"),
                AdapterBindingKey(Extractor.Eip4844ConstantsPath, "Eip4844Constants", "GasPerBlob"),
            ],
            "worldAndRecovery" =>
            [
                AdapterBindingKey(Extractor.TransactionProcessorPath, "TransactionProcessorBase<TGasPolicy>", "RecoverSenderIfNeeded"),
                AdapterBindingKey(Extractor.WorldStatePath, "WorldState", "CreateAccount"),
                AdapterBindingKey(Extractor.StateProviderPath, "StateProvider", "CreateAccount"),
                AdapterBindingKey(Extractor.WorldStatePath, "WorldState", "SetNonce"),
                AdapterBindingKey(Extractor.StateProviderPath, "StateProvider", "SetNonce"),
            ],
            "headerSpecAndStatic" =>
            [
                AdapterBindingKey(Extractor.TransactionProcessorPath, "TransactionProcessorBase<TGasPolicy>", "Execute"),
                AdapterBindingKey(Extractor.TransactionProcessorPath, "TransactionProcessorBase<TGasPolicy>", "BuyGas"),
            ],
            "preIntrinsicRecovery" =>
            [
                AdapterBindingKey(Extractor.TransactionProcessorPath, "TransactionProcessorBase<TGasPolicy>", "Execute"),
                AdapterBindingKey(Extractor.TransactionProcessorPath, "TransactionProcessorBase<TGasPolicy>", "RecoverSenderBeforeIntrinsicGas"),
            ],
            _ => throw new ExtractionException($"Adapter premise {premise.Id} has no admitted binding set."),
        };
        if (premise.Bindings.Length != expected.Length || !premise.Bindings.Select(BindingKey).SequenceEqual(expected))
        {
            throw new ExtractionException($"Adapter premise {premise.Id} has an unadmitted source binding set.");
        }
    }

    private static string AdapterBindingKey(string path, string owner, string member) => path + "|" + owner + "|" + member;

    private static string BindingKey(SourceBinding binding) => AdapterBindingKey(binding.Path, binding.Owner, binding.Member);

    private static BranchShape RequireBranch(IrDocument document, string id)
    {
        BranchShape branch = document.Branches.SingleOrDefault(candidate => candidate.Id == id)
            ?? throw new ExtractionException($"Missing source-lowered branch {id}.");
        if (branch.Terminal != branch.TerminalKind.ToString() || branch.PartialEffects is null || branch.Effects is null ||
            !branch.PartialEffects.SequenceEqual(branch.Effects.Select(static effect => effect.ToString())) ||
            branch.Condition != branch.Binding.CanonicalSyntax)
        {
            throw new ExtractionException($"Source-lowered branch {id} cannot be emitted.");
        }

        ValidateBinding(branch.Binding);
        return branch;
    }

    /// <summary>
    /// Emits the actual source condition tree for every live branch site. The surrounding Lean
    /// locals are an explicit adapter between C# locals and the model state; operators, operands,
    /// conjunctions, and member calls come from <see cref="SourceExpression"/>, never from a
    /// branch enum or normalized source string.
    /// </summary>
    private static string StaticFailureGuard(BranchShape branch) =>
        LowerBoolean(RequireCondition(branch, "validateStaticReturn"), LeanScope.Empty);

    private static string RecoveryMissingSenderGuard(BranchShape branch) =>
        LowerBoolean(RequireCondition(branch, "recoverSenderThrows"), LeanScope.For(
            ("sender", "state.transaction.sender")));

    private static string InvalidContractSenderGuard(BranchShape branch) =>
        LowerBoolean(RequireCondition(branch, "validateSenderContract"), LeanScope.For(
            ("validate", "!skipValidation input.options"),
            ("SkipSenderCodeCheck", "input.skipSenderCodeCheck")));

    private static string PremiumBelowBaseFeeGuard(BranchShape branch) =>
        LowerBoolean(RequireCondition(branch, "buyGasPremiumBelowBaseFee"), LeanScope.For(
            ("validate", "shouldValidateGas input"),
            ("TryCalculatePremiumPerGas", "premium.isSome")));

    private static string OverflowGuard(BranchShape branch, string local)
    {
        SourceExpression invocation = RequireOverflowInvocation(branch);
        return LowerBoolean(invocation, LeanScope.ForInvocations((invocation.SymbolId, local)));
    }

    private static string BlobMaximumFeeOverflowOperand(BranchShape branch, int ordinal, string local)
    {
        SourceExpression condition = RequireCondition(branch, "buyGasBlobMaximumFeeOverflow");
        SourceExpression[] invocations = Descendants(condition)
            .Where(static expression => expression.Kind == SourceExpressionKind.Invocation && expression.Symbol == "MultiplyOverflow" ||
                expression.Kind == SourceExpressionKind.Invocation && expression.Symbol == "AddOverflow")
            .ToArray();
        if (invocations.Length != 2 || condition.Kind != SourceExpressionKind.Binary || condition.Symbol != "||")
        {
            throw new ExtractionException("The blob maximum-fee condition is outside the admitted typed expression grammar.");
        }

        return LowerBoolean(invocations[ordinal], LeanScope.ForInvocations((invocations[ordinal].SymbolId, local + ".overflow")));
    }

    private static string BlobFeeCalculationFailureGuard(BranchShape branch) =>
        LowerBoolean(RequireCondition(branch, "buyGasBlobFeeCalculationOverflow"), LeanScope.For(
            ("TryCalculateBlobFees", "input.blobOracle.feePerBlobGasCalculationSucceeds && input.blobOracle.blobBaseFeeCalculationSucceeds")));

    private static string BlobFeeCapGuard(BranchShape branch) =>
        LowerBoolean(RequireCondition(branch, "buyGasBlobFeeCapBelowBaseFee"), LeanScope.Empty);

    private static string WarmupGuard(BranchShape branch) =>
        LowerBoolean(RequireCondition(branch, "buyGasInsufficientBalanceWarmup"), LeanScope.Empty);

    private static string InsufficientBalanceGuard(BranchShape branch) =>
        LowerBoolean(RequireCondition(branch, "buyGasInsufficientBalanceReturn"), LeanScope.For(
            ("balance", "state.world.effectiveSenderBalance"),
            ("balanceCheck", "balanceCheck")));

    private static string NonZeroReservedPaymentGuard(BranchShape branch) =>
        LowerBoolean(RequireCondition(branch, "buyGasDebit"), LeanScope.For(
            ("senderReservedGasPayment", "state.senderReservedGasPayment")));

    private static string NonceMismatchGuard(BranchShape branch) =>
        LowerBoolean(RequireCondition(branch, "incrementNonceMismatch"), LeanScope.For(
            ("validate", "validate"), ("nonce", "state.world.effectiveSenderNonce")));

    private static SourceExpression RequireCondition(BranchShape branch, string id)
    {
        if (branch.Id != id || branch.ConditionAst is null || string.IsNullOrWhiteSpace(branch.ConditionAst.Normalized) ||
            !branch.Condition.Contains(branch.ConditionAst.Normalized, StringComparison.Ordinal))
        {
            throw new ExtractionException($"Source branch {id} has no complete typed condition tree.");
        }

        ValidateExpressionTree(branch.ConditionAst);
        return branch.ConditionAst;
    }

    private static SourceExpression RequireOverflowInvocation(BranchShape branch)
    {
        SourceExpression condition = RequireCondition(branch, branch.Id);
        if (condition.Kind != SourceExpressionKind.Invocation ||
            (condition.Symbol != "MultiplyOverflow" && condition.Symbol != "AddOverflow"))
        {
            throw new ExtractionException($"Source branch {branch.Id} is not a direct checked-arithmetic condition.");
        }

        return condition;
    }

    private sealed class LeanScope
    {
        private readonly Dictionary<string, string> _identifiers;
        private readonly Dictionary<string, string> _invocations;

        private LeanScope(IEnumerable<(string Name, string Lean)> identifiers, IEnumerable<(string Source, string Lean)> invocations)
        {
            _identifiers = identifiers.ToDictionary(static pair => pair.Name, static pair => pair.Lean, StringComparer.Ordinal);
            _invocations = invocations.ToDictionary(static pair => pair.Source, static pair => pair.Lean, StringComparer.Ordinal);
        }

        internal static LeanScope Empty { get; } = new([], []);

        internal static LeanScope For(params (string Name, string Lean)[] identifiers) => new(identifiers, []);

        internal static LeanScope ForInvocations(params (string Source, string Lean)[] invocations) => new([], invocations);

        internal bool TryIdentifier(string name, out string value)
        {
            if (_identifiers.TryGetValue(name, out string? mapped))
            {
                value = mapped;
                return true;
            }

            value = string.Empty;
            return false;
        }

        internal bool TryInvocation(string source, out string value)
        {
            if (_invocations.TryGetValue(source, out string? mapped))
            {
                value = mapped;
                return true;
            }

            value = string.Empty;
            return false;
        }
    }

    private static string LowerBoolean(SourceExpression expression, LeanScope scope)
    {
        ValidateExpressionTree(expression);
        return expression.Kind switch
        {
            SourceExpressionKind.Unary when expression.Symbol == "!" => "!(" + LowerBoolean(expression.Children[0], scope) + ")",
            SourceExpressionKind.Binary when expression.Symbol == "is" =>
                "(" + LowerValue(expression.Children[0], scope) + " == " + LowerValue(expression.Children[1], scope) + ")",
            SourceExpressionKind.Binary when expression.Symbol is "&&" or "||" or "==" or "!=" or "<" or ">" or "<=" or ">=" =>
                "(" + LowerValue(expression.Children[0], scope) + " " + expression.Symbol + " " + LowerValue(expression.Children[1], scope) + ")",
            SourceExpressionKind.IsPattern => LowerIsPattern(expression, scope),
            SourceExpressionKind.Assignment when expression.Symbol == "=" => LowerBoolean(expression.Children[1], scope),
            SourceExpressionKind.Parenthesized => LowerBoolean(expression.Children[0], scope),
            SourceExpressionKind.Invocation => LowerInvocation(expression, scope),
            _ => LowerValue(expression, scope),
        };
    }

    private static string LowerValue(SourceExpression expression, LeanScope scope)
    {
        if (expression.Kind == SourceExpressionKind.Identifier && scope.TryIdentifier(expression.Symbol, out string mapped))
        {
            return mapped;
        }

        return expression.Kind switch
        {
            SourceExpressionKind.Identifier => LowerIdentifier(expression),
            SourceExpressionKind.Literal => LowerLiteral(expression),
            SourceExpressionKind.MemberAccess => LowerMemberAccess(expression, scope),
            SourceExpressionKind.Invocation => LowerInvocation(expression, scope),
            SourceExpressionKind.Unary when expression.Symbol == "!" => "!(" + LowerBoolean(expression.Children[0], scope) + ")",
            SourceExpressionKind.PostfixUnary when expression.Symbol == "!" => LowerValue(expression.Children[0], scope),
            SourceExpressionKind.Binary when expression.Symbol is "+" or "-" or "*" or "/" or "%" or "&&" or "||" or "==" or "!=" or "<" or ">" or "<=" or ">=" =>
                "(" + LowerValue(expression.Children[0], scope) + " " + expression.Symbol + " " + LowerValue(expression.Children[1], scope) + ")",
            SourceExpressionKind.Conditional => "(if " + LowerBoolean(expression.Children[0], scope) + " then " +
                LowerValue(expression.Children[1], scope) + " else " + LowerValue(expression.Children[2], scope) + ")",
            SourceExpressionKind.Cast => LowerCast(expression, scope),
            SourceExpressionKind.Assignment when expression.Symbol == "=" => LowerValue(expression.Children[1], scope),
            SourceExpressionKind.Parenthesized => "(" + LowerValue(expression.Children[0], scope) + ")",
            SourceExpressionKind.Argument => LowerValue(expression.Children[0], scope),
            _ => throw new ExtractionException("The typed source expression cannot be lowered as a Lean value: " + expression.Kind + "."),
        };
    }

    private static string LowerIdentifier(SourceExpression expression)
    {
        if (expression.SymbolId != "identifier:" + expression.Symbol)
        {
            throw new ExtractionException("The source identifier no longer carries its admitted identity.");
        }

        return expression.Symbol switch
        {
        "true" => "true",
        "false" => "false",
        "validate" => "validate",
        "eip1559Enabled" => "input.spec.eip1559Enabled",
        "baseFee" or "baseFeePerGas" => "u256 input.header.baseFeePerGas",
        "commit" => "recoveryCommit input",
        "noValidation" => "skipValidation input.options",
        "effectiveGasPrice" => "state.effectiveGasPrice",
        "sender" => "state.transaction.sender",
        "balance" => "state.world.effectiveSenderBalance",
        "balanceCheck" => "balanceCheck",
        "nonce" => "state.world.effectiveSenderNonce",
        "feeCap" => "feeCap",
        "effectiveFee" => "effectiveFee.value",
        "freeTransaction" => "freeTransaction",
        "feePerBlobGas" => "u256 input.blobOracle.feePerBlobGas",
        "blobBaseFee" => "state.blobBaseFee",
        "senderReservedGasPayment" => "state.senderReservedGasPayment",
        "SkipSenderCodeCheck" => "input.skipSenderCodeCheck",
        _ => throw new ExtractionException("The source identifier is outside the admitted Lean environment: " + expression.Symbol + "."),
        };
    }

    private static string LowerLiteral(SourceExpression expression)
    {
        if (!expression.SymbolId.StartsWith("literal:", StringComparison.Ordinal))
        {
            throw new ExtractionException("The source literal no longer carries its admitted identity.");
        }

        return expression.Symbol switch
        {
        "true" => "true",
        "false" => "false",
        _ when ulong.TryParse(expression.Symbol, NumberStyles.None, CultureInfo.InvariantCulture, out _) => expression.Symbol,
        _ => throw new ExtractionException("The source literal is outside the admitted Lean grammar: " + expression.Symbol + "."),
        };
    }

    private static string LowerMemberAccess(SourceExpression expression, LeanScope scope)
    {
        if (!expression.SymbolId.StartsWith("member:", StringComparison.Ordinal) ||
            !expression.SymbolId.EndsWith("." + expression.Symbol, StringComparison.Ordinal))
        {
            throw new ExtractionException("The source member access no longer carries its admitted identity.");
        }

        SourceExpression receiver = expression.Children[0];
        if (receiver.Kind == SourceExpressionKind.Identifier)
        {
            if (scope.TryIdentifier(receiver.Symbol + "." + expression.Symbol, out string scopedMember))
            {
                return scopedMember;
            }

            return (receiver.Symbol, expression.Symbol) switch
            {
                ("tx", "MaxPriorityFeePerGas") => "u256 input.tx.maxPriorityFeePerGas",
                ("tx", "MaxFeePerGas") => "u256 input.tx.maxFeePerGas",
                ("tx", "MaxFeePerBlobGas") => "u256 (blobFeeCap input.tx)",
                ("tx", "GasLimit") => "u64 input.tx.gasLimit",
                ("tx", "Nonce") => "u64 input.tx.nonce",
                ("tx", "ValueRef") => "u256 input.tx.value",
                ("tx", "Supports1559") => "input.tx.supports1559",
                ("tx", "SupportsBlobs") => "input.tx.supportsBlobs",
                ("spec", "IsEip1559Enabled") => "input.spec.eip1559Enabled",
                ("header", "BaseFeePerGas") => "u256 input.header.baseFeePerGas",
                ("Eip4844Constants", "GasPerBlob") => "gasPerBlob",
                ("UInt256", "Zero") => "0",
                ("ulong", "MaxValue") => "uint64Max",
                ("ExecutionOptions", "Commit") => "executionOptionCommit",
                ("ExecutionOptions", "None") => "0",
                ("ExecutionOptions", "Restore") => "executionOptionRestore",
                ("ExecutionOptions", "SkipValidation") => "executionOptionSkipValidation",
                ("ExecutionOptions", "Warmup") => "executionOptionWarmup",
                ("ExecutionOptions", "BuildUp") => "executionOptionBuildUp",
                ("effectiveGasPrice", "IsZero") => "state.effectiveGasPrice == 0",
                ("senderReservedGasPayment", "IsZero") => "state.senderReservedGasPayment == 0",
                _ => throw new ExtractionException("The source member access is outside the admitted Lean environment: " +
                    receiver.Symbol + "." + expression.Symbol + "."),
            };
        }

        throw new ExtractionException("The source member receiver is outside the admitted Lean environment.");
    }

    private static string LowerInvocation(SourceExpression expression, LeanScope scope)
    {
        if (!expression.SymbolId.StartsWith("invocation:", StringComparison.Ordinal) ||
            !expression.SymbolId.EndsWith("#" + expression.Symbol, StringComparison.Ordinal) ||
            expression.TypeName == "unresolved")
        {
            throw new ExtractionException("The source invocation no longer carries its admitted identity or result type.");
        }

        if (scope.TryInvocation(expression.SymbolId, out string mapped))
        {
            return mapped;
        }

        SourceExpression target = expression.Children[0];
        if (expression.Symbol == "ValidateStatic" && target.Kind == SourceExpressionKind.Identifier)
        {
            return "input.staticError == .none";
        }

        if (expression.Symbol == "TryCalculatePremiumPerGas" && target.Kind == SourceExpressionKind.Identifier)
        {
            return "premium.isSome";
        }

        if (target.Kind == SourceExpressionKind.MemberAccess && target.Children[0].Kind == SourceExpressionKind.Identifier)
        {
            string receiver = target.Children[0].Symbol;
            if (receiver == "opts" && expression.Symbol == "HasFlag" && expression.Children.Length == 2)
            {
                return "hasFlag input.options.raw " + LowerApplicationArgument(expression.Children[1], scope);
            }

            if (receiver == "tx" && expression.Symbol == "IsFree" && expression.Children.Length == 1)
            {
                return "input.tx.isFree";
            }

            if (receiver == "WorldState" && expression.Symbol == "IsInvalidContractSender")
            {
                return "input.world.effectiveSenderInvalidContract";
            }

            if (receiver == "_blobBaseFeeCalculator" && expression.Symbol == "TryCalculateBlobFees")
            {
                return "input.blobOracle.feePerBlobGasCalculationSucceeds && input.blobOracle.blobBaseFeeCalculationSucceeds";
            }

            if (receiver == "UInt256" && expression.Symbol == "Min" && expression.Children.Length == 3)
            {
                return "Nat.min " + LowerApplicationArgument(expression.Children[1], scope) + " " +
                    LowerApplicationArgument(expression.Children[2], scope);
            }
        }

        throw new ExtractionException("The source invocation is outside the admitted Lean environment: " + expression.Symbol + ".");
    }

    private static string LowerCast(SourceExpression expression, LeanScope scope)
    {
        if (expression.SymbolId != "cast:" + expression.Symbol || expression.TypeName != expression.Symbol)
        {
            throw new ExtractionException("The source cast no longer carries its admitted type identity.");
        }

        return expression.Symbol switch
        {
        "UInt256" => "u256 " + LowerApplicationArgument(expression.Children[0], scope),
        "ulong" => "u64 " + LowerApplicationArgument(expression.Children[0], scope),
        _ => throw new ExtractionException("The source cast is outside the admitted Lean grammar: " + expression.Symbol + "."),
        };
    }

    private static string LowerApplicationArgument(SourceExpression expression, LeanScope scope) =>
        "(" + LowerValue(expression, scope) + ")";

    private static string LowerIsPattern(SourceExpression expression, LeanScope scope)
    {
        SourceExpression value = expression.Children[0];
        SourceExpression pattern = expression.Children[1];
        if (pattern.Kind == SourceExpressionKind.PatternConstant && pattern.Children[0].Kind == SourceExpressionKind.Literal &&
            pattern.Children[0].Symbol == "null")
        {
            return LowerValue(value, scope) + ".isNone";
        }

        if (pattern.Kind == SourceExpressionKind.PatternUnary && pattern.Symbol == "not" &&
            pattern.Children[0].Kind == SourceExpressionKind.PatternConstant &&
            pattern.Children[0].Children[0].Kind == SourceExpressionKind.Literal &&
            pattern.Children[0].Children[0].Symbol == "null")
        {
            return LowerValue(value, scope) + ".isSome";
        }

        return LowerPattern(value, pattern, scope);
    }

    private static string LowerPattern(SourceExpression value, SourceExpression pattern, LeanScope scope) => pattern.Kind switch
    {
        SourceExpressionKind.PatternConstant => LowerValue(value, scope) + " == " + LowerValue(pattern.Children[0], scope),
        SourceExpressionKind.PatternUnary when pattern.Symbol == "not" => "!(" + LowerPattern(value, pattern.Children[0], scope) + ")",
        SourceExpressionKind.PatternBinary when pattern.Symbol is "or" or "and" =>
            "(" + LowerPattern(value, pattern.Children[0], scope) + (pattern.Symbol == "or" ? " || " : " && ") +
            LowerPattern(value, pattern.Children[1], scope) + ")",
        _ => throw new ExtractionException("The source pattern is outside the admitted Lean grammar."),
    };

    private static IEnumerable<SourceExpression> Descendants(SourceExpression expression)
    {
        yield return expression;
        foreach (SourceExpression child in expression.Children)
        {
            foreach (SourceExpression descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static void ValidateExpressionTree(SourceExpression? expression)
    {
        if (expression is null || string.IsNullOrWhiteSpace(expression.Normalized) ||
            string.IsNullOrWhiteSpace(expression.Symbol) || string.IsNullOrWhiteSpace(expression.SymbolId) ||
            string.IsNullOrWhiteSpace(expression.TypeName) || expression.Children is null)
        {
            throw new ExtractionException("Lean emission received an incomplete typed source expression.");
        }

        if (!HasAdmittedExpressionIdentity(expression))
        {
            throw new ExtractionException("Lean emission received a relabeled source symbol or result category.");
        }

        foreach (SourceExpression child in expression.Children)
        {
            ValidateExpressionTree(child);
        }
    }

    private static bool HasAdmittedExpressionIdentity(SourceExpression expression) => expression.Kind switch
    {
        SourceExpressionKind.Identifier => expression.SymbolId == "identifier:" + expression.Symbol &&
            expression.TypeName != "unresolved",
        SourceExpressionKind.Literal => expression.SymbolId.StartsWith("literal:", StringComparison.Ordinal) &&
            expression.TypeName != "unresolved",
        SourceExpressionKind.MemberAccess => expression.SymbolId.StartsWith("member:", StringComparison.Ordinal) &&
            expression.SymbolId.EndsWith("." + expression.Symbol, StringComparison.Ordinal) &&
            expression.TypeName != "unresolved",
        SourceExpressionKind.Invocation => expression.SymbolId.StartsWith("invocation:", StringComparison.Ordinal) &&
            expression.TypeName != "unresolved",
        SourceExpressionKind.Unary or SourceExpressionKind.PostfixUnary or SourceExpressionKind.Binary or SourceExpressionKind.Assignment or
            SourceExpressionKind.Conditional or SourceExpressionKind.IsPattern or SourceExpressionKind.Parenthesized =>
            expression.SymbolId.StartsWith("operator:", StringComparison.Ordinal),
        SourceExpressionKind.Cast => expression.SymbolId == "cast:" + expression.Symbol,
        SourceExpressionKind.Argument => expression.SymbolId == "argument:" + expression.Symbol,
        SourceExpressionKind.Projection or SourceExpressionKind.Block or SourceExpressionKind.LocalDeclaration or
            SourceExpressionKind.VariableDeclarator or SourceExpressionKind.If or SourceExpressionKind.Return or
            SourceExpressionKind.ExpressionStatement or SourceExpressionKind.PatternConstant or
            SourceExpressionKind.PatternUnary or SourceExpressionKind.PatternBinary =>
            expression.SymbolId.StartsWith(expression.Kind + ":", StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>
    /// Emits the production-domain adapter conjunction from the typed adapter IR, rather than
    /// retaining an independently handwritten conjunction in the Lean template.
    /// </summary>
    private static string InputAdapterCoherenceBody(IrDocument document)
    {
        AdapterPremise[] premises =
        [
            RequireAdapter(document, "sourceGrammarSemantics", AdapterKind.SourceGrammarSemanticsAssumption,
                Extractor.SourceGrammarAuditPath, Extractor.SourceGrammarAuditMember, "sourceGrammarSemanticsCoherent"),
            RequireAdapter(document, "transactionFields", AdapterKind.TransactionProjection, Extractor.TransactionPath, "Transaction", "transactionSnapshotCoherent"),
            RequireAdapter(document, "transactionType", AdapterKind.TransactionTypeProjection, Extractor.TxTypePath, "TxType and TxTypeExtensions", "transactionTypeCoherent"),
            RequireAdapter(document, "blobGas", AdapterKind.BlobProjection, Extractor.BlobGasCalculatorPath, "BlobGasCalculator.CalculateBlobGas(Transaction)", "blobProjectionCoherent"),
            RequireAdapter(document, "worldAndRecovery", AdapterKind.WorldStateProjection, Extractor.TransactionProcessorPath, "WorldState/Ecdsa/logging calls", "standardMainnetWorldStateCoherent"),
            RequireAdapter(document, "headerSpecAndStatic", AdapterKind.HeaderAndSpecProjection, Extractor.TransactionProcessorPath, "Execute/ValidateStatic/BuyGas", "headerSpecAndStaticCoherent"),
            RequireAdapter(document, "preIntrinsicRecovery", AdapterKind.RouteProjection, Extractor.TransactionProcessorPath, "RecoverSenderBeforeIntrinsicGas", "preIntrinsicEip2780Coherent"),
        ];
        return string.Join(" ∧\n    ", premises.Select(static premise => premise.DomainPredicate + " input"));
    }

    private static AdapterPremise RequireAdapter(
        IrDocument document,
        string id,
        AdapterKind kind,
        string sourcePath,
        string sourceMember,
        string domainPredicate)
    {
        AdapterPremise premise = document.Semantics.AdapterPremises.SingleOrDefault(candidate => candidate.Id == id)
            ?? throw new ExtractionException($"Missing adapter premise {id}.");
        bool isGrammarAssumption = kind == AdapterKind.SourceGrammarSemanticsAssumption;
        if (premise.Kind != kind || premise.SourcePath != sourcePath || premise.SourceMember != sourceMember ||
            premise.DomainPredicate != domainPredicate || premise.Bindings is null || premise.ProjectionAsts is null ||
            (isGrammarAssumption
                ? premise.Bindings.Length != 0 || premise.ProjectionAsts.Length != 0
                : premise.Bindings.Length == 0 || premise.Bindings.Length != premise.ProjectionAsts.Length) ||
            string.IsNullOrWhiteSpace(premise.Projection) || string.IsNullOrWhiteSpace(premise.Assumption))
        {
            throw new ExtractionException($"Adapter premise {id} cannot be lowered by this emitter.");
        }

        if (isGrammarAssumption)
        {
            return premise;
        }

        for (int index = 0; index < premise.Bindings.Length; index++)
        {
            SourceBinding binding = premise.Bindings[index];
            ValidateBinding(binding);
            SourceExpression projectionAst = premise.ProjectionAsts[index];
            if (!Extractor.SourceExpressionMatchesBinding(binding, projectionAst.Normalized, projectionAst))
            {
                throw new ExtractionException($"Adapter premise {id} lost its source projection.");
            }

            ValidateExpressionTree(projectionAst);
        }

        return premise;
    }

    private static string Modulus(WidthShape width) => "2 ^ " + width.Bits.ToString(CultureInfo.InvariantCulture);

    private static string SignedPositiveLimit(WidthShape width) =>
        "2 ^ " + (width.Bits - 1).ToString(CultureInfo.InvariantCulture);

    private static string NormalizeExpression(SemanticOperation operation, string modulus)
    {
        SourceExpression projection = RequireOperationExpression(operation);
        if (operation.Formula != SemanticFormula.NormalizeUnsigned ||
            !operation.Effects.SequenceEqual([SemanticEffect.NormalizeInput]) || projection.Kind != SourceExpressionKind.Projection)
        {
            throw new ExtractionException($"Cannot lower {operation.Id} normalization.");
        }

        return "value % " + modulus;
    }

    private static string GasPerBlobBody(SemanticOperation operation)
    {
        SourceExpression literal = RequireOperationExpression(operation);
        if (operation.Formula != SemanticFormula.SourceConstant || literal.Kind != SourceExpressionKind.Literal ||
            !operation.Effects.SequenceEqual([SemanticEffect.NormalizeInput]))
        {
            throw new ExtractionException("Cannot lower the source-bound GasPerBlob constant.");
        }

        return LowerLiteral(literal);
    }

    private static string BlobGasBody(SemanticOperation operation)
    {
        SourceExpression multiplication = RequireOperationExpression(operation);
        if (operation.Formula != SemanticFormula.BlobGas || multiplication.Kind != SourceExpressionKind.Binary ||
            multiplication.Symbol != "*" || multiplication.Children.Length != 2 ||
            !operation.Effects.SequenceEqual([SemanticEffect.NormalizeInput]))
        {
            throw new ExtractionException("Cannot lower the source-bound BlobGasCalculator formula.");
        }

        LeanScope scope = LeanScope.For(("blobCount", "u64 (blobHashCount tx)"));
        return "u64 (" + LowerValue(multiplication.Children[0], scope) + " * " +
            LowerValue(multiplication.Children[1], scope) + ")";
    }

    private static string CheckedOperationBody(SemanticOperation operation, string local, string operatorToken)
    {
        SourceExpression invocation = RequireOperationExpression(operation);
        string expectedInvocation = operation.Formula switch
        {
            SemanticFormula.CheckedAdd => "AddOverflow",
            SemanticFormula.CheckedMultiply => "MultiplyOverflow",
            _ => throw new ExtractionException($"Cannot lower {operation.Id} checked arithmetic."),
        };
        if (invocation.Kind != SourceExpressionKind.Invocation || invocation.Symbol != expectedInvocation ||
            !operation.Effects.SequenceEqual([SemanticEffect.PreserveWrappedOutValue]))
        {
            throw new ExtractionException($"Cannot lower {operation.Id} checked arithmetic.");
        }

        return "  let " + local + " := u256 left " + operatorToken + " u256 right\n" +
            "  { value := u256 " + local + ", overflow := !(" + local + " < uint256Modulus) }";
    }

    private static string EffectiveGasPriceBody(SemanticOperation operation)
    {
        SourceExpression outer = RequireOperationExpression(operation);
        if (operation.Formula != SemanticFormula.EffectiveGasPrice || outer.Kind != SourceExpressionKind.Conditional ||
            outer.Children[2].Kind != SourceExpressionKind.Conditional)
        {
            throw new ExtractionException("Cannot lower the effective-gas-price formula.");
        }

        SourceExpression nested = outer.Children[2];
        SourceExpression overflow = nested.Children[0];
        if (overflow.Kind != SourceExpressionKind.Invocation || overflow.Symbol != "AddOverflow" || overflow.Children.Length != 4)
        {
            throw new ExtractionException("The effective-gas-price source expression lost its checked-add branch.");
        }

        return "  if " + LowerBoolean(outer.Children[0], LeanScope.Empty) + " then " +
            LowerValue(outer.Children[1], LeanScope.Empty) + " else\n" +
            "  let effectiveFee := checkedAdd256 " + LowerApplicationArgument(overflow.Children[1], LeanScope.Empty) + " " +
            LowerApplicationArgument(overflow.Children[2], LeanScope.Empty) + "\n" +
            "  if " + LowerBoolean(overflow, LeanScope.ForInvocations((overflow.SymbolId, "effectiveFee.overflow"))) + " then " +
            LowerValue(nested.Children[1], LeanScope.Empty) + "\n" +
            "  else " + LowerValue(nested.Children[2], LeanScope.For(("effectiveFee", "effectiveFee.value")));
    }

    private static string PremiumPerGasBody(SemanticOperation operation)
    {
        SourceExpression program = RequireOperationExpression(operation);
        if (operation.Formula != SemanticFormula.PremiumPerGas || program.Kind != SourceExpressionKind.Block ||
            program.Children.Length != 5 || program.Children[2].Kind != SourceExpressionKind.If)
        {
            throw new ExtractionException("Cannot lower the premium-per-gas formula.");
        }

        SourceExpression freeTransaction = LocalInitializer(program.Children[0], "freeTransaction");
        SourceExpression feeCap = LocalInitializer(program.Children[1], "feeCap");
        SourceExpression negativePremium = program.Children[2];
        SourceExpression returnedFree = ReturnValue(negativePremium.Children[1]);
        SourceExpression zeroPremium = AssignmentValue(negativePremium.Children[1].Children[0], "premiumPerGas");
        SourceExpression normalPremium = AssignmentValue(program.Children[3], "premiumPerGas");
        LeanScope locals = LeanScope.For(("freeTransaction", "freeTransaction"), ("feeCap", "feeCap"));

        return "  let freeTransaction := " + LowerValue(freeTransaction, LeanScope.Empty) + "\n" +
            "  let feeCap := " + LowerValue(feeCap, LeanScope.Empty) + "\n" +
            "  if " + LowerBoolean(negativePremium.Children[0], locals) + " then\n" +
            "    if " + LowerBoolean(returnedFree, locals) + " then some " + LowerValue(zeroPremium, locals) + " else none\n" +
            "  else\n" +
            "    some (" + LowerValue(normalPremium, locals) + ")";
    }

    private static string MetricsGate(SemanticOperation operation)
    {
        SourceExpression body = RequireOperationExpression(operation);
        if (operation.Formula is not (SemanticFormula.MetricsCommitOrNone or SemanticFormula.MetricsCommitOnly) ||
            body.Kind != SourceExpressionKind.Block || body.Children.Length != 1 ||
            body.Children[0].Kind != SourceExpressionKind.If || body.Children[0].Children.Length != 2)
        {
            throw new ExtractionException("Cannot lower the UpdateMetrics equality gate.");
        }

        SourceExpression conditional = body.Children[0];
        SourceExpression[] calls = Descendants(conditional.Children[1])
            .Where(static expression => expression.Kind == SourceExpressionKind.Invocation)
            .ToArray();
        if (calls.Length != 1 || calls[0].Symbol != "UpdateBlockGasPrice" ||
            calls[0].SymbolId != "invocation:Metrics.UpdateBlockGasPrice#UpdateBlockGasPrice" ||
            calls[0].TypeName != "void")
        {
            throw new ExtractionException("Cannot lower a changed UpdateMetrics effect.");
        }

        return LowerBoolean(conditional.Children[0], LeanScope.For(("opts", "input.options.raw")));
    }

    private static string RecoveryCreateGuard(SemanticOperation operation, BranchShape branch)
    {
        if (operation.Formula != SemanticFormula.RecoveryDecision ||
            RequireOperationExpression(operation).Kind != SourceExpressionKind.Binary)
        {
            throw new ExtractionException("Cannot lower the sender-recovery decision.");
        }

        return LowerBoolean(RequireCondition(branch, "recoverSenderCreatesAccount"), LeanScope.Empty);
    }

    private static SourceExpression RequireOperationExpression(SemanticOperation operation)
    {
        if (operation.ExpressionAst is null || operation.Expression != operation.ExpressionAst.Normalized ||
            !operation.Binding.CanonicalSyntax.Contains(operation.ExpressionAst.Normalized, StringComparison.Ordinal) ||
            !Extractor.SourceExpressionMatchesBinding(operation.Binding, operation.ExpressionAst.Normalized, operation.ExpressionAst) ||
            !Extractor.MatchesFormulaGrammar(operation.Formula, operation.ExpressionAst))
        {
            throw new ExtractionException($"Semantic operation {operation.Id} no longer carries a complete source expression tree.");
        }

        ValidateExpressionTree(operation.ExpressionAst);
        return operation.ExpressionAst;
    }

    private static void RequireActionInvocation(BranchShape branch, string id, string invocationName)
    {
        SourceExpression action = RequireCondition(branch, id);
        if (action.Kind != SourceExpressionKind.Invocation || action.Symbol != invocationName)
        {
            throw new ExtractionException($"Source branch {id} no longer carries the admitted {invocationName} action.");
        }
    }

    private static SourceExpression LocalInitializer(SourceExpression statement, string name)
    {
        if (statement.Kind != SourceExpressionKind.LocalDeclaration || statement.Children.Length != 1 ||
            statement.Children[0].Kind != SourceExpressionKind.LocalDeclaration)
        {
            throw new ExtractionException("A source local declaration is outside the admitted premium formula grammar.");
        }

        SourceExpression declarator = statement.Children[0].Children.SingleOrDefault()
            ?? throw new ExtractionException("A source local declaration has no declarator.");
        if (declarator.Kind != SourceExpressionKind.VariableDeclarator || declarator.Symbol != name || declarator.Children.Length != 1)
        {
            throw new ExtractionException("A source local declaration changed its admitted name or initializer.");
        }

        return declarator.Children[0];
    }

    private static SourceExpression ReturnValue(SourceExpression statement)
    {
        if (statement.Kind == SourceExpressionKind.Block)
        {
            SourceExpression returned = statement.Children.SingleOrDefault(static child => child.Kind == SourceExpressionKind.Return)
                ?? throw new ExtractionException("A source premium branch lost its return statement.");
            return ReturnValue(returned);
        }

        if (statement.Kind != SourceExpressionKind.Return || statement.Children.Length != 1)
        {
            throw new ExtractionException("A source return is outside the admitted premium formula grammar.");
        }

        return statement.Children[0];
    }

    private static SourceExpression AssignmentValue(SourceExpression statement, string target)
    {
        if (statement.Kind == SourceExpressionKind.Block)
        {
            SourceExpression assigned = statement.Children.FirstOrDefault(static child => child.Kind == SourceExpressionKind.ExpressionStatement)
                ?? throw new ExtractionException("A source premium branch lost its output assignment.");
            return AssignmentValue(assigned, target);
        }

        if (statement.Kind != SourceExpressionKind.ExpressionStatement || statement.Children.Length != 1 ||
            statement.Children[0].Kind != SourceExpressionKind.Assignment || statement.Children[0].Symbol != "=" ||
            statement.Children[0].Children.Length != 2 || statement.Children[0].Children[0].Kind != SourceExpressionKind.Identifier ||
            statement.Children[0].Children[0].Symbol != target)
        {
            throw new ExtractionException("A source output assignment is outside the admitted premium formula grammar.");
        }

        return statement.Children[0].Children[1];
    }

    private static string RecoveryAccountBody(SemanticOperation operation)
    {
        if (operation.Formula != SemanticFormula.RecoveryApplication ||
            !operation.Effects.SequenceEqual([
                SemanticEffect.AppendRecoveryLog,
                SemanticEffect.ReplaceTransactionSender,
                SemanticEffect.CreateZeroAccount,
                SemanticEffect.PreserveThrowState]) ||
            RequireOperationExpression(operation).Kind != SourceExpressionKind.Binary)
        {
            throw new ExtractionException("Cannot lower sender-recovery effects.");
        }

        return "  if input.world.standardMainnetWorldState then\n" +
            "    { state with world := { effectiveSenderAccountExists := true\n" +
            "                            effectiveSenderBalance := 0\n" +
            "                            effectiveSenderNonce := 0\n" +
            "                            accountCreated := true }\n" +
            "                 deleteCallerAccount := deleteCallerAccount }\n" +
            "  else\n" +
            "    { state with world := { state.world with effectiveSenderAccountExists := true, accountCreated := true }\n" +
            "                 deleteCallerAccount := deleteCallerAccount }";
    }

    private static string ReservePaymentBody(SemanticOperation operation)
    {
        SourceExpression invocation = RequireOperationExpression(operation);
        if (operation.Formula != SemanticFormula.FeeReservation || invocation.Kind != SourceExpressionKind.Invocation ||
            invocation.Symbol != "MultiplyOverflow" || invocation.Children.Length != 4 ||
            !operation.Effects.Contains(SemanticEffect.PreserveWrappedOutValue))
        {
            throw new ExtractionException("Cannot lower fee-reservation arithmetic.");
        }

        LeanScope parameters = LeanScope.For(("tx.GasLimit", "u64 (gasLimit)"), ("effectiveGasPrice", "effectiveGasPrice"));
        return "  checkedMul256 " + LowerApplicationArgument(invocation.Children[1], parameters) + " " +
            LowerApplicationArgument(invocation.Children[2], parameters);
    }

    private static string DebitBody(SemanticOperation operation)
    {
        if (operation.Formula != SemanticFormula.FeeReservation ||
            !operation.Effects.Contains(SemanticEffect.DebitEffectiveSender) ||
            RequireOperationExpression(operation).Kind != SourceExpressionKind.Invocation)
        {
            throw new ExtractionException("Cannot lower fee-reservation debit effects.");
        }

        return "  { state with world :=\n" +
            "    { state.world with effectiveSenderBalance := state.world.effectiveSenderBalance - amount } }";
    }

    private static string LowerCheckedUInt256Operand(SourceExpression expression, LeanScope scope) =>
        expression.Kind == SourceExpressionKind.Cast && expression.Symbol == "UInt256"
            ? LowerValue(expression.Children[0], scope)
            : LowerValue(expression, scope);

    private static string NextNonceBody(SemanticOperation operation, BranchShape setNonce)
    {
        if (operation.Formula != SemanticFormula.NonceAdvance ||
            !operation.Effects.SequenceEqual([SemanticEffect.AppendValidationLog, SemanticEffect.SetEffectiveSenderNonce]) ||
            setNonce.TerminalKind != BranchTerminalKind.Continue)
        {
            throw new ExtractionException("Cannot lower nonce-advance effects.");
        }

        RequireActionInvocation(setNonce, "incrementNonceSet", "SetNonce");
        SourceExpression conditional = RequireOperationExpression(operation);
        if (conditional.Kind != SourceExpressionKind.Conditional)
        {
            throw new ExtractionException("IncrementNonce no longer has an admitted conditional source expression.");
        }

        SourceExpression increment = conditional.Children[1];
        if (increment.Kind != SourceExpressionKind.Binary || increment.Symbol != "+" || increment.Children.Length != 2)
        {
            throw new ExtractionException("IncrementNonce no longer carries the admitted UInt64 increment.");
        }

        LeanScope scope = LeanScope.For(("validate", "validate"), ("nonce", "nonce"));
        return "  if " + LowerBoolean(conditional.Children[0], scope) +
            " then add64 " + LowerApplicationArgument(increment.Children[0], scope) + " " +
            LowerApplicationArgument(increment.Children[1], scope) +
            " else " + LowerValue(conditional.Children[2], scope);
    }

    private static string CombinedResetBody(SemanticOperation operation, BranchShape branch)
    {
        if (operation.Formula != SemanticFormula.CombinedFailureReset ||
            !operation.Effects.SequenceEqual([SemanticEffect.ResetJournalAfterCombinedFailure]) ||
            RequireOperationExpression(operation).Kind != SourceExpressionKind.Invocation)
        {
            throw new ExtractionException("Cannot lower combined-gate reset effects.");
        }

        return "  if " + LowerBoolean(RequireCondition(branch, "combinedAdmissionFailureRestore"), LeanScope.For(("restore", "restore input.options"))) + " then\n" +
            "    { state with journal := { resetCalled := true, resetBlockChangesFalse := true } }\n" +
            "  else state";
    }

    private static string ContinueBody(SemanticOperation operation, BranchShape branch)
    {
        if (operation.Formula != SemanticFormula.AdmissionPrefix ||
            !operation.Effects.SequenceEqual([SemanticEffect.ContinueAfterPrefix]) ||
            RequireOperationExpression(operation).Kind != SourceExpressionKind.Binary)
        {
            throw new ExtractionException("Cannot lower admission-prefix continuation.");
        }

        RequireActionInvocation(branch, "continueAfterAdmissionPrefix", "PrepareSimpleTransferFastPath");
        return "  finish .continue state";
    }

    private static string QuotedIds(IEnumerable<string> ids) =>
        "[" + string.Join(", ", ids.Select(id => "\"" + id + "\"")) + "]";

    private static string LoweredWidths(IEnumerable<WidthShape> widths) =>
        LoweredStrings(widths.Select(width => width.Id + "|" + width.Width + "|" + width.Bits + "|" + BindingIdentity(width.Binding)));

    private static string LoweredOperations(IEnumerable<SemanticOperation> operations) =>
        LoweredStrings(operations.Select(operation => operation.Id + "|" + operation.Ordinal + "|" + operation.Formula + "|" +
            string.Join(',', operation.InputWidths) + "|" + operation.OutputWidth + "|" + string.Join(',', operation.Effects) + "|" +
            SourceExpressionIdentity(operation.ExpressionAst) + "|" + BindingIdentity(operation.Binding)));

    private static string LoweredBranches(IEnumerable<BranchShape> branches) =>
        LoweredStrings(branches.Select(branch => branch.Id + "|" + branch.Ordinal + "|" + branch.TerminalKind + "|" +
            SourceExpressionIdentity(branch.ConditionAst) + "|" + string.Join(',', branch.Effects) + "|" + BindingIdentity(branch.Binding)));

    private static string SourceExpressionIdentity(SourceExpression expression) =>
        expression.Kind + "(" + expression.Symbol + ":" + expression.SymbolId + ":" + expression.TypeName + ":" + expression.Normalized + "[" +
        string.Join(',', expression.Children.Select(SourceExpressionIdentity)) + "])";

    private static string LoweredEffects(IEnumerable<EffectShape> effects) =>
        LoweredStrings(effects.Select(effect => effect.Id + "|" + string.Join(',', effect.Reads) + "|" + string.Join(',', effect.Writes) +
            "|" + effect.FailureVisibility + "|" + string.Join(',', effect.Effects) + "|" + BindingIdentity(effect.Binding)));

    private static string LoweredAdapters(IEnumerable<AdapterPremise> premises) =>
        LoweredStrings(premises.Select(premise => premise.Id + "|" + premise.Kind + "|" + premise.DomainPredicate + "|" +
            premise.Projection + "|" + premise.Assumption + "|" + string.Join(';', premise.Bindings.Select(BindingIdentity)) + "|" +
            string.Join(';', premise.ProjectionAsts.Select(SourceExpressionIdentity))));

    private static string LoweredBindings(IEnumerable<SourceBinding> bindings) =>
        LoweredStrings(bindings.Select(BindingIdentity));

    private static string BindingIdentity(SourceBinding binding) =>
        binding.Path + "|" + binding.Owner + "|" + binding.Member + "|" + binding.Signature + "|" + binding.TokenSha256 + "|" +
        binding.CanonicalSyntaxSha256 + "|" + binding.SourceSyntax + "|" + binding.ContainingMember + "|" + binding.Receiver + "|" +
        binding.StatementOrdinal + "|" + binding.ControlFlowPath;

    private static string LoweredStrings(IEnumerable<string> values) =>
        "[" + string.Join(", ", values.Select(LeanString)) + "]";

    private static string LeanString(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

    private static void ValidateBinding(SourceBinding? binding)
    {
        if (binding is null || string.IsNullOrWhiteSpace(binding.Path) || string.IsNullOrWhiteSpace(binding.Owner) ||
            string.IsNullOrWhiteSpace(binding.Member) || string.IsNullOrWhiteSpace(binding.Signature) ||
            string.IsNullOrWhiteSpace(binding.CanonicalSyntax) || string.IsNullOrWhiteSpace(binding.SourceSyntax) ||
            string.IsNullOrWhiteSpace(binding.ContainingMember) ||
            binding.Receiver is null || binding.ControlFlowPath is null || binding.StatementOrdinal < 0 ||
            binding.StartLine <= 0 || binding.StartColumn <= 0 || binding.EndLine <= 0 || binding.EndColumn <= 0 ||
            !IsSha256(binding.TokenSha256) || !IsSha256(binding.CanonicalSyntaxSha256) ||
            binding.CanonicalSyntaxSha256 != Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(binding.CanonicalSyntax))))
        {
            throw new ExtractionException("Lean emission received an incomplete or tampered source binding.");
        }
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private const string LeanSource = """
-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only
-- Generated by OrdinaryStatefulAdmissionPrefixExtractor. Do not edit.
-- This is a bounded pure model of the ordinary transaction stateful-admission prefix only.

import Lean

namespace OrdinaryStatefulAdmissionPrefixExtractor.Generated

def sourceIrSha256 : String := "__IR_SHA256__"

/- These lists are emitted from every typed IR node. They make the admitted AST-to-Lean
   lowering inspectable and ensure a changed accepted semantic node changes this artifact. -/
def sourceLoweredWidths : List String := __LOWERED_WIDTHS__
def sourceLoweredOperations : List String := __LOWERED_OPERATIONS__
def sourceLoweredBranches : List String := __LOWERED_BRANCHES__
def sourceLoweredEffects : List String := __LOWERED_EFFECTS__
def sourceLoweredAdapters : List String := __LOWERED_ADAPTERS__
def sourceLoweredRoute : List String := __LOWERED_ROUTE__

def uint64Modulus : Nat := __UINT64_MODULUS__
def uint64Max : Nat := uint64Modulus - 1
def uint256Modulus : Nat := __UINT256_MODULUS__
def uint256Max : Nat := uint256Modulus - 1

def gasPerBlob : Nat := __GAS_PER_BLOB__

def u64 (value : Nat) : Nat := __NORMALIZE_UINT64__
def u256 (value : Nat) : Nat := __NORMALIZE_UINT256__
def add64 (left right : Nat) : Nat := u64 (u64 left + u64 right)
structure CheckedUInt256 where
  value : Nat
  overflow : Bool
  deriving DecidableEq, Repr
def checkedAdd256 (left right : Nat) : CheckedUInt256 :=
__CHECKED_ADD_BODY__
def checkedMul256 (left right : Nat) : CheckedUInt256 :=
__CHECKED_MULTIPLY_BODY__

def executionOptionCommit : Nat := __OPTION_COMMIT__
def executionOptionRestore : Nat := __OPTION_RESTORE__
def executionOptionSkipValidation : Nat := __OPTION_SKIP_VALIDATION__
def executionOptionWarmup : Nat := __OPTION_WARMUP__
def executionOptionBuildUp : Nat := __OPTION_BUILD_UP__
def executionOptionsIntLimit : Nat := __EXECUTION_OPTIONS_INT_LIMIT__

def hasFlag (raw flag : Nat) : Bool := ((raw / flag) % 2) == 1

inductive ErrorType where
  | none
  | staticAdmissionFailure
  | senderHasDeployedCode
  | maxFeePerGasBelowBaseFee
  | insufficientMaxFeePerGasForSenderBalance
  | insufficientSenderBalance
  | transactionNonceTooHigh
  | transactionNonceTooLow
  deriving DecidableEq, Repr

inductive ReturnSite where
  | validateStatic
  | validateSender
  | buyGasPremiumBelowBaseFee
  | buyGasReservedPaymentOverflow
  | buyGasMaximumFeeOverflow
  | buyGasValueOverflow
  | buyGasBlobMaximumFeeOverflow
  | buyGasBlobFeeCalculationOverflow
  | buyGasBlobFeeCapBelowBaseFee
  | buyGasBlobPaymentOverflow
  | buyGasInsufficientBalance
  | incrementNonceTooHigh
  | incrementNonceTooLow
  deriving DecidableEq, Repr

inductive ThrowSite where
  | recoverSender
  | createAccount
  | setNonceAbsentAccount
  | malformedBlobFields
  deriving DecidableEq, Repr

inductive Outcome where
  | continue
  | returned (site : ReturnSite) (error : ErrorType)
  | threw (site : ThrowSite)
  deriving DecidableEq, Repr

inductive Stage where
  | validateStatic
  | calculateEffectiveGasPrice
  | updateMetrics
  | recoverSenderIfNeeded
  | validateSender
  | buyGas
  | incrementNonce
  deriving DecidableEq, Repr

inductive LogEvent where
  | recoveryAttempt
  | recoveryChangedSender
  | senderAccountDoesNotExist
  | invalidContractSender
  | premiumBelowBaseFee
  | reservedPaymentOverflow
  | maximumFeeOverflow
  | valueOverflow
  | blobMaximumFeeOverflow
  | blobFeeCalculationOverflow
  | blobFeeCapBelowBaseFee
  | blobPaymentOverflow
  | insufficientBalance
  | nonceMismatch
  deriving DecidableEq, Repr

structure Options where
  raw : Nat
  deriving DecidableEq, Repr

structure Spec where
  eip658Enabled : Bool
  eip1559Enabled : Bool
  eip2780Enabled : Bool
  deriving DecidableEq, Repr

structure Header where
  baseFeePerGas : Nat
  deriving DecidableEq, Repr

structure Transaction where
  sender : Option Nat
  signaturePresent : Bool
  isMessageCall : Bool
  txType : Nat
  isSystem : Bool
  isServiceTransaction : Bool
  supports1559 : Bool
  isFree : Bool
  supportsBlobs : Bool
  gasLimit : Nat
  nonce : Nat
  maxFeePerGas : Nat
  maxPriorityFeePerGas : Nat
  value : Nat
  maxFeePerBlobGas : Option Nat
  blobVersionedHashes : Option (List (Option Nat))
  deriving DecidableEq, Repr

structure WorldState where
  suppliedSenderAccountExists : Bool
  effectiveSenderAccountExists : Bool
  recoveredSenderAccountExists : Bool
  effectiveSenderInvalidContract : Bool
  effectiveSenderBalance : Nat
  effectiveSenderNonce : Nat
  createAccountSucceeds : Bool
  standardMainnetWorldState : Bool
  deriving DecidableEq, Repr

structure BlobOracle where
  feePerBlobGasCalculationSucceeds : Bool
  blobBaseFeeCalculationSucceeds : Bool
  feePerBlobGas : Nat
  blobBaseFee : Nat
  deriving DecidableEq, Repr

structure LoggerState where
  debugEnabled : Bool
  warnEnabled : Bool
  deriving DecidableEq, Repr

structure Input where
  grammarSemanticsAuditPassed : Bool
  staticError : ErrorType
  tx : Transaction
  world : WorldState
  spec : Spec
  header : Header
  options : Options
  skipSenderCodeCheck : Bool
  preIntrinsicSender : Option Nat
  preIntrinsicSenderAccountExists : Bool
  preIntrinsicRecoveredSender : Option Nat
  preIntrinsicRecoveredSenderAccountExists : Bool
  recoveredSender : Option Nat
  blobOracle : BlobOracle
  logger : LoggerState
  deriving DecidableEq, Repr

structure TransactionState where
  sender : Option Nat
  deriving DecidableEq, Repr

structure PartialWorldState where
  effectiveSenderAccountExists : Bool
  effectiveSenderBalance : Nat
  effectiveSenderNonce : Nat
  accountCreated : Bool
  deriving DecidableEq, Repr

structure MetricsState where
  blockGasPrices : List Nat
  deriving DecidableEq, Repr

structure JournalState where
  resetCalled : Bool
  resetBlockChangesFalse : Bool
  deriving DecidableEq, Repr

structure Output where
  outcome : Outcome
  transaction : TransactionState
  world : PartialWorldState
  metrics : MetricsState
  journal : JournalState
  effectiveGasPrice : Nat
  opcodeGasPrice : Nat
  premiumPerGas : Nat
  senderReservedGasPayment : Nat
  blobBaseFee : Nat
  deleteCallerAccount : Bool
  logs : List LogEvent
  trace : List Stage
  deriving DecidableEq, Repr

structure AdmissionState where
  transaction : TransactionState
  world : PartialWorldState
  metrics : MetricsState
  journal : JournalState
  effectiveGasPrice : Nat
  opcodeGasPrice : Nat
  premiumPerGas : Nat
  senderReservedGasPayment : Nat
  blobBaseFee : Nat
  deleteCallerAccount : Bool
  logs : List LogEvent
  trace : List Stage
  deriving DecidableEq, Repr

inductive RecoveryResult where
  | continue (state : AdmissionState)
  | threw (state : AdmissionState) (site : ThrowSite)
  deriving DecidableEq, Repr

inductive RecoveryDecision where
  | fast
  | changed (sender : Option Nat)
  | same (createAccount : Bool)
  deriving DecidableEq, Repr

def finishRecovery (state : AdmissionState) : RecoveryResult :=
  if __RECOVERY_MISSING_SENDER_GUARD__ then .threw state .recoverSender else .continue state

def initialState (input : Input) : AdmissionState :=
  { transaction := { sender := input.tx.sender }
    world := { effectiveSenderAccountExists := input.world.effectiveSenderAccountExists
               effectiveSenderBalance := u256 input.world.effectiveSenderBalance
               effectiveSenderNonce := u64 input.world.effectiveSenderNonce
               accountCreated := false }
    metrics := { blockGasPrices := [] }
    journal := { resetCalled := false, resetBlockChangesFalse := false }
    effectiveGasPrice := 0
    opcodeGasPrice := 0
    premiumPerGas := 0
    senderReservedGasPayment := 0
    blobBaseFee := 0
    deleteCallerAccount := false
    logs := []
    trace := [] }

def appendStage (state : AdmissionState) (stage : Stage) : AdmissionState :=
  { state with trace := state.trace ++ [stage] }

def appendLog (state : AdmissionState) (event : LogEvent) : AdmissionState :=
  { state with logs := state.logs ++ [event] }

def replaceRecoveredSender (state : AdmissionState) (sender : Option Nat) : AdmissionState :=
  { state with transaction := { sender := sender } }

def createRecoveredAccount (input : Input) (state : AdmissionState) (deleteCallerAccount : Bool) : AdmissionState :=
__RECOVERY_ACCOUNT_BODY__

def finish (outcome : Outcome) (state : AdmissionState) : Output :=
  { outcome := outcome
    transaction := state.transaction
    world := state.world
    metrics := state.metrics
    journal := state.journal
    effectiveGasPrice := state.effectiveGasPrice
    opcodeGasPrice := state.opcodeGasPrice
    premiumPerGas := state.premiumPerGas
    senderReservedGasPayment := state.senderReservedGasPayment
    blobBaseFee := state.blobBaseFee
    deleteCallerAccount := state.deleteCallerAccount
    logs := state.logs
    trace := state.trace }

def skipValidation (options : Options) : Bool := hasFlag options.raw executionOptionSkipValidation
def restore (options : Options) : Bool := hasFlag options.raw executionOptionRestore
def warmup (options : Options) : Bool := hasFlag options.raw executionOptionWarmup
def recoveryCommit (input : Input) : Bool :=
  hasFlag input.options.raw executionOptionCommit || !input.spec.eip658Enabled
def shouldValidateGas (input : Input) : Bool :=
  !skipValidation input.options || u256 input.tx.maxFeePerGas != 0 || u256 input.tx.maxPriorityFeePerGas != 0
def preIntrinsicRecoveryApplies (input : Input) : Bool :=
  input.spec.eip2780Enabled && input.tx.isMessageCall && input.tx.signaturePresent &&
    (input.preIntrinsicSender.isNone || !input.preIntrinsicSenderAccountExists)
def postPreIntrinsicSender (input : Input) : Option Nat :=
  if preIntrinsicRecoveryApplies input then input.preIntrinsicRecoveredSender else input.preIntrinsicSender
def postPreIntrinsicSenderAccountExists (input : Input) : Bool :=
  if preIntrinsicRecoveryApplies input then input.preIntrinsicRecoveredSenderAccountExists
  else input.preIntrinsicSenderAccountExists
def recoveryCandidate (input : Input) : Option Nat :=
  if input.tx.signaturePresent && (!input.spec.eip2780Enabled || !input.tx.isMessageCall) then input.recoveredSender
  else input.tx.sender
def recoveryUsesChangedSenderFacts (input : Input) : Bool :=
  !input.world.suppliedSenderAccountExists && recoveryCandidate input != input.tx.sender
def blobHashCount (tx : Transaction) : Nat :=
  match tx.blobVersionedHashes with
  | none => 0
  | some hashes => hashes.length
def blobGas (tx : Transaction) : Nat := __BLOB_GAS_BODY__
def blobFeeCap (tx : Transaction) : Nat :=
  tx.maxFeePerBlobGas.getD 0
def blobFieldsCoherent (input : Input) : Prop :=
  input.tx.supportsBlobs = false ∨ input.tx.maxFeePerBlobGas.isSome
def transactionTypeCoherent (input : Input) : Prop :=
  input.tx.txType < 256 ∧
    input.tx.supports1559 = (input.tx.txType >= 2 && input.tx.txType != 126) ∧
    input.tx.supportsBlobs = (input.tx.txType == 3)
def transactionSnapshotCoherent (input : Input) : Prop :=
  input.tx.isFree = (input.tx.isSystem || input.tx.isServiceTransaction) ∧
    u64 input.tx.gasLimit = input.tx.gasLimit ∧ u64 input.tx.nonce = input.tx.nonce ∧
    u256 input.tx.maxFeePerGas = input.tx.maxFeePerGas ∧
    u256 input.tx.maxPriorityFeePerGas = input.tx.maxPriorityFeePerGas ∧ u256 input.tx.value = input.tx.value ∧
    (match input.tx.maxFeePerBlobGas with | none => True | some fee => u256 fee = fee) ∧ blobFieldsCoherent input
def blobProjectionCoherent (input : Input) : Prop :=
  blobHashCount input.tx < 2 ^ 31 ∧
    u256 input.blobOracle.feePerBlobGas = input.blobOracle.feePerBlobGas ∧ u256 input.blobOracle.blobBaseFee = input.blobOracle.blobBaseFee
def standardMainnetWorldStateCoherent (input : Input) : Prop :=
  input.world.standardMainnetWorldState = true ∧
    u256 input.world.effectiveSenderBalance = input.world.effectiveSenderBalance ∧
    u64 input.world.effectiveSenderNonce = input.world.effectiveSenderNonce ∧
    (recoveryUsesChangedSenderFacts input → (recoveryCandidate input).isSome ∨
      input.world.recoveredSenderAccountExists = false)
def headerSpecAndStaticCoherent (input : Input) : Prop :=
  u256 input.header.baseFeePerGas = input.header.baseFeePerGas ∧
    (input.staticError = .none → input.tx.sender.isSome ∧ input.tx.gasLimit >= 21000)
def preIntrinsicEip2780Coherent (input : Input) : Prop :=
  input.tx.sender = postPreIntrinsicSender input ∧
    input.world.suppliedSenderAccountExists = postPreIntrinsicSenderAccountExists input
def sourceGrammarSemanticsCoherent (input : Input) : Prop :=
  input.grammarSemanticsAuditPassed = true
def inputAdapterCoherent (input : Input) : Prop :=
  __INPUT_ADAPTER_COHERENCE_BODY__
def effectiveAbsentFacts (input : Input) : Prop :=
  input.world.effectiveSenderAccountExists = true ∨
    (u256 input.world.effectiveSenderBalance = 0 ∧ u64 input.world.effectiveSenderNonce = 0 ∧
      input.world.effectiveSenderInvalidContract = false)
def suppliedEffectiveFactsConsistent (input : Input) : Prop :=
  if recoveryUsesChangedSenderFacts input then
    input.world.effectiveSenderAccountExists = input.world.recoveredSenderAccountExists
  else
    input.world.effectiveSenderAccountExists = input.world.suppliedSenderAccountExists ∧ effectiveAbsentFacts input
def ordinaryStandardInput (input : Input) : Prop :=
  input.staticError = .none ∧ input.tx.sender.isSome ∧ input.tx.isFree = false ∧ input.tx.isSystem = false ∧
  input.options.raw < executionOptionsIntLimit ∧ input.options.raw != executionOptionSkipValidation ∧
  input.options.raw != executionOptionBuildUp ∧
  effectiveAbsentFacts input ∧ suppliedEffectiveFactsConsistent input ∧ inputAdapterCoherent input

def calculateEffectiveGasPrice (input : Input) : Nat :=
__EFFECTIVE_GAS_PRICE_BODY__

def tryCalculatePremiumPerGas (input : Input) : Option Nat :=
__PREMIUM_PER_GAS_BODY__

def classifyRecovery (input : Input) (state : AdmissionState) : RecoveryDecision :=
  let sender := state.transaction.sender
  if sender.isSome && input.world.suppliedSenderAccountExists then .fast else
  let recovered := recoveryCandidate input
  if sender != recovered then .changed recovered
  else .same (__RECOVERY_CREATE_GUARD__)

def applyRecoveryDecision (input : Input) (state : AdmissionState) (decision : RecoveryDecision) : RecoveryResult :=
  match decision with
  | .fast => .continue state
  | .changed recovered =>
    let state := if input.logger.debugEnabled then appendLog state .recoveryAttempt else state
    let state := replaceRecoveredSender state recovered
    let state := if input.logger.warnEnabled then appendLog state .recoveryChangedSender else state
    finishRecovery state
  | .same createAccount =>
    let state := if input.logger.debugEnabled then appendLog state .recoveryAttempt else state
    let state := appendLog state .senderAccountDoesNotExist
    let state :=
      if createAccount then
        if input.world.createAccountSucceeds then
          createRecoveredAccount input state (!recoveryCommit input || restore input.options)
        else state
      else state
    if !input.world.createAccountSucceeds && createAccount then
      .threw state .createAccount
    else finishRecovery state

def applyRecovery (input : Input) (state : AdmissionState) : RecoveryResult :=
  applyRecoveryDecision input state (classifyRecovery input state)

def rejectAfterCombinedGate (input : Input) (state : AdmissionState) (site : ReturnSite) (error : ErrorType) : Output :=
  let state := __COMBINED_RESET_BODY__
  finish (.returned site error) state

def reservePayment (gasLimit effectiveGasPrice : Nat) : CheckedUInt256 :=
__RESERVE_PAYMENT_BODY__

def debitEffectiveSender (state : AdmissionState) (amount : Nat) : AdmissionState :=
__DEBIT_BODY__

def nextNonce (validate : Bool) (nonce : Nat) : Nat :=
__NEXT_NONCE_BODY__

def finishPrefixContinuation (state : AdmissionState) : Output :=
__CONTINUE_BODY__

def finishBuyGas (input : Input) (state : AdmissionState) (balanceCheck : Nat) : Sum Output AdmissionState :=
  if __INSUFFICIENT_BALANCE_GUARD__ then
    if __WARMUP_INSUFFICIENT_BALANCE_GUARD__ then
      let warmCharge := Nat.min state.senderReservedGasPayment state.world.effectiveSenderBalance
      let state := debitEffectiveSender state warmCharge
      .inr state
    else
      .inl (rejectAfterCombinedGate input (appendLog state .insufficientBalance)
        .buyGasInsufficientBalance .insufficientMaxFeePerGasForSenderBalance)
  else
    let state := if __NON_ZERO_RESERVED_PAYMENT_GUARD__ then debitEffectiveSender state state.senderReservedGasPayment else state
    .inr state

mutual

def finishReservedPayment (input : Input) (state : AdmissionState)
    (reservedValue : Nat) (reservedOverflow : Bool) : Sum Output AdmissionState :=
  let state := { state with senderReservedGasPayment := reservedValue }
  if __RESERVED_PAYMENT_OVERFLOW_GUARD__ then
    .inl (rejectAfterCombinedGate input (appendLog state .reservedPaymentOverflow)
      .buyGasReservedPaymentOverflow .insufficientMaxFeePerGasForSenderBalance)
  else
    finishMaximumBalanceCheck input state

def finishMaximumBalanceCheck (input : Input) (state : AdmissionState) : Sum Output AdmissionState :=
  let maximumBalanceCheck :=
    if input.spec.eip1559Enabled && !input.tx.isFree then
      checkedMul256 (u64 input.tx.gasLimit) input.tx.maxFeePerGas
    else
      { value := state.senderReservedGasPayment, overflow := false }
  if __MAXIMUM_FEE_OVERFLOW_GUARD__ then
      .inl (rejectAfterCombinedGate input (appendLog state .maximumFeeOverflow)
        .buyGasMaximumFeeOverflow .insufficientMaxFeePerGasForSenderBalance)
  else
    finishBalanceAfterValue input state maximumBalanceCheck.value

def finishBalanceAfterValue (input : Input) (state : AdmissionState)
    (maximumBalance : Nat) : Sum Output AdmissionState :=
  let balanceAfterValue := checkedAdd256 maximumBalance input.tx.value
  if __VALUE_OVERFLOW_GUARD__ then
    .inl (rejectAfterCombinedGate input (appendLog state .valueOverflow)
      .buyGasValueOverflow .insufficientMaxFeePerGasForSenderBalance)
  else if !input.tx.supportsBlobs then
    finishBuyGas input state balanceAfterValue.value
  else if input.tx.maxFeePerBlobGas.isNone then
    .inl (finish (.threw .malformedBlobFields) state)
  else
    finishMaximumBlobFee input state balanceAfterValue.value

def finishMaximumBlobFee (input : Input) (state : AdmissionState)
    (balanceAfterValue : Nat) : Sum Output AdmissionState :=
  let maximumBlobFee := checkedMul256 (blobGas input.tx) (blobFeeCap input.tx)
  if __BLOB_MAXIMUM_FEE_MULTIPLY_OVERFLOW_GUARD__ then
    .inl (rejectAfterCombinedGate input (appendLog state .blobMaximumFeeOverflow)
      .buyGasBlobMaximumFeeOverflow .insufficientMaxFeePerGasForSenderBalance)
  else
    finishBalanceAfterBlobCap input state balanceAfterValue maximumBlobFee.value

def setBlobBaseFee (state : AdmissionState) (feeCalculationSucceeds : Bool) (baseFee : Nat) : AdmissionState :=
  if !feeCalculationSucceeds then
    { state with blobBaseFee := 0 }
  else { state with blobBaseFee := u256 baseFee }

def finishBalanceAfterBlobCap (input : Input) (state : AdmissionState)
    (balanceAfterValue maximumBlobFee : Nat) : Sum Output AdmissionState :=
  let balanceAfterBlobCap := checkedAdd256 balanceAfterValue maximumBlobFee
  if __BLOB_MAXIMUM_FEE_ADD_OVERFLOW_GUARD__ then
    .inl (rejectAfterCombinedGate input (appendLog state .blobMaximumFeeOverflow)
      .buyGasBlobMaximumFeeOverflow .insufficientMaxFeePerGasForSenderBalance)
  else
    finishBlobFeeCalculation input state balanceAfterBlobCap.value

def finishBlobFeeCalculation (input : Input) (state : AdmissionState)
    (balanceAfterBlobCap : Nat) : Sum Output AdmissionState :=
  let state := setBlobBaseFee state input.blobOracle.feePerBlobGasCalculationSucceeds input.blobOracle.blobBaseFee
  if __BLOB_FEE_CALCULATION_FAILURE_GUARD__ then
    .inl (rejectAfterCombinedGate input (appendLog state .blobFeeCalculationOverflow)
      .buyGasBlobFeeCalculationOverflow .insufficientMaxFeePerGasForSenderBalance)
  else if __BLOB_FEE_CAP_BELOW_BASE_FEE_GUARD__ then
    .inl (rejectAfterCombinedGate input (appendLog state .blobFeeCapBelowBaseFee)
      .buyGasBlobFeeCapBelowBaseFee .insufficientSenderBalance)
  else
    finishReservedWithBlob input state balanceAfterBlobCap

def finishReservedWithBlob (input : Input) (state : AdmissionState)
    (balanceAfterBlobCap : Nat) : Sum Output AdmissionState :=
  let reservedWithBlob := checkedAdd256 state.senderReservedGasPayment state.blobBaseFee
  let state := { state with senderReservedGasPayment := reservedWithBlob.value }
  if __BLOB_PAYMENT_OVERFLOW_GUARD__ then
    .inl (rejectAfterCombinedGate input (appendLog state .blobPaymentOverflow)
      .buyGasBlobPaymentOverflow .insufficientMaxFeePerGasForSenderBalance)
  else
    finishBuyGas input state balanceAfterBlobCap

def finishGasPurchase (input : Input) (state : AdmissionState) : Sum Output AdmissionState :=
  let reservedPayment := reservePayment input.tx.gasLimit state.effectiveGasPrice
  finishReservedPayment input state reservedPayment.value reservedPayment.overflow

end

def buyGas (input : Input) (state : AdmissionState) : Sum Output AdmissionState :=
  let state := { state with premiumPerGas := 0, senderReservedGasPayment := 0, blobBaseFee := 0 }
  let state := appendStage state .buyGas
  if shouldValidateGas input then
    let premium := tryCalculatePremiumPerGas input
    let state := { state with premiumPerGas := premium.getD 0 }
    if __PREMIUM_BELOW_BASE_FEE_GUARD__ then
      .inl (rejectAfterCombinedGate input (appendLog state .premiumBelowBaseFee)
        .buyGasPremiumBelowBaseFee .maxFeePerGasBelowBaseFee)
    else
      finishGasPurchase input state
  else
    finishGasPurchase input state

def incrementNonce (input : Input) (state : AdmissionState) : Sum Output AdmissionState :=
  let state := appendStage state .incrementNonce
  let validate := !skipValidation input.options
  if __NONCE_MISMATCH_GUARD__ then
    if u64 input.tx.nonce > state.world.effectiveSenderNonce then
      .inl (rejectAfterCombinedGate input (appendLog state .nonceMismatch)
        .incrementNonceTooHigh .transactionNonceTooHigh)
    else
      .inl (rejectAfterCombinedGate input (appendLog state .nonceMismatch)
        .incrementNonceTooLow .transactionNonceTooLow)
  else
    let newNonce := nextNonce validate state.world.effectiveSenderNonce
    if !state.world.effectiveSenderAccountExists then
      .inl (finish (.threw .setNonceAbsentAccount) state)
    else
      .inr { state with world := { state.world with effectiveSenderNonce := newNonce } }

def finishAfterGasCharge (input : Input) (result : Sum Output AdmissionState) : Output :=
  match result with
  | .inl output => output
  | .inr state =>
    match incrementNonce input state with
    | .inl output => output
    | .inr state => finishPrefixContinuation state

def continueAfterRecovery (input : Input) (state : AdmissionState) : Output :=
  let state := appendStage state .validateSender
  if __INVALID_CONTRACT_SENDER_GUARD__ then
    rejectAfterCombinedGate input (appendLog state .invalidContractSender)
      .validateSender .senderHasDeployedCode
  else
    finishAfterGasCharge input (buyGas input state)

def finishAfterRecovery (input : Input) (result : RecoveryResult) : Output :=
  match result with
  | .threw state site => finish (.threw site) state
  | .continue state => continueAfterRecovery input state

def run (input : Input) : Output :=
  let state := appendStage (initialState input) .validateStatic
  if __STATIC_FAILURE_GUARD__ then
    finish (.returned .validateStatic input.staticError) state
  else
    let state := appendStage state .calculateEffectiveGasPrice
    let effectiveGasPrice := calculateEffectiveGasPrice input
    let state := { state with effectiveGasPrice := effectiveGasPrice, opcodeGasPrice := effectiveGasPrice }
    let state := appendStage state .updateMetrics
    let state :=
      if __METRICS_GATE__ then
        { state with metrics := { blockGasPrices := [effectiveGasPrice] } }
      else state
    let state := appendStage state .recoverSenderIfNeeded
    finishAfterRecovery input (applyRecovery input state)

def orderedStageIds : List String :=
  __ORDERED_STAGE_IDS__

def orderedBranchIds : List String :=
  __ORDERED_BRANCH_IDS__

end OrdinaryStatefulAdmissionPrefixExtractor.Generated
""";
}
