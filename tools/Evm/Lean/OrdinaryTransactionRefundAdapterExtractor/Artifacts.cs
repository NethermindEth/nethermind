// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;

namespace Nethermind.Evm.Lean.OrdinaryTransactionRefundAdapterExtractor;

internal static class Artifacts
{
    internal const string Name = "OrdinaryTransactionRefund";
    internal const int SchemaVersion = 1;
    internal const string Version = "1.0.0";
    internal const string Package = SourceAdmission.PackagePath;
    internal static readonly string[] OwnFiles =
    [
        "OrdinaryTransactionRefundAdapterExtractor.csproj", "Models.cs", "CompilerReferences.cs", "SourceAdmission.cs",
        "OperationLowering.cs", "RefundMachine.cs", "RefundPlanBuilder.cs", "LeanEmitter.cs", "Artifacts.cs", "Program.cs",
        "RefundKernel.lean.template", "Admission/SOURCE_PINS.json", "Admission/REVIEWED_SYNTAX.json", "Admission/COMPILER_REFERENCE_PINS.json", "UPSTREAM_PINS.json",
        "Specification/OrdinaryTransactionRefund.lean", "Refinement/OrdinaryTransactionRefund.lean", "Vectors/OrdinaryTransactionRefundVectors.lean",
        "Schema/ordinary-transaction-refund.ir.schema.json", "Schema/ordinary-transaction-refund.source-manifest.schema.json",
        "Verify.ps1", "Verify-Axioms.ps1", "Verify-MutationGates.ps1", "EXPORTED_THEOREMS.txt", "lean-toolchain", "lakefile.toml",
        "Test/OrdinaryTransactionRefundAdapterExtractor.Test.csproj", "Test/RefundMachineTests.cs", "Test/AdmissionTests.cs", "Test/ArtifactTests.cs",
    ];
    internal static readonly string[] UpstreamFiles =
    [
        "tools/Evm/Lean/Eip803x/Gas.lean", "tools/Evm/Lean/Eip803x/Schedule.lean", "tools/Evm/Lean/Eip803x/Production.lean",
        "tools/Evm/Lean/Eip803x/TransactionGas.lean", "tools/Evm/Lean/Eip803x/TransactionSettlement.lean",
        "tools/Evm/Lean/Eip803x/Generated/TransactionSettlementKernel.lean", "tools/Evm/Lean/Eip803x/Generated/StateGasTransitionKernel.lean",
        "tools/Evm/Lean/Eip803x/Generated/StateGasTransitionAdapterKernel.lean",
        "tools/Evm/Lean/Eip803x/Refinement/TransactionSettlement.lean", "tools/Evm/Lean/Eip803x/Refinement/StateGasTransition.lean",
        "tools/Evm/Lean/Eip803x/Refinement/StateGasTransitionAdapter.lean", "tools/Evm/Lean/Eip803x/Refinement/StateGasTransitionAdapterKernel.lean",
        "tools/Evm/Lean/Eip803x/TransactionSettlementVectors.lean", "tools/Evm/Lean/Eip803x/StateGasTransitionVectors.lean",
        "tools/Evm/Lean/Eip803x/StateGasTransitionAdapterVectors.lean",
        "tools/Evm/Lean/Extractor/Generated/TransactionSettlementKernel.ir.json", "tools/Evm/Lean/Extractor/Generated/TransactionSettlementKernel.source-manifest.json",
        "tools/Evm/Lean/Extractor/Generated/StateGasTransitionKernel.ir.json", "tools/Evm/Lean/Extractor/Generated/StateGasTransitionKernel.source-manifest.json",
        "tools/Evm/Lean/Extractor/Generated/StateGasTransitionAdapterKernel.ir.json", "tools/Evm/Lean/Extractor/Generated/StateGasTransitionAdapterKernel.source-manifest.json",
        "src/Nethermind/Nethermind.Core/Extensions/UInt64Extensions.cs",
    ];
    internal static string[] DependencyPaths => OwnFiles.Select(static path => Package + "/" + path).Concat(UpstreamFiles).ToArray();

    internal sealed record Identity(string Path, string Sha256);
    internal sealed record UpstreamPins(int SchemaVersion, string Status, Identity[] Dependencies);
    internal sealed record Document(int SchemaVersion, string ExtractorVersion, string AcceptanceState, string SourceClosureSha256,
        Identity[] Dependencies, SourceAdmission.AdmissionResult Admission);
    internal sealed record Manifest(int SchemaVersion, string ExtractorVersion, string AcceptanceState, string SourceClosureSha256,
        Identity Ir, Identity Lean, SourceIdentity[] Sources, Identity[] Dependencies, string CompilerInventorySha256);

    internal static Document Audit(string root)
    {
        RequireAcceptedUpstreams(root);
        SourceAdmission.AdmissionResult admission = SourceAdmission.Read(root);
        Identity[] dependencies = DependencyPaths.Select(path => Identify(root, path)).ToArray();
        string closure = CompilerReferences.Hash(Serialize(new { admission, dependencies }));
        return new(SchemaVersion, Version, "source-admitted", closure, dependencies, admission);
    }

    internal static void Extract(string root, string output)
    {
        Document document = Audit(root);
        byte[] ir = Serialize(document);
        Document roundTrip = Read(ir);
        byte[] lean = LeanEmitter.Emit(root, roundTrip.Admission, roundTrip.SourceClosureSha256, CompilerReferences.Hash(ir));
        byte[] manifest = Serialize(ManifestFor(roundTrip, ir, lean));
        string destination = ValidateOutput(root, output);
        Directory.CreateDirectory(destination);
        PublishTriplet(destination, ir, lean, manifest);
    }

