// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Receipt = Nethermind.Evm.Lean.ReceiptTerminalFoldExtractor;

namespace Nethermind.Evm.Lean.SequentialBlockTransactionFoldExtractor;

internal static class ReceiptDependencyAudit
{
    private const string PromotedPinsSha256 = "cf36cbbe50bb9e7a7578a59966acb7b39ca361fd1ddc5171f19a863ad5a0bf69";
    private const string PromotedArtifactsSha256 = "03b3d1d96ac31df4888e60b5e5446a588bbdc71a01052837709d7f9034d4bbe3";
    private const string PromotedSourcePinsSha256 = "0223aadf196cc74051d632260693eeb607fde202fa612caee59d3ddaca886c6f";
    private const string PromotedManifestSha256 = "3d1ec03bce750645bc29d8fb51ac94321508364735b9a65a987043100f74bd8e";

    internal const string PinsPath = "tools/Evm/Lean/SequentialBlockTransactionFoldExtractor/RECEIPT_DEPENDENCY_PINS.json";
    internal const string ReceiptRoot = "tools/Evm/Lean/ReceiptTerminalFoldExtractor/";
    internal static readonly string[] ArtifactPaths =
    [
        ReceiptRoot + "SOURCE_PINS.json",
        ReceiptRoot + "Generated/ReceiptTerminalFoldKernel.source-manifest.json",
        ReceiptRoot + "COMPILER_REFERENCE_PINS.json",
        ReceiptRoot + "Generated/ReceiptTerminalFoldKernel.ir.json",
        ReceiptRoot + "Generated/ReceiptTerminalFoldKernel.lean",
        ReceiptRoot + "Specification/ReceiptTerminalFold.lean",
        ReceiptRoot + "Refinement/ReceiptTerminalFold.lean",
        ReceiptRoot + "ReceiptTerminalFoldProfile.cs",
        ReceiptRoot + "ReceiptTerminalFoldControlFlow.cs",
        ReceiptRoot + "ReceiptTerminalFoldEffects.cs",
        ReceiptRoot + "ReceiptTerminalFoldArtifacts.cs",
        ReceiptRoot + "Program.cs",
        ReceiptRoot + "Models.cs",
        ReceiptRoot + "ReceiptTerminalFoldLeanEmitter.cs",
        ReceiptRoot + "ReceiptTerminalFoldExtractor.csproj",
        "tools/Evm/Lean/Extractor/Generated/BlockReceiptGasAccountingKernel.ir.json",
        "tools/Evm/Lean/Extractor/Generated/BlockReceiptGasAccountingKernel.source-manifest.json",
        "tools/Evm/Lean/Eip803x/BlockReceiptGas.lean",
        "tools/Evm/Lean/Eip803x/Generated/BlockReceiptGasAccountingKernel.lean",
        "tools/Evm/Lean/Eip803x/Generated/TransactionGasInitializationKernel.lean",
        "tools/Evm/Lean/Eip803x/Refinement/BlockReceiptGasAccounting.lean",
        "tools/Evm/Lean/Eip803x/Refinement/TransactionGasInitialization.lean",
        "tools/Evm/Lean/Eip803x/TransactionGas.lean",
    ];

    internal static ReceiptTerminalSourceClosureIdentity ReadValidated(string root)
    {
        ValidateUpstream(root);
        DependencyPins pins = ReadPins(File.ReadAllBytes(Path.Combine(root, PinsPath)));
        foreach (DependencyArtifact artifact in pins.Artifacts)
        {
            if (HashFile(root, artifact.Path) != artifact.Sha256)
            {
                throw new ExtractionException($"fold.receipt.artifact: drift in {artifact.Path}.");
            }
        }

        Receipt.SourcePinsDocument sources = ReadStrict<Receipt.SourcePinsDocument>(
            File.ReadAllBytes(Path.Combine(root, Extractor.ReceiptSourcePinsPath)));
        Receipt.SourceManifest manifest = ReadStrict<Receipt.SourceManifest>(
            File.ReadAllBytes(Path.Combine(root, Extractor.ReceiptSourceManifestPath)));
        if (sources.SchemaVersion != 3 || manifest.SchemaVersion != 9 || manifest.ExtractorVersion != "1.9.2" ||
            !sources.Sources.Select(static source => source.Path)
                .SequenceEqual(Receipt.ReceiptTerminalFoldProfile.SourceRelativePaths, StringComparer.Ordinal) ||
            sources.Sources.Length != 12 ||
            !sources.BindingSources.Select(static source => source.Path)
                .SequenceEqual(Receipt.ReceiptTerminalFoldProfile.BindingSourceRelativePaths, StringComparer.Ordinal))
        {
            throw new ExtractionException("fold.receipt.schema: expected complete Receipt source pins 3 / manifest 9 / extractor 1.9.2.");
        }
        return new(Extractor.ReceiptSourcePinsPath, pins.Artifacts[0].Sha256,
            Extractor.ReceiptSourceManifestPath, pins.Artifacts[1].Sha256,
            Extractor.CompilerReferenceInventoryPath, pins.Artifacts[2].Sha256,
            sources.Sources.Select(static source => new SourcePin(source.Path, source.Role, source.Sha256)).ToArray(),
            sources.BindingSources.Select(static source => new SourcePin(source.Path, source.Role, source.Sha256)).ToArray(),
            3, 9, "1.9.2", pins.Artifacts);
    }


