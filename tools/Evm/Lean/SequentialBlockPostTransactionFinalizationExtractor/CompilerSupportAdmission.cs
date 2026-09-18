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
    internal static readonly PublicationDependencyIdentity[] CompilerSupportSources =
    [
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.SystemContractHandler.cs",
            "a069ba0962027b068e388544d3f338a47990f11e5085d381238231607758128b"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockAccessListSystemContractHandler.cs",
            "07342dde345dcec7b34569e8cb9f447f9d4abe3a640cd127559db77f2e1981fe"),
        new("src/Nethermind/Nethermind.Consensus/Processing/TransactionProcessorAdapterExtensions.cs",
            "910820a7e8bba91c30b052f5818707c46d625a2fbf12727e2aa869512120aa9e"),
        new("src/Nethermind/Nethermind.Consensus/Processing/IBlockAccessListManager.cs",
            "0ed401a074b32a35fb3de497a516d8b717802c55ffdac3f1cd773a7f4e6be273"),
    ];

    private static SyntaxTree[] ReadCompilerSupport(string root, IReadOnlyList<SourceFile> sources,
        IReadOnlyDictionary<string, byte[]>? overrides, bool enforcePins, bool enforceSourceSelection)
    {
        if (overrides is not null && overrides.Keys.Any(path => !CompilerSupportSources.Any(identity => identity.Path == path)))
            throw new ExtractionException("Compiler-support override escaped its exact roster.");
        List<SyntaxTree> result = [];
        foreach (PublicationDependencyIdentity identity in CompilerSupportSources)
        {
            byte[] bytes = overrides is not null && overrides.TryGetValue(identity.Path, out byte[]? changed)
                ? changed : File.ReadAllBytes(Path.Combine(root, identity.Path));
            if (enforcePins && Sha256(bytes) != identity.Sha256 || sources.Any(source => source.RelativePath == identity.Path))
                throw new ExtractionException("Finalization compiler-support identity or classification changed.");
            SyntaxTree tree = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(bytes), ParseOptions, identity.Path);
            if (enforceSourceSelection) ArtifactSafety.RequireUnconditionalSource(tree.GetRoot(), "Finalization compiler-support");
            result.Add(tree);
        }
        return result.ToArray();
    }

    internal static void AuditCompilerSupportForTest(string root, IReadOnlyDictionary<string, byte[]> overrides,
        bool compileOnly, bool enforcePins)
    {
        SourceFile[] sources = ReadSources(root, ReadPins(root), null, true);
        _ = ReadCompilerContext(root, sources, true, overrides, enforcePins, !compileOnly);
    }

    private static bool IsSemanticTree(SyntaxTree tree) => SourcePaths.Contains(tree.FilePath, StringComparer.Ordinal);

    private static bool CompilerClosureMatches(CompilerClosureIdentity actual, CompilerClosureIdentity expected) =>
        actual is not null && expected is not null && actual.InventoryPath == expected.InventoryPath &&
        actual.Count == expected.Count && actual.AggregateSha256 == expected.AggregateSha256 &&
        actual.InventorySha256 == expected.InventorySha256 && actual.SupportSources is not null &&
        expected.SupportSources is not null && actual.SupportSources.SequenceEqual(expected.SupportSources);

    private static void ValidateCompilerSupportBoundary(Compilation compilation, IReadOnlyList<SyntaxTree> support)
    {
        foreach (SyntaxTree tree in support)
        {
            if (IsSemanticTree(tree)) throw new ExtractionException("Compiler support was classified as a semantic-owned tree.");
            SemanticModel model = compilation.GetSemanticModel(tree);
            foreach (ExpressionSyntax expression in tree.GetRoot().DescendantNodes().OfType<ExpressionSyntax>())
            {
                IOperation? operation = model.GetOperation(expression);
                ISymbol? target = operation switch
                {
                    IInvocationOperation call => call.TargetMethod,
                    IMethodReferenceOperation reference => reference.Method,
                    IObjectCreationOperation creation => creation.Constructor,
                    IPropertyReferenceOperation property => property.Property,
                    IFieldReferenceOperation field => field.Field,
                    IEventReferenceOperation eventReference => eventReference.Event,
                    IConversionOperation conversion => conversion.OperatorMethod,
                    IBinaryOperation binary => binary.OperatorMethod,
                    IUnaryOperation unary => unary.OperatorMethod,
                    _ => null,
                };
                if (target is not null && !IsExactCompilerSupportFlagReference(target) &&
                    target.DeclaringSyntaxReferences.Any(reference => IsSemanticTree(reference.SyntaxTree)))
                    throw new ExtractionException("Compiler support re-enters the semantic-owned source closure.");
                if (operation is IAnonymousFunctionOperation or IDelegateCreationOperation or IDynamicInvocationOperation or
                    IFunctionPointerInvocationOperation || operation is IInvocationOperation { TargetMethod.MethodKind: MethodKind.DelegateInvoke })
                    throw new ExtractionException("Compiler support introduced an unadmitted callback route.");
            }
        }
    }

    private static bool IsExactCompilerSupportFlagReference(ISymbol target) => target switch
    {
        IFieldSymbol { HasConstantValue: true, Name: "LoadNonceFromState" } field =>
            field.ContainingType.ToDisplayString() == "Nethermind.Consensus.Processing.ProcessingOptions" &&
            field.ConstantValue is int value && value == 32,
        IMethodSymbol { Name: "ContainsFlag" } method =>
            method.ContainingType.ToDisplayString() == "Nethermind.Consensus.Processing.ProcessingOptionsExtensions" &&
            method.DeclaringSyntaxReferences.Length == 1 && Canonical(method.DeclaringSyntaxReferences[0].GetSyntax()) ==
            "publicstaticboolContainsFlag(thisProcessingOptionsprocessingOptions,ProcessingOptionsflag)=>(processingOptions&flag)==flag;",
        _ => false,
    };
}
