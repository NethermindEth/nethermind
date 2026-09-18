// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Evm.Lean.Extractor;

internal sealed record BlockGasInclusionKernelModel(
    string TxGasLimitCap,
    IReadOnlyList<string> OutcomeCases,
    IReadOnlyList<BlockGasInclusionFunction> Functions);

internal sealed record BlockGasInclusionFunction(
    string Name,
    string Signature,
    string ReturnType,
    IReadOnlyList<BlockGasInclusionParameter> Parameters,
    BlockGasInclusionExpression Body);

internal sealed record BlockGasInclusionParameter(int Ordinal, string Name, string Type);

internal sealed record BlockGasInclusionExpression(
    string Kind,
    string Type,
    string? Value,
    IReadOnlyList<BlockGasInclusionExpression> Children);

internal static class BlockGasInclusionLeanEmitter
{
    private const string KernelTypeName = "Nethermind.Evm.GasPolicy.Eip8037BlockGasInclusionCheck";
    private const string OutcomeTypeName = KernelTypeName + ".Outcome";
    private const string Eip7825ConstantsTypeName = "Nethermind.Core.Eip7825Constants";
    private const string UInt64ExtensionsTypeName = "Nethermind.Core.Extensions.UInt64Extensions";
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static void ValidateRoots(IReadOnlyList<IMethodSymbol> roots)
    {
        if (roots.Count != 2)
        {
            throw new ExtractionException("Block gas inclusion Lean emission requires exactly two roots.");
        }

        ValidateRoot(
            roots[0],
            "Validate",
            OutcomeTypeName,
            ["ulong", "ulong", "ulong", "ulong"]);
        ValidateRoot(
            roots[1],
            "CalculateBlockExecutionGas",
            "ulong",
            ["ulong", "ulong", "ulong"]);
    }

