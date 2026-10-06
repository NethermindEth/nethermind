// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Evm.Lean.SimpleTransferCompletionExtractor;

internal sealed record StageBRefundDispatchReceiver(
    string Kind,
    string RuntimeType,
    string[] Lineage,
    bool IsSealed,
    bool DeclaresRefund,
    bool DeclaresPayRefund,
    string RefundDeclaringType,
    string PayRefundDeclaringType);

internal sealed record StageBRefundDispatchCall(
    string Role,
    string Caller,
    string Callee,
    string Receiver,
    string SourcePath,
    int SpanStart,
    int SpanLength,
    string SyntaxSha256);

internal sealed record StageBRefundDispatchUpstream(
    string Name,
    StageBArtifactFile Manifest,
    StageBArtifactFile[] Artifacts);

internal sealed record StageBRefundDispatchDocument(
    int SchemaVersion,
    string ArtifactKind,
    string GasPolicy,
    string BaseProcessor,
    StageBRefundDispatchReceiver[] Receivers,
    StageBRefundDispatchCall[] Calls,
    StageBRefundDispatchUpstream[] Upstreams,
    string[] Exclusions);

internal sealed record StageBRefundDispatchManifest(
    int SchemaVersion,
    string ArtifactKind,
    string Configuration,
    string SourceClosureSha256,
    string ReferenceClosureSha256,
    SourceIdentity[] Sources,
    ReferenceIdentity[] References,
    StageBRefundDispatchUpstream[] Upstreams,
    StageBArtifactFile Ir,
    StageBArtifactFile Lean);

internal static class StageBRefundDispatchExtractor
{
    private static readonly JsonSerializerOptions LeanStringOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal const string PackagePath = "tools/Evm/Lean/SimpleTransferCompletionExtractor";
    internal const string ProductionPath = "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs";
    internal const string ArtifactKind = "stage-b-standard-mainnet-refund-dispatch";
    internal const string IrName = "StandardMainnetRefundDispatch.ir.json";
    internal const string LeanName = "StandardMainnetRefundDispatch.lean";
    internal const string ManifestName = "StandardMainnetRefundDispatch.source-manifest.json";

    private const string PolicyMetadataName = "Nethermind.Evm.GasPolicy.EthereumGasPolicy";
    private const string BaseMetadataName = "Nethermind.Evm.TransactionProcessing.TransactionProcessorBase`1";
    private const string GenericLeafMetadataName = "Nethermind.Evm.TransactionProcessing.TransactionProcessor`1";
    private const string EthereumLeafMetadataName = "Nethermind.Evm.TransactionProcessing.EthereumTransactionProcessor";
    private const string EthereumWrapperMetadataName = "Nethermind.Evm.TransactionProcessing.EthereumTransactionProcessorBase";
    private const string BaseDisplay = "global::Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<global::Nethermind.Evm.GasPolicy.EthereumGasPolicy>";
    private const string PrefixManifestPath = PackagePath + "/StageB/Generated/PrefixProgram.source-manifest.json";
    private const string RefundManifestPath = "tools/Evm/Lean/OrdinaryTransactionRefundAdapterExtractor/Generated/OrdinaryTransactionRefund.source-manifest.json";
    private const string PrefixManifestSha256 = "d7eb0fedb1ed7f97e18ba030325441e05a8b27d06158aef003808a497b1030ee";
    private const string RefundManifestSha256 = "a0dd398a94994b65c3ea6bc59b6d62cd77958025208c423237e14a44d1ce30a1";

