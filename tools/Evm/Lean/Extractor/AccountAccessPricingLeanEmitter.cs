// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.Extractor;

internal sealed record AccountAccessPricingKernelModel(
    IReadOnlyList<AccountAccessPricingField> ResultFields,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<string> AccessKinds);

internal sealed record AccountAccessPricingField(string Name, string Type, int ConstructorParameterOrdinal);

internal static class AccountAccessPricingLeanEmitter
{
    private const string KernelTypeName = "Nethermind.Evm.GasPolicy.AccountAccessPricingKernel";
    private const string ResultTypeName = "Nethermind.Evm.GasPolicy.AccountAccessPricingResult";
    private const string DecisionTypeName = "Nethermind.Evm.GasPolicy.AccountAccessPricingDecision";
    private const string KindTypeName = "Nethermind.Evm.GasPolicy.AccountAccessKind";
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static void ValidateRoots(IReadOnlyList<IMethodSymbol> roots)
    {
        if (roots.Count != 1)
        {
            throw new ExtractionException("Account-access pricing Lean emission requires exactly one root.");
        }

        IMethodSymbol root = roots[0];
        if (root.ContainingType.ToDisplayString() != KernelTypeName ||
            root.Name != "Price" || !root.IsStatic || root.IsGenericMethod ||
            root.ReturnType.ToDisplayString() != ResultTypeName || root.Parameters.Length != 7 ||
            !ParameterMatches(root.Parameters[0], "hotAndColdEnabled", "bool") ||
            !ParameterMatches(root.Parameters[1], "eip8038Enabled", "bool") ||
            !ParameterMatches(root.Parameters[2], "isCold", "bool") ||
            !ParameterMatches(root.Parameters[3], "isPrecompile", "bool") ||
            !ParameterMatches(root.Parameters[4], "kind", KindTypeName) ||
            !ParameterMatches(root.Parameters[5], "coldAccountAccessGas", "ulong") ||
            !ParameterMatches(root.Parameters[6], "warmAccessGas", "ulong") ||
            root.Parameters.Any(static parameter => parameter.RefKind != RefKind.None))
        {
            throw new ExtractionException(
                "Account-access pricing root does not have the pinned Price(flags, access, kind, schedule) signature.");
        }
    }

    public static AccountAccessPricingKernelModel Normalize(
        SemanticModel semanticModel,
        IReadOnlyList<IMethodSymbol> roots,
        IReadOnlyList<IMethodSymbol> extractedMethods)
    {
        ValidateRoots(roots);
        IMethodSymbol price = roots[0];
        if (extractedMethods.Count != 1 ||
            !extractedMethods.Select(StateGasChargeExtractor.Display).SequenceEqual(
                [StateGasChargeExtractor.Display(price)], StringComparer.Ordinal))
        {
            throw new ExtractionException(
                "Account-access pricing extraction did not retain exactly the pinned Price root graph.");
        }

        IAssemblySymbol assembly = price.ContainingAssembly;
        IReadOnlyList<AccountAccessPricingField> resultFields = NormalizeResultFields(
            semanticModel,
            FindType(assembly, ResultTypeName));
        IReadOnlyList<string> decisions = NormalizeEnum(
            FindType(assembly, DecisionTypeName),
            DecisionTypeName,
            ["NoCharge", "Charge"]);
        IReadOnlyList<string> accessKinds = NormalizeEnum(
            FindType(assembly, KindTypeName),
            KindTypeName,
            ["Default", "SelfDestructBeneficiary"]);
        ValidatePriceBody(price);

        AccountAccessPricingKernelModel model = new(resultFields, decisions, accessKinds);
        ValidateModel(model);
        return model;
    }

