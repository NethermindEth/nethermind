// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Evm.Lean.Extractor;

internal sealed record BlockReceiptGasAccountingKernelModel(
    IReadOnlyList<BlockReceiptGasAccountingField> ResultFields,
    IReadOnlyList<BlockReceiptGasAccountingFunction> Functions,
    IReadOnlyList<BlockReceiptGasAccountingProductionDelegation> ProductionDelegations);

internal sealed record BlockReceiptGasAccountingField(string Name, string Type, int ConstructorParameterOrdinal);

internal sealed record BlockReceiptGasAccountingFunction(
    string Name,
    string Signature,
    string ReturnType,
    IReadOnlyList<BlockReceiptGasAccountingParameter> Parameters,
    BlockReceiptGasAccountingExpression Body);

internal sealed record BlockReceiptGasAccountingParameter(int Ordinal, string Name, string Type);

internal sealed record BlockReceiptGasAccountingExpression(
    string Kind,
    string Type,
    string? Value,
    IReadOnlyList<BlockReceiptGasAccountingExpression> Children);

internal sealed record BlockReceiptGasAccountingProductionDelegation(
    string MethodName,
    string KernelMethod,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string> ResultFields,
    string HeaderUpdateGuard);

internal static class BlockReceiptGasAccountingLeanEmitter
{
    private const string KernelTypeName = "Nethermind.Blockchain.Tracing.BlockReceiptGasAccountingKernel";
    private const string ResultTypeName = "Nethermind.Blockchain.Tracing.BlockReceiptGasAccountingResult";
    private const string EthereumGasPolicyTypeName = "Nethermind.Evm.GasPolicy.EthereumGasPolicy";
    private const string BlockGasAccountingTypeName = "Nethermind.Evm.GasPolicy.BlockGasAccountingKernel";
    private const string BlockReceiptsTracerTypeName = "BlockReceiptsTracer";
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static void ValidateRoots(IReadOnlyList<IMethodSymbol> roots)
    {
        if (roots.Count != 2)
        {
            throw new ExtractionException("Block receipt gas accounting Lean emission requires exactly two roots.");
        }

        ValidateRoot(
            roots[0],
            "Accumulate",
            ["ulong", "ulong", "ulong", "ulong", "ulong", "ulong"]);
        ValidateRoot(
            roots[1],
            "FromTotals",
            ["ulong", "ulong", "ulong"]);
    }

