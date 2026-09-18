// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.Keccak256OpcodeExtractor;

internal static class Keccak256OpcodeProfile
{
    internal const int SchemaVersion = 1;
    internal const string ExtractorVersion = "1.0.0";
    internal const string IrFileName = "Keccak256OpcodeKernel.ir.json";
    internal const string ManifestFileName = "Keccak256OpcodeKernel.source-manifest.json";
    internal const string DefaultLeanRelativePath = "tools/Evm/Lean/Keccak256OpcodeExtractor/Generated/Keccak256OpcodeKernel.lean";

    private const string Kernel = "Nethermind standard-mainnet Amsterdam KECCAK256 immediate-handler slice";
    private const string ExpectedRoslynVersion = "5.6.0.0";
    private const string ExpectedIrSha256 = "6d32c82548c9768a084893c0401a83105d95f5eed6aa5714d8dfa64ecfa14667";
    private const string ExpectedLeanSha256 = "93172be78521facdfb03fb6be61d94ad4890ce18352e4cc92eb978baaa2e0f78";
    private const string ExpectedCombinedSourceSha256 = "91020ccf6f718b948c79e8e57ce6bfa0b3ed2362fb3d0d691398da777e766534";
    private const string Root = "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>";
    private const string BuildTargetsPath = "src/Nethermind/Directory.Build.targets";
    private const string BuildTargetsSha256 = "0598cebaef1df41102a18a3f9ace810bed1e4e64b8471d055724b4a394dccec6";
    private const string InstructionPath = "src/Nethermind/Nethermind.Evm/Instruction.cs";
    private const string GasCostPath = "src/Nethermind/Nethermind.Core/GasCostOf.cs";
    private const string TypeFlagsPath = "src/Nethermind/Nethermind.Core/TypeFlags.cs";
    private const string HandlerPath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Crypto.cs";
    private const string CalculationPath = "src/Nethermind/Nethermind.Evm/Instructions/EvmCalculations.cs";
    private const string InterfaceGasPath = "src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs";
    private const string GasPath = "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs";
    private const string MemoryPath = "src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs";
    private const string StackPath = "src/Nethermind/Nethermind.Evm/EvmStack.cs";
    private const string OpcodePath = "src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs";
    private const string DispatchPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs";
    private const string VmPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.cs";
    private const string VmStandardPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.std.cs";
    private const string DispatchFlagsPath = "src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs";
    private const string TracePath = "src/Nethermind/Nethermind.Evm/Tracing/TracerExtensions.cs";
    private const string CachePath = "src/Nethermind/Nethermind.Core/Crypto/KeccakCache.std.cs";
    private const string ModulePath = "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs";
    private const string NamedReleasePath = "src/Nethermind/Nethermind.Specs/Forks/NamedReleaseSpec.cs";

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp14);
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
    };

    private static readonly DispatchTableBinding[] DispatchTables =
    [
        new("NoTrace", "OffFlag", "OffFlag"),
        new("NoTraceCancelable", "OffFlag", "OnFlag"),
        new("Traced", "OnFlag", "OffFlag"),
        new("TracedCancelable", "OnFlag", "OnFlag"),
    ];

    private static readonly string[] EffectOrder =
    [
        "traceStartIfInstructionTracing",
        "incrementProgramCounter",
        "incrementOpcodeCount",
        "popOffsetLengthAtomically",
        "calculateCheckedCeilingWords",
        "chargeBaseAndWords",
        "rejectWordOverflowAfterCharge",
        "installLogicalMemoryExpansion",
        "chargeMemoryExpansion",
        "loadExactZeroExtendedSlice",
        "callHashOracle",
        "checkPushDepth",
        "traceHashPushIfInstructionTracing",
        "replaceTwoStackInputsWithHash",
        "traceEndIfInstructionTracing",
        "dispatchContinue",
    ];

    private static readonly string[] ForkLineage =
    [
        "Olympic", "Frontier", "Homestead", "Dao", "TangerineWhistle", "SpuriousDragon",
        "Byzantium", "Constantinople", "ConstantinopleFix", "Istanbul", "MuirGlacier", "Berlin",
        "London", "ArrowGlacier", "GrayGlacier", "Paris", "Shanghai", "Cancun", "Prague", "Osaka",
        "BPO1", "BPO2", "Amsterdam",
    ];

    private static readonly string[] ForkFiles =
    [
        "00_Olympic", "01_Frontier", "02_Homestead", "03_Dao", "04_TangerineWhistle",
        "05_SpuriousDragon", "06_Byzantium", "07_Constantinople", "08_ConstantinopleFix",
        "09_Istanbul", "10_MuirGlacier", "11_Berlin", "12_London", "13_ArrowGlacier",
        "14_GrayGlacier", "15_Paris", "16_Shanghai", "17_Cancun", "18_Prague", "19_Osaka",
        "20_BPO1", "21_BPO2", "25_Amsterdam",
    ];

    private static readonly string[] OpenExtractionObligations =
    [
        "The hash is an oracle: no equivalence claim covers Keccak, ValueKeccak, KeccakCache, cache synchronization, native code, or CLR representation.",
        "Roslyn syntax admission does not prove C# compilation, CLR/JIT/AOT execution, function-pointer tail calls, or hardware behavior.",
        "The logical model assumes the admitted UInt256, ValueHash256, unsafe stack, and memory adapters represent the shared Lean words and zero-extended bytes.",
        "Backing allocation, pooling, fresh-array initialization, aliasing, allocation failure, and optional trace payload callbacks or exceptions remain outside this immediate opcode step.",
        "Cancellation polling, counter overflow, outer terminal-opcode and error trace closure, exceptional-frame settlement, rollback, transaction composition, and persistence remain outside this projection.",
    ];

    private static readonly string[] SemanticBindings =
    [
        "IVirtualMachine=>EthereumVirtualMachine",
        "EthereumVirtualMachine=>VirtualMachine<EthereumGasPolicy>",
        "fork=>Amsterdam.Instance",
        "opcode=>Instruction.KECCAK256:0x20",
        "handler=>KeccakOpcode<TTracingInst>",
        "target=>EvmInstructions.InstructionKeccak256<TGasPolicy,TTracingInst>",
        "hash=>KeccakCache.ComputeTo:oracle-only",
        "dispatch=>NoTrace,NoTraceCancelable,Traced,TracedCancelable",
        "build=>EnableZkEvm!=true selects *.std.cs",
    ];

    private static readonly AdmissionSpec[] AdmissionSpecs = BuildAdmissionSpecs();

    // Complete-file pins deliberately make trivia, directives, and unselected competing declarations part of the admission.
    private static readonly IReadOnlyDictionary<string, string> ExpectedSourceSha256 = BuildExpectedSourceSha256();
    private static readonly IReadOnlyDictionary<string, string> ExpectedAdmissionSha256 = BuildExpectedAdmissionSha256();

    internal static IReadOnlyList<string> SourcePaths => AdmissionSpecs.Select(static spec => spec.SourcePath)
        .Distinct(StringComparer.Ordinal).OrderBy(static path => path, StringComparer.Ordinal).ToArray();

    internal static IReadOnlyList<string> InputPaths => [.. SourcePaths, BuildTargetsPath];

    internal static ExtractionResult Extract(string repoRoot, string outputDirectory, string? leanOutputPath = null)
    {
        string root = Path.GetFullPath(repoRoot);
        ValidatePinnedProfile();
        Dictionary<string, SourceFile> sources = LoadSources(root);
        RawSourceIdentity build = LoadBuildTargets(root);
        ValidateBuildFlavor(root);
        List<AdmissionIdentity> admissions = AdmitSyntax(sources);
        ValidateNoCompetingMembers(root);
        ValidateProductionClosure(sources);
        ValidateForkReachability(sources);

        IrDocument document = ExpectedIr();
        byte[] irBytes = Serialize(document);
        IrDocument roundTrippedIr = DeserializeIr(irBytes);
        string irHash = Hash(irBytes);
        List<SourceIdentity> identities = sources.Values.OrderBy(static source => source.RelativePath, StringComparer.Ordinal)
            .Select(static source => new SourceIdentity(source.RelativePath, source.Sha256)).ToList();
        string sourceHash = CombinedSourceHash(identities, [build]);
        byte[] leanBytes = Keccak256OpcodeLeanEmitter.Emit(roundTrippedIr, irHash, sourceHash);
        SourceManifest manifest = new(
            SchemaVersion,
            ExtractorVersion,
            typeof(CSharpSyntaxTree).Assembly.GetName().Version?.ToString() ?? "unknown",
            "CSharp14",
            Kernel,
            [.. identities],
            [build],
            [.. admissions],
            new(IrFileName, irHash),
            new(DefaultLeanRelativePath, Hash(leanBytes)),
            sourceHash,
            SemanticBindings);
        byte[] manifestBytes = Serialize(manifest);
        ValidateManifest(DeserializeManifest(manifestBytes), manifest);

        string output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        string irPath = Path.Combine(output, IrFileName);
        string manifestPath = Path.Combine(output, ManifestFileName);
        string leanPath = leanOutputPath is null
            ? Path.Combine(root, DefaultLeanRelativePath.Replace('/', Path.DirectorySeparatorChar))
            : Path.GetFullPath(leanOutputPath);
        string? leanDirectory = Path.GetDirectoryName(leanPath);
        if (leanDirectory is not null) Directory.CreateDirectory(leanDirectory);
        File.WriteAllBytes(irPath, irBytes);
        File.WriteAllBytes(manifestPath, manifestBytes);
        File.WriteAllBytes(leanPath, leanBytes);
        return new(irPath, manifestPath, leanPath, identities.Count, admissions.Count, document.Specializations.Length);
    }

    internal static IrDocument DeserializeIr(byte[] bytes)
    {
        RejectDuplicateProperties(bytes, "KECCAK256 opcode IR");
        try
        {
            IrDocument document = JsonSerializer.Deserialize<IrDocument>(bytes, JsonOptions)
                ?? throw new ExtractionException("Serialized KECCAK256 opcode IR is empty.");
            ValidateIr(document);
            return document;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Serialized KECCAK256 opcode IR is not valid JSON: {exception.Message}");
        }
    }

    internal static SourceManifest DeserializeManifest(byte[] bytes)
    {
        RejectDuplicateProperties(bytes, "KECCAK256 opcode manifest");
        try
        {
            SourceManifest manifest = JsonSerializer.Deserialize<SourceManifest>(bytes, JsonOptions)
                ?? throw new ExtractionException("Serialized KECCAK256 opcode manifest is empty.");
            ValidateManifest(manifest);
            return manifest;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Serialized KECCAK256 opcode manifest is not valid JSON: {exception.Message}");
        }
    }

    internal static void ValidateIr(IrDocument document)
    {
        if (document is null || document.ExtractorVersion is null || document.Kernel is null ||
            document.Reachability is null || document.Opcode is null || document.Schedule is null ||
            document.DispatchTables is null || document.Specializations is null || document.ForkLineage is null ||
            document.OpenExtractionObligations is null || document.DispatchTables.Any(static item => item is null) ||
            document.Specializations.Any(static item => item is null) || document.ForkLineage.Any(static item => item is null) ||
            document.OpenExtractionObligations.Any(static item => item is null) ||
            document.Opcode.EffectOrder is null || document.Opcode.EffectOrder.Any(static item => item is null))
            throw new ExtractionException("Serialized KECCAK256 opcode IR contains null fields, collections, or entries.");

        IrDocument expected = ExpectedIr();
        if (!Serialize(document).AsSpan().SequenceEqual(Serialize(expected)))
            throw new ExtractionException("Serialized KECCAK256 opcode IR is not the exact admitted profile.");
        ValidateSpecializations(document.Specializations);
    }

    internal static void ValidateManifest(SourceManifest manifest)
    {
        if (manifest is null || manifest.ExtractorVersion is null || manifest.RoslynVersion is null ||
            manifest.LanguageVersion is null || manifest.Kernel is null || manifest.Sources is null ||
            manifest.RawSources is null || manifest.Admissions is null || manifest.Ir is null || manifest.Lean is null ||
            manifest.CombinedSourceSha256 is null || manifest.SemanticBindings is null ||
            manifest.Sources.Any(static item => item is null || item.Path is null || item.Sha256 is null) ||
            manifest.RawSources.Any(static item => item is null || item.Path is null || item.Sha256 is null) ||
            manifest.Admissions.Any(static item => item is null || item.SourcePath is null || item.Namespace is null ||
                item.OwnerPath is null || item.MemberKind is null || item.MemberName is null || item.ParameterTypes is null ||
                item.SyntaxKind is null || item.Sha256 is null) || manifest.Ir.Path is null || manifest.Ir.Sha256 is null ||
            manifest.Lean.Path is null || manifest.Lean.Sha256 is null ||
            manifest.SemanticBindings.Any(static item => item is null))
            throw new ExtractionException("Serialized KECCAK256 opcode manifest contains null fields, collections, or entries.");

        if (manifest.SchemaVersion != SchemaVersion || manifest.ExtractorVersion != ExtractorVersion ||
            manifest.LanguageVersion != "CSharp14" || manifest.Kernel != Kernel ||
            manifest.RoslynVersion != ExpectedRoslynVersion)
            throw new ExtractionException("Serialized KECCAK256 opcode manifest header changed.");
        ValidateArtifact(manifest.Ir, IrFileName);
        ValidateArtifact(manifest.Lean, DefaultLeanRelativePath);
        if (manifest.Ir.Sha256 != ExpectedIrSha256 || manifest.Lean.Sha256 != ExpectedLeanSha256)
            throw new ExtractionException("Serialized KECCAK256 artifact digest changed.");

        string[] expectedPaths = SourcePaths.ToArray();
        if (manifest.Sources.Length != expectedPaths.Length ||
            !manifest.Sources.Select(static item => item.Path).SequenceEqual(expectedPaths, StringComparer.Ordinal) ||
            manifest.Sources.Select(static item => item.Path).Distinct(StringComparer.Ordinal).Count() != manifest.Sources.Length)
            throw new ExtractionException("Serialized KECCAK256 opcode source identities changed or collide.");
        foreach (SourceIdentity source in manifest.Sources)
        {
            ValidateSha256(source.Sha256, source.Path);
            if (!ExpectedSourceSha256.TryGetValue(source.Path, out string? expected) || source.Sha256 != expected)
                throw new ExtractionException($"Serialized KECCAK256 source digest changed for {source.Path}.");
        }

        if (manifest.RawSources.Length != 1 || manifest.RawSources[0].Path != BuildTargetsPath ||
            manifest.RawSources[0].Sha256 != BuildTargetsSha256)
            throw new ExtractionException("Serialized KECCAK256 standard-build identity changed.");
        ValidateSha256(manifest.RawSources[0].Sha256, BuildTargetsPath);
        ValidateAdmissions(manifest.Admissions);
        ValidateSha256(manifest.CombinedSourceSha256, "combined source");
        if (manifest.CombinedSourceSha256 != ExpectedCombinedSourceSha256 ||
            manifest.CombinedSourceSha256 != CombinedSourceHash(manifest.Sources, manifest.RawSources))
            throw new ExtractionException("Serialized KECCAK256 combined source digest is inconsistent.");
        if (!manifest.SemanticBindings.SequenceEqual(SemanticBindings, StringComparer.Ordinal))
            throw new ExtractionException("Serialized KECCAK256 semantic bindings changed.");
    }

    internal static void ValidateManifest(SourceManifest actual, SourceManifest expected)
    {
        ValidateManifest(actual);
        ValidateManifest(expected);
        if (!Serialize(actual).AsSpan().SequenceEqual(Serialize(expected)))
            throw new ExtractionException("Serialized KECCAK256 opcode manifest does not exactly match extracted lineage.");
    }

    internal static byte[] Serialize<T>(T value) => Utf8WithoutBom.GetBytes(
        JsonSerializer.Serialize(value, JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");

    private static IrDocument ExpectedIr()
    {
        OpcodeDescriptor opcode = new(
            "keccak256", "KECCAK256", 0x20, "KeccakOpcode<TTracingInst>",
            "EvmInstructions.InstructionKeccak256<TGasPolicy,TTracingInst>", 1, 1, 2, 1, false, "OnFlag", EffectOrder);
        ReachabilityDescriptor reachability = new(
            "Nethermind.Evm.IVirtualMachine", "Nethermind.Evm.EthereumVirtualMachine", Root,
            "Nethermind.Evm.GasPolicy.EthereumGasPolicy", "Nethermind.Specs.Forks.Amsterdam",
            "VirtualMachine<TGasPolicy>.RunDispatchLoop<TTracingInst,TCancelable>",
            "Directory.Build.targets:EnableZkEvm!=true", "Nethermind.Core.Crypto.KeccakCache.ComputeTo:oracle-only");
        GasScheduleDescriptor schedule = new(30, 6, 3, 512, 2_147_483_616, ulong.MaxValue, uint.MaxValue);
        OpcodeSpecialization[] specializations = DispatchTables.Select(table => new OpcodeSpecialization(
            opcode.Name,
            table.Name,
            table.TracingFlag,
            table.CancellationFlag,
            $"{Root}.ExecuteOpcode<KeccakOpcode<{table.TracingFlag}>,{table.TracingFlag},{table.CancellationFlag},OnFlag>"))
            .ToArray();
        return new(SchemaVersion, ExtractorVersion, Kernel, reachability, opcode, schedule,
            DispatchTables, specializations, ForkLineage, OpenExtractionObligations);
    }

    private static void ValidateSpecializations(IReadOnlyList<OpcodeSpecialization> specializations)
    {
        if (specializations.Count != DispatchTables.Length)
            throw new ExtractionException("Exactly four KECCAK256 dispatch roots are required.");
        HashSet<string> keys = new(StringComparer.Ordinal);
        IrDocument expected = ExpectedIr();
        for (int index = 0; index < specializations.Count; index++)
        {
            OpcodeSpecialization actual = specializations[index];
            if (!keys.Add(actual.DispatchTable) || actual != expected.Specializations[index])
                throw new ExtractionException($"KECCAK256 dispatch specialization at index {index} changed or collides.");
        }
    }

    private static Dictionary<string, SourceFile> LoadSources(string root)
    {
        Dictionary<string, SourceFile> result = new(StringComparer.Ordinal);
        foreach (string path in SourcePaths)
        {
            string fullPath = ResolveExactPath(root, path);
            byte[] bytes = File.ReadAllBytes(fullPath);
            string sha256 = Hash(bytes);
            if (!ExpectedSourceSha256.TryGetValue(path, out string? expected) || sha256 != expected)
                throw new ExtractionException($"Unadmitted complete source content for {path}; found SHA-256 {sha256}.");
            CompilationUnitSyntax syntax = CSharpSyntaxTree.ParseText(Utf8WithoutBom.GetString(bytes), ParseOptions, path)
                .GetCompilationUnitRoot();
            Diagnostic? error = syntax.GetDiagnostics().FirstOrDefault(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            if (error is not null) throw new ExtractionException($"Invalid C# source {path}: {error}.");
            result.Add(path, new(path, fullPath, bytes, sha256, syntax));
        }
        return result;
    }

    private static RawSourceIdentity LoadBuildTargets(string root)
    {
        string fullPath = ResolveExactPath(root, BuildTargetsPath);
        string sha256 = Hash(File.ReadAllBytes(fullPath));
        if (sha256 != BuildTargetsSha256)
            throw new ExtractionException($"Unadmitted standard/zkEVM build-target content; found SHA-256 {sha256}.");
        return new(BuildTargetsPath, sha256);
    }

    private static void ValidateBuildFlavor(string root)
    {
        string source = File.ReadAllText(ResolveExactPath(root, BuildTargetsPath));
        Require(source, "<ItemGroup Condition=\"'$(EnableZkEvm)' == 'true'\">", "zkEVM selection");
        Require(source, "<Compile Remove=\"**/*.std.cs\" />", "zkEVM exclusion of standard sources");
        Require(source, "<ItemGroup Condition=\"'$(EnableZkEvm)' != 'true'\">", "standard selection");
        Require(source, "<Compile Remove=\"**/*.zkevm.cs\" />", "standard exclusion of zkEVM sources");
    }

    private static List<AdmissionIdentity> AdmitSyntax(IReadOnlyDictionary<string, SourceFile> sources)
    {
        List<AdmissionIdentity> result = new(AdmissionSpecs.Length);
        foreach (AdmissionSpec spec in AdmissionSpecs)
        {
            MemberDeclarationSyntax node = FindAdmissionNode(sources[spec.SourcePath].Root, spec);
            string sha256 = Hash(Encoding.UTF8.GetBytes(CompleteCanonical(node)));
            if (!ExpectedAdmissionSha256.TryGetValue(AdmissionKey(spec), out string? expected) || sha256 != expected)
                throw new ExtractionException($"Unadmitted exact syntax for {AdmissionKey(spec)}; found SHA-256 {sha256}.");
            result.Add(new(spec.SourcePath, spec.Namespace, spec.OwnerPath, spec.MemberKind, spec.MemberName,
                spec.MemberGenericArity, spec.ParameterTypes, node.Kind().ToString(), sha256));
        }
        ValidateAdmissions(result);
        return result;
    }

    private static void ValidatePinnedProfile()
    {
        if (ExpectedSourceSha256.Count != SourcePaths.Count)
            throw new ExtractionException("The complete-source pin set is incomplete or contains an extra path.");
        if (ExpectedAdmissionSha256.Count != AdmissionSpecs.Length)
            throw new ExtractionException("The exact Roslyn-admission pin set is incomplete or contains an extra member.");
        foreach ((string path, string digest) in ExpectedSourceSha256)
        {
            if (!SourcePaths.Contains(path, StringComparer.Ordinal))
                throw new ExtractionException($"The complete-source pin set contains an unselected path {path}.");
            ValidateSha256(digest, path);
        }
        foreach ((string key, string digest) in ExpectedAdmissionSha256)
        {
            if (!AdmissionSpecs.Any(spec => AdmissionKey(spec) == key))
                throw new ExtractionException($"The exact Roslyn-admission pin set contains an unselected member {key}.");
            ValidateSha256(digest, key);
        }
        ValidateSha256(ExpectedIrSha256, "expected IR");
        ValidateSha256(ExpectedLeanSha256, "expected Lean");
        ValidateSha256(ExpectedCombinedSourceSha256, "expected combined source");
    }

    private static void ValidateNoCompetingMembers(string root)
    {
        string[] directories =
        [
            "src/Nethermind/Nethermind.Core",
            "src/Nethermind/Nethermind.Evm",
            "src/Nethermind/Nethermind.Init",
            "src/Nethermind/Nethermind.Specs/Forks",
        ];
        foreach (string directory in directories)
        {
            string fullDirectory = ResolveExactPath(root, directory);
            foreach (string file in Directory.EnumerateFiles(fullDirectory, "*.cs", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                if (relative.EndsWith(".zkevm.cs", StringComparison.OrdinalIgnoreCase) ||
                    relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
                    relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
                    continue;
                AdmissionSpec[] possible = AdmissionSpecs.Where(spec => spec.SourcePath != relative).ToArray();
                if (possible.Length == 0) continue;
                CompilationUnitSyntax syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(file), ParseOptions, relative)
                    .GetCompilationUnitRoot();
                foreach (AdmissionSpec spec in possible)
                {
                    if (TryFindAdmissionNode(syntax, spec))
                        throw new ExtractionException($"Competing standard-build declaration for {AdmissionKey(spec)} in {relative}.");
                }
            }
        }
    }

    private static void ValidateAdmissions(IReadOnlyList<AdmissionIdentity> admissions)
    {
        if (admissions.Count != AdmissionSpecs.Length)
            throw new ExtractionException("Serialized KECCAK256 exact-admission count changed.");
        HashSet<string> keys = new(StringComparer.Ordinal);
        for (int index = 0; index < admissions.Count; index++)
        {
            AdmissionIdentity actual = admissions[index];
            AdmissionSpec expected = AdmissionSpecs[index];
            string key = AdmissionKey(expected);
            if (!keys.Add(AdmissionKey(actual)))
                throw new ExtractionException($"Duplicate exact admission {AdmissionKey(actual)}.");
            if (actual.SourcePath != expected.SourcePath || actual.Namespace != expected.Namespace ||
                actual.OwnerPath != expected.OwnerPath || actual.MemberKind != expected.MemberKind ||
                actual.MemberName != expected.MemberName || actual.MemberGenericArity != expected.MemberGenericArity ||
                actual.ParameterTypes != expected.ParameterTypes || actual.SyntaxKind != ExpectedSyntaxKind(expected))
                throw new ExtractionException($"Serialized KECCAK256 admission qualifier changed at index {index}.");
            ValidateSha256(actual.Sha256, key);
            if (!ExpectedAdmissionSha256.TryGetValue(key, out string? digest) || actual.Sha256 != digest)
                throw new ExtractionException($"Serialized KECCAK256 admission digest changed for {key}.");
        }
    }

    private static void ValidateProductionClosure(IReadOnlyDictionary<string, SourceFile> sources)
    {
        string instruction = Canonical(FindAdmissionNode(sources[InstructionPath].Root,
            A(InstructionPath, "Nethermind.Evm", "namespace", "type", "Instruction")));
        Require(instruction, "KECCAK256=0x20", "KECCAK256 opcode byte");

        string generator = MethodText(sources, OpcodePath, "Nethermind.Evm", "VirtualMachine`1",
            "GenerateOpcodeHandlers", 2, "IReleaseSpec");
        RequireExactlyOnce(generator,
            "lookup[(int)Instruction.KECCAK256]=OpcodeHandler<KeccakOpcode<TTracingInst>,TTracingInst,TCancelable>();",
            "unconditional KECCAK256 dispatch binding");
        string body = MethodText(sources, OpcodePath, "Nethermind.Evm", "VirtualMachine`1.KeccakOpcode`1",
            "Execute", 0, "EvmStack,TGasPolicy,VirtualMachine<TGasPolicy>,nint");
        Require(body,
            "EvmInstructions.InstructionKeccak256<TGasPolicy,TTracingInst>(refstack,refgas,vm)",
            "KECCAK256 wrapper target");
        string opcodeInterface = Canonical(FindAdmissionNode(sources[OpcodePath].Root,
            A(OpcodePath, "Nethermind.Evm", "VirtualMachine`1", "type", "IOpcodeBody")));
        Require(opcodeInterface, "staticvirtualboolHasCheckedBody=>false", "unchecked handler default");
        string opcodeHandler = MethodText(sources, OpcodePath, "Nethermind.Evm", "VirtualMachine`1",
            "OpcodeHandler", 3);
        Require(opcodeHandler, "&ExecuteOpcode<TOpcode,TTracingInst,TCancelable,OnFlag>",
            "continuable opcode root");

        string dispatch = MethodText(sources, DispatchPath, "Nethermind.Evm", "VirtualMachine`1",
            "ExecuteOpcode", 4, "EvmStack,TGasPolicy,DispatchState,nint,int");
        RequireOrdered(dispatch,
        [
            "state.Vm.StartInstructionTrace(instruction,TGasPolicy.GetRemainingGas(ingas),(int)pc,instack)",
            "pc++", "opCodeCount++", "exceptionType=TOpcode.Execute(refstack,refgas,state.Vm,refpc)",
            "if(!TOpcode.HasCheckedBody&&exceptionType!=EvmExceptionType.None)gotoExit",
            "state.Vm.EndInstructionTrace(TGasPolicy.GetRemainingGas(ingas))",
        ], "PC-first dispatch and successful trace boundary");

        string handler = MethodText(sources, HandlerPath, "Nethermind.Evm", "EvmInstructions`0",
            "InstructionKeccak256", 2, "EvmStack,TGasPolicy,VirtualMachine<TGasPolicy>");
        RequireOrdered(handler,
        [
            "stack.PopMemoryPositionAndUInt256(outUInt256a,outUInt256b)",
            "EvmCalculations.Div32Ceiling(inb,outbooloutOfGas)",
            "TGasPolicy.TryConsumeKeccak(refgas,words)",
            "if(outOfGas)gotoOutOfGas", "VmState<TGasPolicy>vmState=vm.VmState",
            "TGasPolicy.UpdateMemoryCost(refgas,ina,b,refvmState.Memory)",
            "vmState.Memory.TryLoadSpan(ina,b,outSpan<byte>bytes)",
            "KeccakCache.ComputeTo(bytes,outValueHash256keccak)",
            "stack.Push32Bytes<TTracingInst,OnFlag>(inkeccak)",
        ], "KECCAK256 handler effects");

        string divWide = MethodText(sources, CalculationPath, "Nethermind.Evm", "EvmCalculations`0",
            "Div32Ceiling", 0, "UInt256,bool");
        RequireOrdered(divWide,
        [
            "if(!length.IsUint64)", "outOfGas=true", "return0", "returnDiv32Ceiling(length.u0,outoutOfGas)",
        ], "UInt256 ceiling overflow");
        string divNarrow = MethodText(sources, CalculationPath, "Nethermind.Evm", "EvmCalculations`0",
            "Div32Ceiling", 0, "ulong,bool");
        RequireOrdered(divNarrow,
        [
            "ulongrem=result&31", "result>>=5", "if(rem>0)", "result++", "if(result>uint.MaxValue)",
            "outOfGas=true", "return0", "outOfGas=false", "returnresult",
        ], "uint ceiling overflow");

        string tryKeccak = MethodText(sources, InterfaceGasPath, "Nethermind.Evm.GasPolicy", "IGasPolicy`1",
            "TryConsumeKeccak", 0, "TSelf,ulong");
        Require(tryKeccak, "GasCostOf.Sha3+GasCostOf.Sha3Word*words", "fixed and word gas");
        Require(Canonical(FindAdmissionNode(sources[GasCostPath].Root,
            A(GasCostPath, "Nethermind.Core", "GasCostOf`0", "field", "Sha3"))),
            "publicconstulongSha3=30", "KECCAK256 base gas constant");
        Require(Canonical(FindAdmissionNode(sources[GasCostPath].Root,
            A(GasCostPath, "Nethermind.Core", "GasCostOf`0", "field", "Sha3Word"))),
            "publicconstulongSha3Word=6", "KECCAK256 word gas constant");
        Require(Canonical(FindAdmissionNode(sources[GasCostPath].Root,
            A(GasCostPath, "Nethermind.Core", "GasCostOf`0", "field", "Memory"))),
            "publicconstulongMemory=3", "linear memory gas constant");
        string updateGas = MethodText(sources, GasPath, "Nethermind.Evm.GasPolicy", "EthereumGasPolicy`0",
            "UpdateGas", 0, "EthereumGasPolicy,ulong");
        RequireOrdered(updateGas,
        ["if(GetRemainingGas(ingas)<gasCost)", "gas.Value=0", "returnfalse", "ConsumeRaw(refgas,gasCost)", "returntrue"],
            "execution-gas exhaustion");
        string updateMemory = MethodText(sources, GasPath, "Nethermind.Evm.GasPolicy", "EthereumGasPolicy`0",
            "UpdateMemoryCost", 0, "EthereumGasPolicy,UInt256,UInt256,EvmPooledMemory");
        RequireOrdered(updateMemory,
        ["memory.CalculateMemoryCost(inposition,length,outbooloutOfGas)", "if(memoryCost==0L)return!outOfGas", "returnUpdateGas(refgas,memoryCost)"],
            "memory validation and charge");

        string memoryCost = MethodText(sources, MemoryPath, "Nethermind.Evm", "EvmPooledMemory`0",
            "CalculateMemoryCost", 0, "UInt256,UInt256,bool");
        RequireOrdered(memoryCost,
        ["if(length.IsZero)", "outOfGas=false", "return0", "CheckMemoryAccessViolation(inlocation,inlength,outulongnewSize,outoutOfGas)",
            "if(outOfGas)return0", "returnnewSize>Size?ComputeMemoryExpansionCost(newSize):0"], "memory-range precedence");
        string expansion = MethodText(sources, MemoryPath, "Nethermind.Evm", "EvmPooledMemory`0",
            "ComputeMemoryExpansionCost", 0, "ulong");
        RequireOrdered(expansion,
        ["ulongnewActiveWords=", "ulongactiveWords=", "Size=newActiveWords<<5", "ulongcost=", "returncost"],
            "retained logical memory expansion");
        string load = MethodText(sources, MemoryPath, "Nethermind.Evm", "EvmPooledMemory`0",
            "TryLoadSpan", 0, "UInt256,UInt256,Span<byte>");
        RequireOrdered(load,
        ["if(length.IsZero)", "data=[]", "returntrue", "CheckMemoryAccessViolation", "data=LoadSpan(newLength", "returntrue"],
            "exact memory slice");
        string loadSpan = MethodText(sources, MemoryPath, "Nethermind.Evm", "EvmPooledMemory`0",
            "LoadSpan", 0, "ulong,int,int");
        RequireOrdered(loadSpan, ["UpdateSize(newLength)", "returnGetBackingSpan(offset,length)"],
            "memory load expansion before exact span");
        string ensureRented = MethodText(sources, MemoryPath, "Nethermind.Evm", "EvmPooledMemory`0",
            "EnsureRented", 0, "ulong");
        Require(ensureRented, "if(requiredEnd>GetBackingCapacity()||requiredEnd>_initializedSize)",
            "initialized memory prefix guard");
        string rentSlow = MethodText(sources, MemoryPath, "Nethermind.Evm", "EvmPooledMemory`0",
            "RentSlow", 0, "ulong");
        RequireOrdered(rentSlow, ["if(requiredEnd>initializedSize)", ".Clear()", "MaterializeArray(requiredEnd)"],
            "inline zero extension");
        string materialize = MethodText(sources, MemoryPath, "Nethermind.Evm", "EvmPooledMemory`0",
            "MaterializeArray", 0, "ulong");
        RequireOrdered(materialize,
        ["if(requiredEnd>initializedSize)", "Array.Clear(memory,(int)initializedSize,(int)(target-initializedSize))",
            "_initializedSize=initializedSize"], "array zero extension");

        string pop = MethodText(sources, StackPath, "Nethermind.Evm", "EvmStack`0",
            "PopMemoryPositionAndUInt256", 0, "UInt256,UInt256");
        RequireOrdered(pop,
        ["nintnewHead=Head-2", "if(newHead<0)", "returnfalse", "Head=newHead", "ReadUInt256FromSlot", "ReadMemoryPositionFromSlot", "returntrue"],
            "atomic two-word pop");
        string push = MethodText(sources, StackPath, "Nethermind.Evm", "EvmStack`0",
            "Push32Bytes", 2, "byte");
        RequireOrdered(push,
        ["nintnewOffset=headOffset+1", "if(TCheckDepth.IsActive&&newOffset>=MaxStackSize)", "returnEvmExceptionType.StackOverflow",
            "Head=newOffset", "_tracer.TraceBytes(invalue,32)", "head=Unsafe.ReadUnaligned<EvmWord>(refvalue)"],
            "checked traced hash push");

        string module = MethodText(sources, ModulePath, "Nethermind.Init.Modules", "BlockProcessingModule`0",
            "Load", 0, "ContainerBuilder");
        Require(module, ".AddScoped<IVirtualMachine,EthereumVirtualMachine>()", "standard VM service closure");
        string vmImplementation = Canonical(FindAdmissionNode(sources[VmPath].Root,
            A(VmPath, "Nethermind.Evm", "namespace", "type", "EthereumVirtualMachine")));
        Require(vmImplementation, ":VirtualMachine<EthereumGasPolicy>", "Ethereum VM gas-policy closure");
        string standardVm = Canonical(FindAdmissionNode(sources[VmStandardPath].Root,
            A(VmStandardPath, "Nethermind.Evm", "VirtualMachine`1", "method", "GetOpcodeTable")));
        Require(standardVm, "_opcodeTablesBySpec.GetValue(Spec", "standard opcode table root");
        string cache = MethodText(sources, CachePath, "Nethermind.Core.Crypto", "KeccakCache`0",
            "ComputeTo", 0, "ReadOnlySpan<byte>,ValueHash256");
        Require(cache, "publicstaticvoidComputeTo(ReadOnlySpan<byte>input,outValueHash256keccak256)",
            "explicit Keccak oracle boundary signature");
        string flags = string.Concat(sources[DispatchFlagsPath].Root.DescendantTokens().Select(static token => token.Text));
        Require(flags, "publicconstboolConstTracing=true", "standard tracing capability");
        Require(flags, "publicstaticboolTracing(boolisTracing)=>isTracing", "standard tracing identity");
        Require(flags, "publicstaticboolCancelable(booltracerIsCancelable)=>tracerIsCancelable", "standard cancellation identity");
        string tables = MethodText(sources, DispatchPath, "Nethermind.Evm", "VirtualMachine`1.OpcodeTable`0",
            "GetHandlers", 2, "IReleaseSpec");
        Require(tables,
            "TTracingInst.IsActive?ref(TCancelable.IsActive?refTracedCancelable:refTraced):ref(TCancelable.IsActive?refNoTraceCancelable:refNoTrace)",
            "four dispatch-table selection");
        string startTrace = MethodText(sources, VmPath, "Nethermind.Evm", "VirtualMachine`1",
            "StartInstructionTrace", 0, "Instruction,ulong,int,EvmStack");
        RequireOrdered(startTrace,
        ["_txTracer.StartOperation(programCounter,instruction,gasAvailable,", "_txTracer.SetOperationMemory", "_txTracer.SetOperationStack"],
            "instruction trace start payload");
        string endTrace = MethodText(sources, VmPath, "Nethermind.Evm", "VirtualMachine`1",
            "EndInstructionTrace", 0, "ulong");
        Require(endTrace, "_txTracer.ReportOperationRemainingGas(gasAvailable)", "instruction trace success end");
        string runByteCode = MethodText(sources, VmPath, "Nethermind.Evm", "VirtualMachine`1",
            "RunByteCode", 2, "EvmStack,TGasPolicy");
        RequireOrdered(runByteCode,
        ["RunDispatchLoop<TTracingInst,TCancelable>(refstack,refgas,refprogramCounter)",
            "if(exceptionTypeisEvmExceptionType.NoneorEvmExceptionType.StoporEvmExceptionType.RevertorEvmExceptionType.Suspend)",
            "if(TTracingInst.IsActive&&exceptionType!=EvmExceptionType.None&&(exceptionType!=EvmExceptionType.Suspend||ReturnDataisnotVmState<TGasPolicy>childState||!childState.ExecutionType.IsAnyCreate()))",
            "EndInstructionTrace(TGasPolicy.GetRemainingGas(ingas))", "else", "gotoReturnFailure",
            "if(exceptionType==EvmExceptionType.OutOfGas)", "TGasPolicy.ClearExecutionGas(refgas)",
            "returnGetFailureReturn(TGasPolicy.GetRemainingGas(ingas),exceptionType)"],
            "outer success/error trace closure");
        string failureTrace = MethodText(sources, VmPath, "Nethermind.Evm", "VirtualMachine`1",
            "GetFailureReturn", 0, "ulong,EvmExceptionType");
        Require(failureTrace,
            "if(DispatchFlags.ConstTracing&&_txTracer.IsTracingInstructions)EndInstructionTraceError(gasAvailable,exceptionType)",
            "outer instruction-error trace closure");
        string endTraceError = MethodText(sources, VmPath, "Nethermind.Evm", "VirtualMachine`1",
            "EndInstructionTraceError", 0, "ulong,EvmExceptionType");
        RequireOrdered(endTraceError,
        ["_txTracer.ReportOperationRemainingGas(gasAvailable)", "_txTracer.ReportOperationError(evmExceptionType)"],
            "instruction trace error payload");
        string tracePush = MethodText(sources, TracePath, "Nethermind.Evm.Tracing", "TracerExtensions`0",
            "TraceBytes", 0, "ITxTracer,byte,int");
        Require(tracePush, "tracer.ReportStackPush(MemoryMarshal.CreateReadOnlySpan(invalue,length))", "stack-push trace boundary");
    }

    private static void ValidateForkReachability(IReadOnlyDictionary<string, SourceFile> sources)
    {
        string named = Canonical(FindAdmissionNode(sources[NamedReleasePath].Root,
            A(NamedReleasePath, "Nethermind.Specs.Forks", "NamedReleaseSpec`0", "method", "ReplayAncestors", 0, "NamedReleaseSpec?")));
        RequireOrdered(named, ["if(forkisnull)return", "ReplayAncestors(fork.Parent)", "fork.Apply(this)"],
            "named-fork ancestry replay");
        for (int index = 0; index < ForkLineage.Length; index++)
        {
            string path = ForkPath(ForkFiles[index]);
            TypeDeclarationSyntax type = (TypeDeclarationSyntax)FindAdmissionNode(sources[path].Root,
                A(path, "Nethermind.Specs.Forks", "namespace", "type", ForkLineage[index]));
            string expectedParent = index == 0 ? "null" : $"{ForkLineage[index - 1]}.Instance";
            if (ForkLineage[index] == "Amsterdam") expectedParent = "BPO2.Instance";
            Require(Canonical(type.BaseList!), $"NamedReleaseSpec<{ForkLineage[index]}>({expectedParent})",
                $"{ForkLineage[index]} exact mainnet parent");
        }
    }

    private static AdmissionSpec[] BuildAdmissionSpecs()
    {
        List<AdmissionSpec> specs =
        [
            A(InstructionPath, "Nethermind.Evm", "namespace", "type", "Instruction"),
            A(GasCostPath, "Nethermind.Core", "GasCostOf`0", "field", "Memory"),
            A(GasCostPath, "Nethermind.Core", "GasCostOf`0", "field", "Sha3"),
            A(GasCostPath, "Nethermind.Core", "GasCostOf`0", "field", "Sha3Word"),
            A(TypeFlagsPath, "Nethermind.Core", "namespace", "type", "IFlag"),
            A(TypeFlagsPath, "Nethermind.Core", "namespace", "type", "OffFlag"),
            A(TypeFlagsPath, "Nethermind.Core", "namespace", "type", "OnFlag"),
            A(HandlerPath, "Nethermind.Evm", "EvmInstructions`0", "method", "InstructionKeccak256", 2,
                "EvmStack,TGasPolicy,VirtualMachine<TGasPolicy>"),
            A(CalculationPath, "Nethermind.Evm", "EvmCalculations`0", "method", "Div32Ceiling", 0, "UInt256,bool"),
            A(CalculationPath, "Nethermind.Evm", "EvmCalculations`0", "method", "Div32Ceiling", 0, "ulong,bool"),
            A(InterfaceGasPath, "Nethermind.Evm.GasPolicy", "IGasPolicy`1", "method", "TryConsumeKeccak", 0, "TSelf,ulong"),
            A(InterfaceGasPath, "Nethermind.Evm.GasPolicy", "IGasPolicy`1", "method", "UpdateMemoryCost", 0,
                "TSelf,UInt256,UInt256,EvmPooledMemory"),
            A(InterfaceGasPath, "Nethermind.Evm.GasPolicy", "IGasPolicy`1", "method", "UpdateGas", 0, "TSelf,ulong"),
            A(GasPath, "Nethermind.Evm.GasPolicy", "EthereumGasPolicy`0", "field", "Value"),
            A(GasPath, "Nethermind.Evm.GasPolicy", "EthereumGasPolicy`0", "field", "StateReservoir"),
            A(GasPath, "Nethermind.Evm.GasPolicy", "EthereumGasPolicy`0", "field", "StateGasUsed"),
            A(GasPath, "Nethermind.Evm.GasPolicy", "EthereumGasPolicy`0", "field", "StateGasSpill"),
            A(GasPath, "Nethermind.Evm.GasPolicy", "EthereumGasPolicy`0", "field", "StateGasSpillRefunded"),
            A(GasPath, "Nethermind.Evm.GasPolicy", "EthereumGasPolicy`0", "method", "GetRemainingGas", 0, "EthereumGasPolicy"),
            A(GasPath, "Nethermind.Evm.GasPolicy", "EthereumGasPolicy`0", "method", "ConsumeRaw", 0, "EthereumGasPolicy,ulong"),
            A(GasPath, "Nethermind.Evm.GasPolicy", "EthereumGasPolicy`0", "method", "UpdateMemoryCost", 0,
                "EthereumGasPolicy,UInt256,UInt256,EvmPooledMemory"),
            A(GasPath, "Nethermind.Evm.GasPolicy", "EthereumGasPolicy`0", "method", "UpdateGas", 0, "EthereumGasPolicy,ulong"),
            A(MemoryPath, "Nethermind.Evm", "EvmPooledMemory`0", "field", "WordSize"),
            A(MemoryPath, "Nethermind.Evm", "EvmPooledMemory`0", "field", "MaxMemorySize"),
            A(MemoryPath, "Nethermind.Evm", "EvmPooledMemory`0", "property", "Size"),
            A(MemoryPath, "Nethermind.Evm", "EvmPooledMemory`0", "method", "CheckMemoryAccessViolation", 0, "UInt256,UInt256,ulong,bool"),
            A(MemoryPath, "Nethermind.Evm", "EvmPooledMemory`0", "method", "CalculateMemoryCost", 0, "UInt256,UInt256,bool"),
            A(MemoryPath, "Nethermind.Evm", "EvmPooledMemory`0", "method", "ComputeMemoryExpansionCost", 0, "ulong"),
            A(MemoryPath, "Nethermind.Evm", "EvmPooledMemory`0", "method", "TryLoadSpan", 0, "UInt256,UInt256,Span<byte>"),
            A(MemoryPath, "Nethermind.Evm", "EvmPooledMemory`0", "method", "LoadSpan", 0, "ulong,int,int"),
            A(MemoryPath, "Nethermind.Evm", "EvmPooledMemory`0", "method", "UpdateSize", 0, "ulong,bool"),
            A(MemoryPath, "Nethermind.Evm", "EvmPooledMemory`0", "method", "EnsureRented", 0, "ulong"),
            A(MemoryPath, "Nethermind.Evm", "EvmPooledMemory`0", "method", "RentSlow", 0, "ulong"),
            A(MemoryPath, "Nethermind.Evm", "EvmPooledMemory`0", "method", "MaterializeArray", 0, "ulong"),
            A(StackPath, "Nethermind.Evm", "EvmStack`0", "field", "Head"),
            A(StackPath, "Nethermind.Evm", "EvmStack`0", "method", "ReadUInt256FromSlot", 0, "byte,UInt256"),
            A(StackPath, "Nethermind.Evm", "EvmStack`0", "method", "ReadMemoryPositionFromSlot", 0, "byte,UInt256"),
            A(StackPath, "Nethermind.Evm", "EvmStack`0", "method", "PopMemoryPositionAndUInt256", 0, "UInt256,UInt256"),
            A(StackPath, "Nethermind.Evm", "EvmStack`0", "method", "Push32Bytes", 2, "byte"),
            A(StackPath, "Nethermind.Evm", "EvmStack`0", "method", "Push32Bytes", 2, "ValueHash256"),
            A(OpcodePath, "Nethermind.Evm", "VirtualMachine`1", "type", "IOpcodeBody"),
            A(OpcodePath, "Nethermind.Evm", "VirtualMachine`1", "method", "OpcodeHandler", 3),
            A(OpcodePath, "Nethermind.Evm", "VirtualMachine`1", "method", "GenerateOpcodeHandlers", 2, "IReleaseSpec"),
            A(OpcodePath, "Nethermind.Evm", "VirtualMachine`1.KeccakOpcode`1", "method", "Execute", 0,
                "EvmStack,TGasPolicy,VirtualMachine<TGasPolicy>,nint"),
            A(DispatchPath, "Nethermind.Evm", "VirtualMachine`1", "method", "GetOpcodeHandlers", 2),
            A(DispatchPath, "Nethermind.Evm", "VirtualMachine`1", "method", "PrepareOpcodes", 1),
            A(DispatchPath, "Nethermind.Evm", "VirtualMachine`1", "method", "PrepareOpcodes", 2),
            A(DispatchPath, "Nethermind.Evm", "VirtualMachine`1", "method", "RunDispatchLoop", 2,
                "EvmStack,TGasPolicy,nint"),
            A(DispatchPath, "Nethermind.Evm", "VirtualMachine`1", "method", "ExecuteOpcode", 4,
                "EvmStack,TGasPolicy,DispatchState,nint,int"),
            A(DispatchPath, "Nethermind.Evm", "VirtualMachine`1", "method", "ExitCheckedOpcode", 0,
                "DispatchState,nint,int,EvmExceptionType"),
            A(DispatchPath, "Nethermind.Evm", "VirtualMachine`1.OpcodeTable`0", "field", "NoTrace"),
            A(DispatchPath, "Nethermind.Evm", "VirtualMachine`1.OpcodeTable`0", "field", "NoTraceCancelable"),
            A(DispatchPath, "Nethermind.Evm", "VirtualMachine`1.OpcodeTable`0", "field", "Traced"),
            A(DispatchPath, "Nethermind.Evm", "VirtualMachine`1.OpcodeTable`0", "field", "TracedCancelable"),
            A(DispatchPath, "Nethermind.Evm", "VirtualMachine`1.OpcodeTable`0", "method", "GetHandlers", 2, "IReleaseSpec"),
            A(VmPath, "Nethermind.Evm", "namespace", "type", "EthereumVirtualMachine"),
            A(VmPath, "Nethermind.Evm", "VirtualMachine`1", "property", "VmState"),
            A(VmPath, "Nethermind.Evm", "VirtualMachine`1", "method", "StartInstructionTrace", 0,
                "Instruction,ulong,int,EvmStack"),
            A(VmPath, "Nethermind.Evm", "VirtualMachine`1", "method", "EndInstructionTrace", 0, "ulong"),
            A(VmPath, "Nethermind.Evm", "VirtualMachine`1", "method", "RunByteCode", 2, "EvmStack,TGasPolicy"),
            A(VmPath, "Nethermind.Evm", "VirtualMachine`1", "method", "GetFailureReturn", 0, "ulong,EvmExceptionType"),
            A(VmPath, "Nethermind.Evm", "VirtualMachine`1", "method", "EndInstructionTraceError", 0,
                "ulong,EvmExceptionType"),
            A(VmStandardPath, "Nethermind.Evm", "VirtualMachine`1", "method", "GetOpcodeTable"),
            A(DispatchFlagsPath, "Nethermind.Evm", "DispatchFlags`0", "field", "ConstTracing"),
            A(DispatchFlagsPath, "Nethermind.Evm", "DispatchFlags`0", "method", "Tracing", 0, "bool"),
            A(DispatchFlagsPath, "Nethermind.Evm", "DispatchFlags`0", "method", "Cancelable", 0, "bool"),
            A(TracePath, "Nethermind.Evm.Tracing", "TracerExtensions`0", "method", "TraceBytes", 0, "ITxTracer,byte,int"),
            A(CachePath, "Nethermind.Core.Crypto", "KeccakCache`0", "method", "ComputeTo", 0, "ReadOnlySpan<byte>,ValueHash256"),
            A(ModulePath, "Nethermind.Init.Modules", "BlockProcessingModule`0", "method", "Load", 0, "ContainerBuilder"),
            A(NamedReleasePath, "Nethermind.Specs.Forks", "namespace", "type", "NamedReleaseSpec"),
            A(NamedReleasePath, "Nethermind.Specs.Forks", "namespace", "type", "NamedReleaseSpec", 1),
            A(NamedReleasePath, "Nethermind.Specs.Forks", "NamedReleaseSpec`0", "method", "ReplayAncestors", 0, "NamedReleaseSpec?"),
        ];
        for (int index = 0; index < ForkLineage.Length; index++)
            specs.Add(A(ForkPath(ForkFiles[index]), "Nethermind.Specs.Forks", "namespace", "type", ForkLineage[index]));
        return [.. specs];
    }

    private static MemberDeclarationSyntax FindAdmissionNode(CompilationUnitSyntax root, AdmissionSpec spec)
    {
        BaseNamespaceDeclarationSyntax[] namespaces = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>()
            .Where(ns => ns.Name.ToString() == spec.Namespace).ToArray();
        if (namespaces.Length != 1)
            throw new ExtractionException($"Expected exact namespace {spec.Namespace} in {spec.SourcePath}; found {namespaces.Length}.");
        SyntaxNode container = namespaces[0];
        if (spec.OwnerPath != "namespace")
        {
            foreach (string segment in spec.OwnerPath.Split('.'))
            {
                (string name, int arity) = ParseOwnerSegment(segment);
                TypeDeclarationSyntax[] owners = DirectMembers(container).OfType<TypeDeclarationSyntax>()
                    .Where(type => type.Identifier.ValueText == name && GenericArity(type) == arity).ToArray();
                if (owners.Length != 1)
                    throw new ExtractionException($"Expected exact owner segment {segment} in {spec.SourcePath}; found {owners.Length}.");
                container = owners[0];
            }
        }

        MemberDeclarationSyntax[] matches = DirectMembers(container).Where(member => Matches(member, spec)).ToArray();
        if (matches.Length != 1)
            throw new ExtractionException($"Expected exactly one {AdmissionKey(spec)}; found {matches.Length}.");
        return matches[0];
    }

    private static bool TryFindAdmissionNode(CompilationUnitSyntax root, AdmissionSpec spec)
    {
        BaseNamespaceDeclarationSyntax? ns = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>()
            .SingleOrDefault(candidate => candidate.Name.ToString() == spec.Namespace);
        if (ns is null) return false;
        SyntaxNode container = ns;
        if (spec.OwnerPath != "namespace")
        {
            foreach (string segment in spec.OwnerPath.Split('.'))
            {
                (string name, int arity) = ParseOwnerSegment(segment);
                TypeDeclarationSyntax? owner = DirectMembers(container).OfType<TypeDeclarationSyntax>()
                    .SingleOrDefault(type => type.Identifier.ValueText == name && GenericArity(type) == arity);
                if (owner is null) return false;
                container = owner;
            }
        }
        return DirectMembers(container).Any(member => Matches(member, spec));
    }

    private static bool Matches(MemberDeclarationSyntax member, AdmissionSpec spec) => spec.MemberKind switch
    {
        "type" when member is BaseTypeDeclarationSyntax type =>
            type.Identifier.ValueText == spec.MemberName && GenericArity(type) == spec.MemberGenericArity,
        "method" when member is MethodDeclarationSyntax method =>
            method.Identifier.ValueText == spec.MemberName &&
            (method.TypeParameterList?.Parameters.Count ?? 0) == spec.MemberGenericArity &&
            ParameterTypes(method) == spec.ParameterTypes,
        "property" when member is PropertyDeclarationSyntax property => property.Identifier.ValueText == spec.MemberName,
        "field" when member is FieldDeclarationSyntax field =>
            field.Declaration.Variables.Count == 1 && field.Declaration.Variables[0].Identifier.ValueText == spec.MemberName,
        _ => false,
    };

    private static IEnumerable<MemberDeclarationSyntax> DirectMembers(SyntaxNode container) => container switch
    {
        BaseNamespaceDeclarationSyntax ns => ns.Members,
        TypeDeclarationSyntax type => type.Members,
        _ => throw new ExtractionException($"Unsupported exact-admission owner {container.Kind()}."),
    };

    private static string MethodText(IReadOnlyDictionary<string, SourceFile> sources, string path, string ns,
        string owner, string name, int arity = 0, string parameters = "") =>
        Canonical(FindAdmissionNode(sources[path].Root, A(path, ns, owner, "method", name, arity, parameters)));

    private static string ParameterTypes(BaseMethodDeclarationSyntax method) => string.Join(",",
        method.ParameterList.Parameters.Select(static parameter => Canonical(parameter.Type!)));

    private static int GenericArity(BaseTypeDeclarationSyntax type) => type is TypeDeclarationSyntax declaration
        ? declaration.TypeParameterList?.Parameters.Count ?? 0
        : 0;

    private static (string Name, int Arity) ParseOwnerSegment(string segment)
    {
        int marker = segment.LastIndexOf('`');
        if (marker <= 0 || !int.TryParse(segment.AsSpan(marker + 1), out int arity))
            throw new ExtractionException($"Invalid generic-qualified owner segment '{segment}'.");
        return (segment[..marker], arity);
    }

    private static string CompleteCanonical(SyntaxNode node)
    {
        StringBuilder result = new();
        foreach (SyntaxToken token in node.DescendantTokens(descendIntoTrivia: false))
            result.Append('T').Append(token.RawKind).Append(':').Append(token.Text).Append('\0');
        return result.ToString();
    }

    private static string Canonical(SyntaxNode node) => string.Concat(
        node.DescendantTokens(descendIntoTrivia: false).Select(static token => token.Text));

    private static string ResolveExactPath(string root, string relativePath)
    {
        string current = root;
        foreach (string segment in relativePath.Split('/'))
        {
            string[] matches = Directory.EnumerateFileSystemEntries(current)
                .Where(path => Path.GetFileName(path).Equals(segment, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
                throw new ExtractionException($"Required case-sensitive path '{relativePath}' is missing or ambiguous at '{segment}'.");
            current = matches[0];
        }
        return current;
    }

    private static void RejectDuplicateProperties(byte[]? bytes, string description)
    {
        if (bytes is null) throw new ExtractionException($"Serialized {description} input is null.");
        try
        {
            Utf8JsonReader reader = new(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
            Stack<HashSet<string>?> containers = new();
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        containers.Push(new(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.StartArray:
                        containers.Push(null);
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        if (containers.Count == 0) throw new ExtractionException($"Serialized {description} has malformed nesting.");
                        containers.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        if (containers.Count == 0 || containers.Peek() is not HashSet<string> properties ||
                            !properties.Add(reader.GetString()!))
                            throw new ExtractionException($"Serialized {description} contains a duplicate property '{reader.GetString()}'.");
                        break;
                }
            }
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Serialized {description} is not valid JSON: {exception.Message}");
        }
    }

    private static void ValidateArtifact(ArtifactIdentity artifact, string path)
    {
        if (artifact.Path != path) throw new ExtractionException($"Serialized artifact path changed from {path}.");
        ValidateSha256(artifact.Sha256, artifact.Path);
    }

    private static void ValidateSha256(string? value, string description) =>
        Keccak256OpcodeLeanEmitter.ValidateSha256(value, description);

    private static string CombinedSourceHash(IEnumerable<SourceIdentity> sources, IEnumerable<RawSourceIdentity> raw) =>
        Hash(Encoding.UTF8.GetBytes(string.Join("\n", sources.Select(static item => $"{item.Path}\0{item.Sha256}\0csharp14")
            .Concat(raw.Select(static item => $"{item.Path}\0{item.Sha256}\0raw")))));

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string ForkPath(string stem) => $"src/Nethermind/Nethermind.Specs/Forks/{stem}.cs";

    private static string AdmissionKey(AdmissionSpec spec) =>
        $"{spec.SourcePath}:{spec.Namespace}:{spec.OwnerPath}:{spec.MemberKind}:{spec.MemberName}/{spec.MemberGenericArity}({spec.ParameterTypes})";

    private static string AdmissionKey(AdmissionIdentity identity) =>
        $"{identity.SourcePath}:{identity.Namespace}:{identity.OwnerPath}:{identity.MemberKind}:{identity.MemberName}/{identity.MemberGenericArity}({identity.ParameterTypes})";

    private static string ExpectedSyntaxKind(AdmissionSpec spec) => spec.MemberKind switch
    {
        "type" when spec.MemberName == "Instruction" => "EnumDeclaration",
        "type" when spec.MemberName is "IFlag" or "IOpcodeBody" => "InterfaceDeclaration",
        "type" when spec.MemberName is "OffFlag" or "OnFlag" => "StructDeclaration",
        "type" => "ClassDeclaration",
        "method" => "MethodDeclaration",
        "property" => "PropertyDeclaration",
        "field" => "FieldDeclaration",
        _ => throw new ExtractionException($"Unsupported admission kind {spec.MemberKind}."),
    };

    private static AdmissionSpec A(string path, string ns, string owner, string kind, string name,
        int arity = 0, string parameters = "") => new(path, ns, owner, kind, name, arity, parameters);

    private static void Require(string source, string required, string description)
    {
        if (!source.Contains(required, StringComparison.Ordinal))
            throw new ExtractionException($"Rejected {description}; expected '{required}'.");
    }

    private static void RequireExactlyOnce(string source, string required, string description)
    {
        int first = source.IndexOf(required, StringComparison.Ordinal);
        if (first < 0 || source.IndexOf(required, first + required.Length, StringComparison.Ordinal) >= 0)
            throw new ExtractionException($"Rejected {description}; expected exactly one '{required}'.");
    }

    private static void RequireOrdered(string source, IReadOnlyList<string> required, string description)
    {
        int position = 0;
        foreach (string fragment in required)
        {
            int next = source.IndexOf(fragment, position, StringComparison.Ordinal);
            if (next < 0)
                throw new ExtractionException($"Rejected {description}; ordered fragment '{fragment}' is missing or moved.");
            position = next + fragment.Length;
        }
    }

    // Filled from one reviewed source snapshot; extraction refuses every byte change before semantic emission.
    private static IReadOnlyDictionary<string, string> BuildExpectedSourceSha256() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/Nethermind/Nethermind.Core/Crypto/KeccakCache.std.cs"] = "24f83fb87217b33d3f6f53111ba52c24cb8b2626c80b898ea49531a3554c1103",
            ["src/Nethermind/Nethermind.Core/GasCostOf.cs"] = "10f1f1a29e7bf10a8220422a6f7571af60df4f05ad4075268e3b26559a73c6f5",
            ["src/Nethermind/Nethermind.Core/TypeFlags.cs"] = "33d8404d3ec39fffbc9f665126255f9799a0ba433206a141f480f8144be95747",
            ["src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs"] = "99c58367e5b90fa5904484f6e2fbf10745a54f6cd7d07275810c27028ad0eb2b",
            ["src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs"] = "853f8f9b46e5deef1c655442694dfbb1f862de80d0ecdbee2d66e7282f974e84",
            ["src/Nethermind/Nethermind.Evm/EvmStack.cs"] = "561520be4f0fe1d533ddea9cfea7ff48edb2745b1f3adf1b45c24e6c0bed02ad",
            ["src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs"] = "3c31ba40c24a78bf11243b38859168350f02934806124e913da0560bc1a4350f",
            ["src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs"] = "b4d26db87723243514f2f6b8a5e48d1176139105c3b012ad0f3956e59afc98a8",
            ["src/Nethermind/Nethermind.Evm/Instruction.cs"] = "6895d06277ac4f9369d7bdc748976576bf384f6f3a5a348a2e4fd3767a46a030",
            ["src/Nethermind/Nethermind.Evm/Instructions/EvmCalculations.cs"] = "1912ef2add71990f29ab0cb7ef1de745b44f76bd201d4ac76c3032d5f60dc244",
            ["src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Crypto.cs"] = "c86a9c3219b12b5838cf6a8d4ca973bc9d101926a859cf88b8a7b544c6787c94",
            ["src/Nethermind/Nethermind.Evm/Tracing/TracerExtensions.cs"] = "4704eb408436ecb82614bbf1db26f11fcfcfa65ef613146e37a31f7e30a8d662",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.cs"] = "45edba3691e09185e749485785990662ddea1af849bc6e4564f92b68a5137a6b",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs"] = "4f36bb20057caec9c85bcd3621372563f4d47ba36a9cbf01be267af4e379bca1",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs"] = "5148dd594e40ebb976179b1048f233914f54acc8d033d358cf3e2e7594e112b6",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.std.cs"] = "357fbd35369424a379c8c29338f491627512d40a12199d7a3d9aac44c87ca92a",
            ["src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs"] = "fce65ce5bb523c56fc8aa6ee0e4d092c940a5a90b174c820ffe262eeb5241c60",
            ["src/Nethermind/Nethermind.Specs/Forks/00_Olympic.cs"] = "8874f5ed3ae7bf5a0e0d1d127babd258393abcc3093b98abc296a1a347d61246",
            ["src/Nethermind/Nethermind.Specs/Forks/01_Frontier.cs"] = "28318120065dc43c084c998b4cdef83669fa7b036ebe850802f0f9ff860df075",
            ["src/Nethermind/Nethermind.Specs/Forks/02_Homestead.cs"] = "52dae1ff91fcc53fc95e4dc9adab9ce9686411620b88135a408728d56d6b0ad2",
            ["src/Nethermind/Nethermind.Specs/Forks/03_Dao.cs"] = "9354856fe91fde3d1394828e94bb700b6c765b9d42425221b6e1554898c34455",
            ["src/Nethermind/Nethermind.Specs/Forks/04_TangerineWhistle.cs"] = "82a6ea651cb884be41e1105d73cf726e1f095195322bf3293f663658da40939f",
            ["src/Nethermind/Nethermind.Specs/Forks/05_SpuriousDragon.cs"] = "7fb950f37739ad6435c4dc5bcd3bf9b9566d3a6137479675276cea5a07196af5",
            ["src/Nethermind/Nethermind.Specs/Forks/06_Byzantium.cs"] = "c47fcc9740483de8081c4591aa6b2994271602dacc0a0fb590e63e39f0288e73",
            ["src/Nethermind/Nethermind.Specs/Forks/07_Constantinople.cs"] = "a0ee60c7fb44400a18e1b5d6cc3fe324de557023c34f5c7427ecaf72da17cfbe",
            ["src/Nethermind/Nethermind.Specs/Forks/08_ConstantinopleFix.cs"] = "1a0336f8a06e64f40cdfbba3ecac79158ddf9f38d9694df346916c819819a6a5",
            ["src/Nethermind/Nethermind.Specs/Forks/09_Istanbul.cs"] = "911764bbfd46d09c055d8a5a0a699c62b65ac96a528f76c294044a4c84b84714",
            ["src/Nethermind/Nethermind.Specs/Forks/10_MuirGlacier.cs"] = "76508e7a64c3bf94a8da0145fedb4f277c1d06e75454a53d1c2d036fc9e9f5e8",
            ["src/Nethermind/Nethermind.Specs/Forks/11_Berlin.cs"] = "b02b45deb5dedddc9df19283c7491ffc3ab10ab0aaae313e44cdafca9ee547b0",
            ["src/Nethermind/Nethermind.Specs/Forks/12_London.cs"] = "06ea8bd017ccff59759026a801138bd794919e8493d49f8ef05385276662f8ef",
            ["src/Nethermind/Nethermind.Specs/Forks/13_ArrowGlacier.cs"] = "267c4b5529156e692ac1d1827f189b988f07d398bda986befef9561234c94491",
            ["src/Nethermind/Nethermind.Specs/Forks/14_GrayGlacier.cs"] = "a3904920bc25153c271057fe3b990b183c9cf93bba3a393c1f7561c750907a2e",
            ["src/Nethermind/Nethermind.Specs/Forks/15_Paris.cs"] = "90c0f08765e2b50a8fadc2fdfcbe43965b9352e0a17ac692676403eb95972abd",
            ["src/Nethermind/Nethermind.Specs/Forks/16_Shanghai.cs"] = "dcbfdacf3b6c7a8db0e8d5abc1ad3c388d407ae989395d52f1725721a5cc767e",
            ["src/Nethermind/Nethermind.Specs/Forks/17_Cancun.cs"] = "29b93ca43e919352495a0406242d7b49ce9423117aeec2e1bb94427fff20aa79",
            ["src/Nethermind/Nethermind.Specs/Forks/18_Prague.cs"] = "949248f5f22162433e7f4e17a7e5a35b4cf54416836738831edd3b1c0e5a6b8c",
            ["src/Nethermind/Nethermind.Specs/Forks/19_Osaka.cs"] = "24da07f0c85e3c5822c10f010c4b35a5ca21046ac6b21f5bb26397de2ef5cf77",
            ["src/Nethermind/Nethermind.Specs/Forks/20_BPO1.cs"] = "6fc6d2c0fc7159882c03ad44006da8aad1354d68d2d1fe750ef8a551b43bbad8",
            ["src/Nethermind/Nethermind.Specs/Forks/21_BPO2.cs"] = "4788ce812f73163686b2b419970d70bdae4de73523fe6ad426a618dd1f1d40d9",
            ["src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs"] = "4dfbf9079e9dce30423327ae4433f32e49388d403a8fedfcf253f94f79450c6a",
            ["src/Nethermind/Nethermind.Specs/Forks/NamedReleaseSpec.cs"] = "ea9400ed44270fb18766a5dbd20d4dd7e7ee95b1bb7ab7ee6d5406cca03a46cc",
        };

    // Filled from the exact Roslyn nodes in that same snapshot; the manifest is independently strict at this boundary.
    private static IReadOnlyDictionary<string, string> BuildExpectedAdmissionSha256() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/Nethermind/Nethermind.Evm/Instruction.cs:Nethermind.Evm:namespace:type:Instruction/0()"] = "711a9a9e715ec14a301bfa797cafa90c5edb45896a6be9657f208af0950f7e0e",
            ["src/Nethermind/Nethermind.Core/GasCostOf.cs:Nethermind.Core:GasCostOf`0:field:Memory/0()"] = "186c8fa0f801e0d359c5afbf17d76c2743f01b24f32ae141a664c247989abca1",
            ["src/Nethermind/Nethermind.Core/GasCostOf.cs:Nethermind.Core:GasCostOf`0:field:Sha3/0()"] = "8ea390107751a2671356bf7996cf794bda6a03b1f33a1dc91e337bc194bb2c99",
            ["src/Nethermind/Nethermind.Core/GasCostOf.cs:Nethermind.Core:GasCostOf`0:field:Sha3Word/0()"] = "e3a84879e9503d803a8ae8695a044ea05e422eaa444da24b6ca7a9258a3848b4",
            ["src/Nethermind/Nethermind.Core/TypeFlags.cs:Nethermind.Core:namespace:type:IFlag/0()"] = "fc07b156384dfbfbf109a7040bf2f11786dcbc50e57635de31e9344d46d3a069",
            ["src/Nethermind/Nethermind.Core/TypeFlags.cs:Nethermind.Core:namespace:type:OffFlag/0()"] = "837798a144835ad9e52d59074387712c4a0f8bcec6be7c51cde55609fcf70103",
            ["src/Nethermind/Nethermind.Core/TypeFlags.cs:Nethermind.Core:namespace:type:OnFlag/0()"] = "add37b85db306767de5712b3032ac4a9052f293dd03d5ce4e4f21d8640da9849",
            ["src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Crypto.cs:Nethermind.Evm:EvmInstructions`0:method:InstructionKeccak256/2(EvmStack,TGasPolicy,VirtualMachine<TGasPolicy>)"] = "91727a4afe7e7eb0189e9c3aced1798e4bdfdc88fbe3d9140283547e7153d0ff",
            ["src/Nethermind/Nethermind.Evm/Instructions/EvmCalculations.cs:Nethermind.Evm:EvmCalculations`0:method:Div32Ceiling/0(UInt256,bool)"] = "1a35c64cc2a93a5947c56091bd9815b7cc412410e57cc7a33a50a9d4ed26af19",
            ["src/Nethermind/Nethermind.Evm/Instructions/EvmCalculations.cs:Nethermind.Evm:EvmCalculations`0:method:Div32Ceiling/0(ulong,bool)"] = "9470f09d7ee09e06bd520e18612ba34d170f4073ea384e69c4f4f650a608cf4d",
            ["src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs:Nethermind.Evm.GasPolicy:IGasPolicy`1:method:TryConsumeKeccak/0(TSelf,ulong)"] = "21727d6f199bf3a442f544d86461f89c803cd41531ab120a7e66d27bf1b8f37d",
            ["src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs:Nethermind.Evm.GasPolicy:IGasPolicy`1:method:UpdateMemoryCost/0(TSelf,UInt256,UInt256,EvmPooledMemory)"] = "63c9263a32d7733c6d440ed40714e92802e483ce119e4390cdf6b911d8b483a7",
            ["src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs:Nethermind.Evm.GasPolicy:IGasPolicy`1:method:UpdateGas/0(TSelf,ulong)"] = "1964249d61021f4199701ead2eab741de8970035430529ce3bc906b067350ba2",
            ["src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs:Nethermind.Evm.GasPolicy:EthereumGasPolicy`0:field:Value/0()"] = "8b0dca644a5059c8b7647ca6bce5d6ac05a9cfbc25a1ba8995c58a7a672e5d43",
            ["src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs:Nethermind.Evm.GasPolicy:EthereumGasPolicy`0:field:StateReservoir/0()"] = "c6bbef4512e94b64ccde209e4ee37fbeef7b9a535d1954fcfb54662037c338f5",
            ["src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs:Nethermind.Evm.GasPolicy:EthereumGasPolicy`0:field:StateGasUsed/0()"] = "d2568296f9d0812aedac0a09d026ffe7992ff7fe26b248c71f3d3e8ea88c2b74",
            ["src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs:Nethermind.Evm.GasPolicy:EthereumGasPolicy`0:field:StateGasSpill/0()"] = "0b9377b26502c708c677ed838688f407cce7a61b4d6df8edc0f9e030bd114443",
            ["src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs:Nethermind.Evm.GasPolicy:EthereumGasPolicy`0:field:StateGasSpillRefunded/0()"] = "a78c87d243fcf0db967b7e754589ab0c88b0aade7d4225339de1e5627a6493a9",
            ["src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs:Nethermind.Evm.GasPolicy:EthereumGasPolicy`0:method:GetRemainingGas/0(EthereumGasPolicy)"] = "bbf1839beae4849581235e1dfef8499fa63b755d934e9eff831281b6171327e2",
            ["src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs:Nethermind.Evm.GasPolicy:EthereumGasPolicy`0:method:ConsumeRaw/0(EthereumGasPolicy,ulong)"] = "485e64b9acbaad49810a5cc5cbebecddb853d1217f50a762126865d17aaa2057",
            ["src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs:Nethermind.Evm.GasPolicy:EthereumGasPolicy`0:method:UpdateMemoryCost/0(EthereumGasPolicy,UInt256,UInt256,EvmPooledMemory)"] = "4eb4437c9c80c8e8e388f0cf6f930cb5b7a1bec14ca697dbab7cf91c1348438e",
            ["src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs:Nethermind.Evm.GasPolicy:EthereumGasPolicy`0:method:UpdateGas/0(EthereumGasPolicy,ulong)"] = "40d3c1827005de750f566096cdde91462e8a517b33616fc9a294583934ea6b73",
            ["src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs:Nethermind.Evm:EvmPooledMemory`0:field:WordSize/0()"] = "5cdd4cde5546f289daea1a411a921562c08d53c6a74b6247b9e831bf09550382",
            ["src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs:Nethermind.Evm:EvmPooledMemory`0:field:MaxMemorySize/0()"] = "0811ed8e92e677061531d6e02984b6b1740a2087e0912d00af25a8b1bb9fb439",
            ["src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs:Nethermind.Evm:EvmPooledMemory`0:property:Size/0()"] = "7593c27346c3059372e6ef46dade37354e5a476dceca059265e62b9805c4a5de",
            ["src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs:Nethermind.Evm:EvmPooledMemory`0:method:CheckMemoryAccessViolation/0(UInt256,UInt256,ulong,bool)"] = "53272a676adc4561660d53cbb41a0f8eb09f3b15a39a6c34cea6ea5afcbca381",
            ["src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs:Nethermind.Evm:EvmPooledMemory`0:method:CalculateMemoryCost/0(UInt256,UInt256,bool)"] = "26f85879a9bf25dd06ea38fe9cfe6a2e3e29654614609c7ce9f2c49eb213e620",
            ["src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs:Nethermind.Evm:EvmPooledMemory`0:method:ComputeMemoryExpansionCost/0(ulong)"] = "77e21ae6431f38b86a48ba1f62cf4ff2448e4564c441f0148a47fa354e712fb7",
            ["src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs:Nethermind.Evm:EvmPooledMemory`0:method:TryLoadSpan/0(UInt256,UInt256,Span<byte>)"] = "051779f3a3078fbfb8d153e3cf60cc20b04b31937d825edc23ada830f9a4457f",
            ["src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs:Nethermind.Evm:EvmPooledMemory`0:method:LoadSpan/0(ulong,int,int)"] = "6046a7c703fcef82518095028d0fa1551b829a421e0c544c8898ceddad5a9900",
            ["src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs:Nethermind.Evm:EvmPooledMemory`0:method:UpdateSize/0(ulong,bool)"] = "c6d29c8c3d22bae79ab7809587ebd83f073d97cb2f9755a43d068d307fba4aa4",
            ["src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs:Nethermind.Evm:EvmPooledMemory`0:method:EnsureRented/0(ulong)"] = "bcaf317a52160264b63dfe52e8a2d9b37c1671d7d1139d325fe1d88a868ebcb6",
            ["src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs:Nethermind.Evm:EvmPooledMemory`0:method:RentSlow/0(ulong)"] = "aee357292419cdd1a1c2fa265b4d6c5438feea26b90909240b7e654a190c8688",
            ["src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs:Nethermind.Evm:EvmPooledMemory`0:method:MaterializeArray/0(ulong)"] = "04cc04ec38ffa8a50e6f909074fea437fae1ee8331afc2580338eefffcef7f10",
            ["src/Nethermind/Nethermind.Evm/EvmStack.cs:Nethermind.Evm:EvmStack`0:field:Head/0()"] = "6617446b2ed31e23f5959ae5b801ed2a2714a6f0bfa8d608ce4f573e48491dae",
            ["src/Nethermind/Nethermind.Evm/EvmStack.cs:Nethermind.Evm:EvmStack`0:method:ReadUInt256FromSlot/0(byte,UInt256)"] = "1aa3995e0e1532c55ac2ab4fa72e456c57d170c85b436f769de1ed8eb2b18377",
            ["src/Nethermind/Nethermind.Evm/EvmStack.cs:Nethermind.Evm:EvmStack`0:method:ReadMemoryPositionFromSlot/0(byte,UInt256)"] = "b74af73a2b2805e82e82b8c488a94452a05d37bd4e11bec0c587b556100d3d0d",
            ["src/Nethermind/Nethermind.Evm/EvmStack.cs:Nethermind.Evm:EvmStack`0:method:PopMemoryPositionAndUInt256/0(UInt256,UInt256)"] = "fd85f4a7897838cf631088531e2787e47dc0395f771bc4052d78715f7fba363c",
            ["src/Nethermind/Nethermind.Evm/EvmStack.cs:Nethermind.Evm:EvmStack`0:method:Push32Bytes/2(byte)"] = "d02e5da318805b3f0d268ed65c6c3f0cf12ef0776f0127208531c17ae9738744",
            ["src/Nethermind/Nethermind.Evm/EvmStack.cs:Nethermind.Evm:EvmStack`0:method:Push32Bytes/2(ValueHash256)"] = "a5ad0f7e24a1d35a2780045bc7bc46f7a2909da210b2cad961f32f3d21f1711e",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs:Nethermind.Evm:VirtualMachine`1:type:IOpcodeBody/0()"] = "f866119a5b1f6c8190be70e2fdd66ecc5820696623310db9e37f5a081b56185c",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs:Nethermind.Evm:VirtualMachine`1:method:OpcodeHandler/3()"] = "ea9dc570ad183461562fb0a5b0f29569850de65bcb585621d37addbff58817a5",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs:Nethermind.Evm:VirtualMachine`1:method:GenerateOpcodeHandlers/2(IReleaseSpec)"] = "2a7f84d9ca5adea81b2d2c483ffc996e3fe42bda792bbea648aec674d826fe29",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs:Nethermind.Evm:VirtualMachine`1.KeccakOpcode`1:method:Execute/0(EvmStack,TGasPolicy,VirtualMachine<TGasPolicy>,nint)"] = "71ab89880b6e82401021d0b41a3b7b10551927ba793684d824b6b798bcbb852f",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs:Nethermind.Evm:VirtualMachine`1:method:GetOpcodeHandlers/2()"] = "75b75d65b636bb96fa1ae4640255cc1e23b1c3015882d7475fcc54d6dac1487b",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs:Nethermind.Evm:VirtualMachine`1:method:PrepareOpcodes/1()"] = "c44cee886ad0274aaf70b3ce0ff953775d85ce071c95a247f0b33f368d5575c9",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs:Nethermind.Evm:VirtualMachine`1:method:PrepareOpcodes/2()"] = "c9c7727aba6a582656f44004d907026c1f19a2fe16e5ef93ef0bd293bf9c5c4f",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs:Nethermind.Evm:VirtualMachine`1:method:RunDispatchLoop/2(EvmStack,TGasPolicy,nint)"] = "a49f1e30c2c935cfef7e9e7405443e07c2a73a950cdaebf9a00499bba8faa407",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs:Nethermind.Evm:VirtualMachine`1:method:ExecuteOpcode/4(EvmStack,TGasPolicy,DispatchState,nint,int)"] = "e19f009aacb4d39744b48d2201135255405303e62ee507038676672b514bb305",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs:Nethermind.Evm:VirtualMachine`1:method:ExitCheckedOpcode/0(DispatchState,nint,int,EvmExceptionType)"] = "a714a325afb85e760c40e8a202eea2bdc234b5de4006775b002123fca0bf865e",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs:Nethermind.Evm:VirtualMachine`1.OpcodeTable`0:field:NoTrace/0()"] = "f4445df4e4aa1687917dd66b5220cf76fdd2b01e81f86cf7e761ebfe4e8c9d7a",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs:Nethermind.Evm:VirtualMachine`1.OpcodeTable`0:field:NoTraceCancelable/0()"] = "7d76c46c9d29c614abea4af280cd6a3a9250998564b9d9649cbd1ff230592fc2",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs:Nethermind.Evm:VirtualMachine`1.OpcodeTable`0:field:Traced/0()"] = "52535c11a3546230fca09cf47222db5605dfc00f62edcf5a7410258485dc9575",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs:Nethermind.Evm:VirtualMachine`1.OpcodeTable`0:field:TracedCancelable/0()"] = "0f53153b187bc228e6d4f9fa5ee16b72ca8bc6d0ab2a1431cc898016e665f406",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs:Nethermind.Evm:VirtualMachine`1.OpcodeTable`0:method:GetHandlers/2(IReleaseSpec)"] = "ca8dc69d3c02595055222987f55eb1d625ca82a0f9f6e0b94382fcacf53ebae6",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.cs:Nethermind.Evm:namespace:type:EthereumVirtualMachine/0()"] = "2a36ed0c3e9cccf0f25b6752f1a5df5a9c912816a67d33c783f4d646979ff44a",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.cs:Nethermind.Evm:VirtualMachine`1:property:VmState/0()"] = "8ea2b141f677e578123de47b9d789e7aa277bfd837892072e384313a3aaecb01",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.cs:Nethermind.Evm:VirtualMachine`1:method:StartInstructionTrace/0(Instruction,ulong,int,EvmStack)"] = "862df1cdde4e3b57fd7b41b8041186b6e1faed7e7275528fba1806baf116d7b6",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.cs:Nethermind.Evm:VirtualMachine`1:method:EndInstructionTrace/0(ulong)"] = "9261d5cd80a318791064f07403b3359a4609f4fb9bdf380cf48b40f71cc8429c",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.cs:Nethermind.Evm:VirtualMachine`1:method:RunByteCode/2(EvmStack,TGasPolicy)"] = "b113f4b0ce8585a5e86f46fef5e8e596253de763ff1f7dfa1930d5118299f376",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.cs:Nethermind.Evm:VirtualMachine`1:method:GetFailureReturn/0(ulong,EvmExceptionType)"] = "5d87c56f75995336e228d3ac503f60370b81228c24c2549c6b18092ece9edd9d",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.cs:Nethermind.Evm:VirtualMachine`1:method:EndInstructionTraceError/0(ulong,EvmExceptionType)"] = "97afd538ba5d785e3bd33a21fd8ae116276bc05f08230a601217c6e94ba3da07",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.std.cs:Nethermind.Evm:VirtualMachine`1:method:GetOpcodeTable/0()"] = "01090d180bd50447a703f2a15d644172901b58657734e3d6c952054a834365aa",
            ["src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs:Nethermind.Evm:DispatchFlags`0:field:ConstTracing/0()"] = "c88a9a02dab591993c94d3d09a00c4b2e4a9e6586052b6ee9ccfd26a9f6732b5",
            ["src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs:Nethermind.Evm:DispatchFlags`0:method:Tracing/0(bool)"] = "ef9fc06dbfb8a9cf068fea200e16f0ee80b5ef726f68173a5360779426080da6",
            ["src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs:Nethermind.Evm:DispatchFlags`0:method:Cancelable/0(bool)"] = "4e7917fc7555440f13bde2f40e3fb3949a8b9bb50bfa0d30ff3cbae42e420a34",
            ["src/Nethermind/Nethermind.Evm/Tracing/TracerExtensions.cs:Nethermind.Evm.Tracing:TracerExtensions`0:method:TraceBytes/0(ITxTracer,byte,int)"] = "f7f8266b778c2ab8dbdd6f6746a4ce818eb185e3b3bf7d6530ecc94630bd896f",
            ["src/Nethermind/Nethermind.Core/Crypto/KeccakCache.std.cs:Nethermind.Core.Crypto:KeccakCache`0:method:ComputeTo/0(ReadOnlySpan<byte>,ValueHash256)"] = "9de5531a0fd5ba82c0b7b6e9a09de85bd5c3e59139ae83f3cbbd5f02f0a7a469",
            ["src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs:Nethermind.Init.Modules:BlockProcessingModule`0:method:Load/0(ContainerBuilder)"] = "94a0b9d89711080715bf075fca6130dcdb2b18335590f3e120b4b90ccc8037de",
            ["src/Nethermind/Nethermind.Specs/Forks/NamedReleaseSpec.cs:Nethermind.Specs.Forks:namespace:type:NamedReleaseSpec/0()"] = "c67d05aba92862851bc6b4971a6a94be69a6eb0d411b06c18cfa893d8183019f",
            ["src/Nethermind/Nethermind.Specs/Forks/NamedReleaseSpec.cs:Nethermind.Specs.Forks:namespace:type:NamedReleaseSpec/1()"] = "c453ca758e14070d8b8475161d6953f0262998d395fa8de49c535ddfcb8079c0",
            ["src/Nethermind/Nethermind.Specs/Forks/NamedReleaseSpec.cs:Nethermind.Specs.Forks:NamedReleaseSpec`0:method:ReplayAncestors/0(NamedReleaseSpec?)"] = "197348583bf442d5c5a9fc0177a324406a7d26193cee86b4672328303b7bae3b",
            ["src/Nethermind/Nethermind.Specs/Forks/00_Olympic.cs:Nethermind.Specs.Forks:namespace:type:Olympic/0()"] = "a89568d2b17c9a24e4ba0f8194d3d74fb9538484c688a44988cb2d7585eccdc6",
            ["src/Nethermind/Nethermind.Specs/Forks/01_Frontier.cs:Nethermind.Specs.Forks:namespace:type:Frontier/0()"] = "ed95c3a8b896340bd42595aec1fae40f0a8a902619f25cd3698eeec2803dee76",
            ["src/Nethermind/Nethermind.Specs/Forks/02_Homestead.cs:Nethermind.Specs.Forks:namespace:type:Homestead/0()"] = "eb82fdf0780624aa35fb644dd53b1bede3074a498875c455b550a9aea0a4a850",
            ["src/Nethermind/Nethermind.Specs/Forks/03_Dao.cs:Nethermind.Specs.Forks:namespace:type:Dao/0()"] = "f1d1de4a9a3c3d6fd51d5427ac6d015485000448b490aace37a449b9e2d5fc4a",
            ["src/Nethermind/Nethermind.Specs/Forks/04_TangerineWhistle.cs:Nethermind.Specs.Forks:namespace:type:TangerineWhistle/0()"] = "00bcad52ae07d3aa925f06626cbd043d0b9e78e4f383f2117e50f4683ff857f5",
            ["src/Nethermind/Nethermind.Specs/Forks/05_SpuriousDragon.cs:Nethermind.Specs.Forks:namespace:type:SpuriousDragon/0()"] = "5ea6253e1a8208afbd38b5900855c88b71e2d04b02ccf575f81dbaa3ad5a645e",
            ["src/Nethermind/Nethermind.Specs/Forks/06_Byzantium.cs:Nethermind.Specs.Forks:namespace:type:Byzantium/0()"] = "2e24aed2e4cf9c05f21823c8fe003723be06d8bc4b4bcd8d01f42abc14b754d2",
            ["src/Nethermind/Nethermind.Specs/Forks/07_Constantinople.cs:Nethermind.Specs.Forks:namespace:type:Constantinople/0()"] = "8ba5dd8f3ec2eb4a77f76e5eb57d567ad587d4ad951348e7c516a2fc304b6e71",
            ["src/Nethermind/Nethermind.Specs/Forks/08_ConstantinopleFix.cs:Nethermind.Specs.Forks:namespace:type:ConstantinopleFix/0()"] = "e23ac39b8c412592a2aeff00b18f002d59d151314bc4e809606a102e33dffe29",
            ["src/Nethermind/Nethermind.Specs/Forks/09_Istanbul.cs:Nethermind.Specs.Forks:namespace:type:Istanbul/0()"] = "224221efe334f9e699eb5c8e6cc3478818ddfff6aba09a2e323a85886ac67efd",
            ["src/Nethermind/Nethermind.Specs/Forks/10_MuirGlacier.cs:Nethermind.Specs.Forks:namespace:type:MuirGlacier/0()"] = "9a10850435d4a3a1f2a73d42e8777cc558fb41e5dfbb3f58e8afb821b3b7f64a",
            ["src/Nethermind/Nethermind.Specs/Forks/11_Berlin.cs:Nethermind.Specs.Forks:namespace:type:Berlin/0()"] = "1c69da72f7e816a1021a4bf8ecfc67f018bf9e5819732f9527d0ee859170ecb8",
            ["src/Nethermind/Nethermind.Specs/Forks/12_London.cs:Nethermind.Specs.Forks:namespace:type:London/0()"] = "b0f1f4ebc46356b95beb0262b0f06e6d4763d10b04b50a04233dd3a74f79058b",
            ["src/Nethermind/Nethermind.Specs/Forks/13_ArrowGlacier.cs:Nethermind.Specs.Forks:namespace:type:ArrowGlacier/0()"] = "f5762388f9dbd722ca578639303281c9c285f59b1f166a5bd65bef0bacb05620",
            ["src/Nethermind/Nethermind.Specs/Forks/14_GrayGlacier.cs:Nethermind.Specs.Forks:namespace:type:GrayGlacier/0()"] = "12232e6411e07c2d051d9d4e444f936c5cb748ad01ed968b78650bae78449f9c",
            ["src/Nethermind/Nethermind.Specs/Forks/15_Paris.cs:Nethermind.Specs.Forks:namespace:type:Paris/0()"] = "1999e99205f08d7f2325969af1807376e33b026c7f4fddddce36aea315e349b6",
            ["src/Nethermind/Nethermind.Specs/Forks/16_Shanghai.cs:Nethermind.Specs.Forks:namespace:type:Shanghai/0()"] = "824ecc1d55639196688d258b1f9549749b4f0c1280375488c1a4a811f15517e7",
            ["src/Nethermind/Nethermind.Specs/Forks/17_Cancun.cs:Nethermind.Specs.Forks:namespace:type:Cancun/0()"] = "b52061355dfd32dfbbf74752b5f06ec2e38f83ef637c56295d0198b164df0415",
            ["src/Nethermind/Nethermind.Specs/Forks/18_Prague.cs:Nethermind.Specs.Forks:namespace:type:Prague/0()"] = "0e7cf6a83a30bd3f5f59c45a48130e081598e7211dd46a608e7b0a87b6e7d294",
            ["src/Nethermind/Nethermind.Specs/Forks/19_Osaka.cs:Nethermind.Specs.Forks:namespace:type:Osaka/0()"] = "c7830661156611c43438d6d5dabb368146d76da1bbd3a53ea5972a5e974a681e",
            ["src/Nethermind/Nethermind.Specs/Forks/20_BPO1.cs:Nethermind.Specs.Forks:namespace:type:BPO1/0()"] = "b19c992ab216c1cca976caac1bdc09c5b1976ff93c2145f5c95363c20d215d8b",
            ["src/Nethermind/Nethermind.Specs/Forks/21_BPO2.cs:Nethermind.Specs.Forks:namespace:type:BPO2/0()"] = "5b88d37530f1a93543efd2f3d71d9211c1527624941a485578ed36b888b29309",
            ["src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs:Nethermind.Specs.Forks:namespace:type:Amsterdam/0()"] = "f0cd0162f76382e33cf5799b6544d7d30f8bf427d80b943d1f29a7f6d71ed5fd",
        };
}
