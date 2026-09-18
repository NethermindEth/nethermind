// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Evm.Lean.OrdinaryTransactionRefundAdapterExtractor;

internal static class SourceAdmission
{
    internal const string PackagePath = "tools/Evm/Lean/OrdinaryTransactionRefundAdapterExtractor";
    internal const string PinsPath = PackagePath + "/Admission/SOURCE_PINS.json";
    internal const string PinsSha256 = "2339453e6f2ff64e9de71bf9d022c3aa27869dd3dc1e21b38a4731d2df57f6db";
    private const string ReviewedSyntaxSha256 = "4eaa1662e31e26e40b623b1b78dbfa319e620a3056c99165cd1733b28ba9dfea";
    private const string EvmPath = "src/Nethermind/Nethermind.Evm/";
    private const string ProcessorType = "Nethermind.Evm.TransactionProcessing.TransactionProcessorBase`1";
    private const string PolicyType = "Nethermind.Evm.GasPolicy.EthereumGasPolicy";

    private static readonly Dictionary<string, string[]> OwnedMethods = new(StringComparer.Ordinal)
    {
        [ProcessorType] =
        [
            "Refund", "CompleteEip8037Halt", "RefundRevertedExecutionStateGas", "RefundOnContractCollision",
            "RefundOnTopLevelHalt", "RefundFailedEip8037Gas", "CalculateInitialStateReservoir", "ShouldRefundGas",
            "PayRefund", "ShouldValidateGas", "CalculateClaimableRefund",
        ],
        [PolicyType] =
        [
            "ClearExecutionGas", "GetRemainingGas", "GetStateReservoir", "GetStateGasUsed", "RefundStateGas",
            "ResetForHalt", "GetCodeInsertExecutionRefund", "ApplyCodeInsertRefunds", "ApplyStateGasTransition",
        ],
        ["Nethermind.Evm.GasPolicy.IGasPolicy`1"] = ["GetPreRefundGas", "GetCreateStateCost"],
        ["Nethermind.Evm.RefundHelper"] = ["CalculateClaimableRefund"],
        ["Nethermind.Evm.GasPolicy.Eip8037BlockGasInclusionCheck"] = ["CalculateBlockExecutionGas"],
    };

    internal sealed record SourcePins(int SchemaVersion, string AssemblyName, string Configuration, bool EnableZkEvm, string[] DefineConstants, SourceIdentity[] Sources);
    internal sealed record AdmissionResult(CompilerAdmission Admission, RefundConstants Constants, RefundPlan Plan);
    private sealed record ReviewedSyntax(int SchemaVersion, Artifacts.Identity[] Sources);

    internal static AdmissionResult Read(string root) => ReadCore(root, null, false)!;

    internal static AdmissionResult ReadForTest(string root, IReadOnlyDictionary<string, string> overrides) => ReadCore(root, overrides, false)!;

    internal static void RequireCompilationForTest(string root, IReadOnlyDictionary<string, string> overrides) =>
        _ = ReadCore(root, overrides, true);

