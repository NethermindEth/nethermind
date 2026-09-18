// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Evm.Lean.Extractor;

internal static class StateGasChargeLeanEmitter
{
    private const string RootMethodName = "TryCharge";
    private const string SpillMethodName = "CalculateSpill";
    private const string ResultTypeName = "Nethermind.Evm.GasPolicy.StateGasChargeResult";
    private const string OutcomeTypeName = "Nethermind.Evm.GasPolicy.StateGasChargeOutcome";

    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static byte[] Emit(
        SemanticModel semanticModel,
        IReadOnlyList<IMethodSymbol> methods,
        string extractorVersion,
        string compilerVersion,
        string sourcePath,
        string sourceHash,
        string irHash)
    {
        IMethodSymbol root = FindMethod(methods, RootMethodName);
        IMethodSymbol calculateSpill = FindMethod(methods, SpillMethodName);
        ValidateMethodSet(methods, root, calculateSpill);
        ValidateResultShape(root.ReturnType);

        MethodEmitter emitter = new(semanticModel, calculateSpill);
        string spillBody = emitter.EmitMethodBody(calculateSpill);
        string rootBody = emitter.EmitMethodBody(root);
        string source = $$"""
            -- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
            -- SPDX-License-Identifier: LGPL-3.0-only

            -- This file is generated. Do not edit.
            -- Extractor version: {{extractorVersion}}
            -- Roslyn compiler version: {{compilerVersion}}
            -- Production source: {{sourcePath}}
            -- Production source SHA-256: {{sourceHash}}
            -- Normalized IR SHA-256: {{irHash}}

            namespace Eip803x.Generated.StateGasChargeKernel

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

            def subUInt64 (left right : Nat) : Nat :=
              wrapUInt64 ((left : Int) - (right : Int))

            def int64ToUInt64 (value : Int) : Nat :=
              wrapUInt64 value

            def uint64ToInt64 (value : Nat) : Int :=
              wrapInt64 (value : Int)

            inductive StateGasChargeOutcome where
              | success
              | outOfGas
              deriving DecidableEq, Repr

            structure StateGasChargeResult where
              outcome : StateGasChargeOutcome
              value : Nat
              stateReservoir : Int
              stateGasUsed : Int
              stateGasSpill : Int
              stateGasSpillRefunded : Int
              deriving DecidableEq, Repr

            private def calculateSpillNormalized
                (stateReservoir : Int)
                (stateGasCost : Int) : Nat :=
            {{Indent(spillBody, 2)}}

            def calculateSpill (stateReservoir stateGasCost : Int) : Nat :=
              calculateSpillNormalized (wrapInt64 stateReservoir) (wrapInt64 stateGasCost)

            private def tryChargeNormalized
                (value : Nat)
                (stateReservoir : Int)
                (stateGasUsed : Int)
                (stateGasSpill : Int)
                (stateGasSpillRefunded : Int)
                (stateGasCost : Int) : StateGasChargeResult :=
            {{Indent(rootBody, 2)}}

            def tryCharge
                (value : Nat)
                (stateReservoir : Int)
                (stateGasUsed : Int)
                (stateGasSpill : Int)
                (stateGasSpillRefunded : Int)
                (stateGasCost : Int) : StateGasChargeResult :=
              tryChargeNormalized
                (normalizeUInt64 value)
                (wrapInt64 stateReservoir)
                (wrapInt64 stateGasUsed)
                (wrapInt64 stateGasSpill)
                (wrapInt64 stateGasSpillRefunded)
                (wrapInt64 stateGasCost)

            end Eip803x.Generated.StateGasChargeKernel
            """;

        return Utf8WithoutBom.GetBytes(source.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    }

    private static IMethodSymbol FindMethod(IReadOnlyList<IMethodSymbol> methods, string name)
    {
        IMethodSymbol? match = null;
        foreach (IMethodSymbol method in methods)
        {
            if (method.Name != name)
            {
                continue;
            }

            if (match is not null)
            {
                throw new ExtractionException($"Lean emission found multiple '{name}' methods.");
            }

            match = method;
        }

        return match ?? throw new ExtractionException($"Lean emission did not find '{name}'.");
    }

    private static void ValidateMethodSet(
        IReadOnlyList<IMethodSymbol> methods,
        IMethodSymbol root,
        IMethodSymbol calculateSpill)
    {
        if (methods.Count != 2 ||
            root.Parameters.Length != 6 ||
            root.Parameters[0].Type.SpecialType != SpecialType.System_UInt64 ||
            root.Parameters.Skip(1).Any(static parameter => parameter.Type.SpecialType != SpecialType.System_Int64) ||
            !ParameterNamesMatch(
                root,
                "value",
                "stateReservoir",
                "stateGasUsed",
                "stateGasSpill",
                "stateGasSpillRefunded",
                "stateGasCost") ||
            calculateSpill.ReturnType.SpecialType != SpecialType.System_UInt64 ||
            calculateSpill.Parameters.Length != 2 ||
            calculateSpill.Parameters.Any(static parameter => parameter.Type.SpecialType != SpecialType.System_Int64) ||
            !ParameterNamesMatch(calculateSpill, "stateReservoir", "stateGasCost"))
        {
            throw new ExtractionException(
                "Lean emission requires exactly the pinned TryCharge and CalculateSpill machine signatures.");
        }
    }

    private static bool ParameterNamesMatch(IMethodSymbol method, params string[] expected)
    {
        if (method.Parameters.Length != expected.Length)
        {
            return false;
        }

        for (int i = 0; i < expected.Length; i++)
        {
            if (method.Parameters[i].Name != expected[i])
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateResultShape(ITypeSymbol resultType)
    {
        if (resultType.ToDisplayString() != ResultTypeName || resultType is not INamedTypeSymbol namedResult)
        {
            throw new ExtractionException($"Lean emission received unexpected result type '{resultType}'.");
        }

        (string Name, string Type)[] expectedFields =
        [
            ("Outcome", OutcomeTypeName),
            ("Value", "ulong"),
            ("StateReservoir", "long"),
            ("StateGasUsed", "long"),
            ("StateGasSpill", "long"),
            ("StateGasSpillRefunded", "long"),
        ];
        IFieldSymbol[] fields = namedResult.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(static field => !field.IsStatic && !field.IsImplicitlyDeclared)
            .OrderBy(static field => field.Locations[0].SourceSpan.Start)
            .ToArray();
        if (fields.Length != expectedFields.Length)
        {
            throw new ExtractionException("Lean emission requires the pinned StateGasChargeResult fields.");
        }

        for (int i = 0; i < fields.Length; i++)
        {
            if (fields[i].Name != expectedFields[i].Name ||
                fields[i].Type.ToDisplayString() != expectedFields[i].Type ||
                !fields[i].IsReadOnly ||
                fields[i].DeclaredAccessibility != Accessibility.Public)
            {
                throw new ExtractionException("Lean emission requires the pinned StateGasChargeResult fields.");
            }
        }
    }

    private static string Indent(string value, int spaces)
    {
        string prefix = new(' ', spaces);
        return prefix + value.Replace("\n", "\n" + prefix, StringComparison.Ordinal);
    }

    private sealed class MethodEmitter(
        SemanticModel semanticModel,
        IMethodSymbol calculateSpill)
    {
        private static readonly HashSet<string> ReservedIdentifiers =
        [
            "def",
            "do",
            "else",
            "end",
            "forall",
            "fun",
            "if",
            "in",
            "inductive",
            "let",
            "match",
            "namespace",
            "private",
            "protected",
            "return",
            "structure",
            "then",
            "theorem",
            "where",
            "with",
        ];

        private readonly SemanticModel _semanticModel = semanticModel;
        private readonly IMethodSymbol _calculateSpill = calculateSpill;

        public string EmitMethodBody(IMethodSymbol method)
        {
            if (method.DeclaringSyntaxReferences.Single().GetSyntax() is not MethodDeclarationSyntax { Body: not null } declaration)
            {
                throw new ExtractionException($"Lean emission requires a block-bodied method for '{method.Name}'.");
            }

            return EmitStatements(declaration.Body.Statements, 0);
        }

        private string EmitStatements(SyntaxList<StatementSyntax> statements, int index)
        {
            if (index >= statements.Count)
            {
                throw new ExtractionException("Lean emission reached a method path without a return value.");
            }

            StatementSyntax statement = statements[index];
            switch (statement)
            {
                case ReturnStatementSyntax { Expression: not null } returnStatement:
                    if (index != statements.Count - 1)
                    {
                        throw new ExtractionException("Lean emission does not accept statements after a return.");
                    }

                    return EmitExpression(returnStatement.Expression);

                case LocalDeclarationStatementSyntax localDeclaration:
                    if (localDeclaration.Declaration.Variables.Count != 1 ||
                        localDeclaration.Declaration.Variables[0] is not { Initializer.Value: ExpressionSyntax initializer } variable)
                    {
                        throw new ExtractionException("Lean emission requires initialized, single-local declarations.");
                    }

                    ILocalSymbol local = _semanticModel.GetDeclaredSymbol(variable) as ILocalSymbol
                        ?? throw new ExtractionException($"Roslyn did not bind local '{variable.Identifier.ValueText}'.");
                    return $"let {Identifier(local.Name)} : {LeanType(local.Type)} := {EmitExpression(initializer)}\n" +
                           EmitStatements(statements, index + 1);

                case IfStatementSyntax { Else: null } ifStatement:
                    if (ifStatement.Statement is not BlockSyntax { Statements.Count: 1 } block ||
                        block.Statements[0] is not ReturnStatementSyntax { Expression: not null } branchReturn)
                    {
                        throw new ExtractionException(
                            "Lean emission accepts only an early-return block as the body of an if statement.");
                    }

                    string condition = EmitExpression(ifStatement.Condition);
                    string whenTrue = EmitExpression(branchReturn.Expression);
                    string whenFalse = EmitStatements(statements, index + 1);
                    return $"if {condition} then\n{Indent(whenTrue, 2)}\nelse\n{Indent(whenFalse, 2)}";

                default:
                    throw new ExtractionException(
                        $"Statement '{statement.Kind()}' is not handled by Lean emission.");
            }
        }

        private string EmitExpression(ExpressionSyntax expression)
        {
            IOperation operation = _semanticModel.GetOperation(expression)
                ?? throw new ExtractionException($"Roslyn did not bind expression '{expression}'.");
            return EmitOperation(operation);
        }

        private string EmitOperation(IOperation operation) => operation switch
        {
            ILiteralOperation literal => EmitLiteral(literal),
            IParameterReferenceOperation parameter => Identifier(parameter.Parameter.Name),
            ILocalReferenceOperation local => Identifier(local.Local.Name),
            IFieldReferenceOperation field => EmitEnumConstant(field),
            IParenthesizedOperation parenthesized => EmitOperation(parenthesized.Operand),
            IBinaryOperation binary => EmitBinary(binary),
            IConversionOperation conversion => EmitConversion(conversion),
            IInvocationOperation invocation => EmitInvocation(invocation),
            IObjectCreationOperation creation => EmitObjectCreation(creation),
            IConditionalOperation conditional => EmitConditional(conditional),
            _ => throw new ExtractionException(
                $"Operation '{operation.Kind}' is validated for IR but not handled by Lean emission."),
        };

        private string EmitConditional(IConditionalOperation conditional)
        {
            if (conditional.WhenFalse is null)
            {
                throw new ExtractionException("A conditional without an else value is not handled by Lean emission.");
            }

            return $"if {EmitOperation(conditional.Condition)} then " +
                   $"{EmitOperation(conditional.WhenTrue)} else {EmitOperation(conditional.WhenFalse)}";
        }

        private static string EmitLiteral(ILiteralOperation literal)
        {
            if (!literal.ConstantValue.HasValue)
            {
                throw new ExtractionException("Lean emission received a nonconstant literal.");
            }

            return literal.ConstantValue.Value switch
            {
                bool value => value ? "true" : "false",
                byte or sbyte or short or ushort or int or uint or long or ulong =>
                    Convert.ToString(literal.ConstantValue.Value, CultureInfo.InvariantCulture)!,
                _ => throw new ExtractionException(
                    $"Literal type '{literal.Type}' is not handled by Lean emission."),
            };
        }

        private string EmitBinary(IBinaryOperation binary)
        {
            if (binary.IsChecked || binary.IsLifted || binary.OperatorMethod is not null)
            {
                throw new ExtractionException(
                    $"Checked, lifted, or user-defined binary operator '{binary.OperatorKind}' is not handled by Lean emission.");
            }

            string left = EmitOperation(binary.LeftOperand);
            string right = EmitOperation(binary.RightOperand);
            return binary.OperatorKind switch
            {
                BinaryOperatorKind.GreaterThanOrEqual => $"({left} >= {right})",
                BinaryOperatorKind.GreaterThan => $"({left} > {right})",
                BinaryOperatorKind.LessThan => $"({left} < {right})",
                BinaryOperatorKind.LessThanOrEqual => $"({left} <= {right})",
                BinaryOperatorKind.Add when binary.Type?.SpecialType == SpecialType.System_Int64 =>
                    $"addInt64 {Parenthesize(left)} {Parenthesize(right)}",
                BinaryOperatorKind.Subtract when binary.Type?.SpecialType == SpecialType.System_Int64 =>
                    $"subInt64 {Parenthesize(left)} {Parenthesize(right)}",
                BinaryOperatorKind.Subtract when binary.Type?.SpecialType == SpecialType.System_UInt64 =>
                    $"subUInt64 {Parenthesize(left)} {Parenthesize(right)}",
                _ => throw new ExtractionException(
                    $"Binary operator '{binary.OperatorKind}' on '{binary.Type}' is not handled by Lean emission."),
            };
        }

        private string EmitConversion(IConversionOperation conversion)
        {
            if (conversion.IsChecked || conversion.OperatorMethod is not null || conversion.Type is null || conversion.Operand.Type is null)
            {
                throw new ExtractionException("Checked, user-defined, or untyped conversion is not handled by Lean emission.");
            }

            string operand = EmitOperation(conversion.Operand);
            SpecialType source = conversion.Operand.Type.SpecialType;
            SpecialType target = conversion.Type.SpecialType;
            if (conversion.Conversion.IsIdentity || source == target)
            {
                return operand;
            }

            if (source == SpecialType.System_Int64 && target == SpecialType.System_UInt64)
            {
                return $"int64ToUInt64 {Parenthesize(operand)}";
            }

            if (source == SpecialType.System_UInt64 && target == SpecialType.System_Int64)
            {
                return $"uint64ToInt64 {Parenthesize(operand)}";
            }

            if (conversion.Operand.ConstantValue.HasValue &&
                target is SpecialType.System_Int64 or SpecialType.System_UInt64)
            {
                return EmitLiteralConstant(conversion.Operand.ConstantValue.Value);
            }

            throw new ExtractionException(
                $"Conversion from '{conversion.Operand.Type}' to '{conversion.Type}' is not handled by Lean emission.");
        }

        private string EmitInvocation(IInvocationOperation invocation)
        {
            if (invocation.Instance is not null)
            {
                throw new ExtractionException("Instance calls are not handled by Lean emission.");
            }

            string[] arguments = OrderedArguments(invocation.Arguments);
            if (SymbolEqualityComparer.Default.Equals(invocation.TargetMethod, _calculateSpill))
            {
                return "calculateSpillNormalized " + string.Join(" ", arguments.Select(Parenthesize));
            }

            if (invocation.TargetMethod.Name == nameof(Math.Min) &&
                invocation.TargetMethod.ContainingType.ToDisplayString() == typeof(Math).FullName &&
                invocation.TargetMethod.ReturnType.SpecialType == SpecialType.System_Int64 &&
                arguments.Length == 2)
            {
                return $"min {Parenthesize(arguments[0])} {Parenthesize(arguments[1])}";
            }

            throw new ExtractionException(
                $"Call '{invocation.TargetMethod}' is validated for IR but not handled by Lean emission.");
        }

        private string EmitObjectCreation(IObjectCreationOperation creation)
        {
            if (creation.Constructor is null || creation.Constructor.ContainingType.ToDisplayString() != ResultTypeName)
            {
                throw new ExtractionException(
                    $"Construction of '{creation.Type}' is not handled by Lean emission.");
            }

            string[] arguments = OrderedArguments(creation.Arguments);
            if (arguments.Length != 6)
            {
                throw new ExtractionException("StateGasChargeResult construction requires six arguments.");
            }

            return $$"""
                { outcome := {{arguments[0]}}
                  value := {{arguments[1]}}
                  stateReservoir := {{arguments[2]}}
                  stateGasUsed := {{arguments[3]}}
                  stateGasSpill := {{arguments[4]}}
                  stateGasSpillRefunded := {{arguments[5]}} }
                """;
        }

        private string EmitEnumConstant(IFieldReferenceOperation field)
        {
            if (field.Field.ContainingType.ToDisplayString() != OutcomeTypeName ||
                !field.Field.HasConstantValue)
            {
                throw new ExtractionException($"Enum field '{field.Field}' is not handled by Lean emission.");
            }

            return field.Field.Name switch
            {
                "Success" when Convert.ToInt32(field.Field.ConstantValue, CultureInfo.InvariantCulture) == 0 => ".success",
                "OutOfGas" when Convert.ToInt32(field.Field.ConstantValue, CultureInfo.InvariantCulture) == 1 => ".outOfGas",
                _ => throw new ExtractionException(
                    $"Enum field '{field.Field}' does not match the pinned outcome representation."),
            };
        }

        private string[] OrderedArguments(ImmutableArray<IArgumentOperation> arguments)
        {
            string[] ordered = new string[arguments.Length];
            foreach (IArgumentOperation argument in arguments)
            {
                if (argument.Parameter is null ||
                    argument.ArgumentKind != ArgumentKind.Explicit ||
                    argument.Parameter.Ordinal < 0 ||
                    argument.Parameter.Ordinal >= ordered.Length ||
                    ordered[argument.Parameter.Ordinal] is not null)
                {
                    throw new ExtractionException("Lean emission requires explicit positional arguments.");
                }

                ordered[argument.Parameter.Ordinal] = EmitOperation(argument.Value);
            }

            if (ordered.Any(static argument => argument is null))
            {
                throw new ExtractionException("Lean emission received an incomplete argument list.");
            }

            return ordered;
        }

        private static string LeanType(ITypeSymbol type) => type.SpecialType switch
        {
            SpecialType.System_Int64 => "Int",
            SpecialType.System_UInt64 => "Nat",
            _ => throw new ExtractionException($"Local type '{type}' is not handled by Lean emission."),
        };

        private static string EmitLiteralConstant(object? value) => value switch
        {
            byte or sbyte or short or ushort or int or uint or long or ulong =>
                Convert.ToString(value, CultureInfo.InvariantCulture)!,
            _ => throw new ExtractionException($"Constant '{value}' is not handled by Lean emission."),
        };

        private static string Identifier(string value)
        {
            if (string.IsNullOrEmpty(value) ||
                ReservedIdentifiers.Contains(value) ||
                !char.IsLetter(value[0]) && value[0] != '_' ||
                value.Skip(1).Any(static character => !char.IsLetterOrDigit(character) && character != '_'))
            {
                throw new ExtractionException($"Identifier '{value}' cannot be emitted safely to Lean.");
            }

            return value;
        }

        private static string Parenthesize(string value) => value.IndexOfAny([' ', '\n']) >= 0 ? $"({value})" : value;
    }
}
