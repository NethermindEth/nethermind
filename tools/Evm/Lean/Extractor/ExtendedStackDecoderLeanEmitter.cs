// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.Extractor;

internal sealed record ExtendedStackDecoderField(string Name, string Type, int ConstructorParameterOrdinal);

internal sealed record ExtendedStackDecoderKernelModel(
    IReadOnlyList<ExtendedStackDecoderField> SingleResultFields,
    IReadOnlyList<ExtendedStackDecoderField> PairResultFields);

internal static class ExtendedStackDecoderLeanEmitter
{
    private const string KernelTypeName = "Nethermind.Evm.ExtendedStackDecoderKernel";
    private const string SingleResultTypeName = "Nethermind.Evm.ExtendedStackSingleDecode";
    private const string PairResultTypeName = "Nethermind.Evm.ExtendedStackPairDecode";
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static void ValidateRoots(IReadOnlyList<IMethodSymbol> roots)
    {
        if (roots.Count != 2 ||
            !roots.Select(static root => root.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(["DecodePair", "DecodeSingle"], StringComparer.Ordinal))
        {
            throw new ExtractionException("Extended-stack decoder Lean emission requires DecodeSingle and DecodePair roots.");
        }

        foreach (IMethodSymbol root in roots)
        {
            string expectedReturn = root.Name == "DecodeSingle" ? SingleResultTypeName : PairResultTypeName;
            if (root.ContainingType.ToDisplayString() != KernelTypeName || !root.IsStatic || root.IsGenericMethod ||
                root.ReturnType.ToDisplayString() != expectedReturn || root.Parameters.Length != 1 ||
                !ParameterMatches(root.Parameters[0], "immediate", "byte") ||
                root.Parameters[0].RefKind != RefKind.None)
            {
                throw new ExtractionException(
                    "Extended-stack decoder root does not have the pinned Decode*(byte) value signature.");
            }

            ValidateAggressiveInlining(root);
        }
    }

    public static ExtendedStackDecoderKernelModel Normalize(
        SemanticModel semanticModel,
        IReadOnlyList<IMethodSymbol> roots,
        IReadOnlyList<IMethodSymbol> extractedMethods)
    {
        ValidateRoots(roots);
        IMethodSymbol single = roots.Single(static root => root.Name == "DecodeSingle");
        IMethodSymbol pair = roots.Single(static root => root.Name == "DecodePair");
        string[] expectedMethods = [StateGasChargeExtractor.Display(pair), StateGasChargeExtractor.Display(single)];
        if (!extractedMethods.Select(StateGasChargeExtractor.Display).SequenceEqual(expectedMethods, StringComparer.Ordinal))
        {
            throw new ExtractionException(
                "Extended-stack decoder extraction did not retain exactly the two pinned decoder roots.");
        }

        IAssemblySymbol assembly = single.ContainingAssembly;
        IReadOnlyList<ExtendedStackDecoderField> singleFields = NormalizeResultFields(
            semanticModel,
            FindType(assembly, SingleResultTypeName),
            [("IsValid", "bool"), ("Depth", "int")]);
        IReadOnlyList<ExtendedStackDecoderField> pairFields = NormalizeResultFields(
            semanticModel,
            FindType(assembly, PairResultTypeName),
            [("IsValid", "bool"), ("FirstPosition", "int"), ("SecondPosition", "int")]);
        ValidateSingleBody(single);
        ValidatePairBody(pair);

        ExtendedStackDecoderKernelModel model = new(singleFields, pairFields);
        ValidateModel(model);
        return model;
    }

    public static byte[] Emit(
        ExtendedStackDecoderKernelModel model,
        string extractorVersion,
        string compilerVersion,
        string sourcePath,
        string sourceHash,
        string irHash)
    {
        ValidateModel(model);
        string source = $$"""
            -- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
            -- SPDX-License-Identifier: LGPL-3.0-only

            -- This file is generated. Do not edit.
            -- Extractor version: {{extractorVersion}}
            -- Roslyn compiler version: {{compilerVersion}}
            -- Production source: {{sourcePath}}
            -- Production source SHA-256: {{sourceHash}}
            -- Canonical extended-stack-decoder IR SHA-256: {{irHash}}

            namespace Eip803x.Generated.ExtendedStackDecoderKernel

            def byteModulus : Nat := 2 ^ 8

            def normalizeByte (value : Nat) : Nat :=
              value % byteModulus

            structure SingleDecode where
              isValid : Bool
              depth : Nat
              deriving DecidableEq, Repr

            structure PairDecode where
              isValid : Bool
              firstPosition : Nat
              secondPosition : Nat
              deriving DecidableEq, Repr

            def decodeSingle (immediate : Nat) : SingleDecode :=
              let value := normalizeByte immediate
              { isValid := if 91 ≤ value ∧ value ≤ 127 then false else true
                depth := (value + 145) % byteModulus }

            /-- Production positions are one-based because `EvmStack.Exchange` consumes one-based positions. -/
            def decodePair (immediate : Nat) : PairDecode :=
              let value := normalizeByte immediate
              let shifted := Nat.xor value 143
              let quotient := shifted / 16
              let remainder := shifted % 16
              { isValid := if 82 ≤ value ∧ value ≤ 127 then false else true
                firstPosition := if quotient < remainder then quotient + 2 else remainder + 2
                secondPosition := if quotient < remainder then remainder + 2 else 30 - quotient }

            end Eip803x.Generated.ExtendedStackDecoderKernel
            """;
        return Utf8WithoutBom.GetBytes(source.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    }

    private static bool ParameterMatches(IParameterSymbol parameter, string name, string type) =>
        parameter.Name == name && parameter.Type.ToDisplayString() == type;

    private static void ValidateAggressiveInlining(IMethodSymbol method)
    {
        AttributeData[] attributes = method.GetAttributes().ToArray();
        if (attributes.Length != 1 ||
            attributes[0].AttributeClass?.ToDisplayString() !=
            typeof(System.Runtime.CompilerServices.MethodImplAttribute).FullName ||
            attributes[0].ConstructorArguments is not [{ Value: int option }] ||
            option != (int)System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)
        {
            throw new ExtractionException(
                "Extended-stack decoder roots must retain their AggressiveInlining hot-path contract.");
        }
    }

    private static INamedTypeSymbol FindType(IAssemblySymbol assembly, string name) =>
        assembly.GetTypeByMetadataName(name)
        ?? throw new ExtractionException($"Extended-stack decoder source does not declare or bind '{name}'.");

    private static IReadOnlyList<ExtendedStackDecoderField> NormalizeResultFields(
        SemanticModel semanticModel,
        INamedTypeSymbol type,
        IReadOnlyList<(string Name, string Type)> expected)
    {
        IFieldSymbol[] fields = type.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(static field => !field.IsStatic && !field.IsImplicitlyDeclared)
            .OrderBy(static field => field.Locations[0].SourceSpan.Start)
            .ToArray();
        IMethodSymbol[] constructors = type.InstanceConstructors
            .Where(static constructor => !constructor.IsImplicitlyDeclared)
            .ToArray();
        if (!type.IsReadOnly || fields.Length != expected.Count || constructors.Length != 1 ||
            constructors[0].Parameters.Length != expected.Count ||
            type.DeclaringSyntaxReferences is not [SyntaxReference typeReference] ||
            typeReference.GetSyntax() is not StructDeclarationSyntax typeDeclaration ||
            typeDeclaration.ParameterList is null ||
            typeDeclaration.Members.Any(static member => member is not FieldDeclarationSyntax))
        {
            throw new ExtractionException(
                "Extended-stack decoder result must retain the pinned readonly-primary shape.");
        }

        IMethodSymbol constructor = constructors[0];
        ExtendedStackDecoderField[] normalized = new ExtendedStackDecoderField[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            VariableDeclaratorSyntax declaration = fields[i].DeclaringSyntaxReferences.Single().GetSyntax() as VariableDeclaratorSyntax
                ?? throw new ExtractionException("Extended-stack decoder result field is not a variable declaration.");
            if (declaration.Initializer?.Value is not IdentifierNameSyntax initializer ||
                semanticModel.GetSymbolInfo(initializer).Symbol is not IParameterSymbol parameter ||
                !SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, constructor) ||
                parameter.Ordinal != i || fields[i].Name != expected[i].Name ||
                fields[i].Type.ToDisplayString() != expected[i].Type || !fields[i].IsReadOnly ||
                fields[i].DeclaredAccessibility != Accessibility.Public ||
                !SymbolEqualityComparer.Default.Equals(fields[i].Type, constructor.Parameters[i].Type))
            {
                throw new ExtractionException(
                    "Extended-stack decoder result fields must project their primary-constructor parameters in order.");
            }

            normalized[i] = new ExtendedStackDecoderField(fields[i].Name, fields[i].Type.ToDisplayString(), parameter.Ordinal);
        }

        return normalized;
    }

    private static void ValidateSingleBody(IMethodSymbol method)
    {
        if (method.DeclaringSyntaxReferences.Single().GetSyntax() is not MethodDeclarationSyntax
            {
                Body:
                {
                    Statements:
                    [
                        LocalDeclarationStatementSyntax depth,
                        LocalDeclarationStatementSyntax valid,
                        ReturnStatementSyntax { Expression: not null } result,
                    ],
                },
            })
        {
            throw new ExtractionException("Extended-stack single decoder must retain its three pinned statements.");
        }

        ValidateLocal(depth, "int", "depth", "(immediate+145)&0xFF");
        ValidateLocal(valid, "bool", "isValid", "(uint)(immediate-0x5B)>0x24");
        if (WithoutTrivia(result.Expression!) != "newExtendedStackSingleDecode(isValid,depth)")
        {
            throw new ExtractionException("Extended-stack single decoder must return the pinned validity/depth result.");
        }
    }

    private static void ValidatePairBody(IMethodSymbol method)
    {
        if (method.DeclaringSyntaxReferences.Single().GetSyntax() is not MethodDeclarationSyntax
            {
                Body:
                {
                    Statements:
                    [
                        LocalDeclarationStatementSyntax shifted,
                        LocalDeclarationStatementSyntax quotient,
                        LocalDeclarationStatementSyntax remainder,
                        LocalDeclarationStatementSyntax mask,
                        LocalDeclarationStatementSyntax first,
                        LocalDeclarationStatementSyntax second,
                        LocalDeclarationStatementSyntax valid,
                        ReturnStatementSyntax { Expression: not null } result,
                    ],
                },
            })
        {
            throw new ExtractionException("Extended-stack pair decoder must retain its eight pinned statements.");
        }

        ValidateLocal(shifted, "int", "shifted", "immediate^0x8F");
        ValidateLocal(quotient, "int", "quotient", "shifted>>4");
        ValidateLocal(remainder, "int", "remainder", "shifted&0x0F");
        ValidateLocal(mask, "int", "mask", "(quotient-remainder)>>31");
        ValidateLocal(first, "int", "firstPosition", "((quotient&mask)|(remainder&~mask))+2");
        ValidateLocal(second, "int", "secondPosition", "(((remainder+1)&mask)|((29-quotient)&~mask))+1");
        ValidateLocal(valid, "bool", "isValid", "(uint)(immediate-0x52)>0x2D");
        if (WithoutTrivia(result.Expression!) != "newExtendedStackPairDecode(isValid,firstPosition,secondPosition)")
        {
            throw new ExtractionException("Extended-stack pair decoder must return the pinned validity/position result.");
        }
    }

    private static void ValidateLocal(StatementSyntax statement, string type, string name, string initializer)
    {
        if (statement is not LocalDeclarationStatementSyntax local ||
            Canonical(local.Declaration.Type) != type ||
            local.Declaration.Variables is not [{ Identifier.ValueText: var actualName, Initializer.Value: ExpressionSyntax actualInitializer }] ||
            actualName != name || WithoutTrivia(actualInitializer) != initializer)
        {
            throw new ExtractionException($"Extended-stack decoder local '{name}' does not retain its pinned shape.");
        }
    }

    private static void ValidateModel(ExtendedStackDecoderKernelModel model)
    {
        if (!model.SingleResultFields.SequenceEqual(
                [new ExtendedStackDecoderField("IsValid", "bool", 0), new ExtendedStackDecoderField("Depth", "int", 1)]) ||
            !model.PairResultFields.SequenceEqual(
                [new ExtendedStackDecoderField("IsValid", "bool", 0),
                 new ExtendedStackDecoderField("FirstPosition", "int", 1),
                 new ExtendedStackDecoderField("SecondPosition", "int", 2)]))
        {
            throw new ExtractionException("Extended-stack decoder model does not retain its pinned result projections.");
        }
    }

    private static string Canonical(SyntaxNode node) =>
        string.Concat(node.WithoutTrivia().ToFullString().Where(static character => !char.IsWhiteSpace(character)));

    private static string WithoutTrivia(SyntaxNode node) => Canonical(node);
}