    private static AdmissionResult? ReadCore(string root, IReadOnlyDictionary<string, string>? overrides, bool compilationOnly)
    {
        root = Path.GetFullPath(root);
        byte[] pinBytes = File.ReadAllBytes(CompilerReferences.Within(root, PinsPath));
        CompilerReferences.RequireHash(pinBytes, PinsSha256, PinsPath);
        SourcePins pins = JsonSerializer.Deserialize<SourcePins>(pinBytes, CompilerReferences.JsonOptions)
            ?? throw new AdmissionException("The source pin roster is empty.");
        if (pins.SchemaVersion != 1 || pins.AssemblyName != "Nethermind.Evm" || pins.Configuration != "Release" || pins.EnableZkEvm ||
            pins.Sources.Length != 157 || pins.Sources.Count(static source => source.Role == "semantic") != 11 ||
            pins.Sources.Count(static source => source.Role == "compiler-support") != 132 || pins.Sources.Count(static source => source.Role == "compiler-generated") != 3)
        {
            throw new AdmissionException("The source/build selection header changed.");
        }
        (MetadataReference[] references, ReferenceIdentity[] identities) = CompilerReferences.Load(root, pins);

        string[] liveSources = Directory.EnumerateFiles(CompilerReferences.Within(root, EvmPath), "*.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(static path => !path.Contains("/zkevm/", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains("/obj/", StringComparison.OrdinalIgnoreCase) && !path.Contains("/bin/", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".zkevm.cs", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).ToArray();
        string[] expectedSources = pins.Sources.Where(static source => source.Path.StartsWith(EvmPath, StringComparison.Ordinal) && source.Path.EndsWith(".cs", StringComparison.Ordinal))
            .Select(static source => source.Path).Order(StringComparer.Ordinal).ToArray();
        if (!liveSources.SequenceEqual(expectedSources, StringComparer.Ordinal))
        {
            throw new AdmissionException("The selected EVM source roster changed.");
        }

        CSharpParseOptions parseOptions = new(LanguageVersion.CSharp14, DocumentationMode.Parse, SourceCodeKind.Regular, pins.DefineConstants);
        Dictionary<SyntaxTree, SourceIdentity> sourceByTree = [];
        foreach (SourceIdentity identity in pins.Sources)
        {
            byte[] bytes = File.ReadAllBytes(CompilerReferences.Within(root, identity.Path));
            CompilerReferences.RequireHash(bytes, identity.Sha256, identity.Path);
            if (identity.Role is not ("semantic" or "compiler-support" or "compiler-generated"))
            {
                continue;
            }
            string text = overrides is not null && overrides.TryGetValue(identity.Path, out string? replacement)
                ? replacement : Encoding.UTF8.GetString(bytes);
            SyntaxTree tree = CSharpSyntaxTree.ParseText(text, parseOptions, identity.Path);
            if (tree.GetDiagnostics().Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            {
                throw new AdmissionException($"Source syntax error: {identity.Path}.");
            }
            if (!compilationOnly && identity.Role == "semantic" && tree.GetRoot().DescendantTrivia(descendIntoTrivia: true)
                    .Any(static trivia => trivia.IsDirective || trivia.IsKind(SyntaxKind.DisabledTextTrivia)))
            {
                throw new AdmissionException($"Semantic source contains a directive or disabled text: {identity.Path}.");
            }
            sourceByTree.Add(tree, identity);
        }

        CSharpCompilation compilation = CSharpCompilation.Create("Nethermind.Evm", sourceByTree.Keys, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release, allowUnsafe: true,
                checkOverflow: false, nullableContextOptions: NullableContextOptions.Enable,
                metadataImportOptions: MetadataImportOptions.All));
        RequireCompiles(compilation);
        if (compilationOnly) return null;
        RequireStandardLineage(compilation);

        List<AdmittedMember> members = [];
        foreach ((string typeName, string[] names) in OwnedMethods)
        {
            INamedTypeSymbol type = RequireType(compilation, typeName);
            foreach (string name in names)
            {
                IMethodSymbol[] methods = type.GetMembers(name).OfType<IMethodSymbol>().ToArray();
                int expectedCount = typeName == PolicyType && name is "RefundStateGas" or "ApplyStateGasTransition" ? 2 : 1;
                if (methods.Length != expectedCount)
                {
                    throw new AdmissionException($"Owned method overload roster changed: {typeName}.{name}.");
                }
                foreach (IMethodSymbol method in methods.OrderBy(OperationLowering.Symbol, StringComparer.Ordinal))
                {
                    RequireSynchronous(method);
                    SyntaxNode declaration = method.DeclaringSyntaxReferences.Single().GetSyntax();
                    if (declaration is not MethodDeclarationSyntax syntax || !sourceByTree.TryGetValue(syntax.SyntaxTree, out SourceIdentity? source) || source.Role != "semantic")
                    {
                        throw new AdmissionException($"Owned method is not in its admitted semantic source: {method}.");
                    }
                    if (syntax.DescendantNodes().Any(static node => node is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax or YieldStatementSyntax))
                    {
                        throw new AdmissionException($"Owned method contains an extra lexical callable or iterator: {method}.");
                    }
                    SemanticModel model = compilation.GetSemanticModel(syntax.SyntaxTree);
                    if (!SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(syntax), method))
                    {
                        throw new AdmissionException("Owned declaration identity mismatch.");
                    }
                    IOperation body = model.GetOperation(syntax) ?? throw new AdmissionException($"Missing typed method body: {method}.");
                    MemberIdentity memberIdentity = new(OperationLowering.Symbol(method), method.MethodKind.ToString(), OperationLowering.Symbol(method.ReturnType),
                        method.Parameters.Select(static parameter => new ParameterIdentity(parameter.Name, parameter.Ordinal, OperationLowering.Symbol(parameter.Type), parameter.RefKind.ToString())).ToArray(),
                        source.Path, syntax.SpanStart, syntax.Span.Length, CompilerReferences.Hash(Encoding.UTF8.GetBytes(Canonical(syntax))));
                    members.Add(new(memberIdentity, OperationLowering.Lower(body)));
                }
            }
        }

        RequirePolicyDispatch(compilation);
        RequireReviewedSyntax(root, sourceByTree);
        RefundConstants constants = ReadConstants(compilation, root);
        return new(new("Nethermind.Evm", "14.0", pins.DefineConstants, CompilerReferences.InventorySha256, identities, pins.Sources,
            members.OrderBy(static member => member.Identity.Symbol, StringComparer.Ordinal).ToArray()), constants, RefundPlanBuilder.Build(compilation));
    }

