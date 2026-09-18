// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Nethermind.Evm.Lean.Extractor;

internal sealed record IdentityPrecompileKernelModel(ulong BaseGas, ulong WordGas, uint WordSize);

internal sealed record IdentityPrecompileAdapterShape(
    uint Address,
    string Name,
    bool SupportsCaching,
    string BaseGasMethod,
    string DataGasMethod,
    string RunExpression);

internal static class IdentityPrecompileLeanEmitter
{
    private const string KernelTypeName = "Nethermind.Evm.Precompiles.IdentityPrecompileKernel";
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static void ValidateRoots(IReadOnlyList<IMethodSymbol> roots)
    {
        if (roots.Count != 2)
        {
            throw new ExtractionException("Identity-precompile Lean emission requires exactly two roots.");
        }

        IMethodSymbol baseGas = roots.SingleOrDefault(static root => root.Name == "BaseGasCost")
            ?? throw new ExtractionException("Identity-precompile kernel is missing BaseGasCost.");
        IMethodSymbol dataGas = roots.SingleOrDefault(static root => root.Name == "DataGasCost")
            ?? throw new ExtractionException("Identity-precompile kernel is missing DataGasCost.");
        if (!IsPinnedMethod(baseGas, SpecialType.System_UInt64, []) ||
            !IsPinnedMethod(dataGas, SpecialType.System_UInt64, [SpecialType.System_UInt32]) ||
            dataGas.Parameters[0].Name != "inputLength")
        {
            throw new ExtractionException("Identity-precompile kernel roots do not have the pinned signatures.");
        }
    }

    public static IdentityPrecompileKernelModel Normalize(
        IReadOnlyList<IMethodSymbol> roots,
        IReadOnlyList<IMethodSymbol> extractedMethods)
    {
        ValidateRoots(roots);
        foreach (SyntaxTree tree in roots.SelectMany(static root => root.DeclaringSyntaxReferences)
                     .Select(static reference => reference.SyntaxTree)
                     .Distinct())
        {
            RejectPreprocessorDirectives(tree, "Identity-precompile kernel");
        }

        string[] expected = roots.Select(StateGasChargeExtractor.Display).Order(StringComparer.Ordinal).ToArray();
        string[] actual = extractedMethods.Select(StateGasChargeExtractor.Display).Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new ExtractionException(
                "Identity-precompile extraction did not retain exactly the pinned pricing root graph.");
        }

        IMethodSymbol baseGas = roots.Single(static root => root.Name == "BaseGasCost");
        IMethodSymbol dataGas = roots.Single(static root => root.Name == "DataGasCost");
        string baseExpression = GetExpressionBody(baseGas);
        string dataExpression = GetExpressionBody(dataGas);
        if (baseExpression != "15UL")
        {
            throw new ExtractionException("Identity-precompile base gas expression is not the pinned constant.");
        }

        if (dataExpression != "3UL * (((ulong)inputLength + 31UL) >> 5)")
        {
            throw new ExtractionException("Identity-precompile data gas expression is not the pinned word formula.");
        }

