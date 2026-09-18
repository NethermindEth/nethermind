// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;

namespace Nethermind.Evm.Lean.EvmFrameMachineExtractor;

internal static partial class EvmFrameMachineProfile
{
    private static ProductionRouting DeriveProductionRouting(
        CompilationUnitSyntax handlerRoot,
        CompilationUnitSyntax dispatchRoot,
        AmsterdamForkPredicates predicates)
    {
        DispatchInstantiation[] tables = DeriveDispatchInstantiations(dispatchRoot);
        MethodDeclarationSyntax generator = FindMethod(handlerRoot, "GenerateOpcodeHandlers", 2);
        ValidateBadInstructionInitialization(generator);
        Dictionary<string, MethodDeclarationSyntax> methods = handlerRoot.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(static method => method.TypeParameterList is not null)
            .ToDictionary(static method => MethodKey(method.Identifier.ValueText,
                method.TypeParameterList!.Parameters.Count), StringComparer.Ordinal);
        Dictionary<string, List<(string Table, string Root)>> roots = new(StringComparer.Ordinal);
        List<(string Table, string Root)> badInstructionRoots = [];

        foreach (DispatchInstantiation table in tables)
        {
            Dictionary<string, string> substitutions = new(StringComparer.Ordinal)
            {
                ["TGasPolicy"] = "EthereumGasPolicy",
                ["TTracingInst"] = table.TracingFlag,
                ["TCancelable"] = table.CancelableFlag,
            };
            Dictionary<string, string> tableRoots = new(StringComparer.Ordinal);
            ProcessStatements(generator.Body!, substitutions, methods, predicates, tableRoots);
            foreach ((string instruction, string root) in tableRoots)
            {
                if (!roots.TryGetValue(instruction, out List<(string Table, string Root)>? values))
                    roots.Add(instruction, values = []);
                values.Add((table.Table, root));
            }

            VariableDeclaratorSyntax badInstruction = generator.Body!.DescendantNodes()
                .OfType<VariableDeclaratorSyntax>().Single(variable => variable.Identifier.ValueText == "badInstruction");
            ExpressionSyntax initializer = badInstruction.Initializer?.Value ??
                throw new ExtractionException("Production bad-instruction handler has no initializer.");
            string badRoot = ResolveHandlerRoot(initializer, substitutions, methods, predicates, []);
            badInstructionRoots.Add((table.Table, badRoot));
        }

        return new(roots, badInstructionRoots);
    }

    internal static void ValidateProductionRoutingAgainstIr(
        string repoRoot,
        string handlerSource,
        string dispatchSource,
        IrDocument expected,
        string? mutatedSourcePath = null,
        string? mutatedSource = null)
    {
        CompilationUnitSyntax handlerRoot = ParseForRoutingTest(handlerSource, OpcodeHandlerSourcePath);
        CompilationUnitSyntax dispatchRoot = ParseForRoutingTest(dispatchSource, OpcodeDispatchSourcePath);
        Dictionary<string, ParsedSource> sources = LoadRoutingSourcesForTest(repoRoot,
            mutatedSourcePath, mutatedSource);
        sources[OpcodeHandlerSourcePath] = new(OpcodeHandlerSourcePath, "", [], handlerRoot);
        sources[OpcodeDispatchSourcePath] = new(OpcodeDispatchSourcePath, "", [], dispatchRoot);
        ProductionRouting production = DeriveProductionRouting(handlerRoot, dispatchRoot,
            AmsterdamForkPredicates.Derive(sources));
        string[] expectedInstructions = expected.OpcodeRoutes
            .Where(static route => route.RouteKind == "enabled")
            .Select(static route => route.Instruction).Append("INVALID").Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        if (!production.InstructionNames.Order(StringComparer.Ordinal)
            .SequenceEqual(expectedInstructions, StringComparer.Ordinal))
            throw new ExtractionException("Production active Amsterdam instruction assignment set changed.");
        foreach (IGrouping<int, OpcodeRouteDescriptor> group in expected.OpcodeRoutes
            .Where(static route => route.RouteKind == "enabled").GroupBy(static route => route.Byte))
        {
            OpcodeRouteDescriptor first = group.First();
            OwnedOpcode owner = new(first.Package, first.Instruction.ToLowerInvariant(), first.Instruction,
                first.Byte, first.ActivationRule,
                group.Select(static route => (route.DispatchTable, route.ClosedHandlerRoot)).ToList());
            _ = production.BindSibling(owner).Roots;
        }
        foreach (OpcodeRouteDescriptor route in expected.OpcodeRoutes.Where(static route => route.RouteKind == "badInstruction"))
        {
            List<(string Table, string Root)> roots = route.Instruction == "INVALID"
                ? production.BindDirect("INVALID")
                : production.BadInstructionRoots;
            string root = roots.Single(value => value.Table == route.DispatchTable).Root;
            if (NormalizeRoot(root) != NormalizeRoot(route.ClosedHandlerRoot))
                throw new ExtractionException($"Production bad-instruction route changed for {route.DispatchTable}/0x{route.Byte:x2}.");
        }
    }

