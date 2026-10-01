// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Evm.Lean.SimpleTransferCompletionExtractor;

internal static class StageBLowering
{
    private const string Processor = "Nethermind.Evm.TransactionProcessing.TransactionProcessorBase`1";
    private const string Policy = "Nethermind.Evm.GasPolicy.EthereumGasPolicy";
    private const string PolicyInterface = "Nethermind.Evm.GasPolicy.IGasPolicy`1";
    private static readonly SymbolDisplayFormat IdentityFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMemberOptions(SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters |
            SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeRef | SymbolDisplayMemberOptions.IncludeExplicitInterface)
        .WithParameterOptions(SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeType |
            SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeDefaultValue);

    private static readonly (string Type, string Member, int Arity)[] LocalMethods =
    [
        (Processor, "Execute", 6), (Processor, "PrepareSimpleTransferFastPath", 4),
        (Processor, "IsSimpleTransferFastPathCandidate", 2), (Processor, "HasNoExecutableCode", 2),
        (Processor, "CalculateAvailableGas", 4), (Processor, "ExecuteSimpleTransfer", 15),
        (Processor, "PayValue", 3), (Processor, "TraceSimpleTransferActionStart", 5),
        (Processor, "ReportSimpleTransferAccess", 4), (Processor, "WarmUpTxAccesses", 5),
        (Processor, "UpdateHeaderGasUsedAndPayFees", 11), (Processor, "PayFees", 10),
        (Processor, "FinalizeTransaction", 12),
        (Policy, "TryCreateAvailableFromIntrinsic", 4), (Policy, "TryConsumeStateGas", 2),
        (Policy, "ClearExecutionGas", 1), (Policy, "GetRemainingGas", 1),
        (Policy, "GetStateReservoir", 1), (Policy, "CombineBlockGas", 2),
        (PolicyInterface, "GetNewAccountStateCost", 0),
        ("Nethermind.Evm.GasPolicy.BlockGasAccountingKernel", "Combine", 2),
        ("Nethermind.Evm.TransactionProcessing.SystemTransactionRoutingKernel", "ParticipatesInNormalBlockCounters", 2),
        ("Nethermind.Evm.TransferLog", "CreateTransfer", 3),
        ("Nethermind.Evm.TransferLog", "CreateTransferInternal", 4),
        ("Nethermind.Evm.TransactionSubstate", ".ctor", 8),
        ("Nethermind.Evm.TransactionSubstate", "DestroyListContains", 1),
        ("Nethermind.Evm.TransactionSubstate", "LogsToArray", 0),
        ("Nethermind.Evm.StackAccessTracker", "WarmUp", 1),
        ("Nethermind.Evm.StackAccessTracker", "Dispose", 0),
        ("Nethermind.Evm.EvmExceptionTypeExtensions", "FastToString", 1),
        ("Nethermind.Evm.TransactionProcessing.TransactionResult", ".ctor", 3),
        ("Nethermind.Evm.TransactionProcessing.TransactionResult", "EvmException", 2),
        ("Nethermind.Evm.TransactionProcessing.TransactionResult", "op_Implicit", 1),
        ("Nethermind.Evm.State.WorldStateExtensions", "SubtractFromBalance", 4),
        ("Nethermind.Evm.State.WorldStateExtensions", "AddToBalance", 4),
        ("Nethermind.Evm.State.WorldStateExtensions", "AddToBalanceAndCreateIfNotExists", 4),
    ];

    private static readonly Dictionary<string, string[]> ExternalMembers = new(StringComparer.Ordinal)
    {
        ["Nethermind.Evm.Metrics"] = ["IncrementEmptyCalls"],
        ["Nethermind.Evm.ICodeInfoRepository"] = ["GetCachedCodeInfo"],
        ["Nethermind.Evm.State.IReadOnlyStateProvider"] = ["IsDeadAccount", "GetBalance"],
        ["Nethermind.Evm.State.IWorldState"] = ["IsDeadAccount", "GetBalance", "SubtractFromBalance", "AddToBalance", "AddToBalanceAndCreateIfNotExists", "Commit"],
        ["Nethermind.Evm.Tracing.ITxTracer"] = ["ReportAction", "ReportByteCode", "ReportActionError", "ReportActionEnd", "ReportLog", "ReportAccess", "ReportFees"],
        ["Nethermind.Evm.StackAccessTracker"] = [".ctor"],
        ["Nethermind.Evm.GasPolicy.EthereumGasPolicy"] = [".ctor"],
        ["Nethermind.Evm.StackAccessTracker.TrackingState"] = ["ResetAndReturn"],
        ["Nethermind.Core.Collections.JournalSet<T>"] = ["Add", "Contains"],
        ["Nethermind.Core.Collections.JournalCollection<T>"] = [".ctor", "Add", "ToArray"],
        ["Nethermind.Core.StorageCell"] = [".ctor"],
        ["Nethermind.Core.LogEntry"] = [".ctor"],
        ["Nethermind.Core.Address"] = ["ToHash", "op_Equality", "op_Inequality"],
        ["Nethermind.Core.Crypto.ValueHash256"] = ["ToHash256"],
        ["Nethermind.Core.Crypto.Hash256"] = [".ctor"],
        ["Nethermind.Core.TransactionExtensions"] = ["IsFree"],
        ["Nethermind.Evm.ReadOnlyMemoryExtensions"] = ["AsReadOnlyArray"],
        ["System.Enum"] = ["HasFlag"],
        ["System.Math"] = ["Max"],
        ["int"] = ["ToString"],
        ["System.IDisposable"] = ["Dispose"],
        ["Nethermind.Core.Eip2930.AccessList"] = ["GetEnumerator"],
        ["Nethermind.Core.Eip2930.AccessList.Enumerator"] = ["MoveNext", "Dispose"],
        ["Nethermind.Core.Eip2930.AccessList.StorageKeysEnumerable"] = ["GetEnumerator"],
        ["Nethermind.Core.Eip2930.AccessList.StorageKeysEnumerator"] = ["MoveNext", "Dispose"],
        ["System.Collections.Generic.IEnumerable<T>"] = ["GetEnumerator"],
        ["System.Collections.IEnumerator"] = ["MoveNext"],
        ["System.Runtime.CompilerServices.SwitchExpressionException"] = [".ctor"],
    };

    private static readonly HashSet<string> DataTypes = new(StringComparer.Ordinal)
    {
        "Nethermind.Core.Transaction", "Nethermind.Core.BlockHeader", "Nethermind.Core.Specs.IReleaseSpec",
        "Nethermind.Core.Specs.IReleaseSpecExtensions",
        "Nethermind.Core.Specs.IReceiptSpec", "Nethermind.Core.Specs.IEip1559Spec", "Nethermind.Core.Specs.IExecutionSpec",
        "Nethermind.Evm.Tracing.ITxTracer", "Nethermind.Evm.Tracing.State.IWorldStateTracer", "Nethermind.Evm.Tracing.State.IStateTracer",
        "Nethermind.Evm.TransactionSubstate", "Nethermind.Evm.StackAccessTracker", "Nethermind.Evm.StackAccessTracker.TrackingState",
        "Nethermind.Evm.BlockExecutionContext", "Nethermind.Evm.IVirtualMachine<TGasPolicy>",
        "Nethermind.Evm.CodeAnalysis.CodeInfo", "Nethermind.Int256.UInt256",
        "Nethermind.Core.Collections.JournalSet<T>", "Nethermind.Core.Collections.JournalCollection<T>",
        "Nethermind.Core.Eip2930.AccessList", "System.ReadOnlyMemory<T>",
        "Nethermind.Evm.TransactionProcessing.TransactionResult",
        "Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<TGasPolicy>", "Nethermind.Evm.GasPolicy.IntrinsicGas<TGasPolicy>",
        "Nethermind.Evm.TransactionProcessing.TransactionProcessorBase",
        "Nethermind.Core.Eip7825Constants", "Nethermind.Core.GasCostOf",
        "Nethermind.Core.Address",
        "Nethermind.Evm.Tracing.NullTxTracer", "Nethermind.Evm.Tracing.State.NullStateTracer",
        "Nethermind.Evm.TransactionProcessing.GasConsumed", "Nethermind.Evm.GasPolicy.StateGasChargeResult",
        "Nethermind.Evm.GasPolicy.TransactionGasInitializationResult",
        "Nethermind.Core.Eip2930.AccessList.Enumerator", "Nethermind.Core.Eip2930.AccessList.StorageKeysEnumerator",
        "System.Collections.Generic.IEnumerator<T>",
        "Nethermind.Evm.GasPolicy.EthereumGasPolicy", "Nethermind.Evm.TransferLog",
    };

    internal static StageBPlan Build(string repoRoot, IReadOnlyDictionary<string, string>? overrides = null)
    {
        CompilerClosure baseline = CompilerSources.Load(repoRoot);
        CompilerClosure candidate = overrides is null ? baseline : CompilerSources.Load(repoRoot, overrides);
        Builder builder = new(baseline, candidate);
        return builder.Build();
    }