        return new IdentityPrecompileKernelModel(BaseGas: 15, WordGas: 3, WordSize: 32);
    }

    public static byte[] Emit(
        IdentityPrecompileKernelModel model,
        string extractorVersion,
        string compilerVersion,
        string sourcePath,
        string sourceHash,
        string irHash)
    {
        if (model != new IdentityPrecompileKernelModel(15, 3, 32))
        {
            throw new ExtractionException("Identity-precompile normalized model is not the pinned program.");
        }

        string source = $$"""
            -- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
            -- SPDX-License-Identifier: LGPL-3.0-only

            -- This file is generated. Do not edit.
            -- Extractor version: {{extractorVersion}}
            -- Roslyn compiler version: {{compilerVersion}}
            -- Production source: {{sourcePath}}
            -- Production source SHA-256: {{sourceHash}}
            -- Canonical identity-precompile IR SHA-256: {{irHash}}

            namespace Eip803x.Generated.IdentityPrecompileKernel

            def uint32Modulus : Nat := 2 ^ 32
            def uint32Max : Nat := uint32Modulus - 1

            def normalizeUInt32 (value : Nat) : Nat :=
              if value <= uint32Max then value else value % uint32Modulus

            def baseGasCost : Nat := {{model.BaseGas}}

            def wordsForBytes (inputLength : Nat) : Nat :=
              (normalizeUInt32 inputLength + {{model.WordSize - 1}}) / {{model.WordSize}}

            def dataGasCost (inputLength : Nat) : Nat :=
              {{model.WordGas}} * wordsForBytes inputLength

            end Eip803x.Generated.IdentityPrecompileKernel
            """;
        return Utf8WithoutBom.GetBytes(source.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    }

    internal static void RejectPreprocessorDirectives(SyntaxTree tree, string sourceName)
    {
        if (tree.GetRoot().DescendantTrivia(descendIntoTrivia: true).Any(static trivia =>
                trivia.GetStructure() is DirectiveTriviaSyntax || trivia.IsKind(SyntaxKind.DisabledTextTrivia)))
        {
            throw new ExtractionException($"{sourceName} contains unsupported preprocessor or disabled text.");
        }
    }

    private static bool IsPinnedMethod(
        IMethodSymbol method,
        SpecialType returnType,
        IReadOnlyList<SpecialType> parameterTypes) =>
        method.ContainingType.ToDisplayString() == KernelTypeName &&
        method.IsStatic &&
        !method.IsGenericMethod &&
        method.DeclaredAccessibility == Accessibility.Public &&
        method.ReturnType.SpecialType == returnType &&
        method.Parameters.Select(static parameter => parameter.Type.SpecialType).SequenceEqual(parameterTypes) &&
        method.Parameters.All(static parameter => parameter.RefKind == RefKind.None);

    private static string GetExpressionBody(IMethodSymbol method)
    {
        if (method.DeclaringSyntaxReferences.Single().GetSyntax() is not MethodDeclarationSyntax
            {
                Body: null,
                ExpressionBody.Expression: ExpressionSyntax expression,
            })
        {
            throw new ExtractionException(
                $"Identity-precompile method '{method.Name}' must retain its pinned expression body.");
        }

        return expression.NormalizeWhitespace().ToFullString();
    }
}

internal static class IdentityPrecompileAdapterValidator
{
    private const string KernelTypeName = "Nethermind.Evm.Precompiles.IdentityPrecompileKernel";
    private const string AdapterPrelude = """
        using System;

        namespace Nethermind.Core
        {
            public readonly struct Address
            {
                public static Address FromNumber(int value) => default;
            }

            public readonly struct Result<T>
            {
                public static implicit operator Result<T>(T value) => default;
            }
        }

        namespace Nethermind.Core.Specs
        {
            public interface IReleaseSpec;
        }

        namespace Nethermind.Evm.Precompiles
        {
            using Nethermind.Core;
            using Nethermind.Core.Specs;

            public interface IPrecompile<T> where T : IPrecompile<T>
            {
                static abstract T Instance { get; }
                static abstract Address Address { get; }
                string Name { get; }
                bool SupportsCaching { get; }
                ulong BaseGasCost(IReleaseSpec releaseSpec);
                ulong DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec);
                Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec);
            }
        }
        """;

