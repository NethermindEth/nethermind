// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.Extractor;

internal sealed record TransactionSettlementField(string Name, string Type, int ConstructorParameterOrdinal);

internal sealed record TransactionSettlementKernelModel(
    IReadOnlyList<TransactionSettlementField> ResultFields,
    IReadOnlyList<string> Operations);

internal static class TransactionSettlementLeanEmitter
{
    private const string KernelTypeName = "Nethermind.Evm.TransactionProcessing.TransactionSettlementKernel";
    private const string ResultTypeName = "Nethermind.Evm.TransactionProcessing.TransactionSettlementResult";
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    private const string CalculateTemplate = """
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TransactionSettlementResult Calculate(
            ulong transactionGasLimit,
            ulong preRefundGas,
            long refundCounter,
            int destroyCount,
            ulong destroyRefund,
            ulong codeInsertExecutionRefund,
            ulong calldataFloorGas,
            long stateGasUsed,
            ulong refundQuotient,
            bool isError,
            bool shouldRevert,
            bool isEip8037Enabled,
            bool isEip7778Enabled)
        {
            ulong gasUsedBeforeRefund = isError ? transactionGasLimit : preRefundGas;
            long totalToRefund = unchecked((long)codeInsertExecutionRefund);

            if (!isError && !shouldRevert)
            {
                long destroyRefundTotal = unchecked(destroyCount * (long)destroyRefund);
                totalToRefund = unchecked(totalToRefund + unchecked(refundCounter + destroyRefundTotal));
            }

            long refund = Math.Min(unchecked((long)(gasUsedBeforeRefund / refundQuotient)), totalToRefund);
            ulong operationGas = refund >= 0
                ? unchecked(gasUsedBeforeRefund - (ulong)refund)
                : unchecked(gasUsedBeforeRefund + (ulong)unchecked(-refund));
            ulong spentGas = Math.Max(operationGas, calldataFloorGas);

            ulong blockGas;
            ulong blockStateGas;
            if (isEip8037Enabled)
            {
                blockStateGas = unchecked((ulong)stateGasUsed);
                blockGas = Eip8037BlockGasInclusionCheck.CalculateBlockExecutionGas(
                    gasUsedBeforeRefund,
                    blockStateGas,
                    calldataFloorGas);
            }
            else
            {
                blockStateGas = 0;
                blockGas = isEip7778Enabled ? Math.Max(gasUsedBeforeRefund, calldataFloorGas) : 0;
            }

            return new TransactionSettlementResult(
                spentGas,
                operationGas,
                blockGas,
                blockStateGas,
                Math.Max(gasUsedBeforeRefund, calldataFloorGas),
                refund > 0 ? (ulong)refund : 0);
        }
        """;

    public static void ValidateRoots(IReadOnlyList<IMethodSymbol> roots)
    {
        if (roots is not [IMethodSymbol method] ||
            method.ContainingType.ToDisplayString() != KernelTypeName ||
            method.Name != "Calculate" ||
            method.ReturnType.ToDisplayString() != ResultTypeName ||
            !method.IsStatic ||
            !method.Parameters.Select(static parameter => parameter.Type.ToDisplayString()).SequenceEqual(
                ["ulong", "ulong", "long", "int", "ulong", "ulong", "ulong", "long", "ulong", "bool", "bool", "bool", "bool"],
                StringComparer.Ordinal))
        {
            throw new ExtractionException("Transaction settlement root does not match the pinned scalar signature.");
        }
    }

