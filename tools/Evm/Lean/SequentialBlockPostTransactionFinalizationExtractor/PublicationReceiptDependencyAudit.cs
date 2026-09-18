// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Evm.Lean.SequentialBlockPostTransactionFinalizationExtractor;

internal static partial class ProcessOneValidatedPublicationExtractor
{
    private static readonly string[] ReceiptSourcePaths =
    [
        "src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptsTracer.cs",
        "src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptGasAccountingKernel.cs",
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/GasConsumed.cs",
        "src/Nethermind/Nethermind.Core/TransactionReceipt.cs",
        "src/Nethermind/Nethermind.Core/Transaction.cs",
        "src/Nethermind/Nethermind.Core/TransactionExtensions.cs",
        "src/Nethermind/Nethermind.Core/Block.cs",
        "src/Nethermind/Nethermind.Core/BlockHeader.cs",
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs",
        "src/Nethermind/Nethermind.Evm/StatusCode.cs",
        "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs",
        "src/Nethermind/Nethermind.Evm/GasPolicy/TransactionGasInitializationKernel.cs",
    ];

    internal static void ValidateReceiptDependencyForTest(byte[] pins, byte[] manifest,
        IReadOnlyDictionary<string, string> fileHashes) =>
        ValidateReceiptDependency(pins, manifest, path => fileHashes.GetValueOrDefault(path));

