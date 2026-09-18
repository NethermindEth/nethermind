// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Evm.Lean.Extractor;

internal sealed record TransitionKernelModel(
    string ResultType,
    IReadOnlyList<TransitionField> ResultFields,
    IReadOnlyList<TransitionFunction> Functions);

internal sealed record TransitionField(string Name, string Type, int ConstructorParameterOrdinal);

internal sealed record TransitionFunction(
    string Name,
    string Signature,
    bool IsPublicRoot,
    string ReturnType,
    IReadOnlyList<TransitionParameter> Parameters,
    TransitionExpression Body);

internal sealed record TransitionParameter(int Ordinal, string Name, string Type);

internal sealed record TransitionExpression(
    string Kind,
    string Type,
    string? Value,
    string? Auxiliary,
    IReadOnlyList<TransitionExpression> Children);

internal static class StateGasTransitionLeanEmitter
{
    private const string ResultTypeName = "Nethermind.Evm.GasPolicy.StateGasTransitionResult";
    private const string KernelTypeName = "Nethermind.Evm.GasPolicy.StateGasTransitionKernel";
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    private static readonly MethodShape[] PublicRootShapes =
    [
        new("AddStateGasRefundToReservoir", "ulong", "long", "long", "long", "long", "long", "bool"),
        new("DiscardStateGas", "ulong", "long", "long", "long", "long", "long", "long"),
        new("Refund", "ulong", "long", "long", "long", "long", "ulong", "long", "long", "long", "long"),
        new("RefundStateGas", "ulong", "long", "long", "long", "long", "long", "long", "bool"),
        new("RemoveStateGasRefundFromReservoir", "ulong", "long", "long", "long", "long", "long"),
        new("RepayStateGasSpill", "ulong", "long", "long", "long", "long"),
        new("RestoreChildStateGas", "ulong", "long", "long", "long", "long", "long", "long", "long", "long"),
        new("RestoreChildStateGasOnHalt", "ulong", "long", "long", "long", "long", "long", "long", "long", "long"),
        new("RevertRefundToHalt", "ulong", "long", "long", "long", "long", "long", "long", "long"),
    ];

    private static readonly MethodShape[] HelperShapes =
    [
        new("ClampToRefundAmount", "long", "long"),
        new("GetUnrefundedStateGasSpill", "long", "long"),
        new("PositivePart", "long"),
    ];

    public static void ValidateRoots(IReadOnlyList<IMethodSymbol> roots) =>
        ValidateMethods(roots, PublicRootShapes, Accessibility.Public);

    public static TransitionKernelModel Normalize(
        SemanticModel semanticModel,
        IReadOnlyList<IMethodSymbol> roots,
        IReadOnlyList<IMethodSymbol> extractedMethods)
    {
        ValidateRoots(roots);
        IMethodSymbol[] helpers = extractedMethods
            .Where(static method => method.DeclaredAccessibility == Accessibility.Private)
            .OrderBy(static method => method.Name, StringComparer.Ordinal)
            .ToArray();
        ValidateMethods(helpers, HelperShapes, Accessibility.Private);
        if (extractedMethods.Count != PublicRootShapes.Length + HelperShapes.Length)
        {
            throw new ExtractionException("Transition Lean emission requires exactly the pinned closed method set.");
        }

        INamedTypeSymbol resultType = extractedMethods[0].ContainingAssembly.GetTypeByMetadataName(ResultTypeName)
            ?? throw new ExtractionException($"Required result type '{ResultTypeName}' was not found.");
        IReadOnlyList<TransitionField> fields = NormalizeResultFields(semanticModel, resultType);
        HashSet<IMethodSymbol> publicRoots = new(roots, SymbolEqualityComparer.Default);
        ModelBuilder builder = new(semanticModel, extractedMethods);
        TransitionFunction[] functions = OrderByDependencies(extractedMethods
            .OrderBy(static method => method.Name, StringComparer.Ordinal)
            .Select(method => builder.NormalizeMethod(method, publicRoots.Contains(method)))
            .ToArray());
        return new TransitionKernelModel(ResultTypeName, fields, functions);
    }