    private sealed class Builder(CompilerClosure baseline, CompilerClosure candidate)
    {
        private readonly Dictionary<string, StageBCallee> _allowed = new(StringComparer.Ordinal);
        private readonly Dictionary<string, StageBCallee> _used = new(StringComparer.Ordinal);
        private readonly List<ISymbol> _locals = [];
        private readonly List<StageBStage> _stages = [];
        private readonly Dictionary<string, StageBMember> _members = new(StringComparer.Ordinal);
        private readonly Dictionary<string, StageBType> _types = new(StringComparer.Ordinal);

        internal StageBPlan Build()
        {
            foreach ((string type, string member, int arity) in LocalMethods)
            {
                INamedTypeSymbol owner = Type(baseline, type);
                IMethodSymbol[] methods = owner.GetMembers(member).OfType<IMethodSymbol>()
                    .Where(method => method.Parameters.Length == arity &&
                        (type != "Nethermind.Evm.StackAccessTracker" || member != "WarmUp" ||
                         method.Parameters[0].Type.Name is "Address" or "AccessList")).ToArray();
                if (methods.Length == 0) throw Error("local-missing", type + "." + member);
                foreach (IMethodSymbol method in methods) AddLocal(method);
            }
            foreach ((string type, string member) in new[]
            {
                ("Nethermind.Evm.TransactionProcessing.GasConsumed", "EffectiveBlockGas"),
                ("Nethermind.Evm.TransactionSubstate", "IsError"), ("Nethermind.Evm.TransactionSubstate", "Logs"),
                ("Nethermind.Evm.TransactionSubstate", "DestroyList"),
                ("Nethermind.Evm.TransactionProcessing.TransactionResult", "TransactionExecuted"),
                ("Nethermind.Evm.StackAccessTracker", "AccessedAddresses"), ("Nethermind.Evm.StackAccessTracker", "AccessedStorageCells"),
                ("Nethermind.Evm.StackAccessTracker", "Logs"), ("Nethermind.Evm.StackAccessTracker", "DestroyList"),
            }) AddLocal(Type(baseline, type).GetMembers(member).Single());
            foreach ((string type, string member) in new[]
            {
                ("Nethermind.Evm.TransferLog", "TransferSignature"), ("Nethermind.Evm.TransferLog", "Sender"),
                ("Nethermind.Evm.TransactionProcessing.TransactionResult", "Ok"),
                ("Nethermind.Evm.TransactionProcessing.TransactionResult", "GasLimitBelowIntrinsicGas"),
            }) AddLocal(Type(baseline, type).GetMembers(member).Single());
            foreach (INamedTypeSymbol owner in _locals.OfType<IFieldSymbol>().Select(static field => field.ContainingType)
                .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default))
                RequireStaticInitializers(owner);
            Stage("Nethermind.Evm.GasPolicy.TransactionGasInitializationKernel", ["TryCreate"], "transaction-initialization",
                "Eip803x.Refinement.TransactionGasInitialization.generatedTryCreate_refines_initializeTransactionGas");
            Stage("Nethermind.Evm.GasPolicy.StateGasChargeKernel", ["TryCharge"], "state-charge",
                "Eip803x.Refinement.StateGasCharge.generatedTryCharge_refines_chargeState");
            Stage(Processor, ["Refund"], "ordinary-refund",
                "OrdinaryTransactionRefundAdapterExtractor.Refinement.OrdinaryTransactionRefund.source_attached_refines");
            Stage("Nethermind.Evm.Tracing.ITxTracer", ["MarkAsSuccess", "MarkAsFailed"], "receipt-terminal",
                "ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold.generatedFinalizeTransaction_refines_spec");

            List<StageBMethod> plans = [];
            foreach (ISymbol original in _locals)
            {
                INamedTypeSymbol owner = Type(candidate, MetadataName(original.ContainingType));
                ISymbol[] matches = owner.GetMembers(original.Name).Where(symbol => Identity(symbol) == Identity(original)).ToArray();
                if (matches is not [ISymbol method]) throw Error("local-identity", Identity(original));
                plans.Add(LowerMethod(method));
            }
            return new("post-successful-nonce; ordinary Ethereum; sequential Commit; simple recipient; receipt tracing; no state tracing; EIP-658",
                candidate.EffectiveSources, candidate.References, plans.ToArray(),
                _used.Values.OrderBy(static item => item.Symbol, StringComparer.Ordinal).ToArray(),
                _stages.ToArray(),
                ["Earlier validation, price locals, gas purchase and nonce update have normal-return provenance.",
                 "Standard-mainnet runtime dispatch and pinned fork flags hold; VM context header, processor header and receipt block header alias the same header.",
                 "CodeInfo.IsEmpty retains the pinned empty-code sentinel semantics, not an arbitrary zero-code-length predicate.",
                 "BlockHeader.GasBeneficiary projects Author ?? Beneficiary from the aliased header.",
                 "Code lookup and dead-account observations are typed read requests. World writes affect only their declared targets; normal non-reentrant nonterminal tracer hooks do not mutate caller locals or caller-owned reachable heap objects, including LogEntry and its topic/data arrays.",
                 "ReportAccess borrows collections only during the callback and snapshots observations before Dispose; no callback retains the returned pooled collections.",
                 "Pool rent returns fresh empty access collections; Dispose calls ResetAndReturn, which clears all collections before pooling. Collection/enumeration primitives retain their typed contracts.",
                 "Address/hash/byte, UInt256, enum flags and framework primitives have their typed representation contracts.",
                 "Referenced types have completed pinned CLR static initialization normally before entry; selected readonly initializers bind value provenance, not concurrent type-initialization effects.",
                 "The four accepted stage bodies and artifacts require dependency pin validation before this plan can be promoted."])
            {
                Members = _members.Values.OrderBy(static member => member.Symbol, StringComparer.Ordinal).ToArray(),
                Types = _types.Values.OrderBy(static type => type.Symbol, StringComparer.Ordinal).ToArray(),
                PrefixAnchors = new(
                    Local("Execute", 6), Local("ExecuteSimpleTransfer", 15),
                    _stages.Single(static stage => stage.Name == "ordinary-refund").EntryPoints.Single(), Identity(Type(candidate, Policy)),
                    [Local("ReportSimpleTransferAccess", 4), Local("WarmUpTxAccesses", 5), Local("UpdateHeaderGasUsedAndPayFees", 11),
                     Local("PayFees", 10), Local("FinalizeTransaction", 12)]),
            };

            string Local(string name, int arity) => Identity(_locals.OfType<IMethodSymbol>().Single(method =>
                MetadataName(method.ContainingType) == Processor && method.Name == name && method.Parameters.Length == arity));

