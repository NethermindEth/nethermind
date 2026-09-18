// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.Extractor;

internal sealed record SStorePricingKernelModel(
    IReadOnlyList<SStorePricingField> InputFields,
    IReadOnlyList<SStorePricingField> AccessScheduleFields,
    IReadOnlyList<SStorePricingField> PostAccessScheduleFields,
    IReadOnlyList<SStorePricingField> PostAccessResultFields,
    IReadOnlyList<SStorePricingField> ResultFields,
    IReadOnlyList<string> AccessStatuses);

internal sealed record SStorePricingField(string Name, string Type, int ConstructorParameterOrdinal);

internal static class SStorePricingLeanEmitter
{
    private const string KernelTypeName = "Nethermind.Evm.GasPolicy.SStorePricingKernel";
    private const string InputTypeName = "Nethermind.Evm.GasPolicy.SStorePricingInput";
    private const string AccessScheduleTypeName = "Nethermind.Evm.GasPolicy.SStoreAccessPricingSchedule";
    private const string PostAccessScheduleTypeName = "Nethermind.Evm.GasPolicy.SStorePostAccessPricingSchedule";
    private const string PostAccessResultTypeName = "Nethermind.Evm.GasPolicy.SStorePostAccessPricingResult";
    private const string ResultTypeName = "Nethermind.Evm.GasPolicy.SStorePricingResult";
    private const string AccessStatusTypeName = "Nethermind.Evm.GasPolicy.SStoreAccessStatus";
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static void ValidateRoots(IReadOnlyList<IMethodSymbol> roots)
    {
        if (roots.Count != 1)
        {
            throw new ExtractionException("SSTORE pricing Lean emission requires exactly one root.");
        }

        IMethodSymbol root = roots[0];
        if (root.ContainingType.ToDisplayString() != KernelTypeName ||
            root.Name != "Price" || !root.IsStatic || root.IsGenericMethod ||
            root.ReturnType.ToDisplayString() != ResultTypeName || root.Parameters.Length != 4 ||
            root.Parameters[0].Name != "input" || root.Parameters[0].Type.ToDisplayString() != InputTypeName ||
            root.Parameters[1].Name != "accessStatus" || root.Parameters[1].Type.ToDisplayString() != AccessStatusTypeName ||
            root.Parameters[2].Name != "accessSchedule" || root.Parameters[2].Type.ToDisplayString() != AccessScheduleTypeName ||
            root.Parameters[3].Name != "postAccessSchedule" || root.Parameters[3].Type.ToDisplayString() != PostAccessScheduleTypeName ||
            root.Parameters.Any(static parameter => parameter.RefKind != RefKind.None))
        {
            throw new ExtractionException("SSTORE pricing root does not have the pinned Price(input, access, schedules) signature.");
        }
    }

