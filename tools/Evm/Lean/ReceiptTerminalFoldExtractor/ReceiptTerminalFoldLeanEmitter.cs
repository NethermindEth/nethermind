// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.Evm.Lean.ReceiptTerminalFoldExtractor;

internal static class ReceiptTerminalFoldLeanEmitter
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    internal static byte[] Emit(
        IrDocument document,
        string extractorVersion,
        string compilerVersion,
        string sourcePath,
        string sourceHash,
        string irHash)
    {
        EmissionPlan plan = BuildPlan(document);

        string source = """
            -- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
            -- SPDX-License-Identifier: LGPL-3.0-only

            -- This file is generated. Do not edit.
            -- Extractor version: __EXTRACTOR_VERSION__
            -- Roslyn compiler version: __COMPILER_VERSION__
            -- Production source: __SOURCE_PATH__
            -- Production source SHA-256: __SOURCE_HASH__
            -- Canonical typed terminal-fold IR SHA-256: __IR_HASH__

            import Eip803x.Generated.BlockReceiptGasAccountingKernel

            namespace ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel

            structure BytesOracle where
              id : Nat
              deriving DecidableEq, Repr

            structure LogOracle where
              id : Nat
              deriving DecidableEq, Repr

            structure ErrorOracle where
              id : Nat
              deriving DecidableEq, Repr

            structure EvmExceptionOracle where
              id : Nat
              deriving DecidableEq, Repr

            structure HashOracle where
              id : Nat
              deriving DecidableEq, Repr

            structure AddressOracle where
              id : Nat
              deriving DecidableEq, Repr

            structure PriceOracle where
              id : Nat
              deriving DecidableEq, Repr

            inductive Status where
            | success
            | failure
              deriving DecidableEq, Repr

            inductive TransactionResult where
            | ok
            | evmException (exceptionType : EvmExceptionOracle) (substateError : Option ErrorOracle)
              deriving DecidableEq, Repr

            inductive Event where
            | gasMutation
            | receiptAppend
            | nestedTracerForward
            | currentTracerForward
              deriving DecidableEq, Repr

            structure Gas where
              spentGas : Nat
              operationGas : Nat
              blockGas : Nat
              blockStateGas : Nat
              maxUsedGas : Nat
              gasRefund : Nat
              deriving DecidableEq, Repr

            def effectiveBlockGas (gas : Gas) : Nat :=
            __EFFECTIVE_BLOCK_GAS__

            def statusCode : Status → Nat
            __STATUS_CODE__

            structure TransactionInput where
              txType : Nat
              to : Option AddressOracle
              sender : Option AddressOracle
              txHash : Option HashOracle
              effectiveGasPrice : PriceOracle
              deriving DecidableEq, Repr

            def isContractCreation (transaction : TransactionInput) : Bool :=
              match transaction.to with
              | none => true
              | some _ => false

            structure BlockInput where
              hash : Option HashOracle
              number : Nat
              baseFeePerGas : Nat
              deriving DecidableEq, Repr

            structure GasTotals where
              executionGas : Nat
              stateGas : Nat
              deriving DecidableEq, Repr

            structure Receipt where
            __RECEIPT_FIELDS__
              deriving DecidableEq, Repr

            structure State where
              receipts : List Receipt
              gasHistory : List GasTotals
              cumulativeReceiptGas : Nat
              headerGasUsed : Nat
              parallel : Bool
              currentIndex : Nat
              deriving DecidableEq, Repr

            structure Trace where
              state : State
              events : List Event
              forwardedOutput : BytesOracle
              forwardedLogs : List LogOracle
              forwardedError : Option ErrorOracle
              forwardedStateRoot : Option HashOracle
              deriving DecidableEq, Repr

            structure FinalizationObservation where
              trace : Trace
              result : TransactionResult
              deriving DecidableEq, Repr

            def previousTotals : List GasTotals → Nat × Nat
            | [] => (0, 0)
            | [last] => (last.executionGas, last.stateGas)
            | _ :: tail => previousTotals tail

            def lastReceiptGas : List Receipt → Nat
            | [] => 0
            | [last] => last.gasUsedTotal
            | _ :: tail => lastReceiptGas tail

            def updateCumulativeGasTracking (state : State) (gas : Gas) : State :=
            __UPDATE_BODY__

            def guardedBlockGasUsed (gas : Gas) : Nat :=
              if gas.blockGas > 0 then effectiveBlockGas gas else 0

            def guardedExecutionGasUsed (gas : Gas) : Nat :=
              if gas.blockGas > 0 then gas.operationGas else 0

            def guardedStateGasUsed (gas : Gas) : Nat :=
              if gas.blockStateGas > 0 then gas.blockStateGas else 0

            def buildReceipt
                (state : State)
                (block : BlockInput)
                (transaction : TransactionInput)
                (recipient : AddressOracle)
                (gas : Gas)
                (status : Status)
                (logs : List LogOracle)
                (stateRoot : Option HashOracle) : State × Receipt :=
            __BUILD_RECEIPT_BODY__

            def buildFailedReceipt
                (state : State)
                (block : BlockInput)
                (transaction : TransactionInput)
                (recipient : AddressOracle)
                (gas : Gas)
                (error : Option ErrorOracle)
                (stateRoot : Option HashOracle) : State × Receipt :=
            __BUILD_FAILED_RECEIPT_BODY__

            def emptyBytes : BytesOracle := { id := 0 }

            __FORWARDING_EVENTS__

            def markAsSuccess
                (state : State)
                (block : BlockInput)
                (transaction : TransactionInput)
                (recipient : AddressOracle)
                (gas : Gas)
                (output : BytesOracle)
                (logs : List LogOracle)
                (stateRoot : Option HashOracle)
                (nestedTracer currentTxTracerIsTracingReceipt : Bool) : Trace :=
            __MARK_SUCCESS_BODY__

            def markAsFailed
                (state : State)
                (block : BlockInput)
                (transaction : TransactionInput)
                (recipient : AddressOracle)
                (gas : Gas)
                (output : BytesOracle)
                (error : Option ErrorOracle)
                (stateRoot : Option HashOracle)
                (nestedTracer currentTxTracerIsTracingReceipt : Bool) : Trace :=
            __MARK_FAILED_BODY__

            def takeSnapshot (state : State) : Nat :=
            __TAKE_SNAPSHOT_BODY__

            def restorePrefix (state : State) (snapshot : Nat) : State :=
            __RESTORE_BODY__

            def normalPrefix (state : State) (snapshot : Nat) : Prop :=
              snapshot ≤ state.receipts.length ∧
              state.receipts.length = state.gasHistory.length

            structure FinalizeInput where
            __FINALIZE_FIELDS__
              deriving DecidableEq, Repr

            __FAILURE_OUTPUT__

            __FAILURE_ERROR__

            __TRANSACTION_RESULT__

            def finalizeTransaction
                (state : State)
                (block : BlockInput)
                (transaction : TransactionInput)
                (recipient : AddressOracle)
                (gas : Gas)
                (input : FinalizeInput)
                (nestedTracer currentTxTracerIsTracingReceipt : Bool) : FinalizationObservation :=
            __FINALIZE_TRANSACTION__

            end ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel
            """;

        source = source
            .Replace("__UPDATE_BODY__", plan.UpdateBody, StringComparison.Ordinal)
            .Replace("__BUILD_RECEIPT_BODY__", plan.BuildReceiptBody, StringComparison.Ordinal)
            .Replace("__BUILD_FAILED_RECEIPT_BODY__", plan.BuildFailedReceiptBody, StringComparison.Ordinal)
            .Replace("__MARK_SUCCESS_BODY__", plan.MarkSuccessBody, StringComparison.Ordinal)
            .Replace("__MARK_FAILED_BODY__", plan.MarkFailedBody, StringComparison.Ordinal)
            .Replace("__TAKE_SNAPSHOT_BODY__", plan.TakeSnapshotBody, StringComparison.Ordinal)
            .Replace("__RESTORE_BODY__", plan.RestoreBody, StringComparison.Ordinal)
            .Replace("__RECEIPT_FIELDS__", plan.ReceiptFields, StringComparison.Ordinal)
            .Replace("__RECEIPT_PROJECTION__", plan.ReceiptProjection, StringComparison.Ordinal)
            .Replace("__EFFECTIVE_BLOCK_GAS__", plan.EffectiveBlockGas, StringComparison.Ordinal)
            .Replace("__STATUS_CODE__", plan.StatusCode, StringComparison.Ordinal)
            .Replace("__FORWARDING_EVENTS__", plan.ForwardingEvents, StringComparison.Ordinal)
            .Replace("__FINALIZE_FIELDS__", plan.FinalizeFields, StringComparison.Ordinal)
            .Replace("__FAILURE_OUTPUT__", plan.FailureOutput, StringComparison.Ordinal)
            .Replace("__FAILURE_ERROR__", plan.FailureError, StringComparison.Ordinal)
            .Replace("__TRANSACTION_RESULT__", plan.TransactionResult, StringComparison.Ordinal)
            .Replace("__FINALIZE_TRANSACTION__", plan.FinalizeTransaction, StringComparison.Ordinal)
            .Replace("__EXTRACTOR_VERSION__", extractorVersion, StringComparison.Ordinal)
            .Replace("__COMPILER_VERSION__", compilerVersion, StringComparison.Ordinal)
            .Replace("__SOURCE_PATH__", sourcePath, StringComparison.Ordinal)
            .Replace("__SOURCE_HASH__", sourceHash, StringComparison.Ordinal)
            .Replace("__IR_HASH__", irHash, StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        return Utf8WithoutBom.GetBytes(source + "\n");
    }

    internal static void ValidateIr(IrDocument document) =>
        _ = BuildPlan(document);

    private static EmissionPlan BuildPlan(IrDocument document)
    {
        if (document is null || document.Operations is null || document.Mappings is null || document.Inputs is null || document.Lowering is null ||
            document.Operations.Any(static operation => operation is null || operation.Steps is null || operation.Body is null ||
                operation.Steps.Any(static step => step is null || step.Children is null) ||
                operation.Body.Invocations is null || operation.Body.Assignments is null || operation.Body.Guards is null ||
                operation.Body.Invocations.Any(static invocation => invocation is null || invocation.Arguments is null || invocation.Expression is null) ||
                operation.Body.Assignments.Any(static assignment => assignment is null || assignment.Value is null) ||
                operation.Body.Guards.Any(static guard => guard is null || guard.Children is null)) ||
            document.Mappings.Any(static mapping => mapping is null) ||
            document.Lowering.StatusCode is null || document.Lowering.Result is null ||
            document.Lowering.EffectiveBlockGas is null || document.Lowering.StatusCode.Failure is null ||
            document.Lowering.StatusCode.Success is null || document.Lowering.Result.Condition is null ||
            document.Lowering.Result.ExceptionType is null || document.Lowering.Result.ExceptionError is null ||
            document.Lowering.Result.OkResult is null || document.Lowering.ReceiptFields is null ||
            document.Lowering.Finalization is null ||
            document.Lowering.ReceiptFields.Any(static binding => binding is null || binding.Expression is null) ||
            document.Lowering.Finalization.Any(static binding => binding is null))
        {
            throw new ExtractionException("Receipt-terminal emission received a null typed IR section.");
        }

        ValidateTypedFacts(document);

        string[] expectedOperations =
        [
            "markAsSuccess", "markAsFailed", "buildFailedReceipt", "buildReceipt",
            "updateCumulativeGasTracking", "takeSnapshot", "restore", "finalizeTransaction",
        ];
        if (!document.Operations.Select(static operation => operation.Name).SequenceEqual(expectedOperations, StringComparer.Ordinal))
        {
            throw new ExtractionException("Receipt-terminal emission operation order is not admitted.");
        }

        OperationDescriptor success = Operation(document, "markAsSuccess");
        OperationDescriptor failure = Operation(document, "markAsFailed");
        OperationDescriptor failedReceipt = Operation(document, "buildFailedReceipt");
        OperationDescriptor receipt = Operation(document, "buildReceipt");
        OperationDescriptor update = Operation(document, "updateCumulativeGasTracking");
        OperationDescriptor snapshot = Operation(document, "takeSnapshot");
        OperationDescriptor restore = Operation(document, "restore");
        OperationDescriptor finalize = Operation(document, "finalizeTransaction");
        RequireOperation(success, "terminal", ["recipient", "gasSpent", "output", "logs", "stateRoot"],
            ["receipt", "gasHistory", "headerGasUsed", "cumulativeReceiptGas"],
            ["gasMutation", "receiptAppend", "nestedTracerForward", "currentTracerForward"],
            ["parallel=false header guard"], ["output, logs, stateRoot forwarded; receipt has no output"],
            ["nested tracer totality"]);
        RequireOperation(failure, "terminal", ["recipient", "gasSpent", "output", "error", "stateRoot"],
            ["receipt", "gasHistory", "headerGasUsed", "cumulativeReceiptGas"],
            ["gasMutation", "emptyFailureLogs", "errorAssignment", "receiptAppend", "nestedTracerForward", "currentTracerForward"],
            ["parallel=false header guard"], ["output, error, stateRoot forwarded; output is not stored in receipt"],
            ["nested tracer totality"]);
        RequireOperation(failedReceipt, "receipt", ["recipient", "gasSpent", "error", "stateRoot"],
            ["failureReceipt"], ["delegateBuildReceipt", "errorAssignment"], [], ["logs=[]; error=error"], []);
        RequireOperation(receipt, "receipt", ["recipient", "gasConsumed", "statusCode", "logs", "stateRoot", "CurrentTx", "Block"],
            ["receipt"],
            ["updateGas", "receiptFieldProjection", "diagnosticBlockGasGuard", "diagnosticStateGasGuard"], [],
            ["all consensus and diagnostic fields listed in receiptFields; typed oracles for hashes/addresses/price; guarded diagnostics retain zero defaults"],
            ["Bloom/RLP/root"]);
        RequireOperation(update, "gas", ["previousBlockTotals", "cumulativeReceiptGas", "gasConsumed"],
            ["gasHistory", "headerGasUsed", "cumulativeReceiptGas"],
            ["kernelAccumulate", "historyAppend", "sequentialHeaderUpdate", "receiptCounterUpdate"], ["!parallel"],
            ["unchecked ulong additions delegated to the pinned kernel"], ["parallel/BAL"]);
        RequireOperation(snapshot, "snapshot", ["receipts"], ["snapshotPosition"], ["receiptCount"], [],
            ["position is a valid normal prefix"], ["invalid snapshot"]);
        RequireOperation(restore, "snapshot", ["snapshotPosition", "receipts", "gasHistory"],
            ["receipts", "gasHistory", "headerGasUsed", "cumulativeReceiptGas"],
            ["suffixRemoval", "kernelFromTotals", "headerRestore", "receiptCounterRestore"], ["numToRemove > 0"],
            ["valid synchronized prefix only"], ["invalid snapshot, SetReceipt, parallel/BAL"]);
        RequireOperation(finalize, "caller", ["statusCode", "substate", "executingAccount", "spentGas", "tracer"],
            ["terminalTracerCall", "TransactionResult", "FinalizationObservation"],
            ["normalFinalize", "eip8037AccountReapBeforeTerminal", "receiptGate", "failureOutputGuard", "failureErrorPrecedence", "successLogsProjection", "terminalCall", "resultReturn"],
            ["normally returning; exact base BlockReceiptsTracer.IsTracingReceipt = true"],
            ["single substate.Output forwarded on success and ShouldRevert failure; non-revert failure forwards empty; error prefers substate.Error then EvmExceptionType.FastToString; BuildUp plus EIP-8037 ReapEmptyAccounts is admitted before the receipt gate but remains outside the fold; generated observation retains the returned result"],
            ["VM/world-state semantics"]);
        RequireOrdered(success.OrderedEffects, ["gasMutation", "receiptAppend", "nestedTracerForward", "currentTracerForward"], "success emission effects");
        RequireOrdered(failure.OrderedEffects,
            ["gasMutation", "emptyFailureLogs", "errorAssignment", "receiptAppend", "nestedTracerForward", "currentTracerForward"],
            "failure emission effects");

        RequireMapping(document, "gasConsumed.EffectiveBlockGas", "block.executionGas",
            "gasConsumed.BlockGas > 0 || gasConsumed.BlockStateGas > 0 ? BlockGas : SpentGas");
        RequireMapping(document, "gasConsumed.BlockStateGas", "block.stateGas", "state-dimension cumulative counter");
        RequireMapping(document, "gasConsumed.SpentGas", "receipt.paidGas", "post-refund cumulative receipt counter");
        RequireMapping(document, "FinalizeTransaction.substate.Error", "terminal.error",
            "takes precedence over EvmExceptionType.FastToString");
        RequireMapping(document, "FinalizeTransaction.substate.EvmExceptionType", "finalization.result",
            "None returns TransactionResult.Ok; non-None returns TransactionResult.EvmException");
        RequireMapping(document, "FinalizeTransaction.substate.SubstateError", "finalization.result.error",
            "the returned EvmException carries SubstateError");
        RequireMapping(document, "FinalizeTransaction.substate.Output", "terminal.output",
            "the same output is forwarded on success and ShouldRevert failure; non-revert failure forwards empty");
        RequireMapping(document, "buildReceipt.gasConsumed.EffectiveBlockGas", "receipt.BlockGasUsed",
            "BlockGas > 0 assigns the effective block gas; otherwise the nonnullable receipt field remains zero");
        RequireMapping(document, "buildReceipt.gasConsumed.OperationGas", "receipt.ExecutionGasUsed",
            "BlockGas > 0 assigns operation gas; otherwise the nonnullable receipt field remains zero");
        RequireMapping(document, "buildReceipt.gasConsumed.BlockStateGas", "receipt.StorageGasUsed",
            "BlockStateGas > 0 assigns state gas; otherwise the nonnullable receipt field remains zero");
        RequireMapping(document, "receipt.Logs", "terminal.logs", "success logs; failure BuildFailedReceipt supplies []");
        RequireMapping(document, "receipt.ReturnValue", "excluded",
            "never assigned by BuildReceipt; output is forwarding payload only");
        RequireMapping(document, "transaction.IsContractCreation", "receipt.Recipient/ContractAddress",
            "production IsContractCreation is To-is-null; model derives the same relation and maps creation recipient to ContractAddress, call recipient to Recipient");
        RequireMapping(document, "transaction.CalculateEffectiveGasPrice", "receipt.EffectiveGasPrice",
            "typed diagnostic price oracle");
        RequireMapping(document, "BlockReceiptsTracer.IsTracingReceipt", "FinalizeTransaction.receiptGate",
            "exact base BlockReceiptsTracer.IsTracingReceipt is true; the normally returning caller gate is discharged");
        RequireMapping(document, "BlockReceiptsTracer.TakeSnapshot", "snapshotPosition", "receipt count");
        RequireMapping(document, "BlockReceiptsTracer.Restore", "prefixState", "retained receipt and block-gas prefix");

        return new(
            BuildUpdateBody(document),
            BuildReceiptBody(document),
            BuildFailedReceiptBody(document),
            BuildMarkSuccessBody(document),
            BuildMarkFailedBody(document),
            BuildTakeSnapshotBody(document),
            BuildRestoreBody(document),
            BuildFinalizeFields(document.Inputs.FinalizeFields),
            BuildReceiptFields(document.Inputs.ReceiptFields),
            BuildReceiptProjection(document),
            BuildEffectiveBlockGas(document),
            BuildStatusCode(document),
            BuildForwardingEvents(document, success),
            BuildFailureOutput(document),
            BuildFailureError(document),
            BuildTransactionResult(document),
            BuildFinalizeTransaction(document));
    }

    private static void ValidateTypedFacts(IrDocument document)
    {
        foreach (OperationDescriptor operation in document.Operations)
        {
            ValidateControlAndEffects(operation);
            foreach (SemanticInvocation invocation in operation.Body.Invocations)
            {
                if (invocation is null || invocation.Arguments is null || invocation.Expression is null)
                {
                    throw new ExtractionException($"Receipt-terminal operation {operation.Name} has an incomplete invocation fact.");
                }

                if (string.IsNullOrWhiteSpace(invocation.SymbolId) || string.IsNullOrWhiteSpace(invocation.ReturnType) ||
                    string.IsNullOrWhiteSpace(invocation.ReceiverSymbolId) || string.IsNullOrWhiteSpace(invocation.ReceiverTypeName))
                {
                    throw new ExtractionException($"Receipt-terminal invocation {invocation.Method} has incomplete Roslyn identity " +
                        $"(symbol={invocation.SymbolId ?? "<null>"}, return={invocation.ReturnType ?? "<null>"}, " +
                        $"receiverSymbol={invocation.ReceiverSymbolId ?? "<null>"}, receiverType={invocation.ReceiverTypeName ?? "<null>"}, " +
                        $"expression={invocation.Expression.Text}).");
                }

                SemanticExpression member = invocation.Expression.Children.FirstOrDefault() ??
                    throw new ExtractionException($"Receipt-terminal invocation {invocation.Method} has no member expression.");
                SemanticExpression? receiver = member.Children.FirstOrDefault();
                bool implicitReceiver = receiver is null && member.Kind == "IdentifierName" &&
                    member.SymbolId?.StartsWith("Method:", StringComparison.Ordinal) == true;
                if (invocation.SymbolId != invocation.Expression.SymbolId ||
                    invocation.ReturnType != invocation.Expression.TypeName ||
                    (!implicitReceiver && (receiver is null || invocation.ReceiverSymbolId != receiver.SymbolId ||
                        invocation.ReceiverTypeName != receiver.TypeName)) ||
                    (implicitReceiver && (!invocation.SymbolId.Contains($".{member.Text}:", StringComparison.Ordinal) ||
                        !invocation.ReceiverSymbolId.Contains(invocation.ReceiverTypeName, StringComparison.Ordinal))))
                {
                    throw new ExtractionException($"Receipt-terminal invocation {invocation.Method} has detached Roslyn identity fields.");
                }

                RequireTypedExpression(invocation.Expression, operation.Name, requireSymbol: true);
                foreach (SemanticExpression argument in invocation.Arguments)
                {
                    RequireTypedExpression(argument, operation.Name, requireSymbol: false);
                }
                if (invocation.Guard is SemanticExpression guard)
                {
                    RequireTypedExpression(guard, $"{operation.Name} invocation guard", requireSymbol: false);
                }
            }

            foreach (SemanticAssignment assignment in operation.Body.Assignments)
            {
                if (assignment is null || assignment.Value is null || assignment.TargetExpression is null)
                {
                    throw new ExtractionException($"Receipt-terminal operation {operation.Name} has an incomplete assignment fact.");
                }

                RequireTypedExpression(assignment.TargetExpression, operation.Name,
                    assignment.TargetExpression.Kind is "SimpleMemberAccessExpression" or "MemberAccessExpression");
                RequireTypedExpression(assignment.Value, operation.Name, requireSymbol: false);
                if (assignment.Guard is not null)
                {
                    RequireTypedExpression(assignment.Guard, operation.Name, requireSymbol: false);
                }
            }

            foreach (SemanticStep guard in operation.Body.Guards)
            {
                ValidateTypedStep(guard, operation.Name);
            }

            if (operation.Body.ReturnExpression is not null)
            {
                RequireTypedExpression(operation.Body.ReturnExpression, operation.Name, requireSymbol: false);
            }
        }

        RequireTypedExpression(document.Lowering.EffectiveBlockGas, "lowering.effectiveBlockGas", requireSymbol: false);
        RequireTypedExpression(document.Lowering.StatusCode.Failure, "lowering.status.failure", requireSymbol: false);
        RequireTypedExpression(document.Lowering.StatusCode.Success, "lowering.status.success", requireSymbol: false);
        foreach (LoweringBinding binding in document.Lowering.ReceiptFields.Concat(document.Lowering.Finalization))
        {
            if (binding.Expression is null)
            {
                throw new ExtractionException($"Receipt-terminal lowering {binding.Target} has no source expression.");
            }

            RequireTypedExpression(binding.Expression, $"lowering.{binding.Target}", requireSymbol: false);
            if (binding.Guard is not null)
            {
                RequireTypedExpression(binding.Guard, $"lowering.{binding.Target}.guard", requireSymbol: false);
            }
        }

        RequireTypedExpression(document.Lowering.Result.Condition, "lowering.result.condition", requireSymbol: false);
        RequireTypedExpression(document.Lowering.Result.ExceptionType, "lowering.result.exceptionType", requireSymbol: false);
        RequireTypedExpression(document.Lowering.Result.ExceptionError, "lowering.result.exceptionError", requireSymbol: false);
        RequireTypedExpression(document.Lowering.Result.OkResult, "lowering.result.ok", requireSymbol: false);
    }

    internal static void ValidateControlAndEffects(OperationDescriptor operation)
    {
        ValidateBranchPaths(operation);
        ReceiptTerminalFoldControlFlow.Validate(operation.Member, operation.Steps);
        ReceiptTerminalFoldEffects.Validate(operation.Member, operation.Steps, operation.Body);
    }

    private static void ValidateBranchPaths(OperationDescriptor operation)
    {
        List<SemanticStep> sourceGuards = [];
        foreach (SemanticStep step in operation.Steps) Visit(step);
        if (sourceGuards.Count != operation.Body.Guards.Length ||
            !sourceGuards.Zip(operation.Body.Guards).All(pair => SameStep(pair.First, pair.Second)))
            throw new ExtractionException($"RECEIPT_BRANCH_PATH: {operation.Name} has detached conditional trees.");

        foreach (SemanticInvocation invocation in operation.Body.Invocations)
        {
            Validate(invocation.Position, invocation.Branches, invocation.Method, owner =>
                SourceExpressions(owner.Expression).Any(expression => SourceInvocationMatches(expression, invocation)));
            if (operation.Name == "finalizeTransaction")
            {
                if (invocation.Receiver == "tracer" && invocation.Method is "MarkAsFailed" or "MarkAsSuccess")
                    RequireFinalizeBranches(invocation.Branches, invocation.Method == "MarkAsFailed", 2, invocation.Method);
                if (invocation.Receiver == "substate.EvmExceptionType" && invocation.Method == "FastToString")
                    RequireFinalizeBranches(invocation.Branches, true, 3, invocation.Method);
            }
        }
        foreach (SemanticAssignment assignment in operation.Body.Assignments)
        {
            Validate(assignment.Position, assignment.Branches, assignment.Target, owner =>
                assignment.Form == "initializer"
                    ? owner.Kind == "LocalDeclarationStatement" && owner.Expression?.Start == assignment.Position &&
                      SameNullableExpression(owner.DeclaredTarget, assignment.TargetExpression)
                    : SourceExpressions(owner.Expression).Any(expression => expression.Start == assignment.Position &&
                        expression.Kind == "SimpleAssignmentExpression" && expression.Children.Length == 2 &&
                        SameNullableExpression(expression.Children[0], assignment.TargetExpression)));
            if (operation.Name != "finalizeTransaction") continue;
            if (assignment.Target is "output" or "logs" or "error")
                RequireFinalizeBranches(assignment.Branches, assignment.Target != "logs",
                    assignment.Target == "error" && assignment.Form == "assignment" ? 3 : 2, assignment.Target);
            if (assignment.Target == "stateRoot")
            {
                RequireFinalizeBranches(assignment.Branches, null, assignment.Form == "initializer" ? 1 : 2, assignment.Target);
                if (assignment.Form == "assignment" && assignment.Branches[1].Condition.Text != "!spec.IsEip658Enabled")
                    throw new ExtractionException("RECEIPT_BRANCH_POLARITY: state-root assignment lost its legacy fork branch.");
            }
        }

        void Visit(SemanticStep step)
        {
            if (step is null || step.Children is null || step.Start < 0 || step.End <= step.Start ||
                step.Children.Any(child => child is null || child.Start < step.Start || child.End > step.End))
                throw new ExtractionException($"RECEIPT_BRANCH_PATH: {operation.Name} has invalid statement ranges.");
            if (step.Kind == "IfStatement")
            {
                if (step.Expression is null || step.Children.Length is < 1 or > 2 ||
                    step.Children[0].Kind == "ElseClause" ||
                    step.Children.Length == 2 && (step.Children[1].Kind != "ElseClause" || step.Children[1].Children.Length != 1))
                    throw new ExtractionException($"RECEIPT_BRANCH_PATH: {operation.Name} has malformed conditional arms.");
                sourceGuards.Add(step);
            }
            foreach (SemanticStep child in step.Children) Visit(child);
        }

        void Validate(int position, SemanticBranch[] branches, string label, Func<SemanticStep, bool> matchesSource)
        {
            if (branches is null || branches.Any(branch => branch is null || branch.Condition is null))
                throw new ExtractionException($"RECEIPT_BRANCH_PATH: {operation.Name}/{label} has an incomplete path.");
            foreach (SemanticBranch branch in branches)
                RequireTypedExpression(branch.Condition, $"{operation.Name}/{label} branch", requireSymbol: false);
            (SemanticBranch[] fromTree, SemanticStep owner) = BranchesAt(operation.Steps, position, [], operation.Name);
            if (!matchesSource(owner))
                throw new ExtractionException($"RECEIPT_BRANCH_PATH: {operation.Name}/{label} has no matching source effect at {position}.");
            if (branches.Length != fromTree.Length || !branches.Zip(fromTree).All(pair =>
                    pair.First.Position == pair.Second.Position && pair.First.WhenTrue == pair.Second.WhenTrue &&
                    SameSourceExpression(pair.First.Condition, pair.Second.Condition)))
                throw new ExtractionException($"RECEIPT_BRANCH_PATH: {operation.Name}/{label} disagrees with its source arm.");
            if (operation.Name != "finalizeTransaction" && (branches.Length > 1 || branches.Any(branch => !branch.WhenTrue)))
                throw new ExtractionException($"RECEIPT_BRANCH_POLARITY: {operation.Name}/{label} is outside its admitted true arm.");
        }
    }

    private static (SemanticBranch[] Branches, SemanticStep Owner) BranchesAt(
        SemanticStep[] steps, int position, SemanticBranch[] ancestors, string operation)
    {
        SemanticStep[] matches = steps.Where(step => step.Start <= position && position < step.End).ToArray();
        if (matches.Length != 1)
            throw new ExtractionException($"RECEIPT_BRANCH_PATH: {operation} has no unique statement at {position}.");
        SemanticStep step = matches[0];
        SemanticStep[] children = step.Children.Where(child => child.Start <= position && position < child.End).ToArray();
        if (children.Length == 0) return (ancestors, step);
        if (children.Length != 1)
            throw new ExtractionException($"RECEIPT_BRANCH_PATH: {operation} has overlapping arms at {position}.");
        if (step.Kind == "IfStatement")
            ancestors = [.. ancestors, new(step.Start, step.Expression!, children[0].Kind != "ElseClause")];
        return BranchesAt(children, position, ancestors, operation);
    }

    private static bool SameStep(SemanticStep left, SemanticStep right) =>
        left.Kind == right.Kind && left.Text == right.Text && left.Start == right.Start && left.End == right.End &&
        SameNullableSourceExpression(left.Expression, right.Expression) &&
        SameNullableSourceExpression(left.DeclaredTarget, right.DeclaredTarget) && left.Children.Length == right.Children.Length &&
        left.Children.Zip(right.Children).All(pair => SameStep(pair.First, pair.Second));

    private static bool SameNullableSourceExpression(SemanticExpression? left, SemanticExpression? right) =>
        left is null ? right is null : right is not null && SameSourceExpression(left, right);

    private static bool SameSourceExpression(SemanticExpression left, SemanticExpression right) =>
        left.Start == right.Start && SameExpression(left, right) &&
        left.Children.Zip(right.Children).All(pair => SameSourceExpression(pair.First, pair.Second));

    private static IEnumerable<SemanticExpression> SourceExpressions(SemanticExpression? expression)
    {
        if (expression is null) yield break;
        yield return expression;
        foreach (SemanticExpression child in expression.Children)
            foreach (SemanticExpression descendant in SourceExpressions(child)) yield return descendant;
    }

    private static bool SourceInvocationMatches(SemanticExpression expression, SemanticInvocation invocation)
    {
        if (expression.Start != invocation.Position || expression.Kind != "InvocationExpression" || expression.SymbolId != invocation.SymbolId)
            return false;
        SemanticExpression? member = expression.Children.FirstOrDefault();
        SemanticExpression? receiver = member?.Children.FirstOrDefault();
        return receiver is null
            ? invocation.Receiver.Length == 0
            : receiver.Text == invocation.Receiver && receiver.SymbolId == invocation.ReceiverSymbolId && receiver.TypeName == invocation.ReceiverTypeName;
    }

    private static void RequireFinalizeBranches(SemanticBranch[] branches, bool? failure, int count, string label)
    {
        if (branches.Length != count || !branches[0].WhenTrue ||
            !IsSourceMember(branches[0].Condition, "tracer", "IsTracingReceipt") ||
            failure is bool expectedFailure && (!IsFailureStatusCondition(branches[1].Condition) || branches[1].WhenTrue != expectedFailure) ||
            failure is null && count > 1 && !branches[1].WhenTrue || count == 3 && !branches[2].WhenTrue)
            throw new ExtractionException($"RECEIPT_BRANCH_POLARITY: finalization {label} lost its receipt/status/fallback arm.");
    }

    private static void ValidateTypedStep(SemanticStep step, string operationName)
    {
        if (step is null || step.Children is null)
        {
            throw new ExtractionException($"Receipt-terminal operation {operationName} has an incomplete guard fact.");
        }

        if (step.Expression is not null)
        {
            RequireTypedExpression(step.Expression, operationName, requireSymbol: false);
        }

        foreach (SemanticStep child in step.Children)
        {
            ValidateTypedStep(child, operationName);
        }
    }

    private static void RequireTypedExpression(SemanticExpression expression, string location, bool requireSymbol)
    {
        if (expression is null || expression.Children is null)
        {
            throw new ExtractionException($"Receipt-terminal expression at {location} is incomplete.");
        }

        bool methodGroup = (expression.Kind is "IdentifierName" or "SimpleMemberAccessExpression" or "MemberAccessExpression") &&
            expression.SymbolId?.StartsWith("Method:", StringComparison.Ordinal) == true;
        if (string.IsNullOrWhiteSpace(expression.TypeName) && !methodGroup && !TypeOptional(expression.Kind))
        {
            throw new ExtractionException($"Receipt-terminal expression '{expression.Text}' ({expression.Kind}, symbol={expression.SymbolId ?? "<null>"}) at {location} has no Roslyn type identity.");
        }

        if (requireSymbol && string.IsNullOrWhiteSpace(expression.SymbolId))
        {
            throw new ExtractionException($"Receipt-terminal expression '{expression.Text}' at {location} has no Roslyn symbol identity.");
        }

        for (int index = 0; index < expression.Children.Length; index++)
        {
            // A named-argument label is syntax, not a value expression; its sibling
            // remains subject to the normal Roslyn type requirement.
            if (expression.Kind == "NameColon" && index == 0) continue;
            SemanticExpression child = expression.Children[index];
            RequireTypedExpression(child, location,
                child.Kind is "InvocationExpression" or "SimpleMemberAccessExpression" or "MemberAccessExpression");
        }
    }

    private static bool TypeOptional(string kind) => kind is
        "Argument" or "ArgumentList" or "BracketedArgumentList" or "IndexExpression" or
        "NameColon" or
        "InitializerExpression" or "ObjectInitializerExpression" or
        "DeclarationPattern" or "SingleVariableDesignation" or "ConstantPattern" or "NullLiteralExpression" or
        "PropertyPatternClause" or "RecursivePattern" or "VarPattern";

    private static OperationDescriptor Operation(IrDocument document, string name) =>
        document.Operations.Single(operation => operation.Name == name);

    private static void RequireOperation(
        OperationDescriptor operation,
        string phase,
        string[] inputs,
        string[] outputs,
        string[] orderedEffects,
        string[] guards,
        string[] forwardedOrProjected,
        string[] exclusions)
    {
        if (operation.Phase != phase ||
            operation.Inputs is null || !operation.Inputs.SequenceEqual(inputs, StringComparer.Ordinal) ||
            operation.Outputs is null || !operation.Outputs.SequenceEqual(outputs, StringComparer.Ordinal) ||
            operation.Steps is null || operation.Steps.Length == 0 ||
            operation.Body is null || operation.Body.Invocations is null || operation.Body.Assignments is null || operation.Body.Guards is null ||
            operation.OrderedEffects is null || !operation.OrderedEffects.SequenceEqual(orderedEffects, StringComparer.Ordinal) ||
            operation.Guards is null || !operation.Guards.SequenceEqual(guards, StringComparer.Ordinal) ||
            operation.ForwardedOrProjected is null || !operation.ForwardedOrProjected.SequenceEqual(forwardedOrProjected, StringComparer.Ordinal) ||
            operation.Exclusions is null || !operation.Exclusions.SequenceEqual(exclusions, StringComparer.Ordinal))
        {
            throw new ExtractionException($"Receipt-terminal operation {operation.Name} is not admitted to the Lean lowering.");
        }
    }

    private static MappingDescriptor RequireMapping(IrDocument document, string source, string target, string semantics)
    {
        MappingDescriptor? mapping = document.Mappings.SingleOrDefault(candidate => candidate.Source == source);
        if (mapping is null || mapping.Target != target ||
            mapping.Expression is null && mapping.Semantics != semantics ||
            mapping.Expression is not null && mapping.Semantics !=
                $"source:{mapping.Expression.Kind}:{mapping.Expression.Text}")
        {
            throw new ExtractionException($"Receipt-terminal emission mapping {source} is not admitted.");
        }

        return mapping;
    }

    private static void RequireOrdered(IReadOnlyList<string> values, IReadOnlyList<string> expected, string label)
    {
        int cursor = 0;
        foreach (string value in expected)
        {
            int index = -1;
            for (int candidate = cursor; candidate < values.Count; candidate++)
            {
                if (values[candidate] == value)
                {
                    index = candidate;
                    break;
                }
            }
            if (index < 0)
            {
                throw new ExtractionException($"Receipt-terminal {label} lost '{value}'.");
            }

            cursor = index + 1;
        }
    }

    private static SemanticOperation Body(IrDocument document, string name) =>
        Operation(document, name).Body;

    private static SemanticInvocation RequireInvocation(
        SemanticOperation body,
        string method,
        string receiver,
        string label,
        int? argumentCount = null)
    {
        SemanticInvocation[] matches = body.Invocations
            .Where(invocation => invocation.Method == method && invocation.Receiver == receiver &&
                (!argumentCount.HasValue || invocation.Arguments.Length == argumentCount.Value))
            .OrderBy(static invocation => invocation.Position)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new ExtractionException(
                $"Receipt-terminal source lowering requires exactly one {label} invocation, found {matches.Length}.");
    }

    private static SemanticInvocation[] RequireInvocations(
        SemanticOperation body,
        string method,
        string receiver,
        string label,
        int argumentCount,
        int expectedCount) =>
        body.Invocations
            .Where(invocation => invocation.Method == method && invocation.Receiver == receiver &&
                invocation.Arguments.Length == argumentCount)
            .OrderBy(static invocation => invocation.Position)
            .ToArray() switch
        {
            var invocations when invocations.Length == expectedCount => invocations,
            _ => throw new ExtractionException($"Receipt-terminal source lowering requires exactly {expectedCount} {label} invocations."),
        };

    private static SemanticAssignment RequireAssignment(
        SemanticOperation body,
        string target,
        string form,
        string label)
    {
        SemanticAssignment[] matches = body.Assignments
            .Where(assignment => assignment.Target == target && assignment.Form == form)
            .OrderBy(static assignment => assignment.Position)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new ExtractionException(
                $"Receipt-terminal source lowering requires exactly one {label} assignment, found {matches.Length}.");
    }

    private static SemanticAssignment RequireInitializer(
        SemanticOperation body,
        string target,
        string label) =>
        RequireAssignment(body, target, "initializer", label);

    private static string LowerInvocationArguments(SemanticInvocation invocation, LoweringContext context)
    {
        if (invocation.Arguments.Length == 0)
        {
            throw new ExtractionException($"Receipt-terminal source invocation {invocation.Method} has no lowerable arguments.");
        }

        return string.Join(" ", invocation.Arguments.Select(argument => LowerArgument(argument, context)));
    }

    private static string LowerArgument(SemanticExpression expression, LoweringContext context)
    {
        string lowered = LowerExpression(expression, context);
        return lowered.Length > 1 && lowered[0] == '(' && lowered[^1] == ')'
            ? lowered
            : lowered.Contains(' ')
                ? $"({lowered})"
                : lowered;
    }

    private static SemanticExpression Argument(SemanticInvocation invocation, int index, string label)
    {
        if (invocation.Arguments.Length <= index)
        {
            throw new ExtractionException($"Receipt-terminal source {label} is missing argument {index}.");
        }

        return invocation.Arguments[index];
    }

    private static SemanticExpression TuplePart(SemanticExpression expression, int index, string label)
    {
        SemanticExpression candidate = expression.Kind == "ParenthesizedExpression" && expression.Children.Length == 1
            ? expression.Children[0]
            : expression;
        if (candidate.Kind != "TupleExpression" || candidate.Children.Length <= index)
        {
            throw new ExtractionException($"Receipt-terminal source {label} is not a tuple with element {index}.");
        }

        return candidate.Children[index];
    }

    private static string LowerExpression(SemanticExpression expression, LoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(expression);
        string text = expression.Text.Replace(" ", string.Empty, StringComparison.Ordinal);
        return expression.Kind switch
        {
            "IdentifierName" => LowerIdentifier(expression.Text, context),
            "ParenthesizedExpression" => LowerExpression(Child(expression, 0), context),
            "ConditionalExpression" => LowerConditional(expression, context),
            "LogicalOrExpression" => Binary(expression, " || ", context),
            "LogicalAndExpression" => Binary(expression, " && ", context),
            "GreaterThanExpression" => Binary(expression, " > ", context),
            "LessThanExpression" => Binary(expression, " < ", context),
            "EqualsExpression" => Binary(expression, " == ", context),
            "NotEqualsExpression" => Binary(expression, " != ", context),
            "AddExpression" => Binary(expression, " + ", context),
            "SubtractExpression" => Binary(expression, " - ", context),
            "LogicalNotExpression" => $"! {LowerExpression(Child(expression, 0), context)}",
            "TupleExpression" => $"({string.Join(", ", expression.Children.Select(child => LowerExpression(child, context)))})",
            "Argument" => LowerExpression(Child(expression, 0), context),
            "ElementAccessExpression" => LowerElementAccess(expression, context),
            "CollectionExpression" => LowerEmptyCollection(expression, context),
            "DefaultLiteral" => "none",
            "NumericLiteralExpression" or "StringLiteralExpression" or "CharacterLiteralExpression" => expression.Text,
            "TrueLiteralExpression" => "true",
            "FalseLiteralExpression" => "false",
            "NullLiteralExpression" => "none",
            "LiteralExpression" => text switch
            {
                "null" => "none",
                "true" => "true",
                "false" => "false",
                _ => expression.Text,
            },
            "ImplicitArrayCreationExpression" or "ArrayCreationExpression" =>
                LowerEmptyCollection(expression, context),
            "SimpleMemberAccessExpression" => LowerMember(expression, context),
            "MemberAccessExpression" => LowerMember(expression, context),
            "InvocationExpression" => LowerInvocation(expression, context),
            "IsPatternExpression" => LowerPattern(expression, context),
            "ObjectCreationExpression" => LowerObjectCreation(expression, context),
            _ => throw new ExtractionException($"Receipt-terminal source expression '{expression.Text}' is unsupported in {context}."),
        };
    }

    private static string LowerEmptyCollection(SemanticExpression expression, LoweringContext context)
    {
        if (expression.Children.Length != 0 ||
            !string.Equals(expression.Text.Replace(" ", string.Empty, StringComparison.Ordinal), "[]", StringComparison.Ordinal))
        {
            throw new ExtractionException(
                $"Receipt-terminal source collection '{expression.Text}' is not the admitted empty collection in {context}.");
        }

        return context == LoweringContext.Finalization ? "emptyBytes" : "[]";
    }

    private static string LowerElementAccess(SemanticExpression expression, LoweringContext context)
    {
        if (expression.Children.Length != 2 || expression.Children[0].Kind != "IdentifierName" ||
            expression.Children[1].Kind != "BracketedArgumentList" || expression.Children[1].Children.Length != 1)
        {
            throw new ExtractionException($"Receipt-terminal source element access '{expression.Text}' is unsupported in {context}.");
        }

        SemanticExpression argument = Child(expression.Children[1], 0);
        if (argument.Kind != "Argument" || argument.Children.Length != 1 ||
            argument.Children[0].Kind != "IndexExpression" || argument.Children[0].Children.Length != 1 ||
            argument.Children[0].Children[0].Kind != "NumericLiteralExpression" ||
            argument.Children[0].Children[0].Text != "1")
        {
            throw new ExtractionException($"Receipt-terminal source element access '{expression.Text}' is not the admitted ^1 access.");
        }

        return expression.Children[0].Text switch
        {
            "_cumulativeBlockGasPerTx" when context == LoweringContext.Update =>
                "previousTotals state.gasHistory",
            "_cumulativeBlockGasPerTx" when context == LoweringContext.Restore =>
                "previousTotals gasHistory",
            "_txReceipts" when context == LoweringContext.Restore => "lastReceiptGas receipts",
            _ => throw new ExtractionException($"Receipt-terminal source element access '{expression.Text}' is unsupported in {context}."),
        };
    }

    private static string LowerIdentifier(string identifier, LoweringContext context) =>
        identifier switch
        {
            "logEntries" when context is LoweringContext.ReceiptField or LoweringContext.Terminal => "logs",
            "statusCode" when context == LoweringContext.ReceiptField => "statusCode status",
            "cumulativeReceiptGas" when context == LoweringContext.ReceiptField => "updated.cumulativeReceiptGas",
            "_currentIndex" when context == LoweringContext.ReceiptField => "state.currentIndex",
            "gasSpent" or "gasConsumed" when context is LoweringContext.ReceiptField or LoweringContext.GasProperty => "gas",
            "SpentGas" when context == LoweringContext.GasProperty => "gas.spentGas",
            "OperationGas" when context == LoweringContext.GasProperty => "gas.operationGas",
            "BlockGas" when context == LoweringContext.GasProperty => "gas.blockGas",
            "BlockStateGas" when context == LoweringContext.GasProperty => "gas.blockStateGas",
            "gasConsumed" or "gasSpent" when context is LoweringContext.Update or LoweringContext.Restore or
                LoweringContext.Terminal or LoweringContext.FailedReceipt => "gas",
            "prevExecution" or "previousExecutionGas" when context == LoweringContext.Update => "previousExecutionGas",
            "prevState" or "previousStateGas" when context == LoweringContext.Update => "previousStateGas",
            "cumulativeExecution" when context == LoweringContext.Restore => "cumulativeExecution",
            "cumulativeState" when context == LoweringContext.Restore => "cumulativeState",
            "cumulativeReceipt" when context == LoweringContext.Restore => "cumulativeReceipt",
            "cumulativeReceiptGas" when context == LoweringContext.Restore => "state.cumulativeReceiptGas",
            "snapshot" when context == LoweringContext.Restore => "snapshot",
            "numToRemove" when context == LoweringContext.Restore => "numToRemove",
            "stateRoot" when context is LoweringContext.ReceiptField or LoweringContext.Terminal or LoweringContext.FailedReceipt => "stateRoot",
            "stateRoot" when context is LoweringContext.Finalization or LoweringContext.FinalizationLogs => "input.stateRoot",
            "recipient" when context is LoweringContext.ReceiptField or LoweringContext.Terminal or LoweringContext.FailedReceipt => "recipient",
            "logs" when context is LoweringContext.ReceiptField or LoweringContext.Terminal => "logs",
            "effectiveGasPrice" when context == LoweringContext.ReceiptField => "transaction.effectiveGasPrice",
            "output" when context == LoweringContext.Finalization => "input.output",
            "output" when context == LoweringContext.Terminal => "output",
            "logs" when context is LoweringContext.Finalization or LoweringContext.FinalizationLogs => "input.logs",
            "error" when context == LoweringContext.Finalization => "failureError input",
            "error" when context is LoweringContext.Terminal or LoweringContext.FailedReceipt => "error",
            "executingAccount" when context == LoweringContext.Finalization => "recipient",
            "spentGas" when context == LoweringContext.Finalization => "gas",
            "statusCode" when context == LoweringContext.Finalization => "statusCode input.status",
            "emptyBytes" => "emptyBytes",
            "_cumulativeReceiptGas" when context == LoweringContext.Update => "state.cumulativeReceiptGas",
            "accounting" when context is LoweringContext.Update or LoweringContext.Restore => "accounting",
            "parallel" when context == LoweringContext.Update => "state.parallel",
            "_" => throw new ExtractionException($"Receipt-terminal source identifier '{identifier}' is unsupported in {context}."),
            _ => throw new ExtractionException($"Receipt-terminal source identifier '{identifier}' is unsupported in {context}."),
        };

    private static string LowerMember(SemanticExpression expression, LoweringContext context)
    {
        if (string.IsNullOrWhiteSpace(expression.SymbolId))
        {
            throw new ExtractionException($"Receipt-terminal source member '{expression.Text}' has no Roslyn symbol identity.");
        }

        if (expression.Children.Length == 0 ||
            !expression.SymbolId.Contains($".{expression.Children[^1].Text}", StringComparison.Ordinal))
        {
            throw new ExtractionException($"Receipt-terminal source member '{expression.Text}' has a mismatched Roslyn symbol identity.");
        }

        string source = expression.Text.Replace(" ", string.Empty, StringComparison.Ordinal);
        return source switch
        {
            "Block.Hash" => "block.hash",
            "Block.Number" => "block.number",
            "Block.Header.BaseFeePerGas" => "block.baseFeePerGas",
            "Block.Header.GasUsed" => "state.headerGasUsed",
            "transaction.Type" => "transaction.txType",
            "transaction.IsContractCreation" => "isContractCreation transaction",
            "transaction.SenderAddress" => "transaction.sender",
            "transaction.Hash" => "transaction.txHash",
            "gasConsumed.SpentGas" or "gasSpent.SpentGas" => "gas.spentGas",
            "gasConsumed.OperationGas" => "gas.operationGas",
            "gasConsumed.BlockGas" => "gas.blockGas",
            "gasConsumed.BlockStateGas" => "gas.blockStateGas",
            "gasConsumed.EffectiveBlockGas" => "effectiveBlockGas gas",
            "_cumulativeBlockGasPerTx.Count" when context is LoweringContext.Restore or LoweringContext.RestoreRetained => "gasHistory.length",
            "_cumulativeBlockGasPerTx.Count" => "state.gasHistory.length",
            "_txReceipts.Count" when context == LoweringContext.Restore => "state.receipts.length",
            "_txReceipts.Count" when context == LoweringContext.RestoreRetained => "receipts.length",
            "_txReceipts.Count" => "state.receipts.length",
            "_currentTxTracer.IsTracingReceipt" => "currentTxTracerIsTracingReceipt",
            "StatusCode.Failure" or "StatusCode.Success" when context is LoweringContext.Finalization or LoweringContext.Status =>
                $"statusCode .{Camel(source[(source.IndexOf('.') + 1)..])}",
            "StatusCode.Failure" => ".failure",
            "StatusCode.Success" => ".success",
            "EvmExceptionType.None" => "none",
            "accounting.CumulativeExecutionGas" => "accounting.cumulativeExecutionGas",
            "accounting.CumulativeStateGas" => "accounting.cumulativeStateGas",
            "accounting.CumulativeReceiptGas" => "accounting.cumulativeReceiptGas",
            "accounting.HeaderGasUsed" => "accounting.headerGasUsed",
            "_txReceipts[^1].GasUsedTotal" => "lastReceiptGas receipts",
            "substate.ShouldRevert" => "input.shouldRevert",
            "substate.Output" => "input.output",
            "substate.Error" => "input.substateError",
            "substate.EvmExceptionType" => "input.evmExceptionType",
            "substate.SubstateError" => "input.substateResultError",
            "substate.Logs" => "input.logs",
            "substate.Logs.Count" => "input.logs.length",
            _ => LowerMemberFallback(expression, context),
        };
    }

    private static string LowerMemberFallback(SemanticExpression expression, LoweringContext context)
    {
        if (expression.Children.Length < 2)
        {
            throw new ExtractionException($"Receipt-terminal source member '{expression.Text}' is unsupported in {context}.");
        }
        string receiver = LowerExpression(expression.Children[0], context);
        string member = expression.Children[^1].Text;
        if (receiver == "accounting" &&
            (member is "CumulativeExecutionGas" or "CumulativeStateGas" or "CumulativeReceiptGas" or "HeaderGasUsed"))
        {
            return $"accounting.{Camel(member)}";
        }

        throw new ExtractionException($"Receipt-terminal source member '{expression.Text}' is unsupported in {context}.");
    }

    private static string LowerInvocation(SemanticExpression expression, LoweringContext context)
    {
        if (string.IsNullOrWhiteSpace(expression.SymbolId))
        {
            throw new ExtractionException($"Receipt-terminal source invocation '{expression.Text}' has no Roslyn symbol identity.");
        }

        SemanticExpression member = Child(expression, 0);
        if (string.IsNullOrWhiteSpace(member.SymbolId))
        {
            throw new ExtractionException($"Receipt-terminal source invocation '{expression.Text}' has no member symbol identity.");
        }

        if (member.Children.Length == 0)
        {
            throw new ExtractionException($"Receipt-terminal source invocation '{expression.Text}' has no receiver expression.");
        }

        SemanticExpression receiver = Child(member, 0);
        if (string.IsNullOrWhiteSpace(receiver.TypeName))
        {
            throw new ExtractionException($"Receipt-terminal source invocation '{expression.Text}' has no receiver type identity.");
        }

        if (!expression.SymbolId.Contains($".{member.Children[^1].Text}", StringComparison.Ordinal) ||
            !member.SymbolId.Contains($".{member.Children[^1].Text}", StringComparison.Ordinal))
        {
            throw new ExtractionException($"Receipt-terminal source invocation '{expression.Text}' has mismatched Roslyn symbol identities.");
        }
        if (IsSourceMemberChain(member, "transaction", "CalculateEffectiveGasPrice"))
        {
            return "transaction.effectiveGasPrice";
        }
        if (IsSourceMemberChain(member, "substate", "Output", "AsReadOnlyArray"))
        {
            return "input.output";
        }
        if (IsSourceMemberChain(member, "substate", "LogsToArray"))
        {
            return "input.logs";
        }
        if (IsSourceMemberChain(member, "substate", "EvmExceptionType", "FastToString"))
        {
            return "input.vmError";
        }

        throw new ExtractionException($"Receipt-terminal source invocation '{expression.Text}' is unsupported in {context}.");
    }

    private static string LowerPattern(SemanticExpression expression, LoweringContext context)
    {
        if (expression.Children.Length >= 2 && IsIdentifier(expression.Children[0], "error") &&
            IsNullPattern(expression.Children[1]))
        {
            return context == LoweringContext.FailureErrorGuard ? "initialError = none" : "input.substateError = none";
        }
        if (IsNestedTracerPattern(expression)) return "nestedTracer";
        if (ContainsSourceMember(expression, "substate", "ShouldRevert")) return "input.shouldRevert";
        if (ContainsSourceMember(expression, "substate", "EvmExceptionType") &&
            ContainsSourceMember(expression, "EvmExceptionType", "None"))
        {
            return "input.evmExceptionType != none";
        }
        throw new ExtractionException($"Receipt-terminal source pattern '{expression.Text}' is unsupported in {context}.");
    }

    private static string LowerObjectCreation(SemanticExpression expression, LoweringContext context) =>
        expression.Text == "new()"
            ? "{}"
            : throw new ExtractionException($"Receipt-terminal source object creation '{expression.Text}' is unsupported in {context}.");

    private static string LowerConditional(SemanticExpression expression, LoweringContext context)
    {
        SemanticExpression condition = Child(expression, 0);
        SemanticExpression whenTrue = Child(expression, 1);
        SemanticExpression whenFalse = Child(expression, 2);
        string conditionText = LowerExpression(condition, context);
        string trueText = LowerExpression(whenTrue, context);
        string falseText = LowerExpression(whenFalse, context);
        if (context == LoweringContext.ReceiptField && (trueText == "none" || falseText == "none"))
        {
            if (trueText != "none") trueText = $"some {trueText}";
            if (falseText != "none") falseText = $"some {falseText}";
        }
        return $"if {conditionText} then {trueText} else {falseText}";
    }

    private static string Binary(SemanticExpression expression, string op, LoweringContext context) =>
        $"{LowerExpression(Child(expression, 0), context)}{op}{LowerExpression(Child(expression, 1), context)}";

    private static SemanticExpression Child(SemanticExpression expression, int index) =>
        expression.Children.Length > index
            ? expression.Children[index]
            : throw new ExtractionException($"Receipt-terminal source expression {expression.Text} is missing child {index}.");

    private static bool IsIdentifier(SemanticExpression expression, string identifier) =>
        expression.Kind == "IdentifierName" && expression.Text == identifier;

    private static bool IsSourceMember(SemanticExpression expression, string receiver, string member) =>
        (expression.Kind is "SimpleMemberAccessExpression" or "MemberAccessExpression") &&
        expression.Children.Length == 2 && IsIdentifier(expression.Children[0], receiver) &&
        IsIdentifier(expression.Children[1], member);

    private static bool IsSourceMemberChain(SemanticExpression expression, params string[] members)
    {
        if (members.Length == 0) return false;
        if (members.Length == 1) return IsIdentifier(expression, members[0]);
        return (expression.Kind is "SimpleMemberAccessExpression" or "MemberAccessExpression") &&
            expression.Children.Length == 2 &&
            IsIdentifier(expression.Children[1], members[^1]) &&
            IsSourceMemberChain(expression.Children[0], members[..^1]);
    }

    private static bool ContainsSourceMember(SemanticExpression expression, params string[] members) =>
        IsSourceMemberChain(expression, members) || expression.Children.Any(child => ContainsSourceMember(child, members));

    private static bool IsNullPattern(SemanticExpression expression) =>
        expression.Kind == "ConstantPattern" && expression.Children.Length == 1 &&
        expression.Children[0].Kind == "NullLiteralExpression";

    private static bool IsNestedTracerPattern(SemanticExpression expression) =>
        expression.Kind == "IsPatternExpression" && expression.Children.Length >= 1 &&
        IsIdentifier(expression.Children[0], "_otherTracer");

    private static bool IsFailureStatusCondition(SemanticExpression expression) =>
        expression.Kind == "EqualsExpression" && expression.Children.Length == 2 &&
        IsIdentifier(expression.Children[0], "statusCode") &&
        IsSourceMember(expression.Children[1], "StatusCode", "Failure");

    private static void RequireTerminalArguments(string[] actual, string[] expected, string branch)
    {
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new ExtractionException($"Receipt-terminal {branch} payloads did not lower from the source call arguments.");
        }
    }

    private static bool SameExpression(SemanticExpression left, SemanticExpression right) =>
        left.Kind == right.Kind && left.Text == right.Text && left.SymbolId == right.SymbolId &&
        left.TypeName == right.TypeName && left.Children.Length == right.Children.Length &&
        left.Children.Zip(right.Children).All(pair => SameExpression(pair.First, pair.Second));

    private static bool SameNullableExpression(SemanticExpression? left, SemanticExpression? right) =>
        left is null ? right is null : right is not null && SameExpression(left, right);

    private static string LowerPreviousTotalsExpression(
        SemanticExpression expression,
        LoweringContext context,
        string listExpression)
    {
        if (expression.Kind != "ConditionalExpression")
        {
            throw new ExtractionException("Receipt-terminal previous totals are not the admitted guarded source calculation.");
        }

        string condition = LowerExpression(Child(expression, 0), context);
        string whenTrue = LowerExpression(Child(expression, 1), context);
        string whenFalse = LowerExpression(Child(expression, 2), context);
        if (condition != $"{listExpression}.length > 0" ||
            whenTrue != $"previousTotals {listExpression}" || whenFalse != "(0, 0)")
        {
            throw new ExtractionException("Receipt-terminal previous totals source calculation changed.");
        }

        return $"previousTotals {listExpression}";
    }

    private static string LowerRetainedReceiptGas(SemanticExpression expression, LoweringContext context)
    {
        if (expression.Kind != "ConditionalExpression")
        {
            throw new ExtractionException("Receipt-terminal retained receipt gas is not the admitted guarded source calculation.");
        }

        string condition = LowerExpression(Child(expression, 0), context);
        string whenTrue = LowerExpression(Child(expression, 1), context);
        string whenFalse = LowerExpression(Child(expression, 2), context);
        if (condition != "receipts.length > 0" || whenTrue != "lastReceiptGas receipts" || whenFalse != "0")
        {
            throw new ExtractionException("Receipt-terminal retained receipt gas source calculation changed.");
        }

        return "lastReceiptGas receipts";
    }

    private enum LoweringContext
    {
        GasProperty,
        ReceiptField,
        Snapshot,
        Finalization,
        FinalizationLogs,
        FailureErrorGuard,
        Update,
        Restore,
        RestoreRetained,
        Terminal,
        FailedReceipt,
        Status,
    }

    private static string BuildUpdateBody(IrDocument document)
    {
        OperationDescriptor operation = Operation(document, "updateCumulativeGasTracking");
        SemanticOperation body = operation.Body;
        SemanticInvocation accumulate = RequireInvocation(
            body, "Accumulate", "BlockReceiptGasAccountingKernel", "BlockReceiptGasAccountingKernel.Accumulate", 6);
        SemanticInvocation historyAppend = RequireInvocation(
            body, "Add", "_cumulativeBlockGasPerTx", "_cumulativeBlockGasPerTx.Add", 1);
        SemanticAssignment previousTotals = RequireAssignment(
            body, "(ulong prevExecution, ulong prevState)", "assignment", "previous block totals");
        SemanticAssignment accounting = RequireInitializer(body, "accounting", "accounting result");
        SemanticAssignment headerAssignment = RequireAssignment(
            body, "Block.Header.GasUsed", "assignment", "Block.Header.GasUsed");
        SemanticAssignment receiptAssignment = RequireAssignment(
            body, "_cumulativeReceiptGas", "assignment", "_cumulativeReceiptGas");
        if (accumulate.Guard is not null || historyAppend.Guard is not null || previousTotals.Guard is not null ||
            accounting.Guard is not null || receiptAssignment.Guard is not null)
        {
            throw new ExtractionException("UpdateCumulativeGasTracking moved an unconditional mutation behind a source guard.");
        }
        SemanticExpression historyTuple = Argument(historyAppend, 0, "gas-history append");
        string executionArgument = LowerArgument(Argument(accumulate, 0, "Accumulate"), LoweringContext.Update);
        string stateArgument = LowerArgument(Argument(accumulate, 1, "Accumulate"), LoweringContext.Update);
        string receiptArgument = LowerArgument(Argument(accumulate, 2, "Accumulate"), LoweringContext.Update);
        string blockArgument = LowerArgument(Argument(accumulate, 3, "Accumulate"), LoweringContext.Update);
        string blockStateArgument = LowerArgument(Argument(accumulate, 4, "Accumulate"), LoweringContext.Update);
        string paidArgument = LowerArgument(Argument(accumulate, 5, "Accumulate"), LoweringContext.Update);
        string historyExecution = LowerExpression(TuplePart(historyTuple, 0, "gas-history append"), LoweringContext.Update);
        string historyState = LowerExpression(TuplePart(historyTuple, 1, "gas-history append"), LoweringContext.Update);
        string headerValue = LowerExpression(headerAssignment.Value, LoweringContext.Update);
        string receiptValue = LowerExpression(receiptAssignment.Value, LoweringContext.Update);
        string previousTotalsValue = LowerPreviousTotalsExpression(
            previousTotals.Value, LoweringContext.Update, "state.gasHistory");
        string returnValue = body.ReturnExpression is null
            ? throw new ExtractionException("UpdateCumulativeGasTracking source return is missing.")
            : LowerExpression(body.ReturnExpression, LoweringContext.Update);
        string headerGuard = headerAssignment.Guard is null
            ? throw new ExtractionException("Block.Header.GasUsed assignment lost its sequential guard.")
            : LowerExpression(headerAssignment.Guard, LoweringContext.Update);
        if (headerGuard != "! state.parallel")
        {
            throw new ExtractionException("Block.Header.GasUsed assignment lost the source !parallel guard.");
        }

        RequireSourceOrder(operation,
            ["_cumulativeBlockGasPerTx.Count > 0", "BlockReceiptGasAccountingKernel.Accumulate(",
                "_cumulativeBlockGasPerTx.Add", "if (!parallel)", "_cumulativeReceiptGas = accounting.CumulativeReceiptGas",
                "return _cumulativeReceiptGas"],
            "updateCumulativeGasTracking");
        if (!SameExpression(accounting.Value, accumulate.Expression))
        {
            throw new ExtractionException("UpdateCumulativeGasTracking accounting initializer is not the admitted kernel invocation.");
        }
        if (returnValue != "state.cumulativeReceiptGas")
        {
            throw new ExtractionException("UpdateCumulativeGasTracking source return did not lower to the receipt counter.");
        }

        return $"  let (previousExecutionGas, previousStateGas) := {previousTotalsValue}\n" +
            $"  let accounting :=\n    Eip803x.Generated.BlockReceiptGasAccountingKernel.accumulate\n      {executionArgument} {stateArgument} {receiptArgument}\n      {blockArgument} {blockStateArgument} {paidArgument}\n" +
            $"  let nextHeaderGasUsed :=\n    if {headerGuard} then {headerValue} else state.headerGasUsed\n" +
            $"  {{ state with\n      gasHistory := state.gasHistory ++\n        [{{ executionGas := {historyExecution}\n           stateGas := {historyState} }}]\n      cumulativeReceiptGas := {receiptValue}\n      headerGasUsed := nextHeaderGasUsed }}";
    }

    private static string BuildReceiptBody(IrDocument document)
    {
        OperationDescriptor operation = Operation(document, "buildReceipt");
        RequireSourceOrder(operation,
            ["UpdateCumulativeGasTracking", "TxReceipt txReceipt = new()", "if (gasConsumed.BlockGas > 0)", "if (gasConsumed.BlockStateGas > 0)"],
            "BuildReceipt");
        SemanticInvocation update = RequireInvocation(
            operation.Body, "UpdateCumulativeGasTracking", "", "UpdateCumulativeGasTracking", 1);
        SemanticAssignment cumulativeReceiptGas = RequireInitializer(
            operation.Body, "cumulativeReceiptGas", "cumulative receipt gas initializer");
        if (update.Guard is not null || cumulativeReceiptGas.Guard is not null ||
            !SameExpression(cumulativeReceiptGas.Value, update.Expression))
        {
            throw new ExtractionException("BuildReceipt cumulative gas initializer is not the source update invocation.");
        }
        string gasArgument = LowerExpression(Argument(update, 0, "UpdateCumulativeGasTracking"), LoweringContext.ReceiptField);
        return $"  let updated := updateCumulativeGasTracking state {gasArgument}\n  let receipt : Receipt :=\n{BuildReceiptProjection(document)}\n  (updated, receipt)";
    }

    private static string BuildFailedReceiptBody(IrDocument document)
    {
        OperationDescriptor operation = Operation(document, "buildFailedReceipt");
        SemanticInvocation build = RequireInvocation(operation.Body, "BuildReceipt", "", "BuildReceipt", 5);
        SemanticAssignment receipt = RequireInitializer(operation.Body, "receipt", "failed receipt initializer");
        SemanticAssignment error = RequireAssignment(operation.Body, "receipt.Error", "assignment", "receipt.Error");
        RequireSourceOrder(operation, ["BuildReceipt", "receipt.Error = error"], "BuildFailedReceipt");
        if (build.Guard is not null || receipt.Guard is not null || error.Guard is not null ||
            !SameExpression(receipt.Value, build.Expression) ||
            !IsSourceMember(Argument(build, 2, "BuildReceipt"), "StatusCode", "Failure"))
        {
            throw new ExtractionException("BuildFailedReceipt receipt initializer is not the source BuildReceipt invocation.");
        }
        _ = LowerEmptyCollection(Argument(build, 3, "BuildReceipt"), LoweringContext.FailedReceipt);
        string arguments = LowerInvocationArguments(build, LoweringContext.FailedReceipt);
        string errorValue = LowerExpression(error.Value, LoweringContext.FailedReceipt);
        return $"  let (updated, receipt) :=\n    buildReceipt state block transaction {arguments}\n  (updated, {{ receipt with error := {errorValue} }})";
    }

    private static string BuildMarkSuccessBody(IrDocument document)
    {
        OperationDescriptor operation = Operation(document, "markAsSuccess");
        SemanticInvocation append = RequireInvocation(operation.Body, "Add", "_txReceipts", "_txReceipts.Add", 1);
        SemanticInvocation build = RequireInvocation(operation.Body, "BuildReceipt", "", "BuildReceipt", 5);
        SemanticInvocation[] nested = RequireInvocations(operation.Body, "MarkAsSuccess", "otherTxTracer", "nested MarkAsSuccess", 5, 1);
        SemanticInvocation[] current = RequireInvocations(operation.Body, "MarkAsSuccess", "_currentTxTracer", "current MarkAsSuccess", 5, 1);
        if (append.Guard is not null || build.Guard is not null)
        {
            throw new ExtractionException("MarkAsSuccess receipt construction is unexpectedly guarded.");
        }
        RequireSourceOrder(operation, ["_txReceipts.Add(BuildReceipt", "otherTxTracer.MarkAsSuccess", "_currentTxTracer.MarkAsSuccess"], "MarkAsSuccess");
        if (!SameExpression(Argument(append, 0, "success receipt append"), build.Expression))
        {
            throw new ExtractionException("MarkAsSuccess receipt append does not use the source BuildReceipt invocation.");
        }
        if (!IsSourceMember(Argument(build, 2, "BuildReceipt"), "StatusCode", "Success"))
        {
            throw new ExtractionException("MarkAsSuccess receipt construction lost the source success status.");
        }
        string buildArguments = LowerInvocationArguments(build, LoweringContext.Terminal);
        string output = LowerExpression(Argument(nested[0], 2, "nested MarkAsSuccess"), LoweringContext.Terminal);
        string logs = LowerExpression(Argument(nested[0], 3, "nested MarkAsSuccess"), LoweringContext.Terminal);
        string stateRoot = LowerExpression(Argument(nested[0], 4, "nested MarkAsSuccess"), LoweringContext.Terminal);
        EnsureReceiptForwardingArgumentsMatch(build, nested[0], isFailure: false, "MarkAsSuccess");
        EnsureForwardingArgumentsMatch(nested[0], current[0], LoweringContext.Terminal, "MarkAsSuccess");
        if (append.Position >= nested[0].Position || append.Position >= current[0].Position)
        {
            throw new ExtractionException("MarkAsSuccess receipt append must precede both source forwarding calls.");
        }

        return $"  let (updated, receipt) :=\n    buildReceipt state block transaction {buildArguments}\n" +
            $"  {{ state := {{ updated with receipts := updated.receipts ++ [receipt] }}\n" +
            "    events := forwardingEvents nestedTracer currentTxTracerIsTracingReceipt\n" +
            $"    forwardedOutput := {output}\n    forwardedLogs := {logs}\n" +
            "    forwardedError := none\n" +
            $"    forwardedStateRoot := {stateRoot} }}";
    }

    private static string BuildMarkFailedBody(IrDocument document)
    {
        OperationDescriptor operation = Operation(document, "markAsFailed");
        SemanticInvocation append = RequireInvocation(operation.Body, "Add", "_txReceipts", "_txReceipts.Add", 1);
        SemanticInvocation build = RequireInvocation(operation.Body, "BuildFailedReceipt", "", "BuildFailedReceipt", 4);
        SemanticInvocation[] nested = RequireInvocations(operation.Body, "MarkAsFailed", "otherTxTracer", "nested MarkAsFailed", 5, 1);
        SemanticInvocation[] current = RequireInvocations(operation.Body, "MarkAsFailed", "_currentTxTracer", "current MarkAsFailed", 5, 1);
        if (append.Guard is not null || build.Guard is not null)
        {
            throw new ExtractionException("MarkAsFailed receipt construction is unexpectedly guarded.");
        }
        RequireSourceOrder(operation, ["_txReceipts.Add(BuildFailedReceipt", "otherTxTracer.MarkAsFailed", "_currentTxTracer.MarkAsFailed"], "MarkAsFailed");
        if (!SameExpression(Argument(append, 0, "failure receipt append"), build.Expression))
        {
            throw new ExtractionException("MarkAsFailed receipt append does not use the source BuildFailedReceipt invocation.");
        }
        string buildArguments = LowerInvocationArguments(build, LoweringContext.FailedReceipt);
        string output = LowerExpression(Argument(nested[0], 2, "nested MarkAsFailed"), LoweringContext.Terminal);
        string error = LowerExpression(Argument(nested[0], 3, "nested MarkAsFailed"), LoweringContext.Terminal);
        string stateRoot = LowerExpression(Argument(nested[0], 4, "nested MarkAsFailed"), LoweringContext.Terminal);
        EnsureReceiptForwardingArgumentsMatch(build, nested[0], isFailure: true, "MarkAsFailed");
        EnsureForwardingArgumentsMatch(nested[0], current[0], LoweringContext.Terminal, "MarkAsFailed");
        if (append.Position >= nested[0].Position || append.Position >= current[0].Position)
        {
            throw new ExtractionException("MarkAsFailed receipt append must precede both source forwarding calls.");
        }

        return $"  let (updated, receipt) :=\n    buildFailedReceipt state block transaction {buildArguments}\n" +
            $"  {{ state := {{ updated with receipts := updated.receipts ++ [receipt] }}\n" +
            "    events := forwardingEvents nestedTracer currentTxTracerIsTracingReceipt\n" +
            $"    forwardedOutput := {output}\n    forwardedLogs := []\n    forwardedError := {error}\n" +
            $"    forwardedStateRoot := {stateRoot} }}";
    }

    private static void EnsureForwardingArgumentsMatch(
        SemanticInvocation first,
        SemanticInvocation second,
        LoweringContext context,
        string label)
    {
        if (first.Arguments.Length != second.Arguments.Length ||
            first.Arguments.Length != 5 ||
            !first.Arguments.Zip(second.Arguments).All(pair => SameExpression(pair.First, pair.Second)) ||
            !first.Arguments.Select(argument => LowerExpression(argument, context))
                .SequenceEqual(second.Arguments.Select(argument => LowerExpression(argument, context)), StringComparer.Ordinal))
        {
            throw new ExtractionException($"{label} nested and current forwarding payloads differ.");
        }
    }

    private static void EnsureReceiptForwardingArgumentsMatch(
        SemanticInvocation receipt,
        SemanticInvocation forwarding,
        bool isFailure,
        string label)
    {
        int[] receiptIndexes = isFailure ? [0, 1, 2, 3] : [0, 1, 3, 4];
        int[] forwardingIndexes = isFailure ? [0, 1, 3, 4] : [0, 1, 3, 4];
        if (receiptIndexes.Any(index => receipt.Arguments.Length <= index) ||
            forwardingIndexes.Any(index => forwarding.Arguments.Length <= index) ||
            !receiptIndexes.Zip(forwardingIndexes)
                .All(pair => SameExpression(receipt.Arguments[pair.First], forwarding.Arguments[pair.Second])))
        {
            throw new ExtractionException($"{label} receipt and forwarding payloads differ.");
        }
    }

    private static string BuildTakeSnapshotBody(IrDocument document)
    {
        OperationDescriptor operation = Operation(document, "takeSnapshot");
        SemanticExpression expression = operation.Body.ReturnExpression
            ?? throw new ExtractionException("TakeSnapshot source return expression is missing.");
        string lowered = LowerExpression(expression, LoweringContext.Snapshot);
        if (lowered != "state.receipts.length")
        {
            throw new ExtractionException("TakeSnapshot did not lower from the admitted receipt-count source expression.");
        }
        return $"  {lowered}";
    }

    private static string BuildRestoreBody(IrDocument document)
    {
        OperationDescriptor operation = Operation(document, "restore");
        SemanticOperation body = operation.Body;
        SemanticInvocation[] receiptRemoval = RequireInvocations(body, "RemoveRange", "_txReceipts", "receipt suffix removal", 2, 1);
        SemanticInvocation[] gasRemoval = RequireInvocations(body, "RemoveRange", "_cumulativeBlockGasPerTx", "gas suffix removal", 2, 1);
        SemanticInvocation fromTotals = RequireInvocation(
            body, "FromTotals", "BlockReceiptGasAccountingKernel", "BlockReceiptGasAccountingKernel.FromTotals", 3);
        SemanticAssignment removalCount = RequireInitializer(body, "numToRemove", "restore suffix count");
        SemanticAssignment cumulativeTotals = RequireAssignment(
            body, "(ulong cumulativeExecution, ulong cumulativeState)", "assignment", "restore cumulative totals");
        SemanticAssignment cumulativeReceipt = RequireInitializer(body, "cumulativeReceipt", "restore cumulative receipt gas");
        SemanticAssignment accounting = RequireInitializer(body, "accounting", "restore accounting result");
        SemanticAssignment headerAssignment = RequireAssignment(body, "Block.Header.GasUsed", "assignment", "restore header gas");
        SemanticAssignment receiptAssignment = RequireAssignment(body, "_cumulativeReceiptGas", "assignment", "restore receipt gas");
        if (fromTotals.Guard is not null || cumulativeReceipt.Guard is not null || accounting.Guard is not null ||
            headerAssignment.Guard is not null || receiptAssignment.Guard is not null)
        {
            throw new ExtractionException("Restore moved an unconditional retained-state mutation behind a source guard.");
        }
        RequireSourceOrder(operation,
            ["_txReceipts.RemoveRange", "_cumulativeBlockGasPerTx.RemoveRange", "BlockReceiptGasAccountingKernel.FromTotals", "Block.Header.GasUsed =", "_cumulativeReceiptGas = accounting.CumulativeReceiptGas"],
            "Restore");
        string executionArgument = LowerArgument(Argument(fromTotals, 0, "FromTotals"), LoweringContext.Restore);
        string stateArgument = LowerArgument(Argument(fromTotals, 1, "FromTotals"), LoweringContext.Restore);
        string receiptArgument = LowerArgument(Argument(fromTotals, 2, "FromTotals"), LoweringContext.Restore);
        string removalCountValue = LowerExpression(removalCount.Value, LoweringContext.Restore);
        string cumulativeTotalsValue = LowerPreviousTotalsExpression(
            cumulativeTotals.Value, LoweringContext.Restore, "gasHistory");
        string cumulativeReceiptValue = LowerRetainedReceiptGas(cumulativeReceipt.Value, LoweringContext.RestoreRetained);
        string headerValue = LowerExpression(headerAssignment.Value, LoweringContext.Restore);
        string receiptValue = LowerExpression(receiptAssignment.Value, LoweringContext.Restore);
        if (LowerExpression(Argument(receiptRemoval[0], 0, "receipt suffix removal"), LoweringContext.Restore) != "snapshot" ||
            LowerExpression(Argument(gasRemoval[0], 0, "gas suffix removal"), LoweringContext.Restore) != "snapshot")
        {
            throw new ExtractionException("Restore suffix removals did not retain the source snapshot argument.");
        }
        if (receiptRemoval[0].Guard is not SemanticExpression removalGuard ||
            gasRemoval[0].Guard is not SemanticExpression gasRemovalGuard ||
            !SameExpression(gasRemovalGuard, removalGuard))
        {
            throw new ExtractionException("Restore suffix removals are not enclosed by the source valid-prefix guard.");
        }
        if (LowerExpression(removalGuard, LoweringContext.Restore) != "numToRemove > 0")
        {
            throw new ExtractionException("Restore suffix removals lost the source numToRemove > 0 guard.");
        }
        if (LowerExpression(Argument(receiptRemoval[0], 1, "receipt suffix removal"), LoweringContext.Restore) != "numToRemove" ||
            LowerExpression(Argument(gasRemoval[0], 1, "gas suffix removal"), LoweringContext.Restore) != "numToRemove" ||
            !SameExpression(accounting.Value, fromTotals.Expression))
        {
            throw new ExtractionException("Restore source suffix count or retained totals are not wired to FromTotals.");
        }

        return $"  let numToRemove := {removalCountValue}\n" +
            $"  let retainedLength := state.receipts.length - numToRemove\n" +
            $"  let receipts := state.receipts.take retainedLength\n  let gasHistory := state.gasHistory.take retainedLength\n" +
            $"  let (cumulativeExecution, cumulativeState) := {cumulativeTotalsValue}\n" +
            $"  let cumulativeReceipt := {cumulativeReceiptValue}\n  let accounting :=\n    Eip803x.Generated.BlockReceiptGasAccountingKernel.fromTotals\n      {executionArgument} {stateArgument} {receiptArgument}\n" +
            $"  {{ state with\n      receipts := receipts\n      gasHistory := gasHistory\n      cumulativeReceiptGas := {receiptValue}\n      headerGasUsed := {headerValue} }}";
    }

    private static void RequireSourceOrder(OperationDescriptor operation, IReadOnlyList<string> tokens, string label)
    {
        string source = string.Join('\n', operation.Steps.Select(static step => step.Text));
        int cursor = 0;
        foreach (string token in tokens)
        {
            int found = source.IndexOf(token, cursor, StringComparison.Ordinal);
            if (found < 0)
            {
                throw new ExtractionException($"Receipt-terminal source lowering lost {label} step '{token}'.");
            }
            cursor = found + token.Length;
        }
    }

    private static string BuildEffectiveBlockGas(IrDocument document)
    {
        if (document.Lowering?.EffectiveBlockGas is not SemanticExpression expression)
        {
            throw new ExtractionException("Receipt-terminal lowering has no source-derived EffectiveBlockGas expression.");
        }

        string lowered = LowerExpression(expression, LoweringContext.GasProperty);
        if (!lowered.Contains("effectiveBlockGas", StringComparison.Ordinal) &&
            !lowered.Contains("gas.blockGas", StringComparison.Ordinal))
        {
            throw new ExtractionException("Receipt-terminal EffectiveBlockGas lowering lost its gas inputs.");
        }
        return $"  {lowered}";
    }

    private static string BuildStatusCode(IrDocument document)
    {
        SemanticExpression failure = document.Lowering?.StatusCode?.Failure
            ?? throw new ExtractionException("Receipt-terminal lowering has no source failure status constant.");
        SemanticExpression success = document.Lowering.StatusCode.Success;
        string loweredFailure = LowerExpression(failure, LoweringContext.Status);
        string loweredSuccess = LowerExpression(success, LoweringContext.Status);
        if (!byte.TryParse(loweredFailure, out _) || !byte.TryParse(loweredSuccess, out _))
        {
            throw new ExtractionException("Receipt-terminal status constants did not lower to byte literals.");
        }

        return $"| .success => {loweredSuccess}\n| .failure => {loweredFailure}";
    }

    private static string BuildReceiptProjection(IrDocument document)
    {
        OperationDescriptor operation = Operation(document, "buildReceipt");
        EnsureFieldOrder(document.Inputs.ReceiptFields,
            ["Logs", "TxType", "GasUsedTotal", "StatusCode", "Recipient", "BlockHash", "BlockNumber", "Index", "GasUsed", "EffectiveGasPrice", "Sender", "ContractAddress", "TxHash", "PostTransactionState", "BlockGasUsed", "ExecutionGasUsed", "StorageGasUsed", "Error"],
            "receipt");
        RequireMapping(document, "buildReceipt.gasConsumed.EffectiveBlockGas", "receipt.BlockGasUsed",
            "BlockGas > 0 assigns the effective block gas; otherwise the nonnullable receipt field remains zero");
        RequireMapping(document, "buildReceipt.gasConsumed.OperationGas", "receipt.ExecutionGasUsed",
            "BlockGas > 0 assigns operation gas; otherwise the nonnullable receipt field remains zero");
        RequireMapping(document, "buildReceipt.gasConsumed.BlockStateGas", "receipt.StorageGasUsed",
            "BlockStateGas > 0 assigns state gas; otherwise the nonnullable receipt field remains zero");
        RequireMapping(document, "receipt.Logs", "terminal.logs", "success logs; failure BuildFailedReceipt supplies []");
        RequireMapping(document, "receipt.ReturnValue", "excluded",
            "never assigned by BuildReceipt; output is forwarding payload only");
        RequireMapping(document, "transaction.IsContractCreation", "receipt.Recipient/ContractAddress",
            "production IsContractCreation is To-is-null; model derives the same relation and maps creation recipient to ContractAddress, call recipient to Recipient");

        string[] lines = document.Inputs.ReceiptFields
            .Select(field =>
            {
                LoweringBinding binding = ReceiptBinding(document, field);
                (SemanticExpression expression, SemanticExpression? guard) = ReceiptSourceAssignment(
                    operation.Body, binding, field);
                string lowered = LowerReceiptExpression(expression, guard, field);
                return $"                  {Camel(field)} := {lowered}";
            })
            .ToArray();
        return "                {\n" + string.Join("\n", lines) + "\n                }";
    }

    private static LoweringBinding ReceiptBinding(IrDocument document, string field) =>
        document.Lowering?.ReceiptFields?.SingleOrDefault(binding => binding.Target == $"receipt.{field}")
        ?? throw new ExtractionException($"Receipt-terminal lowering has no source binding for receipt field {field}.");

    private static (SemanticExpression Expression, SemanticExpression? Guard) ReceiptSourceAssignment(
        SemanticOperation body,
        LoweringBinding binding,
        string field)
    {
        SemanticExpression bindingExpression = binding.Expression
            ?? throw new ExtractionException($"Receipt-terminal field {field} has no source expression.");
        SemanticAssignment[] assignments = body.Assignments
            .Where(assignment => assignment.Form == "assignment" &&
                (assignment.Target == field || assignment.Target == $"txReceipt.{field}"))
            .ToArray();
        if (assignments.Length == 0)
        {
            if (field != "Error" || bindingExpression.Kind != "DefaultLiteral")
            {
                throw new ExtractionException($"Receipt-terminal field {field} has no source object-initializer assignment.");
            }

            return (bindingExpression, null);
        }

        if (assignments.Length != 1 || !SameExpression(assignments[0].Value, bindingExpression) ||
            !SameNullableExpression(assignments[0].Guard, binding.Guard))
        {
            throw new ExtractionException($"Receipt-terminal field {field} binding is not the source assignment.");
        }

        return (assignments[0].Value, assignments[0].Guard);
    }

    private static string LowerReceiptExpression(
        SemanticExpression expression,
        SemanticExpression? guard,
        string field)
    {
        string lowered = LowerExpression(expression, LoweringContext.ReceiptField);
        if (guard is SemanticExpression condition)
        {
            if (field is not ("BlockGasUsed" or "ExecutionGasUsed" or "StorageGasUsed"))
            {
                throw new ExtractionException($"Receipt-terminal field {field} has an unsupported assignment guard.");
            }

            string loweredGuard = LowerExpression(condition, LoweringContext.ReceiptField);
            string expectedGuard = field switch
            {
                "BlockGasUsed" or "ExecutionGasUsed" => "gas.blockGas > 0",
                "StorageGasUsed" => "gas.blockStateGas > 0",
                _ => throw new ExtractionException($"Receipt-terminal field {field} has an unsupported assignment guard."),
            };
            if (loweredGuard != expectedGuard)
            {
                throw new ExtractionException($"Receipt-terminal field {field} guard did not lower to its source gas dimension.");
            }

            return $"if {loweredGuard} then {lowered} else 0";
        }

        return lowered;
    }

    private static string BuildForwardingEvents(IrDocument document, OperationDescriptor success)
    {
        OperationDescriptor failure = Operation(document, "markAsFailed");
        SemanticInvocation append = RequireInvocation(success.Body, "Add", "_txReceipts", "success receipt append", 1);
        SemanticInvocation build = RequireInvocation(success.Body, "BuildReceipt", "", "success receipt build", 5);
        SemanticInvocation failureAppend = RequireInvocation(failure.Body, "Add", "_txReceipts", "failure receipt append", 1);
        SemanticInvocation failureBuild = RequireInvocation(failure.Body, "BuildFailedReceipt", "", "failure receipt build", 4);
        SemanticInvocation[] nested = RequireInvocations(success.Body, "MarkAsSuccess", "otherTxTracer", "nested success forwarding", 5, 1);
        SemanticInvocation[] current = RequireInvocations(success.Body, "MarkAsSuccess", "_currentTxTracer", "current success forwarding", 5, 1);
        SemanticInvocation[] failureNested = RequireInvocations(failure.Body, "MarkAsFailed", "otherTxTracer", "nested failure forwarding", 5, 1);
        SemanticInvocation[] failureCurrent = RequireInvocations(failure.Body, "MarkAsFailed", "_currentTxTracer", "current failure forwarding", 5, 1);
        SemanticStep nestedGuard = success.Body.Guards.FirstOrDefault(guard =>
            guard.Expression is SemanticExpression expression && IsNestedTracerPattern(expression))
            ?? throw new ExtractionException("Receipt-terminal source lowering lost the nested tracer guard.");
        SemanticStep currentGuard = success.Body.Guards.FirstOrDefault(guard =>
            guard.Expression is SemanticExpression expression &&
            IsSourceMember(expression, "_currentTxTracer", "IsTracingReceipt"))
            ?? throw new ExtractionException("Receipt-terminal source lowering lost the current tracer guard.");
        SemanticStep failureNestedGuard = failure.Body.Guards.FirstOrDefault(guard =>
            guard.Expression is SemanticExpression expression && IsNestedTracerPattern(expression))
            ?? throw new ExtractionException("Receipt-terminal source lowering lost the failure nested tracer guard.");
        SemanticStep failureCurrentGuard = failure.Body.Guards.FirstOrDefault(guard =>
            guard.Expression is SemanticExpression expression &&
            IsSourceMember(expression, "_currentTxTracer", "IsTracingReceipt"))
            ?? throw new ExtractionException("Receipt-terminal source lowering lost the failure current tracer guard.");
        string nestedCondition = LowerExpression(nestedGuard.Expression!, LoweringContext.Terminal);
        string currentCondition = LowerExpression(currentGuard.Expression!, LoweringContext.Terminal);
        if (nestedCondition != "nestedTracer" || currentCondition != "currentTxTracerIsTracingReceipt")
        {
            throw new ExtractionException("Receipt-terminal tracer forwarding guards did not lower to their typed model inputs.");
        }
        if (!SameExpression(nestedGuard.Expression!, failureNestedGuard.Expression!) ||
            !SameExpression(currentGuard.Expression!, failureCurrentGuard.Expression!))
        {
            throw new ExtractionException("Receipt-terminal success and failure forwarding guards differ.");
        }
        if (nested[0].Guard is not SemanticExpression nestedInvocationGuard ||
            current[0].Guard is not SemanticExpression currentInvocationGuard ||
            failureNested[0].Guard is not SemanticExpression failureNestedInvocationGuard ||
            failureCurrent[0].Guard is not SemanticExpression failureCurrentInvocationGuard ||
            !SameExpression(nestedInvocationGuard, nestedGuard.Expression!) ||
            !SameExpression(currentInvocationGuard, currentGuard.Expression!) ||
            !SameExpression(failureNestedInvocationGuard, failureNestedGuard.Expression!) ||
            !SameExpression(failureCurrentInvocationGuard, failureCurrentGuard.Expression!))
        {
            throw new ExtractionException("Receipt-terminal tracer forwarding calls are not enclosed by their source guards.");
        }

        if (build.Position <= append.Position || failureBuild.Position <= failureAppend.Position ||
            !SameExpression(Argument(append, 0, "success receipt append"), build.Expression) ||
            !SameExpression(Argument(failureAppend, 0, "failure receipt append"), failureBuild.Expression) ||
            append.Position >= nested[0].Position || append.Position >= current[0].Position ||
            failureAppend.Position >= failureNested[0].Position || failureAppend.Position >= failureCurrent[0].Position)
        {
            throw new ExtractionException("Receipt-terminal source forwarding order is not append-before-forward.");
        }

        if (nested[0].Position >= current[0].Position !=
            failureNested[0].Position >= failureCurrent[0].Position)
        {
            throw new ExtractionException("Receipt-terminal success and failure forwarding order differs.");
        }

        List<(int Position, int Order, string Text)> events =
        [
            (append.Position, 0, "[Event.gasMutation]"),
            (append.Position, 1, "[Event.receiptAppend]"),
            (nested[0].Position, 0, "(if nestedTracer then [Event.nestedTracerForward] else [])"),
            (current[0].Position, 0, "(if currentTxTracerIsTracingReceipt then [Event.currentTracerForward] else [])"),
        ];
        events.Sort(static (left, right) =>
            left.Position != right.Position ? left.Position.CompareTo(right.Position) : left.Order.CompareTo(right.Order));
        return "def forwardingEvents (nestedTracer currentTxTracerIsTracingReceipt : Bool) : List Event :=\n  " +
            string.Join(" ++\n    ", events.Select(static item => item.Text));
    }

    private static string BuildFailureOutput(IrDocument document)
    {
        LoweringBinding binding = document.Lowering?.Finalization?.SingleOrDefault(binding => binding.Target == "terminal.output")
            ?? throw new ExtractionException("Receipt-terminal finalization lowering has no output binding.");
        SemanticAssignment initializer = RequireInitializer(Body(document, "finalizeTransaction"), "output", "failure output initializer");
        SemanticExpression expression = binding.Expression
            ?? throw new ExtractionException("Receipt-terminal output binding has no source expression.");
        if (!SameExpression(initializer.Value, expression))
        {
            throw new ExtractionException("Receipt-terminal output binding is detached from the source initializer.");
        }
        return $"def failureOutput (input : FinalizeInput) : BytesOracle :=\n  {LowerExpression(expression, LoweringContext.Finalization)}";
    }

    private static string BuildFailureError(IrDocument document)
    {
        SemanticOperation body = Body(document, "finalizeTransaction");
        SemanticAssignment initializer = RequireInitializer(body, "error", "failure error initializer");
        LoweringBinding binding = document.Lowering?.Finalization?.SingleOrDefault(binding => binding.Target == "terminal.error")
            ?? throw new ExtractionException("Receipt-terminal finalization lowering has no error binding.");
        if (binding.Expression is not SemanticExpression bindingExpression ||
            !SameExpression(initializer.Value, bindingExpression))
        {
            throw new ExtractionException("Receipt-terminal error binding is detached from the source initializer.");
        }
        SemanticInvocation[] fallbackInvocations = body.Invocations
            .Where(invocation => invocation.Receiver == "substate.EvmExceptionType" &&
                invocation.Method == "FastToString" && invocation.Arguments.Length == 0)
            .OrderBy(static invocation => invocation.Position)
            .ToArray();
        if (fallbackInvocations.Length != 1)
        {
            throw new ExtractionException(
                $"Receipt-terminal finalization lowering requires exactly one FastToString fallback invocation, found {fallbackInvocations.Length}.");
        }

        SemanticInvocation fallbackInvocation = fallbackInvocations[0];
        SemanticAssignment[] fallbackAssignments = body.Assignments
            .Where(assignment => assignment.Target == "error" && assignment.Form == "assignment" &&
                SameExpression(assignment.Value, fallbackInvocation.Expression))
            .OrderBy(static assignment => assignment.Position)
            .ToArray();
        if (fallbackAssignments.Length != 1)
        {
            throw new ExtractionException(
                $"Receipt-terminal finalization lowering requires exactly one FastToString fallback assignment, found {fallbackAssignments.Length}.");
        }

        SemanticAssignment fallback = fallbackAssignments[0];
        SemanticExpression guard = fallback.Guard
            ?? throw new ExtractionException("Receipt-terminal FastToString fallback lost its null/exception guard.");
        if (initializer.Position >= fallback.Position || fallback.Position >= fallbackInvocation.Position)
        {
            throw new ExtractionException("Receipt-terminal FastToString fallback source order changed.");
        }
        string initializerValue = LowerExpression(initializer.Value, LoweringContext.Finalization);
        string fallbackValue = LowerExpression(fallback.Value, LoweringContext.Finalization);
        string fallbackGuard = LowerExpression(guard, LoweringContext.FailureErrorGuard);
        if (fallbackValue != "input.vmError" ||
            fallbackGuard != "initialError = none && input.evmExceptionType != none")
        {
            throw new ExtractionException("Receipt-terminal failure error precedence did not lower from the typed source assignments.");
        }

        return $"def failureError (input : FinalizeInput) : Option ErrorOracle :=\n  let initialError := {initializerValue}\n  match initialError with\n  | some error => some error\n  | none => if {fallbackGuard} then some {fallbackValue} else none";
    }

    private static string BuildTransactionResult(IrDocument document)
    {
        FinalizationResultLowering result = document.Lowering?.Result
            ?? throw new ExtractionException("Receipt-terminal finalization lowering has no source result decomposition.");
        LoweringBinding resultErrorBinding = document.Lowering.Finalization
            ?.SingleOrDefault(binding => binding.Target == "finalization.result.error")
            ?? throw new ExtractionException("Receipt-terminal finalization lowering has no result-error binding.");
        if (resultErrorBinding.Expression is not SemanticExpression resultErrorExpression ||
            !SameExpression(resultErrorExpression, result.ExceptionError))
        {
            throw new ExtractionException("Receipt-terminal result-error binding is detached from the source return.");
        }
        string condition = LowerExpression(result.Condition, LoweringContext.Finalization);
        string exceptionType = LowerExpression(result.ExceptionType, LoweringContext.Finalization);
        string exceptionError = LowerExpression(result.ExceptionError, LoweringContext.Finalization);
        string okResult = LowerResultValue(result.OkResult, "TransactionResult.Ok");
        if (condition != "input.evmExceptionType != none" || exceptionType != "input.evmExceptionType")
        {
            throw new ExtractionException("Receipt-terminal result classification did not lower from its typed source branches.");
        }

        return $"def transactionResult (input : FinalizeInput) : TransactionResult :=\n  if {condition} then\n    match {exceptionType} with\n    | some exceptionType => .evmException exceptionType {exceptionError}\n    | none => {okResult}\n  else\n    {okResult}";
    }

    private static string BuildFinalizeTransaction(IrDocument document)
    {
        OperationDescriptor operation = Operation(document, "finalizeTransaction");
        SemanticOperation body = operation.Body;
        SemanticInvocation failure = RequireInvocation(body, "MarkAsFailed", "tracer", "finalization failure terminal call", 5);
        SemanticInvocation success = RequireInvocation(body, "MarkAsSuccess", "tracer", "finalization success terminal call", 5);
        SemanticExpression[] statusConditions = body.Guards
            .Select(static guard => guard.Expression)
            .OfType<SemanticExpression>()
            .Where(IsFailureStatusCondition)
            .ToArray();
        if (statusConditions.Length != 1)
        {
            throw new ExtractionException("Receipt-terminal finalization lowering requires exactly one status branch condition.");
        }
        SemanticExpression statusCondition = statusConditions[0];
        SemanticExpression[] receiptGates = body.Guards
            .Select(static guard => guard.Expression)
            .OfType<SemanticExpression>()
            .Where(expression => IsSourceMember(expression, "tracer", "IsTracingReceipt"))
            .ToArray();
        if (receiptGates.Length != 1 || failure.Guard is not SemanticExpression failureGuard ||
            success.Guard is not SemanticExpression successGuard ||
            !IsFailureStatusCondition(failureGuard) || !SameExpression(failureGuard, successGuard))
        {
            throw new ExtractionException("Receipt-terminal finalization terminal calls lost the source receipt/status guards.");
        }
        string condition = LowerExpression(statusCondition, LoweringContext.Finalization);
        RequireFinalizeTerminalShape(failure, isFailure: true);
        RequireFinalizeTerminalShape(success, isFailure: false);
        string[] failureArguments = LowerFinalizeTerminalArguments(failure, isFailure: true);
        string[] successArguments = LowerFinalizeTerminalArguments(success, isFailure: false);
        if (failure.Position >= success.Position ||
            body.ReturnExpression is null || body.ReturnExpression.Kind != "ConditionalExpression")
        {
            throw new ExtractionException("Receipt-terminal finalization source order or result return is not admitted.");
        }
        RequireSourceOrder(operation,
            ["if (tracer.IsTracingReceipt)", "if (statusCode == StatusCode.Failure)",
                "tracer.MarkAsFailed", "else", "tracer.MarkAsSuccess", "return substate.EvmExceptionType"],
            "FinalizeTransaction");

        FinalizationResultLowering result = document.Lowering?.Result
            ?? throw new ExtractionException("Receipt-terminal finalization result lowering is missing.");
        SemanticExpression sourceResult = body.ReturnExpression ??
            throw new ExtractionException("Receipt-terminal finalization result return expression is missing.");
        if (!SameExpression(Child(sourceResult, 0), result.Condition) ||
            !SameExpression(Child(Child(Child(Child(sourceResult, 1), 1), 0), 0), result.ExceptionType) ||
            !SameExpression(Child(Child(Child(Child(sourceResult, 1), 1), 1), 0), result.ExceptionError) ||
            !SameExpression(Child(sourceResult, 2), result.OkResult))
        {
            throw new ExtractionException("Receipt-terminal finalization result lowering is detached from the source return expression.");
        }

        SemanticAssignment outputInitializer = RequireInitializer(body, "output", "failure output initializer");
        SemanticAssignment logsInitializer = RequireInitializer(body, "logs", "success logs initializer");
        SemanticAssignment stateRootInitializer = RequireInitializer(body, "stateRoot", "state-root initializer");
        if (outputInitializer.Guard is not SemanticExpression outputGuard ||
            logsInitializer.Guard is not SemanticExpression logsGuard ||
            stateRootInitializer.Guard is not SemanticExpression stateRootGuard ||
            !SameExpression(outputGuard, statusCondition) || !SameExpression(logsGuard, statusCondition) ||
            !SameExpression(stateRootGuard, receiptGates[0]) || outputInitializer.Position >= failure.Position ||
            logsInitializer.Position >= success.Position || stateRootInitializer.Position >= failure.Position)
        {
            throw new ExtractionException("Receipt-terminal finalization locals lost their source guards or order before terminal forwarding.");
        }
        if (LowerExpression(outputInitializer.Value, LoweringContext.Finalization) !=
                "if input.shouldRevert then input.output else emptyBytes" ||
            LowerExpression(logsInitializer.Value, LoweringContext.FinalizationLogs) !=
                "if input.logs.length != 0 then input.logs else []" ||
            LowerExpression(stateRootInitializer.Value, LoweringContext.Finalization) != "none")
        {
            throw new ExtractionException("Receipt-terminal finalization locals did not lower from their source initializers.");
        }
        RequireTerminalArguments(failureArguments, ["recipient", "gas", "failureOutput input", "failureError input", "input.stateRoot"], "failure");
        RequireTerminalArguments(successArguments, ["recipient", "gas", "input.output", "input.logs", "input.stateRoot"], "success");

        return $"  let trace :=\n    if {condition} then\n      markAsFailed state block transaction {FormatArguments(failureArguments)} nestedTracer currentTxTracerIsTracingReceipt\n    else\n      markAsSuccess state block transaction {FormatArguments(successArguments)} nestedTracer currentTxTracerIsTracingReceipt\n  {{ trace := trace, result := transactionResult input }}";
    }

    private static void RequireFinalizeTerminalShape(SemanticInvocation invocation, bool isFailure)
    {
        bool common = invocation.Arguments.Length == 5 &&
            IsIdentifier(invocation.Arguments[0], "executingAccount") &&
            IsIdentifier(invocation.Arguments[1], "spentGas") &&
            IsIdentifier(invocation.Arguments[4], "stateRoot");
        if (!common)
        {
            throw new ExtractionException(
                $"Receipt-terminal {(isFailure ? "failure" : "success")} call arguments are not the admitted source payload.");
        }

        bool branch = isFailure
            ? IsIdentifier(invocation.Arguments[2], "output") && IsIdentifier(invocation.Arguments[3], "error")
            : invocation.Arguments[2] is { Kind: "InvocationExpression", Children.Length: 2 } output &&
              IsSourceMemberChain(output.Children[0], "substate", "Output", "AsReadOnlyArray") &&
              output.Children[1] is { Kind: "ArgumentList", Children.Length: 0 } &&
              IsIdentifier(invocation.Arguments[3], "logs");
        if (!branch)
        {
            throw new ExtractionException(
                $"Receipt-terminal {(isFailure ? "failure" : "success")} call arguments are not the admitted source payload.");
        }
    }

    private static string FormatArguments(IEnumerable<string> arguments) =>
        string.Join(" ", arguments.Select(ParenthesizeApplication));

    private static string ParenthesizeApplication(string value) =>
        value.Length > 1 && value[0] == '(' && value[^1] == ')'
            ? value
            : value.Contains(' ')
                ? $"({value})"
                : value;

    private static string[] LowerFinalizeTerminalArguments(SemanticInvocation invocation, bool isFailure)
    {
        string[] arguments = invocation.Arguments.Select(argument => LowerExpression(argument, LoweringContext.Finalization)).ToArray();
        if (arguments.Length != 5)
        {
            throw new ExtractionException($"Receipt-terminal {(isFailure ? "failure" : "success")} terminal call has the wrong arity.");
        }

        if (arguments[0] == "input.executingAccount") arguments[0] = "recipient";
        if (arguments[1] == "input.spentGas") arguments[1] = "gas";
        if (IsIdentifier(invocation.Arguments[2], "output")) arguments[2] = "failureOutput input";
        if (IsIdentifier(invocation.Arguments[3], "error")) arguments[3] = "failureError input";
        if (IsIdentifier(invocation.Arguments[3], "logs")) arguments[3] = "input.logs";
        if (IsIdentifier(invocation.Arguments[4], "stateRoot")) arguments[4] = "input.stateRoot";
        return arguments;
    }

    private static string LowerResultValue(SemanticExpression expression, string expected)
    {
        if (expected != "TransactionResult.Ok" || !IsSourceMember(expression, "TransactionResult", "Ok"))
        {
            throw new ExtractionException($"Receipt-terminal result branch '{expression.Text}' is not the admitted {expected} branch.");
        }

        return ".ok";
    }

    private static string BuildFinalizeFields(string[] fields)
    {
        string[] expected = ["Status", "ShouldRevert", "Output", "SubstateError", "VmError", "EvmExceptionType", "SubstateResultError", "Logs", "StateRoot"];
        EnsureFieldOrder(fields, expected, "finalization");
        return string.Join("\n", fields.Select(field => $"  {Camel(field)} : {FinalizeType(field)}"));
    }

    private static string BuildReceiptFields(string[] fields)
    {
        string[] expected = ["Logs", "TxType", "GasUsedTotal", "StatusCode", "Recipient", "BlockHash", "BlockNumber", "Index", "GasUsed", "EffectiveGasPrice", "Sender", "ContractAddress", "TxHash", "PostTransactionState", "BlockGasUsed", "ExecutionGasUsed", "StorageGasUsed", "Error"];
        EnsureFieldOrder(fields, expected, "receipt");
        return string.Join("\n", fields.Select(field => $"  {Camel(field)} : {ReceiptType(field)}"));
    }

    private static void EnsureFieldOrder(string[] fields, string[] expected, string label)
    {
        if (fields is null || !fields.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new ExtractionException($"Receipt-terminal {label} field schema is not admitted.");
        }
    }

    private static string Field(string[] fields, string name)
    {
        if (fields is null || !fields.Contains(name, StringComparer.Ordinal))
        {
            throw new ExtractionException($"Receipt-terminal lowering field {name} is not present in the typed IR.");
        }

        return Camel(name);
    }

    private static string Camel(string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];

    private static string FinalizeType(string field) =>
        field switch
        {
            "Status" => "Status",
            "ShouldRevert" => "Bool",
            "Output" => "BytesOracle",
            "SubstateError" => "Option ErrorOracle",
            "VmError" => "ErrorOracle",
            "EvmExceptionType" => "Option EvmExceptionOracle",
            "SubstateResultError" => "Option ErrorOracle",
            "Logs" => "List LogOracle",
            "StateRoot" => "Option HashOracle",
            _ => throw new ExtractionException($"Unknown finalization field {field}.")
        };

    private static string ReceiptType(string field) =>
        field switch
        {
            "Logs" => "List LogOracle",
            "TxType" => "Nat",
            "GasUsedTotal" => "Nat",
            "StatusCode" => "Nat",
            "Recipient" => "Option AddressOracle",
            "BlockHash" => "Option HashOracle",
            "BlockNumber" => "Nat",
            "Index" => "Nat",
            "GasUsed" => "Nat",
            "EffectiveGasPrice" => "PriceOracle",
            "Sender" => "Option AddressOracle",
            "ContractAddress" => "Option AddressOracle",
            "TxHash" => "Option HashOracle",
            "PostTransactionState" => "Option HashOracle",
            "BlockGasUsed" => "Nat",
            "ExecutionGasUsed" => "Nat",
            "StorageGasUsed" => "Nat",
            "Error" => "Option ErrorOracle",
            _ => throw new ExtractionException($"Unknown receipt field {field}.")
        };

    private sealed record EmissionPlan(
        string UpdateBody,
        string BuildReceiptBody,
        string BuildFailedReceiptBody,
        string MarkSuccessBody,
        string MarkFailedBody,
        string TakeSnapshotBody,
        string RestoreBody,
        string FinalizeFields,
        string ReceiptFields,
        string ReceiptProjection,
        string EffectiveBlockGas,
        string StatusCode,
        string ForwardingEvents,
        string FailureOutput,
        string FailureError,
        string TransactionResult,
        string FinalizeTransaction);
}