            void AddLocal(ISymbol symbol)
            {
                _locals.Add(symbol);
                Add(symbol, StageBCalleeOwner.Local, Identity(symbol));
            }
            void Stage(string type, string[] members, string contract, string theorem)
            {
                IMethodSymbol[] entries = members.SelectMany(member => Type(baseline, type).GetMembers(member).OfType<IMethodSymbol>()).ToArray();
                if (entries.Length != members.Length) throw Error("stage-entry", contract);
                Dictionary<string, StageBStageDependency> closure = new(StringComparer.Ordinal);
                foreach (IMethodSymbol method in entries)
                {
                    Add(method, StageBCalleeOwner.AcceptedStage, contract);
                    Visit(method);
                }
                _stages.Add(new(contract, theorem, entries.Select(Identity).ToArray(),
                    closure.Values.OrderBy(static dependency => dependency.Symbol, StringComparer.Ordinal).ToArray()));

                void Visit(ISymbol dependency)
                {
                    dependency = dependency.OriginalDefinition;
                    if (dependency.DeclaringSyntaxReferences.Length == 0) return;
                    string identity = Identity(dependency);
                    if (closure.ContainsKey(identity)) return;
                    ISymbol[] matches = Type(candidate, MetadataName(dependency.ContainingType)).GetMembers(dependency.Name)
                        .Where(member => Identity(member) == identity).ToArray();
                    if (matches is not [ISymbol replacement] || SourceText(dependency) != SourceText(replacement))
                        throw Error("accepted-stage-dependency", identity);
                    string path = dependency.DeclaringSyntaxReferences[0].SyntaxTree.FilePath;
                    closure.Add(identity, new(identity, path, CompilerReferences.Hash(System.Text.Encoding.UTF8.GetBytes(SourceText(dependency)))));
                    SyntaxReference[] originalDeclarations = dependency.DeclaringSyntaxReferences.ToArray();
                    SyntaxReference[] actualDeclarations = replacement.DeclaringSyntaxReferences.ToArray();
                    if (originalDeclarations.Length != actualDeclarations.Length) throw Error("accepted-stage-binding", identity);
                    for (int index = 0; index < originalDeclarations.Length; index++)
                    {
                        SyntaxNode node = originalDeclarations[index].GetSyntax();
                        SyntaxNode actualNode = actualDeclarations[index].GetSyntax();
                        SemanticModel model = baseline.Compilation.GetSemanticModel(node.SyntaxTree);
                        SemanticModel actualModel = candidate.Compilation.GetSemanticModel(actualNode.SyntaxTree);
                        IOperation? body = Body(node, model);
                        IOperation? actualBody = Body(actualNode, actualModel);
                        if ((body is null) != (actualBody is null)) throw Error("accepted-stage-binding", identity);
                        if (body is not null) Walk(body, actualBody!, model, actualModel, identity, node, actualNode);
                    }

                    static IOperation? Body(SyntaxNode node, SemanticModel model) => model.GetOperation(node) ?? (node switch
                        {
                            PropertyDeclarationSyntax { ExpressionBody: { } arrow } => model.GetOperation(arrow.Expression),
                            VariableDeclaratorSyntax { Initializer: { } initializer } => model.GetOperation(initializer.Value),
                            _ => null,
                        });
                }
                void Walk(IOperation operation, IOperation actual, SemanticModel model, SemanticModel actualModel, string owner, SyntaxNode origin, SyntaxNode actualOrigin)
                {
                    ISymbol[] dependencies = SemanticMembers(operation, model);
                    ISymbol[] actualDependencies = SemanticMembers(actual, actualModel);
                    if (operation.Kind != actual.Kind || TypeKey(operation.Type) != TypeKey(actual.Type) || operation.IsImplicit != actual.IsImplicit ||
                        operation.ConstantValue.HasValue != actual.ConstantValue.HasValue ||
                        operation.ConstantValue.HasValue && Constant(operation.ConstantValue.Value) != Constant(actual.ConstantValue.Value) ||
                        OperationBinding(operation, origin) != OperationBinding(actual, actualOrigin) ||
                        !dependencies.Select(MemberKey).SequenceEqual(actualDependencies.Select(MemberKey), StringComparer.Ordinal))
                        throw Error("accepted-stage-binding", owner);
                    foreach (ISymbol dependency in dependencies)
                    {
                        Visit(dependency);
                        if (dependency is IMethodSymbol { IsStatic: true } method && MetadataName(method.ContainingType) == PolicyInterface)
                            Visit(RequirePolicyImplementation(method).Baseline);
                    }
                    IOperation[] children = operation.ChildOperations.ToArray();
                    IOperation[] actualChildren = actual.ChildOperations.ToArray();
                    if (children.Length != actualChildren.Length) throw Error("accepted-stage-binding", owner);
                    for (int index = 0; index < children.Length; index++) Walk(children[index], actualChildren[index], model, actualModel, owner, origin, actualOrigin);
                }
                static string OperationBinding(IOperation operation, SyntaxNode origin) => operation switch
                {
                    IArgumentOperation argument => $"{argument.ArgumentKind}:{argument.Parameter?.Ordinal}:{argument.Parameter?.RefKind}:{TypeKey(argument.Parameter?.Type)}:" +
                        $"{argument.Parameter?.HasExplicitDefaultValue}:{(argument.Parameter?.HasExplicitDefaultValue == true ? Constant(argument.Parameter.ExplicitDefaultValue) : "")}:" +
                        Conversion(argument.InConversion) + ":" + Conversion(argument.OutConversion),
                    IConversionOperation conversion => $"{conversion.IsChecked}:{conversion.IsTryCast}:{Conversion(conversion.Conversion)}:{TypeKey(conversion.ConstrainedToType)}",
                    ICoalesceOperation coalesce => Conversion(coalesce.ValueConversion),
                    IBinaryOperation binary => $"{binary.OperatorKind}:{binary.IsChecked}:{binary.IsLifted}:{binary.IsCompareText}:{TypeKey(binary.ConstrainedToType)}",
                    IUnaryOperation unary => $"{unary.OperatorKind}:{unary.IsChecked}:{unary.IsLifted}:{TypeKey(unary.ConstrainedToType)}",
                    ICompoundAssignmentOperation assignment => $"{assignment.OperatorKind}:{assignment.IsChecked}:{assignment.IsLifted}:" +
                        Conversion(assignment.InConversion) + ":" + Conversion(assignment.OutConversion) + ":" + TypeKey(assignment.ConstrainedToType),
                    IInvocationOperation invocation => $"{invocation.IsVirtual}:{TypeKey(invocation.ConstrainedToType)}",
                    IMemberReferenceOperation member => TypeKey(member.ConstrainedToType),
                    ISimpleAssignmentOperation assignment => assignment.IsRef.ToString(),
                    IConditionalOperation conditional => conditional.IsRef.ToString(),
                    ILocalReferenceOperation local => Local(local.Local, origin),
                    IVariableDeclaratorOperation local => Local(local.Symbol, origin),
                    IParameterReferenceOperation parameter => $"{parameter.Parameter.Ordinal}:{parameter.Parameter.RefKind}:{TypeKey(parameter.Parameter.Type)}",
                    IBranchOperation branch => $"{branch.BranchKind}:{branch.Target.Name}:{DeclarationOrdinal(branch.Target, origin)}",
                    _ => "",
                };
                static string Local(ILocalSymbol local, SyntaxNode origin) => $"{local.Name}:{local.RefKind}:{TypeKey(local.Type)}:{DeclarationOrdinal(local, origin)}";
                static int DeclarationOrdinal(ISymbol symbol, SyntaxNode origin)
                {
                    if (symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not { } declaration) return -1;
                    int ordinal = 0;
                    foreach (SyntaxNode node in origin.DescendantNodesAndSelf())
                    {
                        if (node.RawKind == declaration.RawKind && node.Span == declaration.Span) return ordinal;
                        ordinal++;
                    }
                    return -1;
                }
                static ISymbol[] SemanticMembers(IOperation operation, SemanticModel model)
                {
                    List<ISymbol> members = [];
                    if (Target(operation) is IMethodSymbol or IPropertySymbol or IFieldSymbol) members.Add(Target(operation)!);
                    foreach (CommonConversion conversion in operation switch
                    {
                        IConversionOperation conversion => new[] { conversion.Conversion },
                        ICoalesceOperation coalesce => [coalesce.ValueConversion],
                        IArgumentOperation argument => [argument.InConversion, argument.OutConversion],
                        ICompoundAssignmentOperation assignment => [assignment.InConversion, assignment.OutConversion],
                        _ => [],
                    })
                        if (conversion.MethodSymbol is { } method) members.Add(method);
                    if (operation is IForEachLoopOperation iteration)
                    {
                        ForEachStatementInfo info = model.GetForEachStatementInfo((CommonForEachStatementSyntax)iteration.Syntax);
                        foreach (ISymbol? member in new ISymbol?[] { info.GetEnumeratorMethod, info.MoveNextMethod, info.CurrentProperty,
                            info.DisposeMethod, info.ElementConversion.MethodSymbol, info.CurrentConversion.MethodSymbol })
                            if (member is not null) members.Add(member);
                    }
                    if (operation is IDeconstructionAssignmentOperation { Syntax: AssignmentExpressionSyntax assignmentSyntax })
                        Deconstruction(model.GetDeconstructionInfo(assignmentSyntax));
                    return members.ToArray();

                    void Deconstruction(DeconstructionInfo info)
                    {
                        if (info.Method is { } method) members.Add(method);
                        if (info.Conversion?.MethodSymbol is { } conversion) members.Add(conversion);
                        foreach (DeconstructionInfo nested in info.Nested) Deconstruction(nested);
                    }
                }
            }
        }

        private void Add(ISymbol symbol, StageBCalleeOwner owner, string contract) =>
            _allowed.TryAdd(Identity(symbol), new(Identity(symbol), owner, contract));

        private StageBMember DescribeMember(ISymbol symbol)
        {
            string id = Identity(symbol);
            if (_members.TryGetValue(id, out StageBMember? existing)) return existing;
            ITypeSymbol? type = symbol switch
            {
                IMethodSymbol { MethodKind: MethodKind.Constructor } method => method.ContainingType,
                IMethodSymbol method => method.ReturnType,
                IPropertySymbol property => property.Type,
                IFieldSymbol field => field.Type,
                _ => throw Error("member-description", id),
            };
            IParameterSymbol[] parameters = symbol switch
            {
                IMethodSymbol method => method.Parameters.ToArray(),
                IPropertySymbol property => property.Parameters.ToArray(),
                _ => [],
            };
            StageBMember member = new(id, Identity(symbol.OriginalDefinition), symbol.Name, Identity(symbol.ContainingType), symbol switch
            {
                IMethodSymbol { MethodKind: MethodKind.Constructor } => StageBMemberKind.Constructor,
                IMethodSymbol => StageBMemberKind.Method,
                IPropertySymbol => StageBMemberKind.Property,
                _ => StageBMemberKind.Field,
            }, symbol.IsStatic ? StageBReceiverKind.Static : symbol.ContainingType.IsValueType ? StageBReceiverKind.Value : StageBReceiverKind.Reference,
                Identity(type), Ref(symbol switch
                {
                    IMethodSymbol method => method.RefKind,
                    IPropertySymbol property => property.RefKind,
                    _ => RefKind.None,
                }), parameters.Select(parameter => new StageBParameter(Identity(parameter), parameter.Name, parameter.Ordinal,
                    Identity(parameter.Type), Ref(parameter.RefKind), parameter.IsOptional)).ToArray());
            _members.Add(id, member);
            DescribeType(type);
            DescribeType(symbol.ContainingType);
            foreach (IParameterSymbol parameter in parameters) DescribeType(parameter.Type);
            if (member.Kind == StageBMemberKind.Constructor && symbol.ContainingType.IsValueType)
            {
                StageBType layout = _types[Identity(symbol.ContainingType)];
                _types[layout.Symbol] = layout with
                {
                    Fields = symbol.ContainingType.GetMembers().OfType<IFieldSymbol>().Where(static field => !field.IsStatic)
                        .Select(field => new StageBFieldLayout(Identity(field), Identity(field.Type), field.IsReadOnly, Identity(field.AssociatedSymbol)))
                        .OrderBy(static field => field.Symbol, StringComparer.Ordinal).ToArray(),
                };
                foreach (IFieldSymbol field in symbol.ContainingType.GetMembers().OfType<IFieldSymbol>()) DescribeType(field.Type);
            }
            return member;
        }

