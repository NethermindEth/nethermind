// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Evm.Lean.Extractor;

internal sealed record TransactionGasInitializationKernelModel(
    IReadOnlyList<string> OutcomeCases,
    IReadOnlyList<TransactionGasInitializationField> ResultFields,
    IReadOnlyList<TransactionGasInitializationFunction> Functions);

internal sealed record TransactionGasInitializationField(string Name, string Type, int ConstructorParameterOrdinal);

internal sealed record TransactionGasInitializationFunction(
    string Name,
    string Signature,
    string ReturnType,
    IReadOnlyList<TransactionGasInitializationParameter> Parameters,
    TransactionGasInitializationExpression Body);

internal sealed record TransactionGasInitializationParameter(int Ordinal, string Name, string Type);

internal sealed record TransactionGasInitializationExpression(
    string Kind,
    string Type,
    string? Value,
    string? Auxiliary,
    IReadOnlyList<TransactionGasInitializationExpression> Children);

internal static class TransactionGasInitializationLeanEmitter
{
    private const string InitializationKernelTypeName = "Nethermind.Evm.GasPolicy.TransactionGasInitializationKernel";
    private const string BlockAccountingKernelTypeName = "Nethermind.Evm.GasPolicy.BlockGasAccountingKernel";
    private const string ResultTypeName = "Nethermind.Evm.GasPolicy.TransactionGasInitializationResult";
    private const string OutcomeTypeName = "Nethermind.Evm.GasPolicy.TransactionGasInitializationOutcome";
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static void ValidateRoots(IReadOnlyList<IMethodSymbol> roots)
    {
        if (roots.Count != 2)
        {
            throw new ExtractionException("Transaction gas Lean emission requires exactly two roots.");
        }

        ValidateRoot(
            roots[0],
            InitializationKernelTypeName,
            "TryCreate",
            ResultTypeName,
            ["ulong", "ulong", "long", "bool", "ulong"]);
        ValidateRoot(
            roots[1],
            BlockAccountingKernelTypeName,
            "Combine",
            "ulong",
            ["ulong", "ulong"]);
    }