    public static BlockReceiptGasAccountingKernelModel Normalize(
        SemanticModel semanticModel,
        IReadOnlyList<IMethodSymbol> roots,
        IReadOnlyList<IMethodSymbol> extractedMethods,
        string transactionGasInitializationSourcePath,
        string ethereumGasPolicySourcePath,
        string blockReceiptsTracerSourcePath)
    {
        ValidateRoots(roots);
        if (extractedMethods.Count != 2 ||
            !extractedMethods.Select(StateGasChargeExtractor.Display).Order(StringComparer.Ordinal).SequenceEqual(
                roots.Select(StateGasChargeExtractor.Display).Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new ExtractionException("Block receipt gas accounting extraction did not retain exactly the pinned root graph.");
        }

        INamedTypeSymbol resultType = roots[0].ReturnType as INamedTypeSymbol
            ?? throw new ExtractionException("Block receipt gas accounting result is not a named value type.");
        IReadOnlyList<BlockReceiptGasAccountingField> fields = NormalizeResultFields(semanticModel, resultType);
        ValidateCombineSources(transactionGasInitializationSourcePath, ethereumGasPolicySourcePath);
        IReadOnlyList<BlockReceiptGasAccountingProductionDelegation> delegations =
            ValidateTracerDelegations(blockReceiptsTracerSourcePath);
        ModelBuilder builder = new(semanticModel, roots[1], resultType, fields);
        BlockReceiptGasAccountingKernelModel model = new(
            fields,
            [builder.NormalizeAccumulate(roots[0]), builder.NormalizeFromTotals(roots[1])],
            delegations);
        ValidateModel(model);
        return model;
    }

    public static byte[] Emit(
        BlockReceiptGasAccountingKernelModel model,
        string extractorVersion,
        string compilerVersion,
        string sourcePath,
        string sourceHash,
        string irHash)
    {
        ValidateModel(model);
        BlockReceiptGasAccountingFunction accumulate = model.Functions.Single(static function => function.Name == "accumulate");
        BlockReceiptGasAccountingFunction fromTotals = model.Functions.Single(static function => function.Name == "fromTotals");
        string source = $$"""
            -- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
            -- SPDX-License-Identifier: LGPL-3.0-only

            -- This file is generated. Do not edit.
            -- Extractor version: {{extractorVersion}}
            -- Roslyn compiler version: {{compilerVersion}}
            -- Production source: {{sourcePath}}
            -- Production source SHA-256: {{sourceHash}}
            -- Canonical block-receipt-gas-accounting IR SHA-256: {{irHash}}

            import Eip803x.Generated.TransactionGasInitializationKernel

            namespace Eip803x.Generated.BlockReceiptGasAccountingKernel

            def uint64Max : Nat := Eip803x.Generated.TransactionGasInitializationKernel.uint64Max

            def normalizeUInt64 (value : Nat) : Nat :=
              Eip803x.Generated.TransactionGasInitializationKernel.normalizeUInt64 value

            def addUInt64 (left right : Nat) : Nat :=
              Eip803x.Generated.TransactionGasInitializationKernel.addUInt64 left right

            /-- Source-validated `EthereumGasPolicy.CombineBlockGas` delegation. -/
            def combineBlockGas (blockExecutionGas blockStateGas : Nat) : Nat :=
              Eip803x.Generated.TransactionGasInitializationKernel.combine blockExecutionGas blockStateGas

            structure Result where
            {{RenderResultFields(model.ResultFields)}}
              deriving DecidableEq, Repr

            {{RenderFunction(fromTotals, model.ResultFields)}}

            {{RenderWrapper(fromTotals)}}

            {{RenderFunction(accumulate, model.ResultFields)}}

            {{RenderWrapper(accumulate)}}

            end Eip803x.Generated.BlockReceiptGasAccountingKernel
            """;
        return Utf8WithoutBom.GetBytes(source.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    }

    private static void ValidateRoot(
        IMethodSymbol method,
        string name,
        IReadOnlyList<string> parameterTypes)
    {
        if (method.ContainingType.ToDisplayString() != KernelTypeName ||
            method.Name != name ||
            method.ReturnType.ToDisplayString() != ResultTypeName ||
            !method.IsStatic ||
            method.Parameters.Length != parameterTypes.Count ||
            method.Parameters.Where((parameter, index) => parameter.Type.ToDisplayString() != parameterTypes[index]).Any())
        {
            throw new ExtractionException($"Block receipt gas accounting root '{method}' does not match its pinned signature.");
        }
    }

    private static IReadOnlyList<BlockReceiptGasAccountingField> NormalizeResultFields(
        SemanticModel semanticModel,
        INamedTypeSymbol resultType)
    {
        if (resultType.ToDisplayString() != ResultTypeName ||
            !resultType.IsReadOnly ||
            resultType.DeclaringSyntaxReferences is not [SyntaxReference typeReference] ||
            typeReference.GetSyntax() is not StructDeclarationSyntax declaration ||
            declaration.ParameterList is null ||
            declaration.AttributeLists.Count != 0 ||
            declaration.BaseList is not null)
        {
            throw new ExtractionException("Block receipt gas accounting result must be the pinned readonly primary-constructor struct.");
        }

        string[] expectedNames =
        [
            "CumulativeExecutionGas",
            "CumulativeStateGas",
            "CumulativeReceiptGas",
            "HeaderGasUsed",
        ];
        string[] expectedParameters =
        [
            "cumulativeExecutionGas",
            "cumulativeStateGas",
            "cumulativeReceiptGas",
            "headerGasUsed",
        ];
        FieldDeclarationSyntax[] declarations = declaration.Members.OfType<FieldDeclarationSyntax>().ToArray();
        if (declarations.Length != expectedNames.Length ||
            declaration.Members.Count != declarations.Length ||
            declaration.ParameterList.Parameters.Count != expectedParameters.Length)
        {
            throw new ExtractionException("Block receipt gas accounting result does not contain the pinned field shape.");
        }

        IMethodSymbol constructor = resultType.InstanceConstructors.Single(method =>
            method.Parameters.Length == expectedParameters.Length &&
            !method.Parameters.Where((parameter, index) =>
                parameter.Name != expectedParameters[index] ||
                parameter.Type.SpecialType != SpecialType.System_UInt64).Any());

        BlockReceiptGasAccountingField[] fields = new BlockReceiptGasAccountingField[expectedNames.Length];
        for (int index = 0; index < declarations.Length; index++)
        {
            FieldDeclarationSyntax fieldDeclaration = declarations[index];
            if (!fieldDeclaration.Modifiers.Any(SyntaxKind.PublicKeyword) ||
                !fieldDeclaration.Modifiers.Any(SyntaxKind.ReadOnlyKeyword) ||
                fieldDeclaration.Modifiers.Any(SyntaxKind.StaticKeyword) ||
                fieldDeclaration.Declaration.Type is not PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.ULongKeyword } ||
                fieldDeclaration.Declaration.Variables is not [{ Initializer.Value: IdentifierNameSyntax initializer }])
            {
                throw new ExtractionException("Block receipt gas accounting result contains a field outside the pinned projection shape.");
            }

            VariableDeclaratorSyntax variable = fieldDeclaration.Declaration.Variables[0];
            IFieldSymbol field = semanticModel.GetDeclaredSymbol(variable) as IFieldSymbol
                ?? throw new ExtractionException($"Roslyn did not bind result field '{variable.Identifier.ValueText}'.");
            IParameterSymbol parameter = semanticModel.GetSymbolInfo(initializer).Symbol as IParameterSymbol
                ?? throw new ExtractionException($"Roslyn did not bind result field initializer '{initializer}'.");
            if (field.Name != expectedNames[index] ||
                parameter.Name != expectedParameters[index] ||
                parameter.Ordinal != index ||
                !SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, constructor) ||
                !SymbolEqualityComparer.Default.Equals(field.Type, parameter.Type) ||
                field.Type.SpecialType != SpecialType.System_UInt64)
            {
                throw new ExtractionException("Block receipt gas accounting result fields do not project their corresponding primary-constructor parameters.");
            }

            fields[index] = new BlockReceiptGasAccountingField(field.Name, "Nat", parameter.Ordinal);
        }

        return fields;
    }