    private static void RequireReviewedSyntax(string root, IReadOnlyDictionary<SyntaxTree, SourceIdentity> sources)
    {
        const string path = PackagePath + "/Admission/REVIEWED_SYNTAX.json";
        byte[] bytes = File.ReadAllBytes(CompilerReferences.Within(root, path));
        CompilerReferences.RequireHash(bytes, ReviewedSyntaxSha256, path);
        ReviewedSyntax reviewed = JsonSerializer.Deserialize<ReviewedSyntax>(bytes, CompilerReferences.JsonOptions)
            ?? throw new AdmissionException("Missing reviewed syntax closure.");
        if (reviewed.SchemaVersion != 1 || reviewed.Sources.Length != 146 ||
            !reviewed.Sources.Select(static identity => identity.Path).SequenceEqual(sources.Values.Select(static identity => identity.Path), StringComparer.Ordinal))
        {
            throw new AdmissionException("Reviewed source/support syntax roster changed.");
        }
        foreach ((SyntaxTree tree, SourceIdentity identity) in sources)
        {
            SyntaxNode syntax = tree.GetRoot();
            string directives = string.Join('\n', syntax.DescendantTrivia(descendIntoTrivia: true)
                .Where(static trivia => trivia.IsDirective || trivia.IsKind(SyntaxKind.DisabledTextTrivia))
                .Select(static trivia => trivia.ToFullString()));
            string digest = CompilerReferences.Hash(Encoding.UTF8.GetBytes(Canonical(syntax) + "\n" + directives));
            if (digest != reviewed.Sources.Single(source => source.Path == identity.Path).Sha256)
            {
                throw new AdmissionException("Unadmitted complete source/support syntax closure: " + identity.Path + ".");
            }
        }
    }

