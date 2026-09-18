// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Evm.Lean.Extractor;

internal sealed record PrecompileGasPricingKernelModel(IReadOnlyList<string> Outcomes);

internal static class PrecompileGasPricingLeanEmitter
{
    private const string KernelTypeName = "Nethermind.Evm.GasPolicy.PrecompileGasPricingKernel";
    private const string ResultTypeName = "Nethermind.Evm.GasPolicy.PrecompileGasPricingResult";
    private const string OutcomeTypeName = "Nethermind.Evm.GasPolicy.PrecompileGasPricingOutcome";
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static void ValidateRoots(IReadOnlyList<IMethodSymbol> roots)
    {
        if (roots.Count != 1)
        {
            throw new ExtractionException("Precompile gas pricing Lean emission requires exactly one root.");
        }

        IMethodSymbol root = roots[0];
        if (root.ContainingType.ToDisplayString() != KernelTypeName ||
            root.Name != "TryConsume" ||
            !root.IsStatic ||
            root.ReturnType.ToDisplayString() != ResultTypeName ||
            root.Parameters.Length != 3 ||
            root.Parameters.Any(static parameter => parameter.Type.SpecialType != SpecialType.System_UInt64) ||
            !root.Parameters.Select(static parameter => parameter.Name)
                .SequenceEqual(["gas", "baseGasCost", "dataGasCost"], StringComparer.Ordinal))
        {
            throw new ExtractionException(
                "Precompile gas pricing root must retain TryConsume(ulong gas, ulong baseGasCost, ulong dataGasCost).");
        }

        ValidateContainer(root.ContainingType);
        ValidateResultShape(root.ContainingAssembly);
        RejectPreprocessorDirectives(root.DeclaringSyntaxReferences.Single().SyntaxTree, "Precompile gas pricing kernel");
    }

    public static PrecompileGasPricingKernelModel Normalize(
        SemanticModel semanticModel,
        IReadOnlyList<IMethodSymbol> roots,
        IReadOnlyList<IMethodSymbol> extractedMethods)
    {
        ValidateRoots(roots);
        IMethodSymbol root = roots[0];
        if (extractedMethods.Count != 1 ||
            !SymbolEqualityComparer.Default.Equals(extractedMethods[0], root))
        {
            throw new ExtractionException("Precompile gas pricing extraction did not retain exactly the pinned root graph.");
        }

        MethodDeclarationSyntax declaration = (MethodDeclarationSyntax)root.DeclaringSyntaxReferences.Single().GetSyntax();
        ValidateBody(declaration);
        ValidateSemanticBindings(semanticModel, declaration);
        return new PrecompileGasPricingKernelModel(["success", "baseDataOverflow", "outOfGas"]);
    }