    private static void ValidateCombineSources(
        string transactionGasInitializationSourcePath,
        string ethereumGasPolicySourcePath)
    {
        CompilationUnitSyntax transactionSource = ParseSource(transactionGasInitializationSourcePath);
        ClassDeclarationSyntax blockGasAccounting = transactionSource.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .SingleOrDefault(static declaration => declaration.Identifier.ValueText == "BlockGasAccountingKernel")
            ?? throw new ExtractionException("Transaction gas initialization source does not declare BlockGasAccountingKernel exactly once.");
        MethodDeclarationSyntax combine = blockGasAccounting.Members.OfType<MethodDeclarationSyntax>()
            .SingleOrDefault(static declaration => declaration.Identifier.ValueText == "Combine")
            ?? throw new ExtractionException("BlockGasAccountingKernel does not declare Combine exactly once.");
        if (!HasPinnedUlongSignature(combine, "Combine", ["blockExecutionGas", "blockStateGas"]) ||
            combine.Body is not null ||
            !IsStaticInvocation(
                combine.ExpressionBody?.Expression,
                "Math",
                "Max",
                ["blockExecutionGas", "blockStateGas"]))
        {
            throw new ExtractionException("BlockGasAccountingKernel.Combine must retain the pinned Math.Max delegation.");
        }

        CompilationUnitSyntax ethereumSource = ParseSource(ethereumGasPolicySourcePath);
        StructDeclarationSyntax ethereumGasPolicy = ethereumSource.DescendantNodes().OfType<StructDeclarationSyntax>()
            .SingleOrDefault(static declaration => declaration.Identifier.ValueText == "EthereumGasPolicy")
            ?? throw new ExtractionException("Ethereum gas policy source does not declare EthereumGasPolicy exactly once.");
        MethodDeclarationSyntax ethereumCombine = ethereumGasPolicy.Members.OfType<MethodDeclarationSyntax>()
            .SingleOrDefault(static declaration => declaration.Identifier.ValueText == "CombineBlockGas")
            ?? throw new ExtractionException("EthereumGasPolicy does not declare CombineBlockGas exactly once.");
        if (!HasPinnedUlongSignature(ethereumCombine, "CombineBlockGas", ["blockExecutionGas", "blockStateGas"]) ||
            ethereumCombine.Body is not null ||
            !IsStaticInvocation(
                ethereumCombine.ExpressionBody?.Expression,
                "BlockGasAccountingKernel",
                "Combine",
                ["blockExecutionGas", "blockStateGas"]))
        {
            throw new ExtractionException("EthereumGasPolicy.CombineBlockGas must retain the pinned BlockGasAccountingKernel.Combine delegation.");
        }
    }