    public static byte[] Emit(
        TransitionKernelModel model,
        string extractorVersion,
        string compilerVersion,
        string sourcePath,
        string sourceHash,
        string irHash)
    {
        if (model.ResultType != ResultTypeName)
        {
            throw new ExtractionException($"Transition result model '{model.ResultType}' is not supported.");
        }

        StringBuilder functions = new();
        foreach (TransitionFunction function in model.Functions)
        {
            AppendNormalizedFunction(functions, function, model.ResultFields);
            if (function.IsPublicRoot)
            {
                AppendPublicWrapper(functions, function);
            }
        }

        string source = $$"""
            -- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
            -- SPDX-License-Identifier: LGPL-3.0-only

            -- This file is generated. Do not edit.
            -- Extractor version: {{extractorVersion}}
            -- Roslyn compiler version: {{compilerVersion}}
            -- Production source: {{sourcePath}}
            -- Production source SHA-256: {{sourceHash}}
            -- Canonical transition IR SHA-256: {{irHash}}

            namespace Eip803x.Generated.StateGasTransitionKernel

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

            def addInt64 (left right : Int) : Int :=
              wrapInt64 (left + right)

            def subInt64 (left right : Int) : Int :=
              wrapInt64 (left - right)

            def addUInt64 (left right : Nat) : Nat :=
              wrapUInt64 ((left : Int) + (right : Int))

            def int64ToUInt64 (value : Int) : Nat :=
              wrapUInt64 value

            structure StateGasTransitionResult where
            {{RenderResultFields(model.ResultFields)}}
              deriving DecidableEq, Repr

            {{functions.ToString().TrimEnd()}}

            end Eip803x.Generated.StateGasTransitionKernel
            """;
        return Utf8WithoutBom.GetBytes(source.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    }

    private static IReadOnlyList<TransitionField> NormalizeResultFields(
        SemanticModel semanticModel,
        INamedTypeSymbol resultType)
    {
        (string Name, string Type)[] expected =
        [
            ("Value", "ulong"),
            ("StateReservoir", "long"),
            ("StateGasUsed", "long"),
            ("StateGasSpill", "long"),
            ("StateGasSpillRefunded", "long"),
            ("UnappliedAmount", "long"),
        ];
        IFieldSymbol[] fields = resultType.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(static field => !field.IsStatic && !field.IsImplicitlyDeclared)
            .OrderBy(static field => field.Locations[0].SourceSpan.Start)
            .ToArray();
        IMethodSymbol constructor = resultType.InstanceConstructors.Single(
            static constructor => !constructor.IsImplicitlyDeclared);
        if (!resultType.IsReadOnly || fields.Length != expected.Length || constructor.Parameters.Length != expected.Length)
        {
            throw new ExtractionException("Transition Lean emission requires the pinned transition result shape.");
        }

        TransitionField[] normalized = new TransitionField[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            VariableDeclaratorSyntax declaration = fields[i].DeclaringSyntaxReferences.Single().GetSyntax()
                as VariableDeclaratorSyntax
                ?? throw new ExtractionException("Transition result field is not a variable declaration.");
            if (declaration.Initializer?.Value is not IdentifierNameSyntax initializer ||
                semanticModel.GetSymbolInfo(initializer).Symbol is not IParameterSymbol parameter ||
                !SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, constructor) ||
                parameter.Ordinal != i)
            {
                throw new ExtractionException(
                    "Transition result fields must project their corresponding primary-constructor parameters.");
            }

            if (fields[i].Name != expected[i].Name ||
                fields[i].Type.ToDisplayString() != expected[i].Type ||
                !fields[i].IsReadOnly ||
                fields[i].DeclaredAccessibility != Accessibility.Public ||
                !SymbolEqualityComparer.Default.Equals(fields[i].Type, constructor.Parameters[i].Type))
            {
                throw new ExtractionException("Transition Lean emission requires the pinned transition result shape.");
            }

            normalized[i] = new TransitionField(LowerFirst(fields[i].Name), LeanType(fields[i].Type), i);
        }

        return normalized;
    }