    internal static void ValidatePinsForTest(byte[] bytes) => _ = ReadPins(bytes);

    private static DependencyPins ReadPins(byte[] bytes)
    {
        DependencyPins pins = ReadStrict<DependencyPins>(bytes);
        if (pins.SchemaVersion != 1 || pins.Status != "source-attached" || pins.Artifacts is null ||
            pins.Artifacts.Any(static artifact => artifact is null || !IsSha(artifact.Sha256)) ||
            !pins.Artifacts.Select(static artifact => artifact.Path).SequenceEqual(ArtifactPaths, StringComparer.Ordinal))
            throw new ExtractionException("fold.receipt.pins: dependency admission is static-draft, incomplete or changed.");
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != PromotedPinsSha256)
            throw new ExtractionException("fold.receipt.pinsHash: frozen dependency admission changed.");
        return pins;
    }

    internal static bool MatchesFrozenClosure(ReceiptTerminalSourceClosureIdentity closure) =>
        closure.Artifacts is not null && closure.Artifacts.All(static artifact => artifact is not null) &&
        closure.BindingSources is not null && closure.BindingSources.All(static source => source is not null) &&
        Digest(closure.Artifacts.Select(static artifact => $"{artifact.Path}\0{artifact.Sha256}")) ==
            PromotedArtifactsSha256 &&
        Digest(closure.BindingSources.Select(static source => $"{source.Path}\0{source.Role}\0{source.Sha256}")) ==
            "1f03d37698f7ee453139e38e50f07d544e3b2146997759abc86a3ccd8dbaf444" &&
        closure.SourcePinsSha256 == PromotedSourcePinsSha256 &&
        closure.SourceManifestSha256 == PromotedManifestSha256;

    private static string Digest(IEnumerable<string> lines) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));

    internal static void ValidateUpstreamArtifactsForTest(string root, byte[] manifest, byte[] ir, byte[] lean)
    {
        try { Receipt.ReceiptTerminalFoldProfile.ValidateArtifacts(root, manifest, ir, lean); }
        catch (Receipt.ExtractionException exception)
        {
            throw new ExtractionException($"fold.receipt.upstream: {exception.Message}");
        }
    }

    internal static void ValidateUpstream(string root)
    {
        try
        {
            Receipt.ReceiptTerminalFoldProfile.ValidateCheckedInArtifacts(root);
        }
        catch (Receipt.ExtractionException exception)
        {
            throw new ExtractionException($"fold.receipt.upstream: {exception.Message}");
        }
    }

    internal static T ReadStrict<T>(byte[] bytes)
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(bytes);
            CheckProperties(json.RootElement);
            return JsonSerializer.Deserialize<T>(bytes, Options)
                ?? throw new ExtractionException("fold.json: null document.");
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"fold.json: {exception.Message}");
        }
    }

    private static void CheckProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ExtractionException("fold.json: duplicate property.");
                CheckProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement value in element.EnumerateArray()) CheckProperties(value);
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    private static bool IsSha(string? value) => value is { Length: 64 } &&
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string HashFile(string root, string relativePath) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, relativePath))));

    private sealed record DependencyPins(int SchemaVersion, string Status, DependencyArtifact[] Artifacts);
}
