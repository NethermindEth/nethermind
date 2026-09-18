// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Evm.Lean.SequentialBlockPostTransactionFinalizationExtractor;

internal static partial class Extractor
{
    private const string TimerPath = "src/Nethermind/Nethermind.Core/Metric/MetricsTimer.cs";
    private const string TimerDefinition = "global::Nethermind.Core.Metric.MetricsTimer<TMetrics>";
    private const string EnabledValue = "global::Nethermind.Evm.ExecutionMetricsFlag.IsActive";
    private static readonly SourceIdentity TimerSource = new(TimerPath, "instrumentation-wrapper",
        "9e72b71ff14688b9dfb16f5a09db1263153440154e5a1886e4f949f3ab5c4ca1",
        "65fb9dbdd1ed9a419ce44ad6c2fef46685f5d6c85275b1aa10a15eae9f9ea620");

    private sealed record InstrumentationRule(string Name, string OwnerSymbol, string SyntaxSha256, string[] Calls);

    private static readonly InstrumentationRule[] InstrumentationRules =
    [
        new("BloomsTimeSink", CalculateBloomsMethodSymbol,
            "2666099d09fb638d2ec053386f7aead33b21e3d43a5a79a86db0a474cba11c31", ["IncrementBloomsTime"]),
        new("CommitTimeSink", CommitStateMethodSymbol,
            "5c87be1345df77b9abf2ce6ca7fd7e15568a89ef5d853927de028bf578357e3e", ["IncrementCommitTime"]),
        new("ReceiptsRootTimeSink", CalculateReceiptsRootMethodSymbol,
            "8c2b60cd60f4cfd7010b71b82eaac4cd35c0cc5e00d960d957cf60be7a9eba21", ["IncrementReceiptsRootTime"]),
        new("StateRootTimeSink", ComputeStateRootMethodSymbol,
            "0900a60eba60f797d396d80e732bd104cdb6a35931988164993acbd82b6a051f", ["IncrementStateHashTime", "IncrementStateRootTime"]),
        new("StorageMerkleTimeSink", CommitRootsMethodSymbol,
            "3dd8e64b1a6692574a778f925a15340615f1f9e8fe3a3b0751d1494c854db39a", ["IncrementStateHashTime", "IncrementStorageMerkleTime"]),
    ];