    private static IReadOnlyList<BlockReceiptGasAccountingProductionDelegation> ValidateTracerDelegations(string sourcePath)
    {
        CompilationUnitSyntax source = ParseSource(sourcePath);
        ClassDeclarationSyntax tracer = source.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .SingleOrDefault(static declaration => declaration.Identifier.ValueText == BlockReceiptsTracerTypeName)
            ?? throw new ExtractionException("Block receipts tracer source does not declare BlockReceiptsTracer exactly once.");
        MethodDeclarationSyntax update = tracer.Members.OfType<MethodDeclarationSyntax>()
            .SingleOrDefault(static declaration => declaration.Identifier.ValueText == "UpdateCumulativeGasTracking")
            ?? throw new ExtractionException("BlockReceiptsTracer does not declare UpdateCumulativeGasTracking exactly once.");
        MethodDeclarationSyntax restore = tracer.Members.OfType<MethodDeclarationSyntax>()
            .SingleOrDefault(static declaration => declaration.Identifier.ValueText == "Restore")
            ?? throw new ExtractionException("BlockReceiptsTracer does not declare Restore exactly once.");

        string[] updateArguments =
        [
            "prevExecution",
            "prevState",
            "_cumulativeReceiptGas",
            "gasConsumed.EffectiveBlockGas",
            "gasConsumed.BlockStateGas",
            "gasConsumed.SpentGas",
        ];
        RequireAccountingLocal(update, "Accumulate", updateArguments);
        RequireInvocation(
            update,
            "_cumulativeBlockGasPerTx.Add",
            ["(accounting.CumulativeExecutionGas, accounting.CumulativeStateGas)"]);
        RequireAssignment(update, "_cumulativeReceiptGas", "accounting.CumulativeReceiptGas");
        RequireReturn(update, "_cumulativeReceiptGas");
        IfStatementSyntax sequentialHeaderUpdate = update.DescendantNodes().OfType<IfStatementSyntax>()
            .SingleOrDefault(static statement => statement.Condition.ToString() == "!parallel")
            ?? throw new ExtractionException("UpdateCumulativeGasTracking must retain its sequential header update guard.");
        if (sequentialHeaderUpdate.Else is not null ||
            !HasAssignment(sequentialHeaderUpdate, "Block.Header.GasUsed", "accounting.HeaderGasUsed"))
        {
            throw new ExtractionException("UpdateCumulativeGasTracking must assign Header.GasUsed from the accounting result under !parallel.");
        }

        string[] restoreArguments = ["cumulativeExecution", "cumulativeState", "cumulativeReceipt"];
        RequireAccountingLocal(restore, "FromTotals", restoreArguments);
        RequireAssignment(restore, "Block.Header.GasUsed", "accounting.HeaderGasUsed");
        RequireAssignment(restore, "_cumulativeReceiptGas", "accounting.CumulativeReceiptGas");

        string[] resultFields =
        [
            "CumulativeExecutionGas",
            "CumulativeStateGas",
            "CumulativeReceiptGas",
            "HeaderGasUsed",
        ];
        return
        [
            new BlockReceiptGasAccountingProductionDelegation(
                "UpdateCumulativeGasTracking",
                "Accumulate",
                updateArguments,
                resultFields,
                "!parallel"),
            new BlockReceiptGasAccountingProductionDelegation(
                "Restore",
                "FromTotals",
                restoreArguments,
                resultFields,
                "always"),
        ];
    }

    private static void RequireAccountingLocal(
        MethodDeclarationSyntax method,
        string kernelMethod,
        IReadOnlyList<string> expectedArguments)
    {
        if (method.Body is null ||
            method.Body.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
                .SingleOrDefault(static statement => statement.Declaration.Variables is [{ Identifier.ValueText: "accounting" }])
                is not LocalDeclarationStatementSyntax accountingDeclaration ||
            accountingDeclaration.Declaration.Type.ToString() != "BlockReceiptGasAccountingResult" ||
            accountingDeclaration.Declaration.Variables is not [{ Initializer.Value: ExpressionSyntax initializer }] ||
            !IsStaticInvocation(initializer, "BlockReceiptGasAccountingKernel", kernelMethod, expectedArguments))
        {
            throw new ExtractionException(
                $"{method.Identifier.ValueText} must construct its pinned accounting result from BlockReceiptGasAccountingKernel.{kernelMethod}.");
        }
    }

    private static void RequireInvocation(
        MethodDeclarationSyntax method,
        string receiverAndMethod,
        IReadOnlyList<string> expectedArguments)
    {
        if (method.Body is null ||
            method.Body.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Count(invocation => invocation.Expression.ToString() == receiverAndMethod &&
                    ArgumentsMatch(invocation.ArgumentList.Arguments, expectedArguments)) != 1)
        {
            throw new ExtractionException(
                $"{method.Identifier.ValueText} must retain its pinned invocation of '{receiverAndMethod}'.");
        }
    }

    private static void RequireAssignment(MethodDeclarationSyntax method, string left, string right)
    {
        if (method.Body is null || !HasAssignment(method, left, right))
        {
            throw new ExtractionException(
                $"{method.Identifier.ValueText} must retain assignment '{left} = {right}'.");
        }
    }

    private static bool HasAssignment(SyntaxNode node, string left, string right) =>
        node.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(assignment =>
            assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
            assignment.Left.ToString() == left &&
            assignment.Right.ToString() == right);

    private static void RequireReturn(MethodDeclarationSyntax method, string expression)
    {
        if (method.Body?.Statements.LastOrDefault() is not ReturnStatementSyntax { Expression: ExpressionSyntax returnExpression } ||
            returnExpression.ToString() != expression)
        {
            throw new ExtractionException($"{method.Identifier.ValueText} must retain return '{expression}'.");
        }
    }

