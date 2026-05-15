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

namespace Nethermind.Analyzers;

/// <summary>
/// Emits the implementation of <c>Nethermind.Specs.ChainSpecStyle.HardforkLabels.BuildAll</c>
/// from the <c>Nethermind.Specs.Forks.*</c> <c>NamedReleaseSpec</c> subclasses, making Forks/*.cs
/// the single source of truth for per-fork EIP membership.
/// </summary>
/// <remarks>
/// <para>
/// For every concrete subclass of <c>NamedReleaseSpec&lt;TSelf&gt;</c> the generator scans the
/// <c>Apply</c> method body for <c>spec.IsEip{N}Enabled = literal;</c> assignments, extracts the
/// parent fork from the primary-constructor base argument
/// (<c>NamedReleaseSpec&lt;Cancun&gt;(Shanghai.Instance)</c> → parent <c>Shanghai</c>), and emits a
/// <c>Block</c> or <c>Time</c> registration. The hand-written <c>HardforkLabels.cs</c> declares
/// <c>partial class HardforkLabels</c> with a partial <c>BuildAll</c> method; this generator
/// provides the implementation.
/// </para>
/// </remarks>
[Generator]
public sealed class HardforkLabelsGenerator : IIncrementalGenerator
{
    private const string NamedReleaseSpecBaseName = "NamedReleaseSpec";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<ForkInfo?> forks = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                transform: static (ctx, _) => TryExtractFork(ctx))
            .Where(static f => f is not null);

        IncrementalValueProvider<(Compilation Compilation, ImmutableArray<ForkInfo?> Forks)> combined =
            context.CompilationProvider.Combine(forks.Collect());

        context.RegisterSourceOutput(combined, static (spc, payload) =>
        {
            ImmutableArray<ForkInfo> all = payload.Forks.Where(f => f is not null).Select(f => f!).ToImmutableArray();
            if (all.IsEmpty) return;

            // Only emit labels for forks that have a corresponding label property on
            // ChainSpecParamsJson. This auto-skips Olympic/Frontier/Dao/MuirGlacier/ArrowGlacier/
            // GrayGlacier/Paris/BPO* without a hard-coded denylist — those forks contribute to the
            // runtime EIP state via the Apply chain but aren't user-facing chainspec shortcuts.
            INamedTypeSymbol? paramsJson = payload.Compilation.GetTypeByMetadataName(
                "Nethermind.Specs.ChainSpecStyle.Json.ChainSpecParamsJson");
            HashSet<string> labelProperties = paramsJson is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(
                    paramsJson.GetMembers().OfType<IPropertySymbol>().Select(p => p.Name),
                    StringComparer.Ordinal);

            // The Apply body only records this fork's delta. Walk the parent chain to materialize
            // cumulative state for IsPostMerge (set on Paris, inherited by all descendants).
            Dictionary<string, ForkInfo> byName = all.ToDictionary(f => f.Name);
            foreach (ForkInfo fork in all)
            {
                ForkInfo? cursor = fork;
                while (cursor is not null)
                {
                    if (cursor.SetsIsPostMerge) { fork.IsPostMerge = true; break; }
                    cursor = cursor.ParentName is { } pn && byName.TryGetValue(pn, out ForkInfo p) ? p : null;
                }
            }

            spc.AddSource("HardforkLabels.g.cs", SourceText.From(Emit(all, labelProperties), Encoding.UTF8));
        });
    }

    private static ForkInfo? TryExtractFork(GeneratorSyntaxContext ctx)
    {
        ClassDeclarationSyntax cls = (ClassDeclarationSyntax)ctx.Node;
        if (ctx.SemanticModel.GetDeclaredSymbol(cls) is not INamedTypeSymbol symbol) return null;
        if (symbol.IsAbstract) return null;

        // Immediate base must be NamedReleaseSpec<TSelf> (not NamedGnosisReleaseSpec or any other
        // intermediate). Only mainnet forks contribute to the public HardforkLabels list.
        INamedTypeSymbol? baseType = symbol.BaseType;
        if (baseType is null || baseType.Name != NamedReleaseSpecBaseName) return null;

        string? parentName = ExtractParentName(cls);
        ApplyAnalysis? analysis = AnalyzeApplyMethod(cls);
        if (analysis is null) return null;

        return new ForkInfo
        {
            Name = symbol.Name,
            ParentName = parentName,
            EipDelta = analysis.EipDelta,
            SetsIsPostMerge = analysis.SetsIsPostMerge,
        };
    }

    private static string? ExtractParentName(ClassDeclarationSyntax cls)
    {
        // `: NamedReleaseSpec<TSelf>(parentExpr)` — parentExpr is `Shanghai.Instance`, or `null` for the root.
        if (cls.BaseList is not { Types: { Count: > 0 } types }) return null;
        foreach (BaseTypeSyntax bt in types)
        {
            if (bt is not PrimaryConstructorBaseTypeSyntax primary) continue;
            if (primary.ArgumentList.Arguments.Count == 0) return null;

            ExpressionSyntax firstArg = primary.ArgumentList.Arguments[0].Expression;
            if (firstArg is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax idn })
                return idn.Identifier.ValueText;
            return null;
        }
        return null;
    }

    private sealed class ApplyAnalysis
    {
        public List<(int Eip, bool Enabled)> EipDelta { get; } = new();
        public bool SetsIsPostMerge { get; set; }
    }

    private static ApplyAnalysis? AnalyzeApplyMethod(ClassDeclarationSyntax cls)
    {
        MethodDeclarationSyntax? apply = null;
        foreach (MemberDeclarationSyntax m in cls.Members)
        {
            if (m is MethodDeclarationSyntax method &&
                method.Identifier.ValueText == "Apply" &&
                method.Modifiers.Any(SyntaxKind.OverrideKeyword))
            {
                apply = method;
                break;
            }
        }
        if (apply is null) return null;

        ApplyAnalysis result = new();
        foreach (AssignmentExpressionSyntax asn in apply.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            // Pattern: `spec.<Property> = <literal>`
            if (asn.Left is not MemberAccessExpressionSyntax ma) continue;
            if (ma.Expression is not IdentifierNameSyntax id || id.Identifier.ValueText != "spec") continue;
            string propName = ma.Name.Identifier.ValueText;

            if (propName == "IsPostMerge")
            {
                if (asn.Right is LiteralExpressionSyntax lit && lit.Token.IsKind(SyntaxKind.TrueKeyword))
                    result.SetsIsPostMerge = true;
                continue;
            }

            if (!propName.StartsWith("IsEip", StringComparison.Ordinal) ||
                !propName.EndsWith("Enabled", StringComparison.Ordinal)) continue;
            string middle = propName.Substring("IsEip".Length, propName.Length - "IsEip".Length - "Enabled".Length);
            if (!int.TryParse(middle, out int eipNumber)) continue;
            if (asn.Right is not LiteralExpressionSyntax rhs) continue;

            if (rhs.Token.IsKind(SyntaxKind.TrueKeyword)) result.EipDelta.Add((eipNumber, true));
            else if (rhs.Token.IsKind(SyntaxKind.FalseKeyword)) result.EipDelta.Add((eipNumber, false));
        }
        return result;
    }

    private static string Emit(ImmutableArray<ForkInfo> forks, HashSet<string> labelProperties)
    {
        List<ForkInfo> ordered = TopologicallyOrder(forks);

        StringBuilder sb = new();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Generated by Nethermind.Analyzers.HardforkLabelsGenerator.");
        sb.AppendLine("// Edits should target Forks/*.cs (the EIP delta) or the generator (JSON-field rules).");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine();
        sb.AppendLine("namespace Nethermind.Specs.ChainSpecStyle;");
        sb.AppendLine();
        sb.AppendLine("partial class HardforkLabels");
        sb.AppendLine("{");
        sb.AppendLine("    private static partial IReadOnlyList<IHardforkLabel> BuildAll() =>");
        sb.AppendLine("    [");

        foreach (ForkInfo fork in ordered)
        {
            // No matching ChainSpecParamsJson label property → fork is not user-facing as a shorthand.
            if (!labelProperties.Contains(fork.Name)) continue;

            List<string> jsonFields = ResolveJsonFields(fork);
            if (jsonFields.Count == 0) continue;

            string factory = fork.IsPostMerge ? "Time" : "Block";

            sb.Append("        ").Append(factory).Append("(p => p.").Append(fork.Name).AppendLine(",");
            for (int i = 0; i < jsonFields.Count; i++)
            {
                sb.Append("            p => p.").Append(jsonFields[i]);
                sb.AppendLine(i == jsonFields.Count - 1 ? ")," : ",");
            }
        }

        sb.AppendLine("    ];");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// EIPs whose JSON representation differs from the conventional
    /// <c>Eip&lt;N&gt;Transition[Timestamp]</c> on <c>ChainSpecParamsJson</c>:
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item>EIPs with no JSON transition field at all (block-reward, difficulty bomb, Byzantium
    ///         precompile activations, EIP-170 MaxCodeSize, Homestead EIP-2 difficulty curve) are
    ///         silently dropped from the label.</item>
    ///   <item>EIP-158 is Parity-split into <c>Eip161abcTransition</c> + <c>Eip161dTransition</c>.</item>
    ///   <item>Disables (<c>spec.IsEipNEnabled = false</c>) map to <c>Eip&lt;N&gt;DisableTransition</c>
    ///         — only ConstantinopleFix/Petersburg does this.</item>
    /// </list>
    /// </remarks>
    private static readonly HashSet<int> NoJsonField = new()
    {
        2,                   // Homestead — no Eip2Transition
        100, 196, 197, 198,  // Byzantium consensus / precompile activations
        170,                 // MaxCodeSize-tracked
        649, 1234,           // Block-reward / difficulty-bomb deltas
    };

    private static List<string> ResolveJsonFields(ForkInfo fork)
    {
        List<string> result = new();
        string suffix = fork.IsPostMerge ? "TransitionTimestamp" : "Transition";

        foreach ((int eip, bool enabled) in fork.EipDelta)
        {
            if (!enabled) { result.Add($"Eip{eip}DisableTransition"); continue; }
            if (NoJsonField.Contains(eip)) continue;
            if (eip == 158) { result.Add("Eip161abcTransition"); result.Add("Eip161dTransition"); continue; }
            result.Add($"Eip{eip}{suffix}");
        }
        return result;
    }

    private static List<ForkInfo> TopologicallyOrder(ImmutableArray<ForkInfo> forks)
    {
        Dictionary<string, ForkInfo> byName = forks.ToDictionary(f => f.Name);
        List<ForkInfo> ordered = new();
        HashSet<string> emitted = new();

        void Visit(ForkInfo f)
        {
            if (!emitted.Add(f.Name)) return;
            if (f.ParentName is { } pn && byName.TryGetValue(pn, out ForkInfo parent)) Visit(parent);
            ordered.Add(f);
        }

        foreach (ForkInfo f in forks.OrderBy(f => f.Name)) Visit(f);
        return ordered;
    }
}

internal sealed class ForkInfo
{
    public string Name { get; set; } = "";
    public string? ParentName { get; set; }
    public List<(int Eip, bool Enabled)> EipDelta { get; set; } = new();
    public bool SetsIsPostMerge { get; set; }
    public bool IsPostMerge { get; set; }
}