    public static TransactionGasInitializationKernelModel Normalize(
        SemanticModel semanticModel,
        IReadOnlyList<IMethodSymbol> roots,
        IReadOnlyList<IMethodSymbol> extractedMethods)
    {
        ValidateRoots(roots);
        if (extractedMethods.Count != 2 ||
            !extractedMethods.Select(StateGasChargeExtractor.Display).Order(StringComparer.Ordinal).SequenceEqual(
                roots.Select(StateGasChargeExtractor.Display).Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new ExtractionException("Transaction gas extraction did not retain exactly the pinned root graph.");
        }

        INamedTypeSymbol resultType = roots[0].ContainingAssembly.GetTypeByMetadataName(ResultTypeName)
            ?? throw new ExtractionException($"Required result type '{ResultTypeName}' was not found.");
        INamedTypeSymbol outcomeType = roots[0].ContainingAssembly.GetTypeByMetadataName(OutcomeTypeName)
            ?? throw new ExtractionException($"Required outcome type '{OutcomeTypeName}' was not found.");
        IReadOnlyList<string> outcomes = NormalizeOutcomeCases(outcomeType);
        IReadOnlyList<TransactionGasInitializationField> fields = NormalizeResultFields(semanticModel, resultType);
        ModelBuilder builder = new(semanticModel, resultType, outcomeType, fields);
        TransactionGasInitializationFunction tryCreate = builder.NormalizeTryCreate(roots[0]);
        TransactionGasInitializationFunction combine = builder.NormalizeCombine(roots[1]);
        return new TransactionGasInitializationKernelModel(outcomes, fields, [tryCreate, combine]);
    }

    public static byte[] Emit(
        TransactionGasInitializationKernelModel model,
        string extractorVersion,
        string compilerVersion,
        string sourcePath,
        string sourceHash,
        string irHash)
    {
        ValidateModel(model);
        StringBuilder functions = new();
        foreach (TransactionGasInitializationFunction function in model.Functions)
        {
            AppendNormalizedFunction(functions, function, model.ResultFields);
            AppendPublicWrapper(functions, function);
        }

        string source = $$"""
            -- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
            -- SPDX-License-Identifier: LGPL-3.0-only

            -- This file is generated. Do not edit.
            -- Extractor version: {{extractorVersion}}
            -- Roslyn compiler version: {{compilerVersion}}
            -- Production source: {{sourcePath}}
            -- Production source SHA-256: {{sourceHash}}
            -- Canonical transaction-gas IR SHA-256: {{irHash}}

            namespace Eip803x.Generated.TransactionGasInitializationKernel

            def uint64Modulus : Nat := 2 ^ 64
            def uint64Max : Nat := uint64Modulus - 1
            def int64Modulus : Int := (uint64Modulus : Int)
            def int64SignBit : Int := 2 ^ 63
            def int64Min : Int := -int64SignBit
            def int64Max : Int := int64SignBit - 1

            def normalizeUInt64 (value : Nat) : Nat :=
              if value <= uint64Max then value else value % uint64Modulus

            def wrapUInt64 (value : Int) : Nat :=
              if 0 <= value ∧ value <= (uint64Max : Int) then
                Int.toNat value
              else
                Int.toNat (value % int64Modulus)

            def wrapInt64 (value : Int) : Int :=
              if int64Min <= value ∧ value <= int64Max then
                value
              else
                let residue := value % int64Modulus
                if residue < int64SignBit then residue else residue - int64Modulus

            def addUInt64 (left right : Nat) : Nat :=
              wrapUInt64 ((left : Int) + (right : Int))

            def subUInt64 (left right : Nat) : Nat :=
              wrapUInt64 ((left : Int) - (right : Int))

            def int64ToUInt64 (value : Int) : Nat :=
              wrapUInt64 value

            def uint64ToInt64 (value : Nat) : Int :=
              wrapInt64 (value : Int)

            inductive TransactionGasInitializationOutcome where
            {{RenderOutcomeCases(model.OutcomeCases)}}
              deriving DecidableEq, Repr

            structure TransactionGasInitializationResult where
            {{RenderResultFields(model.ResultFields)}}
              deriving DecidableEq, Repr

            {{functions.ToString().TrimEnd()}}

            end Eip803x.Generated.TransactionGasInitializationKernel
            """;
        return Utf8WithoutBom.GetBytes(source.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    }

    private static void ValidateRoot(
        IMethodSymbol method,
        string typeName,
        string name,
        string returnType,
        IReadOnlyList<string> parameterTypes)
    {
        if (method.ContainingType.ToDisplayString() != typeName ||
            method.Name != name ||
            method.ReturnType.ToDisplayString() != returnType ||
            !method.IsStatic ||
            method.Parameters.Length != parameterTypes.Count ||
            method.Parameters.Where((parameter, index) => parameter.Type.ToDisplayString() != parameterTypes[index]).Any())
        {
            throw new ExtractionException($"Transaction gas root '{method}' does not match its pinned signature.");
        }
    }

    private static IReadOnlyList<string> NormalizeOutcomeCases(INamedTypeSymbol outcomeType)
    {
        if (outcomeType.TypeKind != TypeKind.Enum ||
            outcomeType.EnumUnderlyingType?.SpecialType != SpecialType.System_Byte)
        {
            throw new ExtractionException("Transaction gas outcome must be the pinned byte enum.");
        }

        IFieldSymbol[] fields = outcomeType.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(static field => field.HasConstantValue)
            .OrderBy(static field => field.ConstantValue)
            .ToArray();
        string[] expected = ["Success", "IntrinsicGasExceedsLimit"];
        if (!fields.Select(static field => field.Name).SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new ExtractionException("Transaction gas outcome must contain exactly the pinned cases.");
        }

        return fields.Select(static field => LowerFirst(field.Name)).ToArray();
    }

    private static IReadOnlyList<TransactionGasInitializationField> NormalizeResultFields(
        SemanticModel semanticModel,
        INamedTypeSymbol resultType)
    {
        (string Name, string Type)[] expected =
        [
            ("Outcome", OutcomeTypeName),
            ("Value", "ulong"),
            ("StateReservoir", "long"),
            ("StateGasUsed", "long"),
            ("StateGasSpill", "long"),
            ("StateGasSpillRefunded", "long"),
        ];
        IFieldSymbol[] fields = resultType.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(static field => !field.IsStatic && !field.IsImplicitlyDeclared)
            .OrderBy(static field => field.Locations[0].SourceSpan.Start)
            .ToArray();
        IMethodSymbol[] constructors = resultType.InstanceConstructors
            .Where(static constructor => !constructor.IsImplicitlyDeclared)
            .ToArray();
        if (!resultType.IsReadOnly || fields.Length != expected.Length || constructors.Length != 1 ||
            constructors[0].Parameters.Length != expected.Length)
        {
            throw new ExtractionException("Transaction gas result does not have the pinned readonly value shape.");
        }

        IMethodSymbol constructor = constructors[0];
        TransactionGasInitializationField[] normalized = new TransactionGasInitializationField[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            VariableDeclaratorSyntax declaration = fields[i].DeclaringSyntaxReferences.Single().GetSyntax()
                as VariableDeclaratorSyntax
                ?? throw new ExtractionException("Transaction gas result field is not a variable declaration.");
            if (declaration.Initializer?.Value is not IdentifierNameSyntax initializer ||
                semanticModel.GetSymbolInfo(initializer).Symbol is not IParameterSymbol parameter ||
                !SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, constructor) ||
                parameter.Ordinal != i ||
                fields[i].Name != expected[i].Name ||
                fields[i].Type.ToDisplayString() != expected[i].Type ||
                !fields[i].IsReadOnly ||
                fields[i].DeclaredAccessibility != Accessibility.Public ||
                !SymbolEqualityComparer.Default.Equals(fields[i].Type, constructor.Parameters[i].Type))
            {
                throw new ExtractionException(
                    "Transaction gas result fields must project their corresponding primary-constructor parameters.");
            }

            normalized[i] = new TransactionGasInitializationField(
                LowerFirst(fields[i].Name),
                LeanType(fields[i].Type.ToDisplayString()),
                i);
        }

        return normalized;
    }

    private static void ValidateModel(TransactionGasInitializationKernelModel model)
    {
        if (!model.OutcomeCases.SequenceEqual(["success", "intrinsicGasExceedsLimit"], StringComparer.Ordinal) ||
            model.ResultFields.Count != 6 ||
            model.Functions.Count != 2 ||
            model.Functions[0].Name != "TryCreate" ||
            model.Functions[1].Name != "Combine")
        {
            throw new ExtractionException("Transaction gas Lean model does not match the pinned profile.");
        }
    }

    private static void AppendNormalizedFunction(
        StringBuilder builder,
        TransactionGasInitializationFunction function,
        IReadOnlyList<TransactionGasInitializationField> resultFields)
    {
        builder.Append("def ");
        builder.Append(LowerFirst(function.Name));
        builder.AppendLine("Normalized");
        AppendParameters(builder, function.Parameters, 4);
        builder.Append("    : ");
        builder.Append(LeanType(function.ReturnType));
        builder.AppendLine(" :=");
        builder.AppendLine(Indent(Render(function.Body, resultFields), 2));
        builder.AppendLine();
    }

    private static void AppendPublicWrapper(StringBuilder builder, TransactionGasInitializationFunction function)
    {
        builder.Append("def ");
        builder.AppendLine(LowerFirst(function.Name));
        AppendParameters(builder, function.Parameters, 4);
        builder.Append("    : ");
        builder.Append(LeanType(function.ReturnType));
        builder.AppendLine(" :=");
        builder.Append("  ");
        builder.Append(LowerFirst(function.Name));
        builder.AppendLine("Normalized");
        foreach (TransactionGasInitializationParameter parameter in function.Parameters)
        {
            builder.Append("    ");
            builder.AppendLine(parameter.Type switch
            {
                "ulong" => $"(normalizeUInt64 {parameter.Name})",
                "long" => $"(wrapInt64 {parameter.Name})",
                "bool" => parameter.Name,
                _ => throw new ExtractionException($"Transaction gas wrapper parameter type '{parameter.Type}' is not supported."),
            });
        }
        builder.AppendLine();
    }

    private static void AppendParameters(
        StringBuilder builder,
        IReadOnlyList<TransactionGasInitializationParameter> parameters,
        int indent)
    {
        string prefix = new(' ', indent);
        foreach (TransactionGasInitializationParameter parameter in parameters)
        {
            builder.Append(prefix);
            builder.Append('(');
            builder.Append(parameter.Name);
            builder.Append(" : ");
            builder.Append(LeanType(parameter.Type));
            builder.AppendLine(")");
        }
    }

    private static string Render(
        TransactionGasInitializationExpression expression,
        IReadOnlyList<TransactionGasInitializationField> resultFields) => expression.Kind switch
        {
            "literal" or "variable" => expression.Value!,
            "outcome" => "." + expression.Value!,
            "compare" =>
                $"({Render(expression.Children[0], resultFields)} {expression.Value} " +
                $"{Render(expression.Children[1], resultFields)})",
            "uint64Add" => RenderCall("addUInt64", expression.Children, resultFields),
            "uint64Sub" => RenderCall("subUInt64", expression.Children, resultFields),
            "int64ToUInt64" => RenderCall("int64ToUInt64", expression.Children, resultFields),
            "uint64ToInt64" => RenderCall("uint64ToInt64", expression.Children, resultFields),
            "minUInt64" => RenderCall("min", expression.Children, resultFields),
            "maxUInt64" => RenderCall("max", expression.Children, resultFields),
            "if" =>
                $"if {Render(expression.Children[0], resultFields)} then\n" +
                $"{Indent(Render(expression.Children[1], resultFields), 2)}\nelse\n" +
                Indent(Render(expression.Children[2], resultFields), 2),
            "let" =>
                $"let {expression.Value} : {LeanType(expression.Auxiliary!)} := " +
                $"{Render(expression.Children[0], resultFields)}\n" +
                Render(expression.Children[1], resultFields),
            "construct" => RenderResult(expression, resultFields),
            _ => throw new ExtractionException($"Transaction gas expression '{expression.Kind}' is not renderable as Lean."),
        };

    private static string RenderResult(
        TransactionGasInitializationExpression expression,
        IReadOnlyList<TransactionGasInitializationField> resultFields)
    {
        if (expression.Children.Count != resultFields.Count)
        {
            throw new ExtractionException("Transaction gas result does not have the pinned field count.");
        }

        StringBuilder result = new("{ ");
        for (int i = 0; i < resultFields.Count; i++)
        {
            TransactionGasInitializationField field = resultFields[i];
            result.Append(i == 0 ? string.Empty : "  ");
            result.Append(field.Name);
            result.Append(" := ");
            result.Append(Render(expression.Children[i], resultFields));
            result.AppendLine(i == resultFields.Count - 1 ? " }" : string.Empty);
        }

        return result.ToString().TrimEnd();
    }

    private static string RenderCall(
        string name,
        IReadOnlyList<TransactionGasInitializationExpression> arguments,
        IReadOnlyList<TransactionGasInitializationField> resultFields) =>
        name + " " + string.Join(" ", arguments.Select(argument => Parenthesize(Render(argument, resultFields))));

    private static string RenderOutcomeCases(IReadOnlyList<string> outcomes) =>
        string.Join("\n", outcomes.Select(outcome => "  | " + outcome));

    private static string RenderResultFields(IReadOnlyList<TransactionGasInitializationField> fields)
    {
        (string Name, string Type)[] expected =
        [
            ("outcome", "TransactionGasInitializationOutcome"),
            ("value", "Nat"),
            ("stateReservoir", "Int"),
            ("stateGasUsed", "Int"),
            ("stateGasSpill", "Int"),
            ("stateGasSpillRefunded", "Int"),
        ];
        if (fields.Count != expected.Length)
        {
            throw new ExtractionException("Transaction gas result model does not contain the pinned fields.");
        }

        string[] lines = new string[fields.Count];
        for (int i = 0; i < fields.Count; i++)
        {
            if (fields[i].Name != expected[i].Name ||
                fields[i].Type != expected[i].Type ||
                fields[i].ConstructorParameterOrdinal != i)
            {
                throw new ExtractionException("Transaction gas result model does not match the pinned field order.");
            }

            lines[i] = $"  {fields[i].Name} : {fields[i].Type}";
        }

        return string.Join("\n", lines);
    }

    private static string Parenthesize(string value) => value.IndexOfAny([' ', '\n']) >= 0 ? $"({value})" : value;

    private static string Indent(string value, int spaces)
    {
        string prefix = new(' ', spaces);
        return prefix + value.Replace("\n", "\n" + prefix, StringComparison.Ordinal);
    }

    private static string LeanType(string type) => type switch
    {
        "ulong" => "Nat",
        "long" => "Int",
        "bool" => "Bool",
        ResultTypeName => "TransactionGasInitializationResult",
        OutcomeTypeName => "TransactionGasInitializationOutcome",
        _ => throw new ExtractionException($"C# type '{type}' is not supported by transaction gas Lean emission."),
    };

    private static string LowerFirst(string value) => char.ToLowerInvariant(value[0]) + value[1..];

    private sealed class ModelBuilder(
        SemanticModel semanticModel,
        INamedTypeSymbol resultType,
        INamedTypeSymbol outcomeType,
        IReadOnlyList<TransactionGasInitializationField> resultFields)
    {
        private readonly SemanticModel _semanticModel = semanticModel;
        private readonly INamedTypeSymbol _resultType = resultType;
        private readonly INamedTypeSymbol _outcomeType = outcomeType;
        private readonly IReadOnlyList<TransactionGasInitializationField> _resultFields = resultFields;

        public TransactionGasInitializationFunction NormalizeTryCreate(IMethodSymbol method)
        {
            MethodDeclarationSyntax declaration = GetDeclaration(method);
            if (declaration.Body is not { Statements.Count: 7 } body)
            {
                throw new ExtractionException("Transaction gas initialization must keep the pinned seven-statement body.");
            }

            Dictionary<ISymbol, string> names = ParameterNames(method);
            LocalDeclarationStatementSyntax intrinsicTotal = RequireLocal(body.Statements[0]);
            ILocalSymbol intrinsicTotalSymbol = GetLocalSymbol(intrinsicTotal);
            string intrinsicTotalName = Identifier(intrinsicTotalSymbol.Name);
            TransactionGasInitializationExpression intrinsicTotalValue = NormalizeExpression(
                GetInitializer(intrinsicTotal), names);
            names.Add(intrinsicTotalSymbol, intrinsicTotalName);

            IfStatementSyntax insufficient = RequireIf(body.Statements[1]);
            TransactionGasInitializationExpression insufficientCondition = NormalizeExpression(insufficient.Condition, names);
            TransactionGasInitializationExpression insufficientResult = NormalizeEarlyReturn(insufficient);

            LocalDeclarationStatementSyntax availableGas = RequireLocal(body.Statements[2]);
            ILocalSymbol availableGasSymbol = GetLocalSymbol(availableGas);
            string availableGasName = Identifier(availableGasSymbol.Name);
            TransactionGasInitializationExpression availableGasValue = NormalizeExpression(GetInitializer(availableGas), names);
            names.Add(availableGasSymbol, availableGasName);

            LocalDeclarationStatementSyntax gasLeft = RequireLocal(body.Statements[3]);
            ILocalSymbol gasLeftSymbol = GetLocalSymbol(gasLeft);
            string gasLeftName = Identifier(gasLeftSymbol.Name);
            TransactionGasInitializationExpression gasLeftValue = NormalizeExpression(GetInitializer(gasLeft), names);
            names.Add(gasLeftSymbol, gasLeftName);

            IfStatementSyntax eip8037 = RequireIf(body.Statements[4]);
            TransactionGasInitializationExpression eip8037Condition = NormalizeExpression(eip8037.Condition, names);
            TransactionGasInitializationExpression enabledGasLeft = NormalizeEnabledBranch(eip8037, gasLeftSymbol, names);
            string gasLeftAfterName = gasLeftName + "AfterEip8037";
            names[gasLeftSymbol] = gasLeftAfterName;

            LocalDeclarationStatementSyntax stateReservoir = RequireLocal(body.Statements[5]);
            ILocalSymbol stateReservoirSymbol = GetLocalSymbol(stateReservoir);
            string stateReservoirName = Identifier(stateReservoirSymbol.Name);
            TransactionGasInitializationExpression stateReservoirValue = NormalizeExpression(GetInitializer(stateReservoir), names);
            names.Add(stateReservoirSymbol, stateReservoirName);
            TransactionGasInitializationExpression successResult = NormalizeReturn(body.Statements[6], names);

            TransactionGasInitializationExpression eip8037Value = Node(
                "if",
                "ulong",
                null,
                null,
                eip8037Condition,
                enabledGasLeft,
                Leaf("variable", "ulong", gasLeftName));
            TransactionGasInitializationExpression successPath = Let(
                availableGasName,
                "ulong",
                availableGasValue,
                Let(
                    gasLeftName,
                    "ulong",
                    gasLeftValue,
                    Let(
                        gasLeftAfterName,
                        "ulong",
                        eip8037Value,
                        Let(stateReservoirName, "ulong", stateReservoirValue, successResult))));
            TransactionGasInitializationExpression bodyExpression = Let(
                intrinsicTotalName,
                "ulong",
                intrinsicTotalValue,
                Node("if", ResultTypeName, null, null, insufficientCondition, insufficientResult, successPath));
            return Function(method, bodyExpression);
        }

        public TransactionGasInitializationFunction NormalizeCombine(IMethodSymbol method)
        {
            MethodDeclarationSyntax declaration = GetDeclaration(method);
            if (declaration.ExpressionBody?.Expression is not ExpressionSyntax expression || declaration.Body is not null)
            {
                throw new ExtractionException("Block gas combination must remain the pinned expression-bodied function.");
            }

            return Function(method, NormalizeExpression(expression, ParameterNames(method)));
        }

        private TransactionGasInitializationExpression NormalizeEnabledBranch(
            IfStatementSyntax statement,
            ILocalSymbol assignedLocal,
            IReadOnlyDictionary<ISymbol, string> names)
        {
            if (statement.Else is not null || statement.Statement is not BlockSyntax { Statements.Count: 2 } block)
            {
                throw new ExtractionException("EIP-8037 branch must retain its pinned local-and-assignment shape.");
            }

            LocalDeclarationStatementSyntax cap = RequireLocal(block.Statements[0]);
            ILocalSymbol capSymbol = GetLocalSymbol(cap);
            string capName = Identifier(capSymbol.Name);
            Dictionary<ISymbol, string> branchNames = new(names, SymbolEqualityComparer.Default)
            {
                [capSymbol] = capName,
            };
            ExpressionStatementSyntax assignmentStatement = block.Statements[1] as ExpressionStatementSyntax
                ?? throw new ExtractionException("EIP-8037 branch must end in its pinned local assignment.");
            IAssignmentOperation assignment = _semanticModel.GetOperation(assignmentStatement.Expression) as IAssignmentOperation
                ?? throw new ExtractionException("Roslyn did not bind the EIP-8037 gas-left assignment.");
            if (assignment is not ISimpleAssignmentOperation { IsRef: false } ||
                assignment.Target is not ILocalReferenceOperation { Local: ILocalSymbol target } ||
                !SymbolEqualityComparer.Default.Equals(target, assignedLocal))
            {
                throw new ExtractionException("EIP-8037 branch may assign only its pinned gas-left local.");
            }

            return Let(
                capName,
                "ulong",
                NormalizeExpression(GetInitializer(cap), names),
                NormalizeOperation(assignment.Value, branchNames));
        }

        private TransactionGasInitializationExpression NormalizeEarlyReturn(IfStatementSyntax statement)
        {
            if (statement.Else is not null || statement.Statement is not BlockSyntax { Statements.Count: 1 } block)
            {
                throw new ExtractionException("Transaction gas failure branch must remain a single early return.");
            }

            return NormalizeReturn(
                block.Statements[0],
                new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default));
        }

        private TransactionGasInitializationExpression NormalizeReturn(
            StatementSyntax statement,
            IReadOnlyDictionary<ISymbol, string> names)
        {
            if (statement is not ReturnStatementSyntax { Expression: ExpressionSyntax expression })
            {
                throw new ExtractionException("Transaction gas initialization requires an explicit result return.");
            }

            return NormalizeExpression(expression, names);
        }

        private TransactionGasInitializationFunction Function(
            IMethodSymbol method,
            TransactionGasInitializationExpression body) => new(
            method.Name,
            StateGasChargeExtractor.Display(method),
            method.ReturnType.ToDisplayString(),
            method.Parameters.Select(parameter => new TransactionGasInitializationParameter(
                parameter.Ordinal,
                Identifier(parameter.Name),
                parameter.Type.ToDisplayString())).ToArray(),
            body);

        private TransactionGasInitializationExpression NormalizeExpression(
            ExpressionSyntax expression,
            IReadOnlyDictionary<ISymbol, string> names)
        {
            while (expression is ParenthesizedExpressionSyntax parenthesized)
            {
                expression = parenthesized.Expression;
            }

            if (expression is CheckedExpressionSyntax checkedExpression)
            {
                expression = checkedExpression.Expression;
            }

            IOperation operation = _semanticModel.GetOperation(expression)
                ?? throw new ExtractionException($"Roslyn did not bind transaction gas expression '{expression}'.");
            return NormalizeOperation(operation, names);
        }

        private TransactionGasInitializationExpression NormalizeOperation(
            IOperation operation,
            IReadOnlyDictionary<ISymbol, string> names) => operation switch
            {
                ILiteralOperation literal => NormalizeLiteral(literal),
                IParameterReferenceOperation parameter when names.TryGetValue(parameter.Parameter, out string? name) =>
                    Leaf("variable", TypeName(parameter.Type), name),
                ILocalReferenceOperation local when names.TryGetValue(local.Local, out string? name) =>
                    Leaf("variable", TypeName(local.Type), name),
                IParenthesizedOperation parenthesized => NormalizeOperation(parenthesized.Operand, names),
                IConversionOperation conversion => NormalizeConversion(conversion, names),
                IBinaryOperation binary => NormalizeBinary(binary, names),
                IInvocationOperation invocation => NormalizeInvocation(invocation, names),
                IConditionalOperation { WhenFalse: not null } conditional => Node(
                    "if",
                    TypeName(conditional.Type),
                    null,
                    null,
                    NormalizeOperation(conditional.Condition, names),
                    NormalizeOperation(conditional.WhenTrue, names),
                    NormalizeOperation(conditional.WhenFalse, names)),
                IObjectCreationOperation creation => NormalizeObjectCreation(creation, names),
                IFieldReferenceOperation field => NormalizeOutcome(field),
                _ => throw new ExtractionException(
                    $"Operation '{operation.Kind}' is validated for IR but unsupported by transaction gas Lean emission."),
            };

        private static TransactionGasInitializationExpression NormalizeLiteral(ILiteralOperation literal)
        {
            if (!literal.ConstantValue.HasValue)
            {
                throw new ExtractionException("Transaction gas normalization received a nonconstant literal.");
            }

            string value = literal.ConstantValue.Value switch
            {
                bool boolean => boolean ? "true" : "false",
                byte or sbyte or short or ushort or int or uint or long or ulong =>
                    Convert.ToString(literal.ConstantValue.Value, CultureInfo.InvariantCulture)!,
                _ => throw new ExtractionException($"Literal type '{literal.Type}' is not supported by transaction gas normalization."),
            };
            return Leaf("literal", TypeName(literal.Type), value);
        }

        private TransactionGasInitializationExpression NormalizeConversion(
            IConversionOperation conversion,
            IReadOnlyDictionary<ISymbol, string> names)
        {
            if (conversion.IsChecked || conversion.OperatorMethod is not null || conversion.Type is null || conversion.Operand.Type is null)
            {
                throw new ExtractionException("Checked, user-defined, or untyped conversion is not supported by transaction gas normalization.");
            }

            if (conversion.Conversion.IsIdentity ||
                conversion.Type.SpecialType == conversion.Operand.Type.SpecialType)
            {
                return NormalizeOperation(conversion.Operand, names);
            }

            if (conversion.Operand.Type.SpecialType == SpecialType.System_Int64 &&
                conversion.Type.SpecialType == SpecialType.System_UInt64)
            {
                return Node("int64ToUInt64", "ulong", null, null, NormalizeOperation(conversion.Operand, names));
            }

            if (conversion.Operand.Type.SpecialType == SpecialType.System_UInt64 &&
                conversion.Type.SpecialType == SpecialType.System_Int64)
            {
                return Node("uint64ToInt64", "long", null, null, NormalizeOperation(conversion.Operand, names));
            }

            if (conversion.Operand.ConstantValue.HasValue &&
                conversion.Type.SpecialType is SpecialType.System_Int64 or SpecialType.System_UInt64)
            {
                return new TransactionGasInitializationExpression(
                    "literal",
                    conversion.Type.ToDisplayString(),
                    Convert.ToString(conversion.Operand.ConstantValue.Value, CultureInfo.InvariantCulture),
                    null,
                    []);
            }

            throw new ExtractionException(
                $"Conversion from '{conversion.Operand.Type}' to '{conversion.Type}' is not supported by transaction gas normalization.");
        }

        private TransactionGasInitializationExpression NormalizeBinary(
            IBinaryOperation binary,
            IReadOnlyDictionary<ISymbol, string> names)
        {
            if (binary.IsChecked || binary.IsLifted || binary.OperatorMethod is not null)
            {
                throw new ExtractionException(
                    $"Checked, lifted, or user-defined operator '{binary.OperatorKind}' is not supported by transaction gas normalization.");
            }

            string kind;
            string? value = null;
            switch (binary.OperatorKind)
            {
                case BinaryOperatorKind.Add when binary.Type?.SpecialType == SpecialType.System_UInt64:
                    kind = "uint64Add";
                    break;
                case BinaryOperatorKind.Subtract when binary.Type?.SpecialType == SpecialType.System_UInt64:
                    kind = "uint64Sub";
                    break;
                case BinaryOperatorKind.GreaterThan:
                    kind = "compare";
                    value = ">";
                    break;
                case BinaryOperatorKind.GreaterThanOrEqual:
                    kind = "compare";
                    value = ">=";
                    break;
                case BinaryOperatorKind.LessThan:
                    kind = "compare";
                    value = "<";
                    break;
                case BinaryOperatorKind.LessThanOrEqual:
                    kind = "compare";
                    value = "<=";
                    break;
                default:
                    throw new ExtractionException(
                        $"Binary operator '{binary.OperatorKind}' on '{binary.Type}' is not supported by transaction gas normalization.");
            }

            return Node(
                kind,
                TypeName(binary.Type),
                value,
                null,
                NormalizeOperation(binary.LeftOperand, names),
                NormalizeOperation(binary.RightOperand, names));
        }

        private TransactionGasInitializationExpression NormalizeInvocation(
            IInvocationOperation invocation,
            IReadOnlyDictionary<ISymbol, string> names)
        {
            TransactionGasInitializationExpression[] arguments = NormalizeArguments(invocation.Arguments, names);
            if (invocation.TargetMethod.ContainingType.ToDisplayString() == typeof(Math).FullName &&
                invocation.TargetMethod.Parameters.Length == 2 &&
                invocation.TargetMethod.Parameters.All(static parameter => parameter.Type.SpecialType == SpecialType.System_UInt64) &&
                invocation.TargetMethod.ReturnType.SpecialType == SpecialType.System_UInt64)
            {
                return invocation.TargetMethod.Name switch
                {
                    nameof(Math.Min) => new TransactionGasInitializationExpression("minUInt64", "ulong", null, null, arguments),
                    nameof(Math.Max) => new TransactionGasInitializationExpression("maxUInt64", "ulong", null, null, arguments),
                    _ => throw new ExtractionException($"Transaction gas intrinsic '{invocation.TargetMethod}' is not allowed."),
                };
            }

            throw new ExtractionException($"Transaction gas invocation '{invocation.TargetMethod}' is not supported by Lean emission.");
        }

        private TransactionGasInitializationExpression NormalizeObjectCreation(
            IObjectCreationOperation creation,
            IReadOnlyDictionary<ISymbol, string> names)
        {
            if (!SymbolEqualityComparer.Default.Equals(creation.Constructor?.ContainingType, _resultType))
            {
                throw new ExtractionException($"Construction of '{creation.Type}' is not supported by transaction gas Lean emission.");
            }

            TransactionGasInitializationExpression[] arguments = NormalizeArguments(creation.Arguments, names);
            if (arguments.Length != _resultFields.Count)
            {
                throw new ExtractionException("Transaction gas result construction has the wrong argument count.");
            }

            return new TransactionGasInitializationExpression("construct", ResultTypeName, null, null, arguments);
        }

        private TransactionGasInitializationExpression NormalizeOutcome(IFieldReferenceOperation field)
        {
            if (!SymbolEqualityComparer.Default.Equals(field.Field.ContainingType, _outcomeType) ||
                !field.Field.HasConstantValue)
            {
                throw new ExtractionException($"Field '{field.Field}' is not a pinned transaction gas outcome.");
            }

            return Leaf("outcome", OutcomeTypeName, LowerFirst(field.Field.Name));
        }

        private TransactionGasInitializationExpression[] NormalizeArguments(
            ImmutableArray<IArgumentOperation> arguments,
            IReadOnlyDictionary<ISymbol, string> names)
        {
            TransactionGasInitializationExpression?[] ordered = new TransactionGasInitializationExpression?[arguments.Length];
            foreach (IArgumentOperation argument in arguments)
            {
                if (argument.Parameter is null ||
                    argument.ArgumentKind != ArgumentKind.Explicit ||
                    argument.Parameter.Ordinal < 0 ||
                    argument.Parameter.Ordinal >= ordered.Length ||
                    ordered[argument.Parameter.Ordinal] is not null)
                {
                    throw new ExtractionException("Transaction gas normalization requires explicit positional arguments.");
                }

                ordered[argument.Parameter.Ordinal] = NormalizeOperation(argument.Value, names);
            }

            if (ordered.Any(static argument => argument is null))
            {
                throw new ExtractionException("Transaction gas normalization received an incomplete argument list.");
            }

            return ordered.Select(static argument => argument!).ToArray();
        }

        private static MethodDeclarationSyntax GetDeclaration(IMethodSymbol method) =>
            method.DeclaringSyntaxReferences.Single().GetSyntax() as MethodDeclarationSyntax
            ?? throw new ExtractionException($"Transaction gas root '{method}' is not a method declaration.");

        private static LocalDeclarationStatementSyntax RequireLocal(StatementSyntax statement) => statement as LocalDeclarationStatementSyntax
            ?? throw new ExtractionException("Transaction gas initialization statement is not its pinned local declaration.");

        private static IfStatementSyntax RequireIf(StatementSyntax statement) => statement as IfStatementSyntax
            ?? throw new ExtractionException("Transaction gas initialization statement is not its pinned conditional.");

        private ILocalSymbol GetLocalSymbol(LocalDeclarationStatementSyntax statement)
        {
            if (statement.Declaration.Variables.Count != 1)
            {
                throw new ExtractionException("Transaction gas local declaration must declare exactly one value.");
            }

            VariableDeclaratorSyntax variable = statement.Declaration.Variables[0];
            return _semanticModel.GetDeclaredSymbol(variable) as ILocalSymbol
                ?? throw new ExtractionException($"Roslyn local '{variable.Identifier.ValueText}' was not resolved.");
        }

        private static ExpressionSyntax GetInitializer(LocalDeclarationStatementSyntax statement)
        {
            if (statement.Declaration.Variables is not [{ Initializer.Value: ExpressionSyntax initializer }])
            {
                throw new ExtractionException("Transaction gas local declaration must have one initializer.");
            }

            return initializer;
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

        private static TransactionGasInitializationExpression Let(
            string name,
            string type,
            TransactionGasInitializationExpression value,
            TransactionGasInitializationExpression continuation) =>
            Node("let", continuation.Type, name, type, value, continuation);

        private static TransactionGasInitializationExpression Leaf(string kind, string type, string value) =>
            new(kind, type, value, null, []);

        private static TransactionGasInitializationExpression Node(
            string kind,
            string type,
            string? value,
            string? auxiliary,
            params TransactionGasInitializationExpression[] children) =>
            new(kind, type, value, auxiliary, children);

        private static string TypeName(ITypeSymbol? type) => type?.ToDisplayString()
            ?? throw new ExtractionException("Transaction gas normalization received an untyped operation.");

        private static string Identifier(string value)
        {
            ReadOnlySpan<string> reserved =
            [
                "by", "do", "else", "end", "false", "for", "fun", "if", "in", "let", "match",
                "namespace", "open", "private", "section", "then", "true", "where",
            ];
            if (string.IsNullOrEmpty(value) ||
                !char.IsLetter(value[0]) && value[0] != '_' ||
                value.Skip(1).Any(static character => !char.IsLetterOrDigit(character) && character != '_') ||
                reserved.Contains(value, StringComparer.Ordinal))
            {
                throw new ExtractionException($"Identifier '{value}' cannot be emitted safely to Lean.");
            }

            return value;
        }
    }
}
