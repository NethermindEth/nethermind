// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Evm.Lean.OrdinaryEvmCompletionExtractor;

internal static class PlanBuilder
{
    internal static ContinuationPlan Build(IReadOnlyDictionary<string, SourceAdmission.MethodSite> methods)
    {
        List<ExpressionSite> sites = [];
        Add("caller.restore", "Execute", "opts.HasFlag(ExecutionOptions.Restore)");
        Add("caller.commit", "Execute", "opts.HasFlag(ExecutionOptions.Commit) || (!opts.HasFlag(ExecutionOptions.SkipValidation) && !spec.IsEip658Enabled)");
        Add("caller.entry", "Execute", "ExecuteEvmTransaction(tx, header, spec, tracer, opts, restore, commit, deleteCallerAccount, in intrinsicGas, gasAvailable, in opcodeGasPrice, in premiumPerGas, in senderReservedGasPayment, in blobBaseFee, preloadedCodeInfo, preloadedDelegationAddress)");
        Add("prepared.standard-and-floor", "ExecuteEvmTransaction", "new(executionIntrinsicGasStandard, intrinsicGas.FloorGas)");
        Add("prepared.call.off", "ExecuteEvmTransaction", "ExecuteEvmCall<OffFlag>(tx, header, spec, tracer, opts, delegationRefunds, executionIntrinsicGas, postIntrinsicStateReservoir, accessTracker, gasAvailable, env, topFrameOutOfGas, out TransactionSubstate substate, out GasConsumed spentGas)");
        Add("prepared.call.on", "ExecuteEvmTransaction", "ExecuteEvmCall<OnFlag>(tx, header, spec, tracer, opts, delegationRefunds, executionIntrinsicGas, postIntrinsicStateReservoir, accessTracker, gasAvailable, env, topFrameOutOfGas, out substate, out spentGas)");
        Add("frame.entry", "ExecuteEvmCall", "VmState<TGasPolicy>.RentTopLevel(gasAvailable, executionType, env, in accessedItems, in snapshot)");
        Add("vm.off", "ExecuteEvmCall", "VirtualMachine.ExecuteTransaction(state, WorldState, tracer)");
        Add("vm.on", "ExecuteEvmCall", "VirtualMachine.ExecuteTransaction<OnFlag>(state, WorldState, tracer)");
        Add("vm.metrics", "ExecuteEvmCall", "Metrics.IncrementOpCodes(VirtualMachine.OpCodeCount)");
        Add("vm.flush", "ExecuteEvmCall", "VirtualMachine.FlushMetricsCounters()");
        Add("vm.gas-copy", "ExecuteEvmCall", "gasAvailable = state.Gas");
        Add("vm.access", "ExecuteEvmCall", "tracer.ReportAccess(accessedItems.AccessedAddresses, accessedItems.AccessedStorageCells)");
        Add("vm.failed", "ExecuteEvmCall", "substate.ShouldRevert || substate.IsError");
        Add("vm.restore", "ExecuteEvmCall", "WorldState.Restore(snapshot)");
        Add("vm.ripemd", "ExecuteEvmCall", "VirtualMachineStatics.RestoreRipemdTouch(WorldState, spec, substate.ShouldRestoreRipemdTouch)");
        Add("refund.input", "ExecuteEvmCall", "Refund(tx, header, spec, opts, in substate, gasAvailable, VirtualMachine.TxExecutionContext.GasPrice, (ulong)delegationRefunds, gas.FloorGas, gas.Standard, postIntrinsicStateReservoir, topLevelCreateStateGasCharged)");
        Add("processor.route", "ExecuteEvmTransaction", "UpdateHeaderGasUsedAndPayFees(tx, header, spec, tracer, opts, in substate, in spentGas, premiumPerGas, in opcodeGasPrice, blobBaseFee, statusCode)");
        Add("processor.participation", "UpdateHeaderGasUsedAndPayFees", "SystemTransactionRoutingKernel.ParticipatesInNormalBlockCounters(opts, _parallel)");
        Add("processor.execution", "UpdateHeaderGasUsedAndPayFees", "_blockCumulativeExecutionGas += spentGas.EffectiveBlockGas");
        Add("processor.state", "UpdateHeaderGasUsedAndPayFees", "_blockCumulativeStateGas += spentGas.BlockStateGas");
        Add("processor.header", "UpdateHeaderGasUsedAndPayFees", "header.GasUsed = TGasPolicy.CombineBlockGas(_blockCumulativeExecutionGas, _blockCumulativeStateGas)");
        Add("processor.fees", "UpdateHeaderGasUsedAndPayFees", "PayFees(tx, header, spec, tracer, in substate, spentGas.SpentGas, premiumPerGas, in effectiveGasPrice, blobBaseFee, statusCode)");
        Add("fees.premium", "PayFees", "premiumPerGas * spentGas");
        Add("fees.beneficiary", "PayFees", "statusCode == StatusCode.Failure || gasBeneficiaryNotDestroyed || spec.IsEip8037Enabled");
        Add("fees.beneficiary-credit", "PayFees", "WorldState.AddToBalanceAndCreateIfNotExists(header.GasBeneficiary!, fees, spec)");
        Add("fees.base", "PayFees", "UInt256.Min(header.BaseFeePerGas, effectiveGasPrice)");
        Add("fees.free", "PayFees", "!tx.IsFree() ? effectiveBaseFee * spentGas : UInt256.Zero");
        Add("fees.enabled", "PayFees", "spec.IsEip1559Enabled ? eip1559Fees : UInt256.Zero");
        Add("fees.blob", "PayFees", "tx.SupportsBlobs && spec.IsEip4844FeeCollectorEnabled");
        Add("fees.blob-add", "PayFees", "collectedFees += blobBaseFee");
        Add("fees.collector", "PayFees", "spec.FeeCollector is not null && !collectedFees.IsZero");
        Add("fees.collector-credit", "PayFees", "WorldState.AddToBalanceAndCreateIfNotExists(spec.FeeCollector, collectedFees, spec)");
        Add("fees.report", "PayFees", "tracer.ReportFees(fees, eip1559Fees + blobBaseFee)");
        Add("finalize.route", "ExecuteEvmTransaction", "FinalizeTransaction(tx, spec, tracer, opts, restore, commit, deleteCallerAccount, in senderReservedGasPayment, env.ExecutingAccount, in substate, spentGas, statusCode)");
        Add("finalize.block-gas", "FinalizeTransaction", "tx.BlockGasUsed = spentGas.EffectiveBlockGas");
        Add("finalize.spent-gas", "FinalizeTransaction", "tx.SpentGas = spentGas.SpentGas");
        Add("finalize.commit", "FinalizeTransaction", "WorldState.Commit(spec, tracer.IsTracingState ? tracer : NullStateTracer.Instance, commitRoots: !spec.IsEip658Enabled)");
        Add("finalize.failed", "FinalizeTransaction", "statusCode == StatusCode.Failure");
        Add("finalize.failed-output", "FinalizeTransaction", "substate.ShouldRevert ? substate.Output.AsReadOnlyArray() : []");
        Add("receipt.failed", "FinalizeTransaction", "tracer.MarkAsFailed(executingAccount, spentGas, output, error, stateRoot)");
        Add("receipt.success", "FinalizeTransaction", "tracer.MarkAsSuccess(executingAccount, spentGas, substate.Output.AsReadOnlyArray(), logs, stateRoot)");
        Add("frame.initial-state-gas", "Initialize", "InitialStateGasUsed = TGasPolicy.GetStateGasUsed(in gas)");

        RequireVariable("caller.restore", "restore");
        RequireVariable("caller.commit", "commit");
        RequireVariable("prepared.standard-and-floor", "executionIntrinsicGas");
        RequireAssignment("vm.gas-copy", "gasAvailable");
        RequireAssignment("refund.input", "gasConsumed");
        RequireVariable("fees.premium", "fees");
        RequireVariable("fees.base", "effectiveBaseFee");
        RequireVariable("fees.free", "eip1559Fees");
        RequireVariable("fees.enabled", "collectedFees");
        RequireAssignment("finalize.block-gas", "tx.BlockGasUsed");
        RequireAssignment("finalize.spent-gas", "tx.SpentGas");
        RequireOrder("vm.off", "vm.on", "vm.metrics", "vm.flush", "vm.gas-copy", "vm.access", "vm.failed", "vm.ripemd", "refund.input");
        RequireOrder("prepared.call.off", "prepared.call.on", "processor.route", "finalize.route");
        RequireOrder("processor.execution", "processor.state", "processor.header", "processor.fees");
        RequireOrder("fees.premium", "fees.beneficiary", "fees.base", "fees.free", "fees.enabled", "fees.blob", "fees.collector", "fees.report");
        RequireOrder("finalize.block-gas", "finalize.spent-gas", "finalize.commit", "finalize.failed", "receipt.failed", "receipt.success");

        SourceAdmission.MethodSite call = methods["ExecuteEvmCall"];
        UsingStatementSyntax[] frames = call.Syntax.DescendantNodes().OfType<UsingStatementSyntax>().ToArray();
        if (frames.Length != 1 || frames[0].Declaration?.Type.ToString() != "VmState<TGasPolicy>" ||
            frames[0].Span.End >= sites.Single(static site => site.Role == "refund.input").Start)
            throw new AdmissionException("cleanup.frame-before-refund");
        foreach (string role in new[] { "vm.metrics", "vm.flush", "vm.gas-copy" })
            RequireDirectBlock(role, frames[0].Statement);
        foreach (string role in new[] { "caller.restore", "caller.commit", "caller.entry", "prepared.standard-and-floor", "prepared.call.off", "prepared.call.on", "refund.input", "processor.route", "processor.fees", "finalize.route", "fees.premium", "fees.base", "fees.free", "fees.enabled" })
        {
            ExpressionSite site = sites.Single(site => site.Role == role);
            RequireDirectBlock(role, methods.Values.Single(method => OperationLowering.Symbol(method.Symbol) == site.Owner).Syntax.Body!);
        }
        IfStatementSyntax failed = frames[0].DescendantNodes().OfType<IfStatementSyntax>()
            .Single(statement => Canonical(statement.Condition) == Canonical(SyntaxFactory.ParseExpression("substate.ShouldRevert || substate.IsError")));
        if (failed.Parent != frames[0].Statement) throw new AdmissionException("branch.owner.vm-failed");
        InvocationExpressionSyntax[] restores = failed.Statement.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(static invocation => Canonical(invocation) == "WorldState.Restore(snapshot)").ToArray();
        if (restores.Length != 1 || restores[0].SpanStart >= sites.Single(static site => site.Role == "vm.ripemd").Start)
            throw new AdmissionException("rollback.snapshot-before-ripemd");
        if (failed.Else is null || !failed.Else.Statement.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Any(static assignment => Canonical(assignment) == "statusCode=StatusCode.Success") ||
            failed.Statement.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Any(static assignment => Canonical(assignment.Left) == "statusCode"))
            throw new AdmissionException("branch.vm-failure-polarity");
        RequireThen("vm.access", "tracer.IsTracingAccess");
        RequireThen("processor.execution", "spec.IsEip8037Enabled");
        RequireThen("processor.state", "spec.IsEip8037Enabled");
        RequireThen("processor.header", "spec.IsEip8037Enabled");
        foreach (string role in new[] { "processor.execution", "processor.state", "processor.header" })
            RequireOwners(role, "spec.IsEip8037Enabled", "SystemTransactionRoutingKernel.ParticipatesInNormalBlockCounters(opts, _parallel)");
        RequireThen("receipt.failed", "statusCode == StatusCode.Failure");
        RequireElse("receipt.success", "statusCode == StatusCode.Failure");
        RequireThen("finalize.commit", "commit");
        RequireThen("fees.beneficiary-credit", "statusCode == StatusCode.Failure || gasBeneficiaryNotDestroyed || spec.IsEip8037Enabled");
        RequireThen("fees.blob-add", "tx.SupportsBlobs && spec.IsEip4844FeeCollectorEnabled");
        RequireThen("fees.collector-credit", "spec.FeeCollector is not null && !collectedFees.IsZero");
        RequireDominates("vm.metrics", "vm.flush");
        RequireDominates("vm.flush", "vm.gas-copy");
        RequireDominates("vm.gas-copy", "refund.input");
        foreach (ExpressionSite site in sites) RequireReachable(site.Role);
        SourceAdmission.MethodSite outer = methods["ExecuteEvmTransaction"];
        LocalDeclarationStatementSyntax[] scopes = outer.Syntax.Body!.Statements.OfType<LocalDeclarationStatementSyntax>()
            .Where(static statement => !statement.UsingKeyword.IsKind(SyntaxKind.None)).ToArray();
        if (scopes.Length != 2 || scopes[0].Declaration.Variables.Single().Identifier.Text != "accessTracker" ||
            scopes[1].Declaration.Variables.Single().Identifier.Text != "env" || outer.Syntax.Body.Statements.Last() is not ReturnStatementSyntax)
            throw new AdmissionException("cleanup.environment-before-access");

        return new(sites.ToArray(),
        [
            ContinuationStage.VmReturn, ContinuationStage.Metrics, ContinuationStage.CopyFrameGas,
            ContinuationStage.AccessReport, ContinuationStage.Rollback, ContinuationStage.DisposeFrame,
            ContinuationStage.Refund, ContinuationStage.ProcessorCounters, ContinuationStage.PayFees,
            ContinuationStage.TransactionGas, ContinuationStage.Commit, ContinuationStage.Receipt,
            ContinuationStage.DisposeEnvironment, ContinuationStage.DisposeAccessTracker, ContinuationStage.Return,
        ]);

        void Add(string role, string owner, string expression)
        {
            SourceAdmission.MethodSite method = methods[owner];
            string expected = Canonical(SyntaxFactory.ParseExpression(expression));
            ExpressionSyntax[] candidates = method.Syntax.DescendantNodes().OfType<ExpressionSyntax>()
                .Where(node => Canonical(node) == expected &&
                    (!role.StartsWith("vm.", StringComparison.Ordinal) || node.Ancestors().OfType<UsingStatementSyntax>().Any())).ToArray();
            if (candidates.Length != 1) throw new AdmissionException("site." + role);
            ExpressionSyntax syntax = candidates[0];
            IOperation operation = method.Model.GetOperation(syntax) ?? throw new AdmissionException("operation." + role);
            if (operation is IInvocationOperation invocation &&
                invocation.TargetMethod.MethodKind is MethodKind.LocalFunction or MethodKind.DelegateInvoke)
                throw new AdmissionException("callee." + role);
            if (operation is IInvocationOperation call)
            {
                string? expectedType = role switch
                {
                    "caller.entry" or "prepared.call.off" or "prepared.call.on" or "refund.input" or "processor.route" or "processor.fees" or "finalize.route" => "Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<TGasPolicy>",
                    "vm.off" or "vm.on" or "vm.flush" => "Nethermind.Evm.IVirtualMachine<TGasPolicy>",
                    "vm.metrics" => "Nethermind.Evm.Metrics",
                    "vm.access" or "receipt.failed" or "receipt.success" or "fees.report" => "Nethermind.Evm.Tracing.ITxTracer",
                    "vm.ripemd" => "Nethermind.Evm.VirtualMachineStatics",
                    "frame.entry" => "Nethermind.Evm.VmState<TGasPolicy>",
                    "processor.participation" => "Nethermind.Evm.TransactionProcessing.SystemTransactionRoutingKernel",
                    "fees.base" => "Nethermind.Int256.UInt256",
                    "fees.beneficiary-credit" or "fees.collector-credit" => "Nethermind.Evm.State.WorldStateExtensions",
                    "vm.restore" => "Nethermind.Core.IJournal<TSnapshot>",
                    _ => null,
                };
                if (expectedType is not null && call.TargetMethod.ContainingType.OriginalDefinition.ToDisplayString() != expectedType)
                    throw new AdmissionException("callee." + role);
                if (role == "refund.input")
                {
                    string[] names = ["tx", "header", "spec", "opts", "substate", "unspentGas", "gasPrice", "codeInsertRefunds", "floorGas", "intrinsicGasStandard", "postIntrinsicStateReservoir", "topLevelCreateStateGasCharged"];
                    RefKind[] refs = [RefKind.None, RefKind.None, RefKind.None, RefKind.None, RefKind.In, RefKind.In, RefKind.In, RefKind.None, RefKind.In, RefKind.In, RefKind.None, RefKind.None];
                    if (!call.TargetMethod.Parameters.Select(static parameter => parameter.Name).SequenceEqual(names, StringComparer.Ordinal) ||
                        !call.TargetMethod.Parameters.Select(static parameter => parameter.RefKind).SequenceEqual(refs) ||
                        call.TargetMethod.ReturnType.ToDisplayString() != "Nethermind.Evm.TransactionProcessing.GasConsumed")
                        throw new AdmissionException("signature.refund.input");
                }
                foreach (IArgumentOperation argument in call.Arguments)
                    if (argument.Parameter is null ||
                        argument.ArgumentKind != ArgumentKind.Explicit && !(role == "finalize.commit" && argument.ArgumentKind == ArgumentKind.DefaultValue &&
                            argument.Parameter.Name == "isGenesis" && argument.Value.ConstantValue is { HasValue: true, Value: false }) ||
                        argument.InConversion.IsUserDefined || argument.OutConversion.IsUserDefined)
                        throw new AdmissionException("argument." + role);
            }
            sites.Add(new(role, OperationLowering.Symbol(method.Symbol), expected, syntax.SpanStart, syntax.Span.Length, OperationLowering.Lower(operation)));
        }

        SyntaxNode Node(string role)
        {
            ExpressionSite site = sites.Single(site => site.Role == role);
            return methods.Values.Single(method => OperationLowering.Symbol(method.Symbol) == site.Owner).Syntax
                .DescendantNodes().Single(node => node is ExpressionSyntax && node.SpanStart == site.Start && node.Span.Length == site.Length);
        }

        void RequireVariable(string role, string name)
        {
            SyntaxNode node = Node(role);
            if (node.Parent is not EqualsValueClauseSyntax initializer || initializer.Parent is not VariableDeclaratorSyntax variable || variable.Identifier.Text != name)
                throw new AdmissionException("role." + role);
        }

        void RequireDirectBlock(string role, SyntaxNode block)
        {
            StatementSyntax? statement = Node(role).AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();
            if (statement?.Parent != block) throw new AdmissionException("branch.direct." + role);
        }

        void RequireAssignment(string role, string target)
        {
            SyntaxNode node = Node(role);
            AssignmentExpressionSyntax? assignment = node as AssignmentExpressionSyntax ?? node.Parent as AssignmentExpressionSyntax;
            if (assignment is null || Canonical(assignment.Left) != target ||
                node is not AssignmentExpressionSyntax && assignment.Right != node)
                throw new AdmissionException("role." + role);
        }

        void RequireOrder(params string[] roles)
        {
            for (int index = 1; index < roles.Length; index++)
                if (Node(roles[index - 1]).SpanStart >= Node(roles[index]).SpanStart)
                    throw new AdmissionException("order." + roles[index - 1] + "." + roles[index]);
        }

        void RequireThen(string role, string condition)
        {
            SyntaxNode node = Node(role);
            string expected = Canonical(SyntaxFactory.ParseExpression(condition));
            IfStatementSyntax? statement = node.Ancestors().OfType<IfStatementSyntax>().FirstOrDefault();
            if (statement is null || Canonical(statement.Condition) != expected || !statement.Statement.Span.Contains(node.Span))
                throw new AdmissionException("branch.then." + role);
            StatementSyntax? containing = node.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();
            if (containing?.Parent != statement.Statement && containing != statement.Statement)
                throw new AdmissionException("branch.direct." + role);
        }

        void RequireElse(string role, string condition)
        {
            SyntaxNode node = Node(role);
            string expected = Canonical(SyntaxFactory.ParseExpression(condition));
            IfStatementSyntax? statement = node.Ancestors().OfType<IfStatementSyntax>().FirstOrDefault();
            if (statement is null || Canonical(statement.Condition) != expected || statement.Else?.Statement.Span.Contains(node.Span) != true)
                throw new AdmissionException("branch.else." + role);
            StatementSyntax? containing = node.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();
            if (containing?.Parent != statement.Else.Statement && containing != statement.Else.Statement)
                throw new AdmissionException("branch.direct." + role);
        }

        void RequireOwners(string role, params string[] conditions)
        {
            string[] actual = Node(role).Ancestors().OfType<IfStatementSyntax>().Select(statement => Canonical(statement.Condition)).ToArray();
            if (!actual.SequenceEqual(conditions.Select(condition => Canonical(SyntaxFactory.ParseExpression(condition))), StringComparer.Ordinal))
                throw new AdmissionException("branch.owners." + role);
        }

        (ControlFlowGraph Graph, BasicBlock Block) Flow(string role)
        {
            SyntaxNode node = Node(role);
            SourceAdmission.MethodSite method = methods.Values.Single(method => method.Syntax == node.Ancestors().OfType<MethodDeclarationSyntax>().First());
            BasicBlock[] blocks = method.Graph.Blocks.Where(block => block.Operations.SelectMany(Descendants)
                .Concat(block.BranchValue is null ? [] : Descendants(block.BranchValue))
                .Any(operation => operation.Syntax.Span == node.Span)).ToArray();
            if (blocks.Length != 1) throw new AdmissionException("cfg.site." + role);
            return (method.Graph, blocks[0]);
        }

        void RequireReachable(string role)
        {
            SyntaxNode node = Node(role);
            SourceAdmission.MethodSite method = methods.Values.Single(method => method.Syntax == node.Ancestors().OfType<MethodDeclarationSyntax>().First());
            bool reachable = method.Graph.Blocks.Any(block => block.IsReachable && block.Operations.SelectMany(Descendants)
                .Concat(block.BranchValue is null ? [] : Descendants(block.BranchValue))
                .Any(operation => node.Span.Contains(operation.Syntax.Span)));
            if (!reachable) throw new AdmissionException("cfg.unreachable." + role);
        }

        void RequireDominates(string before, string after)
        {
            (ControlFlowGraph graph, BasicBlock first) = Flow(before);
            (ControlFlowGraph other, BasicBlock last) = Flow(after);
            if (graph != other || !first.IsReachable || !last.IsReachable)
                throw new AdmissionException("cfg.dominates." + before + "." + after);
            if (first == last)
            {
                if (Node(before).SpanStart >= Node(after).SpanStart)
                    throw new AdmissionException("cfg.dominates." + before + "." + after);
                return;
            }
            HashSet<int> visited = [];
            Stack<BasicBlock> pending = new();
            pending.Push(graph.Blocks[0]);
            while (pending.TryPop(out BasicBlock? block))
            {
                if (block == first || !block.IsReachable || !visited.Add(block.Ordinal)) continue;
                if (block == last) throw new AdmissionException("cfg.dominates." + before + "." + after);
                if (block.FallThroughSuccessor?.Destination is { } fallThrough) pending.Push(fallThrough);
                if (block.ConditionalSuccessor?.Destination is { } conditional) pending.Push(conditional);
            }
        }
    }

    private static IEnumerable<IOperation> Descendants(IOperation operation)
    {
        yield return operation;
        foreach (IOperation child in operation.ChildOperations)
            foreach (IOperation descendant in Descendants(child)) yield return descendant;
    }

    private static string Canonical(SyntaxNode syntax) => string.Concat(syntax.DescendantTokens().Select(static token => token.Text));
}
