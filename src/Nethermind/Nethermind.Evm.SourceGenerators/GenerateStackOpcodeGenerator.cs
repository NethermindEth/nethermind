// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Nethermind.Evm.SourceGenerators;

[Generator(LanguageNames.CSharp)]
public sealed class GenerateStackOpcodeGenerator : IIncrementalGenerator
{
    private const string AttributeMetadataName = "Nethermind.Evm.CodeAnalysis.GenerateStackOpcodeAttribute";

    private static readonly DiagnosticDescriptor MustBePartial = new(
        id: "NMSTACKSOG001",
        title: "GenerateStackOpcode requires a partial struct",
        messageFormat: "Type '{0}' must be declared partial to use [GenerateStackOpcode]",
        category: "Nethermind.Evm.SourceGenerators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidSize = new(
        id: "NMSTACKSOG002",
        title: "GenerateStackOpcode size must be between 1 and 32",
        messageFormat: "Type '{0}': size '{1}' is invalid. Expected a constant integer between 1 and 32.",
        category: "Nethermind.Evm.SourceGenerators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NestedTypeRequiresPartialContainers = new(
        id: "NMSTACKSOG003",
        title: "GenerateStackOpcode cannot emit into a non-partial containing type",
        messageFormat: "Type '{0}' is nested inside '{1}', but '{1}' is not partial. Make all containing types partial or move the opcode type to top-level.",
        category: "Nethermind.Evm.SourceGenerators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Inject the attribute into the compilation.
        context.RegisterPostInitializationOutput(static ctx =>
        {
            ctx.AddSource("GenerateStackOpcodeAttribute.g.cs", SourceText.From(AttributeSource, Encoding.UTF8));
        });

        IncrementalValuesProvider<Candidate> candidates = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeMetadataName,
                predicate: static (node, _) => node is StructDeclarationSyntax,
                transform: static (ctx, ct) => CreateCandidate(ctx, ct));