    internal static void RequireCompiles(CSharpCompilation compilation)
    {
        Diagnostic[] errors = compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0)
        {
            throw new AdmissionException("The selected production source closure does not compile: " + string.Join("; ", errors.Take(30).Select(static error => error.ToString())));
        }
    }

    internal static void RequireSynchronous(IMethodSymbol method)
    {
        if (method.MethodKind != MethodKind.Ordinary || method.IsAsync || method.IsExtern || method.IsAbstract ||
            method.DeclaringSyntaxReferences.Length != 1 || method.PartialDefinitionPart is not null || method.PartialImplementationPart is not null ||
            method.ExplicitInterfaceImplementations.Length != 0 || method.ReturnsByRef || method.ReturnsByRefReadonly)
        {
            throw new AdmissionException($"Owned method is not an ordinary synchronous source body: {method}.");
        }
        for (IMethodSymbol? current = method; current is not null; current = current.OverriddenMethod)
        {
            if (current.GetAttributes().Any(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Diagnostics.ConditionalAttribute"))
            {
                throw new AdmissionException($"Owned method has direct or inherited ConditionalAttribute: {method}.");
            }
        }
        if (method.DeclaringSyntaxReferences[0].GetSyntax() is not MethodDeclarationSyntax syntax || syntax.Body is null && syntax.ExpressionBody is null)
        {
            throw new AdmissionException($"Owned method has no executable body: {method}.");
        }
    }

    private static void RequireStandardLineage(CSharpCompilation compilation)
    {
        INamedTypeSymbol processor = RequireType(compilation, "Nethermind.Evm.TransactionProcessing.EthereumTransactionProcessor");
        INamedTypeSymbol wrapper = RequireType(compilation, "Nethermind.Evm.TransactionProcessing.EthereumTransactionProcessorBase");
        INamedTypeSymbol generic = RequireType(compilation, ProcessorType);
        INamedTypeSymbol policy = RequireType(compilation, PolicyType);
        if (!processor.IsSealed || !SymbolEqualityComparer.Default.Equals(processor.BaseType, wrapper) ||
            wrapper.BaseType is not { } constructed || !SymbolEqualityComparer.Default.Equals(constructed.OriginalDefinition, generic) ||
            constructed.TypeArguments.Length != 1 || !SymbolEqualityComparer.Default.Equals(constructed.TypeArguments[0], policy))
        {
            throw new AdmissionException("The standard Ethereum transaction processor lineage changed.");
        }
        foreach (INamedTypeSymbol derived in new[] { processor, wrapper })
        {
            if (derived.GetMembers().OfType<IMethodSymbol>().Any(method => OwnedMethods[ProcessorType].Contains(method.Name, StringComparer.Ordinal)))
            {
                throw new AdmissionException("The standard processor lineage shadows an owned refund helper.");
            }
        }
    }

    private static void RequirePolicyDispatch(CSharpCompilation compilation)
    {
        INamedTypeSymbol policy = RequireType(compilation, PolicyType);
        INamedTypeSymbol contract = RequireType(compilation, "Nethermind.Evm.GasPolicy.IGasPolicy`1").Construct(policy);
        foreach (string inherited in new[] { "GetPreRefundGas", "GetCreateStateCost" })
        {
            if (policy.GetMembers(inherited).Length != 0 || contract.GetMembers(inherited).OfType<IMethodSymbol>().Single().IsAbstract)
            {
                throw new AdmissionException($"The inherited Ethereum gas-policy default was rebound: {inherited}.");
            }
        }
        foreach (string methodName in new[] { "ClearExecutionGas", "GetRemainingGas", "GetStateReservoir", "GetStateGasUsed", "RefundStateGas", "ResetForHalt", "ApplyCodeInsertRefunds" })
        {
            foreach (IMethodSymbol member in contract.GetMembers(methodName).OfType<IMethodSymbol>())
            {
                ISymbol? implementation = policy.FindImplementationForInterfaceMember(member);
                if (implementation is not IMethodSymbol method || !SymbolEqualityComparer.Default.Equals(method.ContainingType, policy) || method.DeclaringSyntaxReferences.Length != 1)
                {
                    throw new AdmissionException($"Ethereum static-interface dispatch is not source-owned: {member}.");
                }
            }
        }
        string[] fields = policy.GetMembers().OfType<IFieldSymbol>().Where(static field => !field.IsStatic).Select(static field => field.Name).Order(StringComparer.Ordinal).ToArray();
        if (!fields.SequenceEqual(new[] { "StateGasSpill", "StateGasSpillRefunded", "StateGasUsed", "StateReservoir", "Value" }, StringComparer.Ordinal))
        {
            throw new AdmissionException("The five-field Ethereum gas-policy representation changed.");
        }
    }

    private static RefundConstants ReadConstants(CSharpCompilation compilation, string root)
    {
        INamedTypeSymbol gasCosts = RequireType(compilation, "Nethermind.Core.GasCostOf");
        INamedTypeSymbol refund = RequireType(compilation, "Nethermind.Evm.RefundHelper");
        ulong newAccount = (ulong)((IFieldSymbol)gasCosts.GetMembers("NewAccount").Single()).ConstantValue!;
        ulong perAuthorization = (ulong)((IFieldSymbol)gasCosts.GetMembers("PerAuthBaseCost").Single()).ConstantValue!;
        long create = (long)((IFieldSymbol)gasCosts.GetMembers("CreateState").Single()).ConstantValue!;
        ulong legacy = (ulong)((IFieldSymbol)refund.GetMembers("MaxRefundQuotient").Single()).ConstantValue!;
        ulong eip3529 = (ulong)((IFieldSymbol)refund.GetMembers("MaxRefundQuotientEIP3529").Single()).ConstantValue!;
        SyntaxNode constants = CSharpSyntaxTree.ParseText(File.ReadAllText(CompilerReferences.Within(root, "src/Nethermind/Nethermind.Core/Eip7825Constants.cs"))).GetRoot();
        VariableDeclaratorSyntax cap = constants.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single(static variable => variable.Identifier.ValueText == "DefaultTxGasLimitCap");
        if (cap.Initializer?.Value is not LiteralExpressionSyntax { Token.Value: ulong or int or long } literal)
        {
            throw new AdmissionException("The execution cap is not the admitted literal initializer.");
        }
        ulong executionCap = Convert.ToUInt64(literal.Token.Value, System.Globalization.CultureInfo.InvariantCulture);
        if (newAccount != 25_000 || perAuthorization != 12_500 || create != 183_600 || executionCap != 16_777_216 || legacy != 2 || eip3529 != 5)
        {
            throw new AdmissionException("A refund arithmetic constant changed.");
        }
        return new(executionCap, create, newAccount, perAuthorization, legacy, eip3529);
    }

    private static INamedTypeSymbol RequireType(CSharpCompilation compilation, string name) =>
        compilation.GetTypeByMetadataName(name) ?? throw new AdmissionException($"Missing or ambiguous admitted type: {name}.");

    private static string Canonical(SyntaxNode syntax) => string.Join(" ", syntax.DescendantTokens().Select(static token => token.Text));
}
