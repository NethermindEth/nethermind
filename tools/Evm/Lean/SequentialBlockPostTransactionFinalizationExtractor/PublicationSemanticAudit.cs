// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Evm.Lean.SequentialBlockPostTransactionFinalizationExtractor;

internal static partial class ProcessOneValidatedPublicationExtractor
{
    private sealed record PublicationSemanticContext(Compilation Compilation, CompilerClosureIdentity CompilerClosure)
    {
        internal SemanticModel Model(SyntaxNode node) => Compilation.GetSemanticModel(node.SyntaxTree);

        internal ControlFlowGraph Graph(MethodDeclarationSyntax method) =>
            Model(method).GetOperation(method) is IMethodBodyOperation body
                ? ControlFlowGraph.Create(body)
                : throw new ExtractionException("Publication method has no typed method-body operation.");
    }

    private static PublicationSemanticContext CompileSources(string root, PublicationSourceFile[] sources)
    {
        SourceFile[] files = sources.Select(source => new SourceFile(source.RelativePath, source.Role,
            Path.Combine(root, source.RelativePath), [], source.Sha256, source.SyntaxSha256,
            source.Root, source.Tree)).ToArray();
        CompilerContext compiler = Extractor.ReadCompilerContext(root, files, validateCompilation: true);
        return new(compiler.Compilation, compiler.Closure);
    }

    internal static void RequireCompilationForTest(string root, IReadOnlyDictionary<string, byte[]> overrides)
    {
        PublicationSourceFile[] sources = ReadSources(root, ReadPins(root).Sources, overrides, enforcePins: false, enforceSourceSelection: false);
        _ = CompileSources(root, sources);
    }

    internal static byte[] EmitLeanForTest(string root, ProcessOnePublicationIrDocument document) => EmitLean(root, document);

    private static byte[] EmitLean(string root, ProcessOnePublicationIrDocument document)
    {
        ValidateDocument(document);
        string template = File.ReadAllText(Path.Combine(root, DependencyPaths[^1])).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (ContainsProofPlaceholder(template)) throw new ExtractionException("Publication kernel template contains a proof placeholder.");
        string sites = string.Join(",\n  ", document.Anchors.Select(anchor =>
            $"{{ id := {LeanString(anchor.Id)}, path := {LeanString(anchor.Path)}, owner := {LeanString(anchor.OwnerFqn)}, target := {LeanString(anchor.SymbolFqn)}, " +
            $"position := {anchor.Position}, cfgBlock := {anchor.ControlFlowBlock} }}"));
        return Encoding.UTF8.GetBytes(template
            .Replace("{{SEMANTIC_IR_SHA256}}", Sha256(Serialize(document)), StringComparison.Ordinal)
            .Replace("{{SOURCE_CLOSURE_SHA256}}", document.SourceClosureSha256, StringComparison.Ordinal)
            .Replace("{{SOURCE_SITES}}", sites, StringComparison.Ordinal));
    }

    private static string LeanString(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string SymbolId(ISymbol symbol) => symbol.ToDisplayString(
        SymbolDisplayFormat.FullyQualifiedFormat.WithMemberOptions(
            SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters |
            SymbolDisplayMemberOptions.IncludeType).WithParameterOptions(
            SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeParamsRefOut)
        .WithLocalOptions(SymbolDisplayLocalOptions.IncludeType));

    private static string MethodKey(IMethodSymbol method) =>
        method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "." + method.Name + "(" +
        string.Join(",", method.Parameters.Select(parameter => parameter.Type.ToDisplayString(
            SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers))
            .Replace("global::", string.Empty, StringComparison.Ordinal) + (parameter.RefKind == RefKind.None ? "" : "&"))) + ")";

    private static IEnumerable<IOperation> Operations(IOperation root)
    {
        yield return root;
        foreach (IOperation child in root.ChildOperations)
            foreach (IOperation nested in Operations(child)) yield return nested;
    }

    private static IOperation[] BlockOperations(BasicBlock block) =>
        block.Operations.SelectMany(Operations).Concat(block.BranchValue is null
            ? [] : Operations(block.BranchValue)).ToArray();