    private static bool HasPinnedUlongSignature(
        MethodDeclarationSyntax declaration,
        string name,
        IReadOnlyList<string> parameterNames) =>
        declaration.Identifier.ValueText == name &&
        declaration.Modifiers.Any(SyntaxKind.PublicKeyword) &&
        declaration.Modifiers.Any(SyntaxKind.StaticKeyword) &&
        declaration.ReturnType is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.ULongKeyword } &&
        declaration.ParameterList.Parameters.Count == parameterNames.Count &&
        !declaration.ParameterList.Parameters.Where((parameter, index) =>
            parameter.Type is not PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.ULongKeyword } ||
            parameter.Identifier.ValueText != parameterNames[index]).Any();

    private static bool IsStaticInvocation(
        ExpressionSyntax? expression,
        string containingType,
        string methodName,
        IReadOnlyList<string> expectedArguments) =>
        expression is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax
            {
                Expression: IdentifierNameSyntax { Identifier.ValueText: var typeName },
                Name: IdentifierNameSyntax { Identifier.ValueText: var invokedName },
            },
            ArgumentList.Arguments: SeparatedSyntaxList<ArgumentSyntax> arguments,
        } &&
        typeName == containingType &&
        invokedName == methodName &&
        ArgumentsMatch(arguments, expectedArguments);

    private static bool ArgumentsMatch(
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        IReadOnlyList<string> expectedArguments) =>
        arguments.Count == expectedArguments.Count &&
        !arguments.Where((argument, index) =>
            argument.NameColon is not null ||
            argument.RefKindKeyword.RawKind != 0 ||
            argument.Expression.ToString() != expectedArguments[index]).Any();

    private static CompilationUnitSyntax ParseSource(string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new ExtractionException($"Pinned supporting source was not found: {sourcePath}");
        }

        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(
            File.ReadAllText(sourcePath),
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp14),
            sourcePath);
        Diagnostic[] diagnostics = syntaxTree.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity is DiagnosticSeverity.Error)
            .ToArray();
        if (diagnostics.Length != 0)
        {
            throw new ExtractionException($"Pinned supporting source did not parse: {diagnostics[0]}");
        }

        return syntaxTree.GetCompilationUnitRoot();
    }

    private static void ValidateModel(BlockReceiptGasAccountingKernelModel model)
    {
        string[] expectedFields =
        [
            "CumulativeExecutionGas",
            "CumulativeStateGas",
            "CumulativeReceiptGas",
            "HeaderGasUsed",
        ];
        if (!model.ResultFields.Select(static field => field.Name).SequenceEqual(expectedFields, StringComparer.Ordinal) ||
            model.ResultFields.Any(static field => field.Type != "Nat") ||
            !model.ResultFields.Select(static field => field.ConstructorParameterOrdinal)
                .SequenceEqual([0, 1, 2, 3]) ||
            model.Functions.Count != 2 ||
            !model.Functions.Select(static function => function.Name)
                .SequenceEqual(["accumulate", "fromTotals"], StringComparer.Ordinal) ||
            model.ProductionDelegations.Count != 2 ||
            !model.ProductionDelegations.Select(static delegation => delegation.MethodName)
                .SequenceEqual(["UpdateCumulativeGasTracking", "Restore"], StringComparer.Ordinal))
        {
            throw new ExtractionException("Block receipt gas accounting model does not have the pinned canonical shape.");
        }

        foreach (BlockReceiptGasAccountingFunction function in model.Functions)
        {
            if (function.ReturnType != "Result" ||
                function.Parameters.Any(static parameter => parameter.Type != "Nat") ||
                function.Body.Type != "Result")
            {
                throw new ExtractionException("Block receipt gas accounting model contains an unsupported function type.");
            }

            ValidateExpression(function.Body);
        }

        foreach (BlockReceiptGasAccountingProductionDelegation delegation in model.ProductionDelegations)
        {
            if (delegation.KernelMethod is not ("Accumulate" or "FromTotals") ||
                delegation.ResultFields.Count != expectedFields.Length ||
                !delegation.ResultFields.SequenceEqual(expectedFields, StringComparer.Ordinal) ||
                delegation.HeaderUpdateGuard is not ("!parallel" or "always"))
            {
                throw new ExtractionException("Block receipt gas accounting production delegation is not canonical.");
            }
        }
    }

    private static void ValidateExpression(BlockReceiptGasAccountingExpression expression)
    {
        bool valid = expression.Kind switch
        {
            "parameter" or "local" => expression.Children.Count == 0 && expression.Value is not null,
            "addUInt64" or "fromTotals" or "combineBlockGas" => expression.Children.Count == 2 ||
                expression.Kind == "fromTotals" && expression.Children.Count == 3,
            "construct" => expression.Children.Count == 4,
            "let" => expression.Children.Count == 2 && expression.Value is not null,
            _ => false,
        };
        if (!valid)
        {
            throw new ExtractionException($"Block receipt gas accounting expression '{expression.Kind}' is not canonical.");
        }

        foreach (BlockReceiptGasAccountingExpression child in expression.Children)
        {
            ValidateExpression(child);
        }
    }

    private static string RenderResultFields(IReadOnlyList<BlockReceiptGasAccountingField> fields) => string.Join(
        Environment.NewLine,
        fields.Select(static field => $"  {LowerFirst(field.Name)} : {field.Type}"));

    private static string RenderFunction(
        BlockReceiptGasAccountingFunction function,
        IReadOnlyList<BlockReceiptGasAccountingField> resultFields) =>
        $"def {function.Name}Normalized\n" +
        string.Join(Environment.NewLine, function.Parameters.Select(static parameter =>
            $"    ({parameter.Name} : {parameter.Type})")) +
        $"\n    : {function.ReturnType} :=\n" +
        "  " + RenderExpression(function.Body, resultFields, 2);

    private static string RenderWrapper(BlockReceiptGasAccountingFunction function) =>
        $"def {function.Name}\n" +
        string.Join(Environment.NewLine, function.Parameters.Select(static parameter =>
            $"    ({parameter.Name} : {parameter.Type})")) +
        $"\n    : {function.ReturnType} :=\n" +
        $"  {function.Name}Normalized " + string.Join(" ", function.Parameters.Select(static parameter =>
            $"(normalizeUInt64 {parameter.Name})"));

    private static string RenderExpression(
        BlockReceiptGasAccountingExpression expression,
        IReadOnlyList<BlockReceiptGasAccountingField> resultFields,
        int indentation) => expression.Kind switch
    {
        "parameter" or "local" => expression.Value!,
        "addUInt64" => $"addUInt64 ({RenderExpression(expression.Children[0], resultFields, indentation)}) ({RenderExpression(expression.Children[1], resultFields, indentation)})",
        "fromTotals" => $"fromTotalsNormalized {string.Join(" ", expression.Children.Select(child => $"({RenderExpression(child, resultFields, indentation)})"))}",
        "combineBlockGas" => $"combineBlockGas ({RenderExpression(expression.Children[0], resultFields, indentation)}) ({RenderExpression(expression.Children[1], resultFields, indentation)})",
        "construct" => RenderConstruct(expression, resultFields, indentation),
        "let" => RenderLet(expression, resultFields, indentation),
        _ => throw new ExtractionException($"Block receipt gas accounting expression '{expression.Kind}' cannot be emitted."),
    };

    private static string RenderConstruct(
        BlockReceiptGasAccountingExpression expression,
        IReadOnlyList<BlockReceiptGasAccountingField> resultFields,
        int indentation)
    {
        StringBuilder builder = new("{ ");
        for (int index = 0; index < resultFields.Count; index++)
        {
            if (index != 0)
            {
                builder.AppendLine();
                builder.Append(new string(' ', indentation + 2));
            }

            BlockReceiptGasAccountingField field = resultFields[index];
            builder.Append(LowerFirst(field.Name));
            builder.Append(" := ");
            builder.Append(RenderExpression(expression.Children[field.ConstructorParameterOrdinal], resultFields, indentation + 2));
        }

        builder.Append(" }");
        return builder.ToString();
    }

    private static string RenderLet(
        BlockReceiptGasAccountingExpression expression,
        IReadOnlyList<BlockReceiptGasAccountingField> resultFields,
        int indentation)
    {
        string spaces = new(' ', indentation);
        return $"let {expression.Value} : Nat := {RenderExpression(expression.Children[0], resultFields, indentation)}\n" +
               $"{spaces}{RenderExpression(expression.Children[1], resultFields, indentation)}";
    }

    private static string LowerFirst(string value) => char.ToLowerInvariant(value[0]) + value[1..];

    private sealed class ModelBuilder(
        SemanticModel semanticModel,
        IMethodSymbol fromTotals,
        INamedTypeSymbol resultType,
        IReadOnlyList<BlockReceiptGasAccountingField> resultFields)
    {
        private readonly SemanticModel _semanticModel = semanticModel;
        private readonly IMethodSymbol _fromTotals = fromTotals;
        private readonly INamedTypeSymbol _resultType = resultType;
        private readonly IReadOnlyList<BlockReceiptGasAccountingField> _resultFields = resultFields;

        public BlockReceiptGasAccountingFunction NormalizeAccumulate(IMethodSymbol method)
        {
            MethodDeclarationSyntax declaration = GetDeclaration(method);
            if (declaration.ExpressionBody is not null || declaration.Body?.Statements.Count != 4)
            {
                throw new ExtractionException("Block receipt gas Accumulate must retain its pinned statement shape.");
            }

            LocalDeclarationStatementSyntax[] locals =
            [
                RequireLocal(declaration.Body.Statements[0]),
                RequireLocal(declaration.Body.Statements[1]),
                RequireLocal(declaration.Body.Statements[2]),
            ];
            string[] expectedNames =
            [
                "cumulativeExecutionGas",
                "cumulativeStateGas",
                "cumulativeReceiptGas",
            ];
            Dictionary<ISymbol, string> names = ParameterNames(method);
            for (int index = 0; index < locals.Length; index++)
            {
                ILocalSymbol local = GetLocalSymbol(locals[index]);
                if (local.Name != expectedNames[index] || local.Type.SpecialType != SpecialType.System_UInt64)
                {
                    throw new ExtractionException("Block receipt gas Accumulate local declarations do not match the pinned shape.");
                }

                names.Add(local, Identifier(local.Name));
            }

            BlockReceiptGasAccountingExpression body = NormalizeReturn(
                RequireReturn(declaration.Body.Statements[3]),
                names);
            for (int index = locals.Length - 1; index >= 0; index--)
            {
                ILocalSymbol local = GetLocalSymbol(locals[index]);
                body = Let(
                    Identifier(local.Name),
                    NormalizeExpression(GetInitializer(locals[index]), names),
                    body);
            }

            return Function("accumulate", method, body);
        }

        public BlockReceiptGasAccountingFunction NormalizeFromTotals(IMethodSymbol method)
        {
            MethodDeclarationSyntax declaration = GetDeclaration(method);
            if (declaration.Body is not null || declaration.ExpressionBody?.Expression is not ExpressionSyntax expression)
            {
                throw new ExtractionException("Block receipt gas FromTotals must retain its pinned expression body.");
            }

            return Function("fromTotals", method, NormalizeExpression(expression, ParameterNames(method)));
        }

        private BlockReceiptGasAccountingFunction Function(
            string name,
            IMethodSymbol method,
            BlockReceiptGasAccountingExpression body) =>
            new(
                name,
                StateGasChargeExtractor.Display(method),
                "Result",
                method.Parameters.Select(static parameter => new BlockReceiptGasAccountingParameter(
                    parameter.Ordinal,
                    Identifier(parameter.Name),
                    "Nat")).ToArray(),
                body);

        private BlockReceiptGasAccountingExpression NormalizeReturn(
            ReturnStatementSyntax statement,
            IReadOnlyDictionary<ISymbol, string> names) =>
            statement.Expression is ExpressionSyntax expression
                ? NormalizeExpression(expression, names)
                : throw new ExtractionException("Block receipt gas accounting return must have a value.");

        private BlockReceiptGasAccountingExpression NormalizeExpression(
            ExpressionSyntax syntax,
            IReadOnlyDictionary<ISymbol, string> names)
        {
            if (syntax is CheckedExpressionSyntax
                {
                    Keyword.RawKind: (int)SyntaxKind.UncheckedKeyword,
                    Expression: ExpressionSyntax uncheckedExpression,
                })
            {
                return NormalizeExpression(uncheckedExpression, names);
            }

            if (syntax is CheckedExpressionSyntax)
            {
                throw new ExtractionException("Block receipt gas accounting checked arithmetic is outside the pinned source shape.");
            }

            return NormalizeOperation(
                _semanticModel.GetOperation(syntax)
                    ?? throw new ExtractionException($"Roslyn did not bind block receipt gas expression '{syntax}'."),
                names);
        }

        private BlockReceiptGasAccountingExpression NormalizeOperation(
            IOperation operation,
            IReadOnlyDictionary<ISymbol, string> names)
        {
            if (operation is IConversionOperation conversion)
            {
                if (!conversion.Conversion.IsIdentity || conversion.IsChecked)
                {
                    throw new ExtractionException("Block receipt gas accounting conversion is not an unchecked identity conversion.");
                }

                return NormalizeOperation(conversion.Operand, names);
            }

            return operation switch
            {
                IParameterReferenceOperation parameter when names.TryGetValue(parameter.Parameter, out string? name) =>
                    Leaf("parameter", "Nat", name),
                ILocalReferenceOperation local when names.TryGetValue(local.Local, out string? name) =>
                    Leaf("local", "Nat", name),
                IBinaryOperation binary => NormalizeBinary(binary, names),
                IInvocationOperation invocation => NormalizeInvocation(invocation, names),
                IObjectCreationOperation creation => NormalizeObjectCreation(creation, names),
                _ => throw new ExtractionException(
                    $"Block receipt gas accounting operation '{operation.Kind}' is not supported by Lean emission."),
            };
        }

        private BlockReceiptGasAccountingExpression NormalizeBinary(
            IBinaryOperation operation,
            IReadOnlyDictionary<ISymbol, string> names)
        {
            if (operation.OperatorKind != BinaryOperatorKind.Add ||
                operation.Type?.SpecialType != SpecialType.System_UInt64 ||
                operation.IsChecked)
            {
                throw new ExtractionException($"Block receipt gas binary operator '{operation.OperatorKind}' is not supported.");
            }

            return Node(
                "addUInt64",
                "Nat",
                null,
                NormalizeOperation(operation.LeftOperand, names),
                NormalizeOperation(operation.RightOperand, names));
        }

        private BlockReceiptGasAccountingExpression NormalizeInvocation(
            IInvocationOperation invocation,
            IReadOnlyDictionary<ISymbol, string> names)
        {
            BlockReceiptGasAccountingExpression[] arguments = NormalizeArguments(invocation.Arguments, names);
            if (SymbolEqualityComparer.Default.Equals(invocation.TargetMethod, _fromTotals))
            {
                if (arguments.Length != 3)
                {
                    throw new ExtractionException("Block receipt gas FromTotals invocation has unsupported arguments.");
                }

                return Node("fromTotals", "Result", null, arguments);
            }

            if (invocation.TargetMethod.ContainingType.ToDisplayString() == EthereumGasPolicyTypeName &&
                invocation.TargetMethod.Name == "CombineBlockGas" &&
                arguments.Length == 2)
            {
                return Node("combineBlockGas", "Nat", null, arguments);
            }

            throw new ExtractionException($"Block receipt gas intrinsic '{invocation.TargetMethod}' is not allowed.");
        }

        private BlockReceiptGasAccountingExpression NormalizeObjectCreation(
            IObjectCreationOperation creation,
            IReadOnlyDictionary<ISymbol, string> names)
        {
            if (creation.Constructor is null ||
                !SymbolEqualityComparer.Default.Equals(creation.Constructor.ContainingType, _resultType))
            {
                throw new ExtractionException("Block receipt gas accounting object creation is not its pinned result value.");
            }

            BlockReceiptGasAccountingExpression[] arguments = NormalizeArguments(creation.Arguments, names);
            if (arguments.Length != _resultFields.Count)
            {
                throw new ExtractionException("Block receipt gas accounting result construction has an unsupported argument count.");
            }

            return Node("construct", "Result", null, arguments);
        }

        private BlockReceiptGasAccountingExpression[] NormalizeArguments(
            ImmutableArray<IArgumentOperation> arguments,
            IReadOnlyDictionary<ISymbol, string> names)
        {
            BlockReceiptGasAccountingExpression?[] ordered = new BlockReceiptGasAccountingExpression?[arguments.Length];
            foreach (IArgumentOperation argument in arguments)
            {
                if (argument.Parameter is null ||
                    argument.ArgumentKind != ArgumentKind.Explicit ||
                    argument.Parameter.Ordinal < 0 ||
                    argument.Parameter.Ordinal >= ordered.Length ||
                    ordered[argument.Parameter.Ordinal] is not null)
                {
                    throw new ExtractionException("Block receipt gas invocation arguments are not positional explicit values.");
                }

                ordered[argument.Parameter.Ordinal] = NormalizeOperation(argument.Value, names);
            }

            if (ordered.Any(static argument => argument is null))
            {
                throw new ExtractionException("Block receipt gas invocation arguments are incomplete.");
            }

            BlockReceiptGasAccountingExpression[] normalized = new BlockReceiptGasAccountingExpression[ordered.Length];
            for (int index = 0; index < ordered.Length; index++)
            {
                normalized[index] = ordered[index]!;
                if (normalized[index].Type != "Nat")
                {
                    throw new ExtractionException("Block receipt gas invocation argument is not UInt64.");
                }
            }

            return normalized;
        }

        private Dictionary<ISymbol, string> ParameterNames(IMethodSymbol method)
        {
            Dictionary<ISymbol, string> names = new(SymbolEqualityComparer.Default);
            foreach (IParameterSymbol parameter in method.Parameters)
            {
                names.Add(parameter, Identifier(parameter.Name));
            }

            return names;
        }

        private ILocalSymbol GetLocalSymbol(LocalDeclarationStatementSyntax statement)
        {
            if (statement.Declaration.Variables.Count != 1)
            {
                throw new ExtractionException("Block receipt gas local declaration must declare exactly one value.");
            }

            VariableDeclaratorSyntax variable = statement.Declaration.Variables[0];
            return _semanticModel.GetDeclaredSymbol(variable) as ILocalSymbol
                ?? throw new ExtractionException($"Roslyn local '{variable.Identifier.ValueText}' was not resolved.");
        }

        private static MethodDeclarationSyntax GetDeclaration(IMethodSymbol method) =>
            method.DeclaringSyntaxReferences.Single().GetSyntax() as MethodDeclarationSyntax
            ?? throw new ExtractionException($"Block receipt gas root '{method}' is not a method declaration.");

        private static LocalDeclarationStatementSyntax RequireLocal(StatementSyntax statement) =>
            statement as LocalDeclarationStatementSyntax
            ?? throw new ExtractionException("Block receipt gas accounting statement is not its pinned local declaration.");

        private static ReturnStatementSyntax RequireReturn(StatementSyntax statement) =>
            statement as ReturnStatementSyntax
            ?? throw new ExtractionException("Block receipt gas accounting statement is not its pinned return.");

        private static ExpressionSyntax GetInitializer(LocalDeclarationStatementSyntax statement) =>
            statement.Declaration.Variables is [{ Initializer.Value: ExpressionSyntax initializer }]
                ? initializer
                : throw new ExtractionException("Block receipt gas local declaration must have one initializer.");

        private static BlockReceiptGasAccountingExpression Let(
            string name,
            BlockReceiptGasAccountingExpression value,
            BlockReceiptGasAccountingExpression continuation) =>
            Node("let", continuation.Type, name, value, continuation);

        private static BlockReceiptGasAccountingExpression Leaf(string kind, string type, string value) =>
            new(kind, type, value, []);

        private static BlockReceiptGasAccountingExpression Node(
            string kind,
            string type,
            string? value,
            params BlockReceiptGasAccountingExpression[] children) =>
            new(kind, type, value, children);
    }

    private static string Identifier(string value)
    {
        if (string.IsNullOrEmpty(value) ||
            !char.IsLetter(value[0]) && value[0] != '_' ||
            value.Skip(1).Any(static character => !char.IsLetterOrDigit(character) && character != '_'))
        {
            throw new ExtractionException($"Identifier '{value}' cannot be emitted safely to Lean.");
        }

        return value;
    }
}