    public static SStorePricingKernelModel Normalize(
        SemanticModel semanticModel,
        IReadOnlyList<IMethodSymbol> roots,
        IReadOnlyList<IMethodSymbol> extractedMethods)
    {
        ValidateRoots(roots);
        IMethodSymbol price = roots[0];
        IMethodSymbol priceAfterAccess = FindMethod(extractedMethods, "PriceAfterAccess");
        HashSet<string> expectedMethods =
        [
            StateGasChargeExtractor.Display(price),
            StateGasChargeExtractor.Display(priceAfterAccess),
        ];
        if (extractedMethods.Count != 2 ||
            !expectedMethods.SetEquals(extractedMethods.Select(StateGasChargeExtractor.Display)))
        {
            throw new ExtractionException(
                "SSTORE pricing extraction did not retain exactly the pinned root graph: " +
                string.Join(", ", extractedMethods.Select(StateGasChargeExtractor.Display)));
        }

        ValidatePostAccessMethod(priceAfterAccess);
        IAssemblySymbol assembly = price.ContainingAssembly;
        IReadOnlyList<SStorePricingField> inputFields = NormalizeValueFields(
            semanticModel,
            FindType(assembly, InputTypeName),
            [
                ("OriginalIsZero", "bool"),
                ("CurrentIsZero", "bool"),
                ("NewIsZero", "bool"),
                ("CurrentSameAsOriginal", "bool"),
                ("NewSameAsCurrent", "bool"),
                ("NewSameAsOriginal", "bool"),
            ]);
        IReadOnlyList<SStorePricingField> accessScheduleFields = NormalizeValueFields(
            semanticModel,
            FindType(assembly, AccessScheduleTypeName),
            [("ColdStorageAccessGas", "ulong"), ("WarmAccessGas", "ulong")]);
        IReadOnlyList<SStorePricingField> postAccessScheduleFields = NormalizeValueFields(
            semanticModel,
            FindType(assembly, PostAccessScheduleTypeName),
            [
                ("StorageWriteGas", "ulong"),
                ("StorageClearRefund", "long"),
                ("StorageSetStateGas", "long"),
            ]);
        IReadOnlyList<SStorePricingField> postAccessResultFields = NormalizeValueFields(
            semanticModel,
            FindType(assembly, PostAccessResultTypeName),
            [
                ("ExecutionWriteGas", "ulong"),
                ("StorageClearRefund", "long"),
                ("StorageClearRefundReversal", "long"),
                ("RestoreOriginalRefund", "long"),
                ("StateGasCharge", "long"),
                ("StateGasRefund", "long"),
            ]);
        IReadOnlyList<SStorePricingField> resultFields = NormalizeValueFields(
            semanticModel,
            FindType(assembly, ResultTypeName),
            [("AccessGas", "ulong"), ("PostAccess", PostAccessResultTypeName)]);
        IReadOnlyList<string> statuses = NormalizeAccessStatuses(FindType(assembly, AccessStatusTypeName));
        ValidatePriceBody(price);
        SStorePricingKernelModel model = new(
            inputFields,
            accessScheduleFields,
            postAccessScheduleFields,
            postAccessResultFields,
            resultFields,
            statuses);
        ValidateModel(model);
        return model;
    }

