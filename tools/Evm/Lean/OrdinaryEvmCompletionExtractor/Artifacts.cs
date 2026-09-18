// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Evm.Lean.OrdinaryEvmCompletionExtractor;

internal static class Artifacts
{
    internal const string Stem = "OrdinaryEvmCompletion";
    internal const string Version = "1.0.0";
    internal const string Status = "accepted-conditional-source-audited-refinement";
    internal static readonly string[] OwnFiles =
    [
        "Admission/REVIEWED_BINDINGS.json", "Artifacts.cs", "CompilerReferences.cs", "CompletionKernel.lean.template",
        "LeanEmitter.cs", "Models.cs", "OperationLowering.cs", "OrdinaryEvmCompletionExtractor.csproj", "PlanBuilder.cs", "Program.cs", "SemanticBindings.cs", "SourceAdmission.cs",
        "Schema/ordinary-evm-completion.ir.schema.json", "Schema/ordinary-evm-completion.source-manifest.schema.json",
        "Specification/OrdinaryEvmCompletion.lean", "Refinement/OrdinaryEvmCompletion.lean",
        "Refinement/SourceAttachedOrdinaryEvmCompletion.lean", "Vectors/OrdinaryEvmCompletionVectors.lean",
        "Test/OrdinaryEvmCompletionExtractor.Test.csproj", "Test/SourceAdmissionTests.cs", "Test/ArtifactTests.cs",
        "Verify-Candidate.ps1", "Verify-CandidateSchemas.ps1", "Verify-SemanticMutations.ps1",
        "Verify-ClosureAxioms.ps1", "IMPORTED_DECLARATIONS.txt",
        "Verify-Draft.ps1", "Verify-DraftSchemas.ps1",
        "lakefile.toml", "lean-toolchain", "source-map.json", "source-map.schema.json",
        "upstream-artifacts.json", "upstream-artifacts.schema.json", "stage-dependencies.json", "stage-dependencies.schema.json",
    ];
    private static readonly (string File, string Hash)[] IdentityMaps =
    [
        ("source-map.json", "a8e4923970747350ff2958aa76d637fad88f77428d7c4615a6eb54e94c5ebf12"),
        ("upstream-artifacts.json", "b0e4dfb571456a1edb6e588610f5ad697aa533db4b2aa936936bd9aa1b27b9f1"),
        ("stage-dependencies.json", "319528119ff5797ad1cfb16fec0731b2d03cb08c47ba20cc52329bcfea1dc142"),
    ];

    internal sealed record ArtifactIdentity(string Path, string Sha256);
    internal sealed record Manifest(int SchemaVersion, string ExtractorVersion, string AcceptanceState, string SourceClosure,
        SourceIdentity[] Dependencies, SourceIdentity[] Sources, ArtifactIdentity Ir, ArtifactIdentity Lean);

