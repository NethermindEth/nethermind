// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.Specs.Forks;

namespace Evm.Formal;

internal static class CoverageGate
{
    private const int CurrentSchemaVersion = 1;
    private const int OpcodeCount = byte.MaxValue + 1;
    private const string DispatchSourceRelativePath = "src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs";
    private const string PrecompileRegistryRelativePath = "src/Nethermind/Nethermind.Blockchain/EthereumPrecompileProvider.cs";
    private const string PrecompileProjectRelativePath = "src/Nethermind/Nethermind.Evm.Precompiles/Nethermind.Evm.Precompiles.csproj";
    private const string StandardBuildRulesRelativePath = "src/Nethermind/Directory.Build.targets";
    private const string RootBuildPropsRelativePath = "Directory.Build.props";
    private const string EvmSolutionRelativePath = "tools/Evm/Evm.slnx";
    private const string ReleaseSourceRelativePath = "src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs";
    private const string ManifestRelativePath = "tools/Evm/Lean/verification-manifest.json";
    private const string JsonNewLine = "\n";
    private const string StandardBuildVariant = "standard (EnableZkEvm=false)";

    private static readonly Regex LookupAssignment = new(
        @"lookup\[\s*\(int\)\s*Instruction\.(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\]\s*=\s*(?<handler>.*?);",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex LookupWrite = new(
        @"lookup\[(?<index>[^\]]+)\]\s*=",
        RegexOptions.Compiled);

    private static readonly Regex IfBlock = new(
        @"\bif\s*\((?<condition>.*)\)\s*$",
        RegexOptions.Compiled);

    private static readonly Regex IfStatement = new(
        @"^\s*if\s*\((?<condition>.*)\)\s*$",
        RegexOptions.Compiled);

    private static readonly Regex SpecProperty = new(
        @"^spec\.(?<property>[A-Za-z_][A-Za-z0-9_]*)$",
        RegexOptions.Compiled);

    private static readonly Regex SpecFlag = new(
        @"^SpecFlags\.(?<flag>[A-Za-z_][A-Za-z0-9_]*)(?:<.*>)?\(spec\)$",
        RegexOptions.Compiled);

    private static readonly Regex SemanticTarget = new(
        @"EvmInstructions\.(?<target>Op[A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    private static readonly Regex GenericHandler = new(
        @"(?<![A-Za-z0-9_])(?:OpcodeHandler|TerminatingOpcodeHandler)\s*<\s*(?<target>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static int Run(string? repositoryRoot, string? outputPath, TextWriter output, TextWriter error)
    {
        try
        {
            string root = ResolveRepositoryRoot(repositoryRoot);
            CoverageDocument document = BuildDocument(root);
            string json = JsonSerializer.Serialize(document, JsonOptions) + JsonNewLine;
            json = json.Replace("\r\n", JsonNewLine, StringComparison.Ordinal).Replace('\r', '\n');

            if (outputPath is null)
            {
                output.Write(json);
            }
            else
            {
                string fullOutputPath = Path.GetFullPath(outputPath);
                string parent = Path.GetDirectoryName(fullOutputPath)
                    ?? throw new InvalidOperationException($"Coverage output has no parent directory: {outputPath}");

                Directory.CreateDirectory(parent);
                File.WriteAllText(fullOutputPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }

            return 0;
        }
        catch (Exception exception)
        {
            error.WriteLine($"formal coverage: {exception.Message}");
            return 1;
        }
    }

    private static string ResolveRepositoryRoot(string? repositoryRoot)
    {
        string root = repositoryRoot is null
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(repositoryRoot);

        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Repository root does not exist: {root}");

        string manifestPath = GetRepositoryPath(root, ManifestRelativePath);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"Pinned verification manifest is missing: {manifestPath}");

        return root;
    }

    private static CoverageDocument BuildDocument(string repositoryRoot)
    {
        PinnedConfiguration configuration = ReadPinnedConfiguration(repositoryRoot);
        string dispatchPath = GetRepositoryPath(repositoryRoot, DispatchSourceRelativePath);
        string precompileRegistryPath = GetRepositoryPath(repositoryRoot, PrecompileRegistryRelativePath);
        string releasePath = GetRepositoryPath(repositoryRoot, ReleaseSourceRelativePath);

        string dispatchSource = ReadRequiredFile(dispatchPath);
        SourceLocation defaultHandlerSource = GetDefaultHandlerSource(dispatchSource, DispatchSourceRelativePath);
        List<OpcodeEntry> opcodes = BuildOpcodeInventory(dispatchSource, defaultHandlerSource);

        IReleaseSpec spec = Amsterdam.Instance;
        if (!string.Equals(spec.Name, "Amsterdam", StringComparison.Ordinal))
            throw new InvalidOperationException($"Pinned release instance is not Amsterdam: {spec.Name}");

        ValidateStandardBuildSelection(repositoryRoot);
        PrecompileInventory precompiles = BuildPrecompileInventory(repositoryRoot, spec);
        List<SourceFile> sources = BuildSourceInventory(
            repositoryRoot,
            dispatchPath,
            precompileRegistryPath,
            releasePath,
            precompiles);

        CoverageChecks checks = new(
            opcodes.Count == OpcodeCount,
            HasContiguousOpcodeBytes(opcodes),
            AllEnabledOpcodesHaveHandlers(opcodes),
            precompiles.ActiveEntriesRegistered,
            precompiles.AllRegisteredEntriesResolved);
        EnsureChecksPass(checks);

        return new CoverageDocument(
            CurrentSchemaVersion,
            "standard-mainnet-dispatch-and-precompile-inventory",
            "reachability-and-inventory-only",
            configuration,
            new OpcodeCoverage(
                OpcodeCount,
                Count(opcodes, "enabled"),
                Count(opcodes, "disabled"),
                Count(opcodes, "invalid"),
                opcodes.ToArray()),
            precompiles,
            sources.ToArray(),
            checks,
            [
                "This is a source-anchored inventory and reachability check, not a semantic proof of opcode or precompile behavior.",
                $"Precompile source locations follow the standard Evm.slnx compile selection ({StandardBuildVariant}); zkEVM alternate partials are excluded.",
                "The pinned Amsterdam spec is enabled at synthetic timestamp 0 / block 0; this is not a claim about currently scheduled live-mainnet activation.",
                "Frame, transaction, block, cryptographic, state-root, and parallel-execution refinement remain outside this gate."
            ]);
    }

    private static void EnsureChecksPass(CoverageChecks checks)
    {
        if (!checks.AllOpcodeBytesEnumerated ||
            !checks.OpcodeBytesContiguous ||
            !checks.EnabledOpcodesHaveHandlers ||
            !checks.ActivePrecompilesRegistered ||
            !checks.RegisteredPrecompilesResolved)
        {
            throw new InvalidOperationException("Standard-mainnet coverage checks did not pass.");
        }
    }

    private static void ValidateStandardBuildSelection(string repositoryRoot)
    {
        string buildRules = ReadRequiredFile(GetRepositoryPath(repositoryRoot, StandardBuildRulesRelativePath));
        const string standardCondition = "<ItemGroup Condition=\"'$(EnableZkEvm)' != 'true'\">";
        if (!buildRules.Contains(standardCondition, StringComparison.Ordinal) ||
            !buildRules.Contains("<Compile Remove=\"**/zkevm/**/*.cs\" />", StringComparison.Ordinal) ||
            !buildRules.Contains("<Compile Remove=\"**/*.zkevm.cs\" />", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Standard Evm.slnx compile selection no longer excludes zkEVM precompile partials.");
        }
    }

    private static PinnedConfiguration ReadPinnedConfiguration(string repositoryRoot)
    {
        string manifestPath = GetRepositoryPath(repositoryRoot, ManifestRelativePath);
        using JsonDocument document = JsonDocument.Parse(ReadRequiredFile(manifestPath));
        JsonElement root = document.RootElement;
        int schemaVersion = root.GetProperty("schemaVersion").GetInt32();
        if (schemaVersion != CurrentSchemaVersion)
            throw new InvalidOperationException($"Unsupported verification manifest schema version: {schemaVersion}");

        JsonElement pins = root.GetProperty("pins");
        string commit = pins.GetProperty("nethermindCommit").GetString() ?? string.Empty;
        if (!Regex.IsMatch(commit, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Verification manifest has an invalid Nethermind commit pin.");

        JsonElement fork = pins.GetProperty("fork");
        string chain = fork.GetProperty("chain").GetString() ?? string.Empty;
        string release = fork.GetProperty("release").GetString() ?? string.Empty;
        string activation = fork.GetProperty("activation").GetString() ?? string.Empty;
        if (!string.Equals(chain, "mainnet", StringComparison.Ordinal) ||
            !string.Equals(release, "Amsterdam", StringComparison.Ordinal) ||
            !string.Equals(activation, "synthetic timestamp 0 / block 0", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Coverage gate requires the pinned mainnet Amsterdam configuration at synthetic timestamp 0 / block 0.");
        }

        return new PinnedConfiguration(chain, release, activation, commit);
    }

    private static List<OpcodeEntry> BuildOpcodeInventory(string source, SourceLocation defaultHandlerSource)
    {
        ValidateLookupWrites(source);

        MatchCollection matches = LookupAssignment.Matches(source);
        Dictionary<byte, OpcodeAssignment> assignments = [];
        foreach (Match match in matches)
        {
            string name = match.Groups["name"].Value;
            if (!Enum.TryParse(name, ignoreCase: false, out Instruction instruction))
                throw new InvalidOperationException($"Dispatch source names an unknown instruction: {name}");

            byte opcode = (byte)instruction;
            if (!assignments.TryAdd(opcode, new OpcodeAssignment(
                    instruction,
                    NormalizeExpression(match.Groups["handler"].Value),
                    GetLineNumber(source, match.Index),
                    GetConditions(source, match.Index))))
            {
                throw new InvalidOperationException($"Dispatch source assigns opcode 0x{opcode:X2} more than once.");
            }
        }

        Dictionary<byte, Instruction> enumValues = [];
        foreach (Instruction instruction in Enum.GetValues<Instruction>())
        {
            byte opcode = (byte)instruction;
            if (!enumValues.TryAdd(opcode, instruction))
                throw new InvalidOperationException($"Instruction enum reuses opcode 0x{opcode:X2}.");

            if (!assignments.ContainsKey(opcode))
                throw new InvalidOperationException(
                    $"Instruction.{instruction} (0x{opcode:X2}) has no source dispatch assignment.");
        }

        List<OpcodeEntry> entries = new(OpcodeCount);
        for (int opcode = 0; opcode < OpcodeCount; opcode++)
        {
            byte byteValue = checked((byte)opcode);
            if (!assignments.TryGetValue(byteValue, out OpcodeAssignment? assignment))
            {
                entries.Add(new OpcodeEntry(
                    byteValue,
                    FormatHex(byteValue),
                    null,
                    "invalid",
                    new HandlerInventory(
                        "badInstruction",
                        ["BadInstructionOpcode"],
                        defaultHandlerSource)));
                continue;
            }

            bool enabled = true;
            foreach (string condition in assignment.Conditions)
                enabled &= EvaluateCondition(condition, Amsterdam.Instance);

            string status = assignment.Instruction switch
            {
                Instruction.INVALID => "invalid",
                _ when enabled => "enabled",
                _ => "disabled",
            };
            string[] targets = ExtractSemanticTargets(assignment.HandlerExpression);
            if (targets.Length == 0)
                throw new InvalidOperationException(
                    $"Dispatch handler for Instruction.{assignment.Instruction} has no source target at line {assignment.Line}.");

            entries.Add(new OpcodeEntry(
                byteValue,
                FormatHex(byteValue),
                assignment.Instruction.ToString(),
                status,
                new HandlerInventory(
                    assignment.HandlerExpression,
                    targets,
                    new SourceLocation(DispatchSourceRelativePath, assignment.Line))));
        }

        if (entries.Count != OpcodeCount)
            throw new InvalidOperationException($"Expected {OpcodeCount} opcode entries, found {entries.Count}.");

        return entries;
    }

    private static void ValidateLookupWrites(string source)
    {
        MatchCollection writes = LookupWrite.Matches(source);
        int defaultAssignments = 0;
        foreach (Match write in writes)
        {
            string index = NormalizeExpression(write.Groups["index"].Value);
            if (string.Equals(index, "i", StringComparison.Ordinal))
            {
                defaultAssignments++;
                continue;
            }

            if (!Regex.IsMatch(index, @"^\(int\)Instruction\.[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant))
                throw new InvalidOperationException($"Unsupported opcode table write: lookup[{index}].");
        }

        if (defaultAssignments != 1 || !source.Contains("lookup[i] = badInstruction;", StringComparison.Ordinal))
            throw new InvalidOperationException("Dispatch source must initialize every opcode slot to badInstruction exactly once.");
    }

    private static SourceLocation GetDefaultHandlerSource(string source, string relativePath)
    {
        Match match = Regex.Match(source, @"lookup\[\s*i\s*\]\s*=\s*badInstruction\s*;", RegexOptions.CultureInvariant);
        if (!match.Success)
            throw new InvalidOperationException("Dispatch source has no badInstruction table initialization.");

        return new SourceLocation(relativePath, GetLineNumber(source, match.Index));
    }

    private static IReadOnlyList<string> GetConditions(string source, int position)
    {
        List<string> conditions = [];
        List<string?> braces = [];
        for (int index = 0; index < position; index++)
        {
            char character = source[index];
            if (character == '{')
            {
                int lineStart = source.LastIndexOf('\n', index - 1) + 1;
                string linePrefix = source[lineStart..index].Trim();
                Match condition = IfBlock.Match(linePrefix);
                braces.Add(condition.Success ? condition.Groups["condition"].Value : null);
            }
            else if (character == '}' && braces.Count > 0)
            {
                braces.RemoveAt(braces.Count - 1);
            }
        }

        foreach (string? condition in braces)
        {
            if (condition is not null)
                conditions.Add(condition);
        }

        int assignmentLineStart = source.LastIndexOf('\n', position - 1) + 1;
        int previousLineEnd = assignmentLineStart - 1;
        while (previousLineEnd >= 0 && (source[previousLineEnd] == '\r' || source[previousLineEnd] == '\n'))
            previousLineEnd--;
        if (previousLineEnd >= 0)
        {
            int previousLineStart = source.LastIndexOf('\n', previousLineEnd) + 1;
            string previousLine = source[previousLineStart..(previousLineEnd + 1)];
            Match condition = IfStatement.Match(previousLine);
            if (condition.Success)
                conditions.Add(condition.Groups["condition"].Value);
        }

        return conditions;
    }

    private static bool EvaluateCondition(string condition, IReleaseSpec spec)
    {
        string expression = Regex.Replace(condition, @"\s+", string.Empty, RegexOptions.CultureInvariant);
        bool negate = expression.StartsWith('!');
        if (negate)
            expression = expression[1..];

        bool value;
        Match property = SpecProperty.Match(expression);
        if (property.Success)
        {
            string propertyName = property.Groups["property"].Value;
            value = propertyName switch
            {
                "ShiftOpcodesEnabled" => spec.IsEip145Enabled,
                "CLZEnabled" => spec.IsEip7939Enabled,
                "ReturnDataOpcodesEnabled" => spec.IsEip211Enabled,
                "ChainIdOpcodeEnabled" => spec.IsEip1344Enabled,
                "SelfBalanceOpcodeEnabled" => spec.IsEip1884Enabled,
                "BaseFeeEnabled" => spec.IsEip3198Enabled,
                "BlobBaseFeeEnabled" => spec.IsEip4844Enabled,
                "TransientStorageEnabled" => spec.IsEip1153Enabled,
                "MCopyIncluded" => spec.IsEip5656Enabled,
                "IncludePush0Instruction" => spec.IsEip3855Enabled,
                "DelegateCallEnabled" => spec.IsEip7Enabled,
                "Create2OpcodeEnabled" => spec.IsEip1014Enabled,
                "StaticCallEnabled" => spec.IsEip214Enabled,
                "RevertOpcodeEnabled" => spec.IsEip140Enabled,
                "ExtCodeHashOpcodeEnabled" => spec.IsEip1052Enabled,
                _ => ReadBooleanSpecProperty(propertyName, spec, condition),
            };
        }
        else
        {
            Match flag = SpecFlag.Match(expression);
            if (!flag.Success)
                throw new InvalidOperationException($"Unsupported dispatch guard: {condition}");

            value = flag.Groups["flag"].Value switch
            {
                "Eip150" => spec.IsEip150Enabled,
                "Eip158" => spec.IsEip158Enabled,
                "Eip160" => spec.IsEip160Enabled,
                "Eip2929" => spec.IsEip2929Enabled,
                "Eip3860" => spec.IsEip3860Enabled,
                "Eip6780" => spec.IsEip6780Enabled,
                "Eip8037" => spec.IsEip8037Enabled,
                "Eip8038" => spec.IsEip8038Enabled,
                "Eip7708" => spec.IsEip7708Enabled,
                "Eip8246" => spec.IsEip8246Enabled,
                _ => throw new InvalidOperationException($"Unsupported dispatch guard: {condition}"),
            };
        }

        return negate ? !value : value;
    }

    private static bool ReadBooleanSpecProperty(string propertyName, IReleaseSpec spec, string condition)
    {
        PropertyInfo? propertyInfo = typeof(IReleaseSpec).GetProperty(propertyName);
        if (propertyInfo?.PropertyType != typeof(bool))
            throw new InvalidOperationException($"Unsupported dispatch guard: {condition}");

        return (bool)propertyInfo.GetValue(spec)!;
    }

    private static string[] ExtractSemanticTargets(string handlerExpression)
    {
        List<string> targets = [];
        foreach (Match match in SemanticTarget.Matches(handlerExpression))
            if (!targets.Contains(match.Groups["target"].Value, StringComparer.Ordinal))
                targets.Add(match.Groups["target"].Value);

        if (targets.Count == 0)
        {
            Match generic = GenericHandler.Match(handlerExpression);
            if (generic.Success)
                targets.Add(generic.Groups["target"].Value);
        }

        if (targets.Count == 0)
        {
            int openParen = handlerExpression.IndexOf('(');
            string handlerName = openParen < 0 ? handlerExpression : handlerExpression[..openParen];
            int genericStart = handlerName.IndexOf('<');
            if (genericStart >= 0)
                handlerName = handlerName[..genericStart];
            if (handlerName.EndsWith("Handler", StringComparison.Ordinal))
                targets.Add(handlerName);
        }

        return targets.ToArray();
    }

    private static PrecompileInventory BuildPrecompileInventory(string repositoryRoot, IReleaseSpec spec)
    {
        EthereumPrecompileProvider provider = new();
        FrozenDictionary<AddressAsKey, CodeInfo> registered = provider.GetPrecompiles();
        HashSet<AddressAsKey> active = [];
        foreach (AddressAsKey address in spec.Precompiles)
            active.Add(address);

        List<PrecompileEntry> entries = [];
        foreach (KeyValuePair<AddressAsKey, CodeInfo> registration in registered)
        {
            Address address = registration.Key;
            IPrecompile precompile = registration.Value.Precompile
                ?? throw new InvalidOperationException($"Precompile registration at {address} has no implementation.");
            string typeName = precompile.GetType().FullName
                ?? throw new InvalidOperationException($"Precompile at {address} has no implementation type.");
            List<SourceLocation> implementationSources = FindImplementationSources(repositoryRoot, precompile.GetType().Name);
            entries.Add(new PrecompileEntry(
                address.ToString(),
                precompile.Name,
                active.Contains(registration.Key) ? "enabled" : "disabled",
                typeName,
                implementationSources.ToArray()));
        }

        entries.Sort(static (left, right) => string.CompareOrdinal(left.Address, right.Address));
        foreach (AddressAsKey addressKey in active)
        {
            Address address = addressKey;
            if (!registered.ContainsKey(addressKey))
                throw new InvalidOperationException($"Active Amsterdam precompile {address} is not registered by EthereumPrecompileProvider.");
        }

        bool allRegisteredEntriesResolved = entries.Count == registered.Count && entries.TrueForAll(static entry =>
            entry.Address.StartsWith("0x", StringComparison.Ordinal) &&
            entry.Name.Length != 0 &&
            entry.ImplementationSources.Length != 0);
        if (!allRegisteredEntriesResolved)
            throw new InvalidOperationException("One or more standard precompile registrations could not be resolved.");

        return new PrecompileInventory(
            registered.Count,
            active.Count,
            entries.Count(static entry => string.Equals(entry.Status, "enabled", StringComparison.Ordinal)),
            allRegisteredEntriesResolved,
            true,
            entries.ToArray());
    }

    private static List<SourceLocation> FindImplementationSources(string repositoryRoot, string typeName)
    {
        string sourceRoot = GetRepositoryPath(repositoryRoot, "src/Nethermind/Nethermind.Evm.Precompiles");
        List<SourceLocation> locations = [];
        foreach (string path in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            string relativePath = ToRelativePath(repositoryRoot, path);
            if (!IsStandardBuildSource(relativePath))
                continue;

            string[] lines = File.ReadAllLines(path);
            for (int index = 0; index < lines.Length; index++)
            {
                if (Regex.IsMatch(lines[index], $@"\bclass\s+{Regex.Escape(typeName)}\b", RegexOptions.CultureInvariant))
                {
                    locations.Add(new SourceLocation(
                        relativePath,
                        index + 1));
                    break;
                }
            }
        }

        locations.Sort(static (left, right) =>
        {
            int pathComparison = string.CompareOrdinal(left.Path, right.Path);
            return pathComparison != 0 ? pathComparison : left.Line.CompareTo(right.Line);
        });
        if (locations.Count == 0)
            throw new InvalidOperationException($"No source implementation found for precompile {typeName}.");

        return locations;
    }

    private static bool IsStandardBuildSource(string relativePath)
    {
        string normalizedPath = relativePath.Replace('\\', '/');
        string fileName = Path.GetFileName(normalizedPath);
        return !normalizedPath.Contains("/zkevm/", StringComparison.OrdinalIgnoreCase) &&
            !fileName.EndsWith(".zkevm.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static List<SourceFile> BuildSourceInventory(
        string repositoryRoot,
        string dispatchPath,
        string precompileRegistryPath,
        string releasePath,
        PrecompileInventory precompiles)
    {
        SortedSet<string> paths = new(StringComparer.Ordinal)
        {
            ToRelativePath(repositoryRoot, dispatchPath),
            ToRelativePath(repositoryRoot, precompileRegistryPath),
            ToRelativePath(repositoryRoot, releasePath),
            StandardBuildRulesRelativePath,
            RootBuildPropsRelativePath,
            PrecompileProjectRelativePath,
            EvmSolutionRelativePath,
        };
        foreach (PrecompileEntry entry in precompiles.Entries)
            foreach (SourceLocation source in entry.ImplementationSources)
                paths.Add(source.Path);

        List<SourceFile> files = [];
        foreach (string path in paths)
        {
            string fullPath = GetRepositoryPath(repositoryRoot, path);
            files.Add(new SourceFile(path, ComputeSha256(fullPath)));
        }

        return files;
    }

    private static int Count(List<OpcodeEntry> entries, string status)
    {
        int count = 0;
        foreach (OpcodeEntry entry in entries)
            if (string.Equals(entry.Status, status, StringComparison.Ordinal))
                count++;
        return count;
    }

    private static bool HasContiguousOpcodeBytes(List<OpcodeEntry> entries)
    {
        for (int index = 0; index < entries.Count; index++)
            if (entries[index].Byte != index)
                return false;
        return true;
    }

    private static bool AllEnabledOpcodesHaveHandlers(List<OpcodeEntry> entries)
    {
        foreach (OpcodeEntry entry in entries)
            if (string.Equals(entry.Status, "enabled", StringComparison.Ordinal) && entry.Handler is null)
                return false;
        return true;
    }

    private static string ReadRequiredFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Required coverage source is missing: {path}");
        return File.ReadAllText(path);
    }

    private static string GetRepositoryPath(string repositoryRoot, string relativePath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string fullRoot = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Coverage path escapes repository root: {relativePath}");
        return fullPath;
    }

    private static string ToRelativePath(string repositoryRoot, string path)
        => Path.GetRelativePath(repositoryRoot, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string ComputeSha256(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static int GetLineNumber(string source, int position)
    {
        int line = 1;
        for (int index = 0; index < position; index++)
            if (source[index] == '\n')
                line++;
        return line;
    }

    private static string NormalizeExpression(string expression)
        => Regex.Replace(expression, @"\s+", " ", RegexOptions.CultureInvariant).Trim();

    private static string FormatHex(byte opcode) => $"0x{opcode:X2}";

    private sealed record PinnedConfiguration(string Chain, string Release, string Activation, string NethermindCommit);

    private sealed record OpcodeCoverage(
        int ByteCount,
        int EnabledCount,
        int DisabledCount,
        int InvalidCount,
        OpcodeEntry[] Entries);

    private sealed record OpcodeEntry(
        int Byte,
        string Hex,
        string? Name,
        string Status,
        HandlerInventory? Handler);

    private sealed record HandlerInventory(
        string DispatchExpression,
        string[] SemanticTargets,
        SourceLocation Source);

    private sealed record PrecompileInventory(
        int RegisteredCount,
        int ActiveCount,
        int EnabledCount,
        bool AllRegisteredEntriesResolved,
        bool ActiveEntriesRegistered,
        PrecompileEntry[] Entries);

    private sealed record PrecompileEntry(
        string Address,
        string Name,
        string Status,
        string ImplementationType,
        SourceLocation[] ImplementationSources);

    private sealed record CoverageChecks(
        bool AllOpcodeBytesEnumerated,
        bool OpcodeBytesContiguous,
        bool EnabledOpcodesHaveHandlers,
        bool ActivePrecompilesRegistered,
        bool RegisteredPrecompilesResolved);

    private sealed record SourceFile(string Path, string Sha256);

    private sealed record SourceLocation(string Path, int Line);

    private sealed record OpcodeAssignment(
        Instruction Instruction,
        string HandlerExpression,
        int Line,
        IReadOnlyList<string> Conditions);

    private sealed record CoverageDocument(
        int SchemaVersion,
        string Claim,
        string Scope,
        PinnedConfiguration Target,
        OpcodeCoverage Opcodes,
        PrecompileInventory Precompiles,
        SourceFile[] Sources,
        CoverageChecks Checks,
        string[] Limitations);
}