    private static TransitionFunction[] OrderByDependencies(IReadOnlyList<TransitionFunction> functions)
    {
        Dictionary<string, TransitionFunction> byName = functions.ToDictionary(
            static function => function.Name,
            StringComparer.Ordinal);
        Dictionary<string, bool> visiting = new(StringComparer.Ordinal);
        List<TransitionFunction> ordered = [];
        foreach (TransitionFunction function in functions.OrderBy(static function => function.Name, StringComparer.Ordinal))
        {
            Visit(function);
        }

        return ordered.ToArray();

        void Visit(TransitionFunction function)
        {
            if (visiting.TryGetValue(function.Name, out bool isVisiting))
            {
                if (isVisiting)
                {
                    throw new ExtractionException($"Transition call graph contains a cycle at '{function.Name}'.");
                }

                return;
            }

            visiting.Add(function.Name, true);
            foreach (string dependency in EnumerateCalls(function.Body).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                if (!byName.TryGetValue(dependency, out TransitionFunction? called))
                {
                    throw new ExtractionException(
                        $"Transition function '{function.Name}' calls unnormalized method '{dependency}'.");
                }

                Visit(called);
            }

            visiting[function.Name] = false;
            ordered.Add(function);
        }
    }

    private static IEnumerable<string> EnumerateCalls(TransitionExpression expression)
    {
        if (expression.Kind == "call")
        {
            yield return expression.Value
                ?? throw new ExtractionException("Normalized transition call has no target.");
        }

        foreach (TransitionExpression child in expression.Children)
        {
            foreach (string dependency in EnumerateCalls(child))
            {
                yield return dependency;
            }
        }
    }

    private static void ValidateMethods(
        IReadOnlyList<IMethodSymbol> methods,
        IReadOnlyList<MethodShape> expected,
        Accessibility accessibility)
    {
        if (methods.Count != expected.Count)
        {
            throw new ExtractionException("Transition Lean emission received an unexpected method count.");
        }

        for (int i = 0; i < expected.Count; i++)
        {
            IMethodSymbol method = methods[i];
            MethodShape shape = expected[i];
            if (method.Name != shape.Name ||
                method.ContainingType.ToDisplayString() != KernelTypeName ||
                method.ReturnType.ToDisplayString() != (accessibility == Accessibility.Public ? ResultTypeName : "long") ||
                method.DeclaredAccessibility != accessibility ||
                !method.IsStatic ||
                method.Parameters.Length != shape.ParameterTypes.Length)
            {
                throw new ExtractionException($"Transition method '{method}' does not match its pinned signature.");
            }

            for (int parameterIndex = 0; parameterIndex < method.Parameters.Length; parameterIndex++)
            {
                if (method.Parameters[parameterIndex].Type.ToDisplayString() != shape.ParameterTypes[parameterIndex])
                {
                    throw new ExtractionException($"Transition method '{method}' does not match its pinned signature.");
                }
            }
        }
    }

    private static void AppendNormalizedFunction(
        StringBuilder builder,
        TransitionFunction function,
        IReadOnlyList<TransitionField> resultFields)
    {
        builder.Append("private def ");
        builder.Append(LowerFirst(function.Name));
        builder.AppendLine("Normalized");
        AppendParameters(builder, function.Parameters, indent: 4);
        builder.Append("    : ");
        builder.Append(LeanType(function.ReturnType));
        builder.AppendLine(" :=");
        builder.AppendLine(Indent(Render(function.Body, resultFields), 2));
        builder.AppendLine();
    }