    internal static ArtifactDocument Audit(string root)
    {
        string package = CompilerReferences.Within(root, SourceAdmission.PackagePath);
        List<string> ownSources = [];
        foreach (string file in Directory.EnumerateFiles(package, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(package, file).Replace('\\', '/');
            string[] components = relative.Split('/');
            if (components.Any(static part => part is ".lake" or "bin" or "obj")) continue;
            ownSources.Add(relative);
        }
        RequireOwnSourceCoverage(ownSources, OwnFiles);
        SourceModel source = SourceAdmission.Read(root);
        List<SourceIdentity> dependencies = [];
        HashSet<string> paths = new(StringComparer.Ordinal);
        void Add(string path, string role, string? expected = null)
        {
            byte[] bytes = File.ReadAllBytes(CompilerReferences.Within(root, path));
            string hash = CompilerReferences.Hash(bytes);
            if (expected is not null && hash != expected) throw new AdmissionException("dependency." + path);
            if (paths.Add(path)) dependencies.Add(new(path, role, hash));
        }
        foreach (string file in OwnFiles) Add(SourceAdmission.PackagePath + "/" + file, "candidate-tool-or-proof");
        Add(SourceAdmission.PinsPath, "accepted-compiler-input", SourceAdmission.PinsSha256);
        Add(CompilerReferences.InventoryPath, "accepted-compiler-input", CompilerReferences.InventorySha256);
        foreach ((string file, string expected) in IdentityMaps)
        {
            byte[] bytes = File.ReadAllBytes(CompilerReferences.Within(root, SourceAdmission.PackagePath + "/" + file));
            CompilerReferences.RequireHash(bytes, expected, file);
            RejectDuplicateProperties(bytes);
            using JsonDocument map = JsonDocument.Parse(bytes);
            foreach (JsonElement identity in map.RootElement.GetProperty("files").EnumerateArray())
                Add(identity.GetProperty("path").GetString()!, "pinned-boundary-or-stage", identity.GetProperty("sha256").GetString());
        }
        SourceIdentity[] ordered = dependencies.OrderBy(static identity => identity.Path, StringComparer.Ordinal).ToArray();
        string closure = ComputeSourceClosure(ordered, source);
        return new(1, Version, Status, closure, ordered, source);
    }

    internal static void RequireOwnSourceCoverage(IEnumerable<string> sources, IReadOnlyCollection<string> declared)
    {
        HashSet<string> inventory = new(declared, StringComparer.Ordinal);
        if (inventory.Count != declared.Count) throw new AdmissionException("own-tool.duplicate");
        foreach (string source in sources)
            if (!inventory.Contains(source)) throw new AdmissionException("own-tool.missing." + source);
    }

    internal static string ComputeSourceClosure(SourceIdentity[] dependencies, SourceModel source) =>
        CompilerReferences.Hash(Serialize(new { dependencies, source }));

    internal static (byte[] Ir, byte[] Lean, byte[] Manifest) Render(string root, ArtifactDocument document)
    {
        byte[] ir = Serialize(document);
        RejectDuplicateProperties(ir);
        ArtifactDocument roundTrip = JsonSerializer.Deserialize<ArtifactDocument>(ir, CompilerReferences.JsonOptions)
            ?? throw new AdmissionException("artifact.null");
        if (!Serialize(roundTrip).SequenceEqual(ir)) throw new AdmissionException("artifact.roundtrip");
        byte[] lean = LeanEmitter.Emit(root, document, CompilerReferences.Hash(ir));
        Manifest manifest = new(1, Version, Status, document.SourceClosure, document.Dependencies, document.Source.Compiler.Sources,
            new(Stem + ".ir.json", CompilerReferences.Hash(ir)), new(Stem + ".lean", CompilerReferences.Hash(lean)));
        return (ir, lean, Serialize(manifest));
    }

    internal static void Extract(string root, string output)
    {
        (byte[] ir, byte[] lean, byte[] manifest) = Render(root, Audit(root));
        Directory.CreateDirectory(output);
        AtomicWrite(Path.Combine(output, Stem + ".ir.json"), ir);
        AtomicWrite(Path.Combine(output, Stem + ".lean"), lean);
        AtomicWrite(Path.Combine(output, Stem + ".source-manifest.json"), manifest);
    }

    internal static void Check(string root, string output)
    {
        (byte[] ir, byte[] lean, byte[] manifest) = Render(root, Audit(root));
        CheckFile(Stem + ".ir.json", ir, json: true);
        CheckFile(Stem + ".lean", lean, json: false);
        CheckFile(Stem + ".source-manifest.json", manifest, json: true);
        void CheckFile(string file, byte[] expected, bool json)
        {
            byte[] actual = File.ReadAllBytes(Path.Combine(output, file));
            if (json) RejectDuplicateProperties(actual);
            if (!actual.SequenceEqual(expected)) throw new AdmissionException("artifact.drift." + file);
        }
    }

    internal static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, CompilerReferences.JsonOptions);

    internal static void RejectDuplicateProperties(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 256 });
        Visit(document.RootElement);
        static void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new AdmissionException("artifact.duplicate-property." + property.Name);
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in element.EnumerateArray()) Visit(item);
        }
    }

    internal static void AtomicWrite(string path, byte[] bytes, Action? beforeReplace = null)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            beforeReplace?.Invoke();
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