    public static TransactionSettlementKernelModel Normalize(
        SemanticModel semanticModel,
        IReadOnlyList<IMethodSymbol> roots,
        IReadOnlyList<IMethodSymbol> extractedMethods,
        string blockGasInclusionSourcePath,
        string uint64ExtensionsSourcePath)
    {
        ValidateRoots(roots);
        if (extractedMethods.Count != 1 ||
            !SymbolEqualityComparer.Default.Equals(extractedMethods[0], roots[0]))
        {
            throw new ExtractionException("Transaction settlement extraction retained an unpinned call-graph member.");
        }

        MethodDeclarationSyntax declaration = roots[0].DeclaringSyntaxReferences.Single().GetSyntax()
            as MethodDeclarationSyntax
            ?? throw new ExtractionException("Transaction settlement root is not a method declaration.");
        MethodDeclarationSyntax expected = SyntaxFactory.ParseMemberDeclaration(CalculateTemplate)
            as MethodDeclarationSyntax
            ?? throw new ExtractionException("Internal transaction settlement template is invalid.");
        if (!CanonicalTokens(declaration).SequenceEqual(CanonicalTokens(expected), StringComparer.Ordinal))
        {
            throw new ExtractionException("Transaction settlement Calculate body does not match the pinned extraction profile.");
        }

        INamedTypeSymbol resultType = roots[0].ContainingAssembly.GetTypeByMetadataName(ResultTypeName)
            ?? throw new ExtractionException($"Required result type '{ResultTypeName}' was not found.");
        IReadOnlyList<TransactionSettlementField> fields = NormalizeResultFields(semanticModel, resultType);
        ValidateBlockExecutionDependency(blockGasInclusionSourcePath);
        ValidateSaturatingSubDependency(uint64ExtensionsSourcePath);

        return new TransactionSettlementKernelModel(
            fields,
            [
                "select pre-refund gas from error status",
                "accumulate signed code, refund-counter, and destroy refunds with int64 wrap",
                "cap refund by unsigned quotient then apply signed refund with uint64 wrap",
                "apply calldata floor to paid and max-used gas",
                "project EIP-8037 execution/state dimensions with saturating subtraction",
                "preserve the pre-EIP-8037 EIP-7778 projection branch",
                "expose only a positive refund counter",
            ]);
    }