        context.RegisterSourceOutput(candidates.Collect(), static (spc, all) =>
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (Candidate cand in all)
            {
                if (cand.Diagnostic is not null)
                    spc.ReportDiagnostic(cand.Diagnostic);

                if (cand.Info is null)
                    continue;

                // Avoid double-emitting if the attribute shows up multiple times via partials.
                if (!seen.Add(cand.Info.HintName))
                    continue;

                Emit(spc, cand.Info);
            }
        });
    }

    private static Candidate CreateCandidate(GeneratorAttributeSyntaxContext ctx, CancellationToken _)
    {
        var structSymbol = (INamedTypeSymbol)ctx.TargetSymbol;
        var structDecl = (StructDeclarationSyntax)ctx.TargetNode;

        if (!structDecl.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            return new Candidate(
                info: null,
                diagnostic: Diagnostic.Create(
                    MustBePartial,
                    structDecl.Identifier.GetLocation(),
                    structSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }

        if (!TryGetSize(ctx, out var size, out var sizeDebug) || size is < 1 or > 32)
        {
            return new Candidate(
                info: null,
                diagnostic: Diagnostic.Create(
                    InvalidSize,
                    structDecl.Identifier.GetLocation(),
                    structSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    sizeDebug));
        }

        // If nested, we must be able to "re-open" each containing type via 'partial'.
        for (INamedTypeSymbol? t = structSymbol.ContainingType; t is not null; t = t.ContainingType)
        {
            if (!IsPartial(t))
            {
                return new Candidate(
                    info: null,
                    diagnostic: Diagnostic.Create(
                        NestedTypeRequiresPartialContainers,
                        structDecl.Identifier.GetLocation(),
                        structSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        t.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
            }
        }

        var ns = structSymbol.ContainingNamespace is null || structSymbol.ContainingNamespace.IsGlobalNamespace
            ? ""
            : structSymbol.ContainingNamespace.ToDisplayString();

        // Keep modifiers consistent with the annotated declaration.
        var modifiers = structDecl.Modifiers.ToFullString().Trim();

        var typeName = structDecl.Identifier.ValueText;
        var typeParams = structDecl.TypeParameterList?.ToFullString() ?? "";
        var constraints = structDecl.ConstraintClauses.Count == 0
            ? ""
            : " " + string.Join(" ", structDecl.ConstraintClauses.Select(static c => c.ToFullString().Trim()));

        // Intentionally do NOT repeat the base-list here.
        // This keeps the generated file independent of file-local usings.
        var typeHeader = $"{modifiers} struct {typeName}{typeParams}{constraints}";

        var hintName = MakeHintName(structSymbol, size);

        return new Candidate(
            info: new OpcodeInfo(ns, GetContainingTypes(structSymbol), typeHeader, size, hintName),
            diagnostic: null);
    }

    private static bool TryGetSize(GeneratorAttributeSyntaxContext ctx, out int size, out string debug)
    {
        size = 0;
        debug = "<missing>";

        if (ctx.Attributes.Length == 0)
            return false;

        AttributeData a = ctx.Attributes[0];
        if (a.ConstructorArguments.Length == 0)
            return false;

        TypedConstant arg = a.ConstructorArguments[0];
        debug = arg.Value?.ToString() ?? "<null>";

        if (arg.Value is int i)
        {
            size = i;
            debug = i.ToString();
            return true;
        }

        return false;
    }

    private static bool IsPartial(INamedTypeSymbol type)
    {
        foreach (SyntaxReference syntaxRef in type.DeclaringSyntaxReferences)
        {
            if (syntaxRef.GetSyntax() is TypeDeclarationSyntax tds &&
                tds.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                return true;
            }
        }
        return false;
    }

    private static ImmutableArray<ContainingTypeInfo> GetContainingTypes(INamedTypeSymbol symbol)
    {
        if (symbol.ContainingType is null)
            return ImmutableArray<ContainingTypeInfo>.Empty;

        var stack = new Stack<INamedTypeSymbol>();
        for (INamedTypeSymbol? t = symbol.ContainingType; t is not null; t = t.ContainingType)
            stack.Push(t);

        ImmutableArray<ContainingTypeInfo>.Builder builder = ImmutableArray.CreateBuilder<ContainingTypeInfo>(stack.Count);

        while (stack.Count != 0)
        {
            INamedTypeSymbol t = stack.Pop();

            var accessibility = t.DeclaredAccessibility switch
            {
                Accessibility.Public => "public ",
                Accessibility.Internal => "internal ",
                Accessibility.Private => "private ",
                Accessibility.Protected => "protected ",
                Accessibility.ProtectedAndInternal => "private protected ",
                Accessibility.ProtectedOrInternal => "protected internal ",
                _ => ""
            };

            var kind = t.TypeKind switch
            {
                TypeKind.Class => "class",
                TypeKind.Struct => "struct",
                TypeKind.Interface => "interface",
                _ => "class"
            };

            var typeParams = t.TypeParameters.Length == 0
                ? ""
                : "<" + string.Join(", ", t.TypeParameters.Select(static tp => tp.Name)) + ">";

            var constraintsSb = new StringBuilder();
            foreach (ITypeParameterSymbol tp in t.TypeParameters)
            {
                var clause = BuildConstraintClause(tp);
                if (clause.Length != 0)
                    constraintsSb.Append(' ').Append(clause);
            }

            builder.Add(new ContainingTypeInfo($"{accessibility}partial {kind} {t.Name}{typeParams}{constraintsSb}"));
        }

        return builder.ToImmutable();
    }

    private static string BuildConstraintClause(ITypeParameterSymbol tp)
    {
        var parts = new List<string>(4);

        if (tp.HasReferenceTypeConstraint)
            parts.Add("class");
        else if (tp.HasValueTypeConstraint)
            parts.Add("struct");

        if (tp.HasNotNullConstraint)
            parts.Add("notnull");

        if (tp.HasUnmanagedTypeConstraint)
            parts.Add("unmanaged");

        foreach (ITypeSymbol ct in tp.ConstraintTypes)
            parts.Add(ct.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));

        if (tp.HasConstructorConstraint)
            parts.Add("new()");

        if (parts.Count == 0)
            return "";

        return $"where {tp.Name} : {string.Join(", ", parts)}";
    }

    private static void Emit(SourceProductionContext context, OpcodeInfo info)
    {
        var sb = new StringBuilder(capacity: 1100);

        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using System.Runtime.InteropServices;");
        sb.AppendLine("using Nethermind.Core;");
        sb.AppendLine();

        var indent = 0;

        if (info.Namespace.Length != 0)
        {
            sb.Append("namespace ").Append(info.Namespace).AppendLine(";");
            sb.AppendLine();
        }

        foreach (ContainingTypeInfo ct in info.ContainingTypes)
        {
            AppendIndent(sb, indent);
            sb.AppendLine(ct.Header);
            AppendIndent(sb, indent);
            sb.AppendLine("{");
            indent++;
        }

        AppendIndent(sb, indent);
        sb.AppendLine("/// <summary>");
        AppendIndent(sb, indent);
        sb.Append("/// ").Append(info.Size).AppendLine(" item operations.");
        AppendIndent(sb, indent);
        sb.AppendLine("/// </summary>");

        AppendIndent(sb, indent);
        sb.AppendLine(info.TypeHeader);
        AppendIndent(sb, indent);
        sb.AppendLine("{");
        indent++;

        AppendIndent(sb, indent);
        sb.Append("const int Size = ").Append(info.Size).AppendLine(";");

        AppendIndent(sb, indent);
        sb.AppendLine("public static int Count => Size;");
        sb.AppendLine();

        AppendIndent(sb, indent);
        sb.AppendLine("[SkipLocalsInit]");
        AppendIndent(sb, indent);
        sb.AppendLine("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
        AppendIndent(sb, indent);
        sb.AppendLine("public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)");
        AppendIndent(sb, indent);
        sb.AppendLine("    where TTracingInst : struct, IFlag");
        AppendIndent(sb, indent);
        sb.AppendLine("{");
        indent++;

        AppendIndent(sb, indent);
        sb.AppendLine("int usedFromCode = Math.Min(code.Length - programCounter, length);");
        AppendIndent(sb, indent);
        sb.AppendLine("ref byte start = ref Unsafe.Add(ref MemoryMarshal.GetReference(code), programCounter);");
        AppendIndent(sb, indent);
        sb.AppendLine("if (usedFromCode == Size)");
        AppendIndent(sb, indent);
        sb.AppendLine("{");
        indent++;

        AppendIndent(sb, indent);
        sb.Append("// Direct push of a ").Append(info.Size).AppendLine("-byte value.");
        AppendIndent(sb, indent);
        sb.Append("return stack.Push").Append(info.Size).AppendLine("Bytes<TTracingInst>(ref start);");

        indent--;
        AppendIndent(sb, indent);
        sb.AppendLine("}");
        AppendIndent(sb, indent);
        sb.AppendLine("else");
        AppendIndent(sb, indent);
        sb.AppendLine("{");
        indent++;

        AppendIndent(sb, indent);
        sb.AppendLine("return stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);");

        indent--;
        AppendIndent(sb, indent);
        sb.AppendLine("}");

        indent--;
        AppendIndent(sb, indent);
        sb.AppendLine("}");

        indent--;
        AppendIndent(sb, indent);
        sb.AppendLine("}");

        for (var i = info.ContainingTypes.Length - 1; i >= 0; i--)
        {
            indent--;
            AppendIndent(sb, indent);
            sb.AppendLine("}");
        }

        context.AddSource(info.HintName, SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static void AppendIndent(StringBuilder sb, int indent)
    {
        for (var i = 0; i < indent; i++)
            sb.Append("    ");
    }

    private static string MakeHintName(INamedTypeSymbol symbol, int size)
    {
        var full = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        if (full.StartsWith("global::", StringComparison.Ordinal))
            full = full.Substring("global::".Length);

        full = full.Replace('<', '_')
                   .Replace('>', '_')
                   .Replace('.', '_');

        return $"{full}_StackOpcode_{size}.g.cs";
    }

    private static readonly string AttributeSource =
@"// <auto-generated />
#nullable enable

using System;

namespace Nethermind.Evm.CodeAnalysis;

[AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
internal sealed class GenerateStackOpcodeAttribute : Attribute
{
    public int Size { get; }
    public GenerateStackOpcodeAttribute(int size) => Size = size;
}
";

    private readonly struct Candidate(OpcodeInfo? info, Diagnostic? diagnostic)
    {
        public OpcodeInfo? Info { get; } = info;
        public Diagnostic? Diagnostic { get; } = diagnostic;
    }

    private sealed class OpcodeInfo(
        string Namespace,
        ImmutableArray<ContainingTypeInfo> ContainingTypes,
        string TypeHeader,
        int Size,
        string HintName)
    {

        public string Namespace { get; } = Namespace;
        public ImmutableArray<ContainingTypeInfo> ContainingTypes { get; } = ContainingTypes;
        public string TypeHeader { get; } = TypeHeader;
        public int Size { get; } = Size;
        public string HintName { get; } = HintName;
    }

    private readonly struct ContainingTypeInfo(string Header)
    {
        public string Header { get; } = Header;
    }
}