    private static InstrumentationBoundaryIdentity BuildInstrumentationBoundary(string root, SourceFile source,
        MethodDeclarationSyntax processBlock, SemanticModel model, SourceLocalMethod[] helpers, Compilation compilation)
    {
        SourceIdentity wrapperIdentity = ReadInstrumentationWrapper(root);
        List<InstrumentationSinkIdentity> sinks = [];
        foreach (InstrumentationRule rule in InstrumentationRules)
        {
            string typeId = BlockProcessorTypeSymbol + "." + rule.Name;
            StructDeclarationSyntax[] declarations = source.Root.DescendantNodes().OfType<StructDeclarationSyntax>()
                .Where(type => model.GetDeclaredSymbol(type)?.ToSourceIdentity() == typeId).ToArray();
            if (declarations.Length != 1)
                throw new ExtractionException($"Instrumentation sink '{rule.Name}' must have exactly one source declaration.");
            StructDeclarationSyntax declaration = declarations[0];
            string canonical = Canonical(declaration);
            // The whole type closes implicit getters, initializers, conversions, and disposal additions.
            if (Sha256(Encoding.UTF8.GetBytes(canonical)) != rule.SyntaxSha256)
                throw new ExtractionException($"Instrumentation sink '{rule.Name}' preservation declaration changed.");
            INamedTypeSymbol type = model.GetDeclaredSymbol(declaration)
                ?? throw new ExtractionException("Instrumentation sink type is unresolved.");
            PropertyDeclarationSyntax enabled = declaration.Members.OfType<PropertyDeclarationSyntax>().Single();
            MethodDeclarationSyntax addTicks = declaration.Members.OfType<MethodDeclarationSyntax>().Single();
            IPropertySymbol enabledSymbol = model.GetDeclaredSymbol(enabled)
                ?? throw new ExtractionException("Instrumentation enabled getter is unresolved.");
            IMethodSymbol addTicksSymbol = model.GetDeclaredSymbol(addTicks)
                ?? throw new ExtractionException("Instrumentation AddTicks callback is unresolved.");
            IOperation enabledOperation = model.GetOperation(enabled.ExpressionBody!.Expression)
                ?? throw new ExtractionException("Instrumentation enabled value is unresolved.");
            if (enabledOperation is not IPropertyReferenceOperation enabledReference ||
                enabledReference.Property.ToSourceIdentity() != EnabledValue ||
                IsSourceOwnedSymbol(enabledReference.Property, compilation))
                throw new ExtractionException($"Instrumentation sink '{rule.Name}' enabled getter target changed.");
            string[] calls = addTicks.DescendantNodes().OfType<InvocationExpressionSyntax>().Select(invocation =>
            {
                if (model.GetOperation(invocation) is not IInvocationOperation call ||
                    IsSourceOwnedMethod(call.TargetMethod, compilation) || !call.TargetMethod.IsStatic ||
                    call.Arguments.Length != 1 || call.Arguments[0].Value is not IParameterReferenceOperation parameter ||
                    !SymbolEqualityComparer.Default.Equals(parameter.Parameter, addTicksSymbol.Parameters[0]))
                    throw new ExtractionException($"Instrumentation sink '{rule.Name}' callback target or argument changed.");
                return MethodSymbolId(call.TargetMethod);
            }).ToArray();
            if (type.AllInterfaces.Length != 1 ||
                type.AllInterfaces[0].ToSourceIdentity() != "global::Nethermind.Core.Metric.IMetricSink" ||
                IsSourceOwnedSymbol(type.AllInterfaces[0], compilation))
                throw new ExtractionException($"Instrumentation sink '{rule.Name}' interface changed.");

            SourceLocalMethod owner = helpers.Single(helper => MethodSymbolId(helper.Symbol) == rule.OwnerSymbol);
            IObjectCreationOperation[] creations = DescendantOperations(owner.Model.GetOperation(owner.Syntax)!)
                .OfType<IObjectCreationOperation>().Where(creation => IsExactInstrumentationUsing(owner, creation.Syntax)).ToArray();
            if (creations.Length != 1 || creations[0].Type is not INamedTypeSymbol resourceType ||
                resourceType.OriginalDefinition.ToSourceIdentity() != TimerDefinition || resourceType.TypeArguments.Length != 1 ||
                !SymbolEqualityComparer.Default.Equals(resourceType.TypeArguments[0], type) ||
                IsSourceOwnedTypeDefinition(resourceType, compilation) || resourceType.ContainingAssembly.Name != "Nethermind.Core" ||
                creations[0].Constructor is not IMethodSymbol constructor || !constructor.Parameters.IsEmpty)
                throw new ExtractionException($"Instrumentation sink '{rule.Name}' timer constructor binding changed.");
            IMethodSymbol[] disposals = resourceType.GetMembers("Dispose").OfType<IMethodSymbol>().ToArray();
            if (disposals.Length != 1 || !disposals[0].Parameters.IsEmpty || disposals[0].IsStatic ||
                !disposals[0].ReturnsVoid || IsSourceOwnedMethod(disposals[0], compilation))
                throw new ExtractionException($"Instrumentation sink '{rule.Name}' timer disposal binding changed.");
            VariableDeclarationSyntax resource = creations[0].Syntax.Ancestors().OfType<VariableDeclarationSyntax>().First();
            sinks.Add(new(rule.Name, source.RelativePath, typeId, canonical, rule.SyntaxSha256,
                enabledSymbol.ToSourceIdentity(), enabledReference.Property.ToSourceIdentity(), MethodSymbolId(addTicksSymbol), calls,
                rule.OwnerSymbol, Canonical(resource), ArtifactSafety.TypeIdentity(resourceType),
                MethodSymbolId(constructor.OriginalDefinition), MethodSymbolId(disposals[0].OriginalDefinition)));
        }

        // Every reachable using resource is either one of the five exact timers or the pinned reward tracer.
        foreach (MethodDeclarationSyntax method in helpers.Select(static helper => helper.Syntax).Prepend(processBlock))
        {
            foreach (SyntaxNode resource in method.DescendantNodes().Where(static node =>
                         node is UsingStatementSyntax || node is LocalDeclarationStatementSyntax local && local.UsingKeyword.RawKind != 0))
            {
                MethodDeclarationSyntax owner = resource.Ancestors().OfType<MethodDeclarationSyntax>().First();
                SemanticModel ownerModel = compilation.GetSemanticModel(owner.SyntaxTree, ignoreAccessibility: true);
                string ownerId = MethodSymbolId((IMethodSymbol)ownerModel.GetDeclaredSymbol(owner)!);
                if (ownerId == ApplyMinerRewardsMethodSymbol &&
                    Canonical(resource) == "usingITxTracertxTracer=tracer.StartNewTxTrace(null);") continue;
                SourceLocalMethod? helper = helpers.SingleOrDefault(candidate => MethodSymbolId(candidate.Symbol) == ownerId);
                if (helper is null || !IsExactInstrumentationUsing(helper, resource))
                    throw new ExtractionException("An implicit using/disposal edge is outside the exact instrumentation roster.");
            }
        }
        InstrumentationBoundaryIdentity result = new(wrapperIdentity, sinks.ToArray(),
            BuildInstrumentationActivations(compilation, helpers));
        ValidateInstrumentationBoundary(result, BuildHelperPreservation(helpers));
        return result;
    }