    public static byte[] Emit(
        AccountAccessPricingKernelModel model,
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
            -- Canonical account-access-pricing IR SHA-256: {{irHash}}

            namespace Eip803x.Generated.AccountAccessPricingKernel

            def uint64Modulus : Nat := 2 ^ 64
            def uint64Max : Nat := uint64Modulus - 1

            def normalizeUInt64 (value : Nat) : Nat :=
              if value <= uint64Max then value else value % uint64Modulus

            inductive Decision where
              | noCharge
              | charge
              deriving DecidableEq, Repr

            inductive AccessKind where
              | default
              | selfDestructBeneficiary
              deriving DecidableEq, Repr

            structure Result where
              decision : Decision
              amount : Nat
              deriving DecidableEq, Repr

            def priceNormalized
                (hotAndColdEnabled : Bool)
                (eip8038Enabled : Bool)
                (isCold : Bool)
                (isPrecompile : Bool)
                (kind : AccessKind)
                (coldAccountAccessGas : Nat)
                (warmAccessGas : Nat) : Result :=
              if !hotAndColdEnabled then
                { decision := .noCharge, amount := 0 }
              else if isCold && !isPrecompile then
                { decision := .charge, amount := coldAccountAccessGas }
              else if kind == .selfDestructBeneficiary && !eip8038Enabled then
                { decision := .noCharge, amount := 0 }
              else
                { decision := .charge, amount := warmAccessGas }

            def price
                (hotAndColdEnabled : Bool)
                (eip8038Enabled : Bool)
                (isCold : Bool)
                (isPrecompile : Bool)
                (kind : AccessKind)
                (coldAccountAccessGas : Nat)
                (warmAccessGas : Nat) : Result :=
              priceNormalized
                hotAndColdEnabled
                eip8038Enabled
                isCold
                isPrecompile
                kind
                (normalizeUInt64 coldAccountAccessGas)
                (normalizeUInt64 warmAccessGas)

            end Eip803x.Generated.AccountAccessPricingKernel
            """;
        return Utf8WithoutBom.GetBytes(source.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    }

    private static bool ParameterMatches(IParameterSymbol parameter, string name, string type) =>
        parameter.Name == name && parameter.Type.ToDisplayString() == type;

    private static INamedTypeSymbol FindType(IAssemblySymbol assembly, string name) =>
        assembly.GetTypeByMetadataName(name)
        ?? throw new ExtractionException($"Account-access pricing source does not declare or bind '{name}'.");

    private static IReadOnlyList<AccountAccessPricingField> NormalizeResultFields(
        SemanticModel semanticModel,
        INamedTypeSymbol type)
    {
        IFieldSymbol[] fields = type.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(static field => !field.IsStatic && !field.IsImplicitlyDeclared)
            .OrderBy(static field => field.Locations[0].SourceSpan.Start)
            .ToArray();
        IMethodSymbol[] constructors = type.InstanceConstructors
            .Where(static constructor => !constructor.IsImplicitlyDeclared)
            .ToArray();
        if (!type.IsReadOnly || fields.Length != 2 || constructors.Length != 1 ||
            constructors[0].Parameters.Length != 2 ||
            type.DeclaringSyntaxReferences is not [SyntaxReference typeReference] ||
            typeReference.GetSyntax() is not StructDeclarationSyntax typeDeclaration ||
            typeDeclaration.ParameterList is null ||
            typeDeclaration.Members.Any(static member => member is not FieldDeclarationSyntax))
        {
            throw new ExtractionException(
                "Account-access pricing result must retain the pinned readonly-primary shape.");
        }

        IMethodSymbol constructor = constructors[0];
        (string Name, string Type)[] expected =
        [
            ("Decision", DecisionTypeName),
            ("Amount", "ulong"),
        ];
        AccountAccessPricingField[] normalized = new AccountAccessPricingField[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            VariableDeclaratorSyntax declaration = fields[i].DeclaringSyntaxReferences.Single().GetSyntax() as VariableDeclaratorSyntax
                ?? throw new ExtractionException("Account-access pricing result field is not a variable declaration.");
            if (declaration.Initializer?.Value is not IdentifierNameSyntax initializer ||
                semanticModel.GetSymbolInfo(initializer).Symbol is not IParameterSymbol parameter ||
                !SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, constructor) ||
                parameter.Ordinal != i || fields[i].Name != expected[i].Name ||
                fields[i].Type.ToDisplayString() != expected[i].Type || !fields[i].IsReadOnly ||
                fields[i].DeclaredAccessibility != Accessibility.Public ||
                !SymbolEqualityComparer.Default.Equals(fields[i].Type, constructor.Parameters[i].Type))
            {
                throw new ExtractionException(
                    "Account-access pricing result fields must project primary parameters in order.");
            }

            normalized[i] = new(LowerFirst(fields[i].Name), LeanType(fields[i].Type.ToDisplayString()), i);
        }

        return normalized;
    }

    private static IReadOnlyList<string> NormalizeEnum(
        INamedTypeSymbol type,
        string expectedType,
        IReadOnlyList<string> expectedNames)
    {
        IFieldSymbol[] fields = type.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(static field => field.HasConstantValue)
            .OrderBy(static field => field.ConstantValue)
            .ToArray();
        if (type.ToDisplayString() != expectedType || type.TypeKind != TypeKind.Enum ||
            type.EnumUnderlyingType?.SpecialType != SpecialType.System_Byte ||
            !fields.Select(static field => field.Name).SequenceEqual(expectedNames, StringComparer.Ordinal) ||
            !fields.Select(static field => Convert.ToByte(field.ConstantValue, System.Globalization.CultureInfo.InvariantCulture))
                .SequenceEqual(Enumerable.Range(0, expectedNames.Count).Select(static value => (byte)value)))
        {
            throw new ExtractionException($"Account-access pricing enum '{expectedType}' no longer has its pinned byte cases.");
        }

        return fields.Select(static field => LowerFirst(field.Name)).ToArray();
    }

    private static void ValidatePriceBody(IMethodSymbol root)
    {
        if (root.DeclaringSyntaxReferences.Single().GetSyntax() is not MethodDeclarationSyntax
            {
                Body.Statements:
                [
                    IfStatementSyntax hotAndCold,
                    IfStatementSyntax cold,
                    IfStatementSyntax legacySelfDestruct,
                    ReturnStatementSyntax { Expression: not null } finalReturn,
                ],
            })
        {
            throw new ExtractionException("Account-access pricing Price must retain its four-step decision body.");
        }

        ValidateBranch(hotAndCold, "!hotAndColdEnabled", "newAccountAccessPricingResult(AccountAccessPricingDecision.NoCharge,0)");
        ValidateBranch(cold, "isCold&&!isPrecompile", "newAccountAccessPricingResult(AccountAccessPricingDecision.Charge,coldAccountAccessGas)");
        ValidateBranch(
            legacySelfDestruct,
            "kind==AccountAccessKind.SelfDestructBeneficiary&&!eip8038Enabled",
            "newAccountAccessPricingResult(AccountAccessPricingDecision.NoCharge,0)");
        if (WithoutTrivia(finalReturn.Expression) !=
            "newAccountAccessPricingResult(AccountAccessPricingDecision.Charge,warmAccessGas)")
        {
            throw new ExtractionException("Account-access pricing Price no longer returns the warm-cost decision.");
        }
    }

    private static void ValidateBranch(IfStatementSyntax branch, string condition, string result)
    {
        if (WithoutTrivia(branch.Condition) != condition || branch.Else is not null ||
            branch.Statement is not ReturnStatementSyntax { Expression: not null } returnStatement ||
            WithoutTrivia(returnStatement.Expression) != result)
        {
            throw new ExtractionException("Account-access pricing Price no longer has the pinned branch order/formula.");
        }
    }

    private static void ValidateModel(AccountAccessPricingKernelModel model)
    {
        if (!model.ResultFields.Select(static field => (field.Name, field.Type)).SequenceEqual(
                [("decision", "Decision"), ("amount", "Nat")]) ||
            !model.Decisions.SequenceEqual(["noCharge", "charge"], StringComparer.Ordinal) ||
            !model.AccessKinds.SequenceEqual(["default", "selfDestructBeneficiary"], StringComparer.Ordinal))
        {
            throw new ExtractionException("Account-access pricing Lean model does not match the pinned shape.");
        }
    }

    private static string WithoutTrivia(SyntaxNode syntax) =>
        string.Concat(syntax.WithoutTrivia().ToFullString().Where(static character => !char.IsWhiteSpace(character)));

    private static string LowerFirst(string value) => char.ToLowerInvariant(value[0]) + value[1..];

    private static string LeanType(string type) => type switch
    {
        DecisionTypeName => "Decision",
        "ulong" => "Nat",
        _ => throw new ExtractionException($"Account-access pricing type '{type}' is not Lean-representable."),
    };
}