    private static void AppendPublicWrapper(StringBuilder builder, TransitionFunction function)
    {
        builder.Append("def ");
        builder.AppendLine(LowerFirst(function.Name));
        AppendParameters(builder, function.Parameters, indent: 4);
        builder.Append("    : ");
        builder.Append(LeanType(function.ReturnType));
        builder.AppendLine(" :=");
        builder.Append("  ");
        builder.Append(LowerFirst(function.Name));
        builder.AppendLine("Normalized");
        foreach (TransitionParameter parameter in function.Parameters)
        {
            builder.Append("    ");
            builder.AppendLine(parameter.Type switch
            {
                "ulong" => $"(normalizeUInt64 {parameter.Name})",
                "long" => $"(wrapInt64 {parameter.Name})",
                "bool" => parameter.Name,
                _ => throw new ExtractionException($"Wrapper parameter type '{parameter.Type}' is not supported."),
            });
        }
        builder.AppendLine();
    }

    private static void AppendParameters(
        StringBuilder builder,
        IReadOnlyList<TransitionParameter> parameters,
        int indent)
    {
        string prefix = new(' ', indent);
        foreach (TransitionParameter parameter in parameters)
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
        TransitionExpression expression,
        IReadOnlyList<TransitionField> resultFields) =>
        expression.Kind switch
        {
            "literal" or "variable" => expression.Value!,
            "compare" =>
                $"({Render(expression.Children[0], resultFields)} {expression.Value} " +
                $"{Render(expression.Children[1], resultFields)})",
            "int64Add" => RenderCall("addInt64", expression.Children, resultFields),
            "int64Sub" => RenderCall("subInt64", expression.Children, resultFields),
            "uint64Add" => RenderCall("addUInt64", expression.Children, resultFields),
            "int64ToUInt64" => RenderCall("int64ToUInt64", expression.Children, resultFields),
            "minInt64" => RenderCall("min", expression.Children, resultFields),
            "call" => RenderCall(LowerFirst(expression.Value!) + "Normalized", expression.Children, resultFields),
            "if" =>
                $"if {Render(expression.Children[0], resultFields)} then\n" +
                $"{Indent(Render(expression.Children[1], resultFields), 2)}\nelse\n" +
                Indent(Render(expression.Children[2], resultFields), 2),
            "let" =>
                $"let {expression.Value} : {LeanType(expression.Auxiliary!)} := " +
                $"{Render(expression.Children[0], resultFields)}\n" +
                Render(expression.Children[1], resultFields),
            "construct" => RenderResult(expression, resultFields),
            _ => throw new ExtractionException($"Normalized expression '{expression.Kind}' is not renderable as Lean."),
        };

