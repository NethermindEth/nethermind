// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.Extractor;

internal sealed record ExtendedStackDecoderAdapterShape(
    string ImmediateReader,
    string SingleDecoder,
    string PairDecoder,
    IReadOnlyList<string> AdapterSteps,
    bool MissingImmediateReadsZero,
    bool KernelDecodePrecedesValidityGate,
    bool InvalidImmediateDoesNotAdvanceProgramCounter,
    bool ValidImmediateAdvancesProgramCounter,
    bool SingleDepthMapsToDupAndOneBasedSwap,
    bool PairPositionsMapToOneBasedExchange,
    bool DecoderInvocationsBindToTopLevelKernel);

internal static class ExtendedStackDecoderAdapterValidator
{
    private const string ReaderMethodName = "ReadEip8024ImmediateOrZero";
    private const string SingleDecoderMethodName = "TryDecodeSingle";
    private const string PairDecoderMethodName = "TryDecodePair";
    private const string DupInstructionMethodName = "InstructionDupN";
    private const string SwapInstructionMethodName = "InstructionSwapN";
    private const string ExchangeInstructionMethodName = "InstructionExchange";

    public static ExtendedStackDecoderAdapterShape Validate(
        CSharpCompilation compilation,
        SyntaxTree adapterSyntaxTree,
        string kernelSourcePath)
    {
        SemanticModel semanticModel = compilation.GetSemanticModel(adapterSyntaxTree, ignoreAccessibility: false);
        CompilationUnitSyntax root = adapterSyntaxTree.GetCompilationUnitRoot();
        MethodDeclarationSyntax reader = FindUniqueMethod(root, ReaderMethodName);
        MethodDeclarationSyntax single = FindUniqueMethod(root, SingleDecoderMethodName);
        MethodDeclarationSyntax pair = FindUniqueMethod(root, PairDecoderMethodName);
        MethodDeclarationSyntax dup = FindUniqueMethod(root, DupInstructionMethodName);
        MethodDeclarationSyntax swap = FindUniqueMethod(root, SwapInstructionMethodName);
        MethodDeclarationSyntax exchange = FindUniqueMethod(root, ExchangeInstructionMethodName);
        IMethodSymbol pinnedSingle = FindPinnedKernelMethod(
            compilation,
            kernelSourcePath,
            "DecodeSingle",
            "Nethermind.Evm.ExtendedStackSingleDecode");
        IMethodSymbol pinnedPair = FindPinnedKernelMethod(
            compilation,
            kernelSourcePath,
            "DecodePair",
            "Nethermind.Evm.ExtendedStackPairDecode");

        ValidateReader(reader);
        ValidateSingleDecoder(semanticModel, single, pinnedSingle);
        ValidatePairDecoder(semanticModel, pair, pinnedPair);
        ValidateInstruction(
            dup,
            "!TryDecodeSingle(refstack,refprogramCounter,outintdepth)?EvmExceptionType.BadInstruction:stack.Dup<TTracingInst,OnFlag>(depth)");
        ValidateInstruction(
            swap,
            "!TryDecodeSingle(refstack,refprogramCounter,outintdepth)?EvmExceptionType.BadInstruction:stack.Swap<TTracingInst,OnFlag>(depth+1)");
        ValidateInstruction(
            exchange,
            "!TryDecodePair(refstack,refprogramCounter,outintn,outintm)?EvmExceptionType.BadInstruction:stack.Exchange<TTracingInst>(n,m)");

        return new ExtendedStackDecoderAdapterShape(
            ReaderMethodName,
            SingleDecoderMethodName,
            PairDecoderMethodName,
            [
                "read immediate or zero",
                "decode through the pure kernel",
                "project depth or one-based positions",
                "reject an invalid immediate without advancing PC",
                "advance PC after a valid immediate",
            ],
            MissingImmediateReadsZero: true,
            KernelDecodePrecedesValidityGate: true,
            InvalidImmediateDoesNotAdvanceProgramCounter: true,
            ValidImmediateAdvancesProgramCounter: true,
            SingleDepthMapsToDupAndOneBasedSwap: true,
            PairPositionsMapToOneBasedExchange: true,
            DecoderInvocationsBindToTopLevelKernel: true);
    }