        private void DescribeType(ITypeSymbol? type)
        {
            if (type is null || _types.ContainsKey(Identity(type))) return;
            ITypeSymbol[] arguments = type is INamedTypeSymbol named ? named.TypeArguments.ToArray() : [];
            ITypeSymbol? element = (type as IArrayTypeSymbol)?.ElementType;
            _types.Add(Identity(type), new(Identity(type), type switch
            {
                { SpecialType: SpecialType.System_Void } => StageBTypeKind.Void,
                IArrayTypeSymbol => StageBTypeKind.Array,
                ITypeParameterSymbol => StageBTypeKind.Parameter,
                { IsValueType: true } => StageBTypeKind.Value,
                _ => StageBTypeKind.Reference,
            }, Identity(element), arguments.Select(Identity).ToArray(), []));
            DescribeType(element);
            foreach (ITypeSymbol argument in arguments) DescribeType(argument);
        }

        private static StageBRefKind Ref(RefKind kind) => Enum.TryParse(kind.ToString(), out StageBRefKind result)
            ? result : throw Error("ref-kind", kind.ToString());

        private static StageBArgumentMode ArgumentMode(IArgumentOperation? argument) => argument?.Parameter?.RefKind switch
        {
            RefKind.Ref => StageBArgumentMode.WritableLocation,
            RefKind.Out => StageBArgumentMode.OutLocation,
            RefKind.In or RefKind.RefReadOnlyParameter => IsLocation(argument.Value)
                ? StageBArgumentMode.ReadOnlyLocation : StageBArgumentMode.ReadOnlyTemporary,
            _ => StageBArgumentMode.Value,
        };

        private static bool IsLocation(IOperation operation) => operation switch
        {
            ILocalReferenceOperation or IParameterReferenceOperation or IFieldReferenceOperation or IFlowCaptureReferenceOperation => true,
            IPropertyReferenceOperation property => property.Property.RefKind != RefKind.None,
            IInvocationOperation invocation => invocation.TargetMethod.RefKind != RefKind.None,
            IConditionalOperation conditional => conditional.IsRef,
            IDeclarationExpressionOperation declaration => IsLocation(declaration.Expression),
            _ => false,
        };