    public static byte[] Emit(
        TransactionSettlementKernelModel model,
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
            -- Canonical transaction-settlement IR SHA-256: {{irHash}}

            namespace Eip803x.Generated.TransactionSettlementKernel

            def uint64Modulus : Nat := 2 ^ 64
            def uint64Max : Nat := uint64Modulus - 1
            def int64Modulus : Int := (uint64Modulus : Int)
            def int64SignBit : Int := 2 ^ 63
            def int64Min : Int := -int64SignBit
            def int64Max : Int := int64SignBit - 1
            def int32Modulus : Int := 2 ^ 32
            def int32SignBit : Int := 2 ^ 31
            def int32Min : Int := -int32SignBit
            def int32Max : Int := int32SignBit - 1

            def normalizeUInt64 (value : Nat) : Nat :=
              if value <= uint64Max then value else value % uint64Modulus

            def wrapUInt64 (value : Int) : Nat :=
              if 0 <= value ∧ value <= (uint64Max : Int) then
                Int.toNat value
              else
                Int.toNat (value % (uint64Modulus : Int))

            def wrapInt64 (value : Int) : Int :=
              if int64Min <= value ∧ value <= int64Max then
                value
              else
                let residue := value % int64Modulus
                if residue < int64SignBit then residue else residue - int64Modulus

            def wrapInt32 (value : Int) : Int :=
              if int32Min <= value ∧ value <= int32Max then
                value
              else
                let residue := value % int32Modulus
                if residue < int32SignBit then residue else residue - int32Modulus

            def addUInt64 (left right : Nat) : Nat :=
              wrapUInt64 ((left : Int) + (right : Int))

            def subUInt64 (left right : Nat) : Nat :=
              wrapUInt64 ((left : Int) - (right : Int))

            def addInt64 (left right : Int) : Int :=
              wrapInt64 (left + right)

            def mulInt64 (left right : Int) : Int :=
              wrapInt64 (left * right)

            def negInt64 (value : Int) : Int :=
              wrapInt64 (-value)

            def int64ToUInt64 (value : Int) : Nat :=
              wrapUInt64 value

            def uint64ToInt64 (value : Nat) : Int :=
              wrapInt64 (value : Int)

            def saturatingSubUInt64 (left right : Nat) : Nat :=
              if left > right then subUInt64 left right else 0

            structure TransactionSettlementResult where
              spentGas : Nat
              operationGas : Nat
              blockGas : Nat
              blockStateGas : Nat
              maxUsedGas : Nat
              gasRefund : Nat
              deriving DecidableEq, Repr

            def calculateNormalized
                (transactionGasLimit preRefundGas : Nat)
                (refundCounter destroyCount : Int)
                (destroyRefund codeInsertExecutionRefund calldataFloorGas : Nat)
                (stateGasUsed : Int)
                (refundQuotient : Nat)
                (isError shouldRevert isEip8037Enabled isEip7778Enabled : Bool) :
                TransactionSettlementResult :=
              let gasUsedBeforeRefund := if isError then transactionGasLimit else preRefundGas
              let codeRefund := uint64ToInt64 codeInsertExecutionRefund
              let destroyRefundTotal := mulInt64 destroyCount (uint64ToInt64 destroyRefund)
              let totalToRefund :=
                if !isError && !shouldRevert then
                  addInt64 codeRefund (addInt64 refundCounter destroyRefundTotal)
                else
                  codeRefund
              let refund := min (uint64ToInt64 (gasUsedBeforeRefund / refundQuotient)) totalToRefund
              let operationGas :=
                if 0 <= refund then
                  subUInt64 gasUsedBeforeRefund (int64ToUInt64 refund)
                else
                  addUInt64 gasUsedBeforeRefund (int64ToUInt64 (negInt64 refund))
              let spentGas := max operationGas calldataFloorGas
              let blockStateGas := if isEip8037Enabled then int64ToUInt64 stateGasUsed else 0
              let blockGas :=
                if isEip8037Enabled then
                  max (saturatingSubUInt64 gasUsedBeforeRefund blockStateGas) calldataFloorGas
                else if isEip7778Enabled then
                  max gasUsedBeforeRefund calldataFloorGas
                else
                  0
              { spentGas
                operationGas
                blockGas
                blockStateGas
                maxUsedGas := max gasUsedBeforeRefund calldataFloorGas
                gasRefund := if 0 < refund then int64ToUInt64 refund else 0 }

            def calculate
                (transactionGasLimit preRefundGas : Nat)
                (refundCounter destroyCount : Int)
                (destroyRefund codeInsertExecutionRefund calldataFloorGas : Nat)
                (stateGasUsed : Int)
                (refundQuotient : Nat)
                (isError shouldRevert isEip8037Enabled isEip7778Enabled : Bool) :
                TransactionSettlementResult :=
              calculateNormalized
                (normalizeUInt64 transactionGasLimit)
                (normalizeUInt64 preRefundGas)
                (wrapInt64 refundCounter)
                (wrapInt32 destroyCount)
                (normalizeUInt64 destroyRefund)
                (normalizeUInt64 codeInsertExecutionRefund)
                (normalizeUInt64 calldataFloorGas)
                (wrapInt64 stateGasUsed)
                (normalizeUInt64 refundQuotient)
                isError shouldRevert isEip8037Enabled isEip7778Enabled

            end Eip803x.Generated.TransactionSettlementKernel
            """;
        return Utf8WithoutBom.GetBytes(source.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    }

    private static IReadOnlyList<TransactionSettlementField> NormalizeResultFields(
        SemanticModel semanticModel,
        INamedTypeSymbol resultType)
    {
        (string Name, string Type)[] expected =
        [
            ("SpentGas", "ulong"),
            ("OperationGas", "ulong"),
            ("BlockGas", "ulong"),
            ("BlockStateGas", "ulong"),
            ("MaxUsedGas", "ulong"),
            ("GasRefund", "ulong"),
        ];
        IFieldSymbol[] fields = resultType.GetMembers().OfType<IFieldSymbol>()
            .Where(static field => !field.IsStatic && !field.IsImplicitlyDeclared)
            .OrderBy(static field => field.Locations[0].SourceSpan.Start)
            .ToArray();
        IMethodSymbol[] constructors = resultType.InstanceConstructors
            .Where(static constructor => !constructor.IsImplicitlyDeclared)
            .ToArray();
        if (!resultType.IsReadOnly || fields.Length != expected.Length ||
            constructors is not [IMethodSymbol constructor] || constructor.Parameters.Length != expected.Length)
        {
            throw new ExtractionException("Transaction settlement result does not have the pinned readonly value shape.");
        }

        TransactionSettlementField[] normalized = new TransactionSettlementField[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            VariableDeclaratorSyntax declaration = fields[i].DeclaringSyntaxReferences.Single().GetSyntax()
                as VariableDeclaratorSyntax
                ?? throw new ExtractionException("Transaction settlement result field is not a variable declaration.");
            if (declaration.Initializer?.Value is not IdentifierNameSyntax initializer ||
                semanticModel.GetSymbolInfo(initializer).Symbol is not IParameterSymbol parameter ||
                !SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, constructor) ||
                parameter.Ordinal != i ||
                fields[i].Name != expected[i].Name || fields[i].Type.ToDisplayString() != expected[i].Type ||
                !fields[i].IsReadOnly || fields[i].DeclaredAccessibility != Accessibility.Public)
            {
                throw new ExtractionException(
                    "Transaction settlement result fields must project their corresponding primary-constructor parameters.");
            }

            normalized[i] = new TransactionSettlementField(char.ToLowerInvariant(fields[i].Name[0]) + fields[i].Name[1..], "Nat", i);
        }

        return normalized;
    }

    private static void ValidateBlockExecutionDependency(string sourcePath)
    {
        MethodDeclarationSyntax method = ParseSource(sourcePath).DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(static declaration => declaration.Identifier.ValueText == "CalculateBlockExecutionGas");
        const string Expected = """
            public static ulong CalculateBlockExecutionGas(ulong preRefundGas, ulong blockStateGas, ulong calldataFloor)
                => Math.Max(preRefundGas.SaturatingSub(blockStateGas), calldataFloor);
            """;
        MethodDeclarationSyntax expected = (MethodDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration(Expected)!;
        if (!CanonicalTokens(method).SequenceEqual(CanonicalTokens(expected), StringComparer.Ordinal))
        {
            throw new ExtractionException("Block execution projection dependency does not match the pinned definition.");
        }
    }

    private static void ValidateSaturatingSubDependency(string sourcePath)
    {
        MethodDeclarationSyntax method = ParseSource(sourcePath).DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(static declaration => declaration.Identifier.ValueText == "SaturatingSub" && declaration.ParameterList.Parameters.Count == 2);
        const string Expected = """
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static ulong SaturatingSub(this ulong a, ulong b) => a > b ? a - b : 0UL;
            """;
        MethodDeclarationSyntax expected = (MethodDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration(Expected)!;
        if (!CanonicalTokens(method).SequenceEqual(CanonicalTokens(expected), StringComparer.Ordinal))
        {
            throw new ExtractionException("UInt64 saturating subtraction dependency does not match the pinned definition.");
        }
    }

    private static CompilationUnitSyntax ParseSource(string path)
    {
        if (!File.Exists(path))
        {
            throw new ExtractionException($"Pinned supporting source was not found: {path}");
        }

        return CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetCompilationUnitRoot();
    }

    private static IEnumerable<string> CanonicalTokens(SyntaxNode node) =>
        node.DescendantTokens().Select(static token => token.Text);

    private static void ValidateModel(TransactionSettlementKernelModel model)
    {
        string[] expectedFields = ["spentGas", "operationGas", "blockGas", "blockStateGas", "maxUsedGas", "gasRefund"];
        if (model.ResultFields.Count != expectedFields.Length ||
            !model.ResultFields.Select(static field => field.Name).SequenceEqual(expectedFields, StringComparer.Ordinal) ||
            model.ResultFields.Where((field, index) => field.Type != "Nat" || field.ConstructorParameterOrdinal != index).Any() ||
            model.Operations.Count != 7)
        {
            throw new ExtractionException("Transaction settlement model does not match the pinned canonical shape.");
        }
    }
}