    private static void ValidateSourceGenericCallbackEdges(SourceLocalMethod current, string path,
        IEnumerable<IOperation> operations, Compilation compilation)
    {
        foreach (IOperation operation in operations)
        {
            ISymbol? member = InstrumentationMember(operation);
            bool sourceArguments = HasSourceGenericArguments(operation.Type, compilation) ||
                HasSourceGenericArguments(member?.ContainingType, compilation) || member is IMethodSymbol target &&
                    target.TypeArguments.Any(type => IsSourceOwnedType(type, compilation) || HasSourceGenericArguments(type, compilation));
            bool exactConstruction = UnwrapTargetTypedConstruction(operation) is IObjectCreationOperation;
            if (sourceArguments && !(exactConstruction &&
                    IsExactInstrumentationUsing(current, operation.Syntax)))
                throw new ExtractionException($"ProcessBlock reaches an unbound source-bearing generic callback edge via '{path}' ({operation.Kind}: {OperationCanonical(operation)}).");
        }
    }

    private static IOperation UnwrapTargetTypedConstruction(IOperation operation) =>
        operation is IConversionOperation { IsImplicit: true, OperatorMethod: null,
            Conversion.IsUserDefined: false, Operand: IObjectCreationOperation creation } conversion &&
        SymbolEqualityComparer.Default.Equals(conversion.Type, creation.Type) &&
        conversion.Syntax is ImplicitObjectCreationExpressionSyntax && conversion.Syntax.Span == creation.Syntax.Span
            ? creation : operation;

    private static bool HasSourceGenericArguments(ITypeSymbol? type, Compilation compilation) => type switch
    {
        INamedTypeSymbol named => named.TypeArguments.Any(argument => IsSourceOwnedType(argument, compilation) ||
                HasSourceGenericArguments(argument, compilation)) ||
            HasSourceGenericArguments(named.ContainingType, compilation),
        IArrayTypeSymbol array => HasSourceGenericArguments(array.ElementType, compilation),
        IPointerTypeSymbol pointer => HasSourceGenericArguments(pointer.PointedAtType, compilation),
        _ => false,
    };