        private StageBCallee Resolve(ISymbol symbol)
        {
            DescribeMember(symbol);
            string identity = Identity(symbol);
            string declarationIdentity = Identity(symbol.OriginalDefinition);
            if (symbol is IMethodSymbol callable && (callable.IsAsync || callable.GetAttributes().Any(static attribute =>
                attribute.AttributeClass?.ToDisplayString() == "System.Diagnostics.ConditionalAttribute")))
                throw Error("callable-contract", identity);
            if (symbol is IMethodSymbol { IsStatic: true } policyMethod && MetadataName(policyMethod.ContainingType) == PolicyInterface)
            {
                ISymbol target = RequirePolicyImplementation(policyMethod).Candidate;
                if (!_allowed.TryGetValue(Identity(target.OriginalDefinition), out StageBCallee? implementation)) throw Error("callee", Identity(target));
                StageBCallee dispatch = new(identity, implementation.Owner, Identity(target.OriginalDefinition));
                _used.TryAdd(identity, dispatch);
                return dispatch;
            }
            if (_allowed.TryGetValue(declarationIdentity, out StageBCallee? callee))
            {
                if (callee.Owner == StageBCalleeOwner.AcceptedStage && symbol.DeclaringSyntaxReferences.Length != 0)
                {
                    ISymbol original = Type(baseline, MetadataName(symbol.ContainingType)).GetMembers(symbol.Name)
                        .Single(member => Identity(member) == declarationIdentity);
                    if (SourceText(original) != SourceText(symbol.OriginalDefinition)) throw Error("accepted-stage-body", identity);
                }
                StageBCallee resolved = callee with { Symbol = identity };
                _used.TryAdd(identity, resolved);
                return resolved;
            }
            if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor, IsImplicitlyDeclared: true, Parameters.Length: 0, ContainingType.IsValueType: true } &&
                DataTypes.Contains(symbol.ContainingType.OriginalDefinition.ToDisplayString()))
            {
                StageBCallee valueDefault = new(identity, StageBCalleeOwner.Local, "CLR value-type default; zero fields and null references");
                _used.TryAdd(identity, valueDefault);
                return valueDefault;
            }
            if (IsExternal(symbol))
            {
                ISymbol original = symbol;
                if (SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, candidate.Compilation.Assembly))
                {
                    INamedTypeSymbol originalType = Type(baseline, MetadataName(symbol.ContainingType));
                    ISymbol[] originals = originalType.GetMembers(symbol.Name).Where(member => Identity(member) == declarationIdentity).ToArray();
                    if (originals is not [ISymbol sourceMember]) throw Error("external-identity", identity);
                    original = sourceMember;
                }
                if (SourceText(symbol.OriginalDefinition) != SourceText(original)) throw Error("external-body", identity);
                if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } && symbol.ContainingType.DeclaringSyntaxReferences.Length != 0 &&
                    SourceText(symbol.ContainingType.OriginalDefinition) != SourceText(Type(baseline, MetadataName(symbol.ContainingType))))
                    throw Error("external-constructor-type", Identity(symbol.ContainingType));
                StageBCallee external = new(identity, StageBCalleeOwner.ExternalRequest, ExternalContract(symbol), RequestKind(symbol));
                _used.TryAdd(identity, external);
                return external;
            }
            throw Error("callee", identity);
        }

        private (ISymbol Baseline, ISymbol Candidate) RequirePolicyImplementation(IMethodSymbol method)
        {
            string identity = Identity(method.OriginalDefinition);
            INamedTypeSymbol originalType = Type(baseline, Policy);
            INamedTypeSymbol actualType = Type(candidate, Policy);
            if (!originalType.AllInterfaces.Select(Identity).Order(StringComparer.Ordinal)
                .SequenceEqual(actualType.AllInterfaces.Select(Identity).Order(StringComparer.Ordinal), StringComparer.Ordinal))
                throw Error("policy-interfaces", Policy);
            ISymbol original = Implementation(baseline, originalType);
            ISymbol actual = Implementation(candidate, actualType);
            if (Identity(original) != Identity(actual)) throw Error("policy-dispatch", identity);
            return (original, actual);

            ISymbol Implementation(CompilerClosure compiler, INamedTypeSymbol ethereum)
            {
                INamedTypeSymbol policyInterface = Type(compiler, PolicyInterface).Construct(ethereum);
                IMethodSymbol[] declarations = policyInterface.GetMembers(method.Name).OfType<IMethodSymbol>()
                    .Where(member => Identity(member.OriginalDefinition) == identity).ToArray();
                if (declarations is not [IMethodSymbol exact]) throw Error("policy-declaration", identity);
                return ethereum.FindImplementationForInterfaceMember(exact) ??
                    (!exact.IsAbstract ? exact : throw Error("policy-implementation", identity));
            }
        }

        private void RequireStaticInitializers(INamedTypeSymbol originalType)
        {
            INamedTypeSymbol actualType = Type(candidate, MetadataName(originalType));
            foreach (IFieldSymbol original in _locals.OfType<IFieldSymbol>().Where(field => SymbolEqualityComparer.Default.Equals(field.ContainingType, originalType)))
            {
                IFieldSymbol[] fields = actualType.GetMembers(original.Name).OfType<IFieldSymbol>().ToArray();
                if (fields is not [IFieldSymbol { IsStatic: true, IsReadOnly: true } actual] || Identity(actual) != Identity(original))
                    throw Error("static-field-shape", Identity(original));
            }
            if (actualType.GetMembers().OfType<IMethodSymbol>().Any(static method => method.MethodKind == MethodKind.StaticConstructor && !method.IsImplicitlyDeclared))
                throw Error("static-initialization", Identity(actualType));
            string[] UnloweredInitializers(INamedTypeSymbol type) => type.GetMembers()
                .Where(member => member.IsStatic && member is IFieldSymbol or IPropertySymbol &&
                    !_allowed.ContainsKey(Identity(member)) && member.DeclaringSyntaxReferences.Any(static source => source.GetSyntax() is
                        VariableDeclaratorSyntax { Initializer: not null } or PropertyDeclarationSyntax { Initializer: not null }))
                .Select(member => Identity(member) + "=" + SourceText(member)).Order(StringComparer.Ordinal).ToArray();
            if (!UnloweredInitializers(originalType).SequenceEqual(UnloweredInitializers(actualType), StringComparer.Ordinal))
                throw Error("static-initializer-closure", Identity(actualType));
        }

        private StageBMethod LowerMethod(ISymbol symbol)
        {
            StageBMember signature = DescribeMember(symbol);
            SyntaxReference[] declarations = symbol.DeclaringSyntaxReferences.ToArray();
            if (declarations is not [SyntaxReference declaration]) throw Error("declaration", Identity(symbol));
            SyntaxNode syntax = declaration.GetSyntax();
            if (syntax is ConstructorDeclarationSyntax { Initializer: { } constructorInitializer })
                throw Error("constructor-initializer", constructorInitializer.ToString());
            if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } &&
                symbol.ContainingType.GetMembers().Any(static member => !member.IsStatic && member.DeclaringSyntaxReferences.Any(static source =>
                    source.GetSyntax() is VariableDeclaratorSyntax { Initializer: not null } or PropertyDeclarationSyntax { Initializer: not null })))
                throw Error("constructor-field-initializer", Identity(symbol.ContainingType));
            if (symbol is IMethodSymbol callable && (callable.IsAsync || callable.GetAttributes().Any(static attribute =>
                attribute.AttributeClass?.ToDisplayString() == "System.Diagnostics.ConditionalAttribute")))
                throw Error("callable-contract", Identity(symbol));
            if (syntax.DescendantNodes().Any(static node => node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
                throw Error("callable", symbol.Name);
            SemanticModel model = candidate.Compilation.GetSemanticModel(syntax.SyntaxTree);
            IOperation operation = model.GetOperation(syntax) ?? (syntax switch
            {
                PropertyDeclarationSyntax property => model.GetOperation(property.ExpressionBody!.Expression),
                VariableDeclaratorSyntax { Initializer: { } initializer } => model.GetOperation(initializer.Value),
                _ => null,
            }) ?? throw Error("body", Identity(symbol));
            SyntaxNode body = syntax switch
            {
                BaseMethodDeclarationSyntax method => (SyntaxNode?)method.Body ?? method.ExpressionBody!.Expression,
                PropertyDeclarationSyntax propertySyntax => propertySyntax.ExpressionBody!.Expression,
                VariableDeclaratorSyntax { Initializer: { } initializer } => initializer.Value,
                _ => throw Error("body-kind", syntax.Kind().ToString()),
            };
            List<string> facts = [];
            List<SyntaxNode> selected = body is BlockSyntax sourceBlock ? sourceBlock.Statements.Cast<SyntaxNode>().ToList() : [body];
            List<SyntaxNode> excluded = [];
            int selectedStart = -1;
            if (symbol.Name == "Execute" && symbol is IMethodSymbol { Parameters.Length: 6 })
            {
                IMethodSymbol originalMethod = Type(baseline, Processor).GetMembers("Execute").OfType<IMethodSymbol>()
                    .Single(static method => method.Parameters.Length == 6);
                MethodDeclarationSyntax originalSyntax = (MethodDeclarationSyntax)originalMethod.DeclaringSyntaxReferences.Single().GetSyntax();
                SemanticModel originalModel = baseline.Compilation.GetSemanticModel(originalSyntax.SyntaxTree);
                string nonceIdentity = Identity(Type(baseline, Processor).GetMembers("IncrementNonce").OfType<IMethodSymbol>().Single());
                IfStatementSyntax NonceGuard(IEnumerable<SyntaxNode> statements, SemanticModel semanticModel)
                {
                    IfStatementSyntax[] guards = statements.OfType<IfStatementSyntax>().Where(conditional =>
                        conditional.Condition.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(call =>
                            semanticModel.GetSymbolInfo(call).Symbol is IMethodSymbol target && Identity(target.OriginalDefinition) == nonceIdentity)).ToArray();
                    return guards is [IfStatementSyntax guard] ? guard : throw Error("nonce-boundary", Identity(symbol));
                }
                IfStatementSyntax originalGuard = NonceGuard(originalSyntax.Body!.Statements, originalModel);
                IfStatementSyntax guard = NonceGuard(selected, model);
                selectedStart = guard.Span.End;
                int entry = selected.IndexOf(guard) + 1;
                int originalEntry = originalSyntax.Body.Statements.IndexOf(originalGuard) + 1;
                if (entry < 3 || entry != originalEntry ||
                    !selected.Take(entry).Zip(originalSyntax.Body.Statements.Take(originalEntry)).All(static pair => pair.First.IsEquivalentTo(pair.Second)) ||
                    selected[^1] is not ReturnStatementSyntax { Expression: InvocationExpressionSyntax fallback } ||
                    fallback.Expression.ToString() != "ExecuteEvmTransaction") throw Error("caller-slice", Identity(symbol));
                excluded.AddRange(selected.Skip(2).Take(entry - 2));
                excluded.Add(selected[^1]);
                selected = selected.Take(2).Concat(selected.Skip(entry)).ToList();
                facts.Add("entry is immediately after successful nonce increment; earlier prefix locals are supplied with provenance");
                facts.Add("selected recipient is non-null; the final ExecuteEvmTransaction return is outside this domain");
            }
            if (symbol.Name == "FinalizeTransaction")
            {
                RequireUnchangedScopeInputs(operation, "restore", "commit", "spec");
                IfStatementSyntax[] restores = selected.OfType<IfStatementSyntax>().Where(statement => statement.Condition.ToString() == "restore").ToArray();
                if (restores is not [IfStatementSyntax restore]) throw Error("commit-slice", Identity(symbol));
                if (restore.Else?.Statement is not IfStatementSyntax commit || commit.Condition.ToString() != "commit")
                    throw Error("commit-slice", Identity(symbol));
                excluded.Add(restore.Statement);
                if (commit.Else is not null) excluded.Add(commit.Else.Statement);
                facts.Add("restore=false; commit=true; tracingState=false; IsEip658Enabled=true");
                foreach (IfStatementSyntax conditional in syntax.DescendantNodes().OfType<IfStatementSyntax>())
                    if (conditional.Condition.ToString() == "!spec.IsEip658Enabled") excluded.Add(conditional.Statement);
            }
            if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } && symbol.ContainingType.Name == "TransactionSubstate")
            {
                RequireUnchangedScopeInputs(operation, "shouldRevert");
                int terminal = selected.FindIndex(static statement => statement is IfStatementSyntax conditional && conditional.Condition.ToString() == "!ShouldRevert");
                if (terminal != 7 || selected.Take(terminal).Count(static statement => statement.ToString() == "ShouldRevert = shouldRevert;") != 1 ||
                    selected.Take(terminal).Count(static statement => statement is ExpressionStatementSyntax
                        { Expression: AssignmentExpressionSyntax { Left: IdentifierNameSyntax { Identifier.ValueText: "ShouldRevert" } } }) != 1 ||
                    selected.Take(terminal).Any(static statement => statement is not ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax }) ||
                    selected[terminal] is not IfStatementSyntax { Statement: BlockSyntax { Statements: [.., ReturnStatementSyntax { Expression: null }] } })
                    throw Error("substate-slice", Identity(symbol));
                excluded.AddRange(selected.Skip(terminal + 1));
                selected = selected.Take(terminal + 1).ToList();
                facts.Add("shouldRevert=false at the selected constructor call; its first return terminates this constructor");
            }
            bool Excluded(SyntaxNode node) => excluded.Any(region => region.Span.Contains(node.Span));
            Dictionary<CaptureId, int> captures = [];
            Dictionary<ControlFlowRegion, int> regions = new(ReferenceEqualityComparer.Instance);
            List<StageBRegion> regionPlans = [];
            Dictionary<string, StageBBinding> bindings = new(StringComparer.Ordinal);
            Dictionary<int, StageBCapture> capturePlans = [];
            ControlFlowGraph? graph = operation switch
            {
                IMethodBodyOperation methodBody => ControlFlowGraph.Create(methodBody),
                IConstructorBodyOperation constructorBody => ControlFlowGraph.Create(constructorBody),
                _ => null,
            };
            if (graph is not null) Register(graph.Root);
            StageBTerm Lower(IOperation node)
            {
                if (Excluded(node.Syntax))
                    return new(StageBOperationKind.OutsideSelectedDomain, Identity(node.Type), "", "", "", false, "", -1, "", "", []);
                if (!Enum.TryParse(node.Kind.ToString(), out StageBOperationKind kind)) throw Error("operation", node.Kind.ToString());
                if (node.Type?.TypeKind is TypeKind.Delegate or TypeKind.Dynamic or TypeKind.FunctionPointer)
                    throw Error("callable-type", Identity(node.Type));
                DescribeType(node.Type);
                if (node is ILoopOperation and not IForEachLoopOperation) throw Error("operation", "Loop:" + ((ILoopOperation)node).LoopKind);
                if (node is IVariableDeclaratorOperation { Symbol.RefKind: RefKind.Ref }) throw Error("operation", "RefLocal");
                if (node is IVariableDeclarationOperation { IgnoredDimensions.IsEmpty: false } or
                    IVariableDeclaratorOperation { IgnoredArguments.IsEmpty: false }) throw Error("operation", "IgnoredDeclarationOperands");
                if (node is IBinaryOperation { OperatorMethod: not null, OperatorKind: BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr })
                    throw Error("operation", "UserDefinedConditionalOperator");
                (IOperation? concatLeft, IOperation? concatRight) = node switch
                {
                    IBinaryOperation { OperatorMethod: null, OperatorKind: BinaryOperatorKind.Add, Type.SpecialType: SpecialType.System_String } binary =>
                        (binary.LeftOperand, binary.RightOperand),
                    ICompoundAssignmentOperation { OperatorMethod: null, OperatorKind: BinaryOperatorKind.Add } assignment
                        when assignment.Target.Type?.SpecialType == SpecialType.System_String || assignment.Value.Type?.SpecialType == SpecialType.System_String =>
                        (assignment.Target, assignment.Value),
                    _ => (null, null),
                };
                // Object/value concatenation invokes ToString without an operation-tree callee.
                if (concatLeft is not null && (concatLeft.Type?.SpecialType != SpecialType.System_String || concatRight?.Type?.SpecialType != SpecialType.System_String))
                    throw Error("operation", "StringConcatenationFormatting");
                if (node is IConversionOperation { Type: INamedTypeSymbol { IsTupleType: true }, Conversion.IsIdentity: false } or
                    ICoalesceOperation { Type: INamedTypeSymbol { IsTupleType: true }, ValueConversion.IsIdentity: false } ||
                    node is IArgumentOperation { Parameter.Type: INamedTypeSymbol { IsTupleType: true } } tupleArgument &&
                    (!tupleArgument.InConversion.IsIdentity || !tupleArgument.OutConversion.IsIdentity))
                    throw Error("operation", "TupleConversion");
                ISymbol? target = Target(node);
                if (target is IMethodSymbol or IPropertySymbol or IFieldSymbol) Resolve(target);
                if (target is ILocalSymbol localSymbol)
                    bindings.TryAdd(Identity(localSymbol), new(Identity(localSymbol), Identity(localSymbol.Type), Ref(localSymbol.RefKind), -1));
                IMethodSymbol? collectionAdd = null;
                if (node is ICollectionExpressionOperation collection)
                {
#pragma warning disable RSEXPERIMENTAL006 // Pin Roslyn 5.6's experimental collection-constructor arguments fail-closed.
                    if (!collection.ConstructArguments.IsEmpty) throw Error("operation", "CollectionConstructArguments");
#pragma warning restore RSEXPERIMENTAL006
                    if (collection.Type is not IArrayTypeSymbol)
                    {
                        if (collection.ConstructMethod is not { MethodKind: MethodKind.Constructor })
                            throw Error("collection-construction", Identity(collection.Type));
                        // Roslyn does not expose Add dispatch here; admit only the pinned single-overload metadata collection.
                        if (!collection.Elements.IsEmpty)
                        {
                            if (collection.Type is not INamedTypeSymbol collectionType ||
                                MetadataName(collectionType.OriginalDefinition) != "Nethermind.Core.Collections.JournalCollection`1" ||
                                !collectionType.DeclaringSyntaxReferences.IsEmpty ||
                                collectionType.GetMembers("Add").OfType<IMethodSymbol>().ToArray() is not
                                    [IMethodSymbol { IsStatic: false, Parameters: [IParameterSymbol { RefKind: RefKind.None } element] } add] ||
                                !SymbolEqualityComparer.Default.Equals(element.Type, collectionType.TypeArguments.Single()))
                                throw Error("collection-elements", Identity(collection.Type));
                            foreach (IOperation item in collection.Elements)
                            {
                                if (item is ISpreadOperation) throw Error("operation", "Spread");
                                if (!SymbolEqualityComparer.Default.Equals(item.Type, element.Type))
                                    throw Error("collection-element-conversion", Identity(item.Type));
                            }
                            Resolve(add);
                            collectionAdd = add;
                        }
                    }
                }
                if (node is IInvocationOperation { TargetMethod.MethodKind: MethodKind.LocalFunction or MethodKind.DelegateInvoke })
                    throw Error("callable", kind.ToString());
                if (node is IObjectCreationOperation creation && creation.Constructor?.ContainingType.Name == "TransactionSubstate")
                {
                    IArgumentOperation? argument = creation.Arguments.SingleOrDefault(static arg => arg.Parameter?.Name == "shouldRevert");
                    if (argument?.Value.ConstantValue is not { HasValue: true, Value: false })
                        throw Error("substate-should-revert", "expected literal false");
                }
                string op = node switch
                {
                    IBinaryOperation binary => $"{binary.OperatorKind};checked={binary.IsChecked};lifted={binary.IsLifted};compareText={binary.IsCompareText}",
                    IUnaryOperation unary => $"{unary.OperatorKind};checked={unary.IsChecked};lifted={unary.IsLifted}",
                    ICompoundAssignmentOperation assignment => $"{assignment.OperatorKind};checked={assignment.IsChecked};lifted={assignment.IsLifted}",
                    IConversionOperation conversion => $"checked={conversion.IsChecked};tryCast={conversion.IsTryCast}",
                    IInvocationOperation invocation => $"virtual={invocation.IsVirtual}",
                    ISimpleAssignmentOperation assignment => $"ref={assignment.IsRef}",
                    IConditionalOperation conditional => $"ref={conditional.IsRef}",
                    IInstanceReferenceOperation instance => instance.ReferenceKind.ToString(),
                    IFlowCaptureOperation capture => "capture=" + Capture(capture.Id),
                    IFlowCaptureReferenceOperation reference => $"capture={Capture(reference.Id)};initialization={reference.IsInitialization}",
                    IBranchOperation branch => $"{branch.BranchKind};target={Identity(branch.Target)}",
                    IDeclarationPatternOperation pattern => $"matched={Identity(pattern.MatchedType)};narrowed={Identity(pattern.NarrowedType)};null={pattern.MatchesNull};local={Identity(pattern.DeclaredSymbol)}",
                    IForEachLoopOperation iteration => $"async={iteration.IsAsynchronous};continue={Identity(iteration.ContinueLabel)};exit={Identity(iteration.ExitLabel)}",
                    ICollectionExpressionOperation collectionExpression => $"elements={collectionExpression.Elements.Length}",
                    ITupleOperation tuple => $"natural={Identity(tuple.NaturalType)}",
                    ISwitchExpressionOperation switchExpression => $"exhaustive={switchExpression.IsExhaustive}",
                    ILocalReferenceOperation local => $"declaration={local.IsDeclaration}",
                    IFieldReferenceOperation field => $"declaration={field.IsDeclaration}",
                    _ => "",
                };
                if (concatLeft is not null) op += ";stringConcat=string,string";
                if (collectionAdd is not null) op += ";add=" + Identity(collectionAdd);
                ITypeSymbol? constrained = node switch
                {
                    IInvocationOperation invocation => invocation.ConstrainedToType,
                    IMemberReferenceOperation member => member.ConstrainedToType,
                    IConversionOperation conversion => conversion.ConstrainedToType,
                    IBinaryOperation binary => binary.ConstrainedToType,
                    IUnaryOperation unary => unary.ConstrainedToType,
                    ICompoundAssignmentOperation assignment => assignment.ConstrainedToType,
                    _ => null,
                };
                if (constrained is not null) op += ";constrained=" + Identity(constrained);
                if (node is IPatternOperation patternOperation)
                    op += $";input={Identity(patternOperation.InputType)};narrowed={Identity(patternOperation.NarrowedType)}";
                IEnumerable<ILocalSymbol>? locals = node switch
                {
                    IBlockOperation block => block.Locals,
                    ISymbolInitializerOperation initializer => initializer.Locals,
                    ISwitchExpressionArmOperation arm => arm.Locals,
                    ILoopOperation loopOperation => loopOperation.Locals,
                    _ => null,
                };
                if (locals is not null) op += ";locals=[" + string.Join(";", locals.Select(Identity)) + "]";
                string conversionInfo = node switch
                {
                    IConversionOperation conversion => CheckedConversion(conversion.Conversion),
                    ICoalesceOperation coalesce => CheckedConversion(coalesce.ValueConversion),
                    IArgumentOperation argument => $"in:{CheckedConversion(argument.InConversion)};out:{CheckedConversion(argument.OutConversion)}",
                    ICompoundAssignmentOperation assignment => $"in:{CheckedConversion(assignment.InConversion)};out:{CheckedConversion(assignment.OutConversion)}",
                    _ => "",
                };
                if (node is IDeconstructionAssignmentOperation deconstruction)
                    op += ";deconstruction=" + Deconstruction(deconstruction.Syntax switch
                    {
                        AssignmentExpressionSyntax assignment => model.GetDeconstructionInfo(assignment),
                        ForEachVariableStatementSyntax iteration => model.GetDeconstructionInfo(iteration),
                        _ when deconstruction.Syntax.Ancestors().OfType<ForEachVariableStatementSyntax>().FirstOrDefault() is { } iteration &&
                            iteration.Variable.Span.Contains(deconstruction.Syntax.Span) => model.GetDeconstructionInfo(iteration),
                        _ => throw Error("deconstruction", deconstruction.Syntax.Kind().ToString()),
                    });
                if (node is IForEachLoopOperation loop)
                {
                    if (loop.IsAsynchronous) throw Error("operation", "AsynchronousForEach");
                    ForEachStatementInfo info = model.GetForEachStatementInfo((CommonForEachStatementSyntax)loop.Syntax);
                    foreach (ISymbol? implicitCall in new ISymbol?[] { info.GetEnumeratorMethod, info.MoveNextMethod, info.CurrentProperty, info.DisposeMethod })
                        if (implicitCall is not null) Resolve(implicitCall);
                    op += $";enumerator={Identity(info.GetEnumeratorMethod)};moveNext={Identity(info.MoveNextMethod)};current={Identity(info.CurrentProperty)};dispose={Identity(info.DisposeMethod)};elementType={Identity(info.ElementType)}";
                    conversionInfo = $"element:{CheckedCSharpConversion(info.ElementConversion)};current:{CheckedCSharpConversion(info.CurrentConversion)}";
                    if (loop.Syntax is ForEachVariableStatementSyntax variable)
                        op += ";deconstruction=" + Deconstruction(model.GetDeconstructionInfo(variable));
                }
                if (node is IUsingDeclarationOperation usingDeclaration)
                {
                    if (usingDeclaration.IsAsynchronous || graph is null) throw Error("operation", "UnsupportedUsing");
                    INamedTypeSymbol tracker = Type(candidate, "Nethermind.Evm.StackAccessTracker");
                    foreach (IVariableDeclarationOperation group in usingDeclaration.DeclarationGroup.Declarations)
                        foreach (IVariableDeclaratorOperation variable in group.Declarators)
                            if (!SymbolEqualityComparer.Default.Equals(variable.Symbol.Type, tracker)) throw Error("using-resource", Identity(variable.Symbol.Type));
                    IMethodSymbol disposeInterface = Type(candidate, "System.IDisposable").GetMembers("Dispose").OfType<IMethodSymbol>().Single();
                    if (tracker.FindImplementationForInterfaceMember(disposeInterface) is not IMethodSymbol dispose)
                        throw Error("using-dispose", Identity(tracker));
                    Resolve(dispose);
                    op += ";dispose=" + Identity(dispose);
                }
                IArgumentOperation? arg = node as IArgumentOperation;
                IOperation[] children = node.ChildOperations.ToArray();
                IOperation? receiver = node switch
                {
                    IInvocationOperation call => call.Instance,
                    IPropertyReferenceOperation property => property.Instance,
                    IFieldReferenceOperation field => field.Instance,
                    _ => null,
                };
                List<string> additionalMembers = [];
                if (collectionAdd is not null) additionalMembers.Add(Identity(collectionAdd));
                foreach (CommonConversion conversion in node switch
                {
                    IConversionOperation conversion => new[] { conversion.Conversion },
                    ICoalesceOperation coalesce => [coalesce.ValueConversion],
                    IArgumentOperation argument => [argument.InConversion, argument.OutConversion],
                    ICompoundAssignmentOperation assignment => [assignment.InConversion, assignment.OutConversion],
                    _ => [],
                })
                    if (conversion.MethodSymbol is { } conversionMethod) additionalMembers.Add(Identity(conversionMethod));
                int captureId = node switch
                {
                    IFlowCaptureOperation capture => Capture(capture.Id),
                    IFlowCaptureReferenceOperation capture => Capture(capture.Id),
                    _ => -1,
                };
                if (node is IFlowCaptureOperation flow)
                {
                    StageBRegion region = regionPlans.Single(region => region.Captures.Contains(captureId));
                    capturePlans[captureId] = new(captureId, region.Id, Identity(flow.Value.Type));
                }
                StageBCallSite? callSite = target is IMethodSymbol or IPropertySymbol or IFieldSymbol
                    ? new(Identity(target), receiver is null ? -1 : Array.IndexOf(children, receiver),
                        children.Select((child, index) => (child, index)).Where(static pair => pair.child is IArgumentOperation).Select(static pair => pair.index).ToArray())
                    : null;
                return new(kind, Identity(node.Type), Identity(target), op,
                    node.ConstantValue.HasValue ? Constant(node.ConstantValue.Value) : "", node.IsImplicit,
                    arg?.ArgumentKind.ToString() ?? "", arg?.Parameter?.Ordinal ?? -1, arg?.Parameter?.RefKind.ToString() ?? "",
                    conversionInfo, children.Select(Lower).ToArray())
                {
                    Binding = new(captureId, Ref(arg?.Parameter?.RefKind ?? (target switch
                    {
                        ILocalSymbol local => local.RefKind,
                        IParameterSymbol parameter => parameter.RefKind,
                        IMethodSymbol method => method.RefKind,
                        IPropertySymbol property => property.RefKind,
                        _ => RefKind.None,
                    })), node is ISimpleAssignmentOperation { IsRef: true } or IConditionalOperation { IsRef: true },
                        ArgumentMode(arg is { Syntax: ArgumentSyntax argumentSyntax } && model.GetOperation(argumentSyntax) is IArgumentOperation originalArgument
                            ? originalArgument : arg), callSite,
                        additionalMembers.Where(member => member != Identity(target)).Distinct(StringComparer.Ordinal).ToArray()),
                };
            }
            StageBTerm[] statements = selected.Select(node => Lower(model.GetOperation(node) ?? throw Error("statement", node.Kind().ToString()))).ToArray();
            List<StageBBlock> blocks = [];
            if (graph is not null)
                foreach (BasicBlock block in graph.Blocks)
                    blocks.Add(new(block.Ordinal, block.Kind.ToString(), block.IsReachable, block.ConditionKind.ToString(),
                        block.Operations.Select(Lower).ToArray(), block.BranchValue is null ? null : Lower(block.BranchValue),
                        block.Operations.Any(node => Excluded(node.Syntax)) || block.BranchValue is not null && Excluded(block.BranchValue.Syntax),
                        Edge(block.FallThroughSuccessor), Edge(block.ConditionalSuccessor)));
            bool expressionEnvelope = graph is null;
            if (expressionEnvelope)
            {
                if (statements is not [StageBTerm expression]) throw Error("expression-body", Identity(symbol));
                blocks.Add(new(0, "Block", true, "None", [], expression, false, new(-1, "Return", [], [], []), null));
            }
            StageBCfgPoint? entryPoint = null;
            if (selectedStart >= 0 && graph is not null)
            {
                foreach (BasicBlock block in graph.Blocks)
                {
                    IOperation[] nodes = block.BranchValue is null ? block.Operations.ToArray() : [.. block.Operations, block.BranchValue];
                    for (int index = 0; index < nodes.Length; index++)
                        if (nodes[index].Syntax.SpanStart >= selectedStart && !Excluded(nodes[index].Syntax))
                        {
                            entryPoint = new(block.Ordinal, index);
                            break;
                        }
                    if (entryPoint is not null) break;
                }
                if (entryPoint is null) throw Error("nonce-cfg-entry", Identity(symbol));
            }
            return new(Identity(symbol), syntax.SyntaxTree.FilePath,
                CompilerReferences.Hash(System.Text.Encoding.UTF8.GetBytes(body.ToFullString())), facts.ToArray(), statements, regionPlans.ToArray(), blocks.ToArray())
            {
                Signature = signature, Bindings = bindings.Values.OrderBy(static binding => binding.Symbol, StringComparer.Ordinal).ToArray(),
                Captures = capturePlans.Values.OrderBy(static capture => capture.Id).ToArray(), SelectedEntry = entryPoint,
                ExpressionEnvelope = expressionEnvelope,
            };

            int Capture(CaptureId id) =>
                captures.TryGetValue(id, out int value) ? value : throw Error("capture", "unbound CFG capture");
            void Register(ControlFlowRegion region)
            {
                int id = regions.Count;
                regions.Add(region, id);
                foreach (CaptureId capture in region.CaptureIds) captures.Add(capture, captures.Count);
                foreach (ILocalSymbol local in region.Locals)
                {
                    DescribeType(local.Type);
                    bindings.Add(Identity(local), new(Identity(local), Identity(local.Type), Ref(local.RefKind), id));
                }
                regionPlans.Add(new(id, region.EnclosingRegion is null ? -1 : regions[region.EnclosingRegion], region.Kind.ToString(),
                    region.FirstBlockOrdinal, region.LastBlockOrdinal, region.Locals.Select(Identity).ToArray(),
                    region.CaptureIds.Select(Capture).ToArray(), Identity(region.ExceptionType)));
                foreach (ControlFlowRegion nested in region.NestedRegions) Register(nested);
            }
            string CheckedConversion(CommonConversion conversion)
            {
                if (conversion.MethodSymbol is { } method) Resolve(method);
                return Conversion(conversion);
            }
            string CheckedCSharpConversion(Microsoft.CodeAnalysis.CSharp.Conversion conversion)
            {
                if (conversion.IsTupleConversion || conversion.IsTupleLiteralConversion) throw Error("operation", "TupleConversion");
                if (conversion.MethodSymbol is { } method) Resolve(method);
                return Conversion(conversion);
            }
            string Deconstruction(DeconstructionInfo info)
            {
                if (info.Method is { } method) Resolve(method);
                return $"method={Identity(info.Method)};conversion={(!info.Conversion.HasValue ? "" : CheckedCSharpConversion(info.Conversion.Value))};nested=[{string.Join(";", info.Nested.Select(Deconstruction))}]";
            }
            StageBEdge? Edge(ControlFlowBranch? edge) => edge is null ? null :
                new(edge.Destination?.Ordinal ?? -1, edge.Semantics.ToString(),
                    edge.LeavingRegions.Select(region => regions[region]).ToArray(),
                    edge.EnteringRegions.Select(region => regions[region]).ToArray(),
                    edge.FinallyRegions.Select(region => regions[region]).ToArray());
        }
    }

    private static ISymbol? Target(IOperation? operation) => operation switch
    {
        IInvocationOperation invocation => invocation.TargetMethod,
        IObjectCreationOperation creation => creation.Constructor,
        ICollectionExpressionOperation collection => collection.ConstructMethod,
        IPropertyReferenceOperation property => property.Property,
        IFieldReferenceOperation field => field.Field,
        IParameterReferenceOperation parameter => parameter.Parameter,
        ILocalReferenceOperation local => local.Local,
        IVariableDeclaratorOperation variable => variable.Symbol,
        IDiscardOperation discard => discard.DiscardSymbol,
        IConversionOperation conversion => conversion.OperatorMethod,
        IBinaryOperation binary => binary.OperatorMethod,
        IUnaryOperation unary => unary.OperatorMethod,
        ICompoundAssignmentOperation assignment => assignment.OperatorMethod,
        _ => null,
    };

    private static bool IsExternal(ISymbol symbol)
    {
        string type = symbol.ContainingType?.OriginalDefinition.ToDisplayString() ?? "";
        int extension = type.IndexOf(".extension(", StringComparison.Ordinal);
        if (extension >= 0) type = type[..extension];
        if (symbol is IFieldSymbol field)
            return field.HasConstantValue || DataTypes.Contains(type) || type is "Nethermind.Core.Address" or "Nethermind.Evm.ExecutionMetricsFlag" ||
                field.ContainingType is { IsTupleType: true };
        if (symbol is IPropertySymbol && DataTypes.Contains(type)) return true;
        if (symbol is IMethodSymbol method && type == "Nethermind.Int256.UInt256")
            return method.MethodKind is MethodKind.UserDefinedOperator or MethodKind.Conversion || method.Name is "Min" or "ToBigEndian";
        return ExternalMembers.TryGetValue(type, out string[]? names) && names.Contains(symbol.MetadataName, StringComparer.Ordinal);
    }

    private static string ExternalContract(ISymbol symbol) => symbol is IPropertySymbol
        ? "typed metadata/value projection; runtime representation premise"
        : symbol is IFieldSymbol field
            ? field.HasConstantValue ? "typed constant: " + Constant(field.ConstantValue) : "typed field/storage projection; exact declaration and representation premise"
        : symbol.ContainingType.Name is "StackAccessTracker" or "TrackingState"
            ? "normal pool operation; fresh-empty rent and clear-on-return invariant"
            : "normal non-reentrant typed primitive/request: " + Identity(symbol);

    private static StageBRequestKind RequestKind(ISymbol symbol) => symbol switch
    {
        IFieldSymbol { HasConstantValue: true } => StageBRequestKind.Constant,
        IFieldSymbol or IPropertySymbol => StageBRequestKind.Projection,
        { ContainingType.Name: "IWorldState" or "IReadOnlyStateProvider" } => StageBRequestKind.WorldState,
        { ContainingType.Name: "ITxTracer" } => StageBRequestKind.Tracer,
        { ContainingType.Name: "ICodeInfoRepository" } => StageBRequestKind.CodeLookup,
        { ContainingType.Name: "StackAccessTracker" or "TrackingState" } => StageBRequestKind.Pool,
        { ContainingType.Name: "UInt256" } => StageBRequestKind.Arithmetic,
        { ContainingType.Name: "Address" or "Hash256" or "ValueHash256" or "LogEntry" or "StorageCell" } => StageBRequestKind.Representation,
        { ContainingType.Name: "JournalSet" or "JournalCollection" or "AccessList" or "Enumerator" or "StorageKeysEnumerable" or "StorageKeysEnumerator" or "IEnumerable" or "IEnumerator" } => StageBRequestKind.Collection,
        _ => StageBRequestKind.Framework,
    };

    private static INamedTypeSymbol Type(CompilerClosure compiler, string name) =>
        compiler.Compilation.GetTypeByMetadataName(name) ?? throw Error("type", name);

    private static string MetadataName(INamedTypeSymbol type) => type.ContainingType is null
        ? type.ContainingNamespace + "." + type.MetadataName
        : MetadataName(type.ContainingType) + "+" + type.MetadataName;

    private static string Identity(ISymbol? symbol) => symbol switch
    {
        ILocalSymbol local => $"{Identity(local.ContainingSymbol)}::local:{local.Name}@{local.Locations.FirstOrDefault()?.SourceSpan.Start}:{local.RefKind}:{Identity(local.Type)}",
        IParameterSymbol parameter => $"{Identity(parameter.ContainingSymbol)}::parameter:{parameter.Ordinal}:{parameter.Name}:{parameter.RefKind}:{Identity(parameter.Type)}",
        _ => symbol?.ToDisplayString(IdentityFormat) ?? "",
    };

    private static string TypeKey(ITypeSymbol? type) => type is null ? "" :
        type.ContainingAssembly?.Identity + "|" + Identity(type) + "|" + type.TypeKind + "|" + type.NullableAnnotation + "|" + (type switch
        {
            IArrayTypeSymbol array => array.Rank + ":" + TypeKey(array.ElementType),
            IPointerTypeSymbol pointer => TypeKey(pointer.PointedAtType),
            INamedTypeSymbol named => TypeKey(named.ContainingType) + "<" + string.Join(",", named.TypeArguments.Select(TypeKey)) + ">",
            ITypeParameterSymbol parameter => $"{parameter.TypeParameterKind}:{parameter.Ordinal}",
            _ => "",
        });

    private static string MemberKey(ISymbol symbol) => symbol.ContainingAssembly?.Identity + "|" + Identity(symbol) + "|" +
        TypeKey(symbol.ContainingType) + "|" + (symbol switch
        {
            IMethodSymbol method => TypeKey(method.ReturnType) + "|" + method.RefKind + "|" +
                string.Join(",", method.TypeArguments.Select(TypeKey)) + "|" + string.Join(",", method.Parameters.Select(static parameter => TypeKey(parameter.Type))),
            IPropertySymbol property => TypeKey(property.Type) + "|" + property.RefKind + "|" +
                string.Join(",", property.Parameters.Select(static parameter => TypeKey(parameter.Type))),
            IFieldSymbol field => TypeKey(field.Type) + "|" + field.HasConstantValue + "|" + (field.HasConstantValue ? Constant(field.ConstantValue) : ""),
            _ => "",
        });

    private static string Constant(object? value) => value switch
    {
        null => "null",
        bool boolean => boolean ? "bool:true" : "bool:false",
        string text => "string:" + text,
        IFormattable formattable => value.GetType().FullName + ":" + formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => throw Error("constant", value.GetType().FullName!),
    };

    private static string Conversion(CommonConversion conversion) =>