    public static BlockGasInclusionKernelModel Normalize(
        SemanticModel semanticModel,
        IReadOnlyList<IMethodSymbol> roots,
        IReadOnlyList<IMethodSymbol> extractedMethods,
        string eip7825ConstantsSourcePath,
        string uint64ExtensionsSourcePath)
    {
        ValidateRoots(roots);
        if (extractedMethods.Count != 2 ||
            !extractedMethods.Select(StateGasChargeExtractor.Display).Order(StringComparer.Ordinal).SequenceEqual(
                roots.Select(StateGasChargeExtractor.Display).Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new ExtractionException("Block gas inclusion extraction did not retain exactly the pinned root graph.");
        }

        INamedTypeSymbol outcomeType = roots[0].ContainingType.GetTypeMembers("Outcome").SingleOrDefault()
            ?? throw new ExtractionException($"Required block gas inclusion outcome '{OutcomeTypeName}' was not found.");
        IReadOnlyList<string> outcomes = NormalizeOutcomeCases(outcomeType);
        string cap = ReadEip7825Cap(eip7825ConstantsSourcePath);
        ValidateSaturatingSubSource(uint64ExtensionsSourcePath);
        ModelBuilder builder = new(semanticModel, outcomeType);
        return new BlockGasInclusionKernelModel(
            cap,
            outcomes,
            [builder.NormalizeValidate(roots[0]), builder.NormalizeCalculateBlockExecutionGas(roots[1])]);
    }

    public static byte[] Emit(
        BlockGasInclusionKernelModel model,
        string extractorVersion,
        string compilerVersion,
        string sourcePath,
        string sourceHash,
        string irHash)
    {
        ValidateModel(model);
        BlockGasInclusionFunction validate = model.Functions.Single(static function => function.Name == "validate");
        BlockGasInclusionFunction calculate = model.Functions.Single(static function => function.Name == "calculateBlockExecutionGas");
        string source = $$"""
            -- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
            -- SPDX-License-Identifier: LGPL-3.0-only

            -- This file is generated. Do not edit.
            -- Extractor version: {{extractorVersion}}
            -- Roslyn compiler version: {{compilerVersion}}
            -- Production source: {{sourcePath}}
            -- Production source SHA-256: {{sourceHash}}
            -- Canonical block-gas-inclusion IR SHA-256: {{irHash}}

            namespace Eip803x.Generated.Eip8037BlockGasInclusionCheck

            def uint64Modulus : Nat := 2 ^ 64
            def uint64Max : Nat := uint64Modulus - 1

            def normalizeUInt64 (value : Nat) : Nat :=
              if value <= uint64Max then value else value % uint64Modulus

            def subUInt64 (left right : Nat) : Nat :=
              if left >= right then left - right else uint64Modulus - (right - left)

            def saturatingSubUInt64 (left right : Nat) : Nat :=
              if left > right then subUInt64 left right else 0

            inductive Outcome where
            {{RenderOutcomeCases(model.OutcomeCases)}}
              deriving DecidableEq, Repr

            def txMaxGasLimit : Nat := {{model.TxGasLimitCap}}

            {{RenderFunction(validate)}}

            {{RenderWrapper(validate)}}

            {{RenderFunction(calculate)}}

            {{RenderWrapper(calculate)}}

            end Eip803x.Generated.Eip8037BlockGasInclusionCheck
            """;
        return Utf8WithoutBom.GetBytes(source.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    }

    private static void ValidateRoot(
        IMethodSymbol method,
        string name,
        string returnType,
        IReadOnlyList<string> parameterTypes)
    {
        if (method.ContainingType.ToDisplayString() != KernelTypeName ||
            method.Name != name ||
            method.ReturnType.ToDisplayString() != returnType ||
            !method.IsStatic ||
            method.Parameters.Length != parameterTypes.Count ||
            method.Parameters.Where((parameter, index) => parameter.Type.ToDisplayString() != parameterTypes[index]).Any())
        {
            throw new ExtractionException($"Block gas inclusion root '{method}' does not match its pinned signature.");
        }
    }

    private static IReadOnlyList<string> NormalizeOutcomeCases(INamedTypeSymbol outcomeType)
    {
        if (outcomeType.TypeKind != TypeKind.Enum ||
            outcomeType.EnumUnderlyingType?.SpecialType != SpecialType.System_Int32)
        {
            throw new ExtractionException("Block gas inclusion Outcome must be the pinned int enum.");
        }

        IFieldSymbol[] cases = outcomeType.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(static field => field.HasConstantValue)
            .OrderBy(static field => Convert.ToInt32(field.ConstantValue, CultureInfo.InvariantCulture))
            .ToArray();
        string[] expected = ["Ok", "ExecutionDimensionExceeded", "StateDimensionExceeded"];
        if (!cases.Select(static field => field.Name).SequenceEqual(expected, StringComparer.Ordinal) ||
            !cases.Select(static field => Convert.ToInt32(field.ConstantValue, CultureInfo.InvariantCulture))
                .SequenceEqual([0, 1, 2]))
        {
            throw new ExtractionException("Block gas inclusion Outcome must contain exactly the pinned cases.");
        }

        return cases.Select(static field => LowerFirst(field.Name)).ToArray();
    }

    private static string ReadEip7825Cap(string sourcePath)
    {
        CompilationUnitSyntax unit = ParseSource(sourcePath);
        ClassDeclarationSyntax type = unit.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .SingleOrDefault(static declaration => declaration.Identifier.ValueText == "Eip7825Constants")
            ?? throw new ExtractionException("EIP-7825 constants source does not declare Eip7825Constants exactly once.");
        FieldDeclarationSyntax field = type.Members.OfType<FieldDeclarationSyntax>()
            .SingleOrDefault(static declaration => declaration.Declaration.Variables is [{ Identifier.ValueText: "DefaultTxGasLimitCap" }])
            ?? throw new ExtractionException("EIP-7825 constants source does not declare DefaultTxGasLimitCap exactly once.");
        if (!field.Modifiers.Any(SyntaxKind.PublicKeyword) ||
            !field.Modifiers.Any(SyntaxKind.StaticKeyword) ||
            !field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword) ||
            field.Declaration.Type is not PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.ULongKeyword } ||
            field.Declaration.Variables is not [{ Initializer.Value: LiteralExpressionSyntax literal }] ||
            !ulong.TryParse(literal.Token.ValueText.Replace("_", string.Empty, StringComparison.Ordinal),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out ulong cap) ||
            cap == 0)
        {
            throw new ExtractionException("DefaultTxGasLimitCap must be a positive pinned public static readonly ulong literal.");
        }

        return cap.ToString(CultureInfo.InvariantCulture);
    }

