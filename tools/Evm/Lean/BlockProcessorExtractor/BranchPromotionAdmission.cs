// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Upstream = Nethermind.Evm.Lean.SequentialBlockPostTransactionFinalizationExtractor;

namespace Nethermind.Evm.Lean.BlockProcessorExtractor;

internal static partial class BranchAcceptedIterationExtractor
{
    private const string InventoryHash = "1f73a3800a957dd02d9d7b06b3b955c81bb92fd9eb8d2831920905394bfc1d5c";
    private const string InventoryAggregate = "6fdfa102a4190083ae62080355aa6691b5f46acc32d4f9da2212012686d9c2e2";
    private const string PromotionPins = Package + "/BRANCH_PROMOTION_PINS.json";
    internal sealed record Reference(string Path, string AssemblyName, string Sha256, string Mvid, bool Selected);
    internal sealed record CompilerClosure(int SchemaVersion, Identity Inventory, int Count, int SelectedCount,
        string AggregateSha256, Reference[] References, Identity[] SupportSources);
    private sealed record Promotion(int SchemaVersion, string Status, int PublicationSchemaVersion,
        string PublicationExtractorVersion, Identity[] Artifacts);

    private static readonly Identity[] PublicationSourceIdentities =
    [
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "e2db419219184150c979cd6a081c83ba758c5024a15f996b510806bd1303deda"),
        new("src/Nethermind/Nethermind.Consensus/Validators/BlockValidator.cs", "7cc7b515c3001ba4f36ea16b74216f84216556c95d87585a192979e8c4f195d6"),
        new("src/Nethermind/Nethermind.Consensus/Validators/IBlockValidator.cs", "38cb507befc9695e522b16260b713b0b7b84efa226b857f7e5f410be0fd4e4c3"),
        new("src/Nethermind/Nethermind.Consensus/Processing/ProcessingOptions.cs", "145c5bec2a5e70e01dc448a887cd17bb1b81277605a487e437971882a5b0421a"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.std.cs", "7ec6d0f975f36958f83b002ca53404620427a756cab70a037c0988ed75567ed4"),
        new("src/Nethermind/Nethermind.Consensus/Processing/IBlockProcessor.cs", "df79a2a24ca0ad79918cf10bce5b9d74cb4ad1b7542185b41076a0f3758a940e"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockValidationTransactionsExecutor.cs", "d520090e2adde45bced4b6af6f9add35ea77ed288060d09fdf05cae3b28bdf56"),
        new("src/Nethermind/Nethermind.Blockchain/Receipts/IReceiptStorage.cs", "bccf3716b8bc0be5112cfba522ce58d2b51a6865a0bed20563b090bee62e1328"),
    ];
    private static readonly string[] PublicationSourcePaths = PublicationSourceIdentities.Select(identity => identity.Path).ToArray();

    private static readonly string[] OwnDependencyPaths =
    [
        Inventory, PublicationPackage + "/PROCESS_ONE_PUBLICATION_SOURCE_PINS.json", PromotionPins,
        Package + "/BranchKernel.lean.template", Package + "/Admission/BranchAcceptedIteration.cs.txt",
        Package + "/Specification/BranchAcceptedIteration.lean", Package + "/Refinement/BranchAcceptedIteration.lean",
        Package + "/Vectors/BranchAcceptedIterationVectors.lean", Package + "/Schema/branch-accepted-iteration.ir.schema.json",
        Package + "/Schema/branch-accepted-iteration.source-manifest.schema.json",
        Package + "/Verify-Branch.ps1", Package + "/Verify-BranchMutationGates.ps1", Package + "/Verify-BranchAxioms.ps1",
        Package + "/EXPORTED_BRANCH_THEOREMS.txt", Package + "/BranchAcceptedIterationExtractor.cs",
        Package + "/BranchPromotionAdmission.cs", Package + "/Extractor.cs", Package + "/Program.cs", Package + "/BlockProcessorExtractor.csproj",
        Package + "/lakefile.toml", Package + "/lean-toolchain",
    ];

    private static void ValidateAcceptedPublicationPins(string root, IReadOnlyList<Identity> actual)
    {
        byte[] bytes = File.ReadAllBytes(Resolve(root, PromotionPins));
        try
        {
            using JsonDocument raw = JsonDocument.Parse(bytes);
            RejectDuplicates(raw.RootElement);
            Promotion pins = JsonSerializer.Deserialize<Promotion>(bytes, Json)
                ?? throw new ExtractionException("Branch accepted publication pins are null.");
            if (pins.SchemaVersion != 1 || pins.Status != "accepted-upstream" || pins.PublicationSchemaVersion != 2 ||
                pins.PublicationExtractorVersion != "1.0.0" || pins.Artifacts is null || !pins.Artifacts.SequenceEqual(actual))
                throw new ExtractionException("Branch accepted publication pins changed; independent acceptance is required.");
        }
        catch (JsonException exception)
        {
            throw new ExtractionException("Branch accepted publication pins are invalid: " + exception.Message);
        }
    }

    private static CompilerClosure ReadCompilerClosure(string root)
    {
        try
        {
            Upstream.CompilerContext context = Upstream.Extractor.ReadCompilerContext(root, [], validateCompilation: false);
            CompilerClosure closure = new(2, new(Inventory, context.Closure.InventorySha256), context.References.Length,
                context.References.Count(reference => reference.Selected), context.Closure.AggregateSha256,
                context.References.Select(reference => new Reference(reference.Path, reference.AssemblyName,
                    reference.Sha256, reference.Mvid, reference.Selected)).ToArray(),
                context.Closure.SupportSources.Select(source => new Identity(source.Path, source.Sha256)).ToArray());
            ValidateCompilerClosure(closure);
            return closure;
        }
        catch (Upstream.ExtractionException exception)
        {
            throw new ExtractionException("Branch compiler dependency failed: " + exception.Message);
        }
    }

    internal static void ValidateCompilerClosure(CompilerClosure closure)
    {
        if (closure is null || closure.SchemaVersion != 2 || closure.Inventory != new Identity(Inventory, InventoryHash) ||
            closure.Count != 434 || closure.SelectedCount != 329 || closure.AggregateSha256 != InventoryAggregate ||
            closure.References is null || closure.References.Length != 434 || closure.SupportSources is null ||
            !closure.SupportSources.SequenceEqual(Upstream.Extractor.CompilerSupportSources.Select(source => new Identity(source.Path, source.Sha256))))
            throw new ExtractionException("Branch compiler closure header/support roster changed.");
        Reference[] references = closure.References;
        if (references.Any(reference => reference is null || string.IsNullOrWhiteSpace(reference.Path) ||
                string.IsNullOrWhiteSpace(reference.AssemblyName) || !IsHash(reference.Sha256) ||
                !Guid.TryParseExact(reference.Mvid, "D", out Guid mvid) || mvid.ToString("D") != reference.Mvid) ||
            references.Select(reference => reference.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != references.Length ||
            references.Count(reference => reference.Selected) != 329 ||
            references.Where(reference => reference.Selected).Select(reference => reference.AssemblyName)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 329)
            throw new ExtractionException("Branch compiler reference identity/selection roster changed.");
        string aggregate = Hash(Encoding.UTF8.GetBytes(string.Join('\n', references.Select(reference =>
            $"{reference.Path}\0{reference.AssemblyName}\0{reference.Sha256}\0{reference.Mvid}\0{reference.Selected}")) + "\n"));
        if (aggregate != InventoryAggregate)
            throw new ExtractionException("Branch compiler reference aggregate changed.");
    }

    internal static CSharpCompilation Compile(string root, IEnumerable<SyntaxTree> trees) =>
        Compile(root, trees, ReadCompilerClosure(root));

    private static CSharpCompilation Compile(string root, IEnumerable<SyntaxTree> trees, CompilerClosure closure)
    {
        List<SyntaxTree> syntaxTrees = trees.ToList();
        foreach (Identity source in closure.SupportSources)
        {
            byte[] bytes = File.ReadAllBytes(Resolve(root, source.Path));
            if (Hash(bytes) != source.Sha256 || syntaxTrees.Any(tree => tree.FilePath == source.Path))
                throw new ExtractionException("Branch compiler support identity/classification changed.");
            SyntaxTree tree = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(bytes),
                new CSharpParseOptions(LanguageVersion.CSharp14), source.Path);
            if (tree.GetRoot().DescendantTrivia(descendIntoTrivia: true).Any(trivia => trivia.IsDirective))
                throw new ExtractionException("Branch compiler support directives are not admitted.");
            syntaxTrees.Add(tree);
        }
        string platform = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        MetadataReference[] references = closure.References.Where(reference => reference.Selected)
            .Select(reference => MetadataReference.CreateFromFile(reference.Path.StartsWith("platform/", StringComparison.Ordinal)
                ? Path.Combine(platform, reference.Path[9..]) : Resolve(root, reference.Path))).ToArray();
        // This existing friend identity permits typed inspection of pinned internal State implementation members.
        CSharpCompilation compilation = CSharpCompilation.Create("Nethermind.Blockchain.Test", syntaxTrees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable,
                allowUnsafe: true, metadataImportOptions: MetadataImportOptions.All));
        Diagnostic[] errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0)
            throw new ExtractionException("Branch typed closure failed: " + string.Join("; ", errors.Select(diagnostic => diagnostic.ToString())));
        return compilation;
    }

    internal static void AtomicWrite(string destination, byte[] bytes, Action? beforeReplace = null)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            beforeReplace?.Invoke();
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