#pragma warning disable RSEXPERIMENTAL006 // Preserve the pinned Roslyn conversion discriminator, including experimental union conversions.
        $"exists={conversion.Exists};identity={conversion.IsIdentity};implicit={conversion.IsImplicit};numeric={conversion.IsNumeric};reference={conversion.IsReference};nullable={conversion.IsNullable};user={conversion.IsUserDefined};union={conversion.IsUnion};method={Identity(conversion.MethodSymbol)};constrained={Identity(conversion.ConstrainedToType)}";
#pragma warning restore RSEXPERIMENTAL006

    private static string Conversion(Microsoft.CodeAnalysis.CSharp.Conversion conversion) =>
        $"kind={conversion};exists={conversion.Exists};identity={conversion.IsIdentity};implicit={conversion.IsImplicit};numeric={conversion.IsNumeric};reference={conversion.IsReference};nullable={conversion.IsNullable};user={conversion.IsUserDefined};method={Identity(conversion.MethodSymbol)};constrained={Identity(conversion.ConstrainedToType)}";

    private static string SourceText(ISymbol symbol) => string.Join("\n", symbol.DeclaringSyntaxReferences
        .Select(static declaration => declaration.GetSyntax().WithoutTrivia().ToFullString()));

    private static void RequireUnchangedScopeInputs(IOperation operation, params string[] names)
    {
        IOperation? target = operation switch
        {
            IAssignmentOperation assignment => assignment.Target,
            IIncrementOrDecrementOperation increment => increment.Target,
            IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out } argument => argument.Value,
            IVariableDeclaratorOperation { Symbol.RefKind: RefKind.Ref } variable => variable.Initializer,
            _ => null,
        };
        bool ContainsScopeInput(IOperation node) => node is IParameterReferenceOperation parameter && names.Contains(parameter.Parameter.Name, StringComparer.Ordinal) ||
            node.ChildOperations.Any(ContainsScopeInput);
        if (target is not null && ContainsScopeInput(target))
            throw Error("scope-mutation", operation.Syntax.ToString());
        foreach (IOperation child in operation.ChildOperations) RequireUnchangedScopeInputs(child, names);
    }

    private static ExtractionException Error(string code, string detail) => new($"Stage-B {code}: {detail}.");
}
