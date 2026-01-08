// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Nethermind.Evm.SourceGenerators;

[Generator(LanguageNames.CSharp)]
public sealed class StackPushBytesGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor InvalidUsage = new(
        id: "NMSTACKPUSH001",
        title: "Invalid GenerateStackPushBytes usage",
        messageFormat: "{0}",
        category: "Nethermind.Evm.SourceGenerators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly SymbolDisplayFormat TypeFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private readonly struct Candidate(IMethodSymbol method, int size, byte padDir)
    {
        public IMethodSymbol Method { get; } = method;
        public int Size { get; } = size;
        public byte PadDir { get; } = padDir;
    }

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Inject the attribute into the compilation.
        context.RegisterPostInitializationOutput(static ctx =>
        {
            ctx.AddSource("GenerateStackPushBytesAttribute.g.cs", SourceText.From(AttributeSource, Encoding.UTF8));
        });

        // Find partial method declarations that have any attributes.
        IncrementalValuesProvider<Candidate?> candidateOptions = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: static (node, _) =>
                {
                    if (node is not MethodDeclarationSyntax m)
                        return false;

                    // Only stubs (no body) so we do not generate for already-implemented methods.
                    if (m.Body is not null || m.ExpressionBody is not null)
                        return false;

                    if (!m.Modifiers.Any(SyntaxKind.PartialKeyword))
                        return false;

                    return m.AttributeLists.Count != 0;
                },
                transform: static (ctx, _) =>
                {
                    var methodSyntax = (MethodDeclarationSyntax)ctx.Node;
                    var method = ctx.SemanticModel.GetDeclaredSymbol(methodSyntax) as IMethodSymbol;
                    if (method is null)
                        return default(Candidate?);

                    // Avoid generating if an implementation already exists.
                    if (method.PartialImplementationPart is not null)
                        return default;

                    // Locate our attribute by simple name (namespace-agnostic).
                    AttributeData? attr = null;
                    foreach (AttributeData a in method.GetAttributes())
                    {
                        var name = a.AttributeClass?.Name;
                        if (name is null)
                            continue;

                        if (name.Contains("GenerateStackPushBytes"))
                        {
                            attr = a;
                            break;
                        }
                    }

                    if (attr is null)
                        return default;

                    if (attr.ConstructorArguments.Length == 0)
                        return default;

                    if (attr.ConstructorArguments[0].Value is not int size)
                        return default;

                    byte pad = 1; // default PadDirection.Left

                    if (attr.ConstructorArguments.Length >= 2)
                    {
                        // Enum comes through as its underlying integral value.
                        var v = attr.ConstructorArguments[1].Value;
                        pad = v switch
                        {
                            byte b => b,
                            sbyte sb => unchecked((byte)sb),
                            short s => unchecked((byte)s),
                            ushort us => unchecked((byte)us),
                            int i => unchecked((byte)i),
                            uint ui => unchecked((byte)ui),
                            long l => unchecked((byte)l),
                            ulong ul => unchecked((byte)ul),
                            _ => (byte)1
                        };
                    }

                    return new Candidate(method, size, pad);
                });

        IncrementalValuesProvider<Candidate> candidates = candidateOptions
            .Where(static c => c.HasValue)
            .Select(static (c, _) => c!.Value);

        // Group by containing type and emit one file per type.
        IncrementalValueProvider<ImmutableArray<Candidate>> collected = candidates.Collect();

        context.RegisterSourceOutput(collected, static (spc, items) =>
        {
            if (items.IsDefaultOrEmpty)
                return;

            foreach (IGrouping<ISymbol?, Candidate>? group in items.GroupBy(static x => x.Method.ContainingType, SymbolEqualityComparer.Default))
            {
                ISymbol? type = group.Key;
                Candidate[] methods = group.ToArray();

                var src = EmitForType(spc, type as INamedTypeSymbol, methods);
                if (src is null)
                    continue;

                var hintName = $"{GetSafeHintName(type)}.StackPushBytes.g.cs";
                spc.AddSource(hintName, src);
            }
        });
    }

    private static string? EmitForType(SourceProductionContext spc, INamedTypeSymbol? type, Candidate[] methods)
    {
        // Basic validation
        if (type is null or { TypeKind: not (TypeKind.Class or TypeKind.Struct) })
        {
            spc.ReportDiagnostic(Diagnostic.Create(InvalidUsage, Location.None,
                $"GenerateStackPushBytes can only be used in partial class/struct types. Found: {type?.ToDisplayString(TypeFormat)}"));
            return null;
        }

        var ns = type.ContainingNamespace is { IsGlobalNamespace: false }
            ? type.ContainingNamespace.ToDisplayString()
            : null;

        var sb = new StringBuilder(16 * 1024);

        sb.AppendBlock(0, """
// <auto-generated/>

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Nethermind.Core;
using Nethermind.Evm.Tracing;

""");

        if (ns is not null)
        {
            sb.AppendLine($$"""namespace {{ns}};""");
            sb.AppendLine();
        }

        // Emit nested type chain.
        INamedTypeSymbol[] typeChain = GetTypeChain(type);
        for (int i = 0; i < typeChain.Length; i++)
        {
            INamedTypeSymbol t = typeChain[i];
            sb.Indent(i);

            sb.Append(GetAccessibilityText(t.DeclaredAccessibility)).Append(' ');
            if (t.IsReadOnly)
                sb.Append("readonly ");
            sb.Append("partial ").Append(GetTypeKindText(t)).Append(' ');
            sb.Append(GetTypeNameWithTypeParams(t));

            List<string> typeConstraints = GetTypeParameterConstraints(t);
            if (typeConstraints.Count != 0)
            {
                sb.AppendLine();
                foreach (var c in typeConstraints)
                {
                    sb.Indent(i + 1).Append(c).AppendLine();
                }
            }
            else
            {
                sb.AppendLine();
            }

            sb.Indent(i).AppendLine("{");
        }

        int indent = typeChain.Length;

        // Emit each method implementation.
        foreach (Candidate c in methods.OrderBy(m => m.Method.Name, StringComparer.Ordinal))
        {
            EmitOneMethod(spc, sb, indent, c);
        }

        // Close braces.
        for (int i = typeChain.Length - 1; i >= 0; i--)
        {
            sb.Indent(i).AppendLine("}");
        }

        return sb.ToString();
    }

    private static void EmitOneMethod(SourceProductionContext spc, StringBuilder sb, int indent, Candidate c)
    {
        IMethodSymbol method = c.Method;
        int size = c.Size;
        byte pad = c.PadDir;

        if ((uint)(size - 1) > 31u)
        {
            spc.ReportDiagnostic(Diagnostic.Create(InvalidUsage, method.Locations.FirstOrDefault(),
                $"Size must be 1..32. Got {size} for {method.ToDisplayString(TypeFormat)}"));
            return;
        }

        if (method.ReturnsVoid)
        {
            spc.ReportDiagnostic(Diagnostic.Create(InvalidUsage, method.Locations.FirstOrDefault(),
                $"Method must not return void: {method.ToDisplayString(TypeFormat)}"));
            return;
        }

        if (!method.IsPartialDefinition || method.PartialImplementationPart is not null)
            return;

        // Require at least 1 parameter (the bytes).
        if (method.Parameters.Length != 1)
        {
            spc.ReportDiagnostic(Diagnostic.Create(InvalidUsage, method.Locations.FirstOrDefault(),
                $"Method must have exactly 1 parameter (byte or ref byte). Found {method.Parameters.Length}: {method.ToDisplayString(TypeFormat)}"));
            return;
        }

        IParameterSymbol p0 = method.Parameters[0];
        bool isByte = p0.Type.SpecialType == SpecialType.System_Byte;
        bool isRefByte = isByte && p0.RefKind == RefKind.Ref;
        bool isByteValue = isByte && p0.RefKind == RefKind.None;

        if (!isRefByte && !isByteValue)
        {
            spc.ReportDiagnostic(Diagnostic.Create(InvalidUsage, method.Locations.FirstOrDefault(),
                $"Parameter must be 'byte' (only valid for size=1) or 'ref byte'. Found: {p0.ToDisplayString(TypeFormat)}"));
            return;
        }

        if (isByteValue && size != 1)
        {
            spc.ReportDiagnostic(Diagnostic.Create(InvalidUsage, method.Locations.FirstOrDefault(),
                $"By-value 'byte' parameter is only valid for size=1. Got size={size}: {method.ToDisplayString(TypeFormat)}"));
            return;
        }

        // Must have a tracing generic like TTracingInst (optional, but your examples do).
        string? tracingTpName = null;
        if (method.TypeParameters.Length != 0)
        {
            // Prefer TTracingInst if present.
            tracingTpName = method.TypeParameters.FirstOrDefault(tp => tp.Name == "TTracingInst")?.Name
                            ?? method.TypeParameters[0].Name;
        }

        // Attributes
        sb.Indent(indent).AppendLine("[MethodImpl(MethodImplOptions.AggressiveInlining)]");

        // Signature
        sb.Indent(indent);
        sb.Append(GetAccessibilityText(method.DeclaredAccessibility));

        if (method.IsStatic) sb.Append(" static");
        //if (method.IsUnsafe) sb.Append(" unsafe");
        if (method.IsReadOnly)
            sb.Append(" readonly");
        sb.Append(" partial EvmExceptionType ").Append(method.Name);
        sb.Append(GetMethodTypeParams(method));
        sb.Append('(').Append(GetParameterList(method)).Append(')');

        List<string> constraints = GetMethodTypeParameterConstraints(method);
        if (constraints.Count != 0)
        {
            sb.AppendLine();
            foreach (var cons in constraints)
                sb.Indent(indent + 1).Append(cons).AppendLine();
        }
        else
        {
            sb.AppendLine();
        }

        sb.Indent(indent).Append("{");

        // Body
        var valueName = p0.Name;

        // Tracing
        if (tracingTpName is not null)
        {
            string traceLine = isByteValue
                ? $"_tracer.ReportStackPush({valueName});"
                : $"_tracer.TraceBytes(in {valueName}, {size});";

            sb.AppendBlock(indent + 1, $$"""
if ({{tracingTpName}}.IsActive)
{
    {{traceLine}}
}
""");
        }

        sb.AppendBlock(indent + 1, """
ref Vector256<byte> head = ref Unsafe.As<byte, Vector256<byte>>(ref PushBytesNullableRef());
if (Unsafe.IsNullRef(ref head)) goto StackOverflow;
""");

        // Emit the actual word build.
        if (size == 32)
        {
            // full 32 bytes, no padding
            sb.Indent(indent + 1).AppendLine($$"""head = Unsafe.ReadUnaligned<Vector256<byte>>(ref {{valueName}});""");
        }
        else
        {
            if (pad == (byte)1) // PadDirection.Left: payload right-aligned (EVM PUSH)
            {
                EmitPadLeftBody(sb, indent + 1, size, isByteValue, valueName);
            }
            else // PadDirection.Right: payload left-aligned
            {
                EmitPadRightBody(sb, indent + 1, size, isByteValue, valueName);
            }
        }

        sb.AppendBlock(indent + 1, """
return EvmExceptionType.None;
""");

        sb.Indent(indent).AppendLine("StackOverflow:");
        sb.Indent(indent + 1).AppendLine("return EvmExceptionType.StackOverflow;");
        sb.Indent(indent).AppendLine("}");
        sb.AppendLine();
    }

    private static void EmitPadLeftBody(StringBuilder sb, int indent, int size, bool isByteValue, string valueName)
    {
        // Special-case <= 8: build only lane3 and call CreateWordFromUInt64.
        if (size <= 8)
        {
            if (isByteValue)
            {
                // size must be 1
                sb.Indent(indent).AppendLine($$"""ulong lane3 = (ulong){{valueName}} << 56;""");
            }
            else
            {
                var lane3Expr = EmitPackHiU64Expr(valueName, size);
                sb.Indent(indent).AppendLine($$"""ulong lane3 = {{lane3Expr}};""");
            }

            sb.AppendBlock(indent, """
if (Vector256.IsHardwareAccelerated)
{
    head = CreateWordFromUInt64(lane3);
}
else
{
    ref Vector128<ulong> head128 = ref Unsafe.As<Vector256<byte>, Vector128<ulong>>(ref head);
    head128 = default;
    Unsafe.Add(ref head128, 1) = Vector128.Create(0UL, lane3);
}
""");

            return;
        }

        // Special-case 16: use Vector128 -> Vector256.Create(default, src)
        if (size == 16)
        {
            sb.Indent(indent).AppendLine($$"""Vector128<byte> src = Unsafe.ReadUnaligned<Vector128<byte>>(ref {{valueName}});""");

            sb.AppendBlock(indent, """
if (Vector256.IsHardwareAccelerated)
{
    head = Vector256.Create(default, src);
}
else
{
    ref Vector128<byte> head128 = ref Unsafe.As<Vector256<byte>, Vector128<byte>>(ref head);
    head128 = default;
    Unsafe.Add(ref head128, 1) = src;
}
""");

            return;
        }

        int q = size >> 3;
        int r = size & 7;

        // lanes are ulong-lanes 0..3 (8 bytes each). payload is right-aligned.
        string lane0 = "0UL", lane1 = "0UL", lane2 = "0UL", lane3 = "0UL";

        if (r == 0)
        {
            // q full lanes placed at the end.
            int firstLane = 4 - q; // 1..3
            for (int i = 0; i < q; i++)
            {
                int laneIndex = firstLane + i;
                int srcOff = i * 8;
                var load = EmitLoadU64(valueName, srcOff);
                (lane0, lane1, lane2, lane3) = AssignLane(lane0, lane1, lane2, lane3, laneIndex, load);
            }
        }
        else
        {
            // partial lane then q full lanes
            int partialLaneIndex = 3 - q;
            var partialExpr = EmitPackHiU64Expr(valueName, r);
            (lane0, lane1, lane2, lane3) = AssignLane(lane0, lane1, lane2, lane3, partialLaneIndex, partialExpr);

            for (int i = 0; i < q; i++)
            {
                int laneIndex = partialLaneIndex + 1 + i;
                int srcOff = r + (i * 8);
                var load = EmitLoadU64(valueName, srcOff);
                (lane0, lane1, lane2, lane3) = AssignLane(lane0, lane1, lane2, lane3, laneIndex, load);
            }
        }

        sb.AppendBlock(indent, $$"""
if (Vector256.IsHardwareAccelerated)
{
    head = Vector256.Create(
        {{lane0}},
        {{lane1}},
        {{lane2}},
        {{lane3}}
    ).AsByte();
}
else
{
    ref Vector128<ulong> head128 = ref Unsafe.As<Vector256<byte>, Vector128<ulong>>(ref head);
""");

        if (lane0 == "0UL" && lane1 == "0UL")
        {
            sb.AppendBlock(indent + 1, """
head128 = default;
""");
        }
        else
        {
            sb.AppendBlock(indent + 1, $$"""
head128 = Vector128.Create(
    {{lane0}},
    {{lane1}}
);
""");
        }

        sb.AppendBlock(indent + 1, $$"""
Unsafe.Add(ref head128, 1) = Vector128.Create(
    {{lane2}},
    {{lane3}}
);
""");

        sb.Indent(indent).AppendLine("}");
    }

    private static void EmitPadRightBody(StringBuilder sb, int indent, int size, bool isByteValue, string valueName)
    {
        // payload left-aligned
        if (size == 16)
        {
            sb.AppendBlock(indent, $$"""
Vector128<byte> src = Unsafe.ReadUnaligned<Vector128<byte>>(ref {{valueName}});
head = Vector256.Create(src, default);
""");
            return;
        }

        int q = size >> 3;
        int r = size & 7;

        string lane0 = "0UL", lane1 = "0UL", lane2 = "0UL", lane3 = "0UL";

        // full lanes first
        for (int i = 0; i < q; i++)
        {
            int laneIndex = i;
            int srcOff = i * 8;
            var load = EmitLoadU64(valueName, srcOff);
            (lane0, lane1, lane2, lane3) = AssignLane(lane0, lane1, lane2, lane3, laneIndex, load);
        }

        // remainder in low bytes of lane q
        if (r != 0)
        {
            int tailOff = q * 8;

            // Build low-byte partial without a runtime switch.
            // Easiest: PackHi then shift down by (8 - r) * 8 (constant).
            if (isByteValue)
            {
                // only possible for size==1
                (lane0, lane1, lane2, lane3) = AssignLane(lane0, lane1, lane2, lane3, 0, "(ulong)" + valueName);
            }
            else
            {
                var hi = EmitPackHiU64ExprAtOffset(valueName, tailOff, r);
                var shift = (8 - r) * 8;
                var lo = shift == 0 ? hi : $"({hi} >> {shift})";
                (lane0, lane1, lane2, lane3) = AssignLane(lane0, lane1, lane2, lane3, q, lo);
            }
        }

        sb.AppendBlock(indent, $$"""
head = Vector256.Create(
    {{lane0}},
    {{lane1}},
    {{lane2}},
    {{lane3}}
).AsByte();
""");
    }

    private static string EmitLoadU64(string valueName, int offsetBytes)
    {
        if (offsetBytes == 0)
        {
            return $"Unsafe.ReadUnaligned<ulong>(ref {valueName})";
        }

        return "Unsafe.ReadUnaligned<ulong>("
                 + "ref Unsafe.Add(ref " + valueName + ", " + offsetBytes + "))";
    }

    private static string EmitPackHiU64Expr(string valueName, int r)
    {
        // r is 1..7, pack src[0..r-1] into high end of lane.
        return EmitPackHiU64ExprAtOffset(valueName, 0, r);
    }

    private static string EmitPackHiU64ExprAtOffset(string valueName, int baseOff, int r)
    {
        // use the same patterns as PackHiU64, but emit only the needed case (no jump table)
        string B(int off) => $"Unsafe.Add(ref {valueName}, {baseOff + off})";
        string RefB(int off) => $"ref {(off > 0 ? B(off) : valueName)}";

        string ReadU16(int off) => $"Unsafe.ReadUnaligned<ushort>({RefB(off)})";
        string ReadU32(int off) => $"Unsafe.ReadUnaligned<uint>({RefB(off)})";
        string ReadU64(int off) => $"Unsafe.ReadUnaligned<ulong>({RefB(off)})";

        return r switch
        {
            1 => $"((ulong){B(0)}) << 56",
            2 => $"(ulong){ReadU16(0)} << 48",
            3 => $"((ulong){ReadU16(0)} << 40) | ((ulong){B(2)} << 56)",
            4 => $"(ulong){ReadU32(0)} << 32",
            5 => $"((ulong){ReadU32(0)} << 24) | ((ulong){B(4)} << 56)",
            6 => $"((ulong){ReadU32(0)} << 16) | ((ulong){ReadU16(4)} << 48)",
            7 => $"((ulong){ReadU32(0)} << 8) | ((ulong){ReadU16(4)} << 40) | ((ulong){B(6)} << 56)",
            _ => $"{ReadU64(0)}"
        };
    }

    private static (string lane0, string lane1, string lane2, string lane3) AssignLane(
        string lane0, string lane1, string lane2, string lane3, int laneIndex, string expr)
    {
        return laneIndex switch
        {
            0 => (expr, lane1, lane2, lane3),
            1 => (lane0, expr, lane2, lane3),
            2 => (lane0, lane1, expr, lane3),
            3 => (lane0, lane1, lane2, expr),
            _ => (lane0, lane1, lane2, lane3)
        };
    }

    private static string GetSafeHintName(ISymbol? type)
    {
        // avoid weird characters in hint names
        var full = type?.ToDisplayString(TypeFormat) ?? string.Empty;
        var sb = new StringBuilder(full.Length);
        foreach (char ch in full)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '.')
                sb.Append(ch);
            else
                sb.Append('_');
        }
        return sb.ToString();
    }

    private static INamedTypeSymbol[] GetTypeChain(INamedTypeSymbol type)
    {
        var stack = new Stack<INamedTypeSymbol>();
        for (INamedTypeSymbol? t = type; t is not null; t = t.ContainingType)
            stack.Push(t);

        return stack.ToArray();
    }

    private static string GetAccessibilityText(Accessibility a)
    {
        return a switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            Accessibility.Private => "private",
            Accessibility.Protected => "protected",
            Accessibility.ProtectedAndInternal => "protected internal",
            Accessibility.ProtectedOrInternal => "protected internal",
            _ => "internal",
        };
    }

    private static string GetTypeKindText(INamedTypeSymbol t)
    {
        return t.TypeKind switch
        {
            TypeKind.Struct => "struct",
            _ => "class",
        };
    }

    private static string GetTypeNameWithTypeParams(INamedTypeSymbol t)
    {
        if (t.TypeParameters.Length == 0)
            return t.Name;

        return t.Name + "<" + string.Join(", ", t.TypeParameters.Select(tp => tp.Name)) + ">";
    }

    private static List<string> GetTypeParameterConstraints(INamedTypeSymbol t)
    {
        var list = new List<string>();
        foreach (ITypeParameterSymbol tp in t.TypeParameters)
        {
            var cons = BuildConstraintsClause(tp);
            if (cons is not null)
                list.Add(cons);
        }
        return list;

        static string? BuildConstraintsClause(ITypeParameterSymbol tp)
        {
            var parts = new List<string>();

            if (tp.HasNotNullConstraint) parts.Add("notnull");
            if (tp.HasReferenceTypeConstraint) parts.Add("class");
            if (tp.HasUnmanagedTypeConstraint) parts.Add("unmanaged");
            if (tp.HasValueTypeConstraint) parts.Add("struct");

            foreach (ITypeSymbol ct in tp.ConstraintTypes)
                parts.Add(ct.ToDisplayString(TypeFormat));

            if (tp.HasConstructorConstraint) parts.Add("new()");

            if (parts.Count == 0)
                return null;

            return $"where {tp.Name} : {string.Join(", ", parts)}";
        }
    }

    private static string GetMethodTypeParams(IMethodSymbol m)
    {
        if (m.TypeParameters.Length == 0)
            return string.Empty;

        return "<" + string.Join(", ", m.TypeParameters.Select(tp => tp.Name)) + ">";
    }

    private static string GetParameterList(IMethodSymbol m)
    {
        var parts = new List<string>(m.Parameters.Length);
        foreach (IParameterSymbol p in m.Parameters)
        {
            var sb = new StringBuilder();

            // scoped modifiers (C# 11+)
            if (p.ScopedKind != ScopedKind.None)
                sb.Append("scoped ");

            sb.Append(p.RefKind switch
            {
                RefKind.In => "in ",
                RefKind.Out => "out ",
                RefKind.Ref => "ref ",
                _ => string.Empty
            });

            sb.Append(p.Type.ToDisplayString(TypeFormat)).Append(' ').Append(p.Name);
            parts.Add(sb.ToString());
        }
        return string.Join(", ", parts);
    }

    private static List<string> GetMethodTypeParameterConstraints(IMethodSymbol m)
    {
        var list = new List<string>();
        foreach (ITypeParameterSymbol tp in m.TypeParameters)
        {
            var parts = new List<string>();

            if (tp.HasNotNullConstraint) parts.Add("notnull");
            if (tp.HasReferenceTypeConstraint) parts.Add("class");
            if (tp.HasUnmanagedTypeConstraint) parts.Add("unmanaged");
            if (tp.HasValueTypeConstraint) parts.Add("struct");

            foreach (ITypeSymbol ct in tp.ConstraintTypes)
                parts.Add(ct.ToDisplayString(TypeFormat));

            if (tp.HasConstructorConstraint) parts.Add("new()");

            if (parts.Count != 0)
                list.Add($"where {tp.Name} : {string.Join(", ", parts)}");
        }
        return list;
    }

    private static readonly string AttributeSource = """
// <auto-generated />
#nullable enable

using System;

namespace Nethermind.Evm.CodeAnalysis;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class GenerateStackPushBytesAttribute : Attribute
{
    public GenerateStackPushBytesAttribute(int size, PadDirection padDirection = PadDirection.Left)
    {
        Size = size;
        PadDirection = padDirection;
    }

    public int Size { get; }
    public PadDirection PadDirection { get; }
}
""";
}

internal static class StringBuilderExtension
{
    public static StringBuilder Indent(this StringBuilder sb, int count)
        => sb.Append(' ', count * 4);

    // Appends a multi-line block, prefixing each non-empty line with the given indent.
    // This keeps the generator source readable (raw strings) without baking indentation into the output.
    public static StringBuilder AppendBlock(this StringBuilder sb, int indent, string block)
    {
        int pos = 0;
        int end = block.Length;

        sb.AppendLine();
        while (pos < end)
        {
            int nl = block.IndexOf('\n', pos);
            bool hasNl = nl >= 0;

            int lineEnd = hasNl ? nl : end;
            int lineLen = lineEnd - pos;

            // Trim trailing '\r' (handles CRLF inputs)
            if (lineLen > 0 && block[pos + lineLen - 1] == '\r')
                lineLen--;

            if (lineLen != 0)
            {
                sb.Indent(indent);
                sb.Append(block, pos, lineLen);
            }

            if (!hasNl)
                break;

            sb.AppendLine();
            pos = nl + 1;
        }
        sb.AppendLine();

        return sb;
    }
}