    public static IdentityPrecompileAdapterShape Validate(string path, string kernelPath)
    {
        byte[] source = File.ReadAllBytes(path);
        CSharpParseOptions parseOptions = CSharpParseOptions.Default
            .WithLanguageVersion(LanguageVersion.CSharp14)
            .WithDocumentationMode(DocumentationMode.Parse)
            .WithKind(SourceCodeKind.Regular);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            SourceText.From(source, source.Length, Encoding.UTF8, canBeEmbedded: true),
            parseOptions,
            path);
        byte[] kernelSource = File.ReadAllBytes(kernelPath);
        SyntaxTree kernelTree = CSharpSyntaxTree.ParseText(
            SourceText.From(kernelSource, kernelSource.Length, Encoding.UTF8, canBeEmbedded: true),
            parseOptions,
            kernelPath);
        SyntaxTree preludeTree = CSharpSyntaxTree.ParseText(AdapterPrelude, parseOptions, "<identity-precompile-adapter-prelude>");
        IdentityPrecompileLeanEmitter.RejectPreprocessorDirectives(tree, "Identity-precompile adapter");
        IdentityPrecompileLeanEmitter.RejectPreprocessorDirectives(kernelTree, "Identity-precompile kernel");
        Diagnostic[] errors = tree.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length != 0)
        {
            throw new ExtractionException(
                "Identity-precompile adapter did not parse without diagnostics: " +
                string.Join(Environment.NewLine, errors.Select(static diagnostic => diagnostic.ToString())));
        }

        CompilationUnitSyntax root = tree.GetCompilationUnitRoot();
        if (root.Usings.Select(static directive => directive.Name?.ToString()).ToArray() is not
            ["System", "Nethermind.Core", "Nethermind.Core.Specs"] ||
            root.Usings.Any(static directive =>
                directive.Alias is not null ||
                directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) ||
                directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)) ||
            root.Members.Count != 1 ||
            root.Members[0] is not FileScopedNamespaceDeclarationSyntax fileNamespace ||
            fileNamespace.Members.Count != 1)
        {
            throw new ExtractionException("Identity-precompile adapter imports or top-level declarations changed shape.");
        }

        ClassDeclarationSyntax declaration = fileNamespace.Members
            .OfType<ClassDeclarationSyntax>()
            .SingleOrDefault(static type => type.Identifier.ValueText == "IdentityPrecompile")
            ?? throw new ExtractionException("Identity-precompile adapter type was not found.");
        if (declaration.Ancestors().OfType<TypeDeclarationSyntax>().Any() ||
            ContainingNamespace(declaration) != "Nethermind.Evm.Precompiles" ||
            !declaration.Modifiers.Any(SyntaxKind.PublicKeyword) ||
            declaration.Modifiers.Any(SyntaxKind.PartialKeyword) ||
            declaration.Members.Count != 8 ||
            declaration.Members.OfType<PropertyDeclarationSyntax>().Count() != 4 ||
            declaration.Members.OfType<ConstructorDeclarationSyntax>().Count() != 1 ||
            declaration.Members.OfType<MethodDeclarationSyntax>().Count() != 3 ||
            declaration.BaseList?.Types.SingleOrDefault()?.Type.NormalizeWhitespace().ToFullString() !=
                "IPrecompile<IdentityPrecompile>")
        {
            throw new ExtractionException("Identity-precompile adapter type changed its pinned shape.");
        }

        RequirePropertyInitializer(declaration, "Instance", "IdentityPrecompile", "new()", isStatic: true);
        RequirePrivateEmptyConstructor(declaration);
        RequirePropertyInitializer(declaration, "Address", "Address", "Address.FromNumber(4)", isStatic: true);
        RequireExpressionProperty(declaration, "Name", "string", "\"ID\"");
        RequireExpressionProperty(declaration, "SupportsCaching", "bool", "false");
        RequireExpressionMethod(
            declaration,
            "BaseGasCost",
            "IdentityPrecompileKernel.BaseGasCost()",
            "ulong",
            [("releaseSpec", "IReleaseSpec")]);
        RequireExpressionMethod(
            declaration,
            "DataGasCost",
            "IdentityPrecompileKernel.DataGasCost((uint)inputData.Length)",
            "ulong",
            [("inputData", "ReadOnlyMemory<byte>"), ("releaseSpec", "IReleaseSpec")]);
        RequireExpressionMethod(
            declaration,
            "Run",
            "inputData.ToArray()",
            "Result<byte[]>",
            [("inputData", "ReadOnlyMemory<byte>"), ("releaseSpec", "IReleaseSpec")]);

        CSharpCompilation compilation = CSharpCompilation.Create(
            "Nethermind.Evm.Lean.IdentityPrecompileAdapter",
            [tree, kernelTree, preludeTree],
            StateGasChargeExtractor.GetPlatformReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                checkOverflow: false,
                allowUnsafe: false,
                nullableContextOptions: NullableContextOptions.Enable,
                deterministic: true));
        Diagnostic[] compilationErrors = compilation.GetDiagnostics()
            .Where(static diagnostic => !diagnostic.IsSuppressed && diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (compilationErrors.Length != 0)
        {
            throw new ExtractionException(
                "Identity-precompile adapter did not bind without diagnostics: " +
                string.Join(Environment.NewLine, compilationErrors.Select(static diagnostic => diagnostic.ToString())));
        }

        SemanticModel semanticModel = compilation.GetSemanticModel(tree);
        RequireInvocationTarget(semanticModel, declaration, "BaseGasCost", KernelTypeName, "BaseGasCost", kernelPath);
        RequireInvocationTarget(semanticModel, declaration, "DataGasCost", KernelTypeName, "DataGasCost", kernelPath);
        RequireInvocationTarget(
            semanticModel,
            declaration,
            "Run",
            "System.ReadOnlyMemory<byte>",
            "ToArray",
            expectedSourcePath: null);

        return new IdentityPrecompileAdapterShape(
            Address: 4,
            Name: "ID",
            SupportsCaching: false,
            BaseGasMethod: "IdentityPrecompileKernel.BaseGasCost",
            DataGasMethod: "IdentityPrecompileKernel.DataGasCost",
            RunExpression: "inputData.ToArray()");
    }

    private static void RequireInvocationTarget(
        SemanticModel semanticModel,
        ClassDeclarationSyntax declaration,
        string adapterMethod,
        string expectedContainingType,
        string expectedTargetMethod,
        string? expectedSourcePath)
    {
        MethodDeclarationSyntax method = declaration.Members.OfType<MethodDeclarationSyntax>()
            .Single(member => member.Identifier.ValueText == adapterMethod);
        InvocationExpressionSyntax invocation = method.DescendantNodes().OfType<InvocationExpressionSyntax>().SingleOrDefault()
            ?? throw new ExtractionException(
                $"Identity-precompile adapter method '{adapterMethod}' must contain exactly one invocation.");
        IMethodSymbol target = (semanticModel.GetOperation(invocation) as IInvocationOperation)?.TargetMethod
            ?? throw new ExtractionException(
                $"Identity-precompile adapter method '{adapterMethod}' invocation did not bind.");
        if (target.Name != expectedTargetMethod || target.ContainingType.ToDisplayString() != expectedContainingType)
        {
            throw new ExtractionException(
                $"Identity-precompile adapter method '{adapterMethod}' did not bind to the pinned target.");
        }

        if (expectedSourcePath is not null &&
            !target.DeclaringSyntaxReferences.Any(reference => string.Equals(
                Path.GetFullPath(reference.SyntaxTree.FilePath),
                Path.GetFullPath(expectedSourcePath),
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new ExtractionException(
                $"Identity-precompile adapter method '{adapterMethod}' target is not declared by the extracted kernel source.");
        }
    }

    private static void RequirePropertyInitializer(
        ClassDeclarationSyntax declaration,
        string name,
        string expectedType,
        string expected,
        bool isStatic)
    {
        PropertyDeclarationSyntax property = declaration.Members.OfType<PropertyDeclarationSyntax>()
            .SingleOrDefault(member => member.Identifier.ValueText == name)
            ?? throw new ExtractionException($"Identity-precompile adapter property '{name}' was not found.");
        string actual = property.Initializer?.Value.NormalizeWhitespace().ToFullString()
            ?? throw new ExtractionException($"Identity-precompile adapter property '{name}' has no initializer.");
        if (actual != expected || property.ExpressionBody is not null ||
            property.Type.NormalizeWhitespace().ToFullString() != expectedType ||
            property.Modifiers.Any(SyntaxKind.StaticKeyword) != isStatic ||
            !property.Modifiers.Any(SyntaxKind.PublicKeyword) ||
            property.AccessorList?.Accessors is not [{ Keyword.RawKind: (int)SyntaxKind.GetKeyword }])
        {
            throw new ExtractionException($"Identity-precompile adapter property '{name}' changed shape.");
        }
    }

    private static void RequireExpressionProperty(
        ClassDeclarationSyntax declaration,
        string name,
        string expectedType,
        string expected)
    {
        PropertyDeclarationSyntax property = declaration.Members.OfType<PropertyDeclarationSyntax>()
            .SingleOrDefault(member => member.Identifier.ValueText == name)
            ?? throw new ExtractionException($"Identity-precompile adapter property '{name}' was not found.");
        string actual = property.ExpressionBody?.Expression.NormalizeWhitespace().ToFullString()
            ?? throw new ExtractionException($"Identity-precompile adapter property '{name}' is not expression-bodied.");
        if (actual != expected || property.Type.NormalizeWhitespace().ToFullString() != expectedType ||
            property.Modifiers.Any(SyntaxKind.StaticKeyword) ||
            !property.Modifiers.Any(SyntaxKind.PublicKeyword))
        {
            throw new ExtractionException($"Identity-precompile adapter property '{name}' changed shape.");
        }
    }

    private static void RequireExpressionMethod(
        ClassDeclarationSyntax declaration,
        string name,
        string expected,
        string expectedReturnType,
        IReadOnlyList<(string Name, string Type)> parameters)
    {
        MethodDeclarationSyntax method = declaration.Members.OfType<MethodDeclarationSyntax>()
            .SingleOrDefault(member => member.Identifier.ValueText == name)
            ?? throw new ExtractionException($"Identity-precompile adapter method '{name}' was not found.");
        string actual = method.ExpressionBody?.Expression.NormalizeWhitespace().ToFullString()
            ?? throw new ExtractionException($"Identity-precompile adapter method '{name}' is not expression-bodied.");
        (string Name, string Type)[] actualParameters = method.ParameterList.Parameters
            .Select(static parameter => (
                parameter.Identifier.ValueText,
                parameter.Type?.NormalizeWhitespace().ToFullString() ?? string.Empty))
            .ToArray();
        if (actual != expected ||
            method.ReturnType.NormalizeWhitespace().ToFullString() != expectedReturnType ||
            method.Modifiers.Any(SyntaxKind.StaticKeyword) ||
            !method.Modifiers.Any(SyntaxKind.PublicKeyword) ||
            !actualParameters.SequenceEqual(parameters))
        {
            throw new ExtractionException($"Identity-precompile adapter method '{name}' changed shape.");
        }
    }

    private static void RequirePrivateEmptyConstructor(ClassDeclarationSyntax declaration)
    {
        ConstructorDeclarationSyntax constructor = declaration.Members.OfType<ConstructorDeclarationSyntax>().SingleOrDefault()
            ?? throw new ExtractionException("Identity-precompile adapter constructor was not found.");
        if (!constructor.Modifiers.Any(SyntaxKind.PrivateKeyword) ||
            constructor.ParameterList.Parameters.Count != 0 ||
            constructor.Body?.Statements.Count != 0)
        {
            throw new ExtractionException("Identity-precompile adapter constructor changed shape.");
        }
    }

    private static string ContainingNamespace(SyntaxNode declaration) =>
        string.Join(
            ".",
            declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
                .Reverse()
                .Select(static syntax => syntax.Name.ToString()));
}
