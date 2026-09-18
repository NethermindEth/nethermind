// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Evm.Lean.BlockProcessorExtractor;

/// <summary>Admits the finite normal caller loop and its separate synchronous publication boundary.</summary>
internal static class OuterBlockExtractor
{
    internal const string Package = BranchAcceptedIterationExtractor.Package;
    internal const string ChainPath = "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs";
    internal const string Finite = "NormalFiniteBranchCompletion";
    internal const string Publication = "BlockchainPublication";
    private const string Accepted = "source-admitted";
    private const string Version = "1.0.0";
    private const string BranchOwner = "global::Nethermind.Consensus.Processing.BranchProcessor";
    private const string ChainOwner = "global::Nethermind.Consensus.Processing.BlockchainProcessor";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
    };

    internal sealed record Identity(string Path, string Sha256);
    internal sealed record Site(string Id, string Path, string Owner, string Target, string Syntax,
        int Position, int[] CfgBlocks, string[] Guards, string[] Reads, string[] Writes);
    internal sealed record Flow(string Owner, string[] Blocks, string[] Edges);
    internal sealed record Document(int SchemaVersion, string ExtractorVersion, string Name, string AcceptanceState,
        string SourceClosureSha256, Identity[] Sources, Identity[] Dependencies, Site[] Sites, Flow[] ControlFlows,
        string[] Operations, int LoopIncrement, int ReadOnlyMask, int DoNotUpdateHeadMask, int MarkProcessedMask,
        string[] Exclusions);
    private sealed record Manifest(int SchemaVersion, string ExtractorVersion, string Name, string AcceptanceState,
        string SourceClosureSha256, Identity Ir, Identity Lean, Identity[] Sources, Identity[] Dependencies);
    private sealed record CensusPin(int SchemaVersion, string Name, int Count, Identity Inventory);
    private sealed record PromotionPin(int SchemaVersion, string Name, string Status, string Upstream,
        int UpstreamSchemaVersion, string UpstreamExtractorVersion, Identity[] Artifacts);
    private sealed record Rule(string Id, string Owner, string Syntax);

    private static readonly Identity[] Pins =
    [
        new(ChainPath, "fc76d8d4dcf30093fb2e52262e8cd90d71493c2dc3ce04a1f314dd8f24f3a8cb"),
        new("src/Nethermind/Nethermind.Consensus/Processing/IBlockchainProcessor.cs", "60818b4b3cab75c49a588e3ef61861708553a9b8c9595f5b529974d1bfd92f38"),
        new("src/Nethermind/Nethermind.Consensus/Processing/IBlockProcessingQueue.cs", "b37a8491ac796a72eb38d51b43354e4c77c656a487a68cd82d065aedf58ecfd4"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockRef.cs", "ede22b15471c363d7713f5daf3fcbe0ba959c49c1ce5cc0ddfbe10faeb9d7a7e"),
        new("src/Nethermind/Nethermind.Blockchain/IBlockTree.cs", "fd4713254e7cfbf079f2edec9fc45e8ddfc4eacff858722e7f8992b5d0afb274"),
        new("src/Nethermind/Nethermind.Core/Block.cs", "3cdd12ca52b00b6eefea372be868649653ac57043194aa081d41064d0ed749aa"),
        new("src/Nethermind/Nethermind.Core/BlockHeader.cs", "f354ddd2d45afc8774739ac46a10535b915871ce4ad11d996d0ab972cf9e7349"),
        new("src/Nethermind/Nethermind.Core/Exceptions/InvalidBlockException.cs", "2098b8a970bd64d589d556a773eb0312a4681f8978f1a991f55af249b3f1aa56"),
        new("src/Nethermind/Nethermind.Core/Crypto/Hash256.cs", "ce570ece3b5cd5b8abf9d99491c03340fe32e235a6ad15c65b98e6dbddab18f1"),
        new("src/Nethermind/Nethermind.Core/Collections/ArrayPoolList.cs", "4d88f251cc668907ec51ba06d342efdc278417eb628c7634d2957d1359e32b67"),
    ];
    private static readonly Identity[] SourcePins = new Identity[]
    {
        new("src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "451ad7a9cbb283351519623dcf358e7d3efdb69db2a0e5a452614cbe1d3dc369"),
        new("src/Nethermind/Nethermind.State/WorldState.cs", "2dec74bcc5748a1850d1bed1e8fe0221aed8c904bf2ba0c0827e76e603a71f18"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BranchProcessingCompletedEventArgs.cs", "f0d3fbeac9c35f442d301d2044d903988cdfb726c2f486f5a29b288fc67d283b"),
        new("src/Nethermind/Nethermind.Consensus/Processing/IBranchProcessor.cs", "fc9ecd1b27f22c9b0861615e6c4f0c24f9430ae759e9c040c9e989d9249a0b20"),
        new("src/Nethermind/Nethermind.Core/Extensions/CancellationTokenExtensions.cs", "7811bd704199aaae97c164a54b25f77a1613f716fe298c2baa2b4ac78d3d4fe0"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "e2db419219184150c979cd6a081c83ba758c5024a15f996b510806bd1303deda"),
        new("src/Nethermind/Nethermind.Consensus/Validators/BlockValidator.cs", "7cc7b515c3001ba4f36ea16b74216f84216556c95d87585a192979e8c4f195d6"),
        new("src/Nethermind/Nethermind.Consensus/Validators/IBlockValidator.cs", "38cb507befc9695e522b16260b713b0b7b84efa226b857f7e5f410be0fd4e4c3"),
        new("src/Nethermind/Nethermind.Consensus/Processing/ProcessingOptions.cs", "145c5bec2a5e70e01dc448a887cd17bb1b81277605a487e437971882a5b0421a"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.std.cs", "7ec6d0f975f36958f83b002ca53404620427a756cab70a037c0988ed75567ed4"),
        new("src/Nethermind/Nethermind.Consensus/Processing/IBlockProcessor.cs", "df79a2a24ca0ad79918cf10bce5b9d74cb4ad1b7542185b41076a0f3758a940e"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockValidationTransactionsExecutor.cs", "d520090e2adde45bced4b6af6f9add35ea77ed288060d09fdf05cae3b28bdf56"),
        new("src/Nethermind/Nethermind.Blockchain/Receipts/IReceiptStorage.cs", "bccf3716b8bc0be5112cfba522ce58d2b51a6865a0bed20563b090bee62e1328"),
    }.Concat(Pins).OrderBy(identity => identity.Path, StringComparer.Ordinal).ToArray();

    private static readonly Rule[] Rules =
    [
        new("finite.empty", "branch", "if(suggestedBlocks.Count==0)return[];"),
        new("finite.count", "branch", "blocksCount=suggestedBlocks.Count"),
        new("finite.allocate", "branch", "processedBlocks=newBlock[blocksCount]"),
        new("finite.zeroPrefix", "branch", "processedBlocksCount=0"),
        new("finite.originalList", "branch", "blocksProcessingEventArgs=newBlocksProcessingEventArgs(suggestedBlocks);"),
        new("finite.index", "branch", "i=0"),
        new("finite.guard", "branch", "i<blocksCount"),
        new("finite.increment", "branch", "i++"),
        new("finite.suggested", "branch", "suggestedBlock=suggestedBlocks[i];"),
        new("finite.refresh", "branch", "if(i>0){spec=specProvider.GetSpec(suggestedBlock.Header);}"),
        new("finite.cancellation", "branch", "backgroundCancellation??=newCancellationTokenSource();"),
        new("finite.prewarm", "branch", "preWarmTask??=PreWarmTransactions(suggestedBlock,preBlockBaseBlock,spec,backgroundCancellation.Token);"),
        new("finite.prefetch", "branch", "prefetchBlockhash??=blockhashProvider.Prefetch(suggestedBlock.Header,backgroundCancellation.Token);"),
        new("finite.options", "branch", "blockOptions=blockTracer==NullBlockTracer.Instance?options:options|ProcessingOptions.ForceSequentialBlockAccessList"),
        new("finite.call", "branch", "blockProcessor.ProcessOne(suggestedBlock,blockOptions,blockTracer,spec,token)"),
        new("finite.return", "branch", "returnprocessedBlocks;"),
        new("wrapper.call", "wrapper", "_branchProcessor.Process(processingBranch.BaseBlock,processingBranch.BlocksToProcess,options,tracer,token)"),
        new("wrapper.error", "wrapper", "error=null;"),
        new("invalid.hash", "wrapper", "invalidBlockHash=ex.InvalidBlock.Hash;"),
        new("invalid.error", "wrapper", "error=ex.Message;"),
        new("invalid.first", "wrapper", "processingBranch.BlocksToProcess.FirstOrDefault(b=>b.Hash==invalidBlockHash)"),
        new("invalid.nullResult", "wrapper", "processedBlocks=null;"),
        new("invalid.deleteGuard", "wrapper", "invalidBlockHashisnotnull&&!options.ContainsFlag(ProcessingOptions.ReadOnlyChain)"),
        new("invalid.deleteCall", "wrapper", "DeleteInvalidBlocks(inprocessingBranch,invalidBlockHash);"),
        new("invalid.deleteEach", "wrapper", "_blockTree.DeleteInvalidBlock(processingBranch.BlocksToProcess[i]);"),
        new("wrapper.return", "wrapper", "returnprocessedBlocks;"),
        new("outer.error", "outer", "error=null;"),
        new("outer.simpleChecks", "outer", "!RunSimpleChecksAheadOfProcessing(suggestedBlock,options)"),
        new("outer.shouldProcess", "outer", "shouldProcess=suggestedBlock.IsGenesis||_blockTree.IsBetterThanHead(suggestedBlock.Header)||options.ContainsFlag(ProcessingOptions.ForceProcessing)"),
        new("outer.readOnly", "outer", "readonlyChain=options.ContainsFlag(ProcessingOptions.ReadOnlyChain)"),
        new("outer.branch", "outer", "usingProcessingBranchprocessingBranch=PrepareProcessingBranch(suggestedBlock,options);"),
        new("outer.prepare", "outer", "PrepareBlocksToProcess(suggestedBlock,options,processingBranch);"),
        new("outer.wrapper", "outer", "ProcessBranch(processingBranch,options,tracer,token,outerror)"),
        new("outer.stop", "outer", "_stopwatch.Stop();"),
        new("outer.null", "outer", "if(processedBlocksisnull){returnnull;}"),
        new("outer.nonempty", "outer", "processedBlocks.Length>0"),
        new("outer.last", "outer", "lastProcessed=processedBlocks[^1];"),
        new("outer.difficulty", "outer", "lastProcessed.Header.TotalDifficulty=suggestedBlock.TotalDifficulty;"),
        new("outer.stats", "outer", "_stats.UpdateStats(processedBlocks,processingBranch.BaseBlock,blockProcessingTimeInMicrosecs);"),
        new("outer.headGuard", "outer", "updateHead=!options.ContainsFlag(ProcessingOptions.DoNotUpdateHead)"),
        new("outer.head", "outer", "_blockTree.TryUpdateMainChain(suggestedBlock.Header,wereProcessed:true,preloadedBlocks:processingBranch.Blocks.AsSpan())"),
        new("outer.markGuard", "outer", "(options&ProcessingOptions.MarkAsProcessed)==ProcessingOptions.MarkAsProcessed"),
        new("outer.mark", "outer", "_blockTree.MarkChainAsProcessed(processingBranch.Blocks);"),
        new("outer.metrics", "outer", "Metrics.BestKnownBlockNumber=_blockTree.BestKnownNumber;"),
        new("outer.return", "outer", "returnlastProcessed;"),
        new("outer.dispose", "dispose", "{Blocks.Dispose();BlocksToProcess.Dispose();}"),
        new("prepare.forceClear", "prepare", "processingBranch.Blocks.Clear();"),
        new("prepare.forceAdd", "prepare", "blocksToProcess.Add(suggestedBlock);"),
        new("prepare.inputs", "prepare", "blocksToProcess=processingBranch.BlocksToProcess"),
        new("prepare.copy", "prepare", "blocksToProcess.Add(block);"),
        new("prepare.nonempty", "prepare", "firstBlock=blocksToProcess[0]"),
    ];
    private static readonly string[] FiniteOps = ["allocate", "prepareIteration", "acceptedIteration", "advance", "return"];
    private static readonly string[] PublicationOps = ["stop", "difficulty", "stats", "head", "mark", "metrics", "prepareReturn", "disposeBlocks", "disposeInputs", "return"];
    private static readonly string[] Exclusions =
    [
        "Normal owned nonempty sequential loop after preloop setup; branch selection and acquisition remain entry obligations",
        "No BAL retry, parallel equivalence, externally owned genesis, transaction or world-state finite composition",
        "Invalid and skipped classifications are boundary contracts, not proofs that the accepted finite runner throws",
        "No callback interference, asynchronous queue/recovery, completed cache continuations, prefetch or transaction hashes",
        "Tree calls are observations, not head-change, canonicality, trie, persistence, rollback, restart or crash guarantees",
    ];

    internal static string Diagnostic(string anchor) => "Outer source admission rejected anchor: " + anchor + ".";

    internal static string InventoryFile(string name) => name == Finite ? "EXPORTED_FINITE_THEOREMS.txt" : "EXPORTED_OUTER_THEOREMS.txt";
    internal static string CensusPinFile(string name) => name == Finite ? "FINITE_CENSUS_PINS.json" : "PUBLICATION_CENSUS_PINS.json";
    private static string PromotionPinFile(string name) => name == Finite ? "FINITE_PROMOTION_PINS.json" : "PUBLICATION_PROMOTION_PINS.json";
    private static string SchemaStem(string name) => name == Finite ? "normal-finite-branch" : "blockchain-publication";

    internal static string[] ReadExportedTheorems(string root, string name, byte[] bytes)
    {
        CensusPin pin = ReadStrict<CensusPin>(File.ReadAllBytes(Resolve(root, Package + "/" + CensusPinFile(name))));
        string[] names = Encoding.UTF8.GetString(bytes).TrimEnd('\n').Split('\n');
        if (name is not (Finite or Publication) || pin.SchemaVersion != 1 || pin.Name != name || pin.Count <= 0 ||
            pin.Inventory.Path != Package + "/" + InventoryFile(name) || !IsHash(pin.Inventory.Sha256) ||
            Hash(bytes) != pin.Inventory.Sha256 || names.Length != pin.Count ||
            !names.SequenceEqual(names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) ||
            names.Any(theorem => string.IsNullOrWhiteSpace(theorem) || !theorem.Contains('.') ||
                theorem.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '.'))))
            throw new ExtractionException("Outer exported-theorem inventory changed; explicit review is required.");
        return names;
    }
    private static string[] DependencyPaths(string name)
    {
        string upstream = name == Finite ? BranchAcceptedIterationExtractor.Name : Finite;
        string template = upstream == BranchAcceptedIterationExtractor.Name ? "BranchKernel.lean.template" : Finite + ".lean.template";
        string[] paths = [Package + "/Generated/" + upstream + ".ir.json", Package + "/Generated/" + upstream + ".lean",
            Package + "/Generated/" + upstream + ".source-manifest.json", Package + "/Specification/" + upstream + ".lean",
            Package + "/Refinement/" + upstream + ".lean", Package + "/" + template,
            Package + "/" + (name == Finite ? "BranchAcceptedIterationExtractor.cs" : "OuterBlockExtractor.cs"),
            BranchAcceptedIterationExtractor.Inventory,
            Package + "/" + name + ".lean.template", Package + "/Specification/" + name + ".lean",
            Package + "/Refinement/" + name + ".lean", Package + "/OuterBlockExtractor.cs",
            Package + "/Vectors/" + (name == Finite ? "FiniteBranchVectors" : "OuterBlockVectors") + ".lean",
            Package + "/" + InventoryFile(name), Package + "/" + CensusPinFile(name),
            Package + "/Verify-OuterAxioms.ps1", Package + "/Verify-Outer.ps1", Package + "/Verify-OuterMutationGates.ps1",
            Package + "/Schema/" + SchemaStem(name) + ".ir.schema.json",
            Package + "/Schema/" + SchemaStem(name) + ".source-manifest.schema.json",
            Package + "/" + PromotionPinFile(name), Package + "/Admission/BlockchainOuter.cs.txt"];
        return paths.Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static void Extract(string root, string output, string name)
    {
        Document document = Audit(root, name);
        byte[] ir = Serialize(document);
        byte[] lean = Emit(root, ReadDocument(ir));
        byte[] manifest = Serialize(CreateManifest(document, ir, lean));
        string destination = Path.GetFullPath(output);
        string production = Path.GetFullPath(Path.Combine(root, "src"));
        if (destination.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) || destination.Equals(production, StringComparison.OrdinalIgnoreCase) ||
            destination.StartsWith(production + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ExtractionException("Outer output cannot target production or the workspace root.");
        WriteArtifacts(destination, name, ir, lean, manifest);
    }

    internal static void WriteArtifacts(string directory, string name, byte[] ir, byte[] lean, byte[] manifest)
    {
        if (name is not (Finite or Publication)) throw new ExtractionException("Unknown outer slice.");
        Directory.CreateDirectory(directory);
        AtomicWrite(Path.Combine(directory, name + ".ir.json"), ir);
        AtomicWrite(Path.Combine(directory, name + ".lean"), lean);
        AtomicWrite(Path.Combine(directory, name + ".source-manifest.json"), manifest);
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        string directory = Path.GetDirectoryName(path) ?? throw new ExtractionException("Outer artifact directory is missing.");
        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static void Check(string root, string output, string name)
    {
        Document expected = Audit(root, name);
        byte[] ir = File.ReadAllBytes(Path.Combine(output, name + ".ir.json"));
        Document actual = ReadDocument(ir);
        ValidateDependencies(root, actual);
        if (!ir.AsSpan().SequenceEqual(Serialize(actual)) || !Serialize(actual).AsSpan().SequenceEqual(Serialize(expected)))
            throw new ExtractionException("Outer IR is not the canonical current source audit.");
        byte[] lean = Emit(root, actual);
        if (!lean.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(output, name + ".lean"))))
            throw new ExtractionException("Outer Lean is stale or differs from the admitted IR.");
        byte[] manifest = File.ReadAllBytes(Path.Combine(output, name + ".source-manifest.json"));
        ReadStrict<Manifest>(manifest);
        if (!manifest.AsSpan().SequenceEqual(Serialize(CreateManifest(actual, ir, lean))))
            throw new ExtractionException("Outer manifest lineage changed.");
    }

    internal static void ValidateDependencies(string root, Document document)
    {
        Validate(document);
        foreach (Identity identity in document.Dependencies)
        {
            string path = Resolve(root, identity.Path);
            if (!File.Exists(path) || Hash(File.ReadAllBytes(path)) != identity.Sha256)
                throw new ExtractionException("Outer dependency changed or missing: '" + identity.Path + "'.");
        }
    }

    internal static void ValidateUpstream(string root, string name, string? directory = null)
    {
        if (name is not (Finite or Publication)) throw new ExtractionException("Unknown outer slice.");
        string output = directory ?? Path.Combine(root, Package, "Generated");
        if (name == Finite) BranchAcceptedIterationExtractor.Check(root, output);
        else Check(root, output, Finite);
        PromotionPin pin = ReadStrict<PromotionPin>(File.ReadAllBytes(Resolve(root, Package + "/" + PromotionPinFile(name))));
        string upstream = name == Finite ? BranchAcceptedIterationExtractor.Name : Finite;
        Identity[] artifacts = new[] { ".ir.json", ".lean", ".source-manifest.json" }
            .Select(extension => new Identity(Package + "/Generated/" + upstream + extension,
                Hash(File.ReadAllBytes(Path.Combine(output, upstream + extension))))).ToArray();
        if (pin.SchemaVersion != 1 || pin.Name != name || pin.Status != "accepted-upstream" || pin.Upstream != upstream ||
            pin.UpstreamSchemaVersion != (name == Finite ? 2 : 1) || pin.UpstreamExtractorVersion != "1.0.0" ||
            !pin.Artifacts.SequenceEqual(artifacts))
            throw new ExtractionException("Outer upstream promotion pins changed; independent acceptance is required.");
    }

    internal static Document AuditSourceForTest(string root, string name, IReadOnlyDictionary<string, string>? overrides = null) =>
        Audit(root, name, overrides, sourceOnly: true);

    internal static void RequireCompilationForTest(string root, IReadOnlyDictionary<string, string> overrides) => ReadCompilation(root, overrides, false);

    private static (CSharpCompilation Compilation, Dictionary<string, SyntaxTree> Trees, Identity[] Sources) ReadCompilation(
        string root, IReadOnlyDictionary<string, string>? overrides, bool enforcePins)
    {
        (Dictionary<string, SyntaxTree> trees, List<BranchAcceptedIterationExtractor.Identity> identities) =
            BranchAcceptedIterationExtractor.ReadSourceTrees(root, overrides, enforcePins);
        List<Identity> sources = identities.Select(identity => new Identity(identity.Path, identity.Sha256)).ToList();
        foreach (Identity pin in Pins)
        {
            byte[] bytes = overrides is not null && overrides.TryGetValue(pin.Path, out string? changed)
                ? Encoding.UTF8.GetBytes(changed) : File.ReadAllBytes(Resolve(root, pin.Path));
            if (enforcePins && Hash(bytes) != pin.Sha256) throw new ExtractionException("Outer source pin changed: " + pin.Path);
            sources.Add(new(pin.Path, Hash(bytes)));
            if (pin.Path == ChainPath || pin.Path.StartsWith("src/Nethermind/Nethermind.Consensus/Processing/", StringComparison.Ordinal))
                trees.Add(pin.Path, CSharpSyntaxTree.ParseText(Encoding.UTF8.GetString(bytes), new CSharpParseOptions(LanguageVersion.CSharp14), pin.Path));
        }
        return (BranchAcceptedIterationExtractor.Compile(root, trees.Values), trees, sources.OrderBy(identity => identity.Path, StringComparer.Ordinal).ToArray());
    }

    private static Document Audit(string root, string name, IReadOnlyDictionary<string, string>? overrides = null, bool sourceOnly = false)
    {
        if (name is not (Finite or Publication)) throw new ExtractionException("Unknown outer slice.");
        if (!sourceOnly) ValidateUpstream(root, name);
        if (!sourceOnly) ReadExportedTheorems(root, name, File.ReadAllBytes(Resolve(root, Package + "/" + InventoryFile(name))));
        (CSharpCompilation compilation, Dictionary<string, SyntaxTree> trees, Identity[] sources) = ReadCompilation(root, overrides, !sourceOnly);
        SyntaxNode branch = trees[Extractor.BranchPath].GetRoot();
        SyntaxNode chain = trees[ChainPath].GetRoot();
        if (chain.DescendantTrivia().Any(trivia => trivia.IsDirective)) throw new ExtractionException("Outer preprocessor directives are unadmitted.");
        Dictionary<string, MethodDeclarationSyntax> methods = new(StringComparer.Ordinal)
        {
            ["branch"] = Method(branch, "BranchProcessor", "Process", 5),
            ["wrapper"] = Method(chain, "BlockchainProcessor", "ProcessBranch", 5),
            ["outer"] = Method(chain, "BlockchainProcessor", "Process", 5),
            ["dispose"] = Method(chain, "ProcessingBranch", "Dispose", 0),
            ["prepare"] = Method(chain, "BlockchainProcessor", "PrepareBlocksToProcess", 3),
        };
        MethodDeclarationSyntax wrapper = methods["wrapper"];
        CatchClauseSyntax[] catches = wrapper.DescendantNodes().OfType<CatchClauseSyntax>().ToArray();
        if (catches.Length != 1 || catches[0].Declaration is null || catches[0].Filter is not null ||
            compilation.GetSemanticModel(wrapper.SyntaxTree).GetTypeInfo(catches[0].Declaration!.Type).Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) !=
            "global::Nethermind.Core.Exceptions.InvalidBlockException") throw new ExtractionException(Diagnostic("invalid.catch"));
        Dictionary<string, ControlFlowGraph> graphs = methods.ToDictionary(pair => pair.Key,
            pair => Graph(compilation.GetSemanticModel(pair.Value.SyntaxTree), pair.Value), StringComparer.Ordinal);
        RequireMethod(compilation, methods["branch"], BranchOwner, "Process", ["None", "None", "None", "None", "None"]);
        RequireMethod(compilation, methods["outer"], ChainOwner, "Process", ["None", "None", "None", "None", "Out"]);
        RequireMethod(compilation, methods["wrapper"], ChainOwner, "ProcessBranch", ["In", "None", "None", "None", "Out"]);
        List<Site> sites = [];
        foreach (Rule rule in Rules)
        {
            MethodDeclarationSyntax method = methods[rule.Owner];
            SyntaxNode[] nodes = method.DescendantNodes().Where(node => Canonical(node) == rule.Syntax).ToArray();
            if (nodes.Length != 1) throw new ExtractionException(Diagnostic(rule.Id));
            sites.Add(Anchor(compilation.GetSemanticModel(method.SyntaxTree), graphs[rule.Owner], method, nodes[0], rule.Id));
        }
        RequireInvocation(compilation, methods["wrapper"], "Process", "_branchProcessor", "global::Nethermind.Consensus.Processing.IBranchProcessor", 5);
        IInvocationOperation head = RequireInvocation(compilation, methods["outer"], "TryUpdateMainChain", "_blockTree", "global::Nethermind.Blockchain.IBlockTree", 4);
        IArgumentOperation force = head.Arguments.Single(argument => argument.Parameter!.Name == "forceUpdateHeadBlock");
        if (!force.IsImplicit || !force.Value.ConstantValue.HasValue || force.Value.ConstantValue.Value is not false)
            throw new ExtractionException(Diagnostic("outer.headDefault"));
        RequireInvocation(compilation, methods["outer"], "MarkChainAsProcessed", "_blockTree", "global::Nethermind.Blockchain.IBlockTree", 1);
        Site At(string id) => sites.Single(site => site.Id == id);
        if (!(At("outer.difficulty").Position < At("outer.stats").Position && At("outer.stats").Position < At("outer.head").Position &&
            At("outer.head").Position < At("outer.mark").Position && At("outer.mark").Position < At("outer.metrics").Position))
            throw new ExtractionException(Diagnostic("outer.order"));
        IfStatementSyntax headIf = methods["outer"].DescendantNodes().OfType<IfStatementSyntax>().Single(statement => Canonical(statement.Condition) == "updateHead");
        IfStatementSyntax[] markIf = methods["outer"].DescendantNodes().OfType<IfStatementSyntax>().Where(statement => Canonical(statement.Condition) == Rules.Single(rule => rule.Id == "outer.markGuard").Syntax).ToArray();
        if (headIf.Parent != methods["outer"].Body || markIf.Length != 1 || markIf[0].Parent != methods["outer"].Body ||
            headIf.DescendantNodes().OfType<ReturnStatementSyntax>().Any()) throw new ExtractionException(Diagnostic("outer.independentGuards"));
        RequireTemplate(branch, "BranchAcceptedIteration.cs.txt");
        RequireTemplate(chain, "BlockchainOuter.cs.txt");
        Identity[] dependencies = sourceOnly ? [] : DependencyPaths(name).Select(path => Identify(root, path)).ToArray();
        SemanticModel wrapperModel = compilation.GetSemanticModel(wrapper.SyntaxTree);
        LocalFunctionStatementSyntax delete = wrapper.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Single();
        IMethodSymbol deleteSymbol = wrapperModel.GetDeclaredSymbol(delete)!;
        Flow[] flows = methods.Select(pair => FlowOf(MethodId(compilation.GetSemanticModel(pair.Value.SyntaxTree).GetDeclaredSymbol(pair.Value)!), graphs[pair.Key]))
            .Append(FlowOf(MethodId(deleteSymbol), graphs["wrapper"].GetLocalFunctionControlFlowGraph(deleteSymbol))).ToArray();
        Document document = new(1, Version, name, sourceOnly ? "static-draft" : Accepted,
            Closure(sources, dependencies), sources, dependencies, sites.ToArray(), flows,
            name == Finite ? FiniteOps : PublicationOps, 1, 65, 64, 128, Exclusions);
        Validate(document, sourceOnly);
        return document;
    }

    private static MethodDeclarationSyntax Method(SyntaxNode root, string owner, string name, int arity) =>
        root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == name && method.ParameterList.Parameters.Count == arity &&
            method.Ancestors().OfType<TypeDeclarationSyntax>().First().Identifier.ValueText == owner);
    private static void RequireMethod(CSharpCompilation compilation, MethodDeclarationSyntax syntax, string owner, string name, string[] refs)
    {
        IMethodSymbol method = compilation.GetSemanticModel(syntax.SyntaxTree).GetDeclaredSymbol(syntax)!;
        static string TypeId(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat
            .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers)).Replace("global::", "", StringComparison.Ordinal);
        string expected = owner == BranchOwner
            ? "Nethermind.Core.BlockHeader,System.Collections.Generic.IReadOnlyList<Nethermind.Core.Block>,Nethermind.Consensus.Processing.ProcessingOptions,Nethermind.Evm.Tracing.IBlockTracer,System.Threading.CancellationToken"
            : name == "ProcessBranch"
                ? "Nethermind.Consensus.Processing.BlockchainProcessor.ProcessingBranch,Nethermind.Consensus.Processing.ProcessingOptions,Nethermind.Evm.Tracing.IBlockTracer,System.Threading.CancellationToken,System.String"
                : "Nethermind.Core.Block,Nethermind.Consensus.Processing.ProcessingOptions,Nethermind.Evm.Tracing.IBlockTracer,System.Threading.CancellationToken,System.String";
        if (method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) != owner || method.Name != name ||
            !method.Parameters.Select(parameter => parameter.RefKind.ToString()).SequenceEqual(refs) ||
            string.Join(",", method.Parameters.Select(parameter => TypeId(parameter.Type))) != expected ||
            TypeId(method.ReturnType) != (owner == BranchOwner || name == "ProcessBranch" ? "Nethermind.Core.Block[]" : "Nethermind.Core.Block"))
            throw new ExtractionException("Outer method FQN/ref kinds changed: " + name);
    }
    private static IInvocationOperation RequireInvocation(CSharpCompilation compilation, MethodDeclarationSyntax method, string name, string receiver, string owner, int arity)
    {
        InvocationExpressionSyntax syntax = method.DescendantNodes().OfType<InvocationExpressionSyntax>().Single(call =>
            call.Expression is MemberAccessExpressionSyntax access && access.Name.Identifier.ValueText == name && Canonical(access.Expression) == receiver);
        if (compilation.GetSemanticModel(method.SyntaxTree).GetOperation(syntax) is not IInvocationOperation operation ||
            operation.TargetMethod.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) != owner || operation.TargetMethod.Parameters.Length != arity ||
            operation.Instance is not IFieldReferenceOperation field || field.Field.Name != receiver)
            throw new ExtractionException(Diagnostic("target." + name));
        return operation;
    }
    private static ControlFlowGraph Graph(SemanticModel model, MethodDeclarationSyntax method) => model.GetOperation(method) is IMethodBodyOperation body
        ? ControlFlowGraph.Create(body) : throw new ExtractionException("Outer method has no typed body.");
    private static IEnumerable<IOperation> Walk(IOperation operation)
    {
        yield return operation;
        foreach (IOperation child in operation.ChildOperations) foreach (IOperation descendant in Walk(child)) yield return descendant;
    }
    private static IEnumerable<IOperation> Walk(BasicBlock block) => block.Operations.SelectMany(Walk).Concat(block.BranchValue is null ? [] : Walk(block.BranchValue));
    private static Site Anchor(SemanticModel model, ControlFlowGraph graph, MethodDeclarationSyntax owner, SyntaxNode syntax, string id)
    {
        string ownerId = MethodId(model.GetDeclaredSymbol(owner)!);
        BasicBlock[] blocks = graph.Blocks.Where(block => block.IsReachable && Walk(block).Any(operation => syntax.Span.Contains(operation.Syntax.Span))).ToArray();
        if (blocks.Length == 0 && syntax.Ancestors().OfType<LocalFunctionStatementSyntax>().FirstOrDefault() is { } local)
        {
            IMethodSymbol symbol = model.GetDeclaredSymbol(local)!;
            ownerId = MethodId(symbol);
            graph = graph.GetLocalFunctionControlFlowGraph(symbol);
            blocks = graph.Blocks.Where(block => block.IsReachable && Walk(block).Any(operation => syntax.Span.Contains(operation.Syntax.Span))).ToArray();
        }
        if (blocks.Length == 0) throw new ExtractionException("Outer anchor lacks reachable CFG evidence: " + id);
        static string Symbol(IOperation operation) => operation switch
        {
            IInvocationOperation call => MethodId(call.TargetMethod.ReducedFrom ?? call.TargetMethod),
            IObjectCreationOperation creation when creation.Constructor is not null => MethodId(creation.Constructor),
            IPropertyReferenceOperation property => property.Property.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "." +
                property.Property.MetadataName + ":" + property.Property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            IFieldReferenceOperation field => field.Field.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "." +
                field.Field.Name + ":" + field.Field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ILocalReferenceOperation local => local.Local.Name + ":" + local.Local.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            IParameterReferenceOperation parameter => parameter.Parameter.Name + ":" + parameter.Parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            IVariableDeclaratorOperation variable => variable.Symbol.Name + ":" + variable.Symbol.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            _ => "",
        };
        string target = string.Join("|", blocks.SelectMany(Walk).Where(operation => syntax.Span.Contains(operation.Syntax.Span)).Select(Symbol)
            .Where(value => value.Length != 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        if (string.IsNullOrWhiteSpace(target)) throw new ExtractionException("Outer anchor lacks an exact symbol target: " + id);
        StatementSyntax? statement = syntax as StatementSyntax ?? syntax.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
        DataFlowAnalysis? data = statement is null ? null : model.AnalyzeDataFlow(statement);
        if (data?.Succeeded != true) throw new ExtractionException("Outer anchor dataflow failed: " + id);
        static string[] Symbols(IEnumerable<ISymbol> values) => values.Select(value => value.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Order(StringComparer.Ordinal).ToArray();
        return new(id, syntax.SyntaxTree.FilePath, ownerId, target, Canonical(syntax), syntax.SpanStart,
            blocks.Select(block => block.Ordinal).ToArray(), syntax.Ancestors().TakeWhile(node => node != owner).OfType<IfStatementSyntax>()
                .Select(guard => (guard.Statement.Span.Contains(syntax.Span) ? "true:" : "false:") + Canonical(guard.Condition)).ToArray(), Symbols(data.ReadInside), Symbols(data.WrittenInside));
    }
    private static Flow FlowOf(string owner, ControlFlowGraph graph) => new(owner,
        graph.Blocks.Select(block => $"{block.Ordinal}:{block.Kind}:{block.IsReachable}:{block.EnclosingRegion.Kind}").ToArray(),
        graph.Blocks.SelectMany(block => new[] { block.FallThroughSuccessor, block.ConditionalSuccessor }.Where(edge => edge is not null)
            .Select(edge => $"{block.Ordinal}>{edge!.Destination?.Ordinal}:{edge.Semantics}:{block.ConditionKind}")).ToArray());
    private static string MethodId(IMethodSymbol method) => method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "." + method.MetadataName + "(" +
        string.Join(",", method.Parameters.Select(parameter => parameter.RefKind + ":" + parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))) + "):" +
        method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + (method.IsStatic ? ":static" : ":instance");
    private static string Canonical(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(token => token.Text));
    private static void RequireTemplate(SyntaxNode source, string resource)
    {
        using Stream stream = typeof(Extractor).Assembly.GetManifestResourceStream("Nethermind.Evm.Lean.BlockProcessorExtractor.Admission." + resource)
            ?? throw new ExtractionException("Missing reviewed outer admission: " + resource);
        using StreamReader reader = new(stream);
        if (Canonical(source) != Canonical(CSharpSyntaxTree.ParseText(reader.ReadToEnd(), new CSharpParseOptions(LanguageVersion.CSharp14)).GetRoot()))
            throw new ExtractionException(Diagnostic("complete." + resource));
    }

    internal static byte[] Serialize<T>(T value) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json) + "\n");
    private static T ReadStrict<T>(byte[] bytes)
    {
        try
        {
            using JsonDocument raw = JsonDocument.Parse(bytes);
            static void Visit(JsonElement value)
            {
                if (value.ValueKind == JsonValueKind.Object)
                {
                    HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
                    foreach (JsonProperty property in value.EnumerateObject())
                    {
                        if (!seen.Add(property.Name)) throw new ExtractionException("Duplicate/aliased outer JSON field.");
                        Visit(property.Value);
                    }
                }
                else if (value.ValueKind == JsonValueKind.Array) foreach (JsonElement child in value.EnumerateArray()) Visit(child);
                else if (value.ValueKind == JsonValueKind.Null) throw new ExtractionException("Null outer JSON field.");
            }
            Visit(raw.RootElement);
            return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new ExtractionException("Null outer document.");
        }
        catch (JsonException exception) { throw new ExtractionException("Invalid outer JSON: " + exception.Message); }
    }
    internal static Document ReadDocument(byte[] bytes)
    {
        Document document = ReadStrict<Document>(bytes);
        Validate(document);
        return document;
    }
    private static void Validate(Document document, bool draft = false)
    {
        if (document.Name is not (Finite or Publication) || document.SchemaVersion != 1 || document.ExtractorVersion != Version ||
            document.AcceptanceState != (draft ? "static-draft" : Accepted) || document.Sources is null || document.Dependencies is null ||
            document.Sites is null || document.ControlFlows is null || document.Operations is null || document.Exclusions is null ||
            document.LoopIncrement != 1 || document.ReadOnlyMask != 65 || document.DoNotUpdateHeadMask != 64 || document.MarkProcessedMask != 128)
            throw new ExtractionException("Outer IR header/status/option domain changed.");
        foreach (Identity identity in document.Sources.Concat(document.Dependencies))
            if (identity is null || string.IsNullOrWhiteSpace(identity.Path) || !IsHash(identity.Sha256)) throw new ExtractionException("Invalid outer identity.");
        if (!document.Sources.Select(identity => identity.Path).SequenceEqual(SourcePins.Select(identity => identity.Path)) ||
            (!draft && !document.Sources.SequenceEqual(SourcePins)) ||
            !document.Dependencies.Select(identity => identity.Path).SequenceEqual(draft ? [] : DependencyPaths(document.Name)) ||
            document.SourceClosureSha256 != Closure(document.Sources, document.Dependencies) ||
            !document.Operations.SequenceEqual(document.Name == Finite ? FiniteOps : PublicationOps) || !document.Exclusions.SequenceEqual(Exclusions) ||
            document.ControlFlows.Length != 6 || !document.Sites.Select(site => site?.Id).SequenceEqual(Rules.Select(rule => rule.Id)))
            throw new ExtractionException("Outer IR exact source/dependency/operation roster changed.");
        foreach (Site site in document.Sites)
        {
            if (site is null || string.IsNullOrWhiteSpace(site.Target) || string.IsNullOrWhiteSpace(site.Owner) || string.IsNullOrWhiteSpace(site.Path) ||
                string.IsNullOrWhiteSpace(site.Syntax) || site.Position < 0 || site.CfgBlocks is not { Length: > 0 } || site.CfgBlocks.Any(block => block < 0) ||
                site.Guards is null || site.Reads is null || site.Writes is null || site.Guards.Concat(site.Reads).Concat(site.Writes).Any(string.IsNullOrWhiteSpace))
                throw new ExtractionException("Outer IR invalid site/target/CFG/dataflow evidence.");
        }
        if (document.ControlFlows.Any(flow => flow is null || string.IsNullOrWhiteSpace(flow.Owner) || flow.Blocks is not { Length: > 0 } ||
            flow.Edges is not { Length: > 0 } || flow.Blocks.Concat(flow.Edges).Any(string.IsNullOrWhiteSpace)) ||
            document.ControlFlows.Select(flow => flow.Owner).Distinct(StringComparer.Ordinal).Count() != document.ControlFlows.Length)
            throw new ExtractionException("Outer IR invalid complete CFG roster.");
    }
    internal static byte[] Emit(string root, Document document)
    {
        ValidateDependencies(root, document);
        string lean = File.ReadAllText(Resolve(root, Package + "/" + document.Name + ".lean.template")).Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("{{IR_SHA256}}", Hash(Serialize(document)), StringComparison.Ordinal)
            .Replace("{{SOURCE_SHA256}}", document.SourceClosureSha256, StringComparison.Ordinal)
            .Replace("{{LOOP_INCREMENT}}", document.LoopIncrement.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{READ_ONLY_MASK}}", document.ReadOnlyMask.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{HEAD_MASK}}", document.DoNotUpdateHeadMask.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{MARK_MASK}}", document.MarkProcessedMask.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{OPERATIONS}}", string.Join(", ", document.Operations.Select(operation => "." + (operation == "return" ? "«return»" : operation))), StringComparison.Ordinal);
        if (lean.Contains("{{", StringComparison.Ordinal) || lean.Contains("sorry", StringComparison.Ordinal) || lean.Contains("axiom ", StringComparison.Ordinal) ||
            lean.Contains("theorem ", StringComparison.Ordinal) || lean.Contains("import BlockProcessorExtractor.Specification", StringComparison.Ordinal))
            throw new ExtractionException("Outer generated kernel contains proof/reference content or unresolved markers.");
        return Encoding.UTF8.GetBytes(lean);
    }
    private static Manifest CreateManifest(Document document, byte[] ir, byte[] lean) => new(1, Version, document.Name, Accepted, document.SourceClosureSha256,
        new(document.Name + ".ir.json", Hash(ir)), new(document.Name + ".lean", Hash(lean)), document.Sources, document.Dependencies);
    private static string Closure(Identity[] sources, Identity[] dependencies) => Hash(Serialize(new { sources, dependencies }));
    private static Identity Identify(string root, string path) => new(path, Hash(File.ReadAllBytes(Resolve(root, path))));
    private static string Resolve(string root, string path)
    {
        string full = Path.GetFullPath(Path.Combine(root, path));
        if (Path.IsPathRooted(path) || !full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ExtractionException("Outer artifact path escapes the repository.");
        return full;
    }
    private static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(value => value is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
