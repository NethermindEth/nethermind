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
using PublicationValidation = Nethermind.Evm.Lean.SequentialBlockPostTransactionFinalizationExtractor.ProcessOneValidatedPublicationExtractor;
using PublicationValidationException = Nethermind.Evm.Lean.SequentialBlockPostTransactionFinalizationExtractor.ExtractionException;

namespace Nethermind.Evm.Lean.BlockProcessorExtractor;

/// <summary>Extracts the owned-scope, non-retry normal branch suffix as a separate operational kernel.</summary>
internal static partial class BranchAcceptedIterationExtractor
{
    internal const string Name = "BranchAcceptedIteration";
    internal const string Package = "tools/Evm/Lean/BlockProcessorExtractor";
    internal const string PublicationPackage = "tools/Evm/Lean/SequentialBlockPostTransactionFinalizationExtractor";
    internal const string Publication = PublicationPackage + "/Generated/ProcessOneValidatedPublication";
    internal const string Inventory = "tools/Evm/Lean/ReceiptTerminalFoldExtractor/COMPILER_REFERENCE_PINS.json";
    private const string Accepted = "source-admitted";
    private const int SchemaVersion = 2;
    private const string Version = "1.0.0";
    private const string BranchType = "global::Nethermind.Consensus.Processing.BranchProcessor";
    private const string ProcessKey = BranchType + ".Process(Nethermind.Core.BlockHeader,System.Collections.Generic.IReadOnlyList<Nethermind.Core.Block>,Nethermind.Consensus.Processing.ProcessingOptions,Nethermind.Evm.Tracing.IBlockTracer,System.Threading.CancellationToken)";
    private const string WorldPath = "src/Nethermind/Nethermind.State/WorldState.cs";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<Op>(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    internal enum Op
    {
        Cancel, AssignSlot, Inclusion, QueueClear, WaitPrewarm, CommitTree, CountSuccess,
        BlockEvent, CheckpointDispose, CheckpointReopen, NextBase, ClearPrefetch, Reset,
        ScheduleHashes, Unsubscribe, DisposeFinally, CompletionEvent, Return,
    }

    internal sealed record Identity(string Path, string Sha256);
    internal sealed record Site(string Id, string Path, string Owner, string Target, string Syntax,
        int Position, int CfgBlock, string Region, string[] TrueGuards, string[] Reads, string[] Writes);
    internal sealed record Flow(string Owner, string[] Blocks, string[] Edges);
    internal sealed record Document(int SchemaVersion, string ExtractorVersion, string AcceptanceState, string SourceClosureSha256,
        Identity[] Sources, Identity[] Dependencies, CompilerClosure CompilerClosure, Site[] Sites, Flow[] ControlFlows, Op[] Program,
        int CheckpointModulus, string[] Exclusions);
    internal sealed record Manifest(int SchemaVersion, string ExtractorVersion, string AcceptanceState, string SourceClosureSha256,
        Identity Ir, Identity Lean, Identity[] Sources, Identity[] Dependencies, CompilerClosure CompilerClosure);

    private static readonly Identity[] Pins =
    [
        new(Extractor.BranchPath, "451ad7a9cbb283351519623dcf358e7d3efdb69db2a0e5a452614cbe1d3dc369"),
        new(WorldPath, "2dec74bcc5748a1850d1bed1e8fe0221aed8c904bf2ba0c0827e76e603a71f18"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BranchProcessingCompletedEventArgs.cs", "f0d3fbeac9c35f442d301d2044d903988cdfb726c2f486f5a29b288fc67d283b"),
        new("src/Nethermind/Nethermind.Consensus/Processing/IBranchProcessor.cs", "fc9ecd1b27f22c9b0861615e6c4f0c24f9430ae759e9c040c9e989d9249a0b20"),
        new("src/Nethermind/Nethermind.Core/Extensions/CancellationTokenExtensions.cs", "7811bd704199aaae97c164a54b25f77a1613f716fe298c2baa2b4ac78d3d4fe0"),
    ];
    private static readonly Op[] Program = Enum.GetValues<Op>();
    private static readonly string[] Exclusions =
    [
        "BAL retry, parallel execution, rejected ProcessOne and externally owned genesis scopes",
        "preloop scope acquisition, branch selection, and outer chain-head publication",
        "exception unwinding, callback interleavings, rollback, trie semantics and database durability",
        "completed cache-clear continuations, prefetch and background hash execution",
        "finite multi-block fold: only one admitted iteration and its terminal normal finally are extracted",
    ];

    internal static void Extract(string root, string output)
    {
        Document document = Audit(root);
        byte[] ir = Serialize(document);
        Document roundTrip = ReadDocument(ir);
        byte[] lean = Emit(root, roundTrip);
        Manifest manifest = new(SchemaVersion, Version, Accepted, document.SourceClosureSha256,
            new(Name + ".ir.json", Hash(ir)), new(Name + ".lean", Hash(lean)), document.Sources, document.Dependencies, document.CompilerClosure);
        string destination = Path.GetFullPath(output);
        string production = Path.GetFullPath(Path.Combine(root, "src"));
        if (destination.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) ||
            destination.Equals(production, StringComparison.OrdinalIgnoreCase) ||
            destination.StartsWith(production + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ExtractionException("Branch output cannot target production sources or the workspace root.");
        Directory.CreateDirectory(destination);
        AtomicWrite(Path.Combine(destination, Name + ".ir.json"), ir);
        AtomicWrite(Path.Combine(destination, Name + ".lean"), lean);
        AtomicWrite(Path.Combine(destination, Name + ".source-manifest.json"), Serialize(manifest));
    }

    internal static void Check(string root, string output)
    {
        Document expected = Audit(root);
        byte[] ir = File.ReadAllBytes(Path.Combine(output, Name + ".ir.json"));
        Document actual = ReadDocument(ir);
        if (!Serialize(actual).AsSpan().SequenceEqual(Serialize(expected)) || !ir.AsSpan().SequenceEqual(Serialize(actual)))
            throw new ExtractionException("Branch IR is not the canonical freshly source-bound document.");
        byte[] lean = Emit(root, actual);
        if (!lean.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(output, Name + ".lean"))))
            throw new ExtractionException("Branch Lean differs from the validated serialized IR.");
        Manifest manifest = new(SchemaVersion, Version, Accepted, actual.SourceClosureSha256,
            new(Name + ".ir.json", Hash(ir)), new(Name + ".lean", Hash(lean)), actual.Sources, actual.Dependencies, actual.CompilerClosure);
        if (!Serialize(manifest).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(output, Name + ".source-manifest.json"))))
            throw new ExtractionException("Branch manifest lineage changed.");
    }

    internal static Document AuditSourceForTest(string root, IReadOnlyDictionary<string, string>? overrides = null) =>
        Audit(root, overrides, enforcePins: false, sourceOnly: true);

    internal static void RequireCompilationForTest(string root, IReadOnlyDictionary<string, string> overrides)
    {
        (Dictionary<string, SyntaxTree> trees, _) = ReadSourceTrees(root, overrides, enforcePins: false);
        Compile(root, trees.Values);
    }

    internal static string AdmissionDiagnostic(string anchor) => "Branch source admission rejected anchor: " + anchor + ".";

    internal static (Dictionary<string, SyntaxTree> Trees, List<Identity> Sources) ReadSourceTrees(
        string root, IReadOnlyDictionary<string, string>? overrides, bool enforcePins)
    {
        using JsonDocument publicationPins = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root,
            PublicationPackage + "/PROCESS_ONE_PUBLICATION_SOURCE_PINS.json")));
        Identity[] publicationSources = publicationPins.RootElement.GetProperty("sources").EnumerateArray()
            .Select(item => new Identity(item.GetProperty("path").GetString()!, item.GetProperty("sha256").GetString()!)).ToArray();
        if (!publicationSources.Select(source => source.Path).SequenceEqual(PublicationSourcePaths, StringComparer.Ordinal))
            throw new ExtractionException("Branch publication source roster changed.");
        Identity[] pins = Pins.Concat(publicationSources).DistinctBy(item => item.Path).ToArray();
        Dictionary<string, SyntaxTree> trees = new(StringComparer.Ordinal);
        List<Identity> sources = [];
        foreach (Identity pin in pins)
        {
            string text = overrides is not null && overrides.TryGetValue(pin.Path, out string? changed)
                ? changed : File.ReadAllText(Path.Combine(root, pin.Path));
            byte[] bytes = overrides is not null && overrides.ContainsKey(pin.Path)
                ? Encoding.UTF8.GetBytes(text) : File.ReadAllBytes(Path.Combine(root, pin.Path));
            if (enforcePins && Hash(bytes) != pin.Sha256) throw new ExtractionException("Branch source pin changed: " + pin.Path);
            SyntaxTree tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.CSharp14), pin.Path);
            if (tree.GetRoot().DescendantTrivia().Any(trivia => trivia.IsDirective) && pin.Path == Extractor.BranchPath)
                throw new ExtractionException("Branch preprocessor directives are not admitted.");
            trees.Add(pin.Path, tree);
            sources.Add(new(pin.Path, Hash(bytes)));
        }
        return (trees, sources);
    }

    private static Document Audit(string root, IReadOnlyDictionary<string, string>? overrides = null, bool enforcePins = true, bool sourceOnly = false)
    {
        root = Path.GetFullPath(root);
        Identity[] upstream = sourceOnly
            ? ReadPublicationIdentities(root)
            : ValidatePublication(root);
        (Dictionary<string, SyntaxTree> trees, List<Identity> sources) = ReadSourceTrees(root, overrides, enforcePins);
        CompilerClosure compilerClosure = ReadCompilerClosure(root);
        CSharpCompilation compilation = Compile(root, trees.Values, compilerClosure);
        SyntaxNode branch = trees[Extractor.BranchPath].GetRoot();
        ValidateCallableShapes(branch, compilation.GetSemanticModel(trees[Extractor.BranchPath]));
        MethodDeclarationSyntax process = Method(branch, "BranchProcessor", "Process", 5);
        SemanticModel model = compilation.GetSemanticModel(process.SyntaxTree);
        if (MethodId(model.GetDeclaredSymbol(process)!) != ProcessKey)
            throw new ExtractionException("Branch Process FQN changed.");
        VariableDeclaratorSyntax inclusionGuard = process.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(variable => variable.Identifier.ValueText == "checkInclusionList");
        if (Canonical(inclusionGuard.Initializer!.Value) != "!options.ContainsFlag(ProcessingOptions.NoValidation)")
            throw new ExtractionException(AdmissionDiagnostic("inclusionGuard"));
        ControlFlowGraph graph = Graph(model, process);
        ForStatementSyntax loop = process.DescendantNodes().OfType<ForStatementSyntax>().Single();
        BlockSyntax body = loop.Statement as BlockSyntax ?? throw new ExtractionException("Branch loop body changed.");
        TryStatementSyntax attempt = body.Statements.OfType<TryStatementSyntax>().Single();
        InvocationExpressionSyntax accepted = attempt.Block.DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        RequireCall(model, accepted, "global::Nethermind.Consensus.Processing.IBlockProcessor", "ProcessOne", "blockProcessor", 5);
        if (Canonical(attempt.Block) != "{(processedBlock,receipts)=blockProcessor.ProcessOne(suggestedBlock,blockOptions,blockTracer,spec,token);}")
            throw new ExtractionException("Accepted ProcessOne tuple/predecessor changed.");

        List<Site> sites = [Anchor(model, graph, process, "acceptedPublication", accepted)];
        StatementSyntax[] suffix = body.Statements.Skip(body.Statements.IndexOf(attempt) + 1).ToArray();
        string[] expectedSuffix =
        [
            "CancellationTokenExtensions.CancelDisposeAndClear(refbackgroundCancellation);", "processedBlocks[i]=processedBlock;",
            "boolinclusionListSatisfied=!checkInclusionList||inclusionListSatisfactionChecker.IsSatisfied(processedBlock,suggestedBlock,stateProvider);",
            "processedBlock.IsInclusionListSatisfied=inclusionListSatisfied;", "suggestedBlock.IsInclusionListSatisfied=inclusionListSatisfied;",
            "QueueClearCaches(preWarmTask);", "WaitAndClear(refpreWarmTask);", "PreCommitBlock(suggestedBlock.Header);",
            "processedBlocksCount=i+1;", "if(notReadOnly){BlockProcessed?.Invoke(this,newBlockProcessedEventArgs(processedBlock,receipts));}",
            "boolisFirstInBatch=i==0;", "boolisLastInBatch=i==blocksCount-1;", "boolisNotAtTheEdge=!isFirstInBatch&&!isLastInBatch;",
            "boolisCommitPoint=i%MaxUncommittedBlocks==0&&isNotAtTheEdge;",
            null!, "preBlockBaseBlock=processedBlock.Header;", "prefetchBlockhash=null;", "stateProvider.Reset();",
            "if(suggestedBlock.Transactions.Length>0){TxHashCalculator.CalculateInBackground(suggestedBlock);}",
        ];
        string[] ids = ["cancel", "assignSlot", "inclusion", "assignProcessedSignal", "assignSuggestedSignal", "queueClear", "waitPrewarm", "commitTree", "countSuccess", "blockEvent", "firstGuard", "lastGuard", "edgeGuard", "checkpointGuard", "checkpoint", "nextBase", "clearPrefetch", "reset", "scheduleHashes"];
        if (suffix.Length != expectedSuffix.Length) throw new ExtractionException("Branch normal suffix statement roster changed.");
        for (int i = 0; i < suffix.Length; i++)
            if (expectedSuffix[i] is not null && Canonical(suffix[i]) != expectedSuffix[i])
                throw new ExtractionException(AdmissionDiagnostic(ids[i]));
        IfStatementSyntax checkpoint = suffix[14] as IfStatementSyntax ?? throw new ExtractionException("Missing checkpoint.");
        if (Canonical(checkpoint.Condition) != "isCommitPoint&&notReadOnly") throw new ExtractionException("Checkpoint true edge changed.");
        StatementSyntax[] checkpointBody = ((BlockSyntax)checkpoint.Statement).Statements.ToArray();
        if (checkpointBody.Length != 4) throw new ExtractionException(AdmissionDiagnostic("checkpoint"));
        string[] checkpointExpected = ["BlockHeaderpreviousBranchStateRoot=suggestedBlock.Header;", "worldStateCloser?.Dispose();", "worldStateCloser=stateProvider.BeginScope(previousBranchStateRoot);"];
        string[] checkpointIds = ["checkpointHeader", "checkpointDispose", "checkpointReopen"];
        for (int i = 0; i < checkpointExpected.Length; i++)
            if (Canonical(checkpointBody[i + 1]) != checkpointExpected[i])
                throw new ExtractionException(AdmissionDiagnostic(checkpointIds[i]));

        for (int i = 0; i < suffix.Length; i++) sites.Add(Anchor(model, graph, process, ids[i], suffix[i]));
        foreach (StatementSyntax statement in checkpointBody.Skip(2))
            sites.Add(Anchor(model, graph, process, statement == checkpointBody[2] ? "checkpointDispose" : "checkpointReopen", statement));
        RequireCall(model, FindCall(suffix[2], "IsSatisfied"), "global::Nethermind.Consensus.Processing.IInclusionListSatisfactionChecker", "IsSatisfied", "inclusionListSatisfactionChecker", 3);
        RequireCall(model, FindCall(suffix[17], "Reset"), "global::Nethermind.Evm.State.IWorldState", "Reset", "stateProvider", 1);
        RequireCall(model, FindCall(checkpointBody[3], "BeginScope"), "global::Nethermind.Evm.State.IWorldState", "BeginScope", "stateProvider", 1);

        TryStatementSyntax outer = process.Body!.Statements.OfType<TryStatementSyntax>().Single();
        TryStatementSyntax final = outer.Finally!.Block.Statements.OfType<TryStatementSyntax>().Single();
        if (Canonical(final.Block) != "{blockProcessor.TransactionsExecuted-=CancelBackgroundWork;worldStateCloser?.Dispose();}")
            throw new ExtractionException(AdmissionDiagnostic("finally.unsubscribeDispose"));
        if (Canonical(final.Finally!.Block) != "{if(blocksProcessingEventArgsisnotnull){BranchProcessingCompleted?.Invoke(this,newBranchProcessingCompletedEventArgs(blocksProcessingEventArgs.Blocks,processedBlocksCount,processingException));}}")
            throw new ExtractionException(AdmissionDiagnostic("finally.completion"));
        if (Canonical(outer.Block.Statements.Last()) != "returnprocessedBlocks;")
            throw new ExtractionException(AdmissionDiagnostic("return"));
        foreach ((string id, SyntaxNode node) in new (string, SyntaxNode)[]
            { ("unsubscribe", final.Block.Statements[0]), ("disposeFinally", final.Block.Statements[1]),
              ("completionEvent", final.Finally.Block.Statements[0]), ("return", outer.Block.Statements.OfType<ReturnStatementSyntax>().Single()) })
            sites.Add(Anchor(model, graph, process, id, node));
        List<Flow> flows = [FlowOf(model, process, graph)];
        foreach (string name in new[] { "PreCommitBlock", "QueueClearCaches", "WaitForCacheClear" })
        {
            MethodDeclarationSyntax method = Method(branch, "BranchProcessor", name, name == "WaitForCacheClear" ? 0 : 1);
            if (name == "PreCommitBlock")
            {
                InvocationExpressionSyntax[] calls = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Where(call => call.Expression is MemberAccessExpressionSyntax access && access.Name.Identifier.ValueText == "CommitTree").ToArray();
                if (calls.Length != 1 || Canonical(calls[0]) != "stateProvider.CommitTree(block.Number)")
                    throw new ExtractionException(AdmissionDiagnostic("helper.PreCommitBlock.commitTree"));
            }
            if (name == "QueueClearCaches" && Canonical(method.Body!) !=
                "{if(preWarmTaskisnotnull){_clearTask=preWarmTask.ContinueWith(_clearCaches,TaskContinuationOptions.ExecuteSynchronously);}elseif(preWarmerisnotnull){preWarmer.ClearCaches();_clearTask=Task.CompletedTask;}}")
                throw new ExtractionException(AdmissionDiagnostic("helper.QueueClearCaches"));
            ControlFlowGraph helperGraph = Graph(model, method);
            flows.Add(FlowOf(model, method, helperGraph));
            sites.Add(Anchor(model, helperGraph, method, "helper." + name, method.ExpressionBody?.Expression ?? (SyntaxNode)method.Body!));
            if (name == "PreCommitBlock") RequireCall(model, FindCall(method, "CommitTree"), "global::Nethermind.Evm.State.IWorldState", "CommitTree", "stateProvider", 1);
        }
        LocalFunctionStatementSyntax wait = process.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Single(node => node.Identifier.ValueText == "WaitAndClear");
        if (Canonical(wait.Body!) != "{task?.GetAwaiter().GetResult();task=null;}")
            throw new ExtractionException(AdmissionDiagnostic("helper.WaitAndClear"));
        SyntaxNode world = trees[WorldPath].GetRoot();
        SemanticModel worldModel = compilation.GetSemanticModel(trees[WorldPath]);
        foreach (string name in new[] { "CommitTree", "Reset", "BeginScope", "EndScope" })
        {
            MethodDeclarationSyntax method = Method(world, "WorldState", name, name == "EndScope" ? 0 : 1);
            ControlFlowGraph worldGraph = Graph(worldModel, method);
            flows.Add(FlowOf(worldModel, method, worldGraph));
            sites.Add(Anchor(worldModel, worldGraph, method, "external.WorldState." + name, method.Body!));
        }
        ValidateBranchTemplate(trees[Extractor.BranchPath]);
        Identity[] dependencies = upstream.Concat(OwnDependencyPaths.Select(path => Identify(root, path)))
            .Concat(compilerClosure.SupportSources).DistinctBy(identity => identity.Path).ToArray();
        string closure = Hash(Serialize(new { sources, dependencies, compilerClosure }));
        Document document = new(SchemaVersion, Version, sourceOnly ? "static-draft" : Accepted, closure,
            sources.ToArray(), dependencies, compilerClosure, sites.ToArray(), flows.ToArray(), Program, 64, Exclusions);
        Validate(document, allowSourceOnlyDraft: sourceOnly);
        return document;
    }

    internal static Identity[] ValidatePublication(string root, string? outputDirectory = null)
    {
        try
        {
            PublicationValidation.ValidateCheckedIn(root, outputDirectory);
        }
        catch (PublicationValidationException exception)
        {
            throw new ExtractionException("Branch upstream validation failed: " + exception.Message);
        }

        return ReadPublicationIdentities(root, outputDirectory);
    }

    private static Identity[] ReadPublicationIdentities(string root, string? outputDirectory = null)
    {
        string directory = outputDirectory ?? Path.Combine(root, PublicationPackage, "Generated");
        string manifestPath = Publication + ".source-manifest.json";
        using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, Path.GetFileName(manifestPath))));
        JsonElement manifest = json.RootElement;
        List<Identity> identities = [];
        foreach (string extension in new[] { ".source-manifest.json", ".ir.json", ".lean" })
        {
            string path = Publication + extension;
            identities.Add(new(path, Hash(File.ReadAllBytes(Path.Combine(directory, Path.GetFileName(path))))));
        }
        ValidateAcceptedPublicationPins(root, identities);
        foreach (JsonElement entry in manifest.GetProperty("sources").EnumerateArray().Concat(manifest.GetProperty("dependencies").EnumerateArray()))
            identities.Add(Identify(root, entry.GetProperty("path").GetString()!));
        identities.Add(Identify(root, PublicationPackage + "/Refinement/ProcessOneValidatedPublication.lean"));
        identities.Add(Identify(root, PublicationPackage + "/Specification/ProcessOneValidatedPublication.lean"));
        return identities.DistinctBy(item => item.Path).ToArray();
    }

    private static void ValidateCallableShapes(SyntaxNode branch, SemanticModel model)
    {
        foreach (MethodDeclarationSyntax method in branch.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            IMethodSymbol? symbol = model.GetDeclaredSymbol(method);
            bool conditional = false;
            for (IMethodSymbol? current = symbol; current is not null; current = current.OverriddenMethod)
                conditional |= current.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString(
                    SymbolDisplayFormat.FullyQualifiedFormat) == "global::System.Diagnostics.ConditionalAttribute");
            if (symbol is null || conditional || symbol.IsAsync || symbol.IsExtern ||
                symbol.PartialDefinitionPart is not null || symbol.PartialImplementationPart is not null ||
                method.Modifiers.Any(SyntaxKind.PartialKeyword) || method.Body is null && method.ExpressionBody is null ||
                method.DescendantNodes(node => node is not LocalFunctionStatementSyntax and not AnonymousFunctionExpressionSyntax)
                    .OfType<YieldStatementSyntax>().Any())
                throw new ExtractionException(AdmissionDiagnostic("callable." + method.Identifier.ValueText));
        }
    }

    private static void ValidateBranchTemplate(SyntaxTree tree)
    {
        using Stream stream = typeof(Extractor).Assembly.GetManifestResourceStream(
            "Nethermind.Evm.Lean.BlockProcessorExtractor.Admission.BranchAcceptedIteration.cs.txt")!;
        using StreamReader reader = new(stream);
        SyntaxNode expected = CSharpSyntaxTree.ParseText(reader.ReadToEnd(), new CSharpParseOptions(LanguageVersion.CSharp14)).GetRoot();
        if (Canonical(tree.GetRoot()) != Canonical(expected))
            throw new ExtractionException("Unadmitted complete branch syntax/ownership/initializer closure.");
        foreach (MethodDeclarationSyntax method in expected.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            string owner = method.Ancestors().OfType<ClassDeclarationSyntax>().First().Identifier.ValueText;
            MethodDeclarationSyntax actual = Method(tree.GetRoot(), owner, method.Identifier.ValueText, method.ParameterList.Parameters.Count);
            if (Canonical(method) != Canonical(actual)) throw new ExtractionException("Unadmitted complete branch method: " + method.Identifier);
        }
        string[] roster = ["PreCommitBlock", "Process", "PreWarmTransactions", "WaitForCacheClear", "QueueClearCaches", "CalculateInBackground", "Execute"];
        if (!tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Select(method => method.Identifier.ValueText).SequenceEqual(roster))
            throw new ExtractionException("Competing branch declaration.");
        VariableDeclaratorSyntax modulus = tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>().Single(v => v.Identifier.ValueText == "MaxUncommittedBlocks");
        if (Canonical(modulus.Initializer!.Value) != "64") throw new ExtractionException("Checkpoint modulus requires explicit review.");
    }

    private static MethodDeclarationSyntax Method(SyntaxNode root, string owner, string name, int arity) =>
        root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == name &&
            method.ParameterList.Parameters.Count == arity && method.Ancestors().OfType<ClassDeclarationSyntax>().First().Identifier.ValueText == owner);
    private static string Canonical(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(token => token.Text));
    private static string MethodId(IMethodSymbol method) => method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "." + method.Name + "(" +
        string.Join(",", method.Parameters.Select(parameter => parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat
            .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers)).Replace("global::", "", StringComparison.Ordinal) + (parameter.RefKind == RefKind.None ? "" : "&"))) + ")";
    private static InvocationExpressionSyntax FindCall(SyntaxNode node, string name) => node.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
        .Single(call => call.Expression is MemberAccessExpressionSyntax access && access.Name.Identifier.ValueText == name);
    private static void RequireCall(SemanticModel model, InvocationExpressionSyntax syntax, string owner, string name, string receiver, int arity)
    {
        if (model.GetOperation(syntax) is not IInvocationOperation call || call.TargetMethod.Name != name ||
            call.TargetMethod.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) != owner ||
            call.TargetMethod.Parameters.Length != arity || call.Instance is not IParameterReferenceOperation parameter || parameter.Parameter.Name != receiver)
            throw new ExtractionException("Branch call target or receiver changed: " + Canonical(syntax));
    }
    private static ControlFlowGraph Graph(SemanticModel model, MethodDeclarationSyntax method) =>
        model.GetOperation(method) is IMethodBodyOperation body ? ControlFlowGraph.Create(body) : throw new ExtractionException("Missing branch typed method body.");
    private static IEnumerable<IOperation> Operations(IOperation operation)
    {
        yield return operation;
        foreach (IOperation child in operation.ChildOperations)
            foreach (IOperation descendant in Operations(child)) yield return descendant;
    }
    private static IEnumerable<IOperation> Operations(BasicBlock block) => block.Operations.SelectMany(Operations)
        .Concat(block.BranchValue is null ? [] : Operations(block.BranchValue));
    private static Site Anchor(SemanticModel model, ControlFlowGraph graph, MethodDeclarationSyntax owner, string id, SyntaxNode syntax)
    {
        BasicBlock[] blocks = graph.Blocks.Where(block => block.IsReachable && Operations(block).Any(op => syntax.Span.Contains(op.Syntax.Span))).ToArray();
        if (blocks.Length == 0) throw new ExtractionException("Branch anchor has no reachable CFG membership: " + id);
        string Target(IOperation operation) => operation switch
        {
            IInvocationOperation invocation => MethodId(invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod),
            IPropertyReferenceOperation property => property.Property.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "." + property.Property.Name,
            IFieldReferenceOperation field => field.Field.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "." + field.Field.Name,
            IEventReferenceOperation eventReference => eventReference.Event.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "." + eventReference.Event.Name,
            ILocalReferenceOperation local => local.Local.Name + ":" + local.Local.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            IParameterReferenceOperation parameter => parameter.Parameter.Name + ":" + parameter.Parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            _ => "",
        };
        string target = string.Join("|", blocks.SelectMany(Operations).Where(operation => syntax.Span.Contains(operation.Syntax.Span))
            .Select(Target).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        if (target.Length == 0) throw new ExtractionException("Branch anchor has no typed symbol/operand identity: " + id);
        DataFlowAnalysis? dataflow = syntax is StatementSyntax statement ? model.AnalyzeDataFlow(statement) : null;
        if (syntax is StatementSyntax && dataflow?.Succeeded != true) throw new ExtractionException("Branch anchor dataflow failed: " + id);
        string[] Guards() => syntax.Ancestors().TakeWhile(node => node != owner).OfType<IfStatementSyntax>()
            .Select(guard => (guard.Statement.Span.Contains(syntax.Span) ? "true:" : "false:") + Canonical(guard.Condition)).ToArray();
        string[] Symbols(IEnumerable<ISymbol>? values) => values?.Select(value => value.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Order(StringComparer.Ordinal).ToArray() ?? [];
        return new(id, syntax.SyntaxTree.FilePath, MethodId(model.GetDeclaredSymbol(owner)!), target, Canonical(syntax), syntax.SpanStart,
            blocks[0].Ordinal, blocks[0].EnclosingRegion.Kind.ToString(), Guards(), Symbols(dataflow is null ? null : dataflow.ReadInside), Symbols(dataflow is null ? null : dataflow.WrittenInside));
    }
    private static Flow FlowOf(SemanticModel model, MethodDeclarationSyntax method, ControlFlowGraph graph) => new(MethodId(model.GetDeclaredSymbol(method)!),
        graph.Blocks.Select(block => $"{block.Ordinal}:{block.Kind}:{block.IsReachable}:{block.EnclosingRegion.Kind}").ToArray(),
        graph.Blocks.SelectMany(block => new[] { block.FallThroughSuccessor, block.ConditionalSuccessor }.Where(edge => edge is not null)
            .Select(edge => $"{block.Ordinal}>{edge!.Destination?.Ordinal}:{edge.Semantics}:{block.ConditionKind}" )).ToArray());

    internal static byte[] Serialize<T>(T value) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json) + "\n");
    internal static Document ReadDocument(byte[] bytes)
    {
        try
        {
            using JsonDocument raw = JsonDocument.Parse(bytes);
            RejectDuplicates(raw.RootElement);
            if (raw.RootElement.ValueKind == JsonValueKind.Object &&
                raw.RootElement.TryGetProperty("program", out JsonElement program) && program.ValueKind == JsonValueKind.Array)
            {
                string[] names = Program.Select(operation => JsonNamingPolicy.CamelCase.ConvertName(operation.ToString())).ToArray();
                if (program.EnumerateArray().Any(operation => operation.ValueKind != JsonValueKind.String ||
                        !names.Contains(operation.GetString(), StringComparer.Ordinal)))
                    throw new ExtractionException("Branch operation names are not canonical.");
            }
            Document document = JsonSerializer.Deserialize<Document>(bytes, Json) ?? throw new ExtractionException("Null branch IR.");
            Validate(document);
            return document;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NullReferenceException)
        {
            throw new ExtractionException("Invalid branch IR: " + exception.Message);
        }
    }
    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ExtractionException("Duplicate/aliased branch JSON field.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (JsonElement item in element.EnumerateArray()) RejectDuplicates(item);
    }
    private static void Validate(Document document, bool allowSourceOnlyDraft = false)
    {
        if (document.SchemaVersion != SchemaVersion || document.ExtractorVersion != Version || (document.AcceptanceState != Accepted && !(allowSourceOnlyDraft && document.AcceptanceState == "static-draft")) || !IsHash(document.SourceClosureSha256) ||
            document.Sources is null || document.Dependencies is null || document.Sites is null || document.ControlFlows is null ||
            document.Program is null || document.Exclusions is null || document.CheckpointModulus != 64 ||
            !document.Program.SequenceEqual(Program) || !document.Exclusions.SequenceEqual(Exclusions) ||
            document.Sources.Length != 13 || document.Dependencies.Length < OwnDependencyPaths.Length || document.Sites.Length != 33 || document.ControlFlows.Length != 8)
            throw new ExtractionException("Branch IR header/closed roster changed.");
        ValidateCompilerClosure(document.CompilerClosure);
        if (!document.Sources.Select(identity => identity?.Path).SequenceEqual(Pins.Select(pin => pin.Path).Concat(PublicationSourcePaths)))
            throw new ExtractionException("Branch semantic source roster changed.");
        if (!allowSourceOnlyDraft && !document.Sources.SequenceEqual(Pins.Concat(PublicationSourceIdentities)))
            throw new ExtractionException("Branch semantic source identity changed.");
        if (!OwnDependencyPaths.All(path => document.Dependencies.Any(identity => identity?.Path == path)))
            throw new ExtractionException("Branch own semantic/proof dependency roster changed.");
        if (!document.Dependencies.Where(identity => identity?.Path.StartsWith(Package + "/", StringComparison.Ordinal) == true)
                .Select(identity => identity.Path).SequenceEqual(OwnDependencyPaths.Where(path => path.StartsWith(Package + "/", StringComparison.Ordinal))))
            throw new ExtractionException("Branch own semantic/proof dependency roster changed.");
        if (document.Dependencies.Length != 82 || Hash(Encoding.UTF8.GetBytes(
                string.Join('\n', document.Dependencies.Select(identity => identity?.Path)) + "\n")) !=
            "42578f9a20f831e1087c8ca6df95441ebe16074c2161873818188e14845d86b2")
            throw new ExtractionException("Branch complete dependency roster changed.");
        foreach (Identity identity in document.Sources.Concat(document.Dependencies))
            if (identity is null || string.IsNullOrWhiteSpace(identity.Path) || !IsHash(identity.Sha256)) throw new ExtractionException("Invalid branch identity.");
        if (document.Sources.Select(identity => identity.Path).Distinct(StringComparer.Ordinal).Count() != document.Sources.Length ||
            document.Dependencies.Select(identity => identity.Path).Distinct(StringComparer.Ordinal).Count() != document.Dependencies.Length)
            throw new ExtractionException("Duplicate branch source/dependency identity.");
        if (Hash(Serialize(new { sources = document.Sources, dependencies = document.Dependencies,
                compilerClosure = document.CompilerClosure })) != document.SourceClosureSha256)
            throw new ExtractionException("Branch source/dependency/compiler closure digest changed.");
        foreach (Site site in document.Sites)
            if (site is not null && string.IsNullOrWhiteSpace(site.Target))
                throw new ExtractionException("Branch IR site target is empty: " + site.Id + ".");
        if (document.Sites.Any(site => site is null || string.IsNullOrWhiteSpace(site.Id) || string.IsNullOrWhiteSpace(site.Owner) ||
            string.IsNullOrWhiteSpace(site.Path) || string.IsNullOrWhiteSpace(site.Region) || string.IsNullOrWhiteSpace(site.Syntax) || site.Position < 0 || site.CfgBlock < 0 ||
            site.TrueGuards is null || site.Reads is null || site.Writes is null || site.TrueGuards.Concat(site.Reads).Concat(site.Writes).Any(value => value is null)) ||
            document.Sites.Select(site => site.Id).Distinct(StringComparer.Ordinal).Count() != document.Sites.Length ||
            document.ControlFlows.Any(flow => flow is null || string.IsNullOrWhiteSpace(flow.Owner) || flow.Blocks is null || flow.Edges is null ||
                flow.Blocks.Concat(flow.Edges).Any(string.IsNullOrWhiteSpace)))
            throw new ExtractionException("Invalid branch CFG/dataflow evidence.");
    }
    internal static byte[] Emit(string root, Document document)
    {
        Validate(document);
        string template = File.ReadAllText(Path.Combine(root, Package + "/BranchKernel.lean.template")).Replace("\r\n", "\n", StringComparison.Ordinal);
        string lean = template.Replace("{{IR_SHA256}}", Hash(Serialize(document)), StringComparison.Ordinal)
            .Replace("{{SOURCE_SHA256}}", document.SourceClosureSha256, StringComparison.Ordinal)
            .Replace("{{PROGRAM}}", string.Join(", ", document.Program.Select(op => op == Op.Return ? ".«return»" : "." + JsonNamingPolicy.CamelCase.ConvertName(op.ToString()))), StringComparison.Ordinal)
            .Replace("{{MODULUS}}", document.CheckpointModulus.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        if (lean.Contains("{{", StringComparison.Ordinal) || lean.Contains("sorry", StringComparison.Ordinal) || lean.Contains("axiom ", StringComparison.Ordinal) || lean.Contains("theorem ", StringComparison.Ordinal))
            throw new ExtractionException("Branch generated template contains an unresolved marker or proof declaration.");
        return Encoding.UTF8.GetBytes(lean);
    }
    private static Identity Identify(string root, string path) => new(path, Hash(File.ReadAllBytes(Resolve(root, path))));
    private static string Resolve(string root, string path)
    {
        string full = Path.GetFullPath(Path.Combine(root, path));
        if (Path.IsPathRooted(path) || !full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ExtractionException("Branch dependency escapes repository.");
        return full;
    }
    private static bool IsHash(string? text) => text is { Length: 64 } && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
