// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Evm.Lean.SequentialBlockPostTransactionFinalizationExtractor;

internal static partial class ProcessOneValidatedPublicationExtractor
{
    private static readonly string[] PreservedSourceTokens =
    [
        "4e4132b814775fe20d61628f68be48d18bb09662bc973997582fe96418121615",
        "86fe46cc924e297ab245e55c75b4d8d38cd20b7475510bd3671b2a9f4eae0648",
        "ce9586eee9f91096128f85f759e680f9e41983e56d273bd184d05cba2cb0fecb",
        "13341635f80468839be5d70505d8b90038cba668a44b6a9700aad1269d612559",
        "659b288a4a4425dcfa8d7f2579c00e1e2960a6ca9ae2be9a8a08519074439cd5",
        "b2646a87262313d2575eb33ddb3360a3557e94efcbe0c07d1e03f21398ce4f66",
        "a14e7733329d6c08c738336cf8817f6402ee3b970bd26b24bebc81fdac611139",
        "bf4803955a6049b162c8321a0ed73c282aebe12883e16e5be616b2e90185ad39",
    ];

    private static void ValidatePreservedSourceTokens(PublicationSourceIdentity[] sources)
    {
        if (!sources.Select(static source => source.Path).SequenceEqual(SourcePaths, StringComparer.Ordinal) ||
            !sources.Select(static source => source.SyntaxSha256).SequenceEqual(PreservedSourceTokens, StringComparer.Ordinal))
            throw new ExtractionException("Publication executable source-token closure changed.");
    }

    private static void ValidatePublicationCompilerClosure(CompilerClosureIdentity closure)
    {
        if (closure is null || closure.InventoryPath != "tools/Evm/Lean/ReceiptTerminalFoldExtractor/COMPILER_REFERENCE_PINS.json" ||
            closure.Count != 434 || closure.AggregateSha256 != "6fdfa102a4190083ae62080355aa6691b5f46acc32d4f9da2212012686d9c2e2" ||
            closure.InventorySha256 != "1f73a3800a957dd02d9d7b06b3b955c81bb92fd9eb8d2831920905394bfc1d5c" ||
            closure.SupportSources is null || !closure.SupportSources.SequenceEqual(Extractor.CompilerSupportSources))
            throw new ExtractionException("Publication compiler-reference/support closure changed.");
    }

    private static void ValidatePublicationUpstreamPins(string root)
    {
        const string path = "tools/Evm/Lean/SequentialBlockPostTransactionFinalizationExtractor/PUBLICATION_PROMOTION_PINS.json";
        using JsonDocument pins = ReadJson(Path.Combine(root, path));
        JsonElement document = pins.RootElement;
        RequireReceiptObject(document, ["acceptanceState", "finalizationSchemaVersion", "finalizationExtractorVersion",
            "finalizationIrSha256", "finalizationManifestSha256", "finalizationLeanSha256"]);
        using JsonDocument manifest = ReadJson(Path.Combine(root, FinalizationManifestPath));
        if (ReceiptString(document, "acceptanceState") != "accepted-upstream" ||
            ReceiptInteger(document, "finalizationSchemaVersion") != 8 ||
            ReceiptString(document, "finalizationExtractorVersion") != "1.14.0" ||
            ReceiptInteger(manifest.RootElement, "schemaVersion") != 8 ||
            ReceiptString(manifest.RootElement, "extractorVersion") != "1.14.0" ||
            ReceiptHash(document, "finalizationIrSha256") != Sha256File(root, FinalizationIrPath) ||
            ReceiptHash(document, "finalizationManifestSha256") != Sha256File(root, FinalizationManifestPath) ||
            ReceiptHash(document, "finalizationLeanSha256") != Sha256File(root, FinalizationLeanPath))
            throw new ExtractionException("Publication accepted finalization dependency pins changed.");
    }
}