    internal static void Check(string root, string output)
    {
        Document live = Audit(root);
        byte[] ir = File.ReadAllBytes(Path.Combine(output, Name + ".ir.json"));
        Document checkedIn = Read(ir);
        if (!ir.AsSpan().SequenceEqual(Serialize(checkedIn)) || !ir.AsSpan().SequenceEqual(Serialize(live)))
        {
            throw new AdmissionException("Refund IR differs from canonical live compiled-source admission.");
        }
        byte[] lean = LeanEmitter.Emit(root, live.Admission, live.SourceClosureSha256, CompilerReferences.Hash(ir));
        if (!lean.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(output, Name + ".lean"))))
        {
            throw new AdmissionException("Refund Lean differs from live source-derived expression emission.");
        }
        if (!Serialize(ManifestFor(live, ir, lean)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(output, Name + ".source-manifest.json"))))
        {
            throw new AdmissionException("Refund manifest differs from the complete live artifact set.");
        }
    }

    internal static Document Read(byte[] bytes)
    {
        RejectDuplicateProperties(bytes);
        Document document = JsonSerializer.Deserialize<Document>(bytes, CompilerReferences.JsonOptions)
            ?? throw new AdmissionException("Null refund IR.");
        if (document.SchemaVersion != SchemaVersion || document.ExtractorVersion != Version || document.AcceptanceState != "source-admitted" ||
            document.Dependencies is null || !document.Dependencies.Select(static identity => identity?.Path).SequenceEqual(DependencyPaths, StringComparer.Ordinal) ||
            document.Admission is null || document.Admission.Admission is null || document.Admission.Constants is null || document.Admission.Plan is null)
        {
            throw new AdmissionException("Refund IR header or exact dependency roster changed.");
        }
        foreach (Identity identity in document.Dependencies)
        {
            if (identity is null || !IsHash(identity.Sha256)) throw new AdmissionException("Invalid refund dependency identity.");
        }
        string closure = CompilerReferences.Hash(Serialize(new { admission = document.Admission, dependencies = document.Dependencies }));
        if (document.SourceClosureSha256 != closure) throw new AdmissionException("Refund source/operation/compiler closure digest changed.");
        if (!bytes.AsSpan().SequenceEqual(Serialize(document))) throw new AdmissionException("Refund IR is not canonical JSON.");
        return document;
    }

    internal static void AtomicWrite(string destination, byte[] bytes, Action? beforeReplace = null)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            beforeReplace?.Invoke();
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static void PublishTriplet(string destination, byte[] ir, byte[] lean, byte[] manifest, Action<string>? beforeReplace = null)
    {
        foreach ((string suffix, byte[] bytes) in new[] { (".ir.json", ir), (".lean", lean), (".source-manifest.json", manifest) })
        {
            string name = Name + suffix;
            AtomicWrite(Path.Combine(destination, name), bytes, () => beforeReplace?.Invoke(name));
        }
    }

    internal static byte[] Serialize<T>(T value) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, CompilerReferences.JsonOptions) + "\n");

    internal static void RejectDuplicateProperties(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        Visit(document.RootElement);
        static void Visit(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new AdmissionException("Duplicate or case-aliased refund JSON field.");
                    Visit(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in value.EnumerateArray()) Visit(item);
            }
        }
    }

    private static void RequireAcceptedUpstreams(string root)
    {
        byte[] bytes = File.ReadAllBytes(CompilerReferences.Within(root, Package + "/UPSTREAM_PINS.json"));
        RejectDuplicateProperties(bytes);
        UpstreamPins pins = JsonSerializer.Deserialize<UpstreamPins>(bytes, CompilerReferences.JsonOptions)
            ?? throw new AdmissionException("Null refund upstream pins.");
        if (pins.SchemaVersion != 1 || pins.Status != "accepted-standard-only" || pins.Dependencies is null ||
            !pins.Dependencies.Select(static identity => identity?.Path).SequenceEqual(UpstreamFiles, StringComparer.Ordinal))
        {
            throw new AdmissionException("Refund upstream standard-only proof repair and independent acceptance are still required.");
        }
        foreach (Identity pin in pins.Dependencies)
        {
            CompilerReferences.RequireHash(File.ReadAllBytes(CompilerReferences.Within(root, pin.Path)), pin.Sha256, pin.Path);
        }
    }

    private static Manifest ManifestFor(Document document, byte[] ir, byte[] lean) =>
        new(SchemaVersion, Version, "source-admitted", document.SourceClosureSha256,
            new(Name + ".ir.json", CompilerReferences.Hash(ir)), new(Name + ".lean", CompilerReferences.Hash(lean)),
            document.Admission.Admission.Sources, document.Dependencies, CompilerReferences.InventorySha256);

    private static Identity Identify(string root, string path) => new(path, CompilerReferences.Hash(File.ReadAllBytes(CompilerReferences.Within(root, path))));
    private static bool IsHash(string? text) => text is { Length: 64 } && text.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string ValidateOutput(string root, string output)
    {
        string destination = Path.GetFullPath(output);
        string repository = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        string production = Path.Combine(repository, "src");
        if (destination.Equals(repository, StringComparison.OrdinalIgnoreCase) || destination.Equals(production, StringComparison.OrdinalIgnoreCase) ||
            destination.StartsWith(production + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new AdmissionException("Refund output may not target production or the workspace root.");
        }
        return destination;
    }
}