    private static string RenderResult(
        TransitionExpression expression,
        IReadOnlyList<TransitionField> resultFields)
    {
        if (expression.Children.Count != resultFields.Count)
        {
            throw new ExtractionException("Normalized transition result does not match its result field count.");
        }

        StringBuilder result = new("{ ");
        for (int i = 0; i < resultFields.Count; i++)
        {
            TransitionField field = resultFields[i];
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
        IReadOnlyList<TransitionExpression> arguments,
        IReadOnlyList<TransitionField> resultFields) =>
        name + " " + string.Join(" ", arguments.Select(argument => Parenthesize(Render(argument, resultFields))));

    private static string RenderResultFields(IReadOnlyList<TransitionField> fields)
    {
        (string Name, string Type)[] expected =
        [
            ("value", "Nat"),
            ("stateReservoir", "Int"),
            ("stateGasUsed", "Int"),
            ("stateGasSpill", "Int"),
            ("stateGasSpillRefunded", "Int"),
            ("unappliedAmount", "Int"),
        ];
        if (fields.Count != expected.Length)
        {
            throw new ExtractionException("Transition result model does not contain the pinned fields.");
        }

        string[] lines = new string[fields.Count];
        for (int i = 0; i < fields.Count; i++)
        {
            if (fields[i].Name != expected[i].Name ||
                fields[i].Type != expected[i].Type ||
                fields[i].ConstructorParameterOrdinal != i)
            {
                throw new ExtractionException("Transition result model does not match the pinned field order.");
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

    private static string LeanType(ITypeSymbol type) => LeanType(type.ToDisplayString());

    private static string LeanType(string type) => type switch
    {
        "long" => "Int",
        "ulong" => "Nat",
        "bool" => "Bool",
        ResultTypeName => "StateGasTransitionResult",
        _ => throw new ExtractionException($"C# type '{type}' is not supported by transition Lean emission."),
    };

    private static string LowerFirst(string value) => char.ToLowerInvariant(value[0]) + value[1..];

    private sealed record MethodShape(string Name, params string[] ParameterTypes);

    private sealed class ModelBuilder(
        SemanticModel semanticModel,
        IReadOnlyList<IMethodSymbol> methods)
    {
        private readonly SemanticModel _semanticModel = semanticModel;
        private readonly Dictionary<IMethodSymbol, string> _localMethods = CreateMethodMap(methods);

        public TransitionFunction NormalizeMethod(IMethodSymbol method, bool isPublicRoot)
        {
            MethodDeclarationSyntax declaration = method.DeclaringSyntaxReferences.Single().GetSyntax()
                as MethodDeclarationSyntax
                ?? throw new ExtractionException($"Transition method '{method}' is not a method declaration.");
            TransitionExpression body = declaration switch
            {
                { Body: not null } => NormalizeStatements(declaration.Body.Statements, 0),
                { ExpressionBody.Expression: ExpressionSyntax expression } => NormalizeExpression(expression),
                _ => throw new ExtractionException($"Transition method '{method}' does not have a supported body."),
            };
            TransitionParameter[] parameters = method.Parameters
                .Select(parameter => new TransitionParameter(
                    parameter.Ordinal,
                    Identifier(parameter.Name),
                    parameter.Type.ToDisplayString()))
                .ToArray();
            return new TransitionFunction(
                method.Name,
                StateGasChargeExtractor.Display(method),
                isPublicRoot,
                method.ReturnType.ToDisplayString(),
                parameters,
                body);
        }

        private TransitionExpression NormalizeStatements(SyntaxList<StatementSyntax> statements, int index)
        {
            if (index >= statements.Count)
            {
                throw new ExtractionException("Transition normalization reached a path without a return value.");
            }

            StatementSyntax statement = statements[index];
            switch (statement)
            {
                case ReturnStatementSyntax { Expression: not null } returnStatement:
                    if (index != statements.Count - 1)
                    {
                        throw new ExtractionException("Transition normalization does not accept statements after return.");
                    }
                    return NormalizeExpression(returnStatement.Expression);

                case LocalDeclarationStatementSyntax localDeclaration:
                    if (localDeclaration.Declaration.Variables.Count != 1 ||
                        localDeclaration.Declaration.Variables[0] is not { Initializer.Value: ExpressionSyntax initializer } variable)
                    {
                        throw new ExtractionException("Transition normalization requires initialized single-local declarations.");
                    }

                    ILocalSymbol local = _semanticModel.GetDeclaredSymbol(variable) as ILocalSymbol
                        ?? throw new ExtractionException($"Roslyn did not bind local '{variable.Identifier.ValueText}'.");
                    TransitionExpression localContinuation = NormalizeStatements(statements, index + 1);
                    return Node(
                        "let",
                        localContinuation.Type,
                        Identifier(local.Name),
                        local.Type.ToDisplayString(),
                        NormalizeExpression(initializer),
                        localContinuation);

                case IfStatementSyntax { Else: null } ifStatement:
                    if (ifStatement.Statement is not BlockSyntax { Statements.Count: 1 } block ||
                        block.Statements[0] is not ReturnStatementSyntax { Expression: not null } branchReturn)
                    {
                        throw new ExtractionException(
                            "Transition normalization accepts only an early-return block as an if body.");
                    }

                    TransitionExpression continuation = NormalizeStatements(statements, index + 1);
                    return Node(
                        "if",
                        continuation.Type,
                        null,
                        null,
                        NormalizeExpression(ifStatement.Condition),
                        NormalizeExpression(branchReturn.Expression),
                        continuation);

                default:
                    throw new ExtractionException(
                        $"Statement '{statement.Kind()}' is validated for IR but not supported by transition normalization.");
            }
        }

        private TransitionExpression NormalizeExpression(ExpressionSyntax expression)
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
                ?? throw new ExtractionException($"Roslyn did not bind transition expression '{expression}'.");
            return NormalizeOperation(operation);
        }

        private TransitionExpression NormalizeOperation(IOperation operation) => operation switch
        {
            ILiteralOperation literal => NormalizeLiteral(literal),
            IParameterReferenceOperation parameter =>
                Leaf("variable", parameter.Type, Identifier(parameter.Parameter.Name)),
            ILocalReferenceOperation local => Leaf("variable", local.Type, Identifier(local.Local.Name)),
            IParenthesizedOperation parenthesized => NormalizeOperation(parenthesized.Operand),
            IBinaryOperation binary => NormalizeBinary(binary),
            IConversionOperation conversion => NormalizeConversion(conversion),
            IInvocationOperation invocation => NormalizeInvocation(invocation),
            IObjectCreationOperation creation => NormalizeObjectCreation(creation),
            IConditionalOperation { WhenFalse: not null } conditional => Node(
                "if",
                TypeName(conditional.Type),
                null,
                null,
                NormalizeOperation(conditional.Condition),
                NormalizeOperation(conditional.WhenTrue),
                NormalizeOperation(conditional.WhenFalse)),
            _ => throw new ExtractionException(
                $"Operation '{operation.Kind}' is validated for IR but not normalized for transition Lean emission."),
        };

        private static TransitionExpression NormalizeLiteral(ILiteralOperation literal)
        {
            if (!literal.ConstantValue.HasValue)
            {
                throw new ExtractionException("Transition normalization received a nonconstant literal.");
            }

            string value = literal.ConstantValue.Value switch
            {
                bool boolean => boolean ? "true" : "false",
                byte or sbyte or short or ushort or int or uint or long or ulong =>
                    Convert.ToString(literal.ConstantValue.Value, CultureInfo.InvariantCulture)!,
                _ => throw new ExtractionException($"Literal type '{literal.Type}' is not supported."),
            };
            return Leaf("literal", literal.Type, value);
        }

        private TransitionExpression NormalizeBinary(IBinaryOperation binary)
        {
            if (binary.IsChecked || binary.IsLifted || binary.OperatorMethod is not null)
            {
                throw new ExtractionException(
                    $"Checked, lifted, or user-defined operator '{binary.OperatorKind}' is not supported by transition normalization.");
            }

            string kind;
            string? value = null;
            switch (binary.OperatorKind)
            {
                case BinaryOperatorKind.Add when binary.Type?.SpecialType == SpecialType.System_Int64:
                    kind = "int64Add";
                    break;
                case BinaryOperatorKind.Subtract when binary.Type?.SpecialType == SpecialType.System_Int64:
                    kind = "int64Sub";
                    break;
                case BinaryOperatorKind.Add when binary.Type?.SpecialType == SpecialType.System_UInt64:
                    kind = "uint64Add";
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
                        $"Binary operator '{binary.OperatorKind}' on '{binary.Type}' is not supported by transition normalization.");
            }

            return Node(
                kind,
                TypeName(binary.Type),
                value,
                null,
                NormalizeOperation(binary.LeftOperand),
                NormalizeOperation(binary.RightOperand));
        }

        private TransitionExpression NormalizeConversion(IConversionOperation conversion)
        {
            if (conversion.IsChecked || conversion.OperatorMethod is not null || conversion.Type is null || conversion.Operand.Type is null)
            {
                throw new ExtractionException("Checked, user-defined, or untyped conversion is not supported.");
            }

            if (conversion.Conversion.IsIdentity ||
                conversion.Type.SpecialType == conversion.Operand.Type.SpecialType)
            {
                return NormalizeOperation(conversion.Operand);
            }

            if (conversion.Operand.Type.SpecialType == SpecialType.System_Int64 &&
                conversion.Type.SpecialType == SpecialType.System_UInt64)
            {
                return Node(
                    "int64ToUInt64",
                    "ulong",
                    null,
                    null,
                    NormalizeOperation(conversion.Operand));
            }

            if (conversion.Operand.ConstantValue.HasValue &&
                conversion.Type.SpecialType is SpecialType.System_Int64 or SpecialType.System_UInt64)
            {
                return new TransitionExpression(
                    "literal",
                    conversion.Type.ToDisplayString(),
                    Convert.ToString(conversion.Operand.ConstantValue.Value, CultureInfo.InvariantCulture),
                    null,
                    []);
            }

            throw new ExtractionException(
                $"Conversion from '{conversion.Operand.Type}' to '{conversion.Type}' is not supported.");
        }

        private TransitionExpression NormalizeInvocation(IInvocationOperation invocation)
        {
            TransitionExpression[] arguments = NormalizeArguments(invocation.Arguments);
            if (_localMethods.TryGetValue(invocation.TargetMethod, out string? methodName))
            {
                return new TransitionExpression(
                    "call",
                    TypeName(invocation.Type),
                    methodName,
                    null,
                    arguments);
            }

            if (invocation.TargetMethod.Name == nameof(Math.Min) &&
                invocation.TargetMethod.ContainingType.ToDisplayString() == typeof(Math).FullName &&
                invocation.TargetMethod.ReturnType.SpecialType == SpecialType.System_Int64 &&
                arguments.Length == 2)
            {
                return new TransitionExpression("minInt64", "long", null, null, arguments);
            }

            throw new ExtractionException(
                $"Call '{invocation.TargetMethod}' is validated for IR but unsupported by transition normalization.");
        }

        private TransitionExpression NormalizeObjectCreation(IObjectCreationOperation creation)
        {
            if (creation.Constructor?.ContainingType.ToDisplayString() != ResultTypeName)
            {
                throw new ExtractionException($"Construction of '{creation.Type}' is not supported.");
            }

            TransitionExpression[] arguments = NormalizeArguments(creation.Arguments);
            if (arguments.Length != 6)
            {
                throw new ExtractionException("Transition result construction requires six arguments.");
            }

            return new TransitionExpression("construct", ResultTypeName, null, null, arguments);
        }

        private TransitionExpression[] NormalizeArguments(ImmutableArray<IArgumentOperation> arguments)
        {
            TransitionExpression?[] ordered = new TransitionExpression?[arguments.Length];
            foreach (IArgumentOperation argument in arguments)
            {
                if (argument.Parameter is null ||
                    argument.ArgumentKind != ArgumentKind.Explicit ||
                    argument.Parameter.Ordinal < 0 ||
                    argument.Parameter.Ordinal >= ordered.Length ||
                    ordered[argument.Parameter.Ordinal] is not null)
                {
                    throw new ExtractionException("Transition normalization requires explicit positional arguments.");
                }

                ordered[argument.Parameter.Ordinal] = NormalizeOperation(argument.Value);
            }

            if (ordered.Any(static argument => argument is null))
            {
                throw new ExtractionException("Transition normalization received an incomplete argument list.");
            }

            return ordered.Select(static argument => argument!).ToArray();
        }

        private static TransitionExpression Leaf(string kind, ITypeSymbol? type, string value) =>
            new(kind, TypeName(type), value, null, []);

        private static TransitionExpression Node(
            string kind,
            string type,
            string? value,
            string? auxiliary,
            params TransitionExpression[] children) =>
            new(kind, type, value, auxiliary, children);

        private static string TypeName(ITypeSymbol? type) => type?.ToDisplayString()
            ?? throw new ExtractionException("Transition normalization received an untyped operation.");

        private static Dictionary<IMethodSymbol, string> CreateMethodMap(IReadOnlyList<IMethodSymbol> methods)
        {
            Dictionary<IMethodSymbol, string> result = new(SymbolEqualityComparer.Default);
            foreach (IMethodSymbol method in methods)
            {
                result.Add(method, method.Name);
            }

            return result;
        }

        private static string Identifier(string value)
        {
            ReadOnlySpan<string> reserved =
            [
                "by", "do", "else", "end", "false", "for", "fun", "if", "in", "let", "match",
                "namespace", "open", "private", "section", "structure", "then", "true", "where",
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