    private static MethodDeclarationSyntax FindUniqueMethod(CompilationUnitSyntax root, string methodName)
    {
        MethodDeclarationSyntax[] methods = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == methodName)
            .ToArray();
        return methods.Length == 1
            ? methods[0]
            : throw new ExtractionException($"Extended-stack adapter must contain exactly one '{methodName}' method.");
    }

    private static void ValidateReader(MethodDeclarationSyntax method)
    {
        ValidatePrivateStaticMethod(method, "byte", 3);
        ValidateParameter(method.ParameterList.Parameters[0], "code", "byte", "ref");
        ValidateParameter(method.ParameterList.Parameters[1], "codeLength", "nint");
        ValidateParameter(method.ParameterList.Parameters[2], "programCounter", "nint");
        if (method.Body is not null || method.ExpressionBody is null ||
            WithoutTrivia(method.ExpressionBody.Expression) !=
            "programCounter<codeLength?Unsafe.Add(refcode,programCounter):(byte)0")
        {
            throw new ExtractionException(
                "Extended-stack adapter must read a bounded immediate and zero-extend a missing byte.");
        }
    }

    private static void ValidateSingleDecoder(
        SemanticModel semanticModel,
        MethodDeclarationSyntax method,
        IMethodSymbol pinnedKernelMethod)
    {
        ValidatePrivateStaticMethod(method, "bool", 3);
        ValidateParameter(method.ParameterList.Parameters[0], "stack", "EvmStack", "ref");
        ValidateParameter(method.ParameterList.Parameters[1], "programCounter", "nint", "ref");
        ValidateParameter(method.ParameterList.Parameters[2], "depth", "int", "out");
        if (method.Body is not
            {
                Statements:
                [
                    LocalDeclarationStatementSyntax immediate,
                    LocalDeclarationStatementSyntax decoded,
                    ExpressionStatementSyntax projection,
                    IfStatementSyntax invalid,
                    ExpressionStatementSyntax advance,
                    ReturnStatementSyntax { Expression: LiteralExpressionSyntax result },
                ],
            })
        {
            throw new ExtractionException("Extended-stack single adapter must retain its six ordered steps.");
        }

        ValidateLocal(
            immediate,
            "byte",
            "imm",
            "ReadEip8024ImmediateOrZero(refstack.Code,stack.CodeLength,programCounter)");
        ExpressionSyntax decoderInvocation = ValidateLocal(
            decoded,
            "ExtendedStackSingleDecode",
            "decoded",
            "ExtendedStackDecoderKernel.DecodeSingle(imm)");
        ValidateInvocationTarget(semanticModel, decoderInvocation, pinnedKernelMethod, "DecodeSingle");
        ValidateExpression(projection.Expression, "depth=decoded.Depth", "single depth projection");
        ValidateInvalidGate(invalid, "!decoded.IsValid");
        ValidateExpression(advance.Expression, "programCounter++", "single PC advance");
        if (!result.IsKind(SyntaxKind.TrueLiteralExpression))
        {
            throw new ExtractionException("Extended-stack single adapter must return true after its valid PC advance.");
        }
    }

    private static void ValidatePairDecoder(
        SemanticModel semanticModel,
        MethodDeclarationSyntax method,
        IMethodSymbol pinnedKernelMethod)
    {
        ValidatePrivateStaticMethod(method, "bool", 4);
        ValidateParameter(method.ParameterList.Parameters[0], "stack", "EvmStack", "ref");
        ValidateParameter(method.ParameterList.Parameters[1], "programCounter", "nint", "ref");
        ValidateParameter(method.ParameterList.Parameters[2], "n", "int", "out");
        ValidateParameter(method.ParameterList.Parameters[3], "m", "int", "out");
        if (method.Body is not
            {
                Statements:
                [
                    LocalDeclarationStatementSyntax immediate,
                    LocalDeclarationStatementSyntax decoded,
                    ExpressionStatementSyntax firstProjection,
                    ExpressionStatementSyntax secondProjection,
                    IfStatementSyntax invalid,
                    ExpressionStatementSyntax advance,
                    ReturnStatementSyntax { Expression: LiteralExpressionSyntax result },
                ],
            })
        {
            throw new ExtractionException("Extended-stack pair adapter must retain its seven ordered steps.");
        }

        ValidateLocal(
            immediate,
            "byte",
            "imm",
            "ReadEip8024ImmediateOrZero(refstack.Code,stack.CodeLength,programCounter)");
        ExpressionSyntax decoderInvocation = ValidateLocal(
            decoded,
            "ExtendedStackPairDecode",
            "decoded",
            "ExtendedStackDecoderKernel.DecodePair(imm)");
        ValidateInvocationTarget(semanticModel, decoderInvocation, pinnedKernelMethod, "DecodePair");
        ValidateExpression(firstProjection.Expression, "n=decoded.FirstPosition", "first pair position projection");
        ValidateExpression(secondProjection.Expression, "m=decoded.SecondPosition", "second pair position projection");
        ValidateInvalidGate(invalid, "!decoded.IsValid");
        ValidateExpression(advance.Expression, "programCounter++", "pair PC advance");
        if (!result.IsKind(SyntaxKind.TrueLiteralExpression))
        {
            throw new ExtractionException("Extended-stack pair adapter must return true after its valid PC advance.");
        }
    }

    private static void ValidateInstruction(MethodDeclarationSyntax method, string expectedReturn)
    {
        ValidateHotPathAttributes(method, requiresSkipLocalsInit: true);
        if (!method.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.PublicKeyword)) ||
            !method.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.StaticKeyword)) ||
            Canonical(method.ReturnType) != "EvmExceptionType" || method.Body is not
            {
                Statements:
                [
                    IfStatementSyntax outOfGas,
                    ReturnStatementSyntax { Expression: not null } result,
                ],
            })
        {
            throw new ExtractionException(
                $"Extended-stack instruction adapter '{method.Identifier.ValueText}' does not retain its pinned gas/decode shape.");
        }

        if (WithoutTrivia(outOfGas.Condition) != "!TGasPolicy.UpdateGas<VeryLowGasCost>(refgas)" ||
            outOfGas.Else is not null || outOfGas.Statement is not ReturnStatementSyntax
            {
                Expression: MemberAccessExpressionSyntax exception,
            } ||
            WithoutTrivia(exception) != "EvmExceptionType.OutOfGas" ||
            WithoutTrivia(result.Expression!) != expectedReturn)
        {
            throw new ExtractionException(
                $"Extended-stack instruction adapter '{method.Identifier.ValueText}' must retain its gas gate and decoder mapping.");
        }
    }

    private static void ValidatePrivateStaticMethod(MethodDeclarationSyntax method, string returnType, int parameterCount)
    {
        ValidateHotPathAttributes(method, requiresSkipLocalsInit: false);
        if (!method.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.PrivateKeyword)) ||
            !method.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.StaticKeyword)) ||
            Canonical(method.ReturnType) != returnType || method.TypeParameterList is not null ||
            method.ParameterList.Parameters.Count != parameterCount)
        {
            throw new ExtractionException(
                $"Extended-stack adapter method '{method.Identifier.ValueText}' does not have the pinned private signature.");
        }
    }

    private static void ValidateInvalidGate(IfStatementSyntax statement, string condition)
    {
        if (WithoutTrivia(statement.Condition) != condition || statement.Else is not null ||
            statement.Statement is not ReturnStatementSyntax { Expression: LiteralExpressionSyntax result } ||
            !result.IsKind(SyntaxKind.FalseLiteralExpression))
        {
            throw new ExtractionException("Extended-stack adapter must reject an invalid immediate before its PC advance.");
        }
    }

    private static ExpressionSyntax ValidateLocal(StatementSyntax statement, string type, string name, string initializer)
    {
        if (statement is not LocalDeclarationStatementSyntax local ||
            Canonical(local.Declaration.Type) != type ||
            local.Declaration.Variables is not [{ Identifier.ValueText: var actualName, Initializer.Value: ExpressionSyntax actualInitializer }] ||
            actualName != name || WithoutTrivia(actualInitializer) != initializer)
        {
            throw new ExtractionException($"Extended-stack adapter local '{name}' does not retain its pinned derived-value shape.");
        }

        return actualInitializer;
    }

    private static IMethodSymbol FindPinnedKernelMethod(
        CSharpCompilation compilation,
        string kernelSourcePath,
        string methodName,
        string returnType)
    {
        const string KernelTypeName = "Nethermind.Evm.ExtendedStackDecoderKernel";
        INamedTypeSymbol kernel = compilation.GetTypeByMetadataName(KernelTypeName)
            ?? throw new ExtractionException($"Extended-stack adapter could not bind the pinned kernel type '{KernelTypeName}'.");
        if (kernel.DeclaringSyntaxReferences is not [SyntaxReference declaration] ||
            !string.Equals(
                Path.GetFullPath(declaration.SyntaxTree.FilePath),
                Path.GetFullPath(kernelSourcePath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ExtractionException(
                "Extended-stack adapter kernel type must resolve to the top-level pinned decoder source.");
        }

        IMethodSymbol[] methods = kernel.GetMembers(methodName)
            .OfType<IMethodSymbol>()
            .Where(method => method.MethodKind == MethodKind.Ordinary && method.IsStatic &&
                             method.ReturnType.ToDisplayString() == returnType &&
                             method.Parameters is [{ RefKind: RefKind.None, Type.SpecialType: SpecialType.System_Byte }])
            .ToArray();
        return methods.Length == 1 &&
               methods[0].DeclaringSyntaxReferences is [SyntaxReference methodDeclaration] &&
               string.Equals(
                   Path.GetFullPath(methodDeclaration.SyntaxTree.FilePath),
                   Path.GetFullPath(kernelSourcePath),
                   StringComparison.OrdinalIgnoreCase)
            ? methods[0]
            : throw new ExtractionException(
                $"Extended-stack adapter could not bind the pinned top-level kernel method '{methodName}(byte)'.");
    }

    private static void ValidateInvocationTarget(
        SemanticModel semanticModel,
        ExpressionSyntax expression,
        IMethodSymbol expected,
        string methodName)
    {
        if (expression is not InvocationExpressionSyntax invocation ||
            semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol actual ||
            !SymbolEqualityComparer.Default.Equals(actual.OriginalDefinition, expected.OriginalDefinition))
        {
            throw new ExtractionException(
                $"Extended-stack adapter '{methodName}' invocation must bind to the pinned top-level decoder kernel method.");
        }
    }

    private static void ValidateParameter(ParameterSyntax parameter, string name, string type, string modifiers = "")
    {
        string actualModifiers = string.Concat(parameter.Modifiers.Select(static modifier => modifier.Kind() switch
        {
            SyntaxKind.RefKeyword => "ref",
            SyntaxKind.OutKeyword => "out",
            SyntaxKind.InKeyword => "in",
            SyntaxKind.ReadOnlyKeyword => "readonly",
            _ => modifier.Text,
        }));
        if (parameter.Identifier.ValueText != name || parameter.Type is null ||
            Canonical(parameter.Type) != type || actualModifiers != modifiers)
        {
            throw new ExtractionException($"Extended-stack adapter parameter '{name}' does not retain its pinned shape.");
        }
    }

    private static void ValidateExpression(ExpressionSyntax expression, string expected, string description)
    {
        if (WithoutTrivia(expression) != expected)
        {
            throw new ExtractionException($"Extended-stack adapter {description} does not retain its pinned expression.");
        }
    }

    private static void ValidateHotPathAttributes(MethodDeclarationSyntax method, bool requiresSkipLocalsInit)
    {
        string[] expected = requiresSkipLocalsInit
            ? ["SkipLocalsInit", "MethodImpl(MethodImplOptions.AggressiveInlining)"]
            : ["MethodImpl(MethodImplOptions.AggressiveInlining)"];
        string[] actual = method.AttributeLists.SelectMany(static list => list.Attributes)
            .Select(WithoutTrivia)
            .ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new ExtractionException(
                $"Extended-stack adapter method '{method.Identifier.ValueText}' does not retain its pinned hot-path attributes.");
        }
    }

    private static string Canonical(SyntaxNode node) =>
        string.Concat(node.WithoutTrivia().ToFullString().Where(static character => !char.IsWhiteSpace(character)));

    private static string WithoutTrivia(SyntaxNode node) => Canonical(node);
}