    public static byte[] Emit(
        PrecompileGasPricingKernelModel model,
        string extractorVersion,
        string compilerVersion,
        string sourcePath,
        string sourceHash,
        string irHash)
    {
        if (!model.Outcomes.SequenceEqual(["success", "baseDataOverflow", "outOfGas"], StringComparer.Ordinal))
        {
            throw new ExtractionException("Precompile gas pricing model does not retain the pinned outcome set.");
        }

        string source = $$"""
            -- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
            -- SPDX-License-Identifier: LGPL-3.0-only

            -- This file is generated. Do not edit.
            -- Extractor version: {{extractorVersion}}
            -- Roslyn compiler version: {{compilerVersion}}
            -- Production source: {{sourcePath}}
            -- Production source SHA-256: {{sourceHash}}
            -- Canonical precompile-gas-pricing IR SHA-256: {{irHash}}

            namespace Eip803x.Generated.PrecompileGasPricingKernel

            def uint64Modulus : Nat := 2 ^ 64
            def uint64Max : Nat := uint64Modulus - 1

            def normalizeUInt64 (value : Nat) : Nat :=
              if value <= uint64Max then value else value % uint64Modulus

            inductive Outcome where
              | success
              | baseDataOverflow
              | outOfGas
              deriving DecidableEq, Repr

            structure Result where
              outcome : Outcome
              remainingGas : Nat
              chargedGas : Nat
              deriving DecidableEq, Repr

            def tryConsumeNormalized
                (gas : Nat)
                (baseGasCost : Nat)
                (dataGasCost : Nat) : Result :=
              if baseGasCost > uint64Max - dataGasCost then
                { outcome := .baseDataOverflow, remainingGas := gas, chargedGas := 0 }
              else
                let totalGasCost := baseGasCost + dataGasCost
                if gas < totalGasCost then
                  { outcome := .outOfGas, remainingGas := 0, chargedGas := 0 }
                else
                  { outcome := .success,
                    remainingGas := gas - totalGasCost,
                    chargedGas := totalGasCost }

            def tryConsume
                (gas : Nat)
                (baseGasCost : Nat)
                (dataGasCost : Nat) : Result :=
              tryConsumeNormalized
                (normalizeUInt64 gas)
                (normalizeUInt64 baseGasCost)
                (normalizeUInt64 dataGasCost)

            end Eip803x.Generated.PrecompileGasPricingKernel
            """;
        return Utf8WithoutBom.GetBytes(source.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    }

    private static void ValidateContainer(INamedTypeSymbol type)
    {
        if (!type.IsStatic || type.DeclaredAccessibility != Accessibility.Internal ||
            type.DeclaringSyntaxReferences is not [SyntaxReference reference] ||
            reference.GetSyntax() is not ClassDeclarationSyntax declaration ||
            declaration.Modifiers.Any(SyntaxKind.PartialKeyword) ||
            declaration.AttributeLists.Count != 0 || declaration.BaseList is not null ||
            declaration.Members.Count != 1 || declaration.Members[0] is not MethodDeclarationSyntax)
        {
            throw new ExtractionException("Precompile gas pricing kernel container changed its pinned shape.");
        }
    }

    private static void ValidateResultShape(IAssemblySymbol assembly)
    {
        INamedTypeSymbol outcome = assembly.GetTypeByMetadataName(OutcomeTypeName)
            ?? throw new ExtractionException("Precompile gas pricing outcome type was not found.");
        IFieldSymbol[] outcomeCases = outcome.GetMembers().OfType<IFieldSymbol>()
            .Where(static field => field.HasConstantValue)
            .OrderBy(static field => Convert.ToByte(field.ConstantValue, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        if (outcome.TypeKind != TypeKind.Enum || outcome.EnumUnderlyingType?.SpecialType != SpecialType.System_Byte ||
            outcome.DeclaringSyntaxReferences is not [SyntaxReference outcomeReference] ||
            outcomeReference.GetSyntax() is not EnumDeclarationSyntax outcomeDeclaration ||
            outcomeDeclaration.AttributeLists.Count != 0 ||
            !outcomeCases.Select(static field => field.Name)
                .SequenceEqual(["Success", "BaseDataOverflow", "OutOfGas"], StringComparer.Ordinal) ||
            !outcomeCases.Select(static field => Convert.ToByte(field.ConstantValue, System.Globalization.CultureInfo.InvariantCulture))
                .SequenceEqual(new byte[] { 0, 1, 2 }))
        {
            throw new ExtractionException("Precompile gas pricing outcome does not retain the pinned cases.");
        }

        INamedTypeSymbol result = assembly.GetTypeByMetadataName(ResultTypeName)
            ?? throw new ExtractionException("Precompile gas pricing result type was not found.");
        IFieldSymbol[] fields = result.GetMembers().OfType<IFieldSymbol>()
            .Where(static field => !field.IsStatic && !field.IsImplicitlyDeclared)
            .OrderBy(static field => field.Locations[0].SourceSpan.Start)
            .ToArray();
        (string Name, string Type)[] expected =
        [
            ("Outcome", OutcomeTypeName),
            ("RemainingGas", "ulong"),
            ("ChargedGas", "ulong"),
        ];
        if (!result.IsReadOnly || result.DeclaredAccessibility != Accessibility.Internal ||
            result.DeclaringSyntaxReferences is not [SyntaxReference resultReference] ||
            resultReference.GetSyntax() is not StructDeclarationSyntax resultDeclaration ||
            resultDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword) ||
            resultDeclaration.AttributeLists.Count != 0 || resultDeclaration.BaseList is not null ||
            fields.Length != expected.Length ||
            fields.Where((field, index) => field.Name != expected[index].Name ||
                field.Type.ToDisplayString() != expected[index].Type ||
                !field.IsReadOnly || field.DeclaredAccessibility != Accessibility.Public).Any())
        {
            throw new ExtractionException("Precompile gas pricing result does not retain its pinned value fields.");
        }
    }

    private static void ValidateBody(MethodDeclarationSyntax declaration)
    {
        if (declaration.Body is not
            {
                Statements:
                [
                    IfStatementSyntax overflow,
                    LocalDeclarationStatementSyntax total,
                    IfStatementSyntax insufficient,
                    ReturnStatementSyntax { Expression: not null } success,
                ],
            })
        {
            throw new ExtractionException("Precompile gas pricing kernel must retain its ordered overflow, total, affordability, and success steps.");
        }

        if (Canonical(overflow.Condition) != "baseGasCost>ulong.MaxValue-dataGasCost" ||
            overflow.Statement is not ReturnStatementSyntax { Expression: not null } overflowReturn ||
            Canonical(overflowReturn.Expression) !=
            "newPrecompileGasPricingResult(PrecompileGasPricingOutcome.BaseDataOverflow,gas,0)" ||
            Canonical(total) != "ulongtotalGasCost=baseGasCost+dataGasCost;" ||
            Canonical(insufficient.Condition) != "gas<totalGasCost" ||
            insufficient.Statement is not ReturnStatementSyntax { Expression: not null } insufficientReturn ||
            Canonical(insufficientReturn.Expression) !=
            "newPrecompileGasPricingResult(PrecompileGasPricingOutcome.OutOfGas,0,0)" ||
            Canonical(success.Expression) !=
            "newPrecompileGasPricingResult(PrecompileGasPricingOutcome.Success,gas-totalGasCost,totalGasCost)")
        {
            throw new ExtractionException("Precompile gas pricing kernel does not retain exact overflow, OOG, and debit semantics.");
        }
    }

    private static void ValidateSemanticBindings(SemanticModel semanticModel, MethodDeclarationSyntax declaration)
    {
        IObjectCreationOperation[] creations = declaration.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Select(node => semanticModel.GetOperation(node) as IObjectCreationOperation)
            .Where(static operation => operation is not null)
            .Cast<IObjectCreationOperation>()
            .ToArray();
        if (creations.Length != 3 || creations.Any(creation =>
                creation.Constructor?.ContainingType.ToDisplayString() != ResultTypeName ||
                creation.Arguments.Length != 3))
        {
            throw new ExtractionException("Precompile gas pricing result constructions did not bind to the pinned result type.");
        }

        IFieldReferenceOperation[] outcomeReferences = declaration.DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Select(node => semanticModel.GetOperation(node) as IFieldReferenceOperation)
            .Where(static operation => operation is not null)
            .Cast<IFieldReferenceOperation>()
            .Where(static operation => operation.Field.ContainingType.ToDisplayString() == OutcomeTypeName)
            .ToArray();
        if (!outcomeReferences.Select(static operation => operation.Field.Name).SequenceEqual(
                ["BaseDataOverflow", "OutOfGas", "Success"], StringComparer.Ordinal))
        {
            throw new ExtractionException("Precompile gas pricing outcome references did not bind to the pinned enum members.");
        }
    }

    private static void RejectPreprocessorDirectives(SyntaxTree tree, string subject)
    {
        if (tree.GetRoot().DescendantTrivia(descendIntoTrivia: true).Any(static trivia => trivia.IsDirective))
        {
            throw new ExtractionException($"{subject} must not contain preprocessor directives.");
        }
    }

    private static string Canonical(SyntaxNode node) =>
        string.Concat(node.WithoutTrivia().ToFullString().Where(static character => !char.IsWhiteSpace(character)));
}
