// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Nethermind.Evm.Lean.SequentialBlockPostTransactionFinalizationExtractor;

internal static class ArtifactSafety
{
    internal static void RequireUnconditionalSource(SyntaxNode source, string boundary)
    {
        if (source.DescendantTrivia(descendIntoTrivia: true).Any(trivia =>
                trivia.IsDirective || trivia.IsKind(SyntaxKind.DisabledTextTrivia)))
            throw new ExtractionException(boundary + " conditional compilation or source-selection directives are not admitted.");
    }

    internal static string ToSourceIdentity(this ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => MethodIdentity(method.ReducedFrom ?? method),
        ILocalSymbol local => local.ContainingType.ToSourceIdentity() + "." + local.ContainingSymbol.Name + "." + local.Name,
        IParameterSymbol parameter => parameter.ContainingSymbol.ToSourceIdentity() + "." + parameter.Ordinal,
        IPropertySymbol property => property.ContainingType.ToSourceIdentity() + "." + property.Name,
        IFieldSymbol field => field.ContainingType.ToSourceIdentity() + "." + field.Name,
        IEventSymbol eventSymbol => eventSymbol.ContainingType.ToSourceIdentity() + "." + eventSymbol.Name,
        _ => symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
    };

    private static string MethodIdentity(IMethodSymbol method) =>
        method.ContainingType.ToSourceIdentity() + "." + method.Name + "(" +
        string.Join(",", method.Parameters.Select(static parameter => TypeIdentity(parameter.Type) +
            (parameter.RefKind == RefKind.None ? "" : "&"))) + ")";

    internal static string TypeIdentity(ITypeSymbol? type) =>
        type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat
                .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                    SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers))
            .Replace("global::", "", StringComparison.Ordinal) ?? "void";

    internal static void ValidateJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
            throw new ExtractionException("Finalization JSON schema does not admit null fields.");
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new ExtractionException("Duplicate finalization JSON property: " + property.Name);
                ValidateJson(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray()) ValidateJson(item);
        }
    }

    internal static bool ContainsProofToken(string source, bool generated)
    {
        int index = 0;
        while (index < source.Length)
        {
            if (source.AsSpan(index).StartsWith("--", StringComparison.Ordinal))
            {
                while (index < source.Length && source[index] != '\n') index++;
            }
            else if (source.AsSpan(index).StartsWith("/-", StringComparison.Ordinal))
            {
                int depth = 1;
                index += 2;
                while (index < source.Length && depth > 0)
                {
                    if (source.AsSpan(index).StartsWith("/-", StringComparison.Ordinal)) { depth++; index += 2; }
                    else if (source.AsSpan(index).StartsWith("-/", StringComparison.Ordinal)) { depth--; index += 2; }
                    else index++;
                }
                if (depth != 0) throw new ExtractionException("Unterminated Lean block comment.");
            }
            else if (source[index] == '"')
            {
                index++;
                bool closed = false;
                while (index < source.Length)
                {
                    char next = source[index++];
                    if (next == '\\') index++;
                    else if (next == '"') { closed = true; break; }
                }
                if (!closed) throw new ExtractionException("Unterminated Lean string.");
            }
            else if (char.IsLetter(source[index]) || source[index] == '_')
            {
                int start = index++;
                while (index < source.Length && (char.IsLetterOrDigit(source[index]) || source[index] is '_' or '\'')) index++;
                string token = source[start..index];
                if (token is "sorry" or "admit" or "axiom" or "native_decide" ||
                    generated && token is "theorem" or "example") return true;
            }
            else index++;
        }
        return false;
    }

    internal static void AtomicWrite(string path, byte[] bytes, Action? beforeReplace = null)
    {
        string directory = Path.GetDirectoryName(path) ?? throw new ExtractionException("Artifact directory is missing.");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            beforeReplace?.Invoke();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
