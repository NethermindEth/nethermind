// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Evm.Lean.SequentialBlockTransactionFoldExtractor;

internal static partial class Extractor
{
    private const string ProcessorType = "Nethermind.Consensus.Processing.BlockProcessor";
    private const string ExecutorType = ProcessorType + ".BlockValidationTransactionsExecutor";
    private const string TracerType = "Nethermind.Blockchain.Tracing.BlockReceiptsTracer";
    private const string BalType = "Nethermind.Consensus.Processing.BlockAccessListManager";

    internal static void ValidateCompilationForTest(string root, IReadOnlyDictionary<string, byte[]> overrides)
    {
        SourcePinDocument pins = ReadPins(root);
        _ = BuildCompleteCompilerContext(root, ReadSources(root, pins, overrides, false),
            ReadAuxiliarySources(root, pins, overrides, false), ReadSupportSources(root, pins, overrides, false));
    }

    private static CompilerContext BuildCompleteCompilerContext(string root, SourceFile[] sources,
        SourceFile[] auxiliary, SourceFile[] support)
    {
        CompilerReferenceIdentity[] identities = ReadCompilerReferenceIdentities(root);
        string platform = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        MetadataReference[] references = identities.Where(static identity => identity.Selected)
            .Select(identity => MetadataReference.CreateFromFile(ResolveCompilerReferencePath(root, platform, identity.Path)))
            .ToArray();
        SourceFile[] all = [.. sources, .. auxiliary, .. support];
        Dictionary<string, SemanticModel> models = new(StringComparer.Ordinal);
        CSharpCompilation? consensus = null;
        foreach (string assembly in new[] { "Nethermind.Consensus", "Nethermind.Blockchain", "Nethermind.Init", "Nethermind.Evm" })
        {
            SourceFile[] group = all.Where(source => source.RelativePath.StartsWith(
                "src/Nethermind/" + assembly + "/", StringComparison.Ordinal)).ToArray();
            SyntaxTree globalAliases = support.Single(static source => source.RelativePath == "src/Nethermind/Directory.Build.props").Tree;
            CSharpCompilation compilation = CSharpCompilation.Create(assembly, group.Select(static source => source.Tree).Append(globalAliases),
                references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    nullableContextOptions: NullableContextOptions.Enable, allowUnsafe: true,
                    optimizationLevel: OptimizationLevel.Debug, metadataImportOptions: MetadataImportOptions.All));
            Diagnostic[] errors = compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length != 0)
            {
                throw new ExtractionException($"fold.compiler.compilation: {assembly}: {string.Join("; ", errors.Select(static error => error.ToString()))}");
            }
            foreach (SourceFile source in group) models.Add(source.RelativePath, compilation.GetSemanticModel(source.Tree));
            if (assembly == "Nethermind.Consensus") consensus = compilation;
        }
        return new(consensus!, identities, models);
    }

    private static string CanonicalSymbol(ISymbol? symbol)
    {
        if (symbol is IMethodSymbol method) symbol = (method.ReducedFrom ?? method).OriginalDefinition;
        return symbol switch
        {
            null => string.Empty,
            IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction } local =>
                CanonicalSymbol(local.ContainingSymbol) + "/local-method:" + local.Name,
            IParameterSymbol parameter => CanonicalSymbol(parameter.ContainingSymbol) + $"/parameter:{parameter.Ordinal}:{parameter.Name}",
            ILocalSymbol local => CanonicalSymbol(local.ContainingSymbol) + "/local:" + local.Name,
            _ => symbol.OriginalDefinition.GetDocumentationCommentId() ?? symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
        };
    }

    private static string TypeId(ITypeSymbol? type)
    {
        if (type is null) return "void";
        if (type is IArrayTypeSymbol array) return TypeId(array.ElementType) + "[" + new string(',', array.Rank - 1) + "]";
        if (type is INamedTypeSymbol { IsTupleType: true, TupleUnderlyingType: { } tuple }) return TypeId(tuple);
        if (type is INamedTypeSymbol named && named.SpecialType == SpecialType.None)
        {
            string owner = named.ContainingType is null ? named.ContainingNamespace.ToDisplayString() : TypeId(named.ContainingType);
            return (owner.Length == 0 ? string.Empty : owner + ".") + named.Name +
                (named.TypeArguments.Length == 0 ? string.Empty : "<" + string.Join(',', named.TypeArguments.Select(TypeId)) + ">");
        }
        return type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat).TrimEnd('?');
    }

    private static string OperationType(IOperation operation) => operation is IVariableDeclaratorOperation variable
        ? TypeId(variable.Symbol.Type) : TypeId(operation.Type);

    private static ExpressionSyntax? ReceiverExpression(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Expression,
        MemberBindingExpressionSyntax => invocation.Ancestors().OfType<ConditionalAccessExpressionSyntax>().First().Expression,
        _ => null,
    };

    private static (string Type, string Symbol, string[] Arguments) InvocationIdentity(string id, SyntaxNode node,
        IOperation operation, SemanticModel model)
    {
        if (node is not InvocationExpressionSyntax invocation || operation is not IInvocationOperation call)
            return (string.Empty, string.Empty, []);
        ExpressionSyntax? receiver = ReceiverExpression(invocation);
        string receiverType = receiver is null ? (call.TargetMethod.IsStatic ? string.Empty : TypeId(call.TargetMethod.ContainingType))
            : TypeId(model.GetTypeInfo(receiver).Type);
        ExpressionSyntax? symbolReceiver = receiver;
        if (call.TargetMethod.ContainingType.Name == "ContainerBuilderExtensions")
        {
            while (symbolReceiver is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax fluent })
                symbolReceiver = fluent.Expression;
        }
        string receiverSymbol = symbolReceiver is null ? (call.TargetMethod.IsStatic ? "static" : "this:" + receiverType)
            : CanonicalSymbol(model.GetSymbolInfo(symbolReceiver).Symbol);
        if (receiverSymbol.Length == 0) receiverSymbol = "expression:" + Canonical(receiver!);
        bool identityArguments = id.StartsWith("executor.", StringComparison.Ordinal) ||
            id is "block.transaction-fold-call" or "block.pre-transaction-commit" or "block.post-transaction-commit";
        foreach (ArgumentSyntax argument in invocation.ArgumentList.Arguments)
            ValidateArgumentReferences(model.GetOperation(argument)
                ?? throw new ExtractionException("fold.argument.binding: missing argument operation."), identityArguments);
        string[] arguments = invocation.ArgumentList.Arguments.Select(static argument => Canonical(argument)).ToArray();
        if (call.TargetMethod.IsGenericMethod)
            arguments = [.. call.TargetMethod.TypeArguments.Select(type => "type:" + TypeId(type)), .. arguments];
        return (receiverType, receiverSymbol, arguments);
    }


    private static void ValidateArgumentReferences(IOperation operation, bool identityArguments)
    {
        if (operation is IArgumentOperation argument &&
            (argument.InConversion.IsUserDefined || argument.InConversion.MethodSymbol is not null ||
             argument.OutConversion.IsUserDefined || argument.OutConversion.MethodSymbol is not null ||
             identityArguments && (!argument.InConversion.IsIdentity || !argument.OutConversion.IsIdentity)) ||
            operation is IConversionOperation conversion &&
            (conversion.Conversion.IsUserDefined || conversion.OperatorMethod is not null ||
             identityArguments && !conversion.Conversion.IsIdentity))
            throw new ExtractionException("fold.argument.conversion: admitted arguments must retain identity conversions on main calls and no user-defined conversion operators.");
        ISymbol? reference = operation switch
        {
            IInvocationOperation call => call.TargetMethod,
            IObjectCreationOperation creation => creation.Constructor,
            IPropertyReferenceOperation property => property.Property,
            IFieldReferenceOperation field => field.Field,
            _ => null,
        };
        if (reference is not null)
        {
            string[] allowed =
            [
                "P:Nethermind.Core.Block.Header",
                "P:" + ProcessorType + ".ReceiptsTracer",
                "P:" + TracerType + ".TxReceipts",
                "P:System.ReadOnlySpan\u00601.Item(System.Int32)",
                "F:" + ExecutorType + "._stateProvider",
                "F:Nethermind.Evm.StatusCode.Success",
                "M:" + TracerType + ".BuildReceipt(Nethermind.Core.Address,Nethermind.Evm.TransactionProcessing.GasConsumed@,System.Byte,Nethermind.Core.LogEntry[],Nethermind.Core.Crypto.Hash256)",
                "M:" + TracerType + ".BuildFailedReceipt(Nethermind.Core.Address,Nethermind.Evm.TransactionProcessing.GasConsumed@,System.String,Nethermind.Core.Crypto.Hash256)",
                "M:Nethermind.Consensus.Processing.TxProcessedEventArgs.#ctor(System.Int32,Nethermind.Core.Transaction,Nethermind.Core.BlockHeader,Nethermind.Core.TxReceipt)",
            ];
            if (!allowed.Contains(CanonicalSymbol(reference), StringComparer.Ordinal))
                throw new ExtractionException($"fold.argument.binding: unexpected exact symbol {CanonicalSymbol(reference)}.");
        }
        if (operation is IParameterReferenceOperation parameter)
        {
            IMethodSymbol method = (IMethodSymbol)parameter.Parameter.ContainingSymbol;
            string owner = TypeId(method.ContainingType);
            string[] names = (owner, method.Name) switch
            {
                (ExecutorType, "ProcessTransactions") => ["block", "processingOptions", "receiptsTracer", "token"],
                (ExecutorType, "ProcessTransaction") => ["block", "currentTx", "index", "receiptsTracer", "processingOptions"],
                (ProcessorType, "ProcessBlock") => ["block", "blockTracer", "options", "spec", "token"],
                ("Nethermind.Consensus.Processing.TransactionProcessorAdapterExtensions", "ProcessTransaction") =>
                    ["transactionProcessor", "currentTx", "receiptsTracer", "processingOptions", "stateProvider"],
                (ProcessorType + ".ParallelBlockValidationTransactionsExecutor", "ProcessTransactions") =>
                    ["block", "processingOptions", "receiptsTracer", "token"],
                (TracerType, "MarkAsSuccess") => ["recipient", "gasSpent", "output", "logs", "stateRoot"],
                (TracerType, "MarkAsFailed") => ["recipient", "gasSpent", "output", "error", "stateRoot"],
                _ => [],
            };
            int index = parameter.Parameter.Ordinal;
            if (index >= names.Length || names[index] != parameter.Parameter.Name)
                throw new ExtractionException($"fold.argument.binding: parameter owner or ordinal changed: {CanonicalSymbol(parameter.Parameter)}.");
        }
        if (operation is ILocalReferenceOperation local)
        {
            ISymbol method = local.Local.ContainingSymbol;
            string owner = TypeId(method.ContainingType);
            bool allowed = owner == ExecutorType &&
                (method.Name == "ProcessTransactions" && local.Local.Name is "currentTx" or "i" ||
                 method.Name == "ProcessTransaction" && local.Local.Name == "result");
            if (!allowed)
                throw new ExtractionException($"fold.argument.binding: local owner changed: {CanonicalSymbol(local.Local)}.");
        }
        foreach (IOperation child in operation.ChildOperations) ValidateArgumentReferences(child, identityArguments);
    }

    private const string ProjectionDiagnostic =
        "fold.projection.binding: indexed transaction and helper argument must be the same metadata Transaction local with identity conversions.";
    private const string TransactionType = "Nethermind.Core.Transaction";
    private const string ExecutorSignature = "M:" + ExecutorType + ".ProcessTransactions(Nethermind.Core.Block," +
        "Nethermind.Consensus.Processing.ProcessingOptions," + TracerType + ",System.Threading.CancellationToken)";

    private static TransactionProjectionIdentity BindTransactionProjection(SemanticModel model, Compilation compilation,
        VariableDeclaratorSyntax transaction, VariableDeclaratorSyntax index, InvocationExpressionSyntax helper)
    {
        INamedTypeSymbol? metadataType = compilation.GetTypeByMetadataName(TransactionType);
        if (metadataType is null || metadataType.ContainingAssembly.Name != "Nethermind.Core" ||
            metadataType.Locations.Any(static location => location.IsInSource) ||
            model.GetOperation(transaction) is not IVariableDeclaratorOperation
            {
                Initializer.Value: IArrayElementReferenceOperation
                {
                    ArrayReference: IPropertyReferenceOperation
                    {
                        Instance: IParameterReferenceOperation block,
                    } transactions,
                    Indices: [ILocalReferenceOperation offset],
                } element,
            } declaration ||
            !SymbolEqualityComparer.Default.Equals(declaration.Symbol.Type, metadataType) ||
            !SymbolEqualityComparer.Default.Equals(element.Type, metadataType) ||
            !SymbolEqualityComparer.Default.Equals(offset.Local, model.GetDeclaredSymbol(index)) ||
            model.GetOperation(helper) is not IInvocationOperation call)
            throw new ExtractionException(ProjectionDiagnostic);

        Conversion initializerConversion = model.GetConversion(transaction.Initializer!.Value);
        IArgumentOperation[] arguments = call.Arguments.Where(static argument => argument.Parameter?.Ordinal == 1).ToArray();
        if (!initializerConversion.IsIdentity || initializerConversion.MethodSymbol is not null ||
            arguments is not [IArgumentOperation
            {
                Value: ILocalReferenceOperation argumentLocal,
                Parameter: { } parameter,
            } argument] ||
            !SymbolEqualityComparer.Default.Equals(argumentLocal.Local, declaration.Symbol) ||
            !SymbolEqualityComparer.Default.Equals(parameter.Type, metadataType) ||
            !argument.InConversion.IsIdentity || !argument.OutConversion.IsIdentity ||
            argument.InConversion.MethodSymbol is not null || argument.OutConversion.MethodSymbol is not null)
            throw new ExtractionException(ProjectionDiagnostic);

        TransactionProjectionIdentity identity = new(
            CanonicalSymbol(declaration.Symbol), TypeId(declaration.Symbol.Type), metadataType.ContainingAssembly.Name,
            element.Kind.ToString(), CanonicalSymbol(transactions.Property), CanonicalSymbol(block.Parameter),
            CanonicalSymbol(offset.Local), CanonicalSymbol(parameter), CanonicalSymbol(argumentLocal.Local),
            initializerConversion.IsIdentity, argument.InConversion.IsIdentity,
            CanonicalSymbol(initializerConversion.MethodSymbol), CanonicalSymbol(argument.InConversion.MethodSymbol));
        ValidateTransactionProjection(identity);
        return identity;
    }

    private static void ValidateTransactionProjection(TransactionProjectionIdentity projection)
    {
        TransactionProjectionIdentity expected = new(
            ExecutorSignature + "/local:currentTx", TransactionType, "Nethermind.Core", "ArrayElementReference",
            "P:Nethermind.Core.Block.Transactions", ExecutorSignature + "/parameter:0:block",
            ExecutorSignature + "/local:i", Contract("executor.process-transaction-call").Target + "/parameter:1:currentTx",
            ExecutorSignature + "/local:currentTx", true, true, string.Empty, string.Empty);
        if (projection != expected) throw new ExtractionException(ProjectionDiagnostic);
    }

    private static void ValidateDeclaredOwner(MethodDeclarationSyntax method, SemanticModel model, string expectedOwner, string id)
    {
        IMethodSymbol declared = model.GetDeclaredSymbol(method) ?? throw new ExtractionException($"fold.owner.{id}: unresolved declaration.");
        if (TypeId(declared.ContainingType) != expectedOwner)
            throw new ExtractionException($"fold.owner.{id}: declaration changed.");
    }

    private static SynchronousCallableIdentity BindDirectThrowHelper(SemanticModel model, MethodDeclarationSyntax caller,
        InvocationExpressionSyntax invocation, string id, bool local, string expectedCreation, string expectedConstructor)
    {
        if (model.GetOperation(invocation) is not IInvocationOperation call || !call.TargetMethod.IsStatic ||
            !call.TargetMethod.ReturnsVoid || call.TargetMethod.DeclaringSyntaxReferences.Length != 1)
            throw new ExtractionException($"fold.throw.{id}: helper identity changed.");

        SyntaxNode declaration = call.TargetMethod.DeclaringSyntaxReferences[0].GetSyntax();
        ExpressionSyntax? body = declaration switch
        {
            LocalFunctionStatementSyntax helper when local && helper.Body is null &&
                ReferenceEquals(helper.Parent, caller.Body) &&
                SymbolEqualityComparer.Default.Equals(call.TargetMethod.ContainingSymbol, model.GetDeclaredSymbol(caller)) =>
                helper.ExpressionBody?.Expression,
            MethodDeclarationSyntax helper when !local && helper.Body is null &&
                ReferenceEquals(helper.Parent, caller.Parent) => helper.ExpressionBody?.Expression,
            _ => null,
        };
        if (body is not ThrowExpressionSyntax { Expression: ObjectCreationExpressionSyntax creation } ||
            Canonical(creation) != expectedCreation ||
            model.GetOperation(creation) is not IObjectCreationOperation allocation ||
            CanonicalSymbol(allocation.Constructor) != expectedConstructor)
            throw new ExtractionException($"fold.throw.{id}: helper must directly throw the exact admitted exception.");

        foreach (IdentifierNameSyntax identifier in creation.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (model.GetOperation(identifier) is IParameterReferenceOperation parameter &&
                !SymbolEqualityComparer.Default.Equals(parameter.Parameter.ContainingSymbol, call.TargetMethod))
                throw new ExtractionException($"fold.throw.{id}: helper parameter owner changed.");
        }
        if (local)
        {
            if (model.GetSymbolInfo(creation.ArgumentList!.Arguments[1].Expression).Symbol is not
                IFieldSymbol { Name: "ExceededGasLimit", IsConst: true, ContainingType: { } messages } ||
                TypeId(messages) != "Nethermind.Core.Messages.BlockErrorMessages")
                throw new ExtractionException($"fold.throw.{id}: gas-limit message identity changed.");
        }
        return BindSynchronousCallable(id, declaration, call.TargetMethod);
    }

    private static SynchronousCallableIdentity BindSynchronousCallable(string id, SyntaxNode declaration, IMethodSymbol symbol)
    {
        string body = declaration switch
        {
            MethodDeclarationSyntax { Body: not null } or LocalFunctionStatementSyntax { Body: not null } => "block",
            MethodDeclarationSyntax { ExpressionBody: not null } or LocalFunctionStatementSyntax { ExpressionBody: not null } => "expression",
            _ => "none",
        };
        CallableAttributeIdentity[] attributes = CallableAttributes(symbol);
        SynchronousCallableIdentity identity = new(id, CanonicalSymbol(symbol), symbol.MethodKind.ToString(), body,
            symbol.ReturnsVoid, symbol.IsAsync, declaration.DescendantNodes().OfType<YieldStatementSyntax>().Any(),
            symbol.IsPartialDefinition || symbol.PartialDefinitionPart is not null || symbol.PartialImplementationPart is not null,
            symbol.IsExtern, symbol.IsAbstract,
            attributes.Any(static attribute => attribute.Type == "System.Diagnostics.ConditionalAttribute"), attributes,
            BindCallableLineage(symbol));
        ValidateSynchronousCallable(identity);
        return identity;
    }

    private static CallableAttributeIdentity[] CallableAttributes(IMethodSymbol symbol) =>
        symbol.GetAttributes().Select(attribute => new CallableAttributeIdentity(
            TypeId(attribute.AttributeClass), attribute.AttributeClass?.ContainingAssembly.Name ?? string.Empty,
            attribute.ConstructorArguments.Length != 0 || attribute.NamedArguments.Length != 0)).ToArray();

    private static CallableLineageIdentity BindCallableLineage(IMethodSymbol symbol)
    {
        List<string> overriddenMethods = [];
        List<CallableAttributeIdentity> inheritedAttributes = [];
        for (IMethodSymbol? overridden = symbol.OverriddenMethod; overridden is not null; overridden = overridden.OverriddenMethod)
        {
            overriddenMethods.Add(CanonicalSymbol(overridden));
            inheritedAttributes.AddRange(CallableAttributes(overridden));
        }
        List<CallableTypeIdentity> baseTypes = [];
        for (INamedTypeSymbol? parent = symbol.ContainingType.BaseType; parent is not null; parent = parent.BaseType)
            baseTypes.Add(new(CanonicalSymbol(parent), parent.ContainingAssembly.Name));
        CallableTypeIdentity declaringType = new(CanonicalSymbol(symbol.ContainingType), symbol.ContainingAssembly.Name);
        CallableTypeIdentity[] interfaces = symbol.ContainingType.AllInterfaces.Select(type =>
            new CallableTypeIdentity(CanonicalSymbol(type), type.ContainingAssembly.Name)).ToArray();
        string[] implementedMethods = symbol.ContainingType.AllInterfaces.SelectMany(static type => type.GetMembers())
            .OfType<IMethodSymbol>().Where(method => SymbolEqualityComparer.Default.Equals(
                symbol.ContainingType.FindImplementationForInterfaceMember(method), symbol))
            .Select(CanonicalSymbol).ToArray();
        return new(symbol.IsStatic, symbol.IsVirtual, symbol.IsOverride, overriddenMethods.ToArray(),
            inheritedAttributes.Any(static attribute => attribute.Type == "System.Diagnostics.ConditionalAttribute"),
            inheritedAttributes.ToArray(), declaringType, baseTypes.ToArray(), interfaces, implementedMethods);
    }

    private static void ValidateCallableLineage(SynchronousCallableIdentity callable)
    {
        CallableLineageIdentity lineage = callable.Lineage ??
            throw new ExtractionException($"fold.synchronous.{callable.Id}.lineage: exact callable lineage changed.");
        if (lineage.HasInheritedConditionalAttribute)
            throw new ExtractionException($"fold.synchronous.{callable.Id}.lineage: inherited ConditionalAttribute is not admitted.");
        bool transaction = callable.Id == "executor.processTransaction";
        if (lineage.IsStatic != !transaction || lineage.IsVirtual != transaction || lineage.IsOverride ||
            lineage.OverriddenMethods is not { Length: 0 } || lineage.InheritedAttributes is not { Length: 0 } ||
            lineage.DeclaringType != new CallableTypeIdentity("T:" + ExecutorType, "Nethermind.Consensus") ||
            lineage.BaseTypes is null || !lineage.BaseTypes.SequenceEqual(
                [new CallableTypeIdentity("T:System.Object", "System.Private.CoreLib")]) ||
            lineage.Interfaces is null || !lineage.Interfaces.SequenceEqual(
                [new CallableTypeIdentity("T:Nethermind.Consensus.Processing.IBlockProcessor.IBlockTransactionsExecutor", "Nethermind.Consensus")]) ||
            lineage.ImplementedInterfaceMethods is not { Length: 0 })
            throw new ExtractionException($"fold.synchronous.{callable.Id}.lineage: exact callable lineage changed.");
    }

    private static void ValidateSynchronousCallable(SynchronousCallableIdentity callable)
    {
        if (callable.HasConditionalAttribute)
            throw new ExtractionException($"fold.synchronous.{callable.Id}.attributes: ConditionalAttribute is not admitted.");
        (string symbol, string methodKind, string bodyKind) = callable.Id switch
        {
            "executor.processTransaction" => (Contract("executor.process-transaction-call").Target, "Ordinary", "block"),
            "executor.gas-limit-helper" => (ExecutorSignature + "/local-method:ThrowInvalidBlockForGasLimit", "LocalFunction", "expression"),
            "executor.invalid-result-helper" => (Contract("executor.invalid-result-throw").Target, "Ordinary", "expression"),
            _ => throw new ExtractionException("fold.synchronous.inventory: callable identity set changed."),
        };
        if (callable != new SynchronousCallableIdentity(callable.Id, symbol, methodKind, bodyKind,
                true, false, false, false, false, false, false, callable.Attributes, callable.Lineage))
            throw new ExtractionException($"fold.synchronous.{callable.Id}: callable must retain its synchronous, non-iterator, non-partial, non-extern concrete void body.");
        ValidateCallableLineage(callable);
        CallableAttributeIdentity[] expectedAttributes = callable.Id switch
        {
            "executor.gas-limit-helper" =>
            [
                new("System.Diagnostics.DebuggerHiddenAttribute", "System.Private.CoreLib", false),
                new("System.Diagnostics.CodeAnalysis.DoesNotReturnAttribute", "Microsoft.TestPlatform.Utilities", false),
            ],
            "executor.invalid-result-helper" =>
            [
                new("System.Diagnostics.CodeAnalysis.DoesNotReturnAttribute", "System.Private.CoreLib", false),
                new("System.Diagnostics.StackTraceHiddenAttribute", "System.Private.CoreLib", false),
            ],
            _ => [],
        };
        if (callable.Attributes is null || !callable.Attributes.SequenceEqual(expectedAttributes))
            throw new ExtractionException($"fold.synchronous.{callable.Id}.attributes: exact attribute inventory changed.");
    }

    private static void ValidateSynchronousCallables(SynchronousCallableIdentity[] callables)
    {
        if (callables is null || callables.Any(static callable => callable is null) ||
            !callables.Select(static callable => callable.Id).SequenceEqual(
                ["executor.processTransaction", "executor.gas-limit-helper", "executor.invalid-result-helper"], StringComparer.Ordinal))
            throw new ExtractionException("fold.synchronous.inventory: callable identity set changed.");
        foreach (SynchronousCallableIdentity callable in callables) ValidateSynchronousCallable(callable);
    }

    private static string MainOwner(string id) => id.StartsWith("executor.", StringComparison.Ordinal) ? ExecutorType : ProcessorType;

    private static string AuxiliaryOwner(string owner) => owner == "BlockReceiptsTracer" ? TracerType :
        owner.StartsWith("BlockProcessingModule", StringComparison.Ordinal) ? "Nethermind.Init.Modules." + owner :
        "Nethermind.Consensus.Processing." + owner;

    private sealed record InvocationContract(string Target, string Receiver, string[] Arguments);

    private static string ExpectedAuxiliaryTarget(string id, string fallback) => id switch
    {
        "tracer.reset-index" or "tracer.tx-end-index" => "P:" + TracerType + "._currentIndex",
        "tracer.reset-receipt-gas" => "F:" + TracerType + "._cumulativeReceiptGas",
        "tracer.receipt-index" => "P:Nethermind.Core.TxReceipt.Index",
        "tracer.tx-start-current" => "F:" + TracerType + ".CurrentTx",
        "tracer.tx-start-delegate" => "F:" + TracerType + "._currentTxTracer",
        "bal.enabled-spec" => "F:" + BalType + "._blockAccessListsEnabled",
        "bal.enabled-derived" => "P:" + BalType + ".Enabled",
        _ => fallback,
    };

    private static InvocationContract Contract(string id) => id switch
    {
        "executor.metrics-setup" => new("M:" + ExecutorType + ".SetupTxTimingMetrics(Nethermind.Core.Block)", ExecutorType, ["block"]),
        "executor.process-transaction-call" => new("M:" + ExecutorType + ".ProcessTransaction(Nethermind.Core.Block,Nethermind.Core.Transaction,System.Int32," + TracerType + ",Nethermind.Consensus.Processing.ProcessingOptions)", ExecutorType, ["block", "currentTx", "i", "receiptsTracer", "processingOptions"]),
        "executor.invalid-result-throw" => new("M:" + ExecutorType + ".ThrowInvalidTransactionException(Nethermind.Evm.TransactionProcessing.TransactionResult,Nethermind.Core.BlockHeader,Nethermind.Core.Transaction,System.Int32)", "", ["result", "block.Header", "currentTx", "index"]),
        "executor.transaction-adapter-call" => new("M:Nethermind.Consensus.Processing.TransactionProcessorAdapterExtensions.ProcessTransaction(Nethermind.Evm.TransactionProcessing.ITransactionProcessorAdapter,Nethermind.Core.Transaction," + TracerType + ",Nethermind.Consensus.Processing.ProcessingOptions,Nethermind.Evm.State.IWorldState)", "Nethermind.Evm.TransactionProcessing.ITransactionProcessorAdapter", ["currentTx", "receiptsTracer", "processingOptions", "_stateProvider"]),
        "executor.processed-event" => new("M:" + ExecutorType + ".ITransactionProcessedEventHandler.OnTransactionProcessed(Nethermind.Consensus.Processing.TxProcessedEventArgs)", ExecutorType + ".ITransactionProcessedEventHandler", ["newTxProcessedEventArgs(index,currentTx,block.Header,receiptsTracer.TxReceipts[index])"]),
        "block.transaction-fold-call" or "parallel.inner-sequential" => new("M:Nethermind.Consensus.Processing.IBlockProcessor.IBlockTransactionsExecutor.ProcessTransactions(Nethermind.Core.Block,Nethermind.Consensus.Processing.ProcessingOptions," + TracerType + ",System.Threading.CancellationToken)", "Nethermind.Consensus.Processing.IBlockProcessor.IBlockTransactionsExecutor", id == "block.transaction-fold-call" ? ["block", "options", "ReceiptsTracer", "token"] : ["block", "processingOptions", "receiptsTracer", "token"]),
        "block.pre-transaction-commit" or "block.post-transaction-commit" => new("M:" + ProcessorType + ".CommitState(Nethermind.Core.Specs.IReleaseSpec)", ProcessorType, ["spec"]),
        "block.transactions-executed-signal" => new("M:System.Action.Invoke", "System.Action", []),
        "block.receipts-tracer-start" => new("M:" + TracerType + ".StartNewBlockTrace(Nethermind.Core.Block)", TracerType, ["block"]),
        "adapter.tx-trace-start" => new("M:" + TracerType + ".StartNewTxTrace(Nethermind.Core.Transaction)", TracerType, ["currentTx"]),
        "adapter.execute" => new("M:Nethermind.Evm.TransactionProcessing.ITransactionProcessorAdapter.Execute(Nethermind.Core.Transaction,Nethermind.Evm.Tracing.ITxTracer)", "Nethermind.Evm.TransactionProcessing.ITransactionProcessorAdapter", ["currentTx", "receiptsTracer"]),
        "adapter.tx-trace-end" => new("M:" + TracerType + ".EndTxTrace", TracerType, []),
        "tracer.reset-receipts" => new("M:System.Collections.Generic.List`1.Clear", "System.Collections.Generic.List<Nethermind.Core.TxReceipt>", []),
        "tracer.reset-block-gas" => new("M:System.Collections.Generic.List`1.Clear", "System.Collections.Generic.List<System.ValueTuple<ulong,ulong>>", []),
        "tracer.success-append" => new("M:System.Collections.Generic.List`1.Add(`0)", "System.Collections.Generic.List<Nethermind.Core.TxReceipt>", ["BuildReceipt(recipient,gasSpent,StatusCode.Success,logs,stateRoot)"]),
        "tracer.failure-append" => new("M:System.Collections.Generic.List`1.Add(`0)", "System.Collections.Generic.List<Nethermind.Core.TxReceipt>", ["BuildFailedReceipt(recipient,gasSpent,error,stateRoot)"]),
        "tracer.tx-end-delegate" => new("M:Nethermind.Evm.Tracing.IBlockTracer.EndTxTrace", "Nethermind.Evm.Tracing.IBlockTracer", []),
        "di.base-executor" => new("M:Nethermind.Core.ContainerBuilderExtensions.AddScoped``2(Autofac.ContainerBuilder)", "Autofac.ContainerBuilder", ["type:Nethermind.Consensus.Processing.IBlockProcessor.IBlockTransactionsExecutor", "type:" + ExecutorType]),
        "di.parallel-decorator" => new("M:Nethermind.Core.ContainerBuilderExtensions.AddDecorator``2(Autofac.ContainerBuilder)", "Autofac.ContainerBuilder", ["type:Nethermind.Consensus.Processing.IBlockProcessor.IBlockTransactionsExecutor", "type:" + ProcessorType + ".ParallelBlockValidationTransactionsExecutor"]),
        "di.bal-manager" => new("M:Nethermind.Core.ContainerBuilderExtensions.AddScoped``2(Autofac.ContainerBuilder)", "Autofac.ContainerBuilder", ["type:Nethermind.Consensus.Processing.IBlockAccessListManager", "type:" + BalType]),
        _ => throw new ExtractionException($"fold.contract.{id}: unknown invocation contract."),
    };


    private const string AdapterSignature =
        "M:Nethermind.Consensus.Processing.TransactionProcessorAdapterExtensions.ProcessTransaction(" +
        "Nethermind.Evm.TransactionProcessing.ITransactionProcessorAdapter,Nethermind.Core.Transaction," +
        TracerType + ",Nethermind.Consensus.Processing.ProcessingOptions,Nethermind.Evm.State.IWorldState)";
    private const string ExecutorConstructor = "M:" + ExecutorType + ".#ctor(" +
        "Nethermind.Evm.TransactionProcessing.ITransactionProcessorAdapter,Nethermind.Evm.State.IWorldState," +
        ExecutorType + ".ITransactionProcessedEventHandler)";
    private const string ParallelConstructor = "M:" + ProcessorType + ".ParallelBlockValidationTransactionsExecutor.#ctor(" +
        "Nethermind.Consensus.Processing.IBlockProcessor.IBlockTransactionsExecutor,Nethermind.Evm.State.IWorldState," +
        "Nethermind.Core.Specs.ISpecProvider,Nethermind.Consensus.Processing.IBlockAccessListManager,Nethermind.Logging.ILogManager," +
        ExecutorType + ".ITransactionProcessedEventHandler)";

    private static string ReceiverContract(string id) => id switch
    {
        "executor.metrics-setup" or "executor.process-transaction-call" => "this:" + ExecutorType,
        "executor.invalid-result-throw" => "static",
        "executor.transaction-adapter-call" => ExecutorConstructor + "/parameter:0:transactionProcessor",
        "executor.processed-event" => "F:" + ExecutorType + "._transactionProcessedEventHandler",
        "block.transaction-fold-call" => "F:" + ProcessorType + "._blockTransactionsExecutor",
        "block.pre-transaction-commit" or "block.post-transaction-commit" => "this:" + ProcessorType,
        "block.transactions-executed-signal" => "E:" + ProcessorType + ".TransactionsExecuted",
        "block.receipts-tracer-start" => "P:" + ProcessorType + ".ReceiptsTracer",
        "adapter.tx-trace-start" or "adapter.tx-trace-end" => AdapterSignature + "/parameter:2:receiptsTracer",
        "adapter.execute" => AdapterSignature + "/parameter:0:transactionProcessor",
        "tracer.reset-receipts" or "tracer.success-append" or "tracer.failure-append" => "F:" + TracerType + "._txReceipts",
        "tracer.reset-block-gas" => "F:" + TracerType + "._cumulativeBlockGasPerTx",
        "tracer.tx-end-delegate" => "F:" + TracerType + "._otherTracer",
        "parallel.inner-sequential" => ParallelConstructor + "/parameter:0:inner",
        "di.base-executor" or "di.parallel-decorator" =>
            "M:Nethermind.Init.Modules.BlockProcessingModule.StandardBlockValidationModule.Load(Autofac.ContainerBuilder)/parameter:0:builder",
        "di.bal-manager" => "M:Nethermind.Init.Modules.BlockProcessingModule.Load(Autofac.ContainerBuilder)/parameter:0:builder",
        _ => throw new ExtractionException($"fold.contract.{id}: unknown receiver contract."),
    };

    private static void ValidateInvocation(string id, string target, string receiver, string receiverSymbol, string[] arguments)
    {
        InvocationContract expected = Contract(id);
        if (target != expected.Target || receiver != expected.Receiver || receiverSymbol != ReceiverContract(id) ||
            arguments is null || !arguments.SequenceEqual(expected.Arguments, StringComparer.Ordinal))
            throw new ExtractionException($"fold.binding.{id}: exact target, receiver or arguments changed.");
    }
}
