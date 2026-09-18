// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.BlockProcessorExtractor;

internal sealed class ExtractionException(string message) : Exception(message);

internal static class Extractor
{
    internal const string ProcessingDirectory = "src/Nethermind/Nethermind.Consensus/Processing";
    internal const string BlockPath = ProcessingDirectory + "/BlockProcessor.cs";
    internal const string BranchPath = ProcessingDirectory + "/BranchProcessor.cs";
    internal const string ChainPath = ProcessingDirectory + "/BlockchainProcessor.cs";
    internal const string HeaderPath = "src/Nethermind/Nethermind.Core/BlockHeader.cs";
    internal static readonly string[] Paths = [BlockPath, BranchPath, ChainPath, HeaderPath];
    internal const string ArtifactName = "BlockProcessorControl";
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp14);
    private const string SourceIdentityAuthority =
        "Current source SHA-256 identities are authoritative; admission templates are reviewed syntax allowlists, not baseline revision identities.";
    private static readonly string[] ExpectedExternalObligations =
    [
        "Admission compares complete method syntax tokens with independently reviewable templates; event extraction follows the admitted source AST.",
        "Concrete standard BlockProcessor, BranchProcessor and BlockHeader runtime implementations, standard SystemContractHandler dispatch, and synchronous receipt selection are premises.",
        "Roslyn parsing and the extractor are trusted; no C# semantic-model, overload resolution, full C# operational semantics, CLR/JIT, or dependency-injection correctness theorem is supplied.",
        "Calls into DAO, beacon roots, historical blockhash, rewards, withdrawals, execution requests, hashing, BAL, tracers, events, scopes, storage and persistence remain external hooks.",
        "The abstract reference restores its complete BlockWorld on failure; production source only anchors DisposeAccountChanges, scope disposal/reopen and rethrow. Real world-state rollback and database durability are not proved.",
        "The source records both background receipt arms and finally observation; the Lean synchronous receipt projection does not prove background scheduling or parallel block execution.",
        "Header fields remain syntax projections. Reference Nat header fields do not prove C# constructor defaults, nullable values, deep copy, widths, aliasing or hash bytes.",
        "BlockchainProcessor invalid-block handling is anchored by its complete admitted ProcessBranch template; diagnostic replay, event failures, deletion and head update implementations remain external.",
    ];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    internal static void Extract(string repoRoot, string outputDirectory,
        IReadOnlyDictionary<string, string>? reviewedAdmissionTemplates = null)
    {
        string root = Path.GetFullPath(repoRoot);
        Source[] sources = new Source[Paths.Length];
        for (int i = 0; i < Paths.Length; i++) sources[i] = Read(root, Paths[i]);
        Dictionary<string, MethodDeclarationSyntax> methods = new(StringComparer.Ordinal);
        Dictionary<string, ExpressionSyntax> fieldInitializers = new(StringComparer.Ordinal);
        List<AdmissionIdentity> admissions = [];
        foreach (Source source in sources)
        {
            string? template = null;
            reviewedAdmissionTemplates?.TryGetValue(Path.GetFileNameWithoutExtension(source.Path), out template);
            Admit(source, methods, fieldInitializers, admissions, template);
        }
        ValidateAdmissionIdentities(admissions);
        RejectCompetingDeclarations(root, sources, methods);

        MethodDeclarationSyntax one = methods["BlockProcessor.ProcessOne/5"];
        MethodDeclarationSyntax block = methods["BlockProcessor.ProcessBlock/5"];
        MethodDeclarationSyntax branch = methods["BranchProcessor.Process/5"];
        MethodDeclarationSyntax chain = methods["BlockchainProcessor.Process/5"];
        List<Anchor> phases = [];
        foreach (InvocationExpressionSyntax call in Calls(one))
        {
            switch (Canonical(call))
            {
                case "ApplyDaoTransition(suggestedBlock)":
                    phases.Add(AnchorOf(sources[0], one, call, "daoTransition"));
                    break;
                case "ProcessBlock(block,blockTracer,options,spec,token)":
                    Dictionary<string, string> phaseCalls = new(StringComparer.Ordinal)
                    {
                        ["_systemContractHandler.StoreBeaconRoot(block,spec,NullTxTracer.Instance)"] = "beaconRootSystemCall",
                        ["_systemContractHandler.ApplyBlockhashStateChanges(header,spec)"] = "historicalBlockhashStateChange",
                        ["_blockTransactionsExecutor.ProcessTransactions(block,options,ReceiptsTracer,token)"] = "userTransactionFold",
                        ["ShouldCalculateReceiptsInBackground(receipts)"] = "blobGasReceiptRootAndBloom",
                        ["ApplyMinerRewards(block,blockTracer,spec)"] = "rewards",
                        ["_systemContractHandler.ProcessWithdrawals(block,spec)"] = "withdrawals",
                        ["_systemContractHandler.ProcessExecutionRequests(block,_stateProvider,receipts,spec)"] = "executionRequestsAndSystemCalls",
                        ["CommitStateAndStorageRoots(spec)"] = "storageAndStateRoots",
                        ["_balManager.SetBlockAccessList(block)"] = "blockAccessList",
                    };
                    phases.AddRange(SelectCalls(sources[0], block, phaseCalls));
                    break;
                case "ValidateProcessedBlock(suggestedBlock,options,block,receipts)":
                    phases.Add(AnchorOf(sources[0], one, call, "processedHeaderValidation"));
                    break;
            }
        }
        RequireCount(phases, 11, "ProcessOne phases");

        IfStatementSyntax receiptChoice = Single<IfStatementSyntax>(block, node =>
            Canonical(node.Condition) == "ShouldCalculateReceiptsInBackground(receipts)");
        List<Anchor> receiptEvents = SelectCalls(sources[0], block, new(StringComparer.Ordinal)
        {
            ["CalculateBlooms(receipts)"] = "receiptBloomsComputed",
            ["CalculateReceiptsRoot(receipts,spec,block)"] = "receiptsRootComputed",
        }, receiptChoice.Else!.Statement);
        AssignmentExpressionSyntax receiptRoot = Single<AssignmentExpressionSyntax>(receiptChoice.Else, node =>
            Canonical(node.Left) == "header.ReceiptsRoot");
        receiptEvents.Add(AnchorOf(sources[0], block, receiptRoot, "receiptsRootInstalled"));
        RequireCount(receiptEvents, 3, "synchronous receipt events");

        List<Anchor> blockEvents = SelectCalls(sources[0], block, new(StringComparer.Ordinal)
        {
            ["ReceiptsTracer.StartNewBlockTrace(block)"] = "traceStarted",
            ["_blockTransactionsExecutor.SetBlockExecutionContext(CreateBlockExecutionContext(block.Header,spec))"] = "executionContextSet",
            ["_balManager.Setup(block)"] = "balSetup",
            ["CommitState(spec)"] = "commitState",
            ["_systemContractHandler.ProcessExecutionRequests(block,_stateProvider,receipts,spec)"] = "executionRequests",
            ["ReceiptsTracer.EndBlockTrace(accumulateBlockBloom:bloomsAndReceiptsRootTaskisnull)"] = "traceEnded",
            ["CommitStateAndStorageRoots(spec)"] = "commitStorageRoots",
            ["SetAccountChanges(block)"] = "accountChanges",
            ["ComputeStateRoot(header)"] = "stateRoot",
            ["_balManager.SetBlockAccessList(block)"] = "balFinalized",
        });
        RequireCount(blockEvents, 12, "block control events");

        ForStatementSyntax loop = Single<ForStatementSyntax>(branch, node => Canonical(node.Condition!) == "i<blocksCount");
        List<Anchor> branchEvents = SelectCalls(sources[1], branch, new(StringComparer.Ordinal)
        {
            ["blockProcessor.ProcessOne(suggestedBlock,blockOptions,blockTracer,spec,token)"] = "processOne",
            ["inclusionListSatisfactionChecker.IsSatisfied(processedBlock,suggestedBlock,stateProvider)"] = "inclusionList",
            ["WaitAndClear(refpreWarmTask)"] = "waitPrewarm",
            ["PreCommitBlock(suggestedBlock.Header)"] = "commitTree",
            ["stateProvider.Reset()"] = "reset",
        }, loop, excludeCatch: true);
        RequireCount(branchEvents, 5, "synchronous per-block events");
        InvocationExpressionSyntax commit = Single<InvocationExpressionSyntax>(methods["BranchProcessor.PreCommitBlock/1"],
            node => Canonical(node) == "stateProvider.CommitTree(block.Number)");
        Anchor commitTarget = AnchorOf(sources[1], methods["BranchProcessor.PreCommitBlock/1"], commit, "commitTreeTarget");

        List<Anchor> finalization = [];
        foreach (SyntaxNode node in chain.DescendantNodes())
        {
            string? label = Canonical(node) switch
            {
                "lastProcessed.Header.TotalDifficulty=suggestedBlock.TotalDifficulty" when node is AssignmentExpressionSyntax => "totalDifficulty",
                "_blockTree.TryUpdateMainChain(suggestedBlock.Header,wereProcessed:true,preloadedBlocks:processingBranch.Blocks.AsSpan())" when node is InvocationExpressionSyntax => "mainChainUpdate",
                "_blockTree.MarkChainAsProcessed(processingBranch.Blocks)" when node is InvocationExpressionSyntax => "markProcessed",
                _ => null,
            };
            if (label is not null) finalization.Add(AnchorOf(sources[2], chain, node, label));
        }
        RequireCount(finalization, 3, "chain finalization events");

        List<Anchor> cleanup = [];
        foreach (MethodDeclarationSyntax method in new[] { one, methods["BlockProcessor.ValidateProcessedBlock/4"], branch })
        {
            Source source = method == branch ? sources[1] : sources[0];
            cleanup.AddRange(SelectCalls(source, method, new(StringComparer.Ordinal)
            {
                ["block.DisposeAccountChanges()"] = "disposeAccountChanges",
                ["worldStateCloser.Dispose()"] = "disposeScopeForRetry",
                ["stateProvider.BeginScope(preBlockBaseBlock)"] = "reopenScopeForRetry",
            }));
            foreach (ConditionalAccessExpressionSyntax access in method.DescendantNodes().OfType<ConditionalAccessExpressionSyntax>())
                if (Canonical(access) == "worldStateCloser?.Dispose()")
                    cleanup.Add(AnchorOf(source, method, access, access.Ancestors().Any(node => node is FinallyClauseSyntax)
                        ? "disposeScopeFinally" : "disposeScopeAtCheckpoint"));
        }
        RequireCount(cleanup, 6, "cleanup boundaries");

        List<Projection> headerFields = [];
        foreach (AssignmentExpressionSyntax assignment in methods["BlockHeader.CopyProcessingFields/1"].DescendantNodes().OfType<AssignmentExpressionSyntax>())
            headerFields.Add(new(Canonical(assignment.Left), Canonical(assignment.Right)));
        List<Projection> artifacts = [];
        foreach (AssignmentExpressionSyntax assignment in methods["BlockProcessor.PostValidation/4"].DescendantNodes().OfType<AssignmentExpressionSyntax>())
            artifacts.Add(new(Canonical(assignment.Left), Canonical(assignment.Right)));

        string checkpoint = LowerCheckpoint(loop, fieldInitializers["BranchProcessor.MaxUncommittedBlocks"]);
        Document document = new(1, phases, receiptEvents, blockEvents, branchEvents, commitTarget,
            finalization, cleanup, headerFields, artifacts, checkpoint,
            ExpectedExternalObligations);
        byte[] ir = Serialize(document);
        Document serialized = DeserializeIr(ir);
        ValidateIr(serialized, document);
        byte[] lean = EmitLean(serialized, Sha256(ir));
        Manifest manifest = new(1,
            SourceIdentityAuthority,
            SourceIdentities(sources), admissions, Sha256(ir), Sha256(lean));
        byte[] manifestBytes = Serialize(manifest);
        Manifest serializedManifest = DeserializeManifest(manifestBytes);
        ValidateManifest(serializedManifest, manifest);
        string output = Path.GetFullPath(outputDirectory);
        foreach (Source source in sources)
            if (Path.GetFullPath(Path.Combine(output, ArtifactName + ".lean")) == source.FullPath)
                throw new ExtractionException("Output cannot overwrite a production source.");
        Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(output, ArtifactName + ".ir.json"), ir);
        File.WriteAllBytes(Path.Combine(output, ArtifactName + ".lean"), lean);
        File.WriteAllBytes(Path.Combine(output, ArtifactName + ".source-manifest.json"), manifestBytes);
    }

    private static Source Read(string root, string path)
    {
        string fullPath = Path.Combine(root, path);
        byte[] bytes = File.ReadAllBytes(fullPath);
        CompilationUnitSyntax syntax = CSharpSyntaxTree.ParseText(Encoding.UTF8.GetString(bytes), ParseOptions, path).GetCompilationUnitRoot();
        foreach (Diagnostic diagnostic in syntax.GetDiagnostics())
            if (diagnostic.Severity == DiagnosticSeverity.Error) throw new ExtractionException(diagnostic.ToString());
        foreach (UsingDirectiveSyntax directive in syntax.DescendantNodes().OfType<UsingDirectiveSyntax>())
            if (directive.Alias is not null && !(path == ChainPath && Canonical(directive) == "usingMetrics=Nethermind.Blockchain.Metrics;"))
                throw new ExtractionException($"Using aliases are not admitted in {path}.");
        foreach (SyntaxTrivia trivia in syntax.DescendantTrivia(descendIntoTrivia: true))
            if (trivia.IsDirective) throw new ExtractionException($"Preprocessor directives are not admitted in {path}.");
        return new(path, fullPath, bytes, syntax);
    }

    private static void Admit(Source source, Dictionary<string, MethodDeclarationSyntax> methods,
        Dictionary<string, ExpressionSyntax> fieldInitializers, List<AdmissionIdentity> admissions, string? reviewedTemplate)
    {
        string className = Path.GetFileNameWithoutExtension(source.Path);
        Assembly assembly = typeof(Extractor).Assembly;
        string resourceName = $"Nethermind.Evm.Lean.BlockProcessorExtractor.Admission.{className}.cs.txt";
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new ExtractionException($"Missing admission template {resourceName}.");
        using StreamReader reader = new(stream);
        string template = reviewedTemplate ?? reader.ReadToEnd();
        CompilationUnitSyntax expected = CSharpSyntaxTree.ParseText(template, ParseOptions).GetCompilationUnitRoot();
        ClassDeclarationSyntax actualClass = Single<ClassDeclarationSyntax>(source.Root, node => node.Identifier.ValueText == className);
        string expectedNamespace = className == "BlockHeader" ? "Nethermind.Core" : "Nethermind.Consensus.Processing";
        if (actualClass.Parent is not FileScopedNamespaceDeclarationSyntax ns || ns.Name.ToString() != expectedNamespace)
            throw new ExtractionException($"Unexpected namespace or nested owner for {className}.");
        HashSet<string> selectedNames = new(StringComparer.Ordinal);
        foreach (FieldDeclarationSyntax field in expected.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            string name = field.Declaration.Variables.Single().Identifier.ValueText;
            FieldDeclarationSyntax actual = Single<FieldDeclarationSyntax>(actualClass, node =>
                node.Parent == actualClass && node.Declaration.Variables.Any(variable => variable.Identifier.ValueText == name));
            if (!EquivalentTokens(actual, field)) throw new ExtractionException($"Unadmitted field syntax: {className}.{name}.");
            fieldInitializers.Add($"{className}.{name}", actual.Declaration.Variables.Single().Initializer?.Value
                ?? throw new ExtractionException($"Missing admitted field initializer: {className}.{name}."));
            admissions.Add(new(
                AdmissionKey(source.Path, expectedNamespace, resourceName, className, $"field:{name}"),
                Sha256(Encoding.UTF8.GetBytes(Canonical(field))),
                Sha256(Encoding.UTF8.GetBytes(Canonical(actual)))));
        }
        foreach (MethodDeclarationSyntax method in expected.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            string name = method.Identifier.ValueText;
            string key = $"{className}.{name}/{method.ParameterList.Parameters.Count}";
            MethodDeclarationSyntax actual = Single<MethodDeclarationSyntax>(actualClass, node =>
                node.Parent == actualClass && node.Identifier.ValueText == name &&
                node.ParameterList.Parameters.Count == method.ParameterList.Parameters.Count);
            if (!EquivalentTokens(actual, method))
                throw new ExtractionException($"Unadmitted complete method syntax: {key}.");
            methods.Add(key, actual);
            selectedNames.Add(name);
            admissions.Add(new(
                AdmissionKey(source.Path, expectedNamespace, resourceName, className,
                    $"method:{name}/{method.ParameterList.Parameters.Count}"),
                Sha256(Encoding.UTF8.GetBytes(Canonical(method))),
                Sha256(Encoding.UTF8.GetBytes(Canonical(actual)))));
        }
        foreach (string name in selectedNames)
        {
            int expectedCount = expected.DescendantNodes().OfType<MethodDeclarationSyntax>().Count(node => node.Identifier.ValueText == name);
            int actualCount = actualClass.Members.OfType<MethodDeclarationSyntax>().Count(node => node.Identifier.ValueText == name);
            if (actualCount != expectedCount) throw new ExtractionException($"Competing overload for {className}.{name}.");
        }
    }

    private static void RejectCompetingDeclarations(string root, Source[] sources, Dictionary<string, MethodDeclarationSyntax> methods)
    {
        HashSet<string> known = new(StringComparer.OrdinalIgnoreCase);
        foreach (Source source in sources) known.Add(Path.GetFullPath(source.FullPath));
        foreach (string directory in new[] { ProcessingDirectory, "src/Nethermind/Nethermind.Core" })
        {
            foreach (string path in Directory.EnumerateFiles(Path.Combine(root, directory), "*.cs", SearchOption.AllDirectories))
            {
                if (known.Contains(Path.GetFullPath(path)) || path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
                    path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                CompilationUnitSyntax syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(path), ParseOptions).GetCompilationUnitRoot();
                foreach (ClassDeclarationSyntax type in syntax.DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    if (type.Parent is not BaseNamespaceDeclarationSyntax ns ||
                        ns.Name.ToString() is not ("Nethermind.Core" or "Nethermind.Consensus.Processing")) continue;
                    foreach (MethodDeclarationSyntax method in type.Members.OfType<MethodDeclarationSyntax>())
                    {
                        string prefix = $"{type.Identifier.ValueText}.{method.Identifier.ValueText}/";
                        foreach (string admitted in methods.Keys)
                            if (admitted.StartsWith(prefix, StringComparison.Ordinal))
                                throw new ExtractionException($"Competing declaration in {path}: {prefix}.");
                    }
                }
            }
        }
    }

    private static IEnumerable<InvocationExpressionSyntax> Calls(SyntaxNode node) => node.DescendantNodes().OfType<InvocationExpressionSyntax>();

    private static List<Anchor> SelectCalls(Source source, MethodDeclarationSyntax method,
        Dictionary<string, string> selected, SyntaxNode? subtree = null, bool excludeCatch = false)
    {
        List<Anchor> result = [];
        foreach (InvocationExpressionSyntax call in Calls(subtree ?? method))
        {
            if (excludeCatch && call.Ancestors().Any(node => node is CatchClauseSyntax)) continue;
            if (selected.TryGetValue(Canonical(call), out string? name)) result.Add(AnchorOf(source, method, call, name));
        }
        return result;
    }

    private static Anchor AnchorOf(Source source, MethodDeclarationSyntax method, SyntaxNode node, string name)
    {
        List<string> guards = [];
        foreach (SyntaxNode ancestor in node.Ancestors())
        {
            if (ancestor == method) break;
            if (ancestor is IfStatementSyntax condition && condition.Statement.Span.Contains(node.Span))
                guards.Add(Canonical(condition.Condition));
            if (ancestor is ElseClauseSyntax clause && clause.Parent is IfStatementSyntax conditionOwner)
                guards.Add("else:" + Canonical(conditionOwner.Condition));
            if (ancestor is CatchClauseSyntax clauseCatch) guards.Add("catch:" + Canonical(clauseCatch.Declaration!));
            if (ancestor is FinallyClauseSyntax) guards.Add("finally");
        }
        return new(name, source.Path, method.Identifier.ValueText,
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1, Canonical(node), guards);
    }

    private static string LowerCheckpoint(ForStatementSyntax loop, ExpressionSyntax maxUncommittedBlocks)
    {
        if (maxUncommittedBlocks is not LiteralExpressionSyntax { Token.Value: int modulus } || modulus <= 0)
            throw new ExtractionException("The admitted checkpoint modulus must be a positive integer literal.");
        Dictionary<string, ExpressionSyntax> locals = new(StringComparer.Ordinal);
        foreach (VariableDeclaratorSyntax variable in loop.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            if (variable.Initializer is not null) locals[variable.Identifier.ValueText] = variable.Initializer.Value;
        string Lower(ExpressionSyntax expression) => expression switch
        {
            IdentifierNameSyntax identifier when identifier.Identifier.ValueText == "i" => "index",
            IdentifierNameSyntax identifier when identifier.Identifier.ValueText == "blocksCount" => "total",
            IdentifierNameSyntax identifier when identifier.Identifier.ValueText == "MaxUncommittedBlocks" => Lower(maxUncommittedBlocks),
            IdentifierNameSyntax identifier when locals.TryGetValue(identifier.Identifier.ValueText, out ExpressionSyntax? value) => Lower(value),
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.NumericLiteralExpression) => literal.Token.ValueText,
            PrefixUnaryExpressionSyntax unary when unary.IsKind(SyntaxKind.LogicalNotExpression) => $"(!{Lower(unary.Operand)})",
            BinaryExpressionSyntax binary when binary.Kind() is SyntaxKind.EqualsExpression or SyntaxKind.SubtractExpression or
                SyntaxKind.ModuloExpression or SyntaxKind.LogicalAndExpression => $"({Lower(binary.Left)} {binary.OperatorToken.Text} {Lower(binary.Right)})",
            _ => throw new ExtractionException("Unsupported checkpoint guard expression: " + expression),
        };
        return Lower(locals["isCommitPoint"]);
    }

    internal static Document DeserializeIr(byte[] bytes)
    {
        if (bytes is null) throw new ExtractionException("Serialized block-processor IR input is null.");
        try
        {
            Document document = JsonSerializer.Deserialize<Document>(bytes, JsonOptions)
                ?? throw new ExtractionException("Serialized block-processor IR is empty.");
            ValidateIr(document);
            return document;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Serialized block-processor IR is malformed or contains unknown members: {exception.Message}");
        }
    }

    internal static Manifest DeserializeManifest(byte[] bytes)
    {
        if (bytes is null) throw new ExtractionException("Serialized block-processor manifest input is null.");
        try
        {
            Manifest manifest = JsonSerializer.Deserialize<Manifest>(bytes, JsonOptions)
                ?? throw new ExtractionException("Serialized block-processor manifest is empty.");
            ValidateManifest(manifest);
            return manifest;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Serialized block-processor manifest is malformed or contains unknown members: {exception.Message}");
        }
    }

    internal static void ValidateIr(Document document)
    {
        if (document is null || document.Phases is null || document.ReceiptEvents is null ||
            document.BlockEvents is null || document.BranchEvents is null || document.CommitTarget is null ||
            document.Finalization is null || document.Cleanup is null || document.HeaderFields is null ||
            document.Artifacts is null || document.Checkpoint is null || document.ExternalObligations is null ||
            document.Phases.Any(InvalidAnchor) || document.ReceiptEvents.Any(InvalidAnchor) ||
            document.BlockEvents.Any(InvalidAnchor) || document.BranchEvents.Any(InvalidAnchor) || InvalidAnchor(document.CommitTarget) ||
            document.Finalization.Any(InvalidAnchor) || document.Cleanup.Any(InvalidAnchor) ||
            document.HeaderFields.Any(InvalidProjection) || document.Artifacts.Any(InvalidProjection) ||
            document.ExternalObligations.Any(static obligation => obligation is null))
            throw new ExtractionException("The serialized block-processor IR contains null collections, entries, or fields.");

        if (document.SchemaVersion != 1 || document.Phases.Count != 11 || document.ReceiptEvents.Count != 3 ||
            document.BlockEvents.Count != 12 || document.BranchEvents.Count != 5 || document.Finalization.Count != 3 ||
            document.Cleanup.Count != 6 || document.HeaderFields.Count != 17 || document.Artifacts.Count != 4 ||
            !document.ExternalObligations.SequenceEqual(ExpectedExternalObligations, StringComparer.Ordinal))
            throw new ExtractionException("The serialized block-processor IR header, shape, or obligations changed.");

        ValidateUniqueNames(document.Phases, "phase");
        ValidateUniqueNames(document.ReceiptEvents, "receipt event");
        ValidateUniqueNames(document.BranchEvents, "per-block event");
        ValidateUniqueNames(document.Finalization, "finalization step");
        ValidateUniqueProjections(document.HeaderFields, "header projection");
        ValidateUniqueProjections(document.Artifacts, "artifact projection");

        static bool InvalidAnchor(Anchor? anchor) => anchor is null || anchor.Name is null || anchor.Source is null ||
            anchor.Method is null || anchor.Line <= 0 || anchor.Syntax is null || anchor.Guards is null ||
            anchor.Guards.Any(static guard => guard is null);
        static bool InvalidProjection(Projection? projection) => projection is null || projection.Target is null || projection.Value is null;
    }

    internal static void ValidateIr(Document actual, Document expected)
    {
        ValidateIr(actual);
        ValidateIr(expected);
        if (!Serialize(actual).AsSpan().SequenceEqual(Serialize(expected)))
            throw new ExtractionException("The serialized block-processor IR does not exactly match the source-derived event document.");
    }

    internal static void ValidateManifest(Manifest manifest)
    {
        if (manifest is null || manifest.SourceIdentityAuthority is null || manifest.Sources is null ||
            manifest.AdmittedMethods is null || manifest.IrSha256 is null || manifest.LeanSha256 is null ||
            manifest.Sources.Any(static source => source is null || source.Path is null || source.Sha256 is null) ||
            manifest.AdmittedMethods.Any(static admission => admission is null || admission.Key is null ||
                admission.TemplateSha256 is null || admission.SourceSyntaxSha256 is null))
            throw new ExtractionException("The serialized block-processor manifest contains null collections, entries, or fields.");
        if (manifest.SchemaVersion != 1 || manifest.SourceIdentityAuthority != SourceIdentityAuthority ||
            manifest.Sources.Count != Paths.Length || !IsSha256(manifest.IrSha256) || !IsSha256(manifest.LeanSha256))
            throw new ExtractionException("The serialized block-processor manifest header or artifact fingerprints changed.");
        if (!manifest.Sources.Select(static source => source.Path).SequenceEqual(Paths, StringComparer.Ordinal) ||
            manifest.Sources.Any(static source => !IsSha256(source.Sha256)) ||
            manifest.Sources.Select(static source => source.Path).Distinct(StringComparer.Ordinal).Count() != manifest.Sources.Count)
            throw new ExtractionException("The serialized block-processor source identities changed or collide.");
        ValidateAdmissionIdentities(manifest.AdmittedMethods);
    }

    internal static void ValidateManifest(Manifest actual, Manifest expected)
    {
        ValidateManifest(actual);
        ValidateManifest(expected);
        if (!Serialize(actual).AsSpan().SequenceEqual(Serialize(expected)))
            throw new ExtractionException("The serialized block-processor manifest does not exactly match source and artifact identities.");
    }

    internal static void ValidateAdmissionIdentities(IReadOnlyList<AdmissionIdentity> admissions)
    {
        if (admissions is null || admissions.Any(static admission => admission is null ||
                string.IsNullOrWhiteSpace(admission.Key) || !IsSha256(admission.TemplateSha256) ||
                !IsSha256(admission.SourceSyntaxSha256)))
            throw new ExtractionException("Block-processor admission identities contain null, empty, or invalid values.");
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (AdmissionIdentity admission in admissions)
        {
            if (!admission.Key.Contains(":compilation-unit/0:", StringComparison.Ordinal) ||
                !admission.Key.Contains(":owner:", StringComparison.Ordinal))
                throw new ExtractionException($"Admission identity is not path/resource/owner-qualified: {admission.Key}.");
            if (!keys.Add(admission.Key))
                throw new ExtractionException($"Duplicate owner-qualified admission identity {admission.Key}.");
        }
    }

    private static string AdmissionKey(string path, string @namespace, string resource, string owner, string selector) =>
        $"{path}:{@namespace}:{resource}:compilation-unit/0:owner:{owner}/0:{selector}";

    private static void ValidateUniqueNames(IEnumerable<Anchor> anchors, string description)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (Anchor anchor in anchors)
            if (!names.Add(anchor.Name)) throw new ExtractionException($"The serialized block-processor IR duplicates {description} {anchor.Name}.");
    }

    private static void ValidateUniqueProjections(IEnumerable<Projection> projections, string description)
    {
        HashSet<string> targets = new(StringComparer.Ordinal);
        foreach (Projection projection in projections)
            if (!targets.Add(projection.Target)) throw new ExtractionException($"The serialized block-processor IR duplicates {description} {projection.Target}.");
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(static character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static byte[] EmitLean(Document document, string irHash)
    {
        ValidateIr(document);
        StringBuilder output = new("-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited\n-- SPDX-License-Identifier: LGPL-3.0-only\n\n");
        output.AppendLine("-- Generated from admitted C# syntax. External calls are observations, not proved implementations.");
        output.AppendLine($"-- IR SHA-256: {irHash}");
        output.AppendLine("namespace BlockProcessorExtractor.Generated\n");
        EmitEnum("Phase", document.Phases);
        EmitEnum("ReceiptEvent", document.ReceiptEvents);
        EmitEnum("FinalizationStep", document.Finalization);
        EmitList("processPhases", "Phase", document.Phases, static anchor => "." + anchor.Name);
        EmitList("synchronousReceiptEvents", "ReceiptEvent", document.ReceiptEvents, static anchor => "." + anchor.Name);
        EmitList("blockEvents", "String", document.BlockEvents, static anchor => Quote(anchor.Name));
        EmitList("perBlockEvents", "String", document.BranchEvents, static anchor => Quote(anchor.Name));
        EmitList("finalizationSteps", "FinalizationStep", document.Finalization, static anchor => "." + anchor.Name);
        EmitList("cleanupEvents", "String", document.Cleanup, static anchor => Quote(anchor.Name));
        EmitProjections("copiedHeaderFields", document.HeaderFields);
        EmitProjections("publishedArtifacts", document.Artifacts);
        output.AppendLine($"def checkpointIndex (index total : Nat) : Bool :=\n  {document.Checkpoint}\n");
        output.AppendLine("end BlockProcessorExtractor.Generated");
        return Encoding.UTF8.GetBytes(output.ToString().Replace("\r\n", "\n"));

        void EmitEnum(string name, List<Anchor> values)
        {
            output.AppendLine($"inductive {name} where");
            foreach (Anchor anchor in values) output.AppendLine("  | " + anchor.Name);
            output.AppendLine("  deriving DecidableEq, Repr\n");
        }

        void EmitList(string name, string type, List<Anchor> values, Func<Anchor, string> render)
        {
            output.AppendLine($"def {name} : List {type} :=");
            output.AppendLine("  [ " + string.Join(", ", values.Select(render)) + " ]\n");
        }
        void EmitProjections(string name, List<Projection> values)
        {
            output.AppendLine($"def {name} : List (String × String) :=");
            output.AppendLine("  [ " + string.Join(", ", values.Select(value => $"({Quote(value.Target)}, {Quote(value.Value)})")) + " ]\n");
        }
    }

    private static T Single<T>(SyntaxNode root, Func<T, bool> predicate) where T : SyntaxNode
    {
        T? result = null;
        foreach (T node in root.DescendantNodes().OfType<T>())
        {
            if (!predicate(node)) continue;
            if (result is not null) throw new ExtractionException($"Competing {typeof(T).Name} in admitted source.");
            result = node;
        }
        return result ?? throw new ExtractionException($"Missing {typeof(T).Name} in admitted source.");
    }

    private static void RequireCount<T>(List<T> values, int expected, string name)
    {
        if (values.Count != expected) throw new ExtractionException($"Expected {expected} {name}, extracted {values.Count}.");
    }

    private static List<SourceIdentity> SourceIdentities(Source[] sources)
    {
        List<SourceIdentity> identities = [];
        foreach (Source source in sources) identities.Add(new(source.Path, Sha256(source.Bytes)));
        return identities;
    }

    private static string Canonical(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(token => token.Text));
    private static bool EquivalentTokens(SyntaxNode actual, SyntaxNode expected) =>
        actual.DescendantTokens().Select(token => (token.RawKind, token.Text))
            .SequenceEqual(expected.DescendantTokens().Select(token => (token.RawKind, token.Text)));
    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Quote(string value) => JsonSerializer.Serialize(value);
    private static byte[] Serialize<T>(T value) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions) + "\n");

    private sealed record Source(string Path, string FullPath, byte[] Bytes, CompilationUnitSyntax Root);
    internal sealed record Anchor(string Name, string Source, string Method, int Line, string Syntax, List<string> Guards);
    internal sealed record Projection(string Target, string Value);
    internal sealed record SourceIdentity(string Path, string Sha256);
    internal sealed record AdmissionIdentity(string Key, string TemplateSha256, string SourceSyntaxSha256);
    internal sealed record Document(int SchemaVersion, List<Anchor> Phases, List<Anchor> ReceiptEvents,
        List<Anchor> BlockEvents, List<Anchor> BranchEvents, Anchor CommitTarget, List<Anchor> Finalization,
        List<Anchor> Cleanup, List<Projection> HeaderFields, List<Projection> Artifacts, string Checkpoint, string[] ExternalObligations);
    internal sealed record Manifest(int SchemaVersion, string SourceIdentityAuthority, List<SourceIdentity> Sources,
        List<AdmissionIdentity> AdmittedMethods, string IrSha256, string LeanSha256);
}
