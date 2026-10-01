// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.SimpleTransferCompletionExtractor;

internal sealed record CompilerClosure(
    CSharpCompilation Compilation,
    SourceFile[] Sources,
    CompilerSourcePins Pins,
    ReferenceIdentity[] References,
    SourceIdentity[] EffectiveSources);

internal static class CompilerSources
{
    internal const string PinsPath = "tools/Evm/Lean/OrdinaryTransactionRefundAdapterExtractor/Admission/SOURCE_PINS.json";
    internal const string PinsSha256 = "2339453e6f2ff64e9de71bf9d022c3aa27869dd3dc1e21b38a4731d2df57f6db";
    private const string EvmPath = "src/Nethermind/Nethermind.Evm/";

    internal static HashSet<string> CompiledPaths(string root)
    {
        byte[] bytes = File.ReadAllBytes(CompilerReferences.Within(root, PinsPath));
        CompilerReferences.RequireHash(bytes, PinsSha256, PinsPath);
        CompilerSourcePins pins = JsonSerializer.Deserialize<CompilerSourcePins>(bytes, CompilerReferences.JsonOptions)
            ?? throw new ExtractionException("The accepted source pin roster is empty.");
        if (pins.Sources.Length != 157)
            throw new ExtractionException("The accepted EVM source selection header changed.");
        return pins.Sources.Where(static source => source.Role is "semantic" or "compiler-support" or "compiler-generated")
            .Select(static source => source.Path).ToHashSet(StringComparer.Ordinal);
    }

    internal static CompilerClosure Load(string root, IReadOnlyDictionary<string, string>? overrides = null)
    {
        root = Path.GetFullPath(root);
        byte[] pinBytes = File.ReadAllBytes(CompilerReferences.Within(root, PinsPath));
        CompilerReferences.RequireHash(pinBytes, PinsSha256, PinsPath);
        CompilerSourcePins pins = JsonSerializer.Deserialize<CompilerSourcePins>(pinBytes, CompilerReferences.JsonOptions)
            ?? throw new ExtractionException("The accepted source pin roster is empty.");
        if (pins.SchemaVersion != 1 || pins.AssemblyName != "Nethermind.Evm" || pins.Configuration != "Release" ||
            pins.EnableZkEvm || pins.Sources.Length != 157 ||
            pins.Sources.Count(static source => source.Role == "semantic") != 11 ||
            pins.Sources.Count(static source => source.Role == "compiler-support") != 132 ||
            pins.Sources.Count(static source => source.Role == "compiler-generated") != 3 ||
            pins.Sources.Count(static source => source.Role == "selection-or-projection") != 11 ||
            pins.Sources.Select(static source => source.Path).Distinct(StringComparer.Ordinal).Count() != 157)
            throw new ExtractionException("The accepted EVM source selection header changed.");

        (MetadataReference[] references, ReferenceIdentity[] identities) = CompilerReferences.Load(root, pins);
        string[] liveSources = Directory.EnumerateFiles(CompilerReferences.Within(root, EvmPath), "*.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(static path => !path.Contains("/zkevm/", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains("/obj/", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains("/bin/", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".zkevm.cs", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).ToArray();
        string[] pinnedSources = pins.Sources
            .Where(static source => source.Path.StartsWith(EvmPath, StringComparison.Ordinal) && source.Path.EndsWith(".cs", StringComparison.Ordinal))
            .Select(static source => source.Path).Order(StringComparer.Ordinal).ToArray();
        if (!liveSources.SequenceEqual(pinnedSources, StringComparer.Ordinal))
            throw new ExtractionException("The live EVM source roster changed.");

        HashSet<string> compiledPaths = pins.Sources
            .Where(static source => source.Role is "semantic" or "compiler-support" or "compiler-generated")
            .Select(static source => source.Path).ToHashSet(StringComparer.Ordinal);
        if (overrides is not null && overrides.Keys.Any(path => !compiledPaths.Contains(path)))
            throw new ExtractionException("A compiler override names a path outside the accepted compiled EVM roster.");

        CSharpParseOptions parseOptions = new(LanguageVersion.CSharp14, DocumentationMode.Parse,
            SourceCodeKind.Regular, pins.DefineConstants);
        List<SourceFile> selected = [];
        foreach (SourceIdentity identity in pins.Sources)
        {
            byte[] bytes = File.ReadAllBytes(CompilerReferences.Within(root, identity.Path));
            CompilerReferences.RequireHash(bytes, identity.Sha256, identity.Path);
            if (identity.Role is not ("semantic" or "compiler-support" or "compiler-generated")) continue;
            byte[] selectedBytes = overrides is not null && overrides.TryGetValue(identity.Path, out string? replacement)
                ? Encoding.UTF8.GetBytes(replacement) : bytes;
            SyntaxTree tree = CSharpSyntaxTree.ParseText(Encoding.UTF8.GetString(selectedBytes), parseOptions, identity.Path);
            CompilationUnitSyntax syntax = tree.GetCompilationUnitRoot();
            if (tree.GetDiagnostics().Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                throw new ExtractionException("The selected EVM source has a syntax error: " + identity.Path);
            selected.Add(new SourceFile(identity.Path, identity.Role, CompilerReferences.Hash(selectedBytes), selectedBytes, tree, syntax));
        }
        if (selected.Count != 146)
            throw new ExtractionException("The accepted EVM compile roster is incomplete.");
        CSharpCompilation compilation = CSharpCompilation.Create("Nethermind.Evm",
            selected.Select(static source => source.Tree), references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release, allowUnsafe: true, checkOverflow: false,
                nullableContextOptions: NullableContextOptions.Enable,
                metadataImportOptions: MetadataImportOptions.All));
        Diagnostic[] errors = compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0)
            throw new ExtractionException("The selected production EVM source closure does not compile: " +
                string.Join("; ", errors.Take(30).Select(static error => error.ToString())));
        Dictionary<string, SourceFile> effective = selected.ToDictionary(static source => source.RelativePath, StringComparer.Ordinal);
        SourceIdentity[] effectiveSources = pins.Sources.Select(source => effective.TryGetValue(source.Path, out SourceFile? compiled)
            ? source with { Sha256 = compiled.Sha256 } : source).ToArray();
        return new CompilerClosure(compilation, selected.ToArray(), pins, identities, effectiveSources);
    }
}