    private static PublicationAnchor TypedAnchor(PublicationSemanticContext context, string id,
        PublicationSourceFile source, string expectedOwner, string expectedSymbol, SyntaxNode syntax, string relation)
    {
        SemanticModel model = context.Model(syntax);
        if (syntax is IfStatementSyntax guard && id is "processOne.validation-guard" or "processOne.store-guard")
        {
            InvocationExpressionSyntax predicate = guard.Condition.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                .Single(call => call.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == "ContainsFlag");
            if (model.GetOperation(predicate) is not IInvocationOperation predicateCall)
                throw new ExtractionException("Publication option predicate is not typed.");
            string predicateKey = MethodKey(predicateCall.TargetMethod.ReducedFrom ?? predicateCall.TargetMethod);
            if (predicateKey != "global::Nethermind.Consensus.Processing.ProcessingOptionsExtensions.ContainsFlag(Nethermind.Consensus.Processing.ProcessingOptions,Nethermind.Consensus.Processing.ProcessingOptions)")
                throw new ExtractionException("Publication option predicate target changed.");
            relation += "; predicate=" + predicateKey;
        }
        MethodDeclarationSyntax? method = syntax.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        SyntaxNode binding = syntax switch
        {
            AssignmentExpressionSyntax assignment when id == "processOne.processBlock-return" => assignment.Right,
            AssignmentExpressionSyntax assignment => assignment.Left,
            IfStatementSyntax condition when id == "processOne.disposal-finally" =>
                FindInvocation(condition, "block.DisposeAccountChanges()"),
            IfStatementSyntax condition => condition.Condition.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>()
                .Single(member => member.ToString().StartsWith("ProcessingOptions.", StringComparison.Ordinal)),
            CatchClauseSyntax caught => caught.Declaration?.Type ?? throw new ExtractionException("Untyped publication catch."),
            ReturnStatementSyntax returned => returned.Expression ?? throw new ExtractionException("Untyped publication return."),
            _ => syntax,
        };
        IOperation? operation = model.GetOperation(binding);
        if (operation is IInvocationOperation callOperation &&
            id is "processOne.validator-call" or "processOne.insert-deferred")
        {
            string expectedParameter = id == "processOne.validator-call" ? "blockValidator" : "receiptStorage";
            if (callOperation.Instance is not IParameterReferenceOperation receiver || receiver.Parameter.Name != expectedParameter)
                throw new ExtractionException($"Publication receiver '{expectedParameter}' is not the constructor parameter.");
            relation += "; receiver=" + SymbolId(receiver.Parameter);
        }
        ISymbol? symbol = binding is EnumMemberDeclarationSyntax enumMember
            ? model.GetDeclaredSymbol(enumMember)
            : model.GetSymbolInfo(binding).Symbol;
        symbol ??= operation switch
        {
            IInvocationOperation call => call.TargetMethod,
            IObjectCreationOperation creation => creation.Constructor,
            ILocalReferenceOperation local => local.Local,
            IUnaryOperation unary when unary.Operand is ILocalReferenceOperation local => local.Local,
            ITupleOperation tuple => tuple.Type,
            _ => null,
        };
        if (symbol is null || symbol is ITypeSymbol { TypeKind: TypeKind.Error } ||
            model.GetSymbolInfo(binding).CandidateSymbols.Length != 0)
            throw new ExtractionException($"Publication anchor '{id}' is not uniquely typed.");
        if (symbol is IMethodSymbol methodSymbol) symbol = methodSymbol.ReducedFrom ?? methodSymbol;
        string owner = method is null
            ? SymbolId(symbol.ContainingType ?? throw new ExtractionException("Publication declaration has no owner type."))
            : SymbolId(model.GetDeclaredSymbol(method) ?? throw new ExtractionException("Untyped publication owner."));
        if (method is not null && MethodKey(model.GetDeclaredSymbol(method)!) != expectedOwner)
            throw new ExtractionException($"Publication anchor '{id}' is attached to the wrong method.");

        if (symbol is IMethodSymbol target && id is not "processOne.invalid-block-throw")
        {
            string actual = MethodKey(target);
            if (actual != expectedSymbol) throw new ExtractionException($"Publication anchor '{id}' target changed to '{actual}'.");
        }
        if (id == "processOne.invalid-block-throw" && symbol is IMethodSymbol constructor &&
            constructor.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) !=
                "global::Nethermind.Core.Exceptions.InvalidBlockException")
            throw new ExtractionException("Publication rejection exception type changed.");
        if (symbol is IPropertySymbol property &&
            property.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "." + property.Name != expectedSymbol)
            throw new ExtractionException($"Publication property '{id}' target changed.");
        if (symbol is IFieldSymbol field &&
            field.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "." + field.Name != expectedSymbol)
            throw new ExtractionException($"Publication option '{id}' target changed.");
        if (syntax is CatchClauseSyntax && symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) != expectedSymbol)
            throw new ExtractionException($"Publication retry catch '{id}' exception type changed.");

        int ordinal = -1;
        string region = "declaration";
        bool reachable = true;
        int position = binding.SpanStart;
        if (method is not null)
        {
            ControlFlowGraph graph = context.Graph(method);
            SyntaxNode site = syntax is CatchClauseSyntax caught
                ? caught.Filter?.FilterExpression.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>()
                    .FirstOrDefault() ?? throw new ExtractionException("Publication retry catch lost its filter evaluation.")
                : binding;
            BasicBlock[] candidates = graph.Blocks.Where(block => BlockOperations(block).Any(item =>
                !item.IsImplicit && item.Syntax.Span == site.Span)).ToArray();
            if (candidates.Length != 1 || !candidates[0].IsReachable)
                throw new ExtractionException($"Publication anchor '{id}' does not have one exact reachable CFG owner.");
            BasicBlock block = candidates[0];
            ordinal = block.Ordinal;
            region = $"B{ordinal}:{block.EnclosingRegion.Kind}";
            reachable = block.IsReachable;
            position = site.SpanStart;
        }
        FileLinePositionSpan span = syntax.GetLocation().GetLineSpan();
        return new(id, source.RelativePath, owner, SymbolId(symbol), syntax.Kind().ToString(), Canonical(syntax),
            span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1, region, relation, reachable,
            source.SyntaxSha256, position, ordinal, operation?.Kind.ToString() ?? "Declaration");
    }

    private static PublicationControlFlowIdentity TypedFlow(PublicationSemanticContext context, string id,
        MethodDeclarationSyntax method)
    {
        ControlFlowGraph graph = context.Graph(method);
        List<string> edges = [];
        List<string> normal = [];
        List<string> exceptional = [];
        foreach (BasicBlock block in graph.Blocks)
        {
            foreach ((string arm, ControlFlowBranch? branch) in new[]
                { ("fallthrough", block.FallThroughSuccessor), ("conditional", block.ConditionalSuccessor) })
            {
                if (branch is null) continue;
                string edge = $"B{block.Ordinal}:{arm}:{block.ConditionKind}:{branch.Semantics}->" +
                    (branch.Destination is null ? "exit" : $"B{branch.Destination.Ordinal}") +
                    ":finally=" + string.Join(",", branch.FinallyRegions.Select(region => $"{region.FirstBlockOrdinal}-{region.LastBlockOrdinal}"));
                edges.Add(edge);
                if (branch.Semantics is ControlFlowBranchSemantics.Throw or ControlFlowBranchSemantics.Rethrow)
                    exceptional.Add(edge);
                if (branch.Destination?.Kind == BasicBlockKind.Exit) normal.Add(edge);
            }
        }
        string[] regions = graph.Blocks.Where(static block => block.IsReachable)
            .Select(static block => $"B{block.Ordinal}:{block.EnclosingRegion.Kind}").ToArray();
        ControlFlowBlockMembership[] memberships = graph.Blocks.Select(block => new ControlFlowBlockMembership(
            block.Ordinal, BlockOperations(block).Where(static operation => !operation.IsImplicit)
                .Select(static operation => operation.Syntax.SpanStart).Distinct().Order().ToArray())).ToArray();
        FileLinePositionSpan span = method.GetLocation().GetLineSpan();
        string owner = SymbolId(context.Model(method).GetDeclaredSymbol(method)!);
        string shape = string.Join("|", regions.Concat(edges).Concat(normal).Concat(exceptional)
            .Concat(memberships.Select(item => $"{item.Ordinal}:{string.Join(',', item.SyntaxStarts)}")));
        return new(id, owner, span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1,
            regions, edges.ToArray(), normal.ToArray(), exceptional.ToArray(), Sha256(Encoding.UTF8.GetBytes(shape)), memberships);
    }

    private static PublicationDataFlowIdentity[] TypedDataFlows(PublicationSemanticContext context,
        MethodDeclarationSyntax processOne, MethodDeclarationSyntax postValidation, MethodDeclarationSyntax validator)
    {
        PublicationDataFlowIdentity Local(string name, string id, string[] expectedWrites, int expectedReads)
        {
            SemanticModel model = context.Model(processOne);
            VariableDeclaratorSyntax declaration = processOne.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Single(variable => variable.Identifier.ValueText == name);
            ILocalSymbol local = model.GetDeclaredSymbol(declaration) as ILocalSymbol
                ?? throw new ExtractionException("Publication local is not bound.");
            DataFlowAnalysis flow = model.AnalyzeDataFlow(processOne.Body!)
                ?? throw new ExtractionException("Publication data-flow analysis is unavailable.");
            if (!flow.Succeeded || flow.Captured.Contains(local, SymbolEqualityComparer.Default))
                throw new ExtractionException($"Publication local '{name}' is captured or has failed dataflow.");
            AssignmentExpressionSyntax[] writes = processOne.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(assignment => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left).Symbol, local)).ToArray();
            if (!writes.Select(Canonical).SequenceEqual(expectedWrites))
                throw new ExtractionException($"Publication local '{name}' has competing definitions.");
            IdentifierNameSyntax[] reads = processOne.DescendantNodes().OfType<IdentifierNameSyntax>()
                .Where(identifier => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier).Symbol, local) &&
                    !writes.Any(write => write.Left == identifier)).ToArray();
            if (reads.Length != expectedReads || reads.Any(read => read.Ancestors().OfType<ArgumentSyntax>().Any(argument =>
                argument.RefKindKeyword.RawKind != 0)))
                throw new ExtractionException($"Publication local '{name}' escaped its exact read set.");
            if (!flow.WrittenInside.Contains(local, SymbolEqualityComparer.Default) ||
                !flow.ReadInside.Contains(local, SymbolEqualityComparer.Default))
                throw new ExtractionException("Publication local is missing from Roslyn read/write analysis.");
            string[] readSites = reads.Select(read => Canonical(read.Ancestors().OfType<StatementSyntax>().First())).ToArray();
            return new(id, SymbolId(local), Canonical(declaration), readSites,
                new[] { Canonical(declaration) }.Concat(writes.Select(Canonical)).ToArray(),
                "Exact statement skeleton and typed local read/write set; the normal ProcessBlock definition reaches the suffix through processed=true and finally.", true);
        }

        PublicationDataFlowIdentity Property(MethodDeclarationSyntax method, string id, string propertyName)
        {
            SemanticModel model = context.Model(method);
            AssignmentExpressionSyntax assignment = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Single(item => item.Left.ToString() == "suggestedBlock." + propertyName);
            if (model.GetOperation(assignment) is not ISimpleAssignmentOperation { Target: IPropertyReferenceOperation target } operation ||
                target.Property.ContainingType.ToDisplayString() != "Nethermind.Core.Block")
                throw new ExtractionException("Publication copy is not the exact typed Block property assignment.");
            DataFlowAnalysis flow = model.AnalyzeDataFlow(method.Body!)!;
            if (!flow.Succeeded) throw new ExtractionException("Publication property data-flow analysis failed.");
            IOperation body = model.GetOperation(method) ?? throw new ExtractionException("Publication body is not typed.");
            if (Operations(body).OfType<IAssignmentOperation>().Count(item =>
                item.Target is IPropertyReferenceOperation property &&
                SymbolEqualityComparer.Default.Equals(property.Property, target.Property)) != 1)
                throw new ExtractionException("Publication property has a competing typed write.");
            string[] reads = Operations(operation.Value).OfType<IPropertyReferenceOperation>()
                .Select(reference => SymbolId(reference.Property) + ":" + Canonical(reference.Syntax)).ToArray();
            return new(id, SymbolId(target.Property), Canonical(assignment), reads, [Canonical(assignment.Left)],
                id == "processOne.suggestedGeneratedBal" ? "rejection retains validator observation; normal PostValidation overwrites it" :
                    "Roslyn coalesce operation reads processed first, then suggested only when null", true);
        }

        return [Local("receipts", "processOne.receipts", ["receipts=ProcessBlock(block,blockTracer,options,spec,token)"], 3),
            Local("processed", "processOne.processed", ["processed=true"], 1),
            Property(validator, "processOne.suggestedGeneratedBal", "GeneratedBlockAccessList"),
            Property(postValidation, "postValidation.encodedBal", "EncodedBlockAccessList")];
    }
}
