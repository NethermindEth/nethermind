// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.ControlFlowOpcodeExtractor;

internal static class ControlFlowOpcodeProfile
{
    internal const string IrFileName = "ControlFlowOpcodeKernel.ir.json";
    internal const string ManifestFileName = "ControlFlowOpcodeKernel.source-manifest.json";
    internal const string LeanFileName = "ControlFlowOpcodeKernel.lean";

    private const string ExtractorVersion = "1.0.0";
    private const string KernelName = "standard-mainnet-amsterdam-control-flow-operational";
    private const string AdmissionResourceSuffix = ".Admission.ProductionClosure.txt";
    private const string BuildPropsPath = "src/Nethermind/Directory.Build.props";
    private const string BuildPropsSha256 = "ad544425af1b0ec60334511abf79896a77843088457ef81b98a06b56d13e4e3c";
    private const string BuildTargetsPath = "src/Nethermind/Directory.Build.targets";
    private const string BuildTargetsSha256 = "0598cebaef1df41102a18a3f9ace810bed1e4e64b8471d055724b4a394dccec6";
    private const string OperationalSpecPath = "tools/Evm/Lean/ControlFlowOpcodeExtractor/Specification/ControlFlowExecution.lean";
    private const string OperationalSpecSha256 = "35a72e4a53391e9ae4b34065c1d386639d7ff09e4bb700a53b1f4612917c5328";
    private const string ExpectedCombinedSourceSha256 = "aef29a36090ffa29e8d96b903bdadd58fb6019a78da11d81f4fe07966942c768";
    private const string GasCostPath = "src/Nethermind/Nethermind.Core/GasCostOf.cs";
    private const string TypeFlagsPath = "src/Nethermind/Nethermind.Core/TypeFlags.cs";
    private const string ReleaseSpecPath = "src/Nethermind/Nethermind.Core/Specs/IReleaseSpec.cs";
    private const string ReleaseSpecExtensionsPath = "src/Nethermind/Nethermind.Core/Specs/IReleaseSpecExtensions.cs";
    private const string InstructionPath = "src/Nethermind/Nethermind.Evm/Instruction.cs";
    private const string BytesPath = "src/Nethermind/Nethermind.Core/Extensions/Bytes.cs";
    private const string BytesStandardPath = "src/Nethermind/Nethermind.Core/Extensions/Bytes.std.cs";
    private const string EvmWordExtensionsPath = "src/Nethermind/Nethermind.Core/Extensions/EvmWordExtensions.cs";
    private const string StackPath = "src/Nethermind/Nethermind.Evm/EvmStack.cs";
    private const string StackStandardPath = "src/Nethermind/Nethermind.Evm/EvmStack.std.cs";
    private const string MemoryPath = "src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs";
    private const string GasTagsPath = "src/Nethermind/Nethermind.Evm/GasPolicy/IGasCost.cs";
    private const string GasInterfacePath = "src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs";
    private const string GasPolicyPath = "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs";
    private const string ControlFlowPath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.ControlFlow.cs";
    private const string CallPath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.cs";
    private const string EnvironmentPath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Environment.cs";
    private const string DispatchPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs";
    private const string HandlersPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs";
    private const string VirtualMachinePath = "src/Nethermind/Nethermind.Evm/VirtualMachine.cs";
    private const string VirtualMachineStandardPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.std.cs";
    private const string VmStatePath = "src/Nethermind/Nethermind.Evm/VmState.cs";
    private const string DispatchFlagsPath = "src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs";
    private const string AnalyzerPath = "src/Nethermind/Nethermind.Evm/CodeAnalysis/JumpDestinationAnalyzer.cs";
    private const string AnalyzerStandardPath = "src/Nethermind/Nethermind.Evm/CodeAnalysis/JumpDestinationAnalyzer.std.cs";
    private const string CodeInfoPath = "src/Nethermind/Nethermind.Evm/CodeAnalysis/CodeInfo.cs";
    private const string BlockContextPath = "src/Nethermind/Nethermind.Evm/BlockExecutionContext.cs";
    private const string BlockHeaderPath = "src/Nethermind/Nethermind.Core/BlockHeader.cs";
    private const string TraceInterfacePath = "src/Nethermind/Nethermind.Evm/Tracing/ITxTracer.cs";
    private const string TracerExtensionsPath = "src/Nethermind/Nethermind.Evm/Tracing/TracerExtensions.cs";
    private const string TraceStackPath = "src/Nethermind/Nethermind.Evm/Tracing/TraceStack.cs";
    private const string TraceMemoryPath = "src/Nethermind/Nethermind.Evm/Tracing/TraceMemory.cs";
    private const string BlockProcessingModulePath = "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs";
    private const string NamedReleaseSpecPath = "src/Nethermind/Nethermind.Specs/Forks/NamedReleaseSpec.cs";
    private const string ForkDirectory = "src/Nethermind/Nethermind.Specs/Forks";

    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp14);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    private static readonly string[] ForkLineage =
    [
        "Olympic", "Frontier", "Homestead", "Dao", "TangerineWhistle", "SpuriousDragon",
        "Byzantium", "Constantinople", "ConstantinopleFix", "Istanbul", "MuirGlacier",
        "Berlin", "London", "ArrowGlacier", "GrayGlacier", "Paris", "Shanghai", "Cancun",
        "Prague", "Osaka", "BPO1", "BPO2", "Amsterdam",
    ];

    private static readonly AdmissionSpec[] Admissions = BuildAdmissionSpecs();

    internal static IReadOnlyList<string> SourcePaths => Admissions.Select(static value => value.SourcePath)
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    internal static IReadOnlyList<string> InputPaths => [.. SourcePaths, BuildPropsPath, BuildTargetsPath, OperationalSpecPath];

    internal static ExtractionResult Extract(string repoRoot, string outputDirectory, string? leanOutputPath = null)
    {
        string root = Path.GetFullPath(repoRoot);
        Dictionary<string, (string Raw, string Syntax)> reviewed = LoadReviewedSourceIndex();
        Dictionary<string, SourceFile> sources = LoadSources(root, reviewed);
        RawSourceIdentity buildProps = LoadRawIdentity(root, BuildPropsPath, reviewed: null);
        RawSourceIdentity build = LoadRawIdentity(root, BuildTargetsPath, reviewed: null);
        RawSourceIdentity operational = LoadRawIdentity(root, OperationalSpecPath, reviewed: null);
        ValidateRawAdmission(root, buildProps, BuildPropsSha256);
        ValidateRawAdmission(root, build, BuildTargetsSha256);
        ValidateRawAdmission(root, operational, OperationalSpecSha256);
        List<AdmissionIdentity> admissions = AdmitSyntax(sources);
        ValidateNoCompetingMembers(root, sources);
        ValidateProductionClosure(sources);

        IrDocument ir = ExpectedIr();
        byte[] irBytes = Serialize(ir);
        IrDocument roundTrip = DeserializeIr(irBytes);
        string irSha = Hash(irBytes);
        SourceIdentity[] sourceIdentities = sources.Values.OrderBy(static value => value.RelativePath, StringComparer.Ordinal)
            .Select(static value => new SourceIdentity(value.RelativePath, value.Sha256,
                Hash(CompleteCanonical(value.Root)))).ToArray();
        RawSourceIdentity[] rawSources = [buildProps, build, operational];
        string combinedSourceSha = CombinedSourceHash(sourceIdentities, rawSources, admissions);
        byte[] template = File.ReadAllBytes(ResolveExactPath(root, OperationalSpecPath));
        byte[] leanBytes = ControlFlowOpcodeLeanEmitter.Emit(roundTrip, irSha, combinedSourceSha, template);
        SourceManifest manifest = new(
            1,
            ExtractorVersion,
            typeof(CSharpSyntaxTree).Assembly.GetName().Version?.ToString() ?? "unknown",
            "CSharp14",
            KernelName,
            sourceIdentities,
            rawSources,
            [.. admissions],
            new(IrFileName, irSha),
            new(LeanFileName, Hash(leanBytes)),
            combinedSourceSha,
            SemanticBindings());
        byte[] manifestBytes = Serialize(manifest);
        SourceManifest roundTripManifest = DeserializeManifest(manifestBytes);
        ValidateManifest(roundTripManifest, manifest);
        ValidateArtifactBindings(roundTripManifest, irBytes, leanBytes);

        Directory.CreateDirectory(outputDirectory);
        string irPath = Path.Combine(outputDirectory, IrFileName);
        string manifestPath = Path.Combine(outputDirectory, ManifestFileName);
        string leanPath = leanOutputPath ?? Path.Combine(outputDirectory, LeanFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(leanPath)!);
        WriteDeterministic(irPath, irBytes);
        WriteDeterministic(manifestPath, manifestBytes);
        WriteDeterministic(leanPath, leanBytes);
        return new(irPath, manifestPath, leanPath, ir.Opcodes.Length, ir.Specializations.Length, admissions.Count);
    }

    internal static IrDocument DeserializeIr(byte[] bytes)
    {
        RejectDuplicateProperties(bytes, "IR");
        try
        {
            IrDocument value = JsonSerializer.Deserialize<IrDocument>(bytes, JsonOptions)
                ?? throw new ExtractionException("Serialized control-flow opcode IR is empty.");
            ValidateIr(value);
            return value;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Serialized control-flow opcode IR is not valid JSON: {exception.Message}");
        }
    }

    internal static SourceManifest DeserializeManifest(byte[] bytes)
    {
        RejectDuplicateProperties(bytes, "manifest");
        try
        {
            SourceManifest value = JsonSerializer.Deserialize<SourceManifest>(bytes, JsonOptions)
                ?? throw new ExtractionException("Serialized control-flow opcode manifest is empty.");
            ValidateManifestShape(value);
            return value;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Serialized control-flow opcode manifest is not valid JSON: {exception.Message}");
        }
    }

    internal static void ValidateIr(IrDocument value)
    {
        if (value.Schedule is null || value.JumpValidation is null || value.DispatchTables is null || value.Opcodes is null ||
            value.Specializations is null || value.ForkLineage is null || value.SemanticBindings is null ||
            value.OpenExtractionObligations is null || value.DispatchTables.Any(static item => item is null) ||
            value.Opcodes.Any(static item => item is null || item.EffectOrder is null) ||
            value.Specializations.Any(static item => item is null))
            throw new ExtractionException("Serialized control-flow opcode IR contains null fields, collections, or entries.");
        if (!Serialize(value).AsSpan().SequenceEqual(Serialize(ExpectedIr())))
            throw new ExtractionException("Serialized control-flow opcode IR is not the exact admitted profile.");
    }

    internal static void ValidateManifest(SourceManifest value, SourceManifest expected)
    {
        ValidateManifestShape(value);
        if (!Serialize(value).AsSpan().SequenceEqual(Serialize(expected)))
            throw new ExtractionException("Serialized control-flow opcode manifest does not exactly match extracted lineage.");
    }

    internal static void ValidateArtifactBindings(SourceManifest manifest, byte[] irBytes, byte[] leanBytes)
    {
        IrDocument ir = DeserializeIr(irBytes);
        if (!Serialize(ir).AsSpan().SequenceEqual(irBytes))
            throw new ExtractionException("Control-flow IR bytes are not canonical serialization.");
        if (manifest.Ir.Path != IrFileName || manifest.Ir.Sha256 != Hash(irBytes))
            throw new ExtractionException("Control-flow manifest IR digest does not match canonical IR bytes.");
        if (manifest.Lean.Path != LeanFileName || manifest.Lean.Sha256 != Hash(leanBytes))
            throw new ExtractionException("Control-flow manifest Lean digest does not match emitted Lean bytes.");
    }

    internal static byte[] Serialize<T>(T value) => Utf8WithoutBom.GetBytes(
        JsonSerializer.Serialize(value, JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");

    private static void ValidateManifestShape(SourceManifest value)
    {
        if (value.Sources is null || value.RawSources is null || value.Admissions is null || value.Ir is null ||
            value.Lean is null || value.SemanticBindings is null || value.Sources.Any(static item => item is null) ||
            value.RawSources.Any(static item => item is null) || value.Admissions.Any(static item => item is null) ||
            value.Admissions.Any(static item => item.SourcePath is null || item.Namespace is null ||
                item.OwnerName is null || item.MemberKind is null || item.MemberName is null ||
                item.ParameterTypes is null || item.SyntaxKind is null || item.Sha256 is null))
            throw new ExtractionException("Serialized control-flow opcode manifest contains null fields, collections, or entries.");
        if (value.SchemaVersion != 1 || value.ExtractorVersion != ExtractorVersion || value.Kernel != KernelName ||
            value.LanguageVersion != "CSharp14" ||
            value.RoslynVersion != (typeof(CSharpSyntaxTree).Assembly.GetName().Version?.ToString() ?? "unknown"))
            throw new ExtractionException("Serialized control-flow opcode manifest header changed.");
        if (value.Sources.Length != SourcePaths.Count ||
            value.Sources.Select(static item => item.Path).Distinct(StringComparer.Ordinal).Count() != value.Sources.Length)
            throw new ExtractionException("Serialized control-flow source identity set changed or collides.");
        Dictionary<string, (string Raw, string Syntax)> reviewed = LoadReviewedSourceIndex();
        for (int index = 0; index < value.Sources.Length; index++)
        {
            SourceIdentity source = value.Sources[index];
            ValidateSha256(source.Sha256, source.Path);
            ValidateSha256(source.RoslynSyntaxSha256, source.Path + " syntax");
            if (source.Path != SourcePaths[index] ||
                !reviewed.TryGetValue(source.Path, out (string Raw, string Syntax) expected) ||
                source.Sha256 != expected.Raw || source.RoslynSyntaxSha256 != expected.Syntax)
                throw new ExtractionException($"Serialized control-flow source identity changed for {source.Path}.");
        }
        if (value.RawSources.Length != 3 ||
            value.RawSources.Select(static item => item.Path).Distinct(StringComparer.Ordinal).Count() != 3 ||
            value.RawSources[0].Path != BuildPropsPath || value.RawSources[0].Sha256 != BuildPropsSha256 ||
            value.RawSources[1].Path != BuildTargetsPath || value.RawSources[1].Sha256 != BuildTargetsSha256 ||
            value.RawSources[2].Path != OperationalSpecPath || value.RawSources[2].Sha256 != OperationalSpecSha256)
            throw new ExtractionException("Serialized control-flow raw-source identity set changed.");
        foreach (RawSourceIdentity source in value.RawSources) ValidateSha256(source.Sha256, source.Path);
        if (value.Admissions.Length != Admissions.Sum(static item => item.Members.Length) ||
            value.Admissions.Select(AdmissionKey).Distinct(StringComparer.Ordinal).Count() != value.Admissions.Length)
            throw new ExtractionException("Serialized control-flow exact-admission set changed or collides.");
        string[] expectedAdmissionKeys = Admissions.SelectMany(static spec => spec.Members.Select(selector =>
            AdmissionKey(spec, selector))).ToArray();
        for (int index = 0; index < value.Admissions.Length; index++)
        {
            AdmissionIdentity admission = value.Admissions[index];
            ValidateSha256(admission.Sha256, AdmissionKey(admission));
            if (AdmissionKey(admission) != expectedAdmissionKeys[index] || string.IsNullOrWhiteSpace(admission.SyntaxKind))
                throw new ExtractionException($"Serialized control-flow admission qualifier changed at index {index}.");
        }
        if (value.Ir.Path != IrFileName || value.Lean.Path != LeanFileName)
            throw new ExtractionException("Serialized control-flow artifact path changed.");
        ValidateSha256(value.Ir.Sha256, value.Ir.Path);
        ValidateSha256(value.Lean.Sha256, value.Lean.Path);
        byte[] canonicalIr = Serialize(ExpectedIr());
        string canonicalIrSha = Hash(canonicalIr);
        if (value.Ir.Sha256 != canonicalIrSha)
            throw new ExtractionException("Serialized control-flow IR artifact digest is not canonical.");
        ValidateSha256(value.CombinedSourceSha256, "combined source");
        if (value.CombinedSourceSha256 != CombinedSourceHash(value.Sources, value.RawSources, value.Admissions))
            throw new ExtractionException("Serialized control-flow combined source digest is inconsistent.");
        if (value.CombinedSourceSha256 != ExpectedCombinedSourceSha256)
            throw new ExtractionException(
                $"Serialized control-flow source closure is not the admitted closure; found {value.CombinedSourceSha256}.");
        byte[] canonicalLean = ControlFlowOpcodeLeanEmitter.Emit(
            DeserializeIr(canonicalIr), canonicalIrSha, value.CombinedSourceSha256,
            LoadEmbeddedOperationalTemplate());
        if (value.Lean.Sha256 != Hash(canonicalLean))
            throw new ExtractionException("Serialized control-flow Lean artifact digest is not canonical.");
        if (!value.SemanticBindings.SequenceEqual(SemanticBindings(), StringComparer.Ordinal))
            throw new ExtractionException("Serialized control-flow semantic bindings changed.");
    }

    private static Dictionary<string, SourceFile> LoadSources(
        string root,
        IReadOnlyDictionary<string, (string Raw, string Syntax)> reviewed)
    {
        if (reviewed.Count != SourcePaths.Count || SourcePaths.Any(path => !reviewed.ContainsKey(path)))
            throw new ExtractionException("Production closure resource does not exactly match required source paths.");
        Dictionary<string, SourceFile> result = new(StringComparer.Ordinal);
        List<string> mismatches = [];
        foreach (string relativePath in SourcePaths)
        {
            string fullPath = ResolveExactPath(root, relativePath);
            byte[] bytes = File.ReadAllBytes(fullPath);
            string raw = Hash(bytes);
            CompilationUnitSyntax syntax = CSharpSyntaxTree.ParseText(
                Encoding.UTF8.GetString(bytes), ParseOptions, relativePath).GetCompilationUnitRoot();
            Diagnostic? error = syntax.SyntaxTree.GetDiagnostics().FirstOrDefault(static item => item.Severity == DiagnosticSeverity.Error);
            if (error is not null) throw new ExtractionException($"Roslyn rejected {relativePath}: {error}.");
            string syntaxHash = Hash(CompleteCanonical(syntax));
            (string ExpectedRaw, string ExpectedSyntax) = reviewed[relativePath];
            if (raw != ExpectedRaw)
                mismatches.Add($"Unadmitted complete source content for {relativePath}; found SHA-256 {raw}.");
            if (syntaxHash != ExpectedSyntax)
                mismatches.Add($"Unadmitted Roslyn syntax for {relativePath}; found SHA-256 {syntaxHash}.");
            result.Add(relativePath, new(relativePath, fullPath, bytes, raw, syntax));
        }
        if (mismatches.Count != 0) throw new ExtractionException(string.Join(Environment.NewLine, mismatches));
        return result;
    }

    private static RawSourceIdentity LoadRawIdentity(string root, string path,
        IReadOnlyDictionary<string, string>? reviewed)
    {
        string fullPath = ResolveExactPath(root, path);
        string sha = Hash(File.ReadAllBytes(fullPath));
        if (reviewed is not null && (!reviewed.TryGetValue(path, out string? expected) || expected != sha))
            throw new ExtractionException($"Unadmitted raw source content for {path}; found SHA-256 {sha}.");
        return new(path, sha);
    }

    private static void ValidateRawAdmission(string root, RawSourceIdentity source, string expectedSha256)
    {
        ValidateSha256(source.Sha256, source.Path);
        if (source.Sha256 != expectedSha256)
            throw new ExtractionException($"Unadmitted raw source content for {source.Path}; found SHA-256 {source.Sha256}.");
        if (source.Path == BuildPropsPath)
        {
            string content = File.ReadAllText(ResolveExactPath(root, source.Path));
            Require(content, "<Using Include=\"System.Runtime.Intrinsics.Vector256&lt;byte&gt;\" Alias=\"EvmWord\" />",
                "standard EvmWord alias");
        }
        else if (source.Path == BuildTargetsPath)
        {
            string content = File.ReadAllText(ResolveExactPath(root, source.Path));
            Require(content, "<Compile Remove=\"**/*.std.cs\" />", "zkEVM exclusion of standard sources");
            Require(content, "<Compile Remove=\"**/*.zkevm.cs\" />", "standard exclusion of zkEVM sources");
        }
    }

    private static Dictionary<string, (string Raw, string Syntax)> LoadReviewedSourceIndex()
    {
        Assembly assembly = typeof(ControlFlowOpcodeProfile).Assembly;
        string resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(static value => value.EndsWith(AdmissionResourceSuffix, StringComparison.Ordinal))
            ?? throw new ExtractionException("Missing embedded production source admission resource.");
        using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
        using StreamReader reader = new(stream);
        Dictionary<string, (string Raw, string Syntax)> result = new(StringComparer.Ordinal);
        string? line;
        int lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            string[] fields = line.Split('|');
            if (fields.Length != 3 || fields.Any(static field => field.Length == 0 || field != field.Trim()))
                throw new ExtractionException($"Malformed production closure resource line {lineNumber}.");
            ValidateSha256(fields[1], $"raw source at line {lineNumber}");
            ValidateSha256(fields[2], $"syntax source at line {lineNumber}");
            if (!result.TryAdd(fields[0], (fields[1], fields[2])))
                throw new ExtractionException($"Duplicate production closure resource path {fields[0]}.");
        }
        return result;
    }

    private static byte[] LoadEmbeddedOperationalTemplate()
    {
        Assembly assembly = typeof(ControlFlowOpcodeProfile).Assembly;
        string resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(static value => value.EndsWith(".Specification.ControlFlowExecution.lean", StringComparison.Ordinal))
            ?? throw new ExtractionException("Missing embedded operational specification template.");
        using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
        using MemoryStream output = new();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static List<AdmissionIdentity> AdmitSyntax(IReadOnlyDictionary<string, SourceFile> sources)
    {
        List<AdmissionIdentity> result = [];
        foreach (AdmissionSpec spec in Admissions)
        {
            SyntaxNode owner = spec.OwnerName == "compilation-unit"
                ? sources[spec.SourcePath].Root
                : FindOwner(sources[spec.SourcePath].Root, spec.OwnerName, spec.OwnerGenericArity);
            foreach (MemberSelector selector in spec.Members)
            {
                MemberDeclarationSyntax member = FindSelectedMember(owner, selector);
                string actualNamespace = string.Join(".", member.AncestorsAndSelf()
                    .OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(static value => Canonical(value.Name)));
                if (actualNamespace != spec.Namespace)
                    throw new ExtractionException($"Expected namespace {spec.Namespace} for {AdmissionKey(spec, selector)}.");
                result.Add(new(spec.SourcePath, spec.Namespace, spec.OwnerName, spec.OwnerGenericArity,
                    selector.Kind, selector.Name, selector.GenericArity, selector.ParameterCount,
                    selector.ParameterTypes, member.Kind().ToString(), Hash(CompleteCanonical(member))));
            }
        }
        if (result.Select(AdmissionKey).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw new ExtractionException("Duplicate exact syntax admission.");
        return result;
    }

    private static void ValidateNoCompetingMembers(string root, IReadOnlyDictionary<string, SourceFile> sources)
    {
        HashSet<string> known = sources.Values.Select(static value => Path.GetFullPath(value.FullPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in new[]
        {
            "src/Nethermind/Nethermind.Core", "src/Nethermind/Nethermind.Evm",
            "src/Nethermind/Nethermind.Init", ForkDirectory,
        })
        {
            string absolute = ResolveExactPath(root, directory);
            foreach (string file in Directory.EnumerateFiles(absolute, "*.cs", SearchOption.AllDirectories))
            {
                string full = Path.GetFullPath(file);
                string portable = Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
                if (known.Contains(full) || portable.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
                    portable.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
                    portable.EndsWith(".zkevm.cs", StringComparison.OrdinalIgnoreCase)) continue;
                CompilationUnitSyntax syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(file), ParseOptions, portable)
                    .GetCompilationUnitRoot();
                foreach (AdmissionSpec spec in Admissions.Where(static value => value.OwnerName != "compilation-unit"))
                foreach (TypeDeclarationSyntax owner in syntax.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    if (owner.Identifier.ValueText != spec.OwnerName || GenericArity(owner) != spec.OwnerGenericArity) continue;
                    foreach (MemberSelector selector in spec.Members)
                    {
                        if (DirectMembers(owner).Any(member => Matches(member, selector)))
                            throw new ExtractionException($"Competing selected declaration in {portable}: {AdmissionKey(spec, selector)}.");
                    }
                }
            }
        }
    }

    private static void ValidateProductionClosure(IReadOnlyDictionary<string, SourceFile> sources)
    {
        string gasCosts = Canonical(sources[GasCostPath].Root);
        Require(gasCosts, "Base=2", "base gas constant");
        Require(gasCosts, "Mid=8", "mid gas constant");
        Require(gasCosts, "High=10", "high gas constant");
        Require(gasCosts, "Jump=Mid", "JUMP gas forwarding");
        Require(gasCosts, "JumpI=High", "JUMPI gas forwarding");
        Require(gasCosts, "JumpDest=1", "JUMPDEST gas constant");
        Require(gasCosts, "Memory=3", "memory linear gas constant");
        string gasTags = Canonical(sources[GasTagsPath].Root);
        Require(gasTags, "BaseGasCost:IGasCost", "base gas tag");
        Require(gasTags, "JumpGasCost:IGasCost", "JUMP gas tag");
        Require(gasTags, "JumpIGasCost:IGasCost", "JUMPI gas tag");
        Require(gasTags, "JumpDestGasCost:IGasCost", "JUMPDEST gas tag");
        Require(gasTags, "GasCost=>GasCostOf.Base", "base gas tag forwarding");
        Require(gasTags, "GasCost=>GasCostOf.Jump", "JUMP gas tag forwarding");
        Require(gasTags, "GasCost=>GasCostOf.JumpI", "JUMPI gas tag forwarding");
        Require(gasTags, "GasCost=>GasCostOf.JumpDest", "JUMPDEST gas tag forwarding");
        string instruction = Canonical(sources[InstructionPath].Root);
        foreach ((string opcode, string value) in new[]
        {
            ("STOP", "0x00"), ("SLOTNUM", "0x4b"), ("JUMP", "0x56"), ("JUMPI", "0x57"),
            ("PC", "0x58"), ("JUMPDEST", "0x5b"), ("RETURN", "0xf3"), ("REVERT", "0xfd"),
        }) Require(instruction, $"{opcode}={value}", $"{opcode} opcode byte");
        string typeFlags = Canonical(sources[TypeFlagsPath].Root);
        Require(typeFlags, "structOffFlag:IFlag{publicstaticboolIsActive=>false;}", "OffFlag value");
        Require(typeFlags, "structOnFlag:IFlag{publicstaticboolIsActive=>true;}", "OnFlag value");
        string dispatchFlags = Canonical(sources[DispatchFlagsPath].Root);
        Require(dispatchFlags, "ConstTracing=true", "standard tracing support");
        Require(dispatchFlags, "Tracing(boolisTracing)=>isTracing", "standard tracing selector");
        Require(dispatchFlags, "Cancelable(booltracerIsCancelable)=>tracerIsCancelable", "standard cancellation selector");
        Require(Canonical(sources[StackPath].Root), "MaxStackSize=1025", "1024-word logical stack capacity");
        string memorySource = Canonical(sources[MemoryPath].Root);
        Require(memorySource, "WordSize=32", "memory word size");
        Require(memorySource, "MaxMemorySize=int.MaxValue-WordSize+1", "maximum EVM memory size");
        Require(Canonical(sources[BytesPath].Root), "publicstaticunsafepartialclassBytes",
            "base Bytes partial owner");
        Require(Canonical(FindMethod(FindOwner(sources[BytesStandardPath].Root, "Bytes", 0), "Bswap64", 0, 1)),
            "BinaryPrimitives.ReverseEndianness(value)", "standard 64-bit byte swap");
        TypeDeclarationSyntax evmWordExtensions = FindOwner(sources[EvmWordExtensionsPath].Root, "EvmWordExtensions", 0);
        RequireOrdered(Canonical(FindMethod(evmWordExtensions, "ByteSwap", 0, 0)),
            "Avx512Vbmi.VL.IsSupported", "ByteSwap256Mask", "Avx2.IsSupported", "ByteSwap256Mask",
            "AdvSimd.Arm64.IsSupported", "ReverseBytes128Mask", "Bytes.Bswap64");
        Require(Canonical(FindSelectedMember(evmWordExtensions, P("ReverseBytes128Mask"))),
            "Vector128.Create((byte)15,14,13,12,11,10,9,8,7,6,5,4,3,2,1,0)",
            "ARM64 128-bit byte-swap mask");
        Require(Canonical(FindSelectedMember(evmWordExtensions, P("ByteSwap256Mask"))),
            "0x18191a1b1c1d1e1f", "256-bit byte-swap mask");

        TypeDeclarationSyntax handlers = FindOwner(sources[HandlersPath].Root, "VirtualMachine", 1);
        Require(Canonical(FindMethod(handlers, "OpcodeHandler", 3, 0)),
            "&ExecuteOpcode<TOpcode,TTracingInst,TCancelable,OnFlag>", "continuable handler root");
        Require(Canonical(FindMethod(handlers, "TerminatingOpcodeHandler", 3, 0)),
            "&ExecuteOpcode<TOpcode,TTracingInst,TCancelable,OffFlag>", "terminating handler root");
        Require(Canonical(FindMethod(handlers, "JumpIfOpcodeHandler", 2, 0)),
            "&ExecuteJumpIfOpcode<TTracingInst,TCancelable>", "JUMPI handler root");
        string opcodeBody = Canonical(FindOwner(sources[HandlersPath].Root, "IOpcodeBody", 0));
        Require(opcodeBody, "HasCheckedBody=>false", "unchecked handler default");
        Require(opcodeBody, "EndsInstructionTrace=>false", "instruction-trace ownership default");
        foreach ((string name, int arity) in new[]
        {
            ("BadInstructionOpcode", 0), ("StopOpcode", 0), ("SlotNumOpcode", 1), ("JumpOpcode", 1),
            ("ProgramCounterOpcode", 1), ("JumpDestOpcode", 0), ("ReturnOpcode", 0), ("RevertOpcode", 0),
        }) Reject(Canonical(FindOwner(sources[HandlersPath].Root, name, arity)),
            "EndsInstructionTrace", $"{name} instruction-trace ownership override");
        string generator = Canonical(FindMethod(handlers, "GenerateOpcodeHandlers", 2, 1));
        foreach (string assignment in new[]
        {
            "Instruction.STOP]=TerminatingOpcodeHandler<StopOpcode,TTracingInst,TCancelable>()",
            "Instruction.SLOTNUM]=OpcodeHandler<SlotNumOpcode<TTracingInst>,TTracingInst,TCancelable>()",
            "Instruction.JUMP]=OpcodeHandler<JumpOpcode<TTracingInst>,TTracingInst,TCancelable>()",
            "Instruction.JUMPI]=JumpIfOpcodeHandler<TTracingInst,TCancelable>()",
            "Instruction.PC]=OpcodeHandler<ProgramCounterOpcode<TTracingInst>,TTracingInst,TCancelable>()",
            "Instruction.JUMPDEST]=OpcodeHandler<JumpDestOpcode,TTracingInst,TCancelable>()",
            "Instruction.RETURN]=TerminatingOpcodeHandler<ReturnOpcode,TTracingInst,TCancelable>()",
            "Instruction.REVERT]=TerminatingOpcodeHandler<RevertOpcode,TTracingInst,TCancelable>()",
        }) RequireExactlyOnce(generator, assignment, $"dispatch assignment {assignment}");
        RequireOrdered(generator, "badInstruction=TerminatingOpcodeHandler<BadInstructionOpcode,TTracingInst,TCancelable>()",
            "lookup[i]=badInstruction", "if(spec.IsEip7843Enabled)", "Instruction.SLOTNUM]",
            "if(spec.RevertOpcodeEnabled)", "Instruction.REVERT]");
        string pcOpcode = Canonical(FindOwner(sources[HandlersPath].Root, "ProgramCounterOpcode", 1));
        RequireOrdered(pcOpcode, "HasCheckedBody", "!TTracingInst.IsActive", "UpdateGas<GasPolicy.BaseGasCost>",
            "StackGrowth=>1", "HasCheckedBody?stack.PushUInt32<TTracingInst,OffFlag>", "programCounter-1",
            "InstructionProgramCounter<TGasPolicy,TTracingInst>");
        string jumpOpcode = Canonical(FindOwner(sources[HandlersPath].Root, "JumpOpcode", 1));
        RequireOrdered(jumpOpcode, "TTracingInst.IsActive", "InstructionJump", "InstructionJumpAndSkipJumpDest",
            "programCounter=result.ProgramCounter", "returnresult.Exception");

        TypeDeclarationSyntax dispatch = FindOwner(sources[DispatchPath].Root, "VirtualMachine", 1);
        Require(Canonical(FindSelectedMember(FindOwner(sources[VirtualMachinePath].Root, "VirtualMachine", 1), P("Spec"))),
            "=>_blockExecutionContext.Spec", "dispatch release-spec source");
        RequireOrdered(Canonical(sources[VirtualMachinePath].Root),
            "_isCancelableCached=txTracer.IsCancelable", "PrepareOpcodes<TTracingInst>()");
        RequireOrdered(Canonical(FindMethod(dispatch, "PrepareOpcodes", 1, 0)),
            "DispatchFlags.Cancelable(_isCancelableCached)", "PrepareOpcodes<TTracingInst,OnFlag>",
            "PrepareOpcodes<TTracingInst,OffFlag>");
        TypeDeclarationSyntax opcodeTable = FindOwner(sources[DispatchPath].Root, "OpcodeTable", 0);
        string getHandlers = Canonical(FindMethod(opcodeTable, "GetHandlers", 2, 1));
        RequireOrdered(getHandlers, "TTracingInst.IsActive", "TCancelable.IsActive", "TracedCancelable",
            "Traced", "NoTraceCancelable", "NoTrace", "GenerateOpcodeHandlers<TTracingInst,TCancelable>(spec)");
        string execute = Canonical(FindMethod(dispatch, "ExecuteOpcode", 4, 5));
        RequireOrdered(execute, "StartInstructionTrace", "pc++", "opCodeCount++", "TryConsumeGas",
            "EnsureDepth", "StackGrowth", "TOpcode.Execute", "!TContinuable.IsActive", "exceptionType!=EvmExceptionType.None",
            "!TOpcode.EndsInstructionTrace", "EndInstructionTrace", "state.OpCodeCount=opCodeCount", "state.FinalProgramCounter=pc");
        string jumpIfDispatch = Canonical(FindMethod(dispatch, "ExecuteJumpIfOpcode", 2, 5));
        RequireOrdered(jumpIfDispatch, "StartInstructionTrace", "pc++", "opCodeCount++",
            "InstructionJumpIf", "InstructionJumpIfAndSkipJumpDest", "EndInstructionTrace");

        TypeDeclarationSyntax instructions = FindOwner(sources[ControlFlowPath].Root, "EvmInstructions", 0);
        RequireOrdered(Canonical(FindMethod(instructions, "InstructionProgramCounter", 2, 4)),
            "UpdateGas<BaseGasCost>", "PushUInt32<TTracingInst,OnFlag>", "programCounter-1");
        RequireOrdered(Canonical(FindMethod(instructions, "InstructionJumpDest", 1, 3)),
            "UpdateGas<JumpDestGasCost>", "EvmExceptionType.None");
        RequireOrdered(Canonical(FindMethod(instructions, "InstructionJump", 2, 4)),
            "UpdateGas<JumpGasCost>", "EnsureDepth(1)", "PopBytesByRefUnchecked", "JumpDestination", "SkipJumpDest", "PrefetchCodeAtDestination");
        RequireOrdered(Canonical(FindMethod(instructions, "InstructionJumpIf", 2, 4)),
            "UpdateGas<JumpIGasCost>", "EnsureDepth(2)", "Pop2BytesByRefUnchecked", "IsSlotZero", "JumpDestination", "SkipJumpDest");
        RequireOrdered(Canonical(FindMethod(instructions, "PrefetchCodeAtDestination", 0, 2)),
            "Sse.IsSupported", "stack.Code", "stack.CodeLength", "dest<codeLength", "Sse.Prefetch0");
        RequireOrdered(Canonical(FindMethod(instructions, "SkipJumpDest", 2, 4)),
            "programCounter=destination", "TSkipJumpDest.IsActive", "vm.OpCodeCount++", "programCounter++", "UpdateGas<JumpDestGasCost>");
        Require(Canonical(FindMethod(instructions, "InstructionStop", 1, 3)), "=>EvmExceptionType.Stop", "STOP result");
        RequireOrdered(Canonical(FindMethod(instructions, "InstructionRevert", 1, 3)),
            "PopMemoryPositionAndUInt256", "UpdateMemoryCost", "TryLoad", "ReturnData=returnData.ToArray()", "EvmExceptionType.Revert");
        RequireOrdered(Canonical(FindMethod(FindOwner(sources[CallPath].Root, "EvmInstructions", 0), "InstructionReturn", 1, 3)),
            "PopMemoryPositionAndUInt256", "UpdateMemoryCost", "TryLoad", "ReturnData=returnData.ToArray()", "EvmExceptionType.Stop");
        RequireOrdered(Canonical(FindMethod(FindOwner(sources[EnvironmentPath].Root, "EvmInstructions", 0), "InstructionSlotNum", 2, 3)),
            "Header.SlotNumber", "!slotNumber.HasValue", "UpdateGas<BaseGasCost>", "PushUInt64<TTracingInst,OnFlag>");

        TypeDeclarationSyntax memory = FindOwner(sources[MemoryPath].Root, "EvmPooledMemory", 0);
        string memoryRange256 = Canonical(FindSelectedMember(memory,
            M("CheckMemoryAccessViolation", 0, 4, "inUInt256,inUInt256,outulong,outbool")));
        RequireOrdered(memoryRange256, "!length.IsUint64", "isViolation=true", "newLength=0",
            "CheckMemoryAccessViolation(inlocation,length.u0,outnewLength,outisViolation)");
        string memoryRange64 = Canonical(FindSelectedMember(memory,
            M("CheckMemoryAccessViolation", 0, 4, "inUInt256,ulong,outulong,outbool")));
        RequireOrdered(memoryRange64, "length>MaxMemorySize", "!location.IsUint64", "isViolation=true",
            "offset=location.u0", "offset>MaxMemorySize-length", "isViolation=false", "newLength=offset+length");
        string memoryCost = Canonical(FindMethod(memory, "ComputeMemoryExpansionCost", 0, 1));
        RequireOrdered(memoryCost, "newActiveWords", "Size=newActiveWords<<5", "GasCostOf.Memory", ">>9");
        string tryLoad = Canonical(FindSelectedMember(memory,
            M("TryLoad", 0, 3, "inUInt256,inUInt256,outReadOnlyMemory<byte>")));
        RequireOrdered(tryLoad, "length.IsZero", "data=default", "CheckMemoryAccessViolation",
            "isViolation", "UpdateSize(newLength)", "GetBackingMemory");
        RequireOrdered(Canonical(FindSelectedMember(memory, M("UpdateSize", 0, 2, "ulong,bool"))),
            "length>Size", "Size=(length+(WordSize-1UL))&~(WordSize-1UL)",
            "rentIfNeeded", "EnsureRented(length)");
        RequireOrdered(Canonical(FindSelectedMember(memory, M("EnsureRented", 0, 1, "ulong"))),
            "requiredEnd>GetBackingCapacity()", "requiredEnd>_initializedSize", "RentSlow(requiredEnd)");
        RequireOrdered(Canonical(FindSelectedMember(memory, M("GetBackingMemory", 0, 2, "int,int"))),
            "memory=_memory", "memoryisnull", "_inlineMemoryManager!.Memory.Slice(offset,length)",
            "memory.AsMemory(offset,length)");
        Require(Canonical(FindSelectedMember(memory, M("TruncateToInt32", 0, 1, "ulong"))),
            "=>(int)(uint)value", "validated memory index truncation");
        TypeDeclarationSyntax ethereumGasPolicy = FindOwner(sources[GasPolicyPath].Root, "EthereumGasPolicy", 0);
        string memoryUpdate = Canonical(FindSelectedMember(ethereumGasPolicy,
            M("UpdateMemoryCost", 0, 4, "refEthereumGasPolicy,inUInt256,inUInt256,refEvmPooledMemory")));
        RequireOrdered(memoryUpdate, "size=memory.Size", "length.IsUint64", "length.u0<=size",
            "position.IsUint64", "position.u0<=size-length.u0", "CalculateMemoryCost",
            "memoryCost==0L", "!outOfGas", "UpdateGas");
        string gasUpdate = Canonical(FindMethod(ethereumGasPolicy, "UpdateGas", 0, 2));
        RequireOrdered(gasUpdate, "GetRemainingGas", "gas.Value=0", "ConsumeRaw");
        TypeDeclarationSyntax virtualMachine = FindOwner(sources[VirtualMachinePath].Root, "VirtualMachine", 1);
        string runByteCode = Canonical(FindMethod(virtualMachine, "RunByteCode", 2, 2));
        RequireOrdered(runByteCode, "ReturnData=null", "RunDispatchLoop", "exceptionTypeisEvmExceptionType.NoneorEvmExceptionType.StoporEvmExceptionType.RevertorEvmExceptionType.Suspend",
            "exceptionType!=EvmExceptionType.None", "EndInstructionTrace", "state.ProgramCounter", "state.DataStackHead",
            "exceptionType==EvmExceptionType.Revert", "ReturnDataisnotnull", "exceptionType==EvmExceptionType.OutOfGas",
            "ClearExecutionGas", "GetFailureReturn");
        RequireOrdered(Canonical(FindMethod(virtualMachine, "GetFailureReturn", 0, 2)),
            "EndInstructionTraceError", "EvmExceptionType.OutOfGas", "EvmExceptionType.BadInstruction",
            "EvmExceptionType.StackOverflow", "EvmExceptionType.StackUnderflow", "EvmExceptionType.InvalidJumpDestination");
        Require(Canonical(FindMethod(ethereumGasPolicy, "ClearExecutionGas", 0, 1)), "gas.Value=0", "execution gas exhaustion");
        RequireOrdered(Canonical(FindMethod(virtualMachine, "StartInstructionTrace", 0, 4)),
            "StartOperation", "IsTracingMemory", "SetOperationMemory", "SetOperationMemorySize",
            "IsTracingStack", "SetOperationStack", "IsTracingReturnData", "SetOperationReturnData");
        RequireOrdered(Canonical(FindMethod(virtualMachine, "EndInstructionTraceError", 0, 2)),
            "ReportOperationRemainingGas", "ReportOperationError");
        TypeDeclarationSyntax stack = FindOwner(sources[StackPath].Root, "EvmStack", 0);
        RequireOrdered(Canonical(FindMethod(stack, "PushUInt32", 2, 1)),
            "TCheckDepth.IsActive", "Head=newOffset", "ReverseEndianness", "TraceBytes", "sizeof(uint)");
        RequireOrdered(Canonical(FindMethod(stack, "PushUInt64", 2, 1)),
            "TCheckDepth.IsActive", "Head=newOffset", "Bytes.Bswap64", "TraceBytes", "sizeof(ulong)");
        Require(Canonical(FindMethod(stack, "CreateAcceleratedWordFromUInt64", 0, 1)),
            "Vector256.Create(0UL,0UL,0UL,value).AsByte()", "accelerated small-word stack layout");
        RequireOrdered(Canonical(FindMethod(stack, "WriteScalarWordFromUInt64", 0, 2)),
            "Unsafe.As<EvmWord,ulong>", "parts=0", "Unsafe.Add(refparts,1)=0",
            "Unsafe.Add(refparts,2)=0", "Unsafe.Add(refparts,3)=value");
        RequireOrdered(Canonical(FindSelectedMember(stack,
            M("ReadUInt256FromSlot", 0, 2, "refbyte,outUInt256"))),
            "!Vector256.IsHardwareAccelerated", "!AdvSimd.Arm64.IsSupported", "ReadBeWord",
            "Unsafe.ReadUnaligned<EvmWord>", "ByteSwap");
        RequireOrdered(Canonical(FindSelectedMember(stack,
            M("ReadMemoryPositionFromSlot", 0, 2, "refbyte,outUInt256"))),
            "Unsafe.As<byte,ulong>", "unreachable=limbs", "Bytes.Bswap64", "newUInt256(addressable,0,0,unreachable)");
        RequireOrdered(Canonical(FindMethod(FindOwner(sources[StackStandardPath].Root, "EvmStack", 0), "ReadBeWord", 0, 1)),
            "Bytes.Bswap64", "newUInt256(u0,u1,u2,u3)");
        RequireOrdered(Canonical(FindMethod(FindOwner(sources[TracerExtensionsPath].Root, "TracerExtensions", 0), "TraceBytes", 0, 3)),
            "ReportStackPush", "CreateReadOnlySpan", "length");
        RequireOrdered(Canonical(FindOwner(sources[TraceStackPath].Root, "TraceStack", 0)),
            "_stack=stack", "_stack.Slice(EvmStack.WordSize*index", "ToHexWordList", "this[i]",
            "ToRawBytes", "_stack.Span.CopyTo(raw)");
        RequireOrdered(Canonical(FindMethod(FindOwner(sources[VmStatePath].Root, "VmState", 1), "MemoryStacks", 0, 1)),
            "DataStack", "AllocateStacks", "AsAligned32Memory", "count*EvmStack.WordSize");

        TypeDeclarationSyntax analyzerOwner = FindOwner(sources[AnalyzerPath].Root, "JumpDestinationAnalyzer", 0);
        string analyzer = Canonical(sources[AnalyzerPath].Root);
        Require(analyzer, "PUSH1=(int)Instruction.PUSH1", "jump scanner PUSH1 boundary");
        Require(analyzer, "PUSHx=PUSH1-1", "jump scanner signed PUSH sentinel");
        Require(analyzer, "JUMPDEST=(int)Instruction.JUMPDEST", "jump scanner JUMPDEST marker");
        Require(analyzer, "PUSH32=(int)Instruction.PUSH32", "jump scanner PUSH32 boundary");
        Require(analyzer, "code[0]==(byte)Instruction.STOP", "leading STOP empty-bitmap optimization");
        Require(analyzer, "IsJumpDestination(_jumpDestinationBitmap,destination)", "jump bitmap lookup");
        RequireOrdered(Canonical(FindMethod(analyzerOwner, "CreateJumpDestinationBitmap", 0, 0)),
            "code[0]==(byte)Instruction.STOP", "CreateBitmap(code.Length)",
            "PopulateJumpDestinationBitmap_Vector512", "PopulateJumpDestinationBitmap_Vector128",
            "PopulateJumpDestinationBitmap_Scalar");
        string byteScanner = Canonical(FindSelectedMember(
            FindOwner(sources[AnalyzerStandardPath].Root, "JumpDestinationAnalyzer", 0),
            M("ProcessJumpDestinationBitmap_Byte", 0, 3, "nuint,Span<long>,ReadOnlySpan<byte>")));
        RequireOrdered(byteScanner, "while(programCounter<length)", "op=Unsafe.AddByteOffset",
            "op==JUMPDEST", "programCounter++", "op>=PUSH1",
            "programCounter+=(nuint)op-PUSH1+2", "programCounter++");
        Require(Canonical(sources[ReleaseSpecExtensionsPath].Root),
            "extension(IReleaseSpecspec)", "release-spec extension receiver");
        Require(Canonical(sources[ReleaseSpecExtensionsPath].Root), "RevertOpcodeEnabled=>spec.IsEip140Enabled", "REVERT gate mapping");
        Require(Canonical(sources[$"{ForkDirectory}/06_Byzantium.cs"].Root), "spec.IsEip140Enabled=true", "Byzantium REVERT activation");
        Require(Canonical(sources[$"{ForkDirectory}/25_Amsterdam.cs"].Root), "spec.IsEip7843Enabled=true", "Amsterdam SLOTNUM activation");
        Require(Canonical(sources[BlockProcessingModulePath].Root), ".AddScoped<IVirtualMachine,EthereumVirtualMachine>()", "standard VM DI root");
        Require(Canonical(sources[VirtualMachineStandardPath].Root), "_opcodeTablesBySpec.GetValue", "fork-keyed opcode table");
        ValidateForkLineage(sources);
    }

    private static void ValidateForkLineage(IReadOnlyDictionary<string, SourceFile> sources)
    {
        for (int index = 0; index < ForkLineage.Length; index++)
        {
            string fork = ForkLineage[index];
            string parent = index == 0 ? "null" : $"{ForkLineage[index - 1]}.Instance";
            string actual = Canonical(sources[$"{ForkDirectory}/{ForkFileName(index)}.cs"].Root);
            Require(actual, $"class{fork}():NamedReleaseSpec<{fork}>({parent})", $"{fork} fork parent");
        }
        string named = Canonical(sources[NamedReleaseSpecPath].Root);
        RequireOrdered(named, "Parent=parent", "ReplayAncestors(this)", "ReplayAncestors(fork.Parent)", "fork.Apply(this)");
    }

    private static IrDocument ExpectedIr()
    {
        DispatchTableBinding[] tables =
        [
            new("NoTrace", "OffFlag", "OffFlag"),
            new("NoTraceCancelable", "OffFlag", "OnFlag"),
            new("Traced", "OnFlag", "OffFlag"),
            new("TracedCancelable", "OnFlag", "OnFlag"),
        ];
        OpcodeDescriptor[] opcodes =
        [
            D("stop", "STOP", 0x00, "StopOpcode", "terminating", "EvmInstructions.InstructionStop<TGasPolicy>", 0, 0, "false", false, "always", "zero", 0, 0, ["traceStart", "incrementPc", "incrementOpcodeCount", "returnStop", "outerTraceFinish"]),
            D("slotnum", "SLOTNUM", 0x4b, "SlotNumOpcode<TTracingInst>", "continuable", "EvmInstructions.InstructionSlotNum<TGasPolicy,TTracingInst>", 0, 1, "false", false, "EIP-7843", "base", 2, 8, ["traceStart", "incrementPc", "incrementOpcodeCount", "readNullableSlot", "missingBadInstruction", "chargeBase", "checkPushOverflow", "pushUInt64", "tracePush8", "checkBodyTraceOwnership", "traceFinish"]),
            D("jump", "JUMP", 0x56, "JumpOpcode<TTracingInst>", "continuable", "EvmInstructions.InstructionJump<TGasPolicy,TSkipJumpDest>", 1, 0, "false", false, "always", "mid", 8, 0, ["traceStart", "incrementPc", "incrementOpcodeCount", "chargeMid", "checkDepth", "popDestination", "validateInstructionBoundary", "untracedCountAndAdvanceJumpDest", "untracedChargeJumpDest", "checkBodyTraceOwnership", "traceFinish"]),
            D("jumpi", "JUMPI", 0x57, "ExecuteJumpIfOpcode<TTracingInst,TCancelable>", "jumpIf", "EvmInstructions.InstructionJumpIf<TGasPolicy,TSkipJumpDest>", 2, 0, "false", false, "always", "high", 10, 0, ["traceStart", "incrementPc", "incrementOpcodeCount", "chargeHigh", "checkDepth", "popDestinationAndCondition", "zeroConditionFallthrough", "validateTakenDestination", "untracedCountAndAdvanceJumpDest", "untracedChargeJumpDest", "dedicatedTraceFinish"]),
            D("pc", "PC", 0x58, "ProgramCounterOpcode<TTracingInst>", "continuable", "EvmInstructions.InstructionProgramCounter<TGasPolicy,TTracingInst>", 0, 1, "!TTracingInst.IsActive", false, "always", "base", 2, 4, ["traceStart", "incrementPc", "incrementOpcodeCount", "chargeBase", "checkPushOverflow", "pushPreincrementPc", "tracePush4", "checkBodyTraceOwnership", "traceFinish"]),
            D("jumpdest", "JUMPDEST", 0x5b, "JumpDestOpcode", "continuable", "EvmInstructions.InstructionJumpDest<TGasPolicy>", 0, 0, "false", false, "always", "jumpDest", 1, 0, ["traceStart", "incrementPc", "incrementOpcodeCount", "chargeJumpDest", "checkBodyTraceOwnership", "traceFinish"]),
            D("return_", "RETURN", 0xf3, "ReturnOpcode", "terminating", "EvmInstructions.InstructionReturn<TGasPolicy>", 2, 0, "false", false, "always", "memory", 0, 0, ["traceStart", "incrementPc", "incrementOpcodeCount", "popOffsetAndLengthAtomically", "validateRange", "computeExpansionCost", "chargeExpansion", "installLogicalMemorySize", "loadBytes", "stageOwnedOutput", "returnStop", "outerTraceFinish"]),
            D("revert", "REVERT", 0xfd, "RevertOpcode", "terminating", "EvmInstructions.InstructionRevert<TGasPolicy>", 2, 0, "false", false, "Byzantium REVERT", "memory", 0, 0, ["traceStart", "incrementPc", "incrementOpcodeCount", "popOffsetAndLengthAtomically", "validateRange", "computeExpansionCost", "chargeExpansion", "installLogicalMemorySize", "loadBytes", "stageOwnedOutput", "returnRevert", "outerTraceFinish"]),
        ];
        List<OpcodeSpecialization> roots = new(32);
        foreach (OpcodeDescriptor opcode in opcodes)
        foreach (DispatchTableBinding table in tables)
        {
            string continuation = opcode.HandlerKind == "terminating" ? "OffFlag" : "OnFlag";
            string enabled = opcode.Instruction == "JUMPI"
                ? $"Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteJumpIfOpcode<{table.TracingFlag},{table.CancelableFlag}>"
                : $"Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<{opcode.HandlerBody.Replace("TTracingInst", table.TracingFlag, StringComparison.Ordinal)},{table.TracingFlag},{table.CancelableFlag},{continuation}>";
            string disabled = opcode.Activation == "always" ? string.Empty :
                $"Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<BadInstructionOpcode,{table.TracingFlag},{table.CancelableFlag},OffFlag>";
            roots.Add(new(opcode.Name, table.Name, table.TracingFlag, table.CancelableFlag, continuation, enabled, disabled));
        }
        return new(
            1, ExtractorVersion, KernelName,
            "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>",
            "Nethermind.Evm.GasPolicy.EthereumGasPolicy", "Nethermind.Specs.Forks.Amsterdam",
            "Directory.Build.props aliases EvmWord to Vector256<byte>; Directory.Build.targets EnableZkEvm != true selects *.std.cs and excludes *.zkevm.cs",
            new(0, 2, 8, 10, 1, 3, 512, 2147483616, 1024, 1023),
            new(0x00, 0x5b, 0x60, 0x7f, int.MaxValue),
            tables, opcodes, [.. roots], ForkLineage, SemanticBindings(),
            [
                "Roslyn source/member admission does not prove C# compilation, CLR/JIT/AOT execution, function-pointer tail calls, unsafe memory safety, or hardware behavior.",
                "Trace proofs use an explicit semantic projection; concrete ExecutionEnvironment, TraceMemory, TraceStack, and tracer implementation representations remain open.",
                "The admitted UInt256 and unsafe EvmStack representation is assumed to match the Lean top-first bounded stack without corruption.",
                "The generated jump scanner is independent; admitted scalar/SIMD bitmap implementations are assumed extensionally equal to its PUSH-aware instruction-boundary predicate on production-reachable code of signed-int length.",
                "EvmPooledMemory allocation, pooling, zero initialization, bounds, and ReadOnlyMemory ownership are assumed to implement the modeled byte memory.",
                "Handler-entry states are restricted to the production RunByteCode invariant that staged ReturnData is null; terminal RETURN/REVERT are the only covered handlers that install it.",
                "Fault PC and stack residues describe the dispatcher-local programCounter and EvmStack head; RunByteCode does not commit those locals to VmState on exceptional failure.",
                "Cancellation token state, polling, and OperationCanceledException propagation remain outside one-handler semantics; all four compiled roots are admitted.",
                "Frame rollback, caller returndata installation, transaction/block settlement, persistence, alternate gas policies, and zkEVM execution remain outside this package.",
            ]);
    }

    private static OpcodeDescriptor D(string name, string instruction, int opcodeByte, string body,
        string kind, string target, int inputs, int outputs, string checkedBody, bool endsInstructionTrace, string activation,
        string gasClass, int fixedGas, int tracePushWidth, string[] order) =>
        new(name, instruction, opcodeByte, body, kind, target, inputs, outputs, checkedBody,
            endsInstructionTrace, activation, gasClass, fixedGas, tracePushWidth, order);

    private static string[] SemanticBindings() =>
    [
        "8 opcodes times 4 trace/cancellation tables yield exactly 32 enabled Amsterdam roots",
        "SLOTNUM and REVERT disabled gates share exactly 4 BadInstruction roots",
        "instruction trace starts before PC/opcode-count increment and closes at the admitted handler/RunByteCode boundary",
        "all 8 descriptors have false instruction-trace ownership; generic bodies inherit IOpcodeBody.EndsInstructionTrace false while JUMPI uses its dedicated close",
        "trace projection preserves distinct callback order, bottom-first logical stack snapshots, and 4-byte PC / 8-byte SLOTNUM push payloads without claiming concrete tracer adapter representation",
        "jump validity is independently scanned from source-admitted STOP, JUMPDEST, PUSH1, and PUSH32 facts and rejects PUSH immediate bytes and destinations above Int32.MaxValue",
        "untraced taken JUMP/JUMPI fuse the landed JUMPDEST count, PC advance, and one-gas charge",
        "RETURN/REVERT pop atomically, charge expansion before TryLoad installs logical memory size, and stage an owned output on success",
        "failed execution-gas charges leave gas zero while non-OOG failures preserve the preceding handler-local debit and mutation residue",
    ];

    private static AdmissionSpec[] BuildAdmissionSpecs()
    {
        List<AdmissionSpec> specs =
        [
            S(GasCostPath, "Nethermind.Core", "GasCostOf", 0,
                F("Base"), F("Mid"), F("High"), F("Jump"), F("JumpI"), F("JumpDest"), F("Memory")),
            S(TypeFlagsPath, "Nethermind.Core", "compilation-unit", 0, T("IFlag"), T("OffFlag"), T("OnFlag")),
            S(ReleaseSpecPath, "Nethermind.Core.Specs", "compilation-unit", 0, T("IReleaseSpec")),
            S(ReleaseSpecPath, "Nethermind.Core.Specs", "IReleaseSpec", 0,
                P("IsEip140Enabled"), P("IsEip7843Enabled")),
            S(ReleaseSpecExtensionsPath, "Nethermind.Core.Specs", "IReleaseSpecExtensions", 0, P("RevertOpcodeEnabled")),
            S(BytesPath, "Nethermind.Core.Extensions", "compilation-unit", 0, T("Bytes")),
            S(BytesStandardPath, "Nethermind.Core.Extensions", "compilation-unit", 0, T("Bytes")),
            S(BytesStandardPath, "Nethermind.Core.Extensions", "Bytes", 0, M("Bswap64", 0, 1, "ulong")),
            S(EvmWordExtensionsPath, "Nethermind.Core.Extensions", "compilation-unit", 0, T("EvmWordExtensions")),
            S(EvmWordExtensionsPath, "Nethermind.Core.Extensions", "EvmWordExtensions", 0,
                M("ByteSwap", 0, 0), P("ReverseBytes128Mask"), P("ByteSwap256Mask")),
            S(InstructionPath, "Nethermind.Evm", "compilation-unit", 0, T("Instruction")),
            S(DispatchFlagsPath, "Nethermind.Evm", "compilation-unit", 0, T("DispatchFlags")),
            S(DispatchFlagsPath, "Nethermind.Evm", "DispatchFlags", 0,
                F("ConstTracing"), M("Tracing", 0, 1, "bool"), M("Cancelable", 0, 1, "bool")),
            S(BlockHeaderPath, "Nethermind.Core", "BlockHeader", 0, P("SlotNumber")),
            S(BlockContextPath, "Nethermind.Evm", "BlockExecutionContext", 0, F("Header"), F("Spec")),
            S(StackPath, "Nethermind.Evm", "EvmStack", 0, F("MaxStackSize"), F("WordSize"), F("Head"), F("CodeLength"), F("Code"), P("JumpDestinations"),
                M("EnsureDepth", 0, 1, "int"), M("PopBytesByRefUnchecked", 0, 0), M("Pop2BytesByRefUnchecked", 0, 0),
                M("PopMemoryPositionAndUInt256", 0, 2, "outUInt256,outUInt256"), M("PushUInt32", 2, 1, "uint"),
                M("PushUInt64", 2, 1, "ulong"), M("IsSlotZero", 0, 1, "refbyte"),
                M("CreateAcceleratedWordFromUInt64", 0, 1, "ulong"),
                M("WriteScalarWordFromUInt64", 0, 2, "refEvmWord,ulong"),
                M("ReadUInt256FromSlot", 0, 2, "refbyte,outUInt256"),
                M("ReadMemoryPositionFromSlot", 0, 2, "refbyte,outUInt256")),
            S(StackStandardPath, "Nethermind.Evm", "compilation-unit", 0, T("EvmStack")),
            S(StackStandardPath, "Nethermind.Evm", "EvmStack", 0, M("ReadBeWord", 0, 1, "refbyte")),
            S(MemoryPath, "Nethermind.Evm", "EvmPooledMemory", 0, F("WordSize"), F("MaxMemorySize"), P("Size"),
                M("CalculateMemoryCost", 0, 3, "inUInt256,inUInt256,outbool"), M("TryLoad", 0, 3, "inUInt256,inUInt256,outReadOnlyMemory<byte>"),
                M("CheckMemoryAccessViolation", 0, 4, "inUInt256,inUInt256,outulong,outbool"),
                M("CheckMemoryAccessViolation", 0, 4, "inUInt256,ulong,outulong,outbool"),
                M("ComputeMemoryExpansionCost", 0, 1, "ulong"), M("UpdateSize", 0, 2, "ulong,bool"),
                M("EnsureRented", 0, 1, "ulong"), M("GetBackingMemory", 0, 2, "int,int"),
                M("TruncateToInt32", 0, 1, "ulong"), M("GetTrace", 0, 0)),
            S(GasTagsPath, "Nethermind.Evm.GasPolicy", "compilation-unit", 0, T("IGasCost"), T("BaseGasCost"), T("JumpGasCost"), T("JumpIGasCost"), T("JumpDestGasCost")),
            S(GasInterfacePath, "Nethermind.Evm.GasPolicy", "IGasPolicy", 1,
                M("ClearExecutionGas", 0, 1, "refTSelf"), M("GetRemainingGas", 0, 1, "inTSelf"),
                M("UpdateGas", 1, 1, "refTSelf"), M("UpdateMemoryCost", 0, 4, "refTSelf,inUInt256,inUInt256,refEvmPooledMemory")),
            S(GasPolicyPath, "Nethermind.Evm.GasPolicy", "EthereumGasPolicy", 0,
                M("ClearExecutionGas", 0, 1, "refEthereumGasPolicy"), M("GetRemainingGas", 0, 1, "inEthereumGasPolicy"),
                M("UpdateGas", 0, 2, "refEthereumGasPolicy,ulong"), M("UpdateMemoryCost", 0, 4, "refEthereumGasPolicy,inUInt256,inUInt256,refEvmPooledMemory")),
            S(ControlFlowPath, "Nethermind.Evm", "EvmInstructions", 0,
                M("InstructionProgramCounter", 2, 4, "refEvmStack,refTGasPolicy,VirtualMachine<TGasPolicy>,nint"),
                M("InstructionJumpDest", 1, 3, "refEvmStack,refTGasPolicy,VirtualMachine<TGasPolicy>"),
                M("InstructionJump", 1, 4, "refEvmStack,refTGasPolicy,VirtualMachine<TGasPolicy>,nint"),
                M("InstructionJumpAndSkipJumpDest", 1, 4, "refEvmStack,refTGasPolicy,VirtualMachine<TGasPolicy>,nint"),
                M("InstructionJump", 2, 4, "refEvmStack,refTGasPolicy,VirtualMachine<TGasPolicy>,nint"),
                M("InstructionJumpIf", 1, 4, "refEvmStack,refTGasPolicy,VirtualMachine<TGasPolicy>,nint"),
                M("InstructionJumpIfAndSkipJumpDest", 1, 4, "refEvmStack,refTGasPolicy,VirtualMachine<TGasPolicy>,nint"),
                M("InstructionJumpIf", 2, 4, "refEvmStack,refTGasPolicy,VirtualMachine<TGasPolicy>,nint"),
                M("SkipJumpDest", 2, 4, "VirtualMachine<TGasPolicy>,refTGasPolicy,nint,outnint"),
                M("InstructionStop", 1, 3, "refEvmStack,refTGasPolicy,VirtualMachine<TGasPolicy>"),
                M("InstructionRevert", 1, 3, "refEvmStack,refTGasPolicy,VirtualMachine<TGasPolicy>"),
                M("InstructionBadInstruction", 1, 3, "refEvmStack,refTGasPolicy,VirtualMachine<TGasPolicy>"),
                M("JumpDestination", 0, 2, "refbyte,refEvmStack"), M("JumpDestination", 0, 2, "int,refEvmStack"),
                M("PrefetchCodeAtDestination", 0, 2, "refEvmStack,nint")),
            S(CallPath, "Nethermind.Evm", "EvmInstructions", 0,
                M("InstructionReturn", 1, 3, "refEvmStack,refTGasPolicy,VirtualMachine<TGasPolicy>")),
            S(EnvironmentPath, "Nethermind.Evm", "EvmInstructions", 0,
                M("InstructionSlotNum", 2, 3, "refEvmStack,refTGasPolicy,VirtualMachine<TGasPolicy>")),
            S(HandlersPath, "Nethermind.Evm", "VirtualMachine", 1,
                T("IOpcodeBody"), M("OpcodeHandler", 3, 0), M("TerminatingOpcodeHandler", 3, 0),
                M("JumpIfOpcodeHandler", 2, 0), M("GenerateOpcodeHandlers", 2, 1, "IReleaseSpec"),
                T("BadInstructionOpcode"), T("StopOpcode"), T("SlotNumOpcode", 1), T("JumpOpcode", 1),
                T("ProgramCounterOpcode", 1), T("JumpDestOpcode"), T("ReturnOpcode"), T("RevertOpcode")),
            S(HandlersPath, "Nethermind.Evm", "IOpcodeBody", 0, P("EndsInstructionTrace")),
            S(HandlersPath, "Nethermind.Evm", "BadInstructionOpcode", 0, M("Execute", 0, 4, "refEvmStack,refTGasPolicy,VirtualMachine<TGasPolicy>,refnint")),
            S(HandlersPath, "Nethermind.Evm", "StopOpcode", 0, M("Execute", 0, 4, "refEvmStack,refTGasPolicy,VirtualMachine<TGasPolicy>,refnint")),
            S(HandlersPath, "Nethermind.Evm", "SlotNumOpcode", 1, M("Execute", 0, 4, "refEvmStack,refTGasPolicy,VirtualMachine<TGasPolicy>,refnint")),
            S(HandlersPath, "Nethermind.Evm", "JumpOpcode", 1, M("Execute", 0, 4, "refEvmStack,refTGasPolicy,VirtualMachine<TGasPolicy>,refnint")),
            S(HandlersPath, "Nethermind.Evm", "ProgramCounterOpcode", 1, P("HasCheckedBody"), M("TryConsumeGas", 0, 1, "refTGasPolicy"), P("StackGrowth"), M("Execute", 0, 4, "refEvmStack,refTGasPolicy,VirtualMachine<TGasPolicy>,refnint")),
            S(HandlersPath, "Nethermind.Evm", "JumpDestOpcode", 0, M("Execute", 0, 4, "refEvmStack,refTGasPolicy,VirtualMachine<TGasPolicy>,refnint")),
            S(HandlersPath, "Nethermind.Evm", "ReturnOpcode", 0, M("Execute", 0, 4, "refEvmStack,refTGasPolicy,VirtualMachine<TGasPolicy>,refnint")),
            S(HandlersPath, "Nethermind.Evm", "RevertOpcode", 0, M("Execute", 0, 4, "refEvmStack,refTGasPolicy,VirtualMachine<TGasPolicy>,refnint")),
            S(DispatchPath, "Nethermind.Evm", "VirtualMachine", 1, T("DispatchState"), T("OpcodeTable"),
                M("GetOpcodeHandlers", 2, 0), M("PrepareOpcodes", 1, 0), M("PrepareOpcodes", 2, 0),
                M("RunDispatchLoop", 2, 3, "scopedrefEvmStack,scopedrefTGasPolicy,refnint"),
                M("ExecuteOpcode", 4, 5, "refEvmStack,refTGasPolicy,refDispatchState,nint,int"),
                M("ExitCheckedOpcode", 0, 4, "refDispatchState,nint,int,EvmExceptionType"),
                M("ExecuteJumpIfOpcode", 2, 5, "refEvmStack,refTGasPolicy,refDispatchState,nint,int")),
            S(DispatchPath, "Nethermind.Evm", "OpcodeTable", 0,
                F("NoTrace"), F("NoTraceCancelable"), F("Traced"), F("TracedCancelable"),
                M("GetHandlers", 2, 1, "IReleaseSpec"), M("RefreshNonTraced", 0, 1, "IReleaseSpec")),
            S(VirtualMachinePath, "Nethermind.Evm", "compilation-unit", 0, T("EthereumVirtualMachine")),
            S(VirtualMachinePath, "Nethermind.Evm", "VirtualMachine", 1, F("_isCancelableCached"), P("ReturnDataBuffer"), P("BlockExecutionContext"), P("Spec"), P("OpCodeCount"), P("VmState"),
                M("RunByteCode", 2, 2, "scopedrefEvmStack,scopedrefTGasPolicy"), M("GetFailureReturn", 0, 2, "ulong,EvmExceptionType"),
                M("StartInstructionTrace", 0, 4, "Instruction,ulong,int,inEvmStack"), M("EndInstructionTrace", 0, 1, "ulong"),
                M("EndInstructionTraceError", 0, 2, "ulong,EvmExceptionType")),
            S(VirtualMachineStandardPath, "Nethermind.Evm", "VirtualMachine", 1, F("_opcodeTablesBySpec"), P("ReturnData"), M("GetOpcodeTable", 0, 0), M("ShouldRefreshOpcodes", 0, 0)),
            S(VmStatePath, "Nethermind.Evm", "VmState", 1, F("Gas"), F("DataStackHead"), P("ProgramCounter"),
                P("Env"), P("Memory"), M("MemoryStacks", 0, 1, "int")),
            S(CodeInfoPath, "Nethermind.Evm.CodeAnalysis", "CodeInfo", 0, P("JumpDestinationBitmap"), M("ValidateJump", 0, 1, "int")),
            S(AnalyzerPath, "Nethermind.Evm.CodeAnalysis", "JumpDestinationAnalyzer", 0,
                F("PUSH1"), F("PUSHx"), F("JUMPDEST"), F("PUSH32"), P("JumpDestinationBitmap"),
                M("CreateJumpDestinationBitmap", 0, 0), M("IsJumpDestination", 0, 2, "long[],int")),
            S(AnalyzerStandardPath, "Nethermind.Evm.CodeAnalysis", "JumpDestinationAnalyzer", 0,
                M("PopulateJumpDestinationBitmap_Scalar", 0, 2, "long[],ReadOnlySpan<byte>"),
                M("ProcessJumpDestinationBitmap_Byte", 0, 3, "nuint,Span<long>,ReadOnlySpan<byte>")),
            S(TraceInterfacePath, "Nethermind.Evm.Tracing", "compilation-unit", 0, T("ITxTracer")),
            S(TraceInterfacePath, "Nethermind.Evm.Tracing", "ITxTracer", 0,
                P("IsTracingInstructions"), P("IsTracingMemory"), P("IsTracingStack"), P("IsTracingReturnData"),
                M("StartOperation", 0, 4, "int,Instruction,ulong,inExecutionEnvironment"),
                M("ReportOperationError", 0, 1, "EvmExceptionType"),
                M("ReportOperationRemainingGas", 0, 1, "ulong"),
                M("SetOperationStack", 0, 1, "TraceStack"),
                M("ReportStackPush", 0, 1, "inReadOnlySpan<byte>"),
                M("SetOperationMemory", 0, 1, "TraceMemory"),
                M("SetOperationMemorySize", 0, 1, "ulong"),
                M("SetOperationReturnData", 0, 1, "ReadOnlyMemory<byte>")),
            S(TracerExtensionsPath, "Nethermind.Evm.Tracing", "TracerExtensions", 0,
                M("TraceBytes", 0, 3, "thisITxTracer,inbyte,int")),
            S(TraceStackPath, "Nethermind.Evm.Tracing", "compilation-unit", 0, T("TraceStack")),
            S(TraceMemoryPath, "Nethermind.Evm.Tracing", "compilation-unit", 0, T("TraceMemory")),
            S(BlockProcessingModulePath, "Nethermind.Init.Modules", "BlockProcessingModule", 0, M("Load", 0, 1, "ContainerBuilder")),
            S(NamedReleaseSpecPath, "Nethermind.Specs.Forks", "compilation-unit", 0, T("NamedReleaseSpec"), T("NamedReleaseSpec", 1)),
        ];
        for (int index = 0; index < ForkLineage.Length; index++)
            specs.Add(S($"{ForkDirectory}/{ForkFileName(index)}.cs", "Nethermind.Specs.Forks", "compilation-unit", 0, T(ForkLineage[index])));
        return [.. specs];
    }

    private static AdmissionSpec S(string path, string ns, string owner, int ownerArity,
        params MemberSelector[] members) => new(path, ns, owner, ownerArity, members);
    private static MemberSelector M(string name, int arity, int parameterCount, string parameters = "") =>
        new("method", name, arity, parameterCount, parameters);
    private static MemberSelector T(string name, int arity = 0) => new("type", name, arity);
    private static MemberSelector F(string name) => new("field", name);
    private static MemberSelector P(string name) => new("property", name);

    private static TypeDeclarationSyntax FindOwner(CompilationUnitSyntax syntax, string name, int genericArity)
    {
        TypeDeclarationSyntax[] matches = syntax.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(value => value.Identifier.ValueText == name && GenericArity(value) == genericArity).ToArray();
        if (matches.Length != 1)
            throw new ExtractionException($"Expected exactly one owner {name}/{genericArity}; found {matches.Length}.");
        return matches[0];
    }

    private static MemberDeclarationSyntax FindSelectedMember(SyntaxNode owner, MemberSelector selector)
    {
        MemberDeclarationSyntax[] matches = DirectMembers(owner).Where(value => Matches(value, selector)).ToArray();
        if (matches.Length != 1)
        {
            string candidates = string.Join("; ", DirectMembers(owner).OfType<MethodDeclarationSyntax>()
                .Where(value => value.Identifier.ValueText == selector.Name)
                .Select(value => $"{value.Identifier.ValueText}/{value.TypeParameterList?.Parameters.Count ?? 0}/{value.ParameterList.Parameters.Count}/{ParameterTypes(value)}"));
            throw new ExtractionException($"Expected exactly one {SelectorKey(selector)} in {owner.Kind()}; found {matches.Length}. Candidates: {candidates}");
        }
        return matches[0];
    }

    private static MethodDeclarationSyntax FindMethod(TypeDeclarationSyntax owner, string name, int arity, int parameterCount) =>
        (MethodDeclarationSyntax)FindSelectedMember(owner, M(name, arity, parameterCount));

    private static IEnumerable<MemberDeclarationSyntax> DirectMembers(SyntaxNode owner) => owner switch
    {
        CompilationUnitSyntax unit => unit.Members.SelectMany(static member => member is BaseNamespaceDeclarationSyntax ns ? ns.Members : [member]),
        TypeDeclarationSyntax type => DirectTypeMembers(type),
        _ => throw new ExtractionException($"Unsupported admission owner {owner.Kind()}.")
    };

    private static IEnumerable<MemberDeclarationSyntax> DirectTypeMembers(TypeDeclarationSyntax type)
    {
        foreach (MemberDeclarationSyntax member in type.Members)
        {
            if (member is ExtensionBlockDeclarationSyntax extension)
                foreach (MemberDeclarationSyntax extensionMember in extension.Members) yield return extensionMember;
            else
                yield return member;
        }
    }

    private static bool Matches(MemberDeclarationSyntax member, MemberSelector selector) => selector.Kind switch
    {
        "type" when member is BaseTypeDeclarationSyntax type =>
            type.Identifier.ValueText == selector.Name && GenericArity(type) == selector.GenericArity,
        "method" when member is MethodDeclarationSyntax method =>
            method.Identifier.ValueText == selector.Name &&
            (method.TypeParameterList?.Parameters.Count ?? 0) == selector.GenericArity &&
            method.ParameterList.Parameters.Count == selector.ParameterCount &&
            (selector.ParameterTypes.Length == 0 || ParameterTypes(method) == selector.ParameterTypes),
        "field" when member is FieldDeclarationSyntax field =>
            field.Declaration.Variables.Any(value => value.Identifier.ValueText == selector.Name),
        "property" when member is PropertyDeclarationSyntax property => property.Identifier.ValueText == selector.Name,
        _ => false,
    };

    private static int GenericArity(BaseTypeDeclarationSyntax type) => type is TypeDeclarationSyntax declaration
        ? declaration.TypeParameterList?.Parameters.Count ?? 0 : 0;

    private static string ParameterTypes(MethodDeclarationSyntax method) => string.Join(",",
        method.ParameterList.Parameters.Select(static parameter =>
            string.Concat(parameter.Modifiers.Select(static modifier => modifier.Text)) + Canonical(parameter.Type!)));

    private static string SelectorKey(MemberSelector selector) =>
        $"{selector.Kind}:{selector.Name}/{selector.GenericArity}/{selector.ParameterCount}/{selector.ParameterTypes}";
    private static string AdmissionKey(AdmissionSpec spec, MemberSelector selector) =>
        $"{spec.SourcePath}:{spec.Namespace}:{spec.OwnerName}/{spec.OwnerGenericArity}:{SelectorKey(selector)}";
    private static string AdmissionKey(AdmissionIdentity admission) =>
        $"{admission.SourcePath}:{admission.Namespace}:{admission.OwnerName}/{admission.OwnerGenericArity}:{admission.MemberKind}:{admission.MemberName}/{admission.MemberGenericArity}/{admission.ParameterCount}/{admission.ParameterTypes}";

    private static string Canonical(SyntaxNode node) => string.Concat(
        node.DescendantTokens(descendIntoTrivia: false).Select(static token => token.Text));

    private static byte[] CompleteCanonical(SyntaxNode node)
    {
        StringBuilder result = new();
        foreach (SyntaxToken token in node.DescendantTokens(descendIntoTrivia: false))
            result.Append('T').Append(token.RawKind).Append(':').Append(token.Text).Append('\0');
        return Encoding.UTF8.GetBytes(result.ToString());
    }

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

    private static void RejectDuplicateProperties(byte[] bytes, string description)
    {
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
                        if (containers.Count == 0 || containers.Peek() is not HashSet<string> properties || !properties.Add(reader.GetString()!))
                            throw new ExtractionException($"Serialized {description} contains a duplicate property '{reader.GetString()}'.");
                        break;
                }
            }
            if (containers.Count != 0) throw new ExtractionException($"Serialized {description} has malformed nesting.");
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Serialized {description} is not valid JSON: {exception.Message}");
        }
    }

    private static string CombinedSourceHash(IEnumerable<SourceIdentity> sources, IEnumerable<RawSourceIdentity> raw,
        IEnumerable<AdmissionIdentity> admissions) => Hash(Encoding.UTF8.GetBytes(string.Join("\n",
        sources.Select(static item => $"{item.Path}\0{item.Sha256}\0{item.RoslynSyntaxSha256}\0csharp14")
            .Concat(raw.OrderBy(static item => item.Path, StringComparer.Ordinal)
                .Select(static item => $"{item.Path}\0{item.Sha256}\0raw"))
            .Concat(admissions.Select(static item =>
                $"{AdmissionKey(item)}\0{item.SyntaxKind}\0{item.Sha256}\0admission")))));

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void ValidateSha256(string? value, string description) =>
        ControlFlowOpcodeLeanEmitter.ValidateSha256(value, description);

    private static void Require(string source, string fragment, string description)
    {
        if (!source.Contains(fragment, StringComparison.Ordinal))
            throw new ExtractionException($"Rejected {description}; expected '{fragment}'.");
    }

    private static void RequireExactlyOnce(string source, string fragment, string description)
    {
        int first = source.IndexOf(fragment, StringComparison.Ordinal);
        if (first < 0 || source.IndexOf(fragment, first + fragment.Length, StringComparison.Ordinal) >= 0)
            throw new ExtractionException($"Rejected {description}; expected exactly one '{fragment}'.");
    }

    private static void Reject(string source, string fragment, string description)
    {
        if (source.Contains(fragment, StringComparison.Ordinal))
            throw new ExtractionException($"Rejected {description}; found forbidden '{fragment}'.");
    }

    private static void RequireOrdered(string source, params string[] fragments)
    {
        int position = 0;
        foreach (string fragment in fragments)
        {
            int found = source.IndexOf(fragment, position, StringComparison.Ordinal);
            if (found < 0) throw new ExtractionException($"Rejected production order; missing ordered fragment '{fragment}'.");
            position = found + fragment.Length;
        }
    }

    private static void WriteDeterministic(string path, byte[] bytes)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return;
        File.WriteAllBytes(path, bytes);
    }

    private static string ForkFileName(int index) => index switch
    {
        0 => "00_Olympic", 1 => "01_Frontier", 2 => "02_Homestead", 3 => "03_Dao",
        4 => "04_TangerineWhistle", 5 => "05_SpuriousDragon", 6 => "06_Byzantium",
        7 => "07_Constantinople", 8 => "08_ConstantinopleFix", 9 => "09_Istanbul",
        10 => "10_MuirGlacier", 11 => "11_Berlin", 12 => "12_London", 13 => "13_ArrowGlacier",
        14 => "14_GrayGlacier", 15 => "15_Paris", 16 => "16_Shanghai", 17 => "17_Cancun",
        18 => "18_Prague", 19 => "19_Osaka", 20 => "20_BPO1", 21 => "21_BPO2",
        22 => "25_Amsterdam", _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };
}