    internal static StageBRefundDispatchDocument Generate(string root, CompilerClosure closure)
    {
        CSharpCompilation compilation = closure.Compilation;
        RequireCompiles(compilation);
        INamedTypeSymbol policy = Type(compilation, PolicyMetadataName);
        INamedTypeSymbol genericBase = Type(compilation, BaseMetadataName);
        INamedTypeSymbol constructedBase = genericBase.Construct(policy);
        INamedTypeSymbol genericLeaf = Type(compilation, GenericLeafMetadataName);
        INamedTypeSymbol constructedGenericLeaf = genericLeaf.Construct(policy);
        INamedTypeSymbol ethereumLeaf = Type(compilation, EthereumLeafMetadataName);
        INamedTypeSymbol ethereumWrapper = Type(compilation, EthereumWrapperMetadataName);

        if (!genericLeaf.IsSealed || !ethereumLeaf.IsSealed || !ethereumWrapper.IsAbstract ||
            !SymbolEqualityComparer.Default.Equals(constructedGenericLeaf.BaseType, constructedBase) ||
            !SymbolEqualityComparer.Default.Equals(ethereumLeaf.BaseType, ethereumWrapper) ||
            !SymbolEqualityComparer.Default.Equals(ethereumWrapper.BaseType, constructedBase))
        {
            throw new ExtractionException("The two standard Ethereum transaction-processor lineages changed.");
        }

        IMethodSymbol refund = One(genericBase.GetMembers("Refund").OfType<IMethodSymbol>(), "Base Refund overload roster changed.");
        IMethodSymbol payRefund = One(genericBase.GetMembers("PayRefund").OfType<IMethodSymbol>(), "Base PayRefund overload roster changed.");
        RequireBaseVirtual(refund, "Refund", 12);
        RequireBaseVirtual(payRefund, "PayRefund", 3);

        StageBRefundDispatchReceiver ethereum = Receiver(
            "ethereumTransactionProcessor",
            ethereumLeaf,
            [ethereumLeaf, ethereumWrapper, constructedBase],
            refund,
            payRefund);
        StageBRefundDispatchReceiver bal = Receiver(
            "balTransactionProcessor",
            constructedGenericLeaf,
            [constructedGenericLeaf, constructedBase],
            refund,
            payRefund);

        IMethodSymbol executeSimpleTransfer = One(genericBase.GetMembers("ExecuteSimpleTransfer").OfType<IMethodSymbol>(),
            "ExecuteSimpleTransfer overload roster changed.");
        StageBRefundDispatchCall refundCall = ContainingInstanceCall(compilation, executeSimpleTransfer, refund, "executeSimpleTransferRefund");
        StageBRefundDispatchCall payRefundCall = ContainingInstanceCall(compilation, refund, payRefund, "refundPayRefund");
        StageBRefundDispatchUpstream[] upstreams = ReadUpstreams(root);
        return new(1, ArtifactKind, "global::Nethermind.Evm.GasPolicy.EthereumGasPolicy", BaseDisplay,
            [ethereum, bal], [refundCall, payRefundCall], upstreams,
            [
                "autofac-resolution",
                "plugin-module-selection",
                "host-execution",
                "refund-return",
                "caller-result-assignment",
                "pay-refund-world-state-effect",
                "stage-b-resume",
                "system-transaction-processor",
                "optimism-transaction-processor",
                "taiko-transaction-processor",
            ]);
    }

    private static StageBRefundDispatchReceiver Receiver(
        string kind,
        INamedTypeSymbol runtimeType,
        INamedTypeSymbol[] lineage,
        IMethodSymbol refund,
        IMethodSymbol payRefund)
    {
        bool declaresRefund = lineage[..^1].Any(static type => type.GetMembers("Refund").OfType<IMethodSymbol>().Any());
        bool declaresPayRefund = lineage[..^1].Any(static type => type.GetMembers("PayRefund").OfType<IMethodSymbol>().Any());
        if (declaresRefund || declaresPayRefund)
            throw new ExtractionException("A standard transaction-processor leaf shadows Refund or PayRefund: " + Display(runtimeType) + ".");
        IMethodSymbol resolvedRefund = ResolveVirtual(runtimeType, "Refund");
        IMethodSymbol resolvedPayRefund = ResolveVirtual(runtimeType, "PayRefund");
        if (!SymbolEqualityComparer.Default.Equals(resolvedRefund.OriginalDefinition, refund.OriginalDefinition) ||
            !SymbolEqualityComparer.Default.Equals(resolvedPayRefund.OriginalDefinition, payRefund.OriginalDefinition))
        {
            throw new ExtractionException("A standard transaction-processor leaf resolves a refund slot outside the admitted base.");
        }
        return new(kind, Display(runtimeType), lineage.Select(Display).ToArray(), runtimeType.IsSealed,
            declaresRefund, declaresPayRefund, Display(resolvedRefund.ContainingType), Display(resolvedPayRefund.ContainingType));
    }