    private static CompilationUnitSyntax ParseForRoutingTest(string source, string path)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, StageAParseOptions, path);
        Diagnostic[] errors = tree.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0)
            throw new ExtractionException($"Production routing mutation is not valid C# syntax: {errors[0]}.");
        return (CompilationUnitSyntax)tree.GetRoot();
    }

    private static Dictionary<string, ParsedSource> LoadRoutingSourcesForTest(
        string repoRoot,
        string? mutatedSourcePath,
        string? mutatedSource)
    {
        string root = Path.GetFullPath(repoRoot);
        HashSet<string> required = AmsterdamForkPredicates.RequiredSourcePaths();
        Dictionary<string, ParsedSource> result = new(StringComparer.Ordinal);
        foreach (string path in required)
        {
            string source = path == mutatedSourcePath
                ? mutatedSource ?? throw new ExtractionException("A routing source mutation has no replacement source.")
                : File.ReadAllText(ResolveExactPath(root, path), Encoding.UTF8);
            result.Add(path, new(path, "", [], ParseForRoutingTest(source, path)));
        }
        if (mutatedSourcePath is not null && !required.Contains(mutatedSourcePath))
            throw new ExtractionException($"Routing mutation path {mutatedSourcePath} is outside the predicate source closure.");
        return result;
    }

    private static DispatchInstantiation[] DeriveDispatchInstantiations(CompilationUnitSyntax root)
    {
        MethodDeclarationSyntax getHandlers = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method =>
            method.Identifier.ValueText == "GetHandlers" &&
            method.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText == "OpcodeTable" &&
            method.TypeParameterList?.Parameters.Count == 2);
        VariableDeclaratorSyntax tableVariable = getHandlers.Body!.Statements.OfType<LocalDeclarationStatementSyntax>()
            .SelectMany(static statement => statement.Declaration.Variables)
            .Single(variable => variable.Identifier.ValueText == "table");
        ExpressionSyntax selection = tableVariable.Initializer?.Value ??
            throw new ExtractionException("Production opcode-table selection has no initializer.");
        ReturnStatementSyntax returnStatement = getHandlers.Body.Statements.OfType<ReturnStatementSyntax>().Single();
        if (returnStatement.Expression is not AssignmentExpressionSyntax
            {
                RawKind: (int)SyntaxKind.CoalesceAssignmentExpression,
                Left: IdentifierNameSyntax { Identifier.ValueText: "table" },
                Right: InvocationExpressionSyntax invocation,
            } || InvocationName(invocation) != "GenerateOpcodeHandlers" ||
            InvocationTypeArguments(invocation).Select(Canonical).ToArray() is not ["TTracingInst", "TCancelable"])
            throw new ExtractionException("Production table cache no longer closes GenerateOpcodeHandlers over the selected flags.");

        List<DispatchInstantiation> result = [];
        foreach (bool tracing in new[] { false, true })
            foreach (bool cancelable in new[] { false, true })
            {
                string table = EvaluateDispatchSelection(selection, tracing, cancelable);
                result.Add(new(table, tracing ? "OnFlag" : "OffFlag", cancelable ? "OnFlag" : "OffFlag"));
            }
        if (!result.Select(static value => value.Table).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .SequenceEqual(DispatchTableNames.Order(StringComparer.Ordinal)))
            throw new ExtractionException("Production dispatch-table flag selection is not a four-way bijection.");
        return [.. result.OrderBy(value => Array.IndexOf(DispatchTableNames, value.Table))];
    }

    private static string EvaluateDispatchSelection(ExpressionSyntax expression, bool tracing, bool cancelable)
    {
        expression = Unwrap(expression);
        if (expression is IdentifierNameSyntax identifier) return identifier.Identifier.ValueText;
        if (expression is not ConditionalExpressionSyntax conditional)
            throw new ExtractionException($"Unsupported production dispatch-table selector {expression.Kind()}.");
        bool condition = Canonical(conditional.Condition) switch
        {
            "TTracingInst.IsActive" => tracing,
            "TCancelable.IsActive" => cancelable,
            string value => throw new ExtractionException($"Unsupported production dispatch-table condition {value}."),
        };
        return EvaluateDispatchSelection(condition ? conditional.WhenTrue : conditional.WhenFalse, tracing, cancelable);
    }

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    expression = parenthesized.Expression;
                    continue;
                case RefExpressionSyntax reference:
                    expression = reference.Expression;
                    continue;
                default:
                    return expression;
            }
        }
    }

    private static void ValidateBadInstructionInitialization(MethodDeclarationSyntax generator)
    {
        ForStatementSyntax loop = generator.Body!.Statements.OfType<ForStatementSyntax>().Single();
        VariableDeclarationSyntax declaration = loop.Declaration ??
            throw new ExtractionException("Production bad-instruction initialization loop has no counter declaration.");
        VariableDeclaratorSyntax counter = declaration.Variables.Single();
        bool exactCounter = Canonical(declaration.Type) == "int" && counter.Identifier.ValueText == "i" &&
            counter.Initializer is { Value: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } zero } &&
            zero.Token.ValueText == "0";
        bool exactCondition = loop.Condition is BinaryExpressionSyntax
        {
            RawKind: (int)SyntaxKind.LessThanExpression,
            Left: IdentifierNameSyntax { Identifier.ValueText: "i" },
            Right: MemberAccessExpressionSyntax
            {
                Expression: IdentifierNameSyntax { Identifier.ValueText: "lookup" },
                Name.Identifier.ValueText: "Length",
            },
        };
        bool exactIncrement = loop.Incrementors.SingleOrDefault() is PostfixUnaryExpressionSyntax
        {
            RawKind: (int)SyntaxKind.PostIncrementExpression,
            Operand: IdentifierNameSyntax { Identifier.ValueText: "i" },
        };
        bool exactAssignment = loop.Statement is ExpressionStatementSyntax
        {
            Expression: AssignmentExpressionSyntax
            {
                RawKind: (int)SyntaxKind.SimpleAssignmentExpression,
                Left: ElementAccessExpressionSyntax
                {
                    Expression: IdentifierNameSyntax { Identifier.ValueText: "lookup" },
                    ArgumentList.Arguments: [{ Expression: IdentifierNameSyntax { Identifier.ValueText: "i" } }],
                },
                Right: IdentifierNameSyntax { Identifier.ValueText: "badInstruction" },
            },
        };
        if (!exactCounter || !exactCondition || !exactIncrement || !exactAssignment)
            throw new ExtractionException("Production opcode table no longer initializes all 256 entries to badInstruction.");
    }

    private static void ProcessStatements(
        StatementSyntax statement,
        IReadOnlyDictionary<string, string> substitutions,
        IReadOnlyDictionary<string, MethodDeclarationSyntax> methods,
        AmsterdamForkPredicates predicates,
        Dictionary<string, string> roots)
    {
        switch (statement)
        {
            case BlockSyntax block:
                foreach (StatementSyntax child in block.Statements)
                    ProcessStatements(child, substitutions, methods, predicates, roots);
                break;
            case IfStatementSyntax conditional:
                if (predicates.Evaluate(conditional.Condition))
                    ProcessStatements(conditional.Statement, substitutions, methods, predicates, roots);
                else if (conditional.Else is not null)
                    ProcessStatements(conditional.Else.Statement, substitutions, methods, predicates, roots);
                break;
            case ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment }:
                if (TryReadLookupInstruction(assignment.Left, out string? instruction))
                {
                    string root = ResolveHandlerRoot(assignment.Right, substitutions, methods, predicates, []);
                    if (!roots.TryAdd(instruction, root))
                        throw new ExtractionException($"Production handler has more than one active terminal assignment for {instruction}.");
                }
                else
                    throw new ExtractionException($"Production handler assignment is not an exact lookup terminal: {Canonical(assignment)}.");
                break;
            case ExpressionStatementSyntax { Expression: InvocationExpressionSyntax invocation }
                when InvocationName(invocation) == "ConfigureAccessOpcodes":
                ProcessConfigurationInvocation(invocation, substitutions, methods, predicates, roots);
                break;
            case LocalDeclarationStatementSyntax declaration when declaration.Declaration.Variables.All(static variable =>
                variable.Identifier.ValueText is "lookup" or "badInstruction"):
                break;
            case ForStatementSyntax:
                break;
            case ReturnStatementSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "lookup" } }:
                break;
            default:
                throw new ExtractionException($"Unsupported active production handler statement {statement.Kind()}: {Canonical(statement)}.");
        }
    }

    private static void ProcessConfigurationInvocation(
        InvocationExpressionSyntax invocation,
        IReadOnlyDictionary<string, string> substitutions,
        IReadOnlyDictionary<string, MethodDeclarationSyntax> methods,
        AmsterdamForkPredicates predicates,
        Dictionary<string, string> roots)
    {
        TypeSyntax[] arguments = InvocationTypeArguments(invocation);
        MethodDeclarationSyntax method = methods.GetValueOrDefault(MethodKey("ConfigureAccessOpcodes", arguments.Length)) ??
            throw new ExtractionException($"Production ConfigureAccessOpcodes<{arguments.Length}> overload is missing.");
        Dictionary<string, string> nested = BindTypeParameters(method, arguments, substitutions);
        ProcessStatements(method.Body!, nested, methods, predicates, roots);
    }

    private static string ResolveHandlerRoot(
        ExpressionSyntax expression,
        IReadOnlyDictionary<string, string> substitutions,
        IReadOnlyDictionary<string, MethodDeclarationSyntax> methods,
        AmsterdamForkPredicates predicates,
        HashSet<string> activeMethods)
    {
        expression = Unwrap(expression);
        switch (expression)
        {
            case ConditionalExpressionSyntax conditional:
                return ResolveHandlerRoot(predicates.Evaluate(conditional.Condition)
                    ? conditional.WhenTrue : conditional.WhenFalse, substitutions, methods, predicates, activeMethods);
            case SwitchExpressionSyntax switchExpression:
                return ResolveHandlerRoot(predicates.SelectSwitchArm(switchExpression), substitutions,
                    methods, predicates, activeMethods);
            case InvocationExpressionSyntax invocation:
                return ResolveInvocationRoot(invocation, substitutions, methods, predicates, activeMethods);
            default:
                throw new ExtractionException($"Unsupported production handler RHS {expression.Kind()}: {Canonical(expression)}.");
        }
    }

    private static string ResolveInvocationRoot(
        InvocationExpressionSyntax invocation,
        IReadOnlyDictionary<string, string> substitutions,
        IReadOnlyDictionary<string, MethodDeclarationSyntax> methods,
        AmsterdamForkPredicates predicates,
        HashSet<string> activeMethods)
    {
        string name = InvocationName(invocation);
        TypeSyntax[] arguments = InvocationTypeArguments(invocation);
        string[] closedArguments = arguments.Select(argument => SubstituteType(argument, substitutions)).ToArray();
        switch (name)
        {
            case "OpcodeHandler" when closedArguments.Length == 3:
                return $"VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<{string.Join(',', closedArguments)},OnFlag>";
            case "TerminatingOpcodeHandler" when closedArguments.Length == 3:
                return $"VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<{string.Join(',', closedArguments)},OffFlag>";
            case "JumpIfOpcodeHandler" when closedArguments.Length == 2:
                return $"VirtualMachine<EthereumGasPolicy>.ExecuteJumpIfOpcode<{string.Join(',', closedArguments)}>";
        }

        string key = MethodKey(name, arguments.Length);
        MethodDeclarationSyntax method = methods.GetValueOrDefault(key) ??
            throw new ExtractionException($"Production handler helper {key} is not source-bound.");
        if (!activeMethods.Add(key))
            throw new ExtractionException($"Production handler helper recursion detected at {key}.");
        Dictionary<string, string> nested = BindTypeParameters(method, arguments, substitutions);
        ExpressionSyntax returned = method.ExpressionBody?.Expression ??
            throw new ExtractionException($"Production handler helper {key} is not an exact expression-bodied selector.");
        string result = ResolveHandlerRoot(returned, nested, methods, predicates, activeMethods);
        activeMethods.Remove(key);
        return result;
    }

    private static Dictionary<string, string> BindTypeParameters(
        MethodDeclarationSyntax method,
        TypeSyntax[] arguments,
        IReadOnlyDictionary<string, string> substitutions)
    {
        SeparatedSyntaxList<TypeParameterSyntax> parameters = method.TypeParameterList?.Parameters ?? default;
        if (parameters.Count != arguments.Length)
            throw new ExtractionException($"Production helper {method.Identifier.ValueText} generic arity changed.");
        Dictionary<string, string> result = new(substitutions, StringComparer.Ordinal);
        for (int index = 0; index < parameters.Count; index++)
            result[parameters[index].Identifier.ValueText] = SubstituteType(arguments[index], substitutions);
        return result;
    }

    private static string SubstituteType(TypeSyntax type, IReadOnlyDictionary<string, string> substitutions)
    {
        SyntaxNode current = type;
        for (int index = 0; index <= substitutions.Count; index++)
        {
            SyntaxNode replaced = new TypeSubstitutionRewriter(substitutions).Visit(current)!;
            if (Canonical(replaced) == Canonical(current)) return Canonical(replaced);
            current = replaced;
        }
        throw new ExtractionException($"Production generic substitution did not converge for {Canonical(type)}.");
    }

    private static bool TryReadLookupInstruction(ExpressionSyntax expression, out string instruction)
    {
        instruction = "";
        if (expression is not ElementAccessExpressionSyntax
            {
                Expression: IdentifierNameSyntax { Identifier.ValueText: "lookup" },
                ArgumentList.Arguments: [{ Expression: CastExpressionSyntax cast }],
            } || Canonical(cast.Type) != "int" ||
            cast.Expression is not MemberAccessExpressionSyntax
            {
                Expression: IdentifierNameSyntax { Identifier.ValueText: "Instruction" },
            } member)
            return false;
        instruction = member.Name.Identifier.ValueText;
        return instruction.Length != 0;
    }

    private static string EvaluateExpectedRoot(string expected, IEnumerable<string> candidates, string context)
    {
        string[] matches = candidates.Distinct(StringComparer.Ordinal)
            .Where(candidate => NormalizeRoot(candidate) == NormalizeRoot(expected)).ToArray();
        if (matches.Length != 1)
            throw new ExtractionException($"Production handler RHS has {matches.Length} exact matches for {context}: {expected}.");
        return NormalizeRoot(matches[0]);
    }

    private static string NormalizeRoot(string value) => value
        .Replace("global::", "", StringComparison.Ordinal)
        .Replace("Nethermind.Evm.GasPolicy.", "", StringComparison.Ordinal)
        .Replace("Nethermind.Evm.", "", StringComparison.Ordinal)
        .Replace(" ", "", StringComparison.Ordinal)
        .Replace("\r", "", StringComparison.Ordinal)
        .Replace("\n", "", StringComparison.Ordinal)
        .Replace("\t", "", StringComparison.Ordinal);

    private static MethodDeclarationSyntax FindMethod(CompilationUnitSyntax root, string name, int arity) =>
        root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method =>
            method.Identifier.ValueText == name && method.TypeParameterList?.Parameters.Count == arity);

    private static string MethodKey(string name, int arity) => $"{name}`{arity}";

    private static string InvocationName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        GenericNameSyntax generic => generic.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => throw new ExtractionException($"Unsupported production invocation target {invocation.Expression.Kind()}."),
    };

    private static TypeSyntax[] InvocationTypeArguments(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        GenericNameSyntax generic => [.. generic.TypeArgumentList.Arguments],
        MemberAccessExpressionSyntax { Name: GenericNameSyntax generic } => [.. generic.TypeArgumentList.Arguments],
        _ => [],
    };

    private sealed class AmsterdamForkPredicates(
        IReadOnlyDictionary<string, bool> forkFlags,
        IReadOnlyDictionary<string, ExpressionSyntax> extensionProperties,
        IReadOnlyDictionary<string, ExpressionSyntax> specFlags,
        IReadOnlyDictionary<string, PropertyDeclarationSyntax> releaseProperties)
    {
        private const string ReleaseSpecSourcePath = "src/Nethermind/Nethermind.Specs/ReleaseSpec.cs";
        private const string NamedReleaseSpecSourcePath = "src/Nethermind/Nethermind.Specs/Forks/NamedReleaseSpec.cs";
        private const string ReleaseExtensionsSourcePath = "src/Nethermind/Nethermind.Core/Specs/IReleaseSpecExtensions.cs";
        private const string StandardReleaseExtensionsSourcePath = "src/Nethermind/Nethermind.Core/Specs/IReleaseSpecExtensions.std.cs";
        private const string SpecFlagsSourcePath = "src/Nethermind/Nethermind.Evm/SpecFlags.std.cs";

        private static readonly string[] ForkSourcePaths =
        [
            "src/Nethermind/Nethermind.Specs/Forks/00_Olympic.cs",
            "src/Nethermind/Nethermind.Specs/Forks/01_Frontier.cs",
            "src/Nethermind/Nethermind.Specs/Forks/02_Homestead.cs",
            "src/Nethermind/Nethermind.Specs/Forks/03_Dao.cs",
            "src/Nethermind/Nethermind.Specs/Forks/04_TangerineWhistle.cs",
            "src/Nethermind/Nethermind.Specs/Forks/05_SpuriousDragon.cs",
            "src/Nethermind/Nethermind.Specs/Forks/06_Byzantium.cs",
            "src/Nethermind/Nethermind.Specs/Forks/07_Constantinople.cs",
            "src/Nethermind/Nethermind.Specs/Forks/08_ConstantinopleFix.cs",
            "src/Nethermind/Nethermind.Specs/Forks/09_Istanbul.cs",
            "src/Nethermind/Nethermind.Specs/Forks/10_MuirGlacier.cs",
            "src/Nethermind/Nethermind.Specs/Forks/11_Berlin.cs",
            "src/Nethermind/Nethermind.Specs/Forks/12_London.cs",
            "src/Nethermind/Nethermind.Specs/Forks/13_ArrowGlacier.cs",
            "src/Nethermind/Nethermind.Specs/Forks/14_GrayGlacier.cs",
            "src/Nethermind/Nethermind.Specs/Forks/15_Paris.cs",
            "src/Nethermind/Nethermind.Specs/Forks/16_Shanghai.cs",
            "src/Nethermind/Nethermind.Specs/Forks/17_Cancun.cs",
            "src/Nethermind/Nethermind.Specs/Forks/18_Prague.cs",
            "src/Nethermind/Nethermind.Specs/Forks/19_Osaka.cs",
            "src/Nethermind/Nethermind.Specs/Forks/20_BPO1.cs",
            "src/Nethermind/Nethermind.Specs/Forks/21_BPO2.cs",
            "src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs",
        ];

        internal static HashSet<string> RequiredSourcePaths() =>
        [
            ReleaseSpecSourcePath,
            NamedReleaseSpecSourcePath,
            ReleaseExtensionsSourcePath,
            StandardReleaseExtensionsSourcePath,
            SpecFlagsSourcePath,
            .. ForkSourcePaths,
        ];

        internal static AmsterdamForkPredicates Derive(IReadOnlyDictionary<string, ParsedSource> sources)
        {
            foreach (string path in RequiredSourcePaths())
                if (!sources.ContainsKey(path))
                    throw new ExtractionException($"Amsterdam predicate source {path} is not pinned.");

            ValidateNamedReleaseReplay(sources[NamedReleaseSpecSourcePath].Syntax!);
            Dictionary<string, PropertyDeclarationSyntax> properties = sources[ReleaseSpecSourcePath].Syntax!
                .DescendantNodes().OfType<PropertyDeclarationSyntax>()
                .ToDictionary(static property => property.Identifier.ValueText, StringComparer.Ordinal);
            Dictionary<string, ExpressionSyntax> extensions = new(StringComparer.Ordinal);
            foreach (string path in new[] { ReleaseExtensionsSourcePath, StandardReleaseExtensionsSourcePath })
                foreach (PropertyDeclarationSyntax property in sources[path].Syntax!.DescendantNodes()
                             .OfType<PropertyDeclarationSyntax>())
                {
                    ExpressionSyntax expression = property.ExpressionBody?.Expression ??
                        throw new ExtractionException($"Fork predicate extension {property.Identifier.ValueText} is not expression-bodied.");
                    if (!extensions.TryAdd(property.Identifier.ValueText, expression))
                        throw new ExtractionException($"Fork predicate extension {property.Identifier.ValueText} is duplicated.");
                }

            Dictionary<string, ExpressionSyntax> flags = sources[SpecFlagsSourcePath].Syntax!
                .DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(static method => method.Identifier.ValueText != "Validate")
                .ToDictionary(static method => method.Identifier.ValueText, static method =>
                    method.ExpressionBody?.Expression ??
                    throw new ExtractionException($"SpecFlags.{method.Identifier.ValueText} is not expression-bodied."),
                    StringComparer.Ordinal);
            Dictionary<string, bool> values = ReplayForkLineage(sources);
            return new(values, extensions, flags, properties);
        }

        internal bool Evaluate(ExpressionSyntax expression) => Evaluate(expression, []);

        internal ExpressionSyntax SelectSwitchArm(SwitchExpressionSyntax expression)
        {
            if (Unwrap(expression.GoverningExpression) is not TupleExpressionSyntax { Arguments.Count: 2 } tuple)
                throw new ExtractionException("Production handler switch is not an exact two-predicate fork selector.");
            bool first = Evaluate(tuple.Arguments[0].Expression);
            bool second = Evaluate(tuple.Arguments[1].Expression);
            string expected = $"({first.ToString().ToLowerInvariant()},{second.ToString().ToLowerInvariant()})";
            SwitchExpressionArmSyntax[] matches = expression.Arms.Where(arm => arm.WhenClause is null &&
                Canonical(arm.Pattern) == expected).ToArray();
            if (matches.Length != 1)
                throw new ExtractionException($"Production handler switch has {matches.Length} active Amsterdam arms for {expected}.");
            return matches[0].Expression;
        }

        private bool Evaluate(ExpressionSyntax expression, HashSet<string> activeProperties)
        {
            expression = Unwrap(expression);
            switch (expression)
            {
                case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.TrueLiteralExpression):
                    return true;
                case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.FalseLiteralExpression):
                    return false;
                case PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } negation:
                    return !Evaluate(negation.Operand, activeProperties);
                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalAndExpression):
                    return Evaluate(binary.Left, activeProperties) && Evaluate(binary.Right, activeProperties);
                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalOrExpression):
                    return Evaluate(binary.Left, activeProperties) || Evaluate(binary.Right, activeProperties);
                case MemberAccessExpressionSyntax
                {
                    Expression: IdentifierNameSyntax { Identifier.ValueText: "spec" },
                } member:
                    return EvaluateSpecProperty(member.Name.Identifier.ValueText, activeProperties);
                case InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax
                    {
                        Expression: IdentifierNameSyntax { Identifier.ValueText: "SpecFlags" },
                    },
                    ArgumentList.Arguments: [{ Expression: IdentifierNameSyntax { Identifier.ValueText: "spec" } }],
                } invocation:
                    {
                        string name = InvocationName(invocation);
                        ExpressionSyntax body = specFlags.GetValueOrDefault(name) ??
                            throw new ExtractionException($"Production fork selector SpecFlags.{name} is not source-bound.");
                        return Evaluate(body, activeProperties);
                    }
                default:
                    throw new ExtractionException($"Unsupported production Amsterdam fork predicate {expression.Kind()}: {Canonical(expression)}.");
            }
        }

        private bool EvaluateSpecProperty(string name, HashSet<string> activeProperties)
        {
            if (name.StartsWith("IsEip", StringComparison.Ordinal) && name.EndsWith("Enabled", StringComparison.Ordinal))
            {
                PropertyDeclarationSyntax property = releaseProperties.GetValueOrDefault(name) ??
                    throw new ExtractionException($"Amsterdam fork property {name} is absent from ReleaseSpec.");
                if (Canonical(property.Type) != "bool" || property.Initializer is not null ||
                    property.AccessorList?.Accessors.Select(static accessor => accessor.Keyword.ValueText).ToArray() is not ["get", "set"])
                    throw new ExtractionException($"Amsterdam fork property {name} no longer has a false default auto-property shape.");
                return forkFlags.GetValueOrDefault(name);
            }

            ExpressionSyntax extension = extensionProperties.GetValueOrDefault(name) ??
                throw new ExtractionException($"Amsterdam fork extension property {name} is not source-bound.");
            if (!activeProperties.Add(name))
                throw new ExtractionException($"Amsterdam fork extension property cycle detected at {name}.");
            bool value = Evaluate(extension, activeProperties);
            activeProperties.Remove(name);
            return value;
        }

        private static Dictionary<string, bool> ReplayForkLineage(IReadOnlyDictionary<string, ParsedSource> sources)
        {
            Dictionary<string, bool> values = new(StringComparer.Ordinal);
            string? parent = null;
            foreach (string path in ForkSourcePaths)
            {
                CompilationUnitSyntax root = sources[path].Syntax!;
                ClassDeclarationSyntax declaration = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single();
                string name = declaration.Identifier.ValueText;
                string expectedBase = parent is null
                    ? $"NamedReleaseSpec<{name}>(null)"
                    : $"NamedReleaseSpec<{name}>({parent}.Instance)";
                BaseTypeSyntax actualBase = declaration.BaseList?.Types.Single() ??
                    throw new ExtractionException($"Fork {name} has no exact parent declaration.");
                if (Canonical(actualBase) != expectedBase)
                    throw new ExtractionException($"Fork {name} no longer extends the pinned parent {parent ?? "null"}.");

                MethodDeclarationSyntax apply = declaration.Members.OfType<MethodDeclarationSyntax>()
                    .Single(method => method.Identifier.ValueText == "Apply");
                AssignmentExpressionSyntax[] assignments;
                if (apply.ExpressionBody?.Expression is AssignmentExpressionSyntax assignment)
                {
                    assignments = [assignment];
                }
                else if (apply.Body is { } body && body.Statements.All(static statement =>
                    statement is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax }))
                {
                    assignments = [.. body.Statements.Cast<ExpressionStatementSyntax>()
                        .Select(static statement => (AssignmentExpressionSyntax)statement.Expression)];
                }
                else
                {
                    throw new ExtractionException($"Fork {name}.Apply is no longer an exact unconditional assignment sequence.");
                }
                foreach (AssignmentExpressionSyntax candidate in assignments)
                {
                    if (!candidate.IsKind(SyntaxKind.SimpleAssignmentExpression))
                        throw new ExtractionException($"Fork {name}.Apply contains a non-simple assignment: {Canonical(candidate)}.");
                    if (candidate.Left is not MemberAccessExpressionSyntax
                        {
                            Expression: IdentifierNameSyntax { Identifier.ValueText: "spec" },
                        } member || !member.Name.Identifier.ValueText.StartsWith("IsEip", StringComparison.Ordinal))
                        continue;
                    bool value = candidate.Right switch
                    {
                        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.TrueLiteralExpression) => true,
                        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.FalseLiteralExpression) => false,
                        _ => throw new ExtractionException($"Fork {name} assigns a non-literal EIP predicate {member.Name.Identifier.ValueText}."),
                    };
                    values[member.Name.Identifier.ValueText] = value;
                }
                parent = name;
            }
            if (parent != "Amsterdam")
                throw new ExtractionException("Pinned fork lineage does not terminate at Amsterdam.");
            return values;
        }

        private static void ValidateNamedReleaseReplay(CompilationUnitSyntax root)
        {
            ClassDeclarationSyntax declaration = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Single(type => type.Identifier.ValueText == "NamedReleaseSpec" && type.TypeParameterList is null);
            ConstructorDeclarationSyntax constructor = declaration.Members.OfType<ConstructorDeclarationSyntax>().Single();
            if (Canonical(constructor.ParameterList) != "(NamedReleaseSpec?parent)" ||
                Canonical(constructor.Body!) != "{Parent=parent;ReplayAncestors(this);}")
                throw new ExtractionException("NamedReleaseSpec constructor no longer replays the exact parent lineage.");
            MethodDeclarationSyntax replay = declaration.Members.OfType<MethodDeclarationSyntax>()
                .Single(method => method.Identifier.ValueText == "ReplayAncestors");
            if (Canonical(replay.ParameterList) != "(NamedReleaseSpec?fork)" ||
                Canonical(replay.Body!) != "{if(forkisnull)return;ReplayAncestors(fork.Parent);fork.Apply(this);}")
                throw new ExtractionException("NamedReleaseSpec.ReplayAncestors order changed.");
        }
    }

    private sealed class TypeSubstitutionRewriter(IReadOnlyDictionary<string, string> substitutions) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node) =>
            substitutions.TryGetValue(node.Identifier.ValueText, out string? replacement)
                ? SyntaxFactory.ParseTypeName(replacement)
                : base.VisitIdentifierName(node);
    }

    private sealed record DispatchInstantiation(string Table, string TracingFlag, string CancelableFlag);

    private sealed class ProductionRouting(
        IReadOnlyDictionary<string, List<(string Table, string Root)>> roots,
        List<(string Table, string Root)> badInstructionRoots)
    {
        internal List<(string Table, string Root)> BadInstructionRoots { get; } = badInstructionRoots;
        internal IEnumerable<string> InstructionNames => roots.Keys;

        internal List<(string Table, string Root)> BindDirect(string instruction)
        {
            if (!roots.TryGetValue(instruction, out List<(string Table, string Root)>? values))
                throw new ExtractionException($"Production opcode table has no exact assignment for {instruction}.");
            List<(string Table, string Root)> result = [];
            foreach (string table in DispatchTableNames)
            {
                string[] candidates = values.Where(value => value.Table == table).Select(static value => value.Root)
                    .Distinct(StringComparer.Ordinal).ToArray();
                if (candidates.Length != 1)
                    throw new ExtractionException($"Production direct route {instruction}/{table} has {candidates.Length} roots.");
                result.Add((table, NormalizeRoot(candidates[0])));
            }
            return result;
        }

        internal OwnedOpcode BindSibling(OwnedOpcode owner)
        {
            if (!roots.TryGetValue(owner.Instruction, out List<(string Table, string Root)>? values))
                throw new ExtractionException($"Production opcode table has no exact assignment for {owner.Instruction}.");
            List<(string Table, string Root)> bound = [];
            foreach ((string table, string expected) in owner.Roots)
            {
                IEnumerable<string> candidates = values.Where(value => value.Table == table).Select(static value => value.Root);
                bound.Add((table, EvaluateExpectedRoot(expected, candidates,
                    $"{owner.Package}/{owner.Instruction}/{table}")));
            }
            return owner with { Roots = bound };
        }
    }
}