    public static byte[] Emit(
        SStorePricingKernelModel model,
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
            -- Canonical SSTORE-pricing IR SHA-256: {{irHash}}

            namespace Eip803x.Generated.SStorePricingKernel

            def uint64Modulus : Nat := 2 ^ 64
            def uint64Max : Nat := uint64Modulus - 1
            def int64Modulus : Int := (uint64Modulus : Int)
            def int64SignBit : Int := 2 ^ 63
            def int64Min : Int := -int64SignBit
            def int64Max : Int := int64SignBit - 1

            def normalizeUInt64 (value : Nat) : Nat :=
              if value <= uint64Max then value else value % uint64Modulus

            def wrapInt64 (value : Int) : Int :=
              if int64Min <= value ∧ value <= int64Max then
                value
              else
                let residue := value % int64Modulus
                if residue < int64SignBit then residue else residue - int64Modulus

            def uint64ToInt64 (value : Nat) : Int :=
              wrapInt64 (value : Int)

            def negInt64 (value : Int) : Int :=
              wrapInt64 (-value)

            inductive AccessStatus where
              | cold
              | warm
              deriving DecidableEq, Repr

            structure Input where
              originalIsZero : Bool
              currentIsZero : Bool
              newIsZero : Bool
              currentSameAsOriginal : Bool
              newSameAsCurrent : Bool
              newSameAsOriginal : Bool
              deriving DecidableEq, Repr

            structure AccessSchedule where
              coldStorageAccessGas : Nat
              warmAccessGas : Nat
              deriving DecidableEq, Repr

            structure PostAccessSchedule where
              storageWriteGas : Nat
              storageClearRefund : Int
              storageSetStateGas : Int
              deriving DecidableEq, Repr

            structure PostAccessResult where
              executionWriteGas : Nat
              storageClearRefund : Int
              storageClearRefundReversal : Int
              restoreOriginalRefund : Int
              stateGasCharge : Int
              stateGasRefund : Int
              deriving DecidableEq, Repr

            structure Result where
              accessGas : Nat
              postAccess : PostAccessResult
              deriving DecidableEq, Repr

            def priceAfterAccessNormalized
                (input : Input)
                (schedule : PostAccessSchedule) : PostAccessResult :=
              let writesFirstValue := !input.newSameAsCurrent && input.currentSameAsOriginal
              let restoreOriginalRefund := if input.newSameAsOriginal && !input.newSameAsCurrent then
                uint64ToInt64 schedule.storageWriteGas
              else
                0
              { executionWriteGas := if writesFirstValue then schedule.storageWriteGas else 0
                storageClearRefund :=
                  if !input.originalIsZero && !input.currentIsZero && input.newIsZero then
                    schedule.storageClearRefund
                  else
                    0
                storageClearRefundReversal :=
                  if !input.originalIsZero && input.currentIsZero && !input.newIsZero then
                    negInt64 schedule.storageClearRefund
                  else
                    0
                restoreOriginalRefund := restoreOriginalRefund
                stateGasCharge :=
                  if input.originalIsZero && input.currentIsZero && !input.newIsZero then
                    schedule.storageSetStateGas
                  else
                    0
                stateGasRefund :=
                  if input.originalIsZero && !input.currentIsZero && input.newIsZero then
                    schedule.storageSetStateGas
                  else
                    0 }

            def priceAfterAccess (input : Input) (schedule : PostAccessSchedule) : PostAccessResult :=
              priceAfterAccessNormalized
                input
                { storageWriteGas := normalizeUInt64 schedule.storageWriteGas
                  storageClearRefund := wrapInt64 schedule.storageClearRefund
                  storageSetStateGas := wrapInt64 schedule.storageSetStateGas }

            def priceNormalized
                (input : Input)
                (accessStatus : AccessStatus)
                (accessSchedule : AccessSchedule)
                (postAccessSchedule : PostAccessSchedule) : Result :=
              let accessGas := if accessStatus == .cold then
                accessSchedule.coldStorageAccessGas
              else
                accessSchedule.warmAccessGas
              let postAccess := priceAfterAccess input postAccessSchedule
              { accessGas := accessGas, postAccess := postAccess }

            def price
                (input : Input)
                (accessStatus : AccessStatus)
                (accessSchedule : AccessSchedule)
                (postAccessSchedule : PostAccessSchedule) : Result :=
              priceNormalized
                input
                accessStatus
                { coldStorageAccessGas := normalizeUInt64 accessSchedule.coldStorageAccessGas
                  warmAccessGas := normalizeUInt64 accessSchedule.warmAccessGas }
                postAccessSchedule

            end Eip803x.Generated.SStorePricingKernel
            """;
        return Utf8WithoutBom.GetBytes(source.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    }

    private static IMethodSymbol FindMethod(IReadOnlyList<IMethodSymbol> methods, string name)
    {
        IMethodSymbol[] matches = methods.Where(method => method.Name == name).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new ExtractionException($"SSTORE pricing Lean emission requires exactly one '{name}' method.");
    }

    private static INamedTypeSymbol FindType(IAssemblySymbol assembly, string name) =>
        assembly.GetTypeByMetadataName(name)
        ?? throw new ExtractionException($"SSTORE pricing source does not declare '{name}'.");

    private static IReadOnlyList<SStorePricingField> NormalizeValueFields(
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
            typeDeclaration.ParameterList is null || typeDeclaration.Members.Any(static member => member is not FieldDeclarationSyntax))
        {
            throw new ExtractionException($"SSTORE pricing value type '{type}' does not have the pinned readonly-primary shape.");
        }

        IMethodSymbol constructor = constructors[0];
        SStorePricingField[] normalized = new SStorePricingField[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            VariableDeclaratorSyntax declaration = fields[i].DeclaringSyntaxReferences.Single().GetSyntax() as VariableDeclaratorSyntax
                ?? throw new ExtractionException($"SSTORE pricing field '{fields[i]}' is not a variable declaration.");
            if (declaration.Initializer?.Value is not IdentifierNameSyntax initializer ||
                semanticModel.GetSymbolInfo(initializer).Symbol is not IParameterSymbol parameter ||
                !SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, constructor) ||
                parameter.Ordinal != i || fields[i].Name != expected[i].Name ||
                fields[i].Type.ToDisplayString() != expected[i].Type || !fields[i].IsReadOnly ||
                fields[i].DeclaredAccessibility != Accessibility.Public ||
                !SymbolEqualityComparer.Default.Equals(fields[i].Type, constructor.Parameters[i].Type))
            {
                throw new ExtractionException($"SSTORE pricing value type '{type}' fields must project primary parameters in order.");
            }

            normalized[i] = new SStorePricingField(LowerFirst(fields[i].Name), LeanType(fields[i].Type.ToDisplayString()), i);
        }

        return normalized;
    }

    private static IReadOnlyList<string> NormalizeAccessStatuses(INamedTypeSymbol accessStatus)
    {
        IFieldSymbol[] fields = accessStatus.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(static field => field.HasConstantValue)
            .OrderBy(static field => field.ConstantValue)
            .ToArray();
        if (accessStatus.TypeKind != TypeKind.Enum || accessStatus.EnumUnderlyingType?.SpecialType != SpecialType.System_Byte ||
            !fields.Select(static field => field.Name).SequenceEqual(["Cold", "Warm"], StringComparer.Ordinal) ||
            !fields.Select(static field => Convert.ToByte(field.ConstantValue, System.Globalization.CultureInfo.InvariantCulture))
                .SequenceEqual([(byte)0, (byte)1]))
        {
            throw new ExtractionException("SSTORE pricing access status must retain the pinned Cold/Warm byte enum.");
        }

        return fields.Select(static field => LowerFirst(field.Name)).ToArray();
    }

    private static void ValidatePostAccessMethod(IMethodSymbol method)
    {
        if (method.ContainingType.ToDisplayString() != KernelTypeName || method.Name != "PriceAfterAccess" ||
            !method.IsStatic || method.IsGenericMethod || method.ReturnType.ToDisplayString() != PostAccessResultTypeName ||
            method.Parameters.Length != 2 || method.Parameters[0].Name != "input" ||
            method.Parameters[0].Type.ToDisplayString() != InputTypeName || method.Parameters[1].Name != "schedule" ||
            method.Parameters[1].Type.ToDisplayString() != PostAccessScheduleTypeName ||
            method.Parameters.Any(static parameter => parameter.RefKind != RefKind.None) ||
            method.DeclaringSyntaxReferences.Single().GetSyntax() is not MethodDeclarationSyntax
            {
                Body.Statements:
                [
                    LocalDeclarationStatementSyntax writesFirstValue,
                    LocalDeclarationStatementSyntax restoreOriginalRefund,
                    ReturnStatementSyntax { Expression: not null } returnStatement,
                ],
            })
        {
            throw new ExtractionException("SSTORE PriceAfterAccess does not retain the pinned restricted signature and body.");
        }

        ValidateLocal(writesFirstValue, "writesFirstValue", "bool", "!input.NewSameAsCurrent&&input.CurrentSameAsOriginal");
        ValidateLocal(restoreOriginalRefund, "restoreOriginalRefund", "long",
            "input.NewSameAsOriginal&&!input.NewSameAsCurrent?unchecked((long)schedule.StorageWriteGas):0");
        if (WithoutTrivia(returnStatement.Expression) !=
            "newSStorePostAccessPricingResult(writesFirstValue?schedule.StorageWriteGas:0,!input.OriginalIsZero&&!input.CurrentIsZero&&input.NewIsZero?schedule.StorageClearRefund:0,!input.OriginalIsZero&&input.CurrentIsZero&&!input.NewIsZero?unchecked(-schedule.StorageClearRefund):0,restoreOriginalRefund,input.OriginalIsZero&&input.CurrentIsZero&&!input.NewIsZero?schedule.StorageSetStateGas:0,input.OriginalIsZero&&!input.CurrentIsZero&&input.NewIsZero?schedule.StorageSetStateGas:0)")
        {
            throw new ExtractionException("SSTORE PriceAfterAccess no longer has the pinned EIP-8038 component formula.");
        }
    }

    private static void ValidatePriceBody(IMethodSymbol root)
    {
        if (root.DeclaringSyntaxReferences.Single().GetSyntax() is not MethodDeclarationSyntax
            {
                Body.Statements:
                [
                    LocalDeclarationStatementSyntax accessGas,
                    LocalDeclarationStatementSyntax postAccess,
                    ReturnStatementSyntax { Expression: not null } returnStatement,
                ],
            })
        {
            throw new ExtractionException("SSTORE Price must retain its pinned access-composition statements.");
        }

        ValidateLocal(accessGas, "accessGas", "ulong",
            "accessStatus==SStoreAccessStatus.Cold?accessSchedule.ColdStorageAccessGas:accessSchedule.WarmAccessGas");
        ValidateLocal(postAccess, "postAccess", "SStorePostAccessPricingResult",
            "PriceAfterAccess(input,postAccessSchedule)");
        if (WithoutTrivia(returnStatement.Expression) != "newSStorePricingResult(accessGas,postAccess)")
        {
            throw new ExtractionException("SSTORE Price no longer has the pinned access-composition result.");
        }
    }

    private static void ValidateLocal(LocalDeclarationStatementSyntax statement, string name, string type, string initializer)
    {
        if (statement.Declaration.Type.ToString() != type ||
            statement.Declaration.Variables is not [{ Identifier.ValueText: var actualName, Initializer.Value: ExpressionSyntax actualInitializer }] ||
            actualName != name || WithoutTrivia(actualInitializer) != initializer)
        {
            throw new ExtractionException(
                $"SSTORE pricing local '{name}' no longer has the pinned value formula: " +
                $"'{WithoutTrivia(statement.Declaration.Variables[0].Initializer!.Value)}'.");
        }
    }

    private static string WithoutTrivia(SyntaxNode syntax) =>
        string.Concat(syntax.WithoutTrivia().ToFullString().Where(static character => !char.IsWhiteSpace(character)));

    private static void ValidateModel(SStorePricingKernelModel model)
    {
        if (!FieldsMatch(model.InputFields,
                [("originalIsZero", "Bool"), ("currentIsZero", "Bool"), ("newIsZero", "Bool"),
                 ("currentSameAsOriginal", "Bool"), ("newSameAsCurrent", "Bool"), ("newSameAsOriginal", "Bool")]) ||
            !FieldsMatch(model.AccessScheduleFields,
                [("coldStorageAccessGas", "Nat"), ("warmAccessGas", "Nat")]) ||
            !FieldsMatch(model.PostAccessScheduleFields,
                [("storageWriteGas", "Nat"), ("storageClearRefund", "Int"), ("storageSetStateGas", "Int")]) ||
            !FieldsMatch(model.PostAccessResultFields,
                [("executionWriteGas", "Nat"), ("storageClearRefund", "Int"),
                 ("storageClearRefundReversal", "Int"), ("restoreOriginalRefund", "Int"),
                 ("stateGasCharge", "Int"), ("stateGasRefund", "Int")]) ||
            !FieldsMatch(model.ResultFields, [("accessGas", "Nat"), ("postAccess", "PostAccessResult")]) ||
            !model.AccessStatuses.SequenceEqual(["cold", "warm"], StringComparer.Ordinal))
        {
            throw new ExtractionException("SSTORE pricing Lean model does not match the pinned canonical shape.");
        }
    }

    private static bool FieldsMatch(
        IReadOnlyList<SStorePricingField> actual,
        IReadOnlyList<(string Name, string Type)> expected) =>
        actual.Select(static field => (field.Name, field.Type)).SequenceEqual(expected);

    private static string LowerFirst(string value) => char.ToLowerInvariant(value[0]) + value[1..];

    private static string LeanType(string type) => type switch
    {
        "bool" => "Bool",
        "ulong" => "Nat",
        "long" => "Int",
        AccessStatusTypeName => "AccessStatus",
        PostAccessResultTypeName => "PostAccessResult",
        _ => throw new ExtractionException($"SSTORE pricing type '{type}' is not Lean-representable."),
    };
}