    private static void ValidateSaturatingSubSource(string sourcePath)
    {
        CompilationUnitSyntax unit = ParseSource(sourcePath);
        ClassDeclarationSyntax type = unit.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .SingleOrDefault(static declaration => declaration.Identifier.ValueText == "UInt64Extensions")
            ?? throw new ExtractionException("UInt64 extensions source does not declare UInt64Extensions exactly once.");
        MethodDeclarationSyntax method = type.Members.OfType<MethodDeclarationSyntax>()
            .SingleOrDefault(static declaration => declaration.Identifier.ValueText == "SaturatingSub")
            ?? throw new ExtractionException("UInt64 extensions source does not declare SaturatingSub exactly once.");
        if (!method.Modifiers.Any(SyntaxKind.PublicKeyword) ||
            !method.Modifiers.Any(SyntaxKind.StaticKeyword) ||
            method.ReturnType is not PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.ULongKeyword } ||
            method.ParameterList.Parameters is not
                [{ Type: PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.ULongKeyword }, Identifier.ValueText: "a" },
                 { Type: PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.ULongKeyword }, Identifier.ValueText: "b" }] ||
            method.ExpressionBody?.Expression is not ConditionalExpressionSyntax
                {
                    Condition: BinaryExpressionSyntax { RawKind: (int)SyntaxKind.GreaterThanExpression, Left: IdentifierNameSyntax { Identifier.ValueText: "a" }, Right: IdentifierNameSyntax { Identifier.ValueText: "b" } },
                    WhenTrue: BinaryExpressionSyntax { RawKind: (int)SyntaxKind.SubtractExpression, Left: IdentifierNameSyntax { Identifier.ValueText: "a" }, Right: IdentifierNameSyntax { Identifier.ValueText: "b" } },
                    WhenFalse: LiteralExpressionSyntax { Token.ValueText: "0" },
                } ||
            method.Body is not null)
        {
            throw new ExtractionException("SaturatingSub must retain the pinned a > b ? a - b : 0UL definition.");
        }
    }

    private static CompilationUnitSyntax ParseSource(string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new ExtractionException($"Pinned supporting source was not found: {sourcePath}");
        }

        return CSharpSyntaxTree.ParseText(
                File.ReadAllText(sourcePath),
                CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp14),
                sourcePath)
            .GetCompilationUnitRoot();
    }

    private static void ValidateModel(BlockGasInclusionKernelModel model)
    {
        if (!ulong.TryParse(model.TxGasLimitCap, NumberStyles.None, CultureInfo.InvariantCulture, out ulong cap) ||
            cap == 0 ||
            !model.OutcomeCases.SequenceEqual(
                ["ok", "executionDimensionExceeded", "stateDimensionExceeded"], StringComparer.Ordinal) ||
            model.Functions.Count != 2 ||
            !model.Functions.Select(static function => function.Name).SequenceEqual(
                ["validate", "calculateBlockExecutionGas"], StringComparer.Ordinal))
        {
            throw new ExtractionException("Block gas inclusion model does not have the pinned canonical shape.");
        }

        foreach (BlockGasInclusionFunction function in model.Functions)
        {
            if (function.Parameters.Any(static parameter => parameter.Type != "Nat") ||
                function.ReturnType is not ("Outcome" or "Nat") ||
                function.Body.Type != function.ReturnType)
            {
                throw new ExtractionException("Block gas inclusion model contains an unsupported function type.");
            }

            ValidateExpression(function.Body);
        }
    }

    private static void ValidateExpression(BlockGasInclusionExpression expression)
    {
        bool valid = expression.Kind switch
        {
            "parameter" or "local" or "literal" or "cap" or "outcome" => expression.Children.Count == 0,
            "subUInt64" or "minUInt64" or "maxUInt64" or "saturatingSubUInt64" or "gt" or "ge" or "lt" or "le" or "eq" or "ne" => expression.Children.Count == 2,
            "if" => expression.Children.Count == 3,
            "let" => expression.Children.Count == 2 && expression.Value is not null,
            _ => false,
        };
        if (!valid)
        {
            throw new ExtractionException($"Block gas inclusion expression '{expression.Kind}' is not canonical.");
        }

        foreach (BlockGasInclusionExpression child in expression.Children)
        {
            ValidateExpression(child);
        }
    }

    private static string RenderOutcomeCases(IReadOnlyList<string> cases) => string.Join(
        Environment.NewLine,
        cases.Select(static outcome => $"  | {outcome}"));

    private static string RenderFunction(BlockGasInclusionFunction function) =>
        $"def {function.Name}Normalized\n" +
        string.Join(Environment.NewLine, function.Parameters.Select(static parameter =>
            $"    ({parameter.Name} : {parameter.Type})")) +
        $"\n    : {function.ReturnType} :=\n" +
        "  " + RenderExpression(function.Body, 2);

    private static string RenderWrapper(BlockGasInclusionFunction function) =>
        $"def {function.Name}\n" +
        string.Join(Environment.NewLine, function.Parameters.Select(static parameter =>
            $"    ({parameter.Name} : {parameter.Type})")) +
        $"\n    : {function.ReturnType} :=\n" +
        $"  {function.Name}Normalized " + string.Join(" ", function.Parameters.Select(static parameter =>
            $"(normalizeUInt64 {parameter.Name})"));

    private static string RenderExpression(BlockGasInclusionExpression expression, int indentation) =>
        expression.Kind switch
        {
            "parameter" or "local" or "literal" => expression.Value!,
            "cap" => "txMaxGasLimit",
            "outcome" => "." + expression.Value,
            "subUInt64" => $"subUInt64 ({RenderExpression(expression.Children[0], indentation)}) ({RenderExpression(expression.Children[1], indentation)})",
            "minUInt64" => $"min ({RenderExpression(expression.Children[0], indentation)}) ({RenderExpression(expression.Children[1], indentation)})",
            "maxUInt64" => $"max ({RenderExpression(expression.Children[0], indentation)}) ({RenderExpression(expression.Children[1], indentation)})",
            "saturatingSubUInt64" => $"saturatingSubUInt64 ({RenderExpression(expression.Children[0], indentation)}) ({RenderExpression(expression.Children[1], indentation)})",
            "gt" => RenderBinary(">", expression, indentation),
            "ge" => RenderBinary(">=", expression, indentation),
            "lt" => RenderBinary("<", expression, indentation),
            "le" => RenderBinary("<=", expression, indentation),
            "eq" => RenderBinary("=", expression, indentation),
            "ne" => RenderBinary("!=", expression, indentation),
            "if" => RenderIf(expression, indentation),
            "let" => RenderLet(expression, indentation),
            _ => throw new ExtractionException($"Block gas inclusion expression '{expression.Kind}' cannot be emitted."),
        };

    private static string RenderBinary(string operation, BlockGasInclusionExpression expression, int indentation) =>
        $"{RenderExpression(expression.Children[0], indentation)} {operation} {RenderExpression(expression.Children[1], indentation)}";

    private static string RenderIf(BlockGasInclusionExpression expression, int indentation)
    {
        string spaces = new(' ', indentation);
        return $"if {RenderExpression(expression.Children[0], indentation)} then\n" +
               $"{new string(' ', indentation + 2)}{RenderExpression(expression.Children[1], indentation + 2)}\n" +
               $"{spaces}else\n" +
               $"{new string(' ', indentation + 2)}{RenderExpression(expression.Children[2], indentation + 2)}";
    }

    private static string RenderLet(BlockGasInclusionExpression expression, int indentation)
    {
        string spaces = new(' ', indentation);
        return $"let {expression.Value} : Nat := {RenderExpression(expression.Children[0], indentation)}\n" +
               $"{spaces}{RenderExpression(expression.Children[1], indentation)}";
    }

    private static string LowerFirst(string value) => char.ToLowerInvariant(value[0]) + value[1..];

    private sealed class ModelBuilder(SemanticModel semanticModel, INamedTypeSymbol outcomeType)
    {
        private readonly SemanticModel _semanticModel = semanticModel;
        private readonly INamedTypeSymbol _outcomeType = outcomeType;

        public BlockGasInclusionFunction NormalizeValidate(IMethodSymbol method)
        {
            MethodDeclarationSyntax declaration = GetDeclaration(method);
            if (declaration.ExpressionBody is not null || declaration.Body?.Statements.Count != 8)
            {
                throw new ExtractionException("Block gas inclusion Validate must retain its pinned statement shape.");
            }

            Dictionary<ISymbol, string> names = ParameterNames(method);
            IfStatementSyntax[] branches =
            [
                RequireIf(declaration.Body.Statements[0]),
                RequireIf(declaration.Body.Statements[1]),
                RequireIf(declaration.Body.Statements[5]),
                RequireIf(declaration.Body.Statements[6]),
            ];
            LocalDeclarationStatementSyntax[] locals =
            [
                RequireLocal(declaration.Body.Statements[2]),
                RequireLocal(declaration.Body.Statements[3]),
                RequireLocal(declaration.Body.Statements[4]),
            ];
            string[] expectedLocals = ["executionAvailable", "stateAvailable", "worstCaseExecution"];
            for (int index = 0; index < locals.Length; index++)
            {
                ILocalSymbol local = GetLocalSymbol(locals[index]);
                if (local.Name != expectedLocals[index] || local.Type.SpecialType != SpecialType.System_UInt64)
                {
                    throw new ExtractionException("Block gas inclusion Validate local declarations do not match the pinned shape.");
                }

                names.Add(local, Identifier(local.Name));
            }

            BlockGasInclusionExpression body = NormalizeReturn(RequireReturn(declaration.Body.Statements[7]), names);
            for (int index = 3; index >= 2; index--)
            {
                body = Conditional(branches[index], names, body);
            }

            for (int index = locals.Length - 1; index >= 0; index--)
            {
                ILocalSymbol local = GetLocalSymbol(locals[index]);
                body = Let(
                    Identifier(local.Name),
                    NormalizeExpression(GetInitializer(locals[index]), names),
                    body);
            }

            for (int index = 1; index >= 0; index--)
            {
                body = Conditional(branches[index], names, body);
            }

            return Function("validate", method, "Outcome", body);
        }

        public BlockGasInclusionFunction NormalizeCalculateBlockExecutionGas(IMethodSymbol method)
        {
            MethodDeclarationSyntax declaration = GetDeclaration(method);
            if (declaration.Body is not null || declaration.ExpressionBody?.Expression is not ExpressionSyntax expression)
            {
                throw new ExtractionException("CalculateBlockExecutionGas must retain its pinned expression body.");
            }

            return Function("calculateBlockExecutionGas", method, "Nat", NormalizeExpression(expression, ParameterNames(method)));
        }

        private BlockGasInclusionFunction Function(
            string name,
            IMethodSymbol method,
            string returnType,
            BlockGasInclusionExpression body) =>
            new(
                name,
                StateGasChargeExtractor.Display(method),
                returnType,
                method.Parameters.Select(static parameter => new BlockGasInclusionParameter(
                    parameter.Ordinal,
                    Identifier(parameter.Name),
                    "Nat")).ToArray(),
                body);

        private BlockGasInclusionExpression Conditional(
            IfStatementSyntax statement,
            IReadOnlyDictionary<ISymbol, string> names,
            BlockGasInclusionExpression whenFalse)
        {
            if (statement.Else is not null)
            {
                throw new ExtractionException("Block gas inclusion Validate branches must not have else clauses.");
            }

            BlockGasInclusionExpression condition = NormalizeExpression(statement.Condition, names);
            if (condition.Type != "Bool")
            {
                throw new ExtractionException("Block gas inclusion Validate branch condition is not Boolean.");
            }

            return Node("if", whenFalse.Type, null, condition, NormalizeReturn(RequireReturn(statement.Statement), names), whenFalse);
        }

        private BlockGasInclusionExpression NormalizeReturn(
            ReturnStatementSyntax statement,
            IReadOnlyDictionary<ISymbol, string> names) =>
            statement.Expression is ExpressionSyntax expression
                ? NormalizeExpression(expression, names)
                : throw new ExtractionException("Block gas inclusion return must have a value.");

        private BlockGasInclusionExpression NormalizeExpression(
            ExpressionSyntax syntax,
            IReadOnlyDictionary<ISymbol, string> names) =>
            NormalizeOperation(
                _semanticModel.GetOperation(syntax)
                    ?? throw new ExtractionException($"Roslyn did not bind block gas inclusion expression '{syntax}'."),
                names);

        private BlockGasInclusionExpression NormalizeOperation(
            IOperation operation,
            IReadOnlyDictionary<ISymbol, string> names)
        {
            if (operation is IConversionOperation conversion)
            {
                if (!conversion.Conversion.IsIdentity || conversion.IsChecked)
                {
                    throw new ExtractionException("Block gas inclusion conversion is not an unchecked identity conversion.");
                }

                return NormalizeOperation(conversion.Operand, names);
            }

            switch (operation)
            {
                case IParameterReferenceOperation parameter when names.TryGetValue(parameter.Parameter, out string? name):
                    return Leaf("parameter", LeanType(parameter.Type), name);
                case ILocalReferenceOperation local when names.TryGetValue(local.Local, out string? name):
                    return Leaf("local", LeanType(local.Type), name);
                case ILiteralOperation literal when literal.ConstantValue.HasValue && literal.Type?.SpecialType == SpecialType.System_UInt64:
                    return Leaf("literal", "Nat", Convert.ToUInt64(literal.ConstantValue.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                case IFieldReferenceOperation field when IsEip7825Cap(field.Field):
                    return Leaf("cap", "Nat", null);
                case IFieldReferenceOperation field when SymbolEqualityComparer.Default.Equals(field.Field.ContainingType, _outcomeType):
                    return Leaf("outcome", "Outcome", LowerFirst(field.Field.Name));
                case IBinaryOperation binary:
                    return NormalizeBinary(binary, names);
                case IInvocationOperation invocation:
                    return NormalizeInvocation(invocation, names);
                default:
                    throw new ExtractionException($"Block gas inclusion operation '{operation.Kind}' is not supported by Lean emission.");
            }
        }

        private BlockGasInclusionExpression NormalizeBinary(
            IBinaryOperation operation,
            IReadOnlyDictionary<ISymbol, string> names)
        {
            string kind = operation.OperatorKind switch
            {
                BinaryOperatorKind.Subtract when operation.Type?.SpecialType == SpecialType.System_UInt64 && !operation.IsChecked => "subUInt64",
                BinaryOperatorKind.GreaterThan => "gt",
                BinaryOperatorKind.GreaterThanOrEqual => "ge",
                BinaryOperatorKind.LessThan => "lt",
                BinaryOperatorKind.LessThanOrEqual => "le",
                BinaryOperatorKind.Equals => "eq",
                BinaryOperatorKind.NotEquals => "ne",
                _ => throw new ExtractionException($"Block gas inclusion binary operator '{operation.OperatorKind}' is not supported."),
            };
            return Node(
                kind,
                kind is "gt" or "ge" or "lt" or "le" or "eq" or "ne" ? "Bool" : "Nat",
                null,
                NormalizeOperation(operation.LeftOperand, names),
                NormalizeOperation(operation.RightOperand, names));
        }

        private BlockGasInclusionExpression NormalizeInvocation(
            IInvocationOperation invocation,
            IReadOnlyDictionary<ISymbol, string> names)
        {
            BlockGasInclusionExpression[] arguments = invocation.Arguments
                .OrderBy(static argument => argument.Parameter?.Ordinal)
                .Select(argument => NormalizeOperation(argument.Value, names))
                .ToArray();
            if (arguments.Length != 2 || arguments.Any(static argument => argument.Type != "Nat"))
            {
                throw new ExtractionException("Block gas inclusion intrinsic has unsupported arguments.");
            }

            string kind = invocation.TargetMethod.ContainingType.ToDisplayString() switch
            {
                "System.Math" when invocation.TargetMethod.Name == nameof(Math.Min) => "minUInt64",
                "System.Math" when invocation.TargetMethod.Name == nameof(Math.Max) => "maxUInt64",
                UInt64ExtensionsTypeName when invocation.TargetMethod.Name == "SaturatingSub" => "saturatingSubUInt64",
                _ => throw new ExtractionException($"Block gas inclusion intrinsic '{invocation.TargetMethod}' is not allowed."),
            };
            return Node(kind, "Nat", null, arguments);
        }

        private static bool IsEip7825Cap(IFieldSymbol field) =>
            field.ContainingType.ToDisplayString() == Eip7825ConstantsTypeName &&
            field.Name == "DefaultTxGasLimitCap" &&
            field.IsStatic &&
            field.Type.SpecialType == SpecialType.System_UInt64;

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
                throw new ExtractionException("Block gas inclusion local declaration must declare exactly one value.");
            }

            VariableDeclaratorSyntax variable = statement.Declaration.Variables[0];
            return _semanticModel.GetDeclaredSymbol(variable) as ILocalSymbol
                ?? throw new ExtractionException($"Roslyn local '{variable.Identifier.ValueText}' was not resolved.");
        }

        private static MethodDeclarationSyntax GetDeclaration(IMethodSymbol method) =>
            method.DeclaringSyntaxReferences.Single().GetSyntax() as MethodDeclarationSyntax
            ?? throw new ExtractionException($"Block gas inclusion root '{method}' is not a method declaration.");

        private static IfStatementSyntax RequireIf(StatementSyntax statement) => statement as IfStatementSyntax
            ?? throw new ExtractionException("Block gas inclusion Validate statement is not its pinned conditional.");

        private static LocalDeclarationStatementSyntax RequireLocal(StatementSyntax statement) => statement as LocalDeclarationStatementSyntax
            ?? throw new ExtractionException("Block gas inclusion Validate statement is not its pinned local declaration.");

        private static ReturnStatementSyntax RequireReturn(StatementSyntax statement) => statement as ReturnStatementSyntax
            ?? throw new ExtractionException("Block gas inclusion branch is not its pinned return.");

        private static ExpressionSyntax GetInitializer(LocalDeclarationStatementSyntax statement) =>
            statement.Declaration.Variables is [{ Initializer.Value: ExpressionSyntax initializer }]
                ? initializer
                : throw new ExtractionException("Block gas inclusion local declaration must have one initializer.");

        private static BlockGasInclusionExpression Let(
            string name,
            BlockGasInclusionExpression value,
            BlockGasInclusionExpression continuation) =>
            Node("let", continuation.Type, name, value, continuation);

        private static BlockGasInclusionExpression Leaf(string kind, string type, string? value) =>
            new(kind, type, value, []);

        private static BlockGasInclusionExpression Node(
            string kind,
            string type,
            string? value,
            params BlockGasInclusionExpression[] children) =>
            new(kind, type, value, children);

        private static string LeanType(ITypeSymbol? type) => type?.SpecialType switch
        {
            SpecialType.System_UInt64 => "Nat",
            SpecialType.System_Boolean => "Bool",
            _ => throw new ExtractionException($"Block gas inclusion type '{type}' cannot be emitted to Lean."),
        };
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
