// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text.Json;
using Fold = Nethermind.Evm.Lean.SequentialBlockTransactionFoldExtractor;

namespace Nethermind.Evm.Lean.SequentialBlockPostTransactionFinalizationExtractor;

internal static class DependencyAudit
{
    private const string Package = "tools/Evm/Lean/SequentialBlockPostTransactionFinalizationExtractor/";
    private const string FoldPackage = "tools/Evm/Lean/SequentialBlockTransactionFoldExtractor/";
    private const string ReceiptPackage = "tools/Evm/Lean/ReceiptTerminalFoldExtractor/";
    internal static readonly string[] Paths =
    [
        Package + "LeanEmitter.cs",
        Package + "Specification/SequentialBlockPostTransactionFinalization.lean",
        Package + "Refinement/SequentialBlockPostTransactionFinalization.lean",
        Package + "Vectors/SequentialBlockPostTransactionFinalizationVectors.lean",
        Package + "EXPORTED_THEOREMS.txt",
        Package + "Verify-Axioms.ps1",
        Package + "Verify-MutationGates.ps1",
        Package + "PROMOTION_PINS.json",
        "src/Nethermind/Nethermind.Core/Metric/MetricsTimer.cs",
        .. Extractor.CompilerSupportSources.Select(static source => source.Path),
        FoldPackage + "SOURCE_PINS.json",
        FoldPackage + "Generated/SequentialBlockTransactionFold.ir.json",
        FoldPackage + "Generated/SequentialBlockTransactionFold.source-manifest.json",
        FoldPackage + "Generated/SequentialBlockTransactionFold.lean",
        FoldPackage + "Specification/SequentialBlockTransactionFold.lean",
        FoldPackage + "Refinement/SequentialBlockTransactionFold.lean",
        FoldPackage + "Vectors/SequentialBlockTransactionFoldVectors.lean",
        FoldPackage + "EXPORTED_THEOREMS.txt",
        FoldPackage + "Verify-Axioms.ps1",
        ReceiptPackage + "SOURCE_PINS.json",
        ReceiptPackage + "COMPILER_REFERENCE_PINS.json",
        ReceiptPackage + "Generated/ReceiptTerminalFoldKernel.ir.json",
        ReceiptPackage + "Generated/ReceiptTerminalFoldKernel.source-manifest.json",
        ReceiptPackage + "Generated/ReceiptTerminalFoldKernel.lean",
        ReceiptPackage + "Specification/ReceiptTerminalFold.lean",
        ReceiptPackage + "Refinement/ReceiptTerminalFold.lean",
        ReceiptPackage + "Vectors/ReceiptTerminalFoldVectors.lean",
        ReceiptPackage + "EXPORTED_THEOREMS.txt",
        ReceiptPackage + "Verify-Axioms.ps1",
        "tools/Evm/Lean/Eip803x/BlockReceiptGas.lean",
        "tools/Evm/Lean/Eip803x/TransactionGas.lean",
        "tools/Evm/Lean/Eip803x/Generated/BlockReceiptGasAccountingKernel.lean",
        "tools/Evm/Lean/Eip803x/Generated/TransactionGasInitializationKernel.lean",
        "tools/Evm/Lean/Eip803x/Refinement/BlockReceiptGasAccounting.lean",
        "tools/Evm/Lean/Eip803x/Refinement/TransactionGasInitialization.lean",
    ];

    internal static void ValidateUpstream(string root)
    {
        try
        {
            Fold.Extractor.ValidateCheckedIn(root, null);
        }
        catch (Exception exception) when (exception is Fold.ExtractionException or IOException or UnauthorizedAccessException)
        {
            throw new ExtractionException("Full Fold dependency validation failed: " + exception.Message);
        }
        using JsonDocument promotion = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, Package + "PROMOTION_PINS.json")));
        ArtifactSafety.ValidateJson(promotion.RootElement);
        JsonElement pins = promotion.RootElement;
        using JsonDocument foldManifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root,
            FoldPackage + "Generated/SequentialBlockTransactionFold.source-manifest.json")));
        string[] fields = ["acceptanceState", "foldSchemaVersion", "foldExtractorVersion", "foldManifestSha256",
            "foldIrSha256", "foldLeanSha256", "receiptManifestSha256"];
        if (!pins.EnumerateObject().Select(static property => property.Name).SequenceEqual(fields) ||
            pins.GetProperty("acceptanceState").GetString() != "accepted-upstreams" ||
            pins.GetProperty("foldSchemaVersion").GetInt32() != 9 ||
            pins.GetProperty("foldExtractorVersion").GetString() != "1.8.0" ||
            foldManifest.RootElement.GetProperty("schemaVersion").GetInt32() != 9 ||
            foldManifest.RootElement.GetProperty("extractorVersion").GetString() != "1.8.0" ||
            pins.GetProperty("foldManifestSha256").GetString() != Hash(root, FoldPackage + "Generated/SequentialBlockTransactionFold.source-manifest.json") ||
            pins.GetProperty("foldIrSha256").GetString() != Hash(root, FoldPackage + "Generated/SequentialBlockTransactionFold.ir.json") ||
            pins.GetProperty("foldLeanSha256").GetString() != Hash(root, FoldPackage + "Generated/SequentialBlockTransactionFold.lean") ||
            pins.GetProperty("receiptManifestSha256").GetString() != Hash(root, ReceiptPackage + "Generated/ReceiptTerminalFoldKernel.source-manifest.json"))
            throw new ExtractionException("Finalization promotion pins remain static-draft or accepted upstream manifests drifted.");
    }

    internal static PublicationDependencyIdentity[] Read(string root) =>
        Paths.Select(path => new PublicationDependencyIdentity(path, Hash(root, path))).ToArray();

    internal static void Validate(string root, PublicationDependencyIdentity[] dependencies)
    {
        if (dependencies is null || !dependencies.SequenceEqual(Read(root)))
            throw new ExtractionException("Finalization semantic/proof dependency closure changed.");
    }

    private static string Hash(string root, string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, path)))).ToLowerInvariant();
}
