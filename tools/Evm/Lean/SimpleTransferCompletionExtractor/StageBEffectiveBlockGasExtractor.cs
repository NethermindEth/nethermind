// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.SimpleTransferCompletionExtractor;

internal static class StageBEffectiveBlockGasExtractor
{
    internal const string ExtractorPath = "tools/Evm/Lean/SimpleTransferCompletionExtractor/StageBEffectiveBlockGasExtractor.cs";
    internal const string IrName = "EffectiveBlockGas.ir.json";
    internal const string LeanName = "EffectiveBlockGas.lean";
    internal const string ManifestName = "EffectiveBlockGas.source-manifest.json";
    private const string Expression = "BlockGas>0||BlockStateGas>0?BlockGas:SpentGas";

    internal static Dictionary<string, byte[]> RenderArtifacts(string root, CompilerClosure closure)
    {
        INamedTypeSymbol type = closure.Compilation.GetTypeByMetadataName("Nethermind.Evm.TransactionProcessing.GasConsumed")
            ?? throw new ExtractionException("The GasConsumed type is missing or ambiguous.");
        if (!type.IsRecord || !type.IsReadOnly || type.TypeKind != TypeKind.Struct)
            throw new ExtractionException("The GasConsumed representation changed.");
        IPropertySymbol[] candidates = type.GetMembers("EffectiveBlockGas").OfType<IPropertySymbol>().ToArray();
        if (candidates is not [IPropertySymbol property] || property.IsStatic || property.IsVirtual ||
            property.Type.SpecialType != SpecialType.System_UInt64 || property.SetMethod is not null ||
            property.DeclaredAccessibility != Accessibility.Public || property.DeclaringSyntaxReferences.Length != 1 ||
            property.DeclaringSyntaxReferences[0].GetSyntax() is not PropertyDeclarationSyntax
            { ExpressionBody.Expression: { } expression, AccessorList: null } syntax ||
            syntax.SyntaxTree.FilePath != Extractor.GasConsumedPath ||
            string.Concat(expression.DescendantTokens().Select(static token => token.Text)) != Expression)
            throw new ExtractionException("The EffectiveBlockGas getter shape changed.");

        SemanticModel model = closure.Compilation.GetSemanticModel(syntax.SyntaxTree);
        if (model.GetTypeInfo(expression).Type?.SpecialType != SpecialType.System_UInt64)
            throw new ExtractionException("The EffectiveBlockGas expression type changed.");
        foreach (IdentifierNameSyntax identifier in expression.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (model.GetSymbolInfo(identifier).Symbol is not IPropertySymbol referenced ||
                !SymbolEqualityComparer.Default.Equals(referenced.ContainingType, type) || referenced.IsStatic ||
                referenced.Type.SpecialType != SpecialType.System_UInt64 || referenced.DeclaringSyntaxReferences.Length != 1 ||
                referenced.DeclaringSyntaxReferences[0].GetSyntax() is not ParameterSyntax ||
                referenced.Name is not ("BlockGas" or "BlockStateGas" or "SpentGas"))
                throw new ExtractionException("An EffectiveBlockGas operand no longer names the positional UInt64 property.");
        }

        SourceIdentity source = closure.EffectiveSources.Single(item => item.Path == Extractor.GasConsumedPath);
        byte[] ir = Json(new
        {
            schemaVersion = 1,
            artifactKind = "stage-b-effective-block-gas-source-admitted-leaf",
            symbol = "global::Nethermind.Evm.TransactionProcessing.GasConsumed.EffectiveBlockGas",
            source,
            expression = Expression,
            operands = new[] { "BlockGas", "BlockStateGas", "BlockGas", "SpentGas" },
            operandType = "System.UInt64",
            exclusions = new[] { "reference-heap", "property-read-clr-bridge", "stage-a-completion-bundle" },
        });
        byte[] lean = Encoding.UTF8.GetBytes("""
            -- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
            -- SPDX-License-Identifier: LGPL-3.0-only
            -- Generated from the source-admitted positional UInt64 getter. Do not edit.

            namespace SimpleTransferCompletionExtractor.StageB.Leaf.Generated

            structure GasConsumed where
              spentGas : Nat
              operationGas : Nat
              blockGas : Nat
              blockStateGas : Nat
              maxUsedGas : Nat
              gasRefund : Nat
              deriving DecidableEq, Repr

            def effectiveBlockGas (gas : GasConsumed) : Nat :=
              if gas.blockGas > 0 || gas.blockStateGas > 0 then gas.blockGas else gas.spentGas

            end SimpleTransferCompletionExtractor.StageB.Leaf.Generated
            """ + "\n");
        byte[] manifest = Json(new
        {
            schemaVersion = 1,
            artifactKind = "stage-b-effective-block-gas-source-admitted-leaf",
            source,
            compilerPins = new StageBArtifactFile(CompilerSources.PinsPath, CompilerSources.PinsSha256),
            compilerInventory = new StageBArtifactFile(CompilerReferences.InventoryPath, CompilerReferences.InventorySha256),
            extractor = new StageBArtifactFile(ExtractorPath, CompilerReferences.Hash(File.ReadAllBytes(CompilerReferences.Within(root, ExtractorPath)))),
            references = closure.References,
            ir = new StageBArtifactFile(IrName, CompilerReferences.Hash(ir)),
            lean = new StageBArtifactFile(LeanName, CompilerReferences.Hash(lean)),
        });
        return new(StringComparer.Ordinal) { [IrName] = ir, [LeanName] = lean, [ManifestName] = manifest };
    }

    internal static void ValidateArtifacts(IReadOnlyDictionary<string, byte[]> supplied, IReadOnlyDictionary<string, byte[]> fresh)
    {
        if (!supplied.Keys.Order(StringComparer.Ordinal).SequenceEqual(fresh.Keys.Order(StringComparer.Ordinal)))
            throw new ExtractionException("The EffectiveBlockGas artifact roster changed.");
        foreach ((string name, byte[] bytes) in fresh)
            if (!supplied[name].AsSpan().SequenceEqual(bytes))
                throw new ExtractionException("The EffectiveBlockGas artifact is not fresh/canonical: " + name + ".");
    }

    internal static void Extract(string root, string output, bool check)
    {
        Dictionary<string, byte[]> fresh = RenderArtifacts(root, CompilerSources.Load(root));
        if (check)
        {
            ValidateArtifacts(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(output, path).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal), fresh);
            return;
        }
        Directory.CreateDirectory(output);
        foreach ((string name, byte[] bytes) in fresh) File.WriteAllBytes(Path.Combine(output, name), bytes);
    }

    private static byte[] Json(object value) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, CompilerReferences.JsonOptions) + "\n");
}
