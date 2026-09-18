// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;

namespace Nethermind.Evm.Lean.OrdinaryEvmCompletionExtractor;

internal static class CompilerReferences
{
    internal const string InventoryPath = SourceAdmission.UpstreamPackagePath + "/Admission/COMPILER_REFERENCE_PINS.json";
    internal const string InventorySha256 = "751d3ffff1427c2d751c8baaa023220cc847d88c839ec41792800f1c9fe57796";
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 256,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    internal static (MetadataReference[] References, ReferenceIdentity[] Identities) Load(string root, SourceAdmission.SourcePins pins)
    {
        byte[] inventoryBytes = File.ReadAllBytes(Within(root, InventoryPath));
        RequireHash(inventoryBytes, InventorySha256, InventoryPath);
        ReferenceInventory inventory = JsonSerializer.Deserialize<ReferenceInventory>(inventoryBytes, JsonOptions)
            ?? throw new AdmissionException("The compiler inventory is empty.");
        if (inventory.SchemaVersion != 1 || inventory.Count != 226 || inventory.References.Length != inventory.Count ||
            inventory.References.Any(static reference => !reference.Selected) ||
            !inventory.References.Select(static reference => reference.Path).SequenceEqual(
                inventory.References.Select(static reference => reference.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))
        {
            throw new AdmissionException("The accepted compiler inventory header changed.");
        }

        Dictionary<string, string> paths = ResolveBuildInputs(root, pins);
        if (!paths.Keys.Order(StringComparer.Ordinal).SequenceEqual(inventory.References.Select(static reference => reference.Path), StringComparer.Ordinal))
        {
            throw new AdmissionException("The live compiler reference path roster changed.");
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
                throw new AdmissionException($"Reference has no metadata: {identity.Path}.");
            }
            MetadataReader metadata = reader.GetMetadataReader();
            string nameFromMetadata = metadata.GetString(metadata.GetAssemblyDefinition().Name);
            string mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToString("D");
            if (nameFromMetadata != identity.AssemblyName || mvid != identity.Mvid)
            {
                throw new AdmissionException($"Compiler assembly identity changed: {identity.Path}.");
            }
            if (identity.Selected)
            {
                selected.Add(MetadataReference.CreateFromImage(bytes, filePath: path));
            }
        }
        return (selected.ToArray(), inventory.References);
    }

    private static Dictionary<string, string> ResolveBuildInputs(string root, SourceAdmission.SourcePins pins)
    {
        ProcessStartInfo start = new("dotnet")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.Environment["SOURCE_DATE_EPOCH"] = "1789035784";
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (string argument in new[]
        {
            "msbuild", "src/Nethermind/Nethermind.Evm/Nethermind.Evm.csproj",
            "-t:ResolveReferences;GenerateAssemblyInfo;GenerateTargetFrameworkMonikerAttribute;GenerateGlobalUsings",
            "-p:Configuration=Release", "-p:SaveDiskSpace=true", "-p:EnableZkEvm=false", "-p:BuildProjectReferences=false",
            "-getItem:ReferencePath,Compile",
            "-getProperty:DefineConstants,AssemblyName,TargetFramework,LangVersion,AllowUnsafeBlocks,CheckForOverflowUnderflow,Nullable,NuGetPackageRoot,NetCoreTargetingPackRoot",
            "-nr:false", "-m:1",
        }) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new AdmissionException("Cannot resolve real EVM compiler inputs.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        Task.WhenAll(output, error).GetAwaiter().GetResult();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new AdmissionException("EVM compiler-input resolution failed: " + error.Result + output.Result);
        using JsonDocument document = JsonDocument.Parse(output.Result);
        JsonElement properties = document.RootElement.GetProperty("Properties");
        string Property(string name) => properties.GetProperty(name).GetString() ?? throw new AdmissionException("Null compiler property: " + name);
        if (Property("AssemblyName") != pins.AssemblyName || Property("TargetFramework") != "net10.0" || Property("LangVersion") != "14.0" ||
            Property("AllowUnsafeBlocks") != "true" || Property("CheckForOverflowUnderflow") != "false" || Property("Nullable") != "enable" ||
            !Property("DefineConstants").Split(';').SequenceEqual(pins.DefineConstants, StringComparer.Ordinal))
        {
            throw new AdmissionException("The real EVM compilation properties changed.");
        }
        JsonElement items = document.RootElement.GetProperty("Items");
        string[] sources = items.GetProperty("Compile").EnumerateArray()
            .Select(item => Path.GetRelativePath(root, item.GetProperty("FullPath").GetString()!).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        string[] expected = pins.Sources.Where(static source => source.Role is "semantic" or "compiler-support" or "compiler-generated")
            .Select(static source => source.Path).Order(StringComparer.Ordinal).ToArray();
        if (!sources.SequenceEqual(expected, StringComparer.Ordinal)) throw new AdmissionException("The actual MSBuild Compile roster changed.");

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
            if (alias is null) throw new AdmissionException("Compiler reference is outside the closed repository/package/framework roots.");
            string identity = alias + path[directory.Length..].Replace('\\', '/');
            if (!paths.TryAdd(identity, path)) throw new AdmissionException("Duplicate resolved compiler reference identity.");
        }
        return paths;
    }

    internal static string Within(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (Path.IsPathRooted(relative) || !full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new AdmissionException($"Path escapes the repository: {relative}.");
        }
        return full;
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static void RequireHash(byte[] bytes, string expected, string path)
    {
        if (Hash(bytes) != expected)
        {
            throw new AdmissionException($"Pinned bytes changed: {path}.");
        }
    }
}
