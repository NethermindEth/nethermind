// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Evm.Lean.OrdinaryEvmCompletionExtractor;

internal static class SourceAdmission
{
    internal const string PackagePath = "tools/Evm/Lean/OrdinaryEvmCompletionExtractor";
    internal const string UpstreamPackagePath = "tools/Evm/Lean/OrdinaryTransactionRefundAdapterExtractor";
    internal const string PinsPath = UpstreamPackagePath + "/Admission/SOURCE_PINS.json";
    internal const string PinsSha256 = "2339453e6f2ff64e9de71bf9d022c3aa27869dd3dc1e21b38a4731d2df57f6db";
    internal const string ProcessorType = "Nethermind.Evm.TransactionProcessing.TransactionProcessorBase`1";
    internal const string ProcessorPath = "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs";
    internal sealed record SourcePins(int SchemaVersion, string AssemblyName, string Configuration, bool EnableZkEvm,
        string[] DefineConstants, SourceIdentity[] Sources);
    internal sealed record MethodSite(IMethodSymbol Symbol, MethodDeclarationSyntax Syntax, SemanticModel Model, ControlFlowGraph Graph);

    private static readonly (string Type, string Method, int Parameters)[] Roots =
    [
        (ProcessorType, "Execute", 6),
        (ProcessorType, "ExecuteEvmTransaction", 16),
        (ProcessorType, "ExecuteEvmCall", 14),
        (ProcessorType, "UpdateHeaderGasUsedAndPayFees", 11),
        (ProcessorType, "PayFees", 10),
        (ProcessorType, "FinalizeTransaction", 12),
        ("Nethermind.Evm.VmState`1", "RentTopLevel", 5),
        ("Nethermind.Evm.VmState`1", "Initialize", 12),
        ("Nethermind.Evm.GasPolicy.EthereumGasPolicy", "CombineBlockGas", 2),
        ("Nethermind.Evm.TransactionProcessing.SystemTransactionRoutingKernel", "ParticipatesInNormalBlockCounters", 2),
    ];

    internal static SourceModel Read(string root) => ReadCore(root, null, false)!;
    internal static SourceModel InspectPinnedBaseline(string root) => ReadCore(root, null, false, inspectBindings: true)!;
    internal static SourceModel ReadForTest(string root, IReadOnlyDictionary<string, string> overrides) => ReadCore(root, overrides, false)!;
    internal static void RequireCompilationForTest(string root, IReadOnlyDictionary<string, string> overrides) => _ = ReadCore(root, overrides, true);

    private static SourceModel? ReadCore(string root, IReadOnlyDictionary<string, string>? overrides, bool compileOnly, bool inspectBindings = false)
    {
        root = Path.GetFullPath(root);
        byte[] bytes = File.ReadAllBytes(CompilerReferences.Within(root, PinsPath));
        CompilerReferences.RequireHash(bytes, PinsSha256, PinsPath);
        SourcePins pins = JsonSerializer.Deserialize<SourcePins>(bytes, CompilerReferences.JsonOptions)
            ?? throw new AdmissionException("closure.empty");
        if (pins.SchemaVersion != 1 || pins.AssemblyName != "Nethermind.Evm" || pins.Configuration != "Release" || pins.EnableZkEvm ||
            pins.Sources.Length != 157 || pins.Sources.Select(static source => source.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 157 ||
            pins.Sources.Count(static source => source.Role is "semantic" or "compiler-support" or "compiler-generated") != 146)
            throw new AdmissionException("closure.roster");
        (MetadataReference[] references, ReferenceIdentity[] identities) = CompilerReferences.Load(root, pins);
        HashSet<string> selectedPaths = pins.Sources.Where(static source => source.Role is "semantic" or "compiler-support" or "compiler-generated")
            .Select(static source => source.Path).ToHashSet(StringComparer.Ordinal);
        if (overrides is not null && overrides.Keys.Any(path => !selectedPaths.Contains(path)))
            throw new AdmissionException("closure.unknown-override");
        CSharpParseOptions parse = new(LanguageVersion.CSharp14, DocumentationMode.Parse, SourceCodeKind.Regular, pins.DefineConstants);
        List<SyntaxTree> trees = [];
        foreach (SourceIdentity source in pins.Sources)
        {
            byte[] original = File.ReadAllBytes(CompilerReferences.Within(root, source.Path));
            CompilerReferences.RequireHash(original, source.Sha256, source.Path);
            if (!selectedPaths.Contains(source.Path)) continue;
            string text = overrides is not null && overrides.TryGetValue(source.Path, out string? replacement)
                ? replacement : Encoding.UTF8.GetString(original);
            trees.Add(CSharpSyntaxTree.ParseText(text, parse, source.Path));
        }
        CSharpCompilation compilation = CSharpCompilation.Create(pins.AssemblyName, trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: true, checkOverflow: false, nullableContextOptions: NullableContextOptions.Enable,
                metadataImportOptions: MetadataImportOptions.All));
        Diagnostic[] errors = compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0) throw new AdmissionException("compile: " + string.Join(Environment.NewLine, errors.Select(static error => error.ToString())));
        if (compileOnly) return null;