    private static InstrumentationActivationIdentity[] BuildInstrumentationActivations(Compilation compilation,
        SourceLocalMethod[] helpers)
    {
        List<InstrumentationActivationIdentity> activations = [];
        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            SemanticModel model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            HashSet<int> creations = [];
            foreach (ExpressionSyntax syntax in tree.GetRoot().DescendantNodes().OfType<ExpressionSyntax>())
            {
                IOperation? operation = model.GetOperation(syntax);
                if (operation is null || !ReferencesTimer(operation)) continue;
                operation = UnwrapTargetTypedConstruction(operation);
                MethodDeclarationSyntax? owner = syntax.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                IMethodSymbol? ownerSymbol = owner is null ? null : model.GetDeclaredSymbol(owner);
                SourceLocalMethod? helper = helpers.SingleOrDefault(candidate =>
                    ownerSymbol is not null && SameMethodSymbol(candidate.Symbol, ownerSymbol));
                if (operation is not IObjectCreationOperation { Type: INamedTypeSymbol timer, Constructor: not null } creation ||
                    helper is null || !IsExactInstrumentationUsing(helper, syntax) || timer.TypeArguments.Length != 1 ||
                    timer.OriginalDefinition.ToSourceIdentity() != TimerDefinition ||
                    creation.Syntax.Parent is not EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax variable } ||
                    variable.Initializer!.Value.Span != creation.Syntax.Span || variable.Parent is not VariableDeclarationSyntax resource)
                    throw new ExtractionException("An explicit timer construction, default, invocation, or receiver is outside the exact instrumentation activation roster.");
                if (creations.Add(creation.Syntax.SpanStart))
                    activations.Add(new(tree.FilePath, MethodSymbolId(helper.Symbol), operation.Kind.ToString(),
                        Canonical(resource), ArtifactSafety.TypeIdentity(timer), timer.TypeArguments[0].ToSourceIdentity(),
                        MethodSymbolId(creation.Constructor.OriginalDefinition)));
            }
        }
        return activations.OrderBy(static activation => activation.SinkTypeSymbol, StringComparer.Ordinal).ToArray();
    }

    private static bool ReferencesTimer(IOperation operation)
    {
        if (ContainsTimer(operation.Type)) return true;
        ISymbol? member = InstrumentationMember(operation);
        return ContainsTimer(member?.ContainingType) || member is IMethodSymbol method && method.TypeArguments.Any(ContainsTimer) ||
            operation is ITypeOfOperation typeOf && ContainsTimer(typeOf.TypeOperand) ||
            operation is ISizeOfOperation sizeOf && ContainsTimer(sizeOf.TypeOperand);
    }

    private static ISymbol? InstrumentationMember(IOperation operation) => operation switch
    {
        IInvocationOperation invocation => invocation.TargetMethod,
        IObjectCreationOperation creation => creation.Constructor,
        IMethodReferenceOperation reference => reference.Method,
        IPropertyReferenceOperation property => property.Property,
        IFieldReferenceOperation field => field.Field,
        IEventReferenceOperation eventReference => eventReference.Event,
        IImplicitIndexerReferenceOperation indexer => indexer.IndexerSymbol,
        IConversionOperation conversion => conversion.OperatorMethod,
        IUnaryOperation unary => unary.OperatorMethod,
        IBinaryOperation binary => binary.OperatorMethod,
        ICompoundAssignmentOperation compound => compound.OperatorMethod,
        IIncrementOrDecrementOperation increment => increment.OperatorMethod,
        ICollectionExpressionOperation collection => collection.ConstructMethod,
        _ => null,
    };

    private static bool ContainsTimer(ITypeSymbol? type) => type switch
    {
        INamedTypeSymbol named => named.OriginalDefinition.ToSourceIdentity() == TimerDefinition ||
            named.TypeArguments.Any(ContainsTimer) || ContainsTimer(named.ContainingType),
        IArrayTypeSymbol array => ContainsTimer(array.ElementType),
        IPointerTypeSymbol pointer => ContainsTimer(pointer.PointedAtType),
        _ => false,
    };

    private static SourceIdentity ReadInstrumentationWrapper(string root)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(root, TimerPath));
        SyntaxTree wrapper = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(bytes), ParseOptions, TimerPath);
        ArtifactSafety.RequireUnconditionalSource(wrapper.GetRoot(), "Finalization MetricsTimer");
        SourceIdentity identity = new(TimerPath, TimerSource.Role, Sha256(bytes),
            Sha256(Encoding.UTF8.GetBytes(Canonical(wrapper.GetRoot()))));
        if (identity != TimerSource)
            throw new ExtractionException("The exact MetricsTimer constructor/getter/disposal wrapper changed.");
        return identity;
    }

    private static void ValidateInstrumentationBoundary(InstrumentationBoundaryIdentity boundary, HelperPreservationIdentity[] helpers)
    {
        if (boundary is null || boundary.Wrapper != TimerSource || boundary.Sinks is null ||
            boundary.Sinks.Length != InstrumentationRules.Length)
            throw new ExtractionException("The complete pinned instrumentation wrapper and sink roster is required.");
        foreach ((InstrumentationSinkIdentity sink, InstrumentationRule rule) in boundary.Sinks.Zip(InstrumentationRules))
        {
            string typeId = BlockProcessorTypeSymbol + "." + rule.Name;
            string resource = "MetricsTimer<" + rule.Name + ">_=new()";
            string wrapperType = "Nethermind.Core.Metric.MetricsTimer<" + typeId.Replace("global::", "", StringComparison.Ordinal) + ">";
            if (sink is null || sink.Name != rule.Name || sink.Path != BlockProcessorPath || sink.TypeSymbol != typeId ||
                sink.OwnerSymbol != rule.OwnerSymbol || sink.ResourceCanonical != resource || sink.WrapperType != wrapperType ||
                sink.ConstructorSymbol != TimerDefinition + "..ctor()" || sink.DisposeSymbol != TimerDefinition + ".Dispose()" ||
                sink.EnabledPropertySymbol != typeId + ".IsEnabled" || sink.EnabledValueSymbol != EnabledValue ||
                sink.AddTicksSymbol != typeId + ".AddTicks(long)" || sink.AddTicksCalls is null ||
                !sink.AddTicksCalls.SequenceEqual(rule.Calls.Select(static call => "global::Nethermind.Evm.Metrics." + call + "(long)"), StringComparer.Ordinal))
                throw new ExtractionException("The exact instrumentation sink/callback/site binding roster changed.");
            if (sink.SyntaxSha256 != rule.SyntaxSha256 || sink.CanonicalSyntax is null ||
                Sha256(Encoding.UTF8.GetBytes(sink.CanonicalSyntax)) != rule.SyntaxSha256)
                throw new ExtractionException($"Instrumentation sink '{rule.Name}' preservation declaration changed.");
            HelperPreservationIdentity[] owners = helpers.Where(helper => helper.SymbolId == rule.OwnerSymbol).ToArray();
            if (owners.Length != 1 || owners[0].Path != BlockProcessorPath ||
                !owners[0].CanonicalSyntax.Contains(resource, StringComparison.Ordinal))
                throw new ExtractionException("The instrumentation timer site is absent from the exact helper preservation closure.");
        }
        InstrumentationActivationIdentity[] expected = boundary.Sinks.Select(static sink => new InstrumentationActivationIdentity(
            sink.Path, sink.OwnerSymbol, "ObjectCreation", sink.ResourceCanonical, sink.WrapperType, sink.TypeSymbol,
            sink.ConstructorSymbol)).ToArray();
        if (boundary.Activations is null || !boundary.Activations.SequenceEqual(expected))
            throw new ExtractionException("The complete exact instrumentation activation roster changed.");
    }
}
