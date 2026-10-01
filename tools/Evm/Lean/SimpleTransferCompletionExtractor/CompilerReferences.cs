// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;

namespace Nethermind.Evm.Lean.SimpleTransferCompletionExtractor;

internal sealed record CompilerSourcePins(int SchemaVersion, string AssemblyName, string Configuration, bool EnableZkEvm, string[] DefineConstants, SourceIdentity[] Sources);
internal sealed record ReferenceIdentity(string Path, string AssemblyName, string Sha256, string Mvid, bool Selected);
internal sealed record ReferenceInventory(int SchemaVersion, int Count, ReferenceIdentity[] References);

internal static class CompilerReferences
{
    internal const string SourceDateEpoch = "1789035784";
    internal const string InventoryPath = "tools/Evm/Lean/OrdinaryTransactionRefundAdapterExtractor/Admission/COMPILER_REFERENCE_PINS.json";
    internal const string InventorySha256 = "b36eeb0db516d68842159d34b9b9b730e83460e385de87dc35110db9a756b84c";
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    internal static (MetadataReference[] References, ReferenceIdentity[] Identities) Load(string root, CompilerSourcePins pins)
    {
        byte[] inventoryBytes = File.ReadAllBytes(Within(root, InventoryPath));
        RequireHash(inventoryBytes, InventorySha256, InventoryPath);
        ReferenceInventory inventory = JsonSerializer.Deserialize<ReferenceInventory>(inventoryBytes, JsonOptions)
            ?? throw new ExtractionException("The compiler inventory is empty.");
        if (inventory.SchemaVersion != 1 || inventory.Count != 226 || inventory.References.Length != inventory.Count ||
            inventory.References.Any(static reference => !reference.Selected) ||
            !inventory.References.Select(static reference => reference.Path).SequenceEqual(
                inventory.References.Select(static reference => reference.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))
        {
            throw new ExtractionException("The accepted compiler inventory header changed.");
        }

        Dictionary<string, string> paths = ResolveBuildInputs(root, pins);
        if (!paths.Keys.Order(StringComparer.Ordinal).SequenceEqual(inventory.References.Select(static reference => reference.Path), StringComparer.Ordinal))
        {
            throw new ExtractionException("The live compiler reference path roster changed.");
        }

        List<MetadataReference> selected = [];
        foreach (ReferenceIdentity identity in inventory.References)
        {
            string path = paths[identity.Path];
            byte[] bytes = File.ReadAllBytes(path);
            RequireHash(bytes, identity.Sha256, identity.Path);
            using MemoryStream stream = new(bytes, writable: false);
            using PEReader reader = new(stream);
            if (!reader.HasMetadata)
            {
                throw new ExtractionException($"Reference has no metadata: {identity.Path}.");
            }
            MetadataReader metadata = reader.GetMetadataReader();
            string nameFromMetadata = metadata.GetString(metadata.GetAssemblyDefinition().Name);
            string mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToString("D");
            if (nameFromMetadata != identity.AssemblyName || mvid != identity.Mvid)
            {
                throw new ExtractionException($"Compiler assembly identity changed: {identity.Path}.");
            }
            if (identity.Selected)
            {
                selected.Add(MetadataReference.CreateFromImage(bytes, filePath: path));
            }
        }
        return (selected.ToArray(), inventory.References);
    }

    private static Dictionary<string, string> ResolveBuildInputs(string root, CompilerSourcePins pins)
    {
        string canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (canonicalRoot.IndexOfAny([',', ';', '=']) >= 0)
        {
            throw new ExtractionException($"The repository path cannot be represented by PathMap: {canonicalRoot}.");
        }
        string sourceRevisionId = ReadSourceRevisionId(root);
        string pathMap = canonicalRoot + "=/_/";
        ProcessStartInfo start = new("dotnet")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.Environment["SOURCE_DATE_EPOCH"] = SourceDateEpoch;
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["SourceRevisionId"] = sourceRevisionId;
        start.Environment["SourceDateEpoch"] = SourceDateEpoch;
        start.Environment["EnableSourceLink"] = "false";
        start.Environment["EmbedUntrackedSources"] = "false";
        start.Environment["ContinuousIntegrationBuild"] = "false";
        start.Environment["BuildingInsideVisualStudio"] = "false";
        start.Environment["Deterministic"] = "true";
        start.Environment["DeterministicSourcePaths"] = "true";
        start.Environment["PathMap"] = pathMap;
        foreach (string argument in new[]
        {
            "msbuild", "src/Nethermind/Nethermind.Evm/Nethermind.Evm.csproj",
            "-t:ResolveReferences;GenerateAssemblyInfo;GenerateTargetFrameworkMonikerAttribute;GenerateGlobalUsings",
            "-p:Configuration=Release", "-p:EnableZkEvm=false", "-p:BuildProjectReferences=false", "-p:SaveDiskSpace=true",
            $"-p:SourceRevisionId={sourceRevisionId}", $"-p:SourceDateEpoch={SourceDateEpoch}",
            "-p:EnableSourceLink=false", "-p:EmbedUntrackedSources=false", "-p:ContinuousIntegrationBuild=false", "-p:BuildingInsideVisualStudio=false",
            "-p:Deterministic=true", "-p:DeterministicSourcePaths=true", $"-p:PathMap={pathMap}",
            "-getItem:ReferencePath,Compile",
            "-getProperty:DefineConstants,AssemblyName,TargetFramework,LangVersion,AllowUnsafeBlocks,CheckForOverflowUnderflow,Nullable,NuGetPackageRoot,NetCoreTargetingPackRoot,SourceRevisionId,SourceDateEpoch,EnableSourceLink,EmbedUntrackedSources,ContinuousIntegrationBuild,BuildingInsideVisualStudio,Deterministic,DeterministicSourcePaths,PathMap",
            "-nr:false", "-m:1",
        }) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new ExtractionException("Cannot resolve real EVM compiler inputs.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        Task.WhenAll(output, error).GetAwaiter().GetResult();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new ExtractionException("EVM compiler-input resolution failed: " + error.Result + output.Result);
        using JsonDocument document = JsonDocument.Parse(output.Result);
        JsonElement properties = document.RootElement.GetProperty("Properties");
        string Property(string name) => properties.GetProperty(name).GetString() ?? throw new ExtractionException("Null compiler property: " + name);
        if (Property("AssemblyName") != pins.AssemblyName || Property("TargetFramework") != "net10.0" || Property("LangVersion") != "14.0" ||
            Property("AllowUnsafeBlocks") != "true" || Property("CheckForOverflowUnderflow") != "false" || Property("Nullable") != "enable" ||
            Property("SourceRevisionId") != sourceRevisionId || Property("SourceDateEpoch") != SourceDateEpoch ||
            Property("EnableSourceLink") != "false" || Property("EmbedUntrackedSources") != "false" ||
            Property("ContinuousIntegrationBuild") != "false" || Property("BuildingInsideVisualStudio") != "false" || Property("Deterministic") != "true" ||
            Property("DeterministicSourcePaths") != "true" || Property("PathMap") != pathMap ||
            !Property("DefineConstants").Split(';').SequenceEqual(pins.DefineConstants, StringComparer.Ordinal))
        {
            throw new ExtractionException("The real EVM compilation properties changed.");
        }
        JsonElement items = document.RootElement.GetProperty("Items");
        string[] sources = items.GetProperty("Compile").EnumerateArray()
            .Select(item => Path.GetRelativePath(root, item.GetProperty("FullPath").GetString()!).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        string[] expected = pins.Sources.Where(static source => source.Role is "semantic" or "compiler-support" or "compiler-generated")
            .Select(static source => source.Path).Order(StringComparer.Ordinal).ToArray();
        if (!sources.SequenceEqual(expected, StringComparer.Ordinal)) throw new ExtractionException("The actual MSBuild Compile roster changed.");

        (string Alias, string Directory)[] roots =
        [
            ("repo/", Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar),
            ("nuget/", Path.GetFullPath(Property("NuGetPackageRoot")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar),
            ("framework/", Path.GetFullPath(Property("NetCoreTargetingPackRoot")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar),
        ];
        Dictionary<string, string> paths = new(StringComparer.Ordinal);
        foreach (JsonElement item in items.GetProperty("ReferencePath").EnumerateArray())
        {
            string path = Path.GetFullPath(item.GetProperty("FullPath").GetString()!);
            (string alias, string directory) = roots.SingleOrDefault(pair => path.StartsWith(pair.Directory, StringComparison.OrdinalIgnoreCase));
            if (alias is null) throw new ExtractionException("Compiler reference is outside the closed repository/package/framework roots.");
            string identity = alias + path[directory.Length..].Replace('\\', '/');
            if (!paths.TryAdd(identity, path)) throw new ExtractionException("Duplicate resolved compiler reference identity.");
        }
        return paths;
    }

    private static string ReadSourceRevisionId(string root)
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Within(root, "tools/Evm/Lean/verification-manifest.json")));
        JsonElement document = manifest.RootElement;
        string? revision = document.GetProperty("pins").GetProperty("nethermindCommit").GetString();
        if (document.GetProperty("schemaVersion").GetInt32() != 1 || revision is null || revision.Length != 40 ||
            revision.Any(static character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ExtractionException("The verification manifest has an invalid Nethermind commit pin.");
        }
        return revision;
    }

    internal static string Within(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (Path.IsPathRooted(relative) || !full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ExtractionException($"Path escapes the repository: {relative}.");
        }
        return full;
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static void RequireHash(byte[] bytes, string expected, string path)
    {
        if (Hash(bytes) != expected)
        {
            throw new ExtractionException($"Pinned bytes changed: {path}.");
        }
    }
}