        RequireLineage(compilation);
        Dictionary<string, MethodSite> sites = new(StringComparer.Ordinal);
        List<AdmittedMember> members = [];
        foreach ((string typeName, string name, int parameterCount) in Roots)
        {
            INamedTypeSymbol type = compilation.GetTypeByMetadataName(typeName) ?? throw new AdmissionException("type." + typeName);
            IMethodSymbol[] candidates = type.GetMembers(name).OfType<IMethodSymbol>().Where(method => method.Parameters.Length == parameterCount).ToArray();
            if (candidates.Length != 1) throw new AdmissionException("callable." + name);
            IMethodSymbol method = candidates[0];
            RequireSynchronous(method);
            if (method.DeclaringSyntaxReferences.Length != 1 || method.DeclaringSyntaxReferences[0].GetSyntax() is not MethodDeclarationSyntax syntax)
                throw new AdmissionException("declaration." + name);
            SemanticModel model = compilation.GetSemanticModel(syntax.SyntaxTree);
            if (!SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(syntax), method) || model.GetOperation(syntax) is not IMethodBodyOperation body)
                throw new AdmissionException("binding." + name);
            LocalFunctionStatementSyntax[] locals = syntax.DescendantNodes().OfType<LocalFunctionStatementSyntax>().ToArray();
            string[] localNames = name switch
            {
                "ExecuteEvmTransaction" => ["FinalizeDestroyedAccount"],
                "Initialize" => ["ThrowIsInUse"],
                _ => [],
            };
            if (!locals.Select(static local => local.Identifier.Text).SequenceEqual(localNames, StringComparer.Ordinal) ||
                syntax.DescendantNodes().Any(static node => node is AnonymousFunctionExpressionSyntax or YieldStatementSyntax))
                throw new AdmissionException("callable." + name);
            ControlFlowGraph graph = ControlFlowGraph.Create(body);
            MethodSite site = new(method, syntax, model, graph);
            sites.Add(name, site);
            MemberIdentity identity = new(OperationLowering.Symbol(method), method.MethodKind.ToString(), OperationLowering.Symbol(method.ReturnType),
                method.Parameters.Select(static parameter => new ParameterIdentity(parameter.Name, parameter.Ordinal,
                    OperationLowering.Symbol(parameter.Type), parameter.RefKind.ToString())).ToArray(), syntax.SyntaxTree.FilePath, syntax.SpanStart, syntax.Span.Length);
            Dictionary<CaptureId, int> captures = Captures(graph.Root).Distinct().Select((capture, index) => (capture, index)).ToDictionary(static pair => pair.capture, static pair => pair.index);
            members.Add(new(identity, OperationLowering.Lower(body), graph.Blocks.Select(block => Block(block, captures)).ToArray(), Region(graph.Root)));
        }
        ContinuationPlan plan = PlanBuilder.Build(sites);
        SourceModel sourceModel = new(new(pins.AssemblyName, "14.0", pins.DefineConstants, CompilerReferences.InventorySha256,
            identities, pins.Sources, members.ToArray()), plan);
        if (!inspectBindings) SemanticBindings.Require(root, sourceModel);
        // Source mutations may compile, but no unreviewed active, disabled or trivia bytes are admitted.
        if (overrides is not null)
            foreach ((string path, string replacement) in overrides)
                if (replacement != File.ReadAllText(CompilerReferences.Within(root, path)))
                    throw new AdmissionException("source.exact." + path);
        return sourceModel;
    }

    private static ControlBlock Block(BasicBlock block, IReadOnlyDictionary<CaptureId, int> captures) => new(block.Ordinal, block.Kind.ToString(), block.ConditionKind.ToString(),
        block.Operations.Select(operation => OperationLowering.Lower(operation, captures)).ToArray(), block.BranchValue is null ? null : OperationLowering.Lower(block.BranchValue, captures),
        Edge(block.FallThroughSuccessor), Edge(block.ConditionalSuccessor));
    private static ControlEdge? Edge(ControlFlowBranch? edge) => edge is null ? null : new(edge.Destination?.Ordinal ?? -1,
        edge.Semantics.ToString(), edge.LeavingRegions.Select(static region => region.FirstBlockOrdinal).ToArray(),
        edge.FinallyRegions.Select(static region => region.FirstBlockOrdinal).ToArray());

    private static ControlRegionModel Region(ControlFlowRegion region) => new(region.Kind.ToString(), region.FirstBlockOrdinal, region.LastBlockOrdinal,
        OperationLowering.Symbol(region.ExceptionType), region.Locals.Select(OperationLowering.Symbol).ToArray(), region.NestedRegions.Select(Region).ToArray());

    private static IEnumerable<CaptureId> Captures(ControlFlowRegion region) => region.CaptureIds.Concat(region.NestedRegions.SelectMany(Captures));

    private static void RequireSynchronous(IMethodSymbol method)
    {
        for (IMethodSymbol? current = method; current is not null; current = current.OverriddenMethod)
            if (current.IsAsync || current.IsExtern || current.PartialDefinitionPart is not null || current.PartialImplementationPart is not null ||
                current.GetAttributes().Any(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Diagnostics.ConditionalAttribute"))
                throw new AdmissionException("callable." + method.Name);
    }

    private static void RequireLineage(CSharpCompilation compilation)
    {
        INamedTypeSymbol? standard = compilation.GetTypeByMetadataName("Nethermind.Evm.TransactionProcessing.EthereumTransactionProcessor");
        if (standard is null || !standard.IsSealed || standard.BaseType?.ToDisplayString() != "Nethermind.Evm.TransactionProcessing.EthereumTransactionProcessorBase" ||
            standard.BaseType.BaseType?.OriginalDefinition.ToDisplayString() != "Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<TGasPolicy>" ||
            standard.BaseType.BaseType.TypeArguments.SingleOrDefault()?.ToDisplayString() != "Nethermind.Evm.GasPolicy.EthereumGasPolicy")
            throw new AdmissionException("lineage.standard-mainnet");
        foreach (INamedTypeSymbol type in new[] { standard, standard.BaseType })
            if (type.GetMembers().OfType<IMethodSymbol>().Any(static method => method.Name is "PayFees" or "Refund" or "FinalizeTransaction" or "ExecuteEvmCall"))
                throw new AdmissionException("lineage.override");
    }
}
