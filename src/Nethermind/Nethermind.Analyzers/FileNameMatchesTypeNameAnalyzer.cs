// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Nethermind.Analyzers;

/// <summary>
/// Reports .cs files whose name does not match the single top-level type they contain.
/// Files with zero or more than one top-level type are ignored.
/// Attribute types may drop the <c>Attribute</c> suffix from the file name.
/// Partial types and generic types may use <c>TypeName.Descriptor.cs</c> form.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class FileNameMatchesTypeNameAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "NETH003";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "File name does not match the contained type",
        messageFormat: "File name '{0}.cs' does not match the contained type '{1}'. Rename the file to '{1}.cs' (or '{1}.<suffix>.cs' for partials).",
        category: "Naming",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Each .cs file with a single top-level type should be named after that type. " +
                     "For attribute types the 'Attribute' suffix may be omitted from the file name. " +
                     "For partial types the file name may use a 'TypeName.Descriptor.cs' form.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxTreeAction(AnalyzeSyntaxTree);
    }

    private static void AnalyzeSyntaxTree(SyntaxTreeAnalysisContext context)
    {
        string filePath = context.Tree.FilePath;
        if (string.IsNullOrEmpty(filePath) || !filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return;

        CompilationUnitSyntax root = (CompilationUnitSyntax)context.Tree.GetRoot(context.CancellationToken);
        List<MemberDeclarationSyntax> topLevelTypes = [];
        CollectTopLevelTypes(root.Members, topLevelTypes);

        if (topLevelTypes.Count != 1)
            return;

        SyntaxToken identifier = GetIdentifier(topLevelTypes[0]);
        if (identifier == default)
            return;

        string typeName = identifier.ValueText;
        bool isPartial = topLevelTypes[0] is TypeDeclarationSyntax td
            && td.Modifiers.Any(SyntaxKind.PartialKeyword);
        bool isGeneric = topLevelTypes[0] is TypeDeclarationSyntax { TypeParameterList.Parameters.Count: > 0 };

        string fileBaseName = Path.GetFileNameWithoutExtension(filePath);

        // Attribute types may drop the "Attribute" suffix in the file name
        const string attributeSuffix = "Attribute";
        string? strippedName = typeName.EndsWith(attributeSuffix, StringComparison.Ordinal) && typeName.Length > attributeSuffix.Length
            ? typeName.Substring(0, typeName.Length - attributeSuffix.Length)
            : null;

        if (IsMatch(fileBaseName, typeName, strippedName, isPartial || isGeneric))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, identifier.GetLocation(), fileBaseName, typeName));
    }

    private static bool IsMatch(string fileBaseName, string typeName, string? strippedName, bool isPartial)
    {
        // Strip a leading numeric-order prefix of the form "NN_" (e.g. "00_Olympic" → "Olympic")
        string stripped = StripNumericPrefix(fileBaseName);

        if (stripped == typeName || stripped == strippedName)
            return true;

        if (isPartial)
        {
            if (HasDescriptorPrefix(stripped, typeName))
                return true;
            if (strippedName is not null && HasDescriptorPrefix(stripped, strippedName))
                return true;
        }

        return false;
    }

    // Strips a leading "digits_" ordering prefix, e.g. "00_Olympic" → "Olympic".
    private static string StripNumericPrefix(string fileBaseName)
    {
        int i = 0;
        while (i < fileBaseName.Length && char.IsDigit(fileBaseName[i]))
            i++;
        return i > 0 && i < fileBaseName.Length && fileBaseName[i] == '_'
            ? fileBaseName.Substring(i + 1)
            : fileBaseName;
    }

    // Returns true for "TypeName.SomeDescriptor" where the descriptor is non-empty.
    private static bool HasDescriptorPrefix(string fileBaseName, string prefix) =>
        fileBaseName.Length > prefix.Length + 1
        && fileBaseName[prefix.Length] == '.'
        && fileBaseName.StartsWith(prefix, StringComparison.Ordinal);

    private static void CollectTopLevelTypes(SyntaxList<MemberDeclarationSyntax> members, List<MemberDeclarationSyntax> result)
    {
        foreach (MemberDeclarationSyntax member in members)
        {
            if (result.Count > 1)
                return;
            if (member is BaseNamespaceDeclarationSyntax ns)
                CollectTopLevelTypes(ns.Members, result);
            else if (member is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
                result.Add(member);
        }
    }

    private static SyntaxToken GetIdentifier(MemberDeclarationSyntax decl) => decl switch
    {
        BaseTypeDeclarationSyntax type => type.Identifier,
        DelegateDeclarationSyntax del => del.Identifier,
        _ => default,
    };
}