    private static void ValidateReceiptDependency(byte[] pinsBytes, byte[] manifestBytes, Func<string, string?> fileHash)
    {
        try
        {
            using JsonDocument pins = JsonDocument.Parse(pinsBytes);
            using JsonDocument manifest = JsonDocument.Parse(manifestBytes);
            ValidateReceiptEnvelope(pins.RootElement, isPins: true);
            ValidateReceiptEnvelope(manifest.RootElement, isPins: false);
            Dictionary<string, string> pinned = ReadReceiptSourceHashes(pins.RootElement, isPins: true);
            Dictionary<string, string> emitted = ReadReceiptSourceHashes(manifest.RootElement, isPins: false);
            foreach (string path in ReceiptSourcePaths)
            {
                if (pinned[path] != emitted[path])
                    throw new ExtractionException($"Settled ReceiptTerminal manifest is stale for '{path}'.");
                if (pinned[path] != fileHash(path))
                    throw new ExtractionException($"Settled ReceiptTerminal source is missing or stale for '{path}'.");
            }
            foreach ((string name, string path) in new[] { ("ir", ReceiptIrPath), ("lean", ReceiptLeanPath) })
            {
                JsonElement artifact = manifest.RootElement.GetProperty(name);
                RequireReceiptObject(artifact, ["path", "sha256"]);
                string expectedPath = name == "ir" ? Path.GetFileName(path) : path;
                if (ReceiptString(artifact, "path") != expectedPath || ReceiptHash(artifact, "sha256") != fileHash(path))
                    throw new ExtractionException($"Settled ReceiptTerminal {name} artifact is missing or stale against its manifest.");
            }
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"ReceiptTerminal dependency JSON is malformed: {exception.Message}");
        }
    }

    private static void ValidateReceiptEnvelope(JsonElement document, bool isPins)
    {
        ArtifactSafety.ValidateJson(document);
        if (isPins)
        {
            RequireReceiptObject(document, ["schemaVersion", "root", "sources", "bindingSources", "delegatedKernel"]);
            if (ReceiptInteger(document, "schemaVersion") != 3 || ReceiptString(document, "root") !=
                "Nethermind.Blockchain.Tracing.BlockReceiptsTracer (exact base; no BuildReceipt override)")
                throw new ExtractionException("ReceiptTerminal pins schema/header is unsupported.");
            ValidateReceiptSourceArray(ReceiptArray(document, "bindingSources"), isPins: true);
            ValidateReceiptStringObject(document.GetProperty("delegatedKernel"),
                ["generatedLean", "generatedLeanSha256", "refinement", "refinementSha256", "ir", "irSha256",
                 "manifest", "manifestSha256", "sourceSha256", "leanDependencies"]);
            return;
        }

        RequireReceiptObject(document,
            ["schemaVersion", "extractorVersion", "compilerVersion", "languageVersion", "kernel", "sources",
             "bindingSources", "compilerReferences", "compilerReferenceCount", "compilerReferenceAggregateSha256",
             "members", "ir", "lean", "accountingKernel", "combinedSourceSha256", "combinedMemberSha256", "semanticBindings"]);
        if (ReceiptInteger(document, "schemaVersion") != 9 || ReceiptString(document, "extractorVersion") != "1.9.2" ||
            ReceiptString(document, "compilerVersion") != "5.6.0.0" || ReceiptString(document, "languageVersion") != "14.0" ||
            ReceiptString(document, "kernel") != "standard-mainnet sequential BlockReceiptsTracer terminal receipt fold")
            throw new ExtractionException("ReceiptTerminal manifest schema/header is unsupported.");
        ValidateReceiptSourceArray(ReceiptArray(document, "bindingSources"), isPins: false);
        JsonElement references = ReceiptArray(document, "compilerReferences");
        if (ReceiptInteger(document, "compilerReferenceCount") != 434 || references.GetArrayLength() != 434)
            throw new ExtractionException("ReceiptTerminal compiler-reference schema is incomplete.");
        foreach (JsonElement reference in references.EnumerateArray())
        {
            RequireReceiptObject(reference, ["path", "assemblyName", "sha256", "mvid", "selected"]);
            _ = ReceiptString(reference, "path");
            _ = ReceiptString(reference, "assemblyName");
            _ = ReceiptHash(reference, "sha256");
            if (!Guid.TryParseExact(ReceiptString(reference, "mvid"), "D", out _) ||
                reference.GetProperty("selected").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ExtractionException("ReceiptTerminal compiler-reference identity is malformed.");
        }
        _ = ReceiptHash(document, "compilerReferenceAggregateSha256");
        _ = ReceiptHash(document, "combinedSourceSha256");
        _ = ReceiptHash(document, "combinedMemberSha256");
        foreach (JsonElement member in ReceiptArray(document, "members").EnumerateArray())
        {
            RequireReceiptObject(member, ["id", "sourcePath", "containingType", "member", "kind", "signature",
                "parameterTypes", "semanticType", "nodeKind", "canonicalSha256"]);
            foreach (string name in new[] { "id", "sourcePath", "containingType", "member", "kind", "signature", "semanticType", "nodeKind" })
                _ = ReceiptString(member, name);
            _ = ReceiptHash(member, "canonicalSha256");
            ValidateReceiptStrings(ReceiptArray(member, "parameterTypes", allowEmpty: true));
        }
        ValidateReceiptStringObject(document.GetProperty("accountingKernel"),
            ["root", "sourcePath", "sourceSha256", "irPath", "irSha256", "generatedLeanPath", "generatedLeanSha256",
             "refinementPath", "refinementSha256", "manifestPath", "manifestSha256", "leanDependencies"]);
        ValidateReceiptStrings(ReceiptArray(document, "semanticBindings"));
    }

    private static Dictionary<string, string> ReadReceiptSourceHashes(JsonElement document, bool isPins)
    {
        JsonElement sources = ReceiptArray(document, "sources");
        if (sources.GetArrayLength() != ReceiptSourcePaths.Length)
            throw new ExtractionException("ReceiptTerminal source inventory must contain exactly 12 admitted entries.");
        ValidateReceiptSourceArray(sources, isPins);
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        int index = 0;
        foreach (JsonElement source in sources.EnumerateArray())
        {
            string path = ReceiptString(source, "path");
            if (path != ReceiptSourcePaths[index++] || !result.TryAdd(path, ReceiptHash(source, "sha256")))
                throw new ExtractionException("ReceiptTerminal source inventory is duplicated, reordered, or contains an unexpected path.");
        }
        return result;
    }

    private static void ValidateReceiptSourceArray(JsonElement sources, bool isPins)
    {
        HashSet<string> paths = new(StringComparer.Ordinal);
        foreach (JsonElement source in sources.EnumerateArray())
        {
            RequireReceiptObject(source, isPins ? ["path", "role", "sha256"] : ["path", "sha256"]);
            if (!paths.Add(ReceiptString(source, "path")))
                throw new ExtractionException("ReceiptTerminal source inventory contains a duplicate path.");
            if (isPins) _ = ReceiptString(source, "role");
            _ = ReceiptHash(source, "sha256");
        }
    }

    private static void RequireReceiptObject(JsonElement element, string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(names.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new ExtractionException("ReceiptTerminal dependency object has missing, duplicate, or unexpected schema fields.");
    }

    private static void ValidateReceiptStringObject(JsonElement element, string[] names)
    {
        RequireReceiptObject(element, names);
        foreach (string name in names)
        {
            if (name == "leanDependencies")
            {
                JsonElement dependencies = ReceiptArray(element, name);
                ValidateReceiptSourceArray(dependencies, isPins: false);
                if (!dependencies.EnumerateArray().Select(static dependency => ReceiptString(dependency, "path")).SequenceEqual(new[]
                {
                    "tools/Evm/Lean/Eip803x/BlockReceiptGas.lean",
                    "tools/Evm/Lean/Eip803x/Generated/BlockReceiptGasAccountingKernel.lean",
                    "tools/Evm/Lean/Eip803x/Generated/TransactionGasInitializationKernel.lean",
                    "tools/Evm/Lean/Eip803x/Refinement/BlockReceiptGasAccounting.lean",
                    "tools/Evm/Lean/Eip803x/Refinement/TransactionGasInitialization.lean",
                    "tools/Evm/Lean/Eip803x/TransactionGas.lean",
                }, StringComparer.Ordinal)) throw new ExtractionException("ReceiptTerminal accounting proof dependency roster changed.");
            }
            else if (name.EndsWith("Sha256", StringComparison.Ordinal) || name == "sha256") _ = ReceiptHash(element, name);
            else _ = ReceiptString(element, name);
        }
    }

    private static string ReceiptString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new ExtractionException($"ReceiptTerminal dependency field '{name}' must be a nonempty string.");
        return value.GetString()!;
    }

    private static string ReceiptHash(JsonElement element, string name)
    {
        string hash = ReceiptString(element, name);
        if (!IsSha256(hash)) throw new ExtractionException($"ReceiptTerminal dependency field '{name}' is not a SHA-256 digest.");
        return hash;
    }

    private static int ReceiptInteger(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
            throw new ExtractionException($"ReceiptTerminal dependency field '{name}' must be an integer.");
        return result;
    }

    private static JsonElement ReceiptArray(JsonElement element, string name, bool allowEmpty = false)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array ||
            (!allowEmpty && value.GetArrayLength() == 0))
            throw new ExtractionException($"ReceiptTerminal dependency array '{name}' is missing, malformed, or empty.");
        return value;
    }

    private static void ValidateReceiptStrings(JsonElement array)
    {
        foreach (JsonElement value in array.EnumerateArray())
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw new ExtractionException("ReceiptTerminal dependency string array is malformed.");
    }

}