    private static StageBRefundDispatchCall ContainingInstanceCall(
        CSharpCompilation compilation,
        IMethodSymbol caller,
        IMethodSymbol callee,
        string role)
    {
        MethodDeclarationSyntax declaration = Declaration(caller) as MethodDeclarationSyntax
            ?? throw new ExtractionException("Refund dispatch caller is not a method: " + caller.Name + ".");
        SemanticModel model = compilation.GetSemanticModel(declaration.SyntaxTree);
        IInvocationOperation[] calls = declaration.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(node => model.GetOperation(node)).OfType<IInvocationOperation>()
            .Where(call => SymbolEqualityComparer.Default.Equals(call.TargetMethod.OriginalDefinition, callee.OriginalDefinition))
            .ToArray();
        IInvocationOperation call = One(calls, caller.Name + " must contain exactly one " + callee.Name + " call.");
        if (call.Instance is not IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance })
            throw new ExtractionException(role + " must use the containing instance receiver.");
        InvocationExpressionSyntax syntax = (InvocationExpressionSyntax)call.Syntax;
        string sourcePath = syntax.SyntaxTree.FilePath.Replace('\\', '/');
        return new(role, MethodIdentity(caller), MethodIdentity(callee), "containingInstance", sourcePath,
            syntax.SpanStart, syntax.Span.Length, HashCanonical(syntax));
    }

    private static void RequireBaseVirtual(IMethodSymbol method, string name, int parameterCount)
    {
        if (method.Name != name || method.Parameters.Length != parameterCount || method.IsStatic || !method.IsVirtual || method.IsOverride ||
            method.DeclaredAccessibility != Accessibility.Protected || method.DeclaringSyntaxReferences.Length != 1)
        {
            throw new ExtractionException("The base " + name + " virtual slot changed.");
        }
    }

    private static IMethodSymbol ResolveVirtual(INamedTypeSymbol runtimeType, string name)
    {
        for (INamedTypeSymbol? current = runtimeType; current is not null; current = current.BaseType)
        {
            IMethodSymbol[] methods = current.GetMembers(name).OfType<IMethodSymbol>().Where(static method => !method.IsStatic).ToArray();
            if (methods.Length != 0) return One(methods, "Virtual slot overload roster changed: " + name + ".");
        }
        throw new ExtractionException("Virtual slot disappeared: " + name + ".");
    }

    internal static Dictionary<string, byte[]> RenderArtifacts(string root, CompilerClosure closure)
    {
        StageBRefundDispatchDocument document = Generate(root, closure);
        byte[] ir = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, CompilerReferences.JsonOptions) + "\n");
        byte[] lean = Encoding.UTF8.GetBytes(Emit(document, ClosureHash(closure.EffectiveSources), ClosureHash(closure.References)));
        StageBRefundDispatchManifest manifest = new(1, ArtifactKind, "Release",
            ClosureHash(closure.EffectiveSources), ClosureHash(closure.References),
            closure.EffectiveSources, closure.References, document.Upstreams,
            new(IrName, CompilerReferences.Hash(ir)), new(LeanName, CompilerReferences.Hash(lean)));
        byte[] metadata = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, CompilerReferences.JsonOptions) + "\n");
        return new(StringComparer.Ordinal) { [IrName] = ir, [LeanName] = lean, [ManifestName] = metadata };
    }

    internal static void Extract(string root, string output, bool check)
    {
        Dictionary<string, byte[]> artifacts = RenderArtifacts(root, CompilerSources.Load(root));
        if (check)
        {
            ValidateDirectory(output, artifacts);
            return;
        }
        Directory.CreateDirectory(output);
        foreach ((string name, byte[] bytes) in artifacts) File.WriteAllBytes(Path.Combine(output, name), bytes);
    }

    internal static void ValidateDirectory(string output, IReadOnlyDictionary<string, byte[]> fresh)
    {
        Dictionary<string, byte[]> supplied = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(output, path).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal);
        if (!supplied.Keys.Order(StringComparer.Ordinal).SequenceEqual(fresh.Keys.Order(StringComparer.Ordinal)))
            throw new ExtractionException("Refund-dispatch artifact roster changed.");
        foreach ((string name, byte[] bytes) in fresh)
            if (!supplied[name].AsSpan().SequenceEqual(bytes))
                throw new ExtractionException("Refund-dispatch artifact is not fresh/canonical: " + name + ".");
    }

    internal static string Emit(StageBRefundDispatchDocument document, string sourceClosureSha256, string referenceClosureSha256)
    {
        StageBRefundDispatchReceiver ethereum = document.Receivers.Single(static receiver => receiver.Kind == "ethereumTransactionProcessor");
        StageBRefundDispatchReceiver bal = document.Receivers.Single(static receiver => receiver.Kind == "balTransactionProcessor");
        StageBRefundDispatchCall refund = document.Calls.Single(static call => call.Role == "executeSimpleTransferRefund");
        StageBRefundDispatchCall payRefund = document.Calls.Single(static call => call.Role == "refundPayRefund");
        StageBRefundDispatchUpstream prefix = document.Upstreams.Single(static upstream => upstream.Name == "stageBPrefix");
        StageBRefundDispatchUpstream ordinaryRefund = document.Upstreams.Single(static upstream => upstream.Name == "ordinaryRefund");
        StringBuilder lean = new();
        lean.AppendLine("-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited");
        lean.AppendLine("-- SPDX-License-Identifier: LGPL-3.0-only");
        lean.AppendLine("-- Generated from the admitted standard-mainnet receiver and call-site closure. Do not edit.");
        lean.AppendLine();
        lean.AppendLine("namespace SimpleTransferCompletionExtractor.StageB.Dispatch.Generated");
        lean.AppendLine();
        lean.AppendLine("inductive ReceiverKind where");
        lean.AppendLine("  | ethereumTransactionProcessor");
        lean.AppendLine("  | balTransactionProcessor");
        lean.AppendLine("  deriving DecidableEq, Repr");
        lean.AppendLine();
        lean.AppendLine("inductive MethodKind where");
        lean.AppendLine("  | refund");
        lean.AppendLine("  | payRefund");
        lean.AppendLine("  deriving DecidableEq, Repr");
        lean.AppendLine();
        lean.AppendLine("structure ReceiverEvidence where");
        lean.AppendLine("  runtimeType : String");
        lean.AppendLine("  lineage : List String");
        lean.AppendLine("  isSealed : Bool");
        lean.AppendLine("  declaresRefund : Bool");
        lean.AppendLine("  declaresPayRefund : Bool");
        lean.AppendLine("  deriving DecidableEq, Repr");
        lean.AppendLine();
        lean.AppendLine("def baseProcessor : String := " + Quote(document.BaseProcessor));
        lean.AppendLine("def gasPolicy : String := " + Quote(document.GasPolicy));
        lean.AppendLine("def sourceClosureSha256 : String := " + Quote(sourceClosureSha256));
        lean.AppendLine("def referenceClosureSha256 : String := " + Quote(referenceClosureSha256));
        lean.AppendLine("def prefixManifestSha256 : String := " + Quote(prefix.Manifest.Sha256));
        lean.AppendLine("def ordinaryRefundManifestSha256 : String := " + Quote(ordinaryRefund.Manifest.Sha256));
        lean.AppendLine();
        lean.AppendLine("def receiverEvidence : ReceiverKind → ReceiverEvidence");
        lean.AppendLine("  | .ethereumTransactionProcessor => " + ReceiverLiteral(ethereum));
        lean.AppendLine("  | .balTransactionProcessor => " + ReceiverLiteral(bal));
        lean.AppendLine();
        lean.AppendLine("def resolve (_receiver : ReceiverKind) (_method : MethodKind) : String := baseProcessor");
        lean.AppendLine();
        lean.AppendLine("structure CallEvidence where");
        lean.AppendLine("  caller : String");
        lean.AppendLine("  callee : String");
        lean.AppendLine("  receiver : String");
        lean.AppendLine("  sourcePath : String");
        lean.AppendLine("  syntaxSha256 : String");
        lean.AppendLine("  deriving DecidableEq, Repr");
        lean.AppendLine();
        lean.AppendLine("def executeSimpleTransferRefundCall : CallEvidence := " + CallLiteral(refund));
        lean.AppendLine("def refundPayRefundCall : CallEvidence := " + CallLiteral(payRefund));
        lean.AppendLine();
        lean.AppendLine("end SimpleTransferCompletionExtractor.StageB.Dispatch.Generated");
        return lean.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string ReceiverLiteral(StageBRefundDispatchReceiver receiver) =>
        "{ runtimeType := " + Quote(receiver.RuntimeType) + ", lineage := [" +
        string.Join(", ", receiver.Lineage.Select(Quote)) + "], isSealed := " + LeanBool(receiver.IsSealed) +
        ", declaresRefund := " + LeanBool(receiver.DeclaresRefund) + ", declaresPayRefund := " + LeanBool(receiver.DeclaresPayRefund) + " }";

    private static string CallLiteral(StageBRefundDispatchCall call) =>
        "{ caller := " + Quote(call.Caller) + ", callee := " + Quote(call.Callee) +
        ", receiver := " + Quote(call.Receiver) + ", sourcePath := " + Quote(call.SourcePath) +
        ", syntaxSha256 := " + Quote(call.SyntaxSha256) + " }";

    private static StageBRefundDispatchUpstream[] ReadUpstreams(string root) =>
    [
        ReadPrefixUpstream(root),
        ReadOrdinaryRefundUpstream(root),
    ];

    private static StageBRefundDispatchUpstream ReadPrefixUpstream(string root)
    {
        string path = CompilerReferences.Within(root, PrefixManifestPath);
        byte[] bytes = File.ReadAllBytes(path);
        CompilerReferences.RequireHash(bytes, PrefixManifestSha256, PrefixManifestPath);
        using JsonDocument json = JsonDocument.Parse(bytes);
        JsonElement document = json.RootElement;
        if (document.EnumerateObject().Select(static property => property.Name).ToArray() is not
            ["schemaVersion", "artifactKind", "prefixIntegrity", "ir", "lean", "syntax"] ||
            document.GetProperty("schemaVersion").GetInt32() != 1 ||
            document.GetProperty("artifactKind").GetString() != "stage-b-prefix-program-data" ||
            document.GetProperty("prefixIntegrity").GetString() != StageBArtifact.ExpectedPrefixIntegrity)
            throw new ExtractionException("The accepted Stage-B prefix manifest shape changed.");
        string directory = Path.GetDirectoryName(PrefixManifestPath)!.Replace('\\', '/');
        StageBArtifactFile ir = ManifestArtifact(root, directory, document.GetProperty("ir"));
        StageBArtifactFile lean = ManifestArtifact(root, directory, document.GetProperty("lean"));
        StageBArtifactFile syntax = ManifestArtifact(root, "", document.GetProperty("syntax"));
        return new("stageBPrefix", new(PrefixManifestPath, PrefixManifestSha256), [ir, lean, syntax]);
    }

    private static StageBRefundDispatchUpstream ReadOrdinaryRefundUpstream(string root)
    {
        string path = CompilerReferences.Within(root, RefundManifestPath);
        byte[] bytes = File.ReadAllBytes(path);
        CompilerReferences.RequireHash(bytes, RefundManifestSha256, RefundManifestPath);
        using JsonDocument json = JsonDocument.Parse(bytes);
        JsonElement document = json.RootElement;
        if (document.GetProperty("schemaVersion").GetInt32() != 1 ||
            document.GetProperty("extractorVersion").GetString() != "1.0.0" ||
            document.GetProperty("acceptanceState").GetString() != "source-admitted" ||
            document.GetProperty("sourceClosureSha256").GetString() != "8e753ccf7f150729d60d0fa31c7b3720865057cd556084d405c3dba1cb283734" ||
            document.GetProperty("compilerInventorySha256").GetString() != CompilerReferences.InventorySha256 ||
            document.GetProperty("sources").GetArrayLength() != 157 ||
            document.GetProperty("dependencies").GetArrayLength() != 52)
            throw new ExtractionException("The accepted ordinary-refund manifest shape changed.");
        string directory = Path.GetDirectoryName(RefundManifestPath)!.Replace('\\', '/');
        StageBArtifactFile ir = ManifestArtifact(root, directory, document.GetProperty("ir"));
        StageBArtifactFile lean = ManifestArtifact(root, directory, document.GetProperty("lean"));
        return new("ordinaryRefund", new(RefundManifestPath, RefundManifestSha256), [ir, lean]);
    }

    private static StageBArtifactFile ManifestArtifact(string root, string directory, JsonElement element)
    {
        if (element.EnumerateObject().Select(static property => property.Name).ToArray() is not ["path", "sha256"])
            throw new ExtractionException("An upstream artifact identity changed shape.");
        string relative = element.GetProperty("path").GetString() ?? throw new ExtractionException("An upstream artifact path is null.");
        string expected = element.GetProperty("sha256").GetString() ?? throw new ExtractionException("An upstream artifact hash is null.");
        string rooted = string.IsNullOrEmpty(directory) ? relative : directory + "/" + relative;
        byte[] bytes = File.ReadAllBytes(CompilerReferences.Within(root, rooted));
        CompilerReferences.RequireHash(bytes, expected, rooted);
        return new(rooted, expected);
    }

    private static string ClosureHash(IEnumerable<SourceIdentity> identities) => CompilerReferences.Hash(Encoding.UTF8.GetBytes(
        string.Join('\n', identities.Select(static identity => identity.Path + "\t" + identity.Role + "\t" + identity.Sha256)) + "\n"));

    private static string ClosureHash(IEnumerable<ReferenceIdentity> identities) => CompilerReferences.Hash(Encoding.UTF8.GetBytes(
        string.Join('\n', identities.Select(static identity => identity.Path + "\t" + identity.AssemblyName + "\t" + identity.Sha256 + "\t" + identity.Mvid)) + "\n"));

    private static string HashCanonical(SyntaxNode syntax) => CompilerReferences.Hash(Encoding.UTF8.GetBytes(
        string.Join(" ", syntax.DescendantTokens().Select(static token => token.Text))));

    private static string MethodIdentity(IMethodSymbol method) => Display(method.ContainingType) + "." + method.Name + "/" + method.Parameters.Length;
    private static string Display(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    private static string Quote(string value) => JsonSerializer.Serialize(value, LeanStringOptions);
    private static string LeanBool(bool value) => value ? "true" : "false";

    private static INamedTypeSymbol Type(CSharpCompilation compilation, string metadataName) =>
        compilation.GetTypeByMetadataName(metadataName) ?? throw new ExtractionException("Missing or ambiguous production type: " + metadataName + ".");

    private static SyntaxNode Declaration(ISymbol symbol) =>
        One(symbol.DeclaringSyntaxReferences, "Production declaration cardinality changed: " + symbol.Name + ".").GetSyntax();

    private static T One<T>(IEnumerable<T> candidates, string message)
    {
        T[] values = candidates.Take(2).ToArray();
        return values.Length == 1 ? values[0] : throw new ExtractionException(message);
    }

    private static void RequireCompiles(CSharpCompilation compilation)
    {
        Diagnostic[] errors = compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0)
            throw new ExtractionException("The refund-dispatch production closure does not compile: " +
                string.Join("; ", errors.Take(30).Select(static error => error.ToString())));
    }
}
