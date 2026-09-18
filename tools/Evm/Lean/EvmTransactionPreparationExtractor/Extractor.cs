// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Nethermind.Evm.Lean.EvmTransactionPreparationExtractor;

/// <summary>Admits the exact production preparation boundary and emits its typed artifacts.</summary>
internal static class Extractor
{
    internal const string TransactionProcessorPath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs";
    internal const string VirtualMachinePath = "src/Nethermind/Nethermind.Evm/VirtualMachine.cs";
    internal const string VirtualMachineStaticsAdapterPath = "tools/Evm/Lean/EvmTransactionPreparationExtractor/Admission/VirtualMachineStaticsAdapter.cs";
    internal const string VirtualMachineInterfacePath = "src/Nethermind/Nethermind.Evm/IVirtualMachine.cs";
    internal const string VmStatePath = "src/Nethermind/Nethermind.Evm/VmState.cs";
    internal const string StackAccessTrackerPath = "src/Nethermind/Nethermind.Evm/StackAccessTracker.cs";
    internal const string EvmObjectPoolStandardPath = "src/Nethermind/Nethermind.Evm/EvmObjectPool.std.cs";
    internal const string TxExecutionContextPath = "src/Nethermind/Nethermind.Evm/TxExecutionContext.cs";
    internal const string ExecutionEnvironmentPath = "src/Nethermind/Nethermind.Evm/ExecutionEnvironment.cs";
    internal const string WorldStateInterfacePath = "src/Nethermind/Nethermind.Evm/State/IWorldState.cs";
    internal const string SnapshotPath = "src/Nethermind/Nethermind.Evm/State/Snapshot.cs";
    internal const string CodeInfoRepositoryInterfacePath = "src/Nethermind/Nethermind.Evm/ICodeInfoRepository.cs";
    internal const string CodeInfoRepositoryPath = "src/Nethermind/Nethermind.Evm/CodeInfoRepository.cs";
    internal const string CacheCodeInfoRepositoryPath = "src/Nethermind/Nethermind.Evm/CacheCodeInfoRepository.cs";
    internal const string CodeInfoPath = "src/Nethermind/Nethermind.Evm/CodeAnalysis/CodeInfo.cs";
    internal const string CodeInfoFactoryPath = "src/Nethermind/Nethermind.Evm/CodeAnalysis/CodeInfoFactory.cs";
    internal const string JumpDestinationAnalyzerPath = "src/Nethermind/Nethermind.Evm/CodeAnalysis/JumpDestinationAnalyzer.cs";
    internal const string JumpDestinationAnalyzerStandardPath = "src/Nethermind/Nethermind.Evm/CodeAnalysis/JumpDestinationAnalyzer.std.cs";
    internal const string GasPolicyInterfacePath = "src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs";
    internal const string EthereumGasPolicyPath = "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs";
    internal const string AccountAccessPricingPath = "src/Nethermind/Nethermind.Evm/GasPolicy/AccountAccessPricingKernel.cs";
    internal const string PrecompileGasPricingPath = "src/Nethermind/Nethermind.Evm/GasPolicy/PrecompileGasPricingKernel.cs";
    internal const string StateGasChargePath = "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasChargeKernel.cs";
    internal const string StateGasTransitionPath = "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs";
    internal const string StateGasTransitionAdapterPath = "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionAdapterKernel.cs";
    internal const string TransactionGasInitializationPath = "src/Nethermind/Nethermind.Evm/GasPolicy/TransactionGasInitializationKernel.cs";
    internal const string Eip8037BlockGasPath = "src/Nethermind/Nethermind.Evm/GasPolicy/Eip8037BlockGasInclusionCheck.cs";
    internal const string TransactionPath = "src/Nethermind/Nethermind.Core/Transaction.cs";
    internal const string SetCodeValidationPath = "src/Nethermind/Nethermind.Core/Validation/SetCodeTxValidation.cs";
    internal const string TransactionStandardPath = "src/Nethermind/Nethermind.Core/Transaction.std.cs";
    internal const string TransactionExtensionsPath = "src/Nethermind/Nethermind.Core/TransactionExtensions.cs";
    internal const string EvmTransactionExtensionsPath = "src/Nethermind/Nethermind.Evm/TransactionExtensions.cs";
    internal const string AuthorizationTuplePath = "src/Nethermind/Nethermind.Core/AuthorizationTuple.cs";
    internal const string SignaturePath = "src/Nethermind/Nethermind.Core/Crypto/Signature.cs";
    internal const string BlockHeaderPath = "src/Nethermind/Nethermind.Core/BlockHeader.cs";
    internal const string MainnetSpecProviderPath = "src/Nethermind/Nethermind.Specs/MainnetSpecProvider.cs";
    internal const string AmsterdamForkPath = "src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs";
    internal const string MainnetDiPath = "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs";
    internal const string IntrinsicGasCalculatorPath = "src/Nethermind/Nethermind.Evm/IntrinsicGasCalculator.cs";
    internal const string MetricsPath = "src/Nethermind/Nethermind.Evm/Metrics.cs";
    internal const string MetricsStandardPath = "src/Nethermind/Nethermind.Evm/Metrics.std.cs";
    internal const string DispatchFlagsPath = "src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs";
    internal const string TxTracerPath = "src/Nethermind/Nethermind.Evm/Tracing/ITxTracer.cs";
    internal const string TracerExtensionsPath = "src/Nethermind/Nethermind.Evm/Tracing/TracerExtensions.cs";
    internal const string WorldStateTracerPath = "src/Nethermind/Nethermind.Evm/Tracing/State/IWorldStateTracer.cs";
    internal const string StateWorldPath = "src/Nethermind/Nethermind.State/WorldState.cs";
    internal const string StateProviderPath = "src/Nethermind/Nethermind.State/StateProvider.cs";
    internal const string PartialStoragePath = "src/Nethermind/Nethermind.State/PartialStorageProviderBase.cs";
    internal const string PersistentStoragePath = "src/Nethermind/Nethermind.State/PersistentStorageProvider.cs";
    internal const string PersistentStorageStandardPath = "src/Nethermind/Nethermind.State/PersistentStorageProvider.std.cs";
    internal const string TransientStoragePath = "src/Nethermind/Nethermind.State/TransientStorageProvider.cs";
    internal const string LocalMetricsPath = "src/Nethermind/Nethermind.State/LocalMetricsFlush.std.cs";
    internal const string ChangeTypePath = "src/Nethermind/Nethermind.State/ChangeType.cs";
    internal const string SystemTransactionProcessorPath = "src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionProcessor.cs";
    internal const string ExecutionOptionsPath = "src/Nethermind/Nethermind.Evm/TransactionProcessing/ExecutionOptions.cs";
    internal const string RoutingKernelPath = "src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionRoutingKernel.cs";
    internal const string ProcessorInterfacePath = "src/Nethermind/Nethermind.Evm/TransactionProcessing/ITransactionProcessor.cs";
    internal const string TransactionSubstatePath = "src/Nethermind/Nethermind.Evm/TransactionSubstate.cs";
    internal const string SettlementKernelPath = "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionSettlementKernel.cs";

    private const int SchemaVersion = 1;
    private const string ExtractorVersion = "1.0.0";
    private const string IrFileName = "EvmTransactionPreparation.ir.json";
    private const string ManifestFileName = "EvmTransactionPreparation.source-manifest.json";
    private const string DefaultLeanPath = "tools/Evm/Lean/EvmTransactionPreparationExtractor/Generated/EvmTransactionPreparation.lean";
    private const string AdmissionClosurePath = "tools/Evm/Lean/EvmTransactionPreparationExtractor/Admission/ProductionClosure.txt";
    private const string CompilerInventoryPath = "tools/Evm/Lean/EvmTransactionPreparationExtractor/COMPILER_REFERENCE_PINS.json";
    private const string AdmissionClosureSha256 = "dc3e652e6489ed33b922ebfd291833420ab437e42fb3b7664942746f8420780e";
    private const string Kernel = "Nethermind standard-mainnet Amsterdam ordinary EVM transaction preparation";
    private const string AcceptanceState = "bounded-source-extraction-and-universal-refinement-only";
    private const string BoundaryMember = "ExecuteEvmTransaction";
    private const string ProcessorOwner = "TransactionProcessorBase<TGasPolicy>";
    private const string SignedArithmeticResidual = "Production StateGasChargeKernel agreement for negative reservoirs or signed-counter wrap: input representation ranges do not establish nonnegativity or no-wrap reachability.";

    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default
        .WithLanguageVersion(LanguageVersion.CSharp14)
        .WithDocumentationMode(DocumentationMode.Parse)
        .WithKind(SourceCodeKind.Regular);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private static readonly string[] RequiredAssemblies =
    [
        "Autofac", "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.ObjectPool",
        "Nethermind.Api", "Nethermind.Blockchain", "Nethermind.Config", "Nethermind.Consensus",
        "Nethermind.Core", "Nethermind.Crypto", "Nethermind.Db", "Nethermind.Evm",
        "Nethermind.Evm.Precompiles", "Nethermind.Facade", "Nethermind.Init", "Nethermind.Int256",
        "Nethermind.Logging", "Nethermind.Network", "Nethermind.Network.Contract", "Nethermind.Network.Enr",
        "Nethermind.Serialization.Json", "Nethermind.Serialization.Rlp", "Nethermind.Serialization.Ssz",
        "Nethermind.Specs", "Nethermind.State", "Nethermind.Trie", "Nethermind.TxPool",
    ];

    private static readonly string[] ReferenceDirectories =
    [
        "src/Nethermind/artifacts/bin/Nethermind.Init/release",
        "tools/artifacts/bin/Evm/release",
        "src/Nethermind/artifacts/bin/Nethermind.Network.Enr.Test/release",
    ];

    private static readonly HashSet<string> MetadataOnlySourcePaths =
    [
        TxTracerPath,
        TracerExtensionsPath,
        WorldStateTracerPath,
        VmStatePath,
    ];

    private static readonly string[] UnitNames =
    ["processor", "frame", "world", "gas", "code", "core", "fork", "di"];

    internal static ExtractionResult Extract(string repoRoot, string outputDirectory, string? leanOutputPath = null) =>
        ExtractCore(Path.GetFullPath(repoRoot), outputDirectory, leanOutputPath, null, requireReviewedAdmission: true);

    internal static ExtractionResult BuildForTest(
        string repoRoot,
        string outputDirectory,
        IReadOnlyDictionary<string, byte[]> sourceOverrides,
        string? leanOutputPath = null) =>
        ExtractCore(Path.GetFullPath(repoRoot), outputDirectory, leanOutputPath, sourceOverrides, requireReviewedAdmission: false);

    internal static IrDocument LoadIrForTest(string path) => DeserializeIr(File.ReadAllBytes(path));

    internal static void ValidateIrForTest(IrDocument document) => ValidateIr(document);

    internal static void ValidateForEmission(IrDocument document) => ValidateIr(document);

    internal static byte[] EmitLeanForTest(IrDocument document, string irSha256) => LeanEmitter.Emit(document, irSha256);

    private static ExtractionResult ExtractCore(
        string root,
        string outputDirectory,
        string? leanOutputPath,
        IReadOnlyDictionary<string, byte[]>? sourceOverrides,
        bool requireReviewedAdmission)
    {
        AdmissionClosure closure = ReadAdmissionClosure(root);
        if (sourceOverrides is not null && sourceOverrides.Keys.Any(path => !closure.Sources.Any(source => source.Path == path)))
            throw new ExtractionException("A test source override is outside the reviewed source closure.");
        SourceFile[] sources = ReadSources(root, closure, sourceOverrides);
        if (requireReviewedAdmission)
        {
            ValidateSourceClosure(sources, closure);
        }

        DependencyIdentity[] dependencies = ReadDependencies(root, closure);
        CompilerReferenceClosure compilerReferences = BuildMetadataReferences(root, dependencies);
        SemanticContext context = BuildSemanticContext(sources, compilerReferences);
        List<SourceBinding> bindings = [];
        SemanticProfile profile = AdmitPreparation(sources, context, bindings);
        if (requireReviewedAdmission)
        {
            ValidateDependencyClosure(root, dependencies, closure);
        }

        SourceIdentity[] sourceIdentities = sources
            .Select(static source => new SourceIdentity(source.Role, source.RelativePath, source.Sha256, source.SyntaxSha256))
            .ToArray();
        CompositionDependency[] composition = BuildComposition(dependencies);
        IrDocument document = BuildDocument(closure, sourceIdentities, dependencies, compilerReferences, profile, composition);
        ValidateIr(document);
        byte[] irBytes = Serialize(document);
        IrDocument roundTripped = DeserializeIr(irBytes);
        ValidateRoundTrip(document, roundTripped);
        string irSha256 = Sha256(irBytes);
        byte[] leanBytes = LeanEmitter.Emit(roundTripped, irSha256);

        string output = Path.GetFullPath(outputDirectory);
        string leanPath = Path.GetFullPath(leanOutputPath ?? Path.Combine(root, DefaultLeanPath));
        EnsureWithin(leanOutputPath is null ? root : output, leanPath);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(Path.GetDirectoryName(leanPath)!);
        string irPath = Path.Combine(output, IrFileName);
        string manifestPath = Path.Combine(output, ManifestFileName);

        MemberIdentity[] members = AdmitMembers(sources, context, profile.Bindings);
        Manifest manifest = new(
            SchemaVersion,
            ExtractorVersion,
            typeof(CSharpCompilation).Assembly.GetName().Version?.ToString() ?? "unknown",
            LanguageVersion.CSharp14.ToDisplayString(),
            Kernel,
            AcceptanceState,
            closure.Sha256,
            CombinedSourceHash(sourceIdentities),
            sourceIdentities,
            dependencies,
            compilerReferences.Identities,
            composition,
            members,
            profile.Bindings,
            new ArtifactIdentity(IrFileName, irSha256),
            new ArtifactIdentity(Normalize(DefaultLeanPath), Sha256(leanBytes)),
            CombinedSourceHash(sourceIdentities),
            CombinedMemberHash(members),
            CombinedBindingHash(profile.Bindings),
            irSha256);
        ValidateManifest(manifest);
        byte[] manifestBytes = Serialize(manifest);
        Manifest roundTrippedManifest = DeserializeManifest(manifestBytes);
        ValidateRoundTrip(manifest, roundTrippedManifest);
        Write(irPath, irBytes);
        Write(leanPath, leanBytes);
        Write(manifestPath, manifestBytes);

        return new ExtractionResult(
            irPath,
            manifestPath,
            leanPath,
            sources.Length,
            members.Length,
            profile.Operations.Length,
            profile.Branches.Length,
            irSha256,
            Sha256(manifestBytes),
            Sha256(leanBytes));
    }

    internal static void ValidateExistingArtifacts(string repoRoot, string outputDirectory, string? leanOutputPath)
    {
        string root = Path.GetFullPath(repoRoot);
        string tempBase = Path.GetFullPath(Path.GetTempPath());
        string temp = Path.Combine(tempBase, "evm-transaction-preparation-check-" + Guid.NewGuid().ToString("N"));
        string generated = Path.Combine(temp, "Generated");
        string lean = Path.Combine(generated, "EvmTransactionPreparation.lean");
        EnsureWithin(tempBase, temp);
        try
        {
            ExtractionResult fresh = Extract(root, generated, lean);
            CompareFiles(Path.Combine(Path.GetFullPath(outputDirectory), IrFileName), fresh.IrPath);
            CompareFiles(Path.Combine(Path.GetFullPath(outputDirectory), ManifestFileName), fresh.ManifestPath);
            CompareFiles(Path.GetFullPath(leanOutputPath ?? Path.Combine(root, DefaultLeanPath)), fresh.LeanPath);
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    private static AdmissionClosure ReadAdmissionClosure(string root)
    {
        string path = Path.GetFullPath(Path.Combine(root, AdmissionClosurePath));
        EnsureWithin(root, path);
        if (!File.Exists(path)) throw new ExtractionException($"Missing reviewed admission closure: {AdmissionClosurePath}.");
        byte[] bytes = File.ReadAllBytes(path);
        if (!string.Equals(Sha256(bytes), AdmissionClosureSha256, StringComparison.Ordinal))
            throw new ExtractionException("The reviewed admission closure hash changed.");
        List<SourceAdmission> sources = [];
        List<DependencyAdmission> dependencies = [];
        foreach (string raw in StrictUtf8(bytes).Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            string[] columns = line.Split('|');
            if (columns.Length == 4 && columns[0] == "source")
            {
                sources.Add(new SourceAdmission(columns[1], Normalize(columns[2]), RequireSha256(columns[3], "source")));
            }
            else if (columns.Length == 7 && columns[0] == "dependency")
            {
                string[] theorems = columns[6].Length == 0 ? [] : columns[6].Split(',', StringSplitOptions.RemoveEmptyEntries);
                dependencies.Add(new DependencyAdmission(columns[1], columns[2], Normalize(columns[3]), RequireSha256(columns[4], "dependency"), columns[5], theorems));
            }
            else
            {
                throw new ExtractionException($"Malformed admission closure row: {line}");
            }
        }

        if (sources.Count == 0 || dependencies.Count == 0) throw new ExtractionException("The admission closure must contain sources and dependencies.");
        if (sources.Select(static source => source.Path).Distinct(StringComparer.Ordinal).Count() != sources.Count)
            throw new ExtractionException("The admission closure contains duplicate source paths.");
        if (dependencies.Select(static dependency => dependency.Path).Distinct(StringComparer.Ordinal).Count() != dependencies.Count)
            throw new ExtractionException("The admission closure contains duplicate dependency paths.");
        return new(SchemaVersion, sources.ToArray(), dependencies.ToArray(), Sha256(bytes));
    }

    private static SourceFile[] ReadSources(
        string root,
        AdmissionClosure closure,
        IReadOnlyDictionary<string, byte[]>? sourceOverrides)
    {
        List<SourceFile> result = [];
        foreach (SourceAdmission admission in closure.Sources)
        {
            byte[] bytes;
            if (sourceOverrides is not null && sourceOverrides.TryGetValue(admission.Path, out byte[]? overrideBytes))
            {
                bytes = overrideBytes;
            }
            else
            {
                string path = Path.GetFullPath(Path.Combine(root, admission.Path));
                EnsureWithin(root, path);
                if (!File.Exists(path)) throw new ExtractionException($"Missing admitted source: {admission.Path}.");
                bytes = File.ReadAllBytes(path);
            }

            SourceText text = SourceText.From(bytes, bytes.Length, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), canBeEmbedded: true);
            SyntaxTree tree = CSharpSyntaxTree.ParseText(text, ParseOptions, admission.Path);
            CompilationUnitSyntax syntax = tree.GetCompilationUnitRoot();
            Diagnostic[] errors = syntax.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length != 0) throw new ExtractionException($"Cannot parse admitted source {admission.Path}: {string.Join("; ", errors.Select(static diagnostic => diagnostic.GetMessage()))}");
            result.Add(new SourceFile(admission.Path, admission.Role, Sha256(bytes), Sha256(Encoding.UTF8.GetBytes(Canonical(syntax))), bytes, tree, syntax));
        }

        return result.ToArray();
    }

    private static DependencyIdentity[] ReadDependencies(string root, AdmissionClosure closure)
    {
        List<DependencyIdentity> dependencies = [];
        foreach (DependencyAdmission admission in closure.Dependencies)
        {
            string path = Path.GetFullPath(Path.Combine(root, admission.Path));
            EnsureWithin(root, path);
            if (!File.Exists(path)) throw new ExtractionException($"Missing admitted dependency: {admission.Path}.");
            byte[] bytes = File.ReadAllBytes(path);
            string actual = Sha256(bytes);
            if (actual != admission.Sha256) throw new ExtractionException($"Dependency byte drift for {admission.Path}: expected {admission.Sha256}, got {actual}.");
            dependencies.Add(new DependencyIdentity(admission.Kind, admission.Name, admission.Path, actual, admission.Binding, admission.Theorems));
        }

        DependencyIdentity generated = dependencies.Single(dependency => dependency.Name == "ordinary-post-nonce-lean");
        DependencyIdentity manifest = dependencies.Single(dependency => dependency.Name == "ordinary-post-nonce-manifest");
        ValidateOrdinaryPostNonceManifest(File.ReadAllBytes(Path.Combine(root, manifest.Path)), new(generated.Path, generated.Sha256));
        return dependencies.ToArray();
    }

    internal static void ValidateOrdinaryPostNonceManifest(byte[] manifestBytes, ArtifactIdentity generatedLean)
    {
        try
        {
            using JsonDocument manifest = JsonDocument.Parse(manifestBytes);
            if (manifest.RootElement.ValueKind != JsonValueKind.Object ||
                !manifest.RootElement.TryGetProperty("lean", out JsonElement lean) || lean.ValueKind != JsonValueKind.Object ||
                !lean.TryGetProperty("path", out JsonElement path) || path.ValueKind != JsonValueKind.String ||
                !lean.TryGetProperty("sha256", out JsonElement hash) || hash.ValueKind != JsonValueKind.String ||
                path.GetString() != generatedLean.Path || hash.GetString() != generatedLean.Sha256)
                throw new ExtractionException("The ordinary post-nonce manifest does not identify the exact imported generated Lean file.");
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"The ordinary post-nonce manifest is invalid: {exception.Message}");
        }
    }

    private static void ValidateSourceClosure(SourceFile[] sources, AdmissionClosure closure)
    {
        Dictionary<string, SourceFile> actual = sources.ToDictionary(static source => source.RelativePath, StringComparer.Ordinal);
        foreach (SourceAdmission admission in closure.Sources)
        {
            if (!actual.TryGetValue(admission.Path, out SourceFile? source)) throw new ExtractionException($"The admitted source disappeared: {admission.Path}.");
            if (source.Sha256 != admission.Sha256) throw new ExtractionException($"Production source drift for {admission.Path}: expected {admission.Sha256}, got {source.Sha256}.");
        }

        if (actual.Count != closure.Sources.Length) throw new ExtractionException("The source closure contains an unexpected source.");
    }

    private static void ValidateDependencyClosure(string root, DependencyIdentity[] dependencies, AdmissionClosure closure)
    {
        if (dependencies.Length != closure.Dependencies.Length) throw new ExtractionException("The dependency closure count changed.");
        foreach (DependencyIdentity dependency in dependencies)
        {
            string path = Path.GetFullPath(Path.Combine(root, dependency.Path));
            if (Sha256(File.ReadAllBytes(path)) != dependency.Sha256) throw new ExtractionException($"Dependency drift after admission: {dependency.Path}.");
            if (dependency.Theorems.Length == 0 && dependency.Kind == "proof") throw new ExtractionException($"Proof dependency has no theorem identity: {dependency.Name}.");
        }
    }

    private static CompilerReferenceClosure BuildMetadataReferences(string root, DependencyIdentity[] dependencies)
    {
        DependencyIdentity inventoryDependency = dependencies.SingleOrDefault(static dependency => dependency.Kind == "compiler-reference")
            ?? throw new ExtractionException("The admission closure has no compiler-reference inventory dependency.");
        string inventoryPath = Path.GetFullPath(Path.Combine(root, inventoryDependency.Path));
        CompilerInventory inventory;
        try
        {
            inventory = JsonSerializer.Deserialize<CompilerInventory>(File.ReadAllBytes(inventoryPath), JsonOptions)
                ?? throw new ExtractionException("The compiler-reference inventory is empty.");
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"The compiler-reference inventory is invalid: {exception.Message}");
        }

        if (inventory.SchemaVersion != SchemaVersion || inventory.References is null || inventory.Count != inventory.References.Length)
            throw new ExtractionException("The compiler-reference inventory header or count changed.");
        string aggregate = Sha256(Encoding.UTF8.GetBytes(string.Join('\n', inventory.References.Select(static reference =>
            $"{reference.Path}\0{reference.AssemblyName}\0{reference.Sha256}\0{reference.Mvid}\0{reference.Selected}")) + "\n"));
        if (!string.Equals(aggregate, inventory.AggregateSha256, StringComparison.Ordinal)) throw new ExtractionException("The compiler-reference inventory aggregate changed.");

        string platformDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)
            ?? throw new ExtractionException("The runtime platform directory is unavailable.");
        ValidateReferenceInventoryPaths(root, platformDirectory, inventory.References);
        Dictionary<string, string> resolved = new(StringComparer.Ordinal);
        List<MetadataReference> references = [];
        List<CompilerReferenceIdentity> identities = [];
        HashSet<string> selectedNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (CompilerReferencePin pin in inventory.References)
        {
            string logical = Normalize(pin.Path);
            string physical = logical.StartsWith("platform/", StringComparison.Ordinal)
                ? Path.Combine(platformDirectory, logical["platform/".Length..])
                : Path.GetFullPath(Path.Combine(root, logical));
            if (!File.Exists(physical)) throw new ExtractionException($"Missing compiler reference {logical}.");
            string hash = Sha256(File.ReadAllBytes(physical));
            if (!string.Equals(hash, pin.Sha256, StringComparison.Ordinal)) throw new ExtractionException($"Compiler reference drift for {logical}: expected {pin.Sha256}, got {hash}.");
            (string name, string mvid, string[] assemblyReferences) = ReadMetadataIdentity(physical, logical);
            if (name != pin.AssemblyName || mvid != pin.Mvid) throw new ExtractionException($"Compiler reference identity drift for {logical}.");
            if (pin.Selected)
            {
                if (!selectedNames.Add(name)) throw new ExtractionException($"Compiler reference assembly selected twice: {name}.");
                references.Add(MetadataReference.CreateFromFile(physical));
            }

            resolved.Add(logical, physical);
            identities.Add(new CompilerReferenceIdentity(logical, name, hash, mvid, pin.Selected, assemblyReferences));
        }

        foreach (string required in RequiredAssemblies)
        {
            if (!selectedNames.Any(name => string.Equals(name, required, StringComparison.OrdinalIgnoreCase))) throw new ExtractionException($"Compiler-reference inventory is missing selected assembly {required}.");
        }

        return new(references.ToArray(), identities.ToArray(), resolved);
    }

    private static void ValidateReferenceInventoryPaths(string root, string platformDirectory, CompilerReferencePin[] pins)
    {
        HashSet<string> expected = pins.Select(static pin => Normalize(pin.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> actual = [];
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string trusted || string.IsNullOrWhiteSpace(trusted))
            throw new ExtractionException("TRUSTED_PLATFORM_ASSEMBLIES is unavailable for the pinned compiler closure.");
        foreach (string path in trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                     .Select(Path.GetFullPath)
                     .Where(path => string.Equals(Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                         platformDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                     .Where(path => string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase)))
        {
            actual.Add("platform/" + Path.GetFileName(path));
        }

        foreach (string relative in ReferenceDirectories)
        {
            string directory = Path.GetFullPath(Path.Combine(root, relative));
            EnsureWithin(root, directory);
            if (!Directory.Exists(directory)) throw new ExtractionException($"Missing compiler closure directory {relative}.");
            foreach (string path in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
                actual.Add(Normalize(Path.GetRelativePath(root, path)));
        }

        if (!actual.SetEquals(expected))
        {
            string additions = string.Join(",", actual.Except(expected, StringComparer.OrdinalIgnoreCase).OrderBy(static value => value, StringComparer.Ordinal));
            string removals = string.Join(",", expected.Except(actual, StringComparer.OrdinalIgnoreCase).OrderBy(static value => value, StringComparer.Ordinal));
            throw new ExtractionException($"The pinned compiler reference path set changed. additions=[{additions}] removals=[{removals}].");
        }
    }

    private static (string Name, string Mvid, string[] References) ReadMetadataIdentity(string path, string logicalPath)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using PEReader peReader = new(stream);
            if (!peReader.HasMetadata) throw new ExtractionException($"Compiler reference {logicalPath} has no metadata.");
            using MetadataReaderProvider provider = MetadataReaderProvider.FromMetadataImage(peReader.GetMetadata().GetContent());
            MetadataReader reader = provider.GetMetadataReader();
            AssemblyDefinition assembly = reader.GetAssemblyDefinition();
            string name = reader.GetString(assembly.Name);
            string mvid = reader.GetGuid(reader.GetModuleDefinition().Mvid).ToString("D");
            string[] refs = reader.AssemblyReferences.Select(handle => reader.GetString(reader.GetAssemblyReference(handle).Name))
                .Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal).ToArray();
            return (name, mvid, refs);
        }
        catch (ExtractionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ExtractionException($"Compiler reference {logicalPath} is not valid metadata: {exception.Message}");
        }
    }

    private static SemanticContext BuildSemanticContext(SourceFile[] sources, CompilerReferenceClosure references)
    {
        Dictionary<SyntaxTree, SemanticModel> models = [];
        Dictionary<string, SourceFile> byPath = sources.ToDictionary(static source => source.RelativePath, StringComparer.Ordinal);
        string[][] groupPaths =
        [
            [TransactionProcessorPath, SystemTransactionProcessorPath, ExecutionOptionsPath, RoutingKernelPath,
                ProcessorInterfacePath, TransactionSubstatePath, SettlementKernelPath, MetricsPath, MetricsStandardPath,
                DispatchFlagsPath, VirtualMachineInterfacePath, VirtualMachineStaticsAdapterPath, SignaturePath],
            [EvmTransactionExtensionsPath],
            [StackAccessTrackerPath, EvmObjectPoolStandardPath, TxExecutionContextPath, ExecutionEnvironmentPath, "src/Nethermind/Nethermind.Evm/ExecutionType.cs"],
            [StateWorldPath, StateProviderPath, PartialStoragePath, PersistentStoragePath, PersistentStorageStandardPath,
                TransientStoragePath, LocalMetricsPath, ChangeTypePath, WorldStateInterfacePath, SnapshotPath],
            [GasPolicyInterfacePath, EthereumGasPolicyPath, AccountAccessPricingPath, StateGasChargePath, StateGasTransitionPath,
                StateGasTransitionAdapterPath, TransactionGasInitializationPath, Eip8037BlockGasPath, IntrinsicGasCalculatorPath,
                PrecompileGasPricingPath],
            [CodeInfoRepositoryInterfacePath, CodeInfoRepositoryPath, CacheCodeInfoRepositoryPath, MetricsPath, MetricsStandardPath],
            [CodeInfoPath, CodeInfoFactoryPath, JumpDestinationAnalyzerPath, JumpDestinationAnalyzerStandardPath],
            [TransactionPath, TransactionStandardPath, TransactionExtensionsPath, AuthorizationTuplePath, BlockHeaderPath],
            [MainnetSpecProviderPath, AmsterdamForkPath],
            [MainnetDiPath],
        ];

        HashSet<string> compiled = new(StringComparer.Ordinal);
        foreach (string[] paths in groupPaths)
        {
            SourceFile[] group = paths
                .Where(path => byPath.TryGetValue(path, out _))
                .Select(path => byPath[path])
                .ToArray();
            if (group.Length == 0) continue;
            CompileGroup(group, references, CompilationAssemblyName(group[0].RelativePath), models);
            foreach (SourceFile source in group) compiled.Add(source.RelativePath);
        }

        string[] uncompiled = sources.Select(static source => source.RelativePath)
            .Where(path => path != VirtualMachinePath && !MetadataOnlySourcePaths.Contains(path) && !compiled.Any(compiledPath => compiledPath == path))
            .ToArray();
        foreach (string path in uncompiled)
        {
            CompileGroup([byPath[path]], references, CompilationAssemblyName(path), models);
        }

        if (!models.TryGetValue(byPath[TransactionProcessorPath].Tree, out _))
            throw new ExtractionException("The semantic context did not compile TransactionProcessor.cs.");
        return new SemanticContext(models, byPath);
    }

    private static void CompileGroup(
        SourceFile[] group,
        CompilerReferenceClosure references,
        string assemblyName,
        Dictionary<SyntaxTree, SemanticModel> models)
    {
        SyntaxTree[] trees = group.Select(static source => source.Tree).ToArray();
        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName,
            trees,
            references.References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                allowUnsafe: true,
                optimizationLevel: OptimizationLevel.Debug,
                metadataImportOptions: MetadataImportOptions.All));
        Diagnostic[] errors = compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length != 0)
        {
            string members = string.Join(", ", group.Select(static source => source.RelativePath));
            throw new ExtractionException($"The admitted semantic unit has compiler errors ({members}): {string.Join("; ", errors.Take(80).Select(static diagnostic => diagnostic.ToString()))}");
        }

        foreach (SourceFile source in group)
        {
            models.TryAdd(source.Tree, compilation.GetSemanticModel(source.Tree, ignoreAccessibility: true));
        }
    }

    private static string CompilationAssemblyName(string path) => path switch
    {
        MainnetDiPath => "Nethermind.Init",
        StateWorldPath or StateProviderPath or PartialStoragePath or PersistentStoragePath or PersistentStorageStandardPath or
            TransientStoragePath or LocalMetricsPath or ChangeTypePath or WorldStateInterfacePath or SnapshotPath => "Nethermind.State",
        TransactionPath or SetCodeValidationPath or TransactionStandardPath or TransactionExtensionsPath or AuthorizationTuplePath or BlockHeaderPath => "Nethermind.Core",
        MainnetSpecProviderPath or AmsterdamForkPath => "Nethermind.Specs",
        _ => "Nethermind.Evm",
    };

    private static SemanticProfile AdmitPreparation(SourceFile[] sources, SemanticContext context, List<SourceBinding> allBindings)
    {
        SourceFile processor = FindSource(sources, TransactionProcessorPath);
        MethodDeclarationSyntax execute = RequireMethod(processor, BoundaryMember, 16, genericArity: 0);
        MethodDeclarationSyntax validateStatic = RequireMethod(processor, "ValidateStatic", 5, genericArity: 0);
        MethodDeclarationSyntax delegations = RequireMethod(processor, "ProcessDelegations", 6, genericArity: 0);
        MethodDeclarationSyntax authorization = RequireMethod(processor, "IsValidForExecution", 5, genericArity: 0);
        MethodDeclarationSyntax environment = RequireMethod(processor, "BuildExecutionEnvironment", 10, genericArity: 0);
        MethodDeclarationSyntax call = RequireMethod(processor, "ExecuteEvmCall", 14, genericArity: 1);
        MethodDeclarationSyntax deployment = RequireMethod(processor, "PrepareDeployment", 3, genericArity: 0);
        ValidateProcessorOwner(processor, [execute, validateStatic, delegations, authorization, environment, call, deployment], context);
        ValidateFreshAccessInitialization(execute, context);
        ControlFlowIdentity[] controlFlows = AdmitControlFlows(
            [
                ("executeEvmTransaction", execute),
                ("validateStatic", validateStatic),
                ("processDelegations", delegations),
                ("isValidForExecution", authorization),
                ("buildExecutionEnvironment", environment),
                ("executeEvmCall", call),
                ("prepareDeployment", deployment),
            ], context, allBindings);
        ValidateVirtualMachineSource(sources, context, allBindings);
        ValidateAmsterdamFork(sources, context, allBindings);
        AddTransactionAuthorizationTypeBinding(sources, context, allBindings);
        AddRecipientDerivationBinding(sources, context, allBindings);

        List<StageIdentity> stages = [];
        List<BranchIdentity> branches = [];
        List<OperationIdentity> operations = [];
        List<AdapterPremise> adapters = [];
        int stageOrdinal = 0;
        int branchOrdinal = 0;
        int operationOrdinal = 0;

        ValidateDiRoute(sources, context, allBindings);

        AddStage("contextAndOwnership", "initialization", execute, stages, allBindings, context, [
            ("setTransactionContext", SemanticFormula.SetTransactionContext, "VirtualMachine", "VirtualMachine.SetTxExecutionContext", ["tx.SenderAddress", "_codeInfoRepository", "tx.BlobVersionedHashes", "opcodeGasPrice"], ["VirtualMachine.TxExecutionContext"], SemanticEffect.OwnsTxExecutionContext),
            ("incrementCreateMetrics", SemanticFormula.IncrementCreateMetrics, "Metrics.IncrementCreates", "Metrics.IncrementCreates()", ["tx.IsContractCreation"], ["Metrics"], SemanticEffect.RecordsMetrics),
            ("allocateStackAccessTracker", SemanticFormula.AllocateAccessTracker, "new StackAccessTracker", "new StackAccessTracker(tracer.IsTracingAccess)", ["tracer.IsTracingAccess"], ["accessTracker"], SemanticEffect.OwnsStackAccessTracker),
            ("capturePreparationGas", SemanticFormula.CapturePreparationGas, "prePreparationGas", "prePreparationGas=gasAvailable", ["gasAvailable"], ["prePreparationGas"], SemanticEffect.PreservesGas),
            ("captureExecutionIntrinsicGasStandard", SemanticFormula.CaptureExecutionIntrinsicGasStandard, "executionIntrinsicGasStandard", "executionIntrinsicGasStandard=intrinsicGas.Standard", ["intrinsicGas.Standard"], ["executionIntrinsicGasStandard"], SemanticEffect.PreservesGas),
            ("captureDelegationRefunds", SemanticFormula.CaptureDelegationRefunds, "delegationRefunds", "delegationRefunds=0", [], ["delegationRefunds"], SemanticEffect.PreservesRefundCounter),
            ("initializePreparationSnapshot", SemanticFormula.InitializePreparationSnapshot, "preExecutionSnapshot", "preExecutionSnapshot=Snapshot.Empty", [], ["preExecutionSnapshot"], SemanticEffect.CapturesSnapshot),
            ("capturePreExecutionSnapshot", SemanticFormula.CapturePreparationSnapshot, "preExecutionSnapshot", "preExecutionSnapshot=WorldState.TakeSnapshot()", ["WorldState"], ["preExecutionSnapshot"], SemanticEffect.CapturesSnapshot),
        ], ref stageOrdinal, ref operationOrdinal, ref branchOrdinal, branches, operations);

        AddStage("authorization", "postNonceAuthorization", execute, stages, allBindings, context, [
            ("processDelegations", SemanticFormula.ProcessDelegations, "ProcessDelegations", "ProcessDelegations(tx,spec,accessTracker,refgasAvailable,refexecutionIntrinsicGasStandard,outdelegationRefunds)", ["tx", "spec", "accessTracker", "gasAvailable", "executionIntrinsicGasStandard"], ["delegationRefunds", "gasAvailable", "executionIntrinsicGasStandard", "WorldState", "_codeInfoRepository"], SemanticEffect.ChargesStateGas),
        ], ref stageOrdinal, ref operationOrdinal, ref branchOrdinal, branches, operations);
        AddAuthorizationCreateExclusion(validateStatic, context, allBindings, operations, ref operationOrdinal, branches, ref branchOrdinal);
        AddAuthorizationOperations(authorization, context, allBindings, operations, ref operationOrdinal, branches, ref branchOrdinal);
        AddDelegationEffects(delegations, context, allBindings, operations, ref operationOrdinal, branches, ref branchOrdinal);

        AddStage("environment", "executionEnvironment", environment, stages, allBindings, context, [
            ("deriveRecipient", SemanticFormula.DeriveRecipient, "tx.GetRecipient", "tx.GetRecipient(tx.IsContractCreation?WorldState.GetNonce(tx.SenderAddress!):0)", ["tx", "WorldState.GetNonce"], ["recipient"], SemanticEffect.ReadsWorld),
            ("warmTransactionAccesses", SemanticFormula.WarmTransactionAccesses, "WarmUpTxAccesses", "WarmUpTxAccesses(tx,spec,inaccessTracker,recipient,warmUpRecipient:loadRecipient)", ["tx", "spec", "accessTracker", "recipient", "loadRecipient"], ["accessTracker"], SemanticEffect.WarmsAccount),
            ("resolveCreationCode", SemanticFormula.ResolveCreationCode, "CodeInfoFactory.CreateCodeInfo", "CodeInfoFactory.CreateCodeInfo(tx.Data)", ["tx.Data"], ["codeInfo"], SemanticEffect.PreservesCodeIdentity),
            ("resolveCode", SemanticFormula.ResolveRecipientCode, "codeInfoRepository.GetCachedCodeInfo", "codeInfoRepository.GetCachedCodeInfo(recipient,followDelegation:!spec.IsEip8037Enabled,spec,outdelegationAddress)", ["recipient", "spec", "loadRecipient", "preloadedCodeInfo", "preloadedDelegationAddress"], ["codeInfo", "delegationAddress"], SemanticEffect.PreservesCodeIdentity),
            ("resolveDelegationAddress", SemanticFormula.ResolveDelegationTarget, "codeInfoRepository.GetCachedCodeInfo", "codeInfoRepository.GetCachedCodeInfo(delegationAddress,followDelegation:false,spec,out_)", ["delegationAddress", "spec"], ["codeInfo"], SemanticEffect.PreservesDelegationIdentity),
            ("resolveDelegationPrecompile", SemanticFormula.ResolveDelegationPrecompile, "spec.IsPrecompile", "spec.IsPrecompile(delegationAddress)", ["delegationAddress", "spec"], ["isPrecompile"], SemanticEffect.PreservesCodeIdentity),
            ("chargeDelegatedTarget", SemanticFormula.ChargeDelegatedTargetAccess, "TryConsumeAccountAccessGas", "TGasPolicy.TryConsumeAccountAccessGas(refgasAvailable,spec,inaccessTracker,isTracingAccess:false,delegationAddress)", ["gasAvailable", "spec", "accessTracker", "delegationAddress"], ["gasAvailable", "topFrameOutOfGas"], SemanticEffect.ChargesStateGas),
            ("readDelegationTarget", SemanticFormula.ReadDelegatedTarget, "WorldState.AddAccountRead", "WorldState.AddAccountRead(delegationAddress)", ["delegationAddress"], ["WorldState"], SemanticEffect.ReadsWorld),
            ("warmLegacyDelegatedTarget", SemanticFormula.WarmLegacyDelegatedTarget, "accessTracker.WarmUp", "accessTracker.WarmUp(delegationAddress)", ["delegationAddress", "spec.IsEip8037Enabled"], ["accessTracker"], SemanticEffect.WarmsAccount),
            ("rentEnvironment", SemanticFormula.CreateVmInput, "ExecutionEnvironment.Rent", "ExecutionEnvironment.Rent(codeInfo:codeInfo,executingAccount:recipient,caller:tx.SenderAddress!,codeSource:recipient,callDepth:0,value:intx.ValueRef,inputData:ininputData)", ["codeInfo", "recipient", "tx.SenderAddress", "tx.ValueRef", "inputData"], ["env"], SemanticEffect.PreservesInputIdentity),
        ], ref stageOrdinal, ref operationOrdinal, ref branchOrdinal, branches, operations);
        AddEnvironmentBranches(environment, context, allBindings, branches, ref branchOrdinal);

        AddStage("reservoirAndRollback", "eip8037Preparation", execute, stages, allBindings, context, [
            ("capturePostIntrinsicReservoir", SemanticFormula.CapturePostIntrinsicReservoir, "GetStateReservoir", "TGasPolicy.GetStateReservoir(ingasAvailable)", ["gasAvailable"], ["postIntrinsicStateReservoir"], SemanticEffect.PreservesGas),
            ("chargeDeadRecipient", SemanticFormula.ChargeDeadRecipient, "IsDeadAccount", "WorldState.IsDeadAccount(tx.To)", ["tx.To", "WorldState"], ["gasAvailable", "topFrameOutOfGas"], SemanticEffect.ChargesStateGas),
            ("chargeDeadRecipientState", SemanticFormula.ChargeDeadRecipientState, "TryConsumeStateGas", "TGasPolicy.TryConsumeStateGas(refgasAvailable,TGasPolicy.GetNewAccountStateCost())", ["gasAvailable", "TGasPolicy.GetNewAccountStateCost"], ["gasAvailable", "topFrameOutOfGas"], SemanticEffect.ChargesStateGas),
            ("restorePreparationSnapshot", SemanticFormula.RestorePreparationSnapshot, "WorldState.Restore", "WorldState.Restore(preExecutionSnapshot)", ["preExecutionSnapshot"], ["WorldState"], SemanticEffect.RestoresWorld),
            ("restorePreparationGas", SemanticFormula.RestorePreparationGas, "gasAvailable=prePreparationGas", "gasAvailable=prePreparationGas", ["prePreparationGas"], ["gasAvailable"], SemanticEffect.ResetsGas),
            ("buildExecutionIntrinsic", SemanticFormula.BuildExecutionIntrinsic, "executionIntrinsicGas", "IntrinsicGas<TGasPolicy>executionIntrinsicGas=new(executionIntrinsicGasStandard,intrinsicGas.FloorGas)", ["executionIntrinsicGasStandard", "intrinsicGas.FloorGas"], ["executionIntrinsicGas"], SemanticEffect.PreservesGas),
        ], ref stageOrdinal, ref operationOrdinal, ref branchOrdinal, branches, operations);
        AddPreparationBranches(execute, context, allBindings, branches, ref branchOrdinal);
        AddDeploymentOperations(deployment, context, allBindings, operations, ref operationOrdinal);

        AddStage("topLevelCallPreparation", "executeEvmCall", call, stages, allBindings, context, [
            ("captureTopLevelSnapshot", SemanticFormula.CaptureTopLevelSnapshot, "WorldState.TakeSnapshot", "WorldState.TakeSnapshot()", ["WorldState"], ["snapshot"], SemanticEffect.CapturesSnapshot),
            ("haltTopFrame", SemanticFormula.HaltTopFrame, "CompleteEip8037Halt", "CompleteEip8037Halt(tx,spec,opts,refgasAvailable,VirtualMachine.TxExecutionContext.GasPrice,inintrinsicGasStandard,floorGasLong,postIntrinsicStateReservoir)", ["tx", "spec", "opts", "gasAvailable", "intrinsicGasStandard", "floorGasLong", "postIntrinsicStateReservoir"], ["substate", "gasConsumed"], SemanticEffect.PreservesStatus),
            ("haltTopFrameCreate", SemanticFormula.HaltTopFrame, "CompleteEip8037Halt", "CompleteEip8037Halt(tx,spec,opts,refgasAvailable,VirtualMachine.TxExecutionContext.GasPrice,inintrinsicGasStandard,floorGasLong,postIntrinsicStateReservoir)", ["tx", "spec", "opts", "gasAvailable", "intrinsicGasStandard", "floorGasLong", "postIntrinsicStateReservoir"], ["substate", "gasConsumed"], SemanticEffect.PreservesStatus),
            ("prepareCreateDestination", SemanticFormula.PrepareCreateDestination, "PrepareDeployment", "PrepareDeployment(env.ExecutingAccount,spec.IsEip8037Enabled,outlogicalTargetExists)", ["env.ExecutingAccount", "spec.IsEip8037Enabled"], ["logicalTargetExists", "deploymentPrepared"], SemanticEffect.PreservesLogicalExistence),
            ("createLogicalExistence", SemanticFormula.CreateLogicalExistence, "logicalTargetExists", "PrepareDeployment", ["env.ExecutingAccount", "spec.IsEip8037Enabled"], ["logicalTargetExists"], SemanticEffect.PreservesLogicalExistence),
            ("createCollisionResult", SemanticFormula.CreateCollisionResult, "PrepareDeployment", "PrepareDeployment->collision-result", ["env.ExecutingAccount", "spec.IsEip8037Enabled"], ["deploymentPrepared", "collision", "physicalLeafExists", "logicalTargetExists"], SemanticEffect.PreservesStatus),
            ("chargeCreateState", SemanticFormula.ChargeCreateState, "TryConsumeCreateStateGas", "TGasPolicy.TryConsumeCreateStateGas(refgasAvailable)", ["gasAvailable"], ["gasAvailable", "topLevelCreateStateGasCharged"], SemanticEffect.ChargesStateGas),
            ("restoreCreateSnapshot", SemanticFormula.RestoreTopLevelSnapshot, "WorldState.Restore", "WorldState.Restore(snapshot)", ["snapshot"], ["WorldState"], SemanticEffect.RestoresWorld),
            ("rejectCreateCollision", SemanticFormula.RejectCreateCollision, "!deploymentPrepared", "!deploymentPrepared", ["deploymentPrepared", "logicalTargetExists"], ["status", "WorldState"], SemanticEffect.PreservesStatus),
            ("payValue", SemanticFormula.PayValue, "PayValue", "PayValue(tx,spec,opts)", ["tx", "spec", "opts"], ["WorldState"], SemanticEffect.WritesWorld),
            ("nullCodeShortCircuit", SemanticFormula.NullCodeShortCircuit, "env.CodeInfo", "env.CodeInfoisnull", ["env.CodeInfo"], ["substate", "gasConsumed"], SemanticEffect.PreservesStatus),
            ("rentTopLevel", SemanticFormula.RentTopLevel, "VmState.RentTopLevel", "VmState<TGasPolicy>.RentTopLevel(gasAvailable,executionType,env,inaccessedItems,insnapshot)", ["gasAvailable", "executionType", "env", "accessedItems", "snapshot"], ["state"], SemanticEffect.PreservesGas),
            ("executeTransactionBoundary", SemanticFormula.ExecuteTransactionBoundary, "VirtualMachine.ExecuteTransaction", "VirtualMachine.ExecuteTransaction(state,WorldState,tracer)", ["state", "WorldState", "tracer"], ["substate"], SemanticEffect.StopsBeforeVmExecution),
        ], ref stageOrdinal, ref operationOrdinal, ref branchOrdinal, branches, operations);
        AddCallBranches(call, context, allBindings, branches, ref branchOrdinal);

        AddEffectCoverage(operations);

        GasFieldIdentity[] gasFields = AddGasFields(sources, context, allBindings);
        SnapshotIdentity[] snapshots = AddSnapshots(execute, call, context, allBindings);
        EnvironmentIdentity envIdentity = AddEnvironmentIdentity(environment, context, allBindings);
        HandoffIdentity handoff = AddHandoff(execute, delegations, environment, call, context, allBindings);
        adapters.AddRange(BuildAdapters(allBindings, operations));
        SemanticPlan plan = BuildSemanticPlan(stages, branches, operations, gasFields, snapshots, envIdentity, handoff, controlFlows, adapters);

        string[] excluded =
        [
            "VM execution: VirtualMachine.ExecuteTransaction body and opcode/frame execution",
            "post-boundary frame/tail settlement, fees, receipts, destroy-list finalization, and block counters",
            "caller-selected predicates or source-spelling substring matches",
        ];
        string[] orderedEffects = operations.SelectMany(static operation => operation.Effects.Select(effect => $"{operation.Id}:{effect}"))
            .Concat(["boundary:stop-before-VirtualMachine.ExecuteTransaction"])
            .ToArray();
        return new SemanticProfile(
            stages.ToArray(),
            branches.OrderBy(static branch => branch.Ordinal).ToArray(),
            operations.OrderBy(static operation => operation.Ordinal).ToArray(),
            gasFields,
            snapshots,
            envIdentity,
            handoff,
            controlFlows,
            plan,
            adapters.ToArray(),
            orderedEffects,
            excluded,
            "TransactionProcessorBase<TGasPolicy>.ExecuteEvmTransaction after successful post-nonce dispatch through the call immediately before VirtualMachine.ExecuteTransaction",
            UnitNames,
            allBindings.ToArray());
    }

    private static void AddEffectCoverage(List<OperationIdentity> operations)
    {
        AddEffects(operations, "allocateStackAccessTracker", SemanticEffect.PreservesTraceObservations);
        AddEffects(operations, "capturePreExecutionSnapshot", SemanticEffect.PreservesOriginalFacts, SemanticEffect.PreservesCurrentFacts);
        AddEffects(operations, "warmTransactionAccesses", SemanticEffect.WarmsStorage);
        AddEffects(operations, "chargeDelegatedTarget", SemanticEffect.ChargesExecutionGas);
        AddEffects(operations, "delegationSetCode", SemanticEffect.PreservesCurrentFacts);
        AddEffects(operations, "restorePreparationSnapshot", SemanticEffect.PreservesPhysicalExistence, SemanticEffect.PreservesLogicalExistence,
            SemanticEffect.PreservesOriginalFacts, SemanticEffect.PreservesCurrentFacts);
        AddEffects(operations, "restoreCreateSnapshot", SemanticEffect.PreservesPhysicalExistence, SemanticEffect.PreservesLogicalExistence,
            SemanticEffect.PreservesOriginalFacts, SemanticEffect.PreservesCurrentFacts);
        AddEffects(operations, "restorePreparationGas", SemanticEffect.PreservesRefundCounter);
        AddEffects(operations, "rentTopLevel", SemanticEffect.PreservesInputIdentity, SemanticEffect.PreservesStatus, SemanticEffect.PreservesError);
        AddEffects(operations, "executeTransactionBoundary", SemanticEffect.PreservesStatus, SemanticEffect.PreservesError, SemanticEffect.PreservesRefundCounter);
    }

    private static void AddEffects(List<OperationIdentity> operations, string id, params SemanticEffect[] effects)
    {
        int index = operations.FindIndex(operation => operation.Id == id);
        if (index < 0) throw new ExtractionException($"Cannot attach semantic effect coverage to missing operation {id}.");
        SemanticEffect[] merged = operations[index].Effects.Concat(effects).Distinct().ToArray();
        operations[index] = operations[index] with { Effects = merged };
    }

    private static MethodDeclarationSyntax RequireMethod(SourceFile source, string name, int parameterCount, int genericArity)
    {
        MethodDeclarationSyntax[] methods = source.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == name && method.ParameterList.Parameters.Count == parameterCount &&
                (method.TypeParameterList?.Parameters.Count ?? 0) == genericArity &&
                method.Ancestors().OfType<ClassDeclarationSyntax>().Any(type => type.Identifier.ValueText == "TransactionProcessorBase" &&
                    (type.TypeParameterList?.Parameters.Count ?? 0) == 1))
            .ToArray();
        return methods.Length switch
        {
            1 => methods[0],
            0 => throw new ExtractionException($"Missing exact {name}({parameterCount} parameters, generic arity {genericArity}) in the admitted processor."),
            _ => throw new ExtractionException($"The admitted processor has ambiguous {name} declarations."),
        };
    }

    private static void ValidateDiRoute(SourceFile[] sources, SemanticContext context, List<SourceBinding> bindings)
    {
        SourceFile source = FindSource(sources, MainnetDiPath);
        MethodDeclarationSyntax load = source.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == "Load" && method.ParameterList.Parameters.Count == 1 &&
                method.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText == "BlockProcessingModule")
            .SingleOrDefault() ?? throw new ExtractionException("BlockProcessingModule.Load route declaration is missing or ambiguous.");
        (string Method, string Types)[] registrations =
        [
            ("AddScoped", "ITransactionProcessor,EthereumTransactionProcessor"),
            ("AddSingleton", "ITransactionProcessorFactory,TransactionProcessorFactory<EthereumGasPolicy>"),
            ("AddScoped", "ICodeInfoRepository,CacheCodeInfoRepository"),
            ("AddScoped", "IWorldState,WorldState"),
            ("AddScoped", "IVirtualMachine,EthereumVirtualMachine"),
        ];
        foreach ((string registrationMethod, string registration) in registrations)
        {
            GenericNameSyntax[] names = load.DescendantNodes().OfType<GenericNameSyntax>()
                .Where(name => name.Identifier.ValueText == registrationMethod && Canonical(name.TypeArgumentList) == "<" + registration + ">")
                .ToArray();
            if (names.Length != 1) throw new ExtractionException($"The standard-mainnet DI route registration {registration} is missing or ambiguous.");
            InvocationExpressionSyntax invocation = names[0].Parent?.Parent as InvocationExpressionSyntax
                ?? throw new ExtractionException($"The DI route registration {registration} is not an invocation expression.");
            SourceBinding binding = Bind(source.Tree, load, "di." + registration, invocation, context);
            if (binding.TargetSymbol.Length == 0 || binding.TargetSymbolKind != nameof(SymbolKind.Method) || binding.HasCandidateSymbols || binding.IsErrorSymbol)
                throw new ExtractionException($"The DI route registration {registration} did not bind to one exact AddScoped method.");
            bindings.Add(binding);
        }
    }

    private static ControlFlowIdentity[] AdmitControlFlows(
        (string Id, MethodDeclarationSyntax Method)[] methods,
        SemanticContext context,
        List<SourceBinding> bindings)
    {
        List<ControlFlowIdentity> result = [];
        foreach ((string id, MethodDeclarationSyntax method) in methods)
        {
            ControlFlowGraph graph = context.Graph(method)
                ?? throw new ExtractionException($"{method.Identifier.ValueText} has no Roslyn control-flow graph.");
            BasicBlock[] reachable = graph.Blocks.Where(static block => block.IsReachable).OrderBy(static block => block.Ordinal).ToArray();
            if (reachable.Length == 0) throw new ExtractionException($"{method.Identifier.ValueText} has no reachable CFG blocks.");

            HashSet<int> reachableOrdinals = reachable.Select(static block => block.Ordinal).ToHashSet();
            HashSet<int> normalExits = [];
            HashSet<int> exceptionalExits = [];
            HashSet<string> backEdges = [];
            HashSet<string> edges = [];
            HashSet<int> loopHeaders = [];
            foreach (BasicBlock block in reachable)
            {
                foreach (ControlFlowBranch branch in Branches(block))
                {
                    bool exceptional = IsExceptionalBranch(branch) || ContainsThrow(block);
                    if (branch.Destination is not BasicBlock destination || !destination.IsReachable)
                    {
                        if (exceptional) exceptionalExits.Add(block.Ordinal);
                        else throw new ExtractionException($"{method.Identifier.ValueText} has an unclassified reachable normal CFG exit from block {block.Ordinal}.");
                        continue;
                    }

                    edges.Add(block.Ordinal + "->" + destination.Ordinal);
                    if (destination.Kind.ToString() == "Exit")
                    {
                        if (exceptional) exceptionalExits.Add(block.Ordinal);
                        else normalExits.Add(block.Ordinal);
                    }

                    if (destination.Ordinal <= block.Ordinal)
                    {
                        string edge = block.Ordinal + "->" + destination.Ordinal;
                        backEdges.Add(edge);
                        loopHeaders.Add(destination.Ordinal);
                    }
                }

                if (ContainsThrow(block)) exceptionalExits.Add(block.Ordinal);
            }

            if (normalExits.Count == 0 && exceptionalExits.Count == 0)
                throw new ExtractionException($"{method.Identifier.ValueText} has no reachable normal or exceptional exit edge.");
            if (id == "processDelegations" && backEdges.Count == 0)
                throw new ExtractionException("ProcessDelegations must retain a reachable authorization-loop back edge.");
            if (normalExits.Overlaps(exceptionalExits))
                throw new ExtractionException($"{method.Identifier.ValueText} has an exit block classified as both normal and exceptional.");

            SourceBinding binding = BindMethodDeclaration(context.Source(Normalize(method.SyntaxTree.FilePath)), method,
                "cfg." + id, context);
            bindings.Add(binding);
            result.Add(new ControlFlowIdentity(
                id,
                OwnerPath(method),
                SignatureOf(method),
                reachableOrdinals.OrderBy(static value => value).ToArray(),
                edges.OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
                normalExits.OrderBy(static value => value).ToArray(),
                exceptionalExits.OrderBy(static value => value).ToArray(),
                backEdges.OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
                loopHeaders.OrderBy(static value => value).ToArray(),
                binding));
        }

        return result.ToArray();
    }

    private static IEnumerable<ControlFlowBranch> Branches(BasicBlock block)
    {
        if (block.FallThroughSuccessor is ControlFlowBranch fallThrough)
            yield return fallThrough;
        if (block.ConditionalSuccessor is ControlFlowBranch conditional)
            yield return conditional;
    }

    private static bool IsExceptionalBranch(ControlFlowBranch branch)
    {
        string semantics = branch.Semantics.ToString();
        return semantics.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0 ||
            semantics.IndexOf("Throw", StringComparison.OrdinalIgnoreCase) >= 0 ||
            semantics.IndexOf("Finally", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool ContainsThrow(BasicBlock block) =>
        block.Operations.Any(ContainsThrow) || block.BranchValue is not null && ContainsThrow(block.BranchValue);

    private static bool ContainsThrow(IOperation operation)
    {
        if (operation is IThrowOperation) return true;
        foreach (IOperation child in operation.ChildOperations)
        {
            if (ContainsThrow(child)) return true;
        }

        return false;
    }

    private static void ValidateProcessorOwner(SourceFile source, MethodDeclarationSyntax[] methods, SemanticContext context)
    {
        foreach (MethodDeclarationSyntax method in methods)
        {
            IMethodSymbol symbol = context.Model(source.Tree).GetDeclaredSymbol(method)
                ?? throw new ExtractionException($"The declared symbol for {method.Identifier.ValueText} is missing.");
            if (symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != "Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<TGasPolicy>" ||
                HasErrorSymbol(symbol) || !symbol.Locations.Any(location => location.IsInSource && ReferenceEquals(location.SourceTree, source.Tree)))
            {
                throw new ExtractionException($"{method.Identifier.ValueText} did not bind to the exact production TransactionProcessorBase<TGasPolicy> owner.");
            }

            if (TryGetGraph(context.Model(source.Tree), method) is null)
                throw new ExtractionException($"{method.Identifier.ValueText} has no Roslyn control-flow graph.");
        }
    }

    private static void ValidateAmsterdamFork(SourceFile[] sources, SemanticContext context, List<SourceBinding> bindings)
    {
        SourceFile forkSource = FindSource(sources, AmsterdamForkPath);
        ClassDeclarationSyntax[] forkTypes = forkSource.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(type => type.Identifier.ValueText == "Amsterdam" && (type.TypeParameterList?.Parameters.Count ?? 0) == 0)
            .ToArray();
        if (forkTypes.Length != 1) throw new ExtractionException("The admitted Amsterdam fork type is missing or ambiguous.");

        SemanticModel forkModel = context.Model(forkSource.Tree);
        INamedTypeSymbol forkType = forkModel.GetDeclaredSymbol(forkTypes[0])
            ?? throw new ExtractionException("The Amsterdam fork type has no semantic declaration.");
        if (forkType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != "Nethermind.Specs.Forks.Amsterdam" ||
            !forkType.Locations.Any(location => location.IsInSource && ReferenceEquals(location.SourceTree, forkSource.Tree)) ||
            HasErrorSymbol(forkType))
        {
            throw new ExtractionException("The admitted Amsterdam fork type did not bind to the exact production namespace/type.");
        }

        if (forkType.BaseType is not INamedTypeSymbol baseType || baseType.Name != "NamedReleaseSpec" ||
            baseType.TypeArguments.Length != 1 || !SymbolEqualityComparer.Default.Equals(baseType.TypeArguments[0], forkType) ||
            baseType.ContainingNamespace.ToDisplayString() != "Nethermind.Specs.Forks")
        {
            throw new ExtractionException("Amsterdam must inherit NamedReleaseSpec<Amsterdam>.");
        }

        MethodDeclarationSyntax[] applyMethods = forkTypes[0].Members.OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == "Apply" && method.ParameterList.Parameters.Count == 1 &&
                (method.TypeParameterList?.Parameters.Count ?? 0) == 0)
            .ToArray();
        if (applyMethods.Length != 1) throw new ExtractionException("Amsterdam.Apply is missing or ambiguous.");
        IMethodSymbol applySymbol = forkModel.GetDeclaredSymbol(applyMethods[0])
            ?? throw new ExtractionException("Amsterdam.Apply has no semantic declaration.");
        if (!applySymbol.IsOverride || !applySymbol.ReturnsVoid || applySymbol.Parameters.Length != 1 || HasErrorSymbol(applySymbol) ||
            applySymbol.Parameters[0].Type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != "Nethermind.Specs.Forks.NamedReleaseSpec")
        {
            throw new ExtractionException("Amsterdam.Apply did not bind to the exact NamedReleaseSpec override.");
        }

        bindings.Add(BindDeclaration(forkSource, forkTypes[0], "fork.amsterdam.type", forkTypes[0], context));
        bindings.Add(BindMethodDeclaration(forkSource, applyMethods[0], "fork.amsterdam.apply", context));
        RequireAmsterdamFlag(forkSource, applyMethods[0], forkModel, "IsEip8037Enabled", context, bindings);
        RequireAmsterdamFlag(forkSource, applyMethods[0], forkModel, "IsEip8038Enabled", context, bindings);
        ValidateAmsterdamBase(forkTypes[0], forkModel);

        SourceFile mainnetSource = FindSource(sources, MainnetSpecProviderPath);
        ClassDeclarationSyntax[] mainnetTypes = mainnetSource.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(type => type.Identifier.ValueText == "MainnetSpecProvider" && (type.TypeParameterList?.Parameters.Count ?? 0) == 0)
            .ToArray();
        if (mainnetTypes.Length != 1) throw new ExtractionException("The admitted MainnetSpecProvider type is missing or ambiguous.");
        SemanticModel mainnetModel = context.Model(mainnetSource.Tree);
        INamedTypeSymbol mainnetType = mainnetModel.GetDeclaredSymbol(mainnetTypes[0])
            ?? throw new ExtractionException("MainnetSpecProvider has no semantic declaration.");
        if (mainnetType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != "Nethermind.Specs.MainnetSpecProvider" ||
            mainnetType.BaseType?.Name != "ForkScheduleSpecProvider" ||
            !mainnetType.Locations.Any(location => location.IsInSource && ReferenceEquals(location.SourceTree, mainnetSource.Tree)) ||
            HasErrorSymbol(mainnetType))
        {
            throw new ExtractionException("MainnetSpecProvider did not bind to the exact fork-schedule owner.");
        }

        VariableDeclaratorSyntax[] timestamps = mainnetTypes[0].DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(variable => variable.Identifier.ValueText == "AmsterdamBlockTimestamp")
            .ToArray();
        if (timestamps.Length != 1) throw new ExtractionException("AmsterdamBlockTimestamp is missing or ambiguous.");
        IFieldSymbol timestamp = mainnetModel.GetDeclaredSymbol(timestamps[0]) as IFieldSymbol
            ?? throw new ExtractionException("AmsterdamBlockTimestamp has no field symbol.");
        ulong expectedTimestamp = ulong.MaxValue - 1;
        if (!timestamp.IsConst || timestamp.Type.SpecialType != SpecialType.System_UInt64 ||
            timestamp.ConstantValue is not ulong actualTimestamp || actualTimestamp != expectedTimestamp)
        {
            throw new ExtractionException("AmsterdamBlockTimestamp is not the pinned mainnet activation timestamp.");
        }

        MemberAccessExpressionSyntax[] instances = mainnetTypes[0].DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(member => member.Name.Identifier.ValueText == "Instance")
            .Where(member => mainnetModel.GetSymbolInfo(member).Symbol is IPropertySymbol property &&
                property.Name == "Instance" && property.IsStatic &&
                property.ContainingType.Name == "NamedReleaseSpec" && property.ContainingType.TypeArguments.Length == 1 &&
                SymbolEqualityComparer.Default.Equals(property.ContainingType.TypeArguments[0], forkType) &&
                property.ContainingType.ContainingNamespace.ToDisplayString() == "Nethermind.Specs.Forks")
            .ToArray();
        if (instances.Length != 1) throw new ExtractionException("MainnetSpecProvider must select Amsterdam.Instance exactly once.");
        AssignmentExpressionSyntax mapping = instances[0].Ancestors().OfType<AssignmentExpressionSyntax>()
            .FirstOrDefault(assignment => ReferenceEquals(assignment.Right, instances[0]))
            ?? throw new ExtractionException("The Amsterdam schedule mapping is not an exact dictionary assignment.");
        IdentifierNameSyntax[] keys = mapping.Left.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Where(identifier => SymbolEqualityComparer.Default.Equals(mainnetModel.GetSymbolInfo(identifier).Symbol, timestamp))
            .ToArray();
        if (keys.Length != 1 || mapping.Ancestors().OfType<ConstructorDeclarationSyntax>().Count() != 1)
            throw new ExtractionException("MainnetSpecProvider does not map AmsterdamBlockTimestamp to Amsterdam.Instance in its schedule constructor.");
        IOperation? mappingOperation = mainnetModel.GetOperation(mapping);
        if (mappingOperation is null || mappingOperation is IInvalidOperation)
            throw new ExtractionException("The Amsterdam schedule mapping has no valid semantic operation.");
        ValidateOperationTree(mainnetModel, mappingOperation, "fork.mainnet.amsterdamMapping");

        bindings.Add(BindDeclaration(mainnetSource, mainnetTypes[0], "fork.mainnet.type", mainnetTypes[0], context));
    }

    private static void RequireAmsterdamFlag(
        SourceFile source,
        MethodDeclarationSyntax apply,
        SemanticModel model,
        string propertyName,
        SemanticContext context,
        List<SourceBinding> bindings)
    {
        AssignmentExpressionSyntax[] assignments = apply.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(assignment => model.GetOperation(assignment) is ISimpleAssignmentOperation operation &&
                operation.Target is IPropertyReferenceOperation property && property.Property.Name == propertyName &&
                operation.Value.ConstantValue.HasValue && Equals(operation.Value.ConstantValue.Value, true))
            .ToArray();
        if (assignments.Length != 1) throw new ExtractionException($"Amsterdam.Apply must set {propertyName} to true exactly once.");
        bindings.Add(Bind(source.Tree, apply, "fork.amsterdam." + propertyName, assignments[0], context));
    }

    private static void ValidateAmsterdamBase(ClassDeclarationSyntax forkType, SemanticModel model)
    {
        BaseTypeSyntax[] baseTypes = forkType.BaseList?.Types.ToArray() ?? [];
        if (baseTypes.Length != 1) throw new ExtractionException("Amsterdam must have exactly one production base type.");
        MemberAccessExpressionSyntax[] parentInstances = baseTypes[0].DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(member => member.Name.Identifier.ValueText == "Instance")
            .Where(member => model.GetSymbolInfo(member).Symbol is IPropertySymbol property && property.IsStatic &&
                property.ContainingType.Name == "NamedReleaseSpec" && property.ContainingType.TypeArguments.Length == 1 &&
                property.ContainingType.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == "Nethermind.Specs.Forks.BPO2" &&
                property.ContainingType.ContainingNamespace.ToDisplayString() == "Nethermind.Specs.Forks")
            .ToArray();
        if (parentInstances.Length != 1) throw new ExtractionException("Amsterdam must inherit the pinned BPO2.Instance schedule.");
    }

    private static void ValidateVirtualMachineSource(SourceFile[] sources, SemanticContext context, List<SourceBinding> bindings)
    {
        SourceFile source = FindSource(sources, VirtualMachinePath);
        ClassDeclarationSyntax[] classes = source.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(type => type.Identifier.ValueText == "VirtualMachine" && (type.TypeParameterList?.Parameters.Count ?? 0) == 1)
            .ToArray();
        if (classes.Length != 1) throw new ExtractionException("VirtualMachine<TGasPolicy> source declaration is missing or ambiguous.");
        MethodDeclarationSyntax[] execute = classes[0].Members.OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == "ExecuteTransaction" && method.TypeParameterList is not null)
            .ToArray();
        if (execute.Length != 1 || execute[0].ParameterList.Parameters.Count != 3)
            throw new ExtractionException("VirtualMachine.ExecuteTransaction<TTracingInst> source boundary changed.");
        string[] names = execute[0].ParameterList.Parameters.Select(static parameter => parameter.Identifier.ValueText).ToArray();
        if (!names.SequenceEqual(["vmState", "worldState", "txTracer"], StringComparer.Ordinal))
            throw new ExtractionException("VirtualMachine.ExecuteTransaction parameter identities changed.");

        SourceFile interfaceSource = FindSource(sources, VirtualMachineInterfacePath);
        InterfaceDeclarationSyntax[] interfaces = interfaceSource.Root.DescendantNodes().OfType<InterfaceDeclarationSyntax>()
            .Where(declaration => declaration.Identifier.ValueText == "IVirtualMachine" && (declaration.TypeParameterList?.Parameters.Count ?? 0) == 1)
            .ToArray();
        if (interfaces.Length != 1 || interfaces[0].Members.OfType<MethodDeclarationSyntax>().Count(method => method.Identifier.ValueText == "ExecuteTransaction") != 2)
            throw new ExtractionException("IVirtualMachine<TGasPolicy> ExecuteTransaction overload set changed.");
        SemanticModel interfaceModel = context.Model(interfaceSource.Tree);
        INamedTypeSymbol interfaceType = interfaceModel.GetDeclaredSymbol(interfaces[0])
            ?? throw new ExtractionException("IVirtualMachine<TGasPolicy> has no semantic declaration.");
        if (interfaceType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != "Nethermind.Evm.IVirtualMachine<TGasPolicy>" ||
            interfaceType.TypeKind != TypeKind.Interface || HasErrorSymbol(interfaceType))
            throw new ExtractionException("IVirtualMachine<TGasPolicy> did not bind to the exact production interface symbol.");
        MethodDeclarationSyntax[] overloads = interfaces[0].Members.OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == "ExecuteTransaction")
            .OrderBy(static method => method.TypeParameterList?.Parameters.Count ?? 0)
            .ToArray();
        foreach (MethodDeclarationSyntax overload in overloads)
        {
            IMethodSymbol symbol = interfaceModel.GetDeclaredSymbol(overload)
                ?? throw new ExtractionException("IVirtualMachine.ExecuteTransaction has no semantic declaration.");
            if (HasErrorSymbol(symbol) || !symbol.Parameters.Select(static parameter => parameter.Name)
                    .SequenceEqual(["state", "worldState", "txTracer"], StringComparer.Ordinal) ||
                !symbol.Parameters.Select(static parameter => parameter.RefKind.ToString())
                    .SequenceEqual(["None", "None", "None"], StringComparer.Ordinal))
                throw new ExtractionException("IVirtualMachine.ExecuteTransaction parameter identity changed.");
            bindings.Add(BindMethodDeclaration(interfaceSource, overload, "vm.interface.executeTransaction." +
                ((overload.TypeParameterList?.Parameters.Count ?? 0) == 0 ? "nonGeneric" : "generic"), context));
        }
        bindings.Add(BindDeclaration(interfaceSource, interfaces[0], "vm.interface", interfaces[0], context));
    }

    private static void AddStage(
        string id,
        string kind,
        MethodDeclarationSyntax method,
        List<StageIdentity> stages,
        List<SourceBinding> allBindings,
        SemanticContext context,
        (string Id, SemanticFormula Formula, string Expression, string Expected, string[] Reads, string[] Writes, SemanticEffect Effect)[] specifications,
        ref int stageOrdinal,
        ref int operationOrdinal,
        ref int branchOrdinal,
        List<BranchIdentity> branches,
        List<OperationIdentity> operations)
    {
        List<string> operationIds = [];
        List<SourceBinding> stageBindings = [];
        foreach ((string operationId, SemanticFormula formula, string expression, string expected, string[] reads, string[] writes, SemanticEffect effect) in specifications)
        {
            if (string.IsNullOrWhiteSpace(expression) || string.IsNullOrWhiteSpace(expected))
                throw new ExtractionException($"Stage {id} operation {operationId} has no normalized expression/expectation.");
            if (formula == SemanticFormula.ExecuteTransactionBoundary)
            {
                InvocationExpressionSyntax[] boundaryCalls = FindVmBoundaryCalls(method, context);
                InvocationExpressionSyntax firstCall = FindVmBoundaryOverload(boundaryCalls, method, context, generic: false);
                InvocationExpressionSyntax secondCall = FindVmBoundaryOverload(boundaryCalls, method, context, generic: true);
                SourceBinding first = Bind(method.SyntaxTree, method, operationId + ".off", firstCall, context);
                SourceBinding second = Bind(method.SyntaxTree, method, operationId + ".on", secondCall, context);
                allBindings.Add(first);
                allBindings.Add(second);
                stageBindings.Add(first);
                operations.Add(new OperationIdentity(operationId, operationOrdinal++, id, formula,
                    first.CanonicalSyntax + " || " + second.CanonicalSyntax, expected,
                    reads.Concat(["tracing-overload"]).ToArray(), reads, writes, "boundary-only; body-not-admitted",
                    [effect], first));
                operationIds.Add(operationId);
                continue;
            }
            SyntaxNode node = SelectFormulaNode(method, operationId, formula, context);
            SourceBinding binding = Bind(method.SyntaxTree, method, operationId, node, context);
            allBindings.Add(binding);
            stageBindings.Add(binding);
            string failure = formula switch
            {
                SemanticFormula.ChargeDelegatedTargetAccess or SemanticFormula.AuthorizationNewAccountStateCharge or
                SemanticFormula.AuthorizationAccountWriteCharge or SemanticFormula.AuthorizationPerAuthStateCharge or
                SemanticFormula.ChargeCreateState or SemanticFormula.ChargeDeadRecipient or SemanticFormula.ChargeDeadRecipientState => "false=>preparation-out-of-gas",
                SemanticFormula.RestorePreparationSnapshot or SemanticFormula.RestoreTopLevelSnapshot => "restore-is-visible-before-terminal",
                SemanticFormula.ExecuteTransactionBoundary => "boundary-only; body-not-admitted",
                _ => "none",
            };
            operations.Add(new OperationIdentity(
                operationId,
                operationOrdinal++,
                id,
                formula,
                Canonical(node),
                expected,
                reads,
                reads,
                writes,
                failure,
                [effect],
                binding));
            operationIds.Add(operationId);
        }

        if (stageBindings.Count == 0) throw new ExtractionException($"Stage {id} has no source-bound operations.");
        stages.Add(new StageIdentity(id, stageOrdinal++, ProcessorOwner + "." + method.Identifier.ValueText, kind, stageBindings[0], operationIds.ToArray()));
        _ = branchOrdinal;
        _ = branches;
    }

    private static SyntaxNode SelectFormulaNode(MethodDeclarationSyntax method, string operationId, SemanticFormula formula, SemanticContext context) => formula switch
        {
            SemanticFormula.SetTransactionContext => FindInvocation(method, context, "SetTxExecutionContext"),
            SemanticFormula.IncrementCreateMetrics => FindInvocation(method, context, "IncrementCreates"),
            SemanticFormula.AllocateAccessTracker => FindAccessTrackerCreation(method, context),
            SemanticFormula.CapturePreparationGas => FindVariable(method, "prePreparationGas", "gasAvailable"),
            SemanticFormula.CaptureExecutionIntrinsicGasStandard => FindVariable(method, "executionIntrinsicGasStandard", "intrinsicGas.Standard"),
            SemanticFormula.CaptureDelegationRefunds => FindVariable(method, "delegationRefunds", "0"),
            SemanticFormula.InitializePreparationSnapshot => FindVariable(method, "preExecutionSnapshot", "Snapshot.Empty"),
            SemanticFormula.CapturePreparationSnapshot => FindAssignment(method, "preExecutionSnapshot", "WorldState.TakeSnapshot()"),
            SemanticFormula.ProcessDelegations => FindInvocation(method, context, "ProcessDelegations"),
            SemanticFormula.DeriveRecipient => FindInvocation(method, context, "GetRecipient"),
            SemanticFormula.WarmTransactionAccesses => FindInvocation(method, context, "WarmUpTxAccesses"),
            SemanticFormula.ResolveCreationCode => FindInvocation(method, context, "CreateCodeInfo"),
            SemanticFormula.ResolveRecipientCode => FindInvocation(method, context, "GetCachedCodeInfo",
                static operation => HasArgumentExpression(operation, "followDelegation", "!spec.IsEip8037Enabled")),
            SemanticFormula.ResolveDelegationTarget => FindInvocation(method, context, "GetCachedCodeInfo",
                static operation => HasArgumentExpression(operation, "followDelegation", "false")),
            SemanticFormula.ResolveDelegationPrecompile => FindInvocation(method, context, "IsPrecompile"),
            SemanticFormula.ChargeDelegatedTargetAccess => FindInvocation(method, context, "TryConsumeAccountAccessGas"),
            SemanticFormula.ReadDelegatedTarget => FindInvocation(method, context, "AddAccountRead"),
            SemanticFormula.WarmLegacyDelegatedTarget => FindLegacyDelegatedTargetWarmUp(method, context),
            SemanticFormula.CreateVmInput => FindInvocation(method, context, "Rent"),
            SemanticFormula.CapturePostIntrinsicReservoir => FindVariable(method, "postIntrinsicStateReservoir", "TGasPolicy.GetStateReservoir(ingasAvailable)"),
            SemanticFormula.ChargeDeadRecipient => FindInvocation(method, context, "IsDeadAccount"),
            SemanticFormula.ChargeDeadRecipientState => FindInvocation(method, context, "TryConsumeStateGas"),
            SemanticFormula.RestorePreparationSnapshot => FindInvocation(method, context, "Restore"),
            SemanticFormula.RestorePreparationGas => FindAssignment(method, "gasAvailable", "prePreparationGas"),
            SemanticFormula.BuildExecutionIntrinsic => FindVariable(method, "executionIntrinsicGas", "new"),
            SemanticFormula.CaptureTopLevelSnapshot => FindInvocation(method, context, "TakeSnapshot"),
            SemanticFormula.HaltTopFrame => operationId == "haltTopFrameCreate"
                ? FindLastInvocation(method, context, "CompleteEip8037Halt")
                : FindFirstInvocation(method, context, "CompleteEip8037Halt"),
            SemanticFormula.PrepareCreateDestination => FindInvocation(method, context, "PrepareDeployment"),
            SemanticFormula.CreateLogicalExistence => FindInvocation(method, context, "PrepareDeployment"),
            SemanticFormula.CreateCollisionResult => FindInvocation(method, context, "PrepareDeployment"),
            SemanticFormula.ChargeCreateState => FindInvocation(method, context, "TryConsumeCreateStateGas"),
            SemanticFormula.RestoreTopLevelSnapshot => FindInvocation(method, context, "Restore",
                static operation => HasAncestorIf(operation.Syntax, "!deploymentPrepared")),
            SemanticFormula.RejectCreateCollision => FindIfCondition(method, "!deploymentPrepared"),
            SemanticFormula.PayValue => FindInvocation(method, context, "PayValue"),
            SemanticFormula.NullCodeShortCircuit => FindNullCodeCondition(method),
            SemanticFormula.RentTopLevel => FindInvocation(method, context, "RentTopLevel"),
            SemanticFormula.ExecuteTransactionBoundary => FindVmBoundaryAnchor(method, context),
            _ => throw new ExtractionException($"No source selector exists for semantic formula {formula}."),
        };

    private static InvocationExpressionSyntax FindInvocation(
        MethodDeclarationSyntax method,
        SemanticContext context,
        string targetName,
        Func<IInvocationOperation, bool>? shape = null)
    {
        InvocationExpressionSyntax[] matches = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() == method)
            .Where(invocation => context.Model(method.SyntaxTree).GetOperation(invocation) is IInvocationOperation operation &&
                operation.TargetMethod.Name == targetName && !HasErrorSymbol(operation.TargetMethod) &&
                (shape is null || shape(operation)))
            .OrderBy(static invocation => invocation.SpanStart)
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ExtractionException($"Missing exact semantic invocation {targetName} in {method.Identifier.ValueText}."),
            _ => throw new ExtractionException($"Semantic invocation {targetName} is ambiguous in {method.Identifier.ValueText}."),
        };
    }

    private static InvocationExpressionSyntax FindLegacyDelegatedTargetWarmUp(MethodDeclarationSyntax method, SemanticContext context)
    {
        InvocationExpressionSyntax invocation = FindInvocation(method, context, "WarmUp", static operation =>
            operation.TargetMethod.ContainingType.ToDisplayString() == "Nethermind.Evm.StackAccessTracker" &&
            operation.Instance is IParameterReferenceOperation { Parameter.Name: "accessTracker", Parameter.RefKind: RefKind.In } &&
            operation.Arguments.Length == 1 &&
            operation.Arguments[0].Value is ILocalReferenceOperation { Local.Name: "delegationAddress" });
        ElseClauseSyntax? legacyBranch = invocation.Ancestors().OfType<ElseClauseSyntax>().FirstOrDefault();
        IfStatementSyntax[] targetPresenceGuards = invocation.Ancestors().OfType<IfStatementSyntax>()
            .Where(condition => Canonical(condition.Condition) == "delegationAddressisnotnull")
            .ToArray();
        if (legacyBranch?.Parent is not IfStatementSyntax forkGuard || Canonical(forkGuard.Condition) != "spec.IsEip8037Enabled" ||
            targetPresenceGuards.Length != 1 ||
            targetPresenceGuards[0].Statement.SpanStart > invocation.SpanStart ||
            targetPresenceGuards[0].Statement.Span.End < invocation.Span.End)
        {
            throw new ExtractionException("Delegated-target WarmUp must retain the exact legacy fork branch and target-presence guard.");
        }

        return invocation;
    }

    private static InvocationExpressionSyntax FindFirstInvocation(MethodDeclarationSyntax method, SemanticContext context, string targetName)
    {
        InvocationExpressionSyntax? invocation = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() == method)
            .Where(invocation => context.Model(method.SyntaxTree).GetOperation(invocation) is IInvocationOperation operation &&
                operation.TargetMethod.Name == targetName && !HasErrorSymbol(operation.TargetMethod))
            .OrderBy(static invocation => invocation.SpanStart)
            .FirstOrDefault();
        return invocation ?? throw new ExtractionException($"Missing exact semantic invocation {targetName} in {method.Identifier.ValueText}.");
    }

    private static InvocationExpressionSyntax FindLastInvocation(MethodDeclarationSyntax method, SemanticContext context, string targetName)
    {
        InvocationExpressionSyntax? invocation = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() == method)
            .Where(invocation => context.Model(method.SyntaxTree).GetOperation(invocation) is IInvocationOperation operation &&
                operation.TargetMethod.Name == targetName && !HasErrorSymbol(operation.TargetMethod))
            .OrderByDescending(static invocation => invocation.SpanStart)
            .FirstOrDefault();
        return invocation ?? throw new ExtractionException($"Missing exact semantic invocation {targetName} in {method.Identifier.ValueText}.");
    }

    private static SyntaxNode FindObjectCreation(MethodDeclarationSyntax method, SemanticContext context, string typeName)
    {
        SyntaxNode[] matches = method.DescendantNodes()
            .Where(static expression => expression is ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax)
            .Where(expression => expression.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() == method)
            .Where(expression => context.Model(method.SyntaxTree).GetOperation(expression) is IObjectCreationOperation operation &&
                operation.Type is INamedTypeSymbol type && type.Name == typeName && !HasErrorSymbol(type))
            .OrderBy(static expression => expression.SpanStart)
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ExtractionException($"Missing exact semantic object construction {typeName} in {method.Identifier.ValueText}."),
            _ => throw new ExtractionException($"Semantic object construction {typeName} is ambiguous in {method.Identifier.ValueText}."),
        };
    }

    private static SyntaxNode FindAccessTrackerCreation(MethodDeclarationSyntax method, SemanticContext context)
    {
        SyntaxNode creation = FindObjectCreation(method, context, "StackAccessTracker");
        SemanticModel model = context.Model(method.SyntaxTree);
        IObjectCreationOperation operation = model.GetOperation(creation) as IObjectCreationOperation
            ?? throw new ExtractionException("The StackAccessTracker allocation is not an object-creation operation.");
        IMethodSymbol executeSymbol = model.GetDeclaredSymbol(method)
            ?? throw new ExtractionException("The source entry method has no exact semantic symbol.");
        IParameterSymbol tracer = executeSymbol.Parameters.SingleOrDefault(parameter => parameter.Name == "tracer")
            ?? throw new ExtractionException("ExecuteEvmTransaction.tracer parameter is missing.");
        if (operation.Type is not INamedTypeSymbol trackerType ||
            trackerType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != "Nethermind.Evm.StackAccessTracker" ||
            trackerType.ContainingAssembly.Identity.Name != "Nethermind.Evm" || operation.Arguments.Length != 1 ||
            operation.Arguments[0].Value is not IPropertyReferenceOperation property ||
            property.Property.Name != "IsTracingAccess" ||
            property.Property.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != "Nethermind.Evm.Tracing.ITxTracer" ||
            property.Property.ContainingAssembly.Identity.Name != "Nethermind.Evm" ||
            property.Instance?.Type?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != "Nethermind.Evm.Tracing.ITxTracer" ||
            tracer.Type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != "Nethermind.Evm.Tracing.ITxTracer" ||
            tracer.RefKind != RefKind.None ||
            property.Instance is not IOperation tracerInstance ||
            UnwrapParameterReference(tracerInstance) is not IParameterReferenceOperation tracerReference ||
            !SymbolEqualityComparer.Default.Equals(tracerReference.Parameter, tracer))
            throw new ExtractionException("StackAccessTracker must be initialized from the exact tracer.IsTracingAccess property.");
        return creation;
    }

    private static void ValidateFreshAccessInitialization(MethodDeclarationSyntax method, SemanticContext context)
    {
        SemanticModel model = context.Model(method.SyntaxTree);
        SyntaxNode creation = FindAccessTrackerCreation(method, context);
        VariableDeclaratorSyntax variable = creation.AncestorsAndSelf().OfType<VariableDeclaratorSyntax>().SingleOrDefault()
            ?? throw new ExtractionException("The StackAccessTracker allocation is not bound to a local variable.");
        ILocalSymbol tracker = model.GetDeclaredSymbol(variable) as ILocalSymbol
            ?? throw new ExtractionException("The StackAccessTracker local has no exact semantic symbol.");
        InvocationExpressionSyntax dispatch = FindInvocation(method, context, "ProcessDelegations");
        HashSet<ISymbol> trackerAliases = new(SymbolEqualityComparer.Default) { tracker };
        IEnumerable<SyntaxNode> preDispatchNodes = method.DescendantNodes()
            .Where(node => node.SpanStart > creation.Span.End && node.SpanStart < dispatch.SpanStart)
            .OrderBy(static node => node.SpanStart);
        foreach (SyntaxNode node in preDispatchNodes)
        {
            switch (node)
            {
                case VariableDeclaratorSyntax declarator when declarator.Initializer is not null:
                {
                    IOperation? initializer = model.GetOperation(declarator.Initializer.Value);
                    if (initializer is not null && ContainsSymbolReference(initializer, trackerAliases))
                    {
                        ILocalSymbol? alias = model.GetDeclaredSymbol(declarator) as ILocalSymbol;
                        if (alias is not null) trackerAliases.Add(alias);
                        throw new ExtractionException("The source entry copies or aliases StackAccessTracker before post-nonce dispatch; the production-entry access projection is not fresh.");
                    }

                    break;
                }
                case AssignmentExpressionSyntax assignment:
                {
                    IOperation? right = model.GetOperation(assignment.Right);
                    if (right is not null && ContainsSymbolReference(right, trackerAliases))
                        throw new ExtractionException("The source entry assigns a StackAccessTracker alias before post-nonce dispatch; the production-entry access projection is not fresh.");
                    break;
                }
                case InvocationExpressionSyntax invocation when model.GetOperation(invocation) is IInvocationOperation operation:
                    if (operation.Instance is not null && ContainsSymbolReference(operation.Instance, trackerAliases) ||
                        operation.Arguments.Any(argument => ContainsSymbolReference(argument.Value, trackerAliases)))
                        throw new ExtractionException("The source entry mutates or passes StackAccessTracker before post-nonce dispatch; the production-entry access projection is not fresh.");
                    break;
            }
        }
    }

    private static bool HasArgumentExpression(IInvocationOperation operation, string parameterName, string canonicalValue)
    {
        IArgumentOperation? argument = operation.Arguments.FirstOrDefault(argument => argument.Parameter?.Name == parameterName);
        if (argument is null) return false;
        return canonicalValue switch
        {
            "!spec.IsEip8037Enabled" => argument.Value is IUnaryOperation unary && unary.OperatorKind == UnaryOperatorKind.Not &&
                unary.Operand is IPropertyReferenceOperation property && IsExactEip8037Property(property),
            "false" => argument.Value is ILiteralOperation literal && literal.ConstantValue.HasValue &&
                literal.ConstantValue.Value is bool value && !value,
            _ => Canonical(argument.Value.Syntax) == canonicalValue,
        };
    }

    private static bool HasAnyArgumentExpression(IInvocationOperation operation, string canonicalValue)
    {
        int open = canonicalValue.IndexOf('(');
        string expectedName = (open < 0 ? canonicalValue : canonicalValue[..open]).Split('.').Last();
        return operation.Arguments.Any(argument => argument.Value is IInvocationOperation nested &&
            nested.TargetMethod.Name == expectedName && nested.Arguments.Length == 0 && !HasErrorSymbol(nested.TargetMethod));
    }

    private static bool IsExactEip8037ThenBranch(SyntaxNode node, SemanticModel model) =>
        IsExactEip8037Branch(node, model, inElse: false);

    private static bool IsExactEip8037ElseBranch(SyntaxNode node, SemanticModel model) =>
        IsExactEip8037Branch(node, model, inElse: true);

    private static bool IsExactEip8037Branch(SyntaxNode node, SemanticModel model, bool inElse)
    {
        IfStatementSyntax[] guards = node.Ancestors().OfType<IfStatementSyntax>()
            .Where(statement => Canonical(statement.Condition) == "spec.IsEip8037Enabled")
            .Where(statement => IsExactEip8037Condition(statement.Condition, model))
            .ToArray();
        if (guards.Length != 1) return false;
        IfStatementSyntax guard = guards[0];
        if (guard.Else is null) return !inElse;
        bool insideElse = node.Ancestors().Any(ancestor => ReferenceEquals(ancestor, guard.Else));
        return insideElse == inElse;
    }

    private static bool IsExactEip8037Condition(ExpressionSyntax condition, SemanticModel model)
    {
        IOperation? operation = model.GetOperation(condition);
        return operation is IPropertyReferenceOperation property && IsExactEip8037Property(property);
    }

    private static bool IsExactEip8037Property(IPropertyReferenceOperation property) =>
        property.Property.Name == "IsEip8037Enabled" &&
        property.Property.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == "Nethermind.Core.Specs.IReleaseSpec" &&
        property.Property.ContainingAssembly.Identity.Name == "Nethermind.Core" &&
        property.Instance?.Type is ITypeSymbol instanceType &&
        instanceType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == "Nethermind.Core.Specs.IReleaseSpec";

    private static bool IsExactEip7702Property(IPropertyReferenceOperation property) =>
        property.Property.Name == "IsEip7702Enabled" &&
        property.Property.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == "Nethermind.Core.Specs.IReleaseSpec" &&
        property.Property.ContainingAssembly.Identity.Name == "Nethermind.Core" &&
        property.Instance?.Type is ITypeSymbol instanceType &&
        instanceType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == "Nethermind.Core.Specs.IReleaseSpec";

    private static bool IsExactEip7702AuthorizationCondition(ExpressionSyntax condition, SemanticModel model)
    {
        IOperation? operation = model.GetOperation(condition);
        return operation is IBinaryOperation binary && binary.OperatorKind == BinaryOperatorKind.ConditionalAnd &&
            binary.LeftOperand is IPropertyReferenceOperation eip7702 && IsExactEip7702Property(eip7702) &&
            binary.RightOperand is IPropertyReferenceOperation authorizationList &&
            IsExactHasAuthorizationListProperty(authorizationList);
    }

    private static bool IsExactSetCodeComparison(BinaryExpressionSyntax expression, SemanticModel model)
    {
        IOperation? operation = model.GetOperation(expression);
        return operation is IBinaryOperation binary && binary.OperatorKind == BinaryOperatorKind.Equals &&
            binary.LeftOperand is IPropertyReferenceOperation typeProperty &&
            typeProperty.Property.Name == "Type" &&
            typeProperty.Property.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == "Nethermind.Core.Transaction" &&
            typeProperty.Property.ContainingAssembly.Identity.Name == "Nethermind.Core" &&
            typeProperty.Instance?.Type?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == "Nethermind.Core.Transaction" &&
            binary.RightOperand is IFieldReferenceOperation setCodeField &&
            setCodeField.Field.Name == "SetCode" &&
            setCodeField.Field.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == "Nethermind.Core.TxType" &&
            setCodeField.Field.ContainingAssembly.Identity.Name == "Nethermind.Core" &&
            Canonical(expression) == "Type==TxType.SetCode";
    }

    private static bool IsExactAuthorizationListNonNull(IsPatternExpressionSyntax expression, SemanticModel model)
    {
        IOperation? operation = model.GetOperation(expression.Expression);
        return operation is IPropertyReferenceOperation property && IsExactAuthorizationListProperty(property) &&
            expression.Pattern is UnaryPatternSyntax unary && unary.OperatorToken.IsKind(SyntaxKind.NotKeyword) &&
            unary.Pattern is ConstantPatternSyntax constant && constant.Expression.IsKind(SyntaxKind.NullLiteralExpression) &&
            Canonical(expression) == "AuthorizationListisnotnull";
    }

    private static bool IsExactAuthorizationListLength(BinaryExpressionSyntax expression, SemanticModel model)
    {
        IOperation? operation = model.GetOperation(expression);
        return operation is IBinaryOperation binary && binary.OperatorKind == BinaryOperatorKind.GreaterThan &&
            binary.LeftOperand is IPropertyReferenceOperation length && length.Property.Name == "Length" &&
            length.Instance is IPropertyReferenceOperation property && IsExactAuthorizationListProperty(property) &&
            IsZeroLiteral(binary.RightOperand) && Canonical(expression) == "AuthorizationList.Length>0";
    }

    private static bool IsExactAuthorizationListProperty(IPropertyReferenceOperation property) =>
        property.Property.Name == "AuthorizationList" &&
        property.Property.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == "Nethermind.Core.Transaction" &&
        property.Property.ContainingAssembly.Identity.Name == "Nethermind.Core" &&
        property.Instance?.Type is ITypeSymbol instanceType &&
        instanceType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == "Nethermind.Core.Transaction";

    private static bool IsZeroLiteral(IOperation operation) => operation switch
    {
        ILiteralOperation literal => literal.ConstantValue.HasValue && literal.ConstantValue.Value is 0,
        IConversionOperation conversion => IsZeroLiteral(conversion.Operand),
        _ => false,
    };

    private static bool HasFailureReturnForLocal(IfStatementSyntax guard, ILocalSymbol localSymbol, SemanticModel model) =>
        guard.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Any(returnStatement =>
            returnStatement.Expression is not null && returnStatement.Expression.DescendantNodesAndSelf()
                .Select(node => model.GetOperation(node))
                .OfType<IPropertyReferenceOperation>()
                .Any(property => property.Property.Name == "Error" &&
                    property.Instance is ILocalReferenceOperation localReference &&
                    SymbolEqualityComparer.Default.Equals(localReference.Local, localSymbol)));

    private static bool IsNegatedLocalCondition(ExpressionSyntax condition, ILocalSymbol localSymbol, SemanticModel model)
    {
        if (model.GetOperation(condition) is not IUnaryOperation unary || unary.OperatorKind != UnaryOperatorKind.Not)
            return false;

        IOperation operand = unary.Operand;
        while (operand is IConversionOperation conversion) operand = conversion.Operand;
        return operand is ILocalReferenceOperation localReference &&
            SymbolEqualityComparer.Default.Equals(localReference.Local, localSymbol);
    }

    private static bool IsExactSupportsAuthorizationListCondition(ExpressionSyntax condition, SemanticModel model)
    {
        IOperation? operation = model.GetOperation(condition);
        return operation is IPropertyReferenceOperation property && IsExactSupportsAuthorizationListProperty(property);
    }

    private static bool IsExactSupportsAuthorizationListProperty(IPropertyReferenceOperation property) =>
        property.Property.Name == "SupportsAuthorizationList" &&
        property.Property.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == "Nethermind.Core.Transaction" &&
        property.Property.ContainingAssembly.Identity.Name == "Nethermind.Core" &&
        property.Instance?.Type is ITypeSymbol instanceType &&
        instanceType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == "Nethermind.Core.Transaction";

    private static bool IsExactHasAuthorizationListProperty(IPropertyReferenceOperation property) =>
        property.Property.Name == "HasAuthorizationList" &&
        property.Property.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == "Nethermind.Core.Transaction" &&
        property.Property.ContainingAssembly.Identity.Name == "Nethermind.Core" &&
        property.Instance?.Type is ITypeSymbol instanceType &&
        instanceType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == "Nethermind.Core.Transaction";

    private static bool IsExactStorageCollisionExclusion(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        IfStatementSyntax[] guards = invocation.Ancestors().OfType<IfStatementSyntax>()
            .Where(statement => Canonical(statement.Condition) == "!includeStorageCollision")
            .Where(statement => IsExactStorageCollisionExclusionCondition(statement.Condition, model))
            .Where(statement => statement.Else is null && statement.Statement.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().SequenceEqual([invocation]))
            .ToArray();
        return guards.Length == 1;
    }

    private static bool IsExactStorageCollisionExclusionCondition(ExpressionSyntax condition, SemanticModel model)
    {
        IOperation? operation = model.GetOperation(condition);
        return operation is IUnaryOperation unary && unary.OperatorKind == UnaryOperatorKind.Not &&
            unary.Operand is IParameterReferenceOperation parameter && parameter.Parameter.Name == "includeStorageCollision" &&
            parameter.Parameter.Type.SpecialType == SpecialType.System_Boolean;
    }

    private static bool IsExactAssignment(SyntaxNode node, string canonical)
    {
        AssignmentExpressionSyntax? assignment = node.Ancestors().OfType<AssignmentExpressionSyntax>().FirstOrDefault();
        return assignment is not null && Canonical(assignment) == canonical;
    }

    private static bool HasAncestorIf(SyntaxNode node, string canonicalCondition) =>
        node.Ancestors().OfType<IfStatementSyntax>().Any(statement => Canonical(statement.Condition) == canonicalCondition);

    private static bool IsVirtualMachineBoundary(IInvocationOperation operation)
    {
        IMethodSymbol target = operation.TargetMethod;
        return target.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == "Nethermind.Evm.IVirtualMachine<TGasPolicy>" &&
            target.ContainingAssembly.Identity.Name == "Nethermind.Evm" && target.Name == "ExecuteTransaction" &&
            !HasErrorSymbol(target) && target.Parameters.Select(static parameter => parameter.Name)
                .SequenceEqual(["state", "worldState", "txTracer"], StringComparer.Ordinal) &&
            operation.Arguments.Select(static argument => argument.Parameter?.Name ?? string.Empty)
                .SequenceEqual(["state", "worldState", "txTracer"], StringComparer.Ordinal) &&
            operation.Arguments.Select(static argument => argument.Parameter?.RefKind.ToString() ?? "missing")
                .SequenceEqual(["None", "None", "None"], StringComparer.Ordinal) &&
            target.Parameters.Select(static parameter => parameter.RefKind.ToString())
                .SequenceEqual(["None", "None", "None"], StringComparer.Ordinal);
    }

    private static InvocationExpressionSyntax[] FindVmBoundaryCalls(MethodDeclarationSyntax method, SemanticContext context)
    {
        InvocationExpressionSyntax[] calls = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() == method)
            .Where(invocation => context.Model(method.SyntaxTree).GetOperation(invocation) is IInvocationOperation operation &&
                IsVirtualMachineBoundary(operation))
            .OrderBy(static invocation => invocation.SpanStart)
            .ToArray();
        IInvocationOperation[] operations = calls.Select(call => context.Model(method.SyntaxTree).GetOperation(call) as IInvocationOperation
            ?? throw new ExtractionException("The exact VM boundary call is not an invocation operation.")).ToArray();
        bool hasGenericOverload = operations.Any(static operation => operation.TargetMethod.IsGenericMethod);
        bool hasNonGenericOverload = operations.Any(static operation => !operation.TargetMethod.IsGenericMethod);
        if (!hasGenericOverload || !hasNonGenericOverload || operations.GroupBy(static operation =>
                operation.TargetMethod.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), StringComparer.Ordinal)
            .Any(static group => group.Count() != 1))
            throw new ExtractionException("The VM boundary must retain one exact generic and one exact non-generic tracing overload.");
        return calls;
    }

    private static InvocationExpressionSyntax FindVmBoundaryAnchor(MethodDeclarationSyntax method, SemanticContext context)
    {
        InvocationExpressionSyntax[] calls = FindVmBoundaryCalls(method, context);
        return calls.OrderBy(static invocation => invocation.SpanStart).First();
    }

    private static InvocationExpressionSyntax FindVmBoundaryOverload(
        InvocationExpressionSyntax[] calls,
        MethodDeclarationSyntax method,
        SemanticContext context,
        bool generic)
    {
        InvocationExpressionSyntax[] matches = calls.Where(call =>
        {
            IInvocationOperation operation = context.Model(method.SyntaxTree).GetOperation(call) as IInvocationOperation
                ?? throw new ExtractionException("The exact VM boundary call is not an invocation operation.");
            return operation.TargetMethod.IsGenericMethod == generic;
        }).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            _ => throw new ExtractionException($"The VM boundary must retain one {(generic ? "generic" : "non-generic")} tracing overload."),
        };
    }

    private static AssignmentExpressionSyntax FindAssignment(MethodDeclarationSyntax method, string left, string right)
    {
        AssignmentExpressionSyntax[] matches = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(assignment => assignment.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() == method &&
                Canonical(assignment.Left) == left && (right.Length == 0 || Canonical(assignment.Right) == right))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ExtractionException($"Missing exact assignment {left}={right} in {method.Identifier.ValueText}."),
            _ => throw new ExtractionException($"Assignment {left}={right} is ambiguous in {method.Identifier.ValueText}."),
        };
    }

    private static VariableDeclaratorSyntax FindVariable(MethodDeclarationSyntax method, string name, string initializerKind)
    {
        VariableDeclaratorSyntax[] matches = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(variable => variable.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() == method &&
                variable.Identifier.ValueText == name && variable.Initializer is not null &&
                (initializerKind switch
                {
                    "" => true,
                    "new" => variable.Initializer.Value.IsKind(SyntaxKind.ObjectCreationExpression) ||
                        variable.Initializer.Value.IsKind(SyntaxKind.ImplicitObjectCreationExpression),
                    _ => Canonical(variable.Initializer.Value) == initializerKind,
                }))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ExtractionException($"Missing exact variable initializer {name} in {method.Identifier.ValueText}."),
            _ => throw new ExtractionException($"Variable initializer {name} is ambiguous in {method.Identifier.ValueText}."),
        };
    }

    private static IsPatternExpressionSyntax FindNullCodeCondition(MethodDeclarationSyntax method)
    {
        IsPatternExpressionSyntax[] matches = method.DescendantNodes().OfType<IsPatternExpressionSyntax>()
            .Where(expression => expression.Pattern is ConstantPatternSyntax constant && constant.Expression.IsKind(SyntaxKind.NullLiteralExpression) &&
                expression.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == "CodeInfo")
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ExtractionException("The top-level null-code short-circuit condition is missing."),
            _ => throw new ExtractionException("The top-level null-code short-circuit condition is ambiguous."),
        };
    }

    private static ExpressionSyntax FindIfCondition(MethodDeclarationSyntax method, string canonical)
    {
        IfStatementSyntax[] matches = method.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(statement => Canonical(statement.Condition) == canonical)
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0].Condition,
            0 => throw new ExtractionException($"Missing exact if condition {canonical} in {method.Identifier.ValueText}."),
            _ => throw new ExtractionException($"If condition {canonical} is ambiguous in {method.Identifier.ValueText}."),
        };
    }

    private static void AddAuthorizationCreateExclusion(
        MethodDeclarationSyntax method,
        SemanticContext context,
        List<SourceBinding> allBindings,
        List<OperationIdentity> operations,
        ref int operationOrdinal,
        List<BranchIdentity> branches,
        ref int branchOrdinal)
    {
        InvocationExpressionSyntax validation = FindInvocation(method, context, "ValidateNoContractCreation");
        SemanticModel model = context.Model(method.SyntaxTree);
        IInvocationOperation validationOperation = model.GetOperation(validation) as IInvocationOperation
            ?? throw new ExtractionException("ValidateStatic no-creation validation is not an invocation operation.");
        if (validationOperation.TargetMethod.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) !=
                "Nethermind.Core.Validation.SetCodeTxValidation.ValidateNoContractCreation(Nethermind.Core.Transaction)" ||
            validationOperation.TargetMethod.ContainingAssembly.Identity.Name != "Nethermind.Core" ||
            validationOperation.Instance is not null ||
            !validationOperation.Arguments.Select(static argument => argument.Parameter?.Name ?? "missing")
                .SequenceEqual(["transaction"], StringComparer.Ordinal) ||
            !validationOperation.Arguments.Select(static argument => argument.Parameter?.RefKind.ToString() ?? "missing")
                .SequenceEqual(["None"], StringComparer.Ordinal))
            throw new ExtractionException("ValidateStatic must bind the exact SetCodeTxValidation.ValidateNoContractCreation call.");
        IfStatementSyntax[] guards = method.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(statement => Canonical(statement.Condition) == "tx.SupportsAuthorizationList")
            .Where(statement => IsExactSupportsAuthorizationListCondition(statement.Condition, model))
            .Where(statement => statement.Else is null && statement.Statement.DescendantNodes().Any(node => ReferenceEquals(node, validation)))
            .OrderBy(static statement => statement.SpanStart)
            .ToArray();
        IfStatementSyntax guard = guards.Length switch
        {
            1 => guards[0],
            0 => throw new ExtractionException("ValidateStatic is missing the exact authorization-list guard enclosing ValidateNoContractCreation."),
            _ => throw new ExtractionException("ValidateStatic has ambiguous authorization-list guards enclosing ValidateNoContractCreation."),
        };
        SourceBinding guardBinding = Bind(method.SyntaxTree, method, "authorizationCreateExclusionGuard", guard.Condition, context);
        allBindings.Add(guardBinding);
        VariableDeclaratorSyntax noCreationVariable = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .SingleOrDefault(variable => variable.Identifier.ValueText == "noCreation" &&
                variable.Initializer?.Value.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(invocation => ReferenceEquals(invocation, validation)) == true)
            ?? throw new ExtractionException("ValidateStatic no-creation validation result is not assigned to the exact local.");
        ILocalSymbol noCreationSymbol = model.GetDeclaredSymbol(noCreationVariable) as ILocalSymbol
            ?? throw new ExtractionException("ValidateStatic no-creation validation local has no exact symbol.");
        IfStatementSyntax noCreationFailureGuard = method.DescendantNodes().OfType<IfStatementSyntax>()
            .SingleOrDefault(candidate => Canonical(candidate.Condition) == "!noCreation" && candidate.Else is null &&
                IsNegatedLocalCondition(candidate.Condition, noCreationSymbol, model) &&
                HasFailureReturnForLocal(candidate, noCreationSymbol, model))
            ?? throw new ExtractionException("ValidateStatic must return on the exact negated ValidateNoContractCreation result.");
        allBindings.Add(Bind(method.SyntaxTree, method, "authorizationCreateExclusionFailureGuard", noCreationFailureGuard.Condition, context));
        branches.Add(MakeBranch("staticValidation.authorizationList", branchOrdinal++, method, guard, TerminalKind.None,
            [SemanticEffect.PreservesStatus, SemanticEffect.PreservesError], guardBinding, context));
        SourceBinding binding = Bind(method.SyntaxTree, method, "authorizationCreateExclusion", validation, context);
        allBindings.Add(binding);
        InvocationExpressionSyntax listValidation = FindInvocation(method, context, "ValidateAuthorizationList");
        IInvocationOperation listValidationOperation = context.Model(method.SyntaxTree).GetOperation(listValidation) as IInvocationOperation
            ?? throw new ExtractionException("ValidateStatic authorization-list nonempty validation is not an invocation operation.");
        if (listValidationOperation.TargetMethod.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) !=
                "Nethermind.Core.Validation.SetCodeTxValidation.ValidateAuthorizationList(Nethermind.Core.Transaction)" ||
            listValidationOperation.TargetMethod.ContainingAssembly.Identity.Name != "Nethermind.Core" ||
            listValidationOperation.Instance is not null ||
            !listValidationOperation.Arguments.Select(static argument => argument.Parameter?.Name ?? "missing")
                .SequenceEqual(["transaction"], StringComparer.Ordinal) ||
            !listValidationOperation.Arguments.Select(static argument => argument.Parameter?.RefKind.ToString() ?? "missing")
                .SequenceEqual(["None"], StringComparer.Ordinal))
            throw new ExtractionException("ValidateStatic must bind the exact SetCodeTxValidation.ValidateAuthorizationList call.");
        SourceBinding listBinding = Bind(method.SyntaxTree, method, "authorizationListNonEmpty", listValidation, context);
        allBindings.Add(listBinding);
        VariableDeclaratorSyntax listVariable = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .SingleOrDefault(variable => variable.Identifier.ValueText == "authList" &&
                variable.Initializer?.Value.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(invocation => ReferenceEquals(invocation, listValidation)) == true)
            ?? throw new ExtractionException("ValidateStatic authorization-list result is not assigned to the exact local.");
        ILocalSymbol listSymbol = model.GetDeclaredSymbol(listVariable) as ILocalSymbol
            ?? throw new ExtractionException("ValidateStatic authorization-list result local has no exact symbol.");
        IfStatementSyntax listFailureGuard = method.DescendantNodes().OfType<IfStatementSyntax>()
            .SingleOrDefault(candidate => Canonical(candidate.Condition) == "!authList" && candidate.Else is null &&
                IsNegatedLocalCondition(candidate.Condition, listSymbol, model) &&
                HasFailureReturnForLocal(candidate, listSymbol, model))
            ?? throw new ExtractionException("ValidateStatic must return on the exact negated ValidateAuthorizationList result.");
        allBindings.Add(Bind(method.SyntaxTree, method, "authorizationListNonEmptyFailureGuard", listFailureGuard.Condition, context));
        operations.Add(new OperationIdentity(
            "authorizationListNonEmpty",
            operationOrdinal++,
            "staticValidation",
            SemanticFormula.ValidateAuthorization,
            listBinding.CanonicalSyntax,
            "ValidateAuthorizationList",
            ["tx.SupportsAuthorizationList", "tx.AuthorizationList"],
            ["tx.SupportsAuthorizationList", "tx.AuthorizationList"],
            ["validationResult"],
            "false=>static-validation-return",
            [SemanticEffect.PreservesStatus, SemanticEffect.PreservesError],
            listBinding));
        operations.Add(new OperationIdentity(
            "authorizationCreateExclusion",
            operationOrdinal++,
            "staticValidation",
            SemanticFormula.AuthorizationCreateExclusion,
            binding.CanonicalSyntax,
            "ValidateNoContractCreation",
            ["tx.SupportsAuthorizationList", "tx.IsContractCreation"],
            ["tx.SupportsAuthorizationList", "tx.IsContractCreation"],
            ["validationResult"],
            "false=>static-validation-return",
            [SemanticEffect.PreservesStatus, SemanticEffect.PreservesError],
            binding));
    }

    private static void AddAuthorizationOperations(
        MethodDeclarationSyntax method,
        SemanticContext context,
        List<SourceBinding> allBindings,
        List<OperationIdentity> operations,
        ref int operationOrdinal,
        List<BranchIdentity> branches,
        ref int branchOrdinal)
    {
        string[] expectedConditions =
        [
            "authorizationTuple.ChainId!=0&&SpecProvider.ChainId!=authorizationTuple.ChainId",
            "authorizationTuple.Nonce==ulong.MaxValue",
            "authorizationTuple.Authorityisnull||s>SecP256k1Curve.HalfN||authorizationTuple.AuthoritySignature.V-Signature.VOffset>1",
            "WorldState.HasCode(authorizationTuple.Authority)",
            "!hasDelegation",
            "authNonce!=authorizationTuple.Nonce",
        ];
        IfStatementSyntax[] conditions = RequireIfConditions(method, expectedConditions, "IsValidForExecution");
        string[] ids = ["chainIdGuard", "authorizationNonceGuard", "authorizationSignatureGuard", "authorityCodeGuard", "delegationCodeGuard", "authorityNonceGuard"];
        SemanticEffect[] effects = [SemanticEffect.PreservesError, SemanticEffect.PreservesError, SemanticEffect.PreservesError, SemanticEffect.WarmsAccount, SemanticEffect.ReadsWorld, SemanticEffect.PreservesError];
        SemanticFormula[] formulas = [SemanticFormula.ValidateAuthorization, SemanticFormula.ValidateAuthorization, SemanticFormula.ValidateAuthorization, SemanticFormula.AuthorizationWarm, SemanticFormula.AuthorizationAccountRead, SemanticFormula.ValidateAuthorization];
        string[] terminals = ["InvalidChainId", "InvalidNonce", "InvalidSignature", "InvalidAsCodeDeployed", "InvalidAsCodeDeployed", "IncorrectNonce"];
        for (int index = 0; index < conditions.Length; index++)
        {
            SourceBinding binding = Bind(method.SyntaxTree, method, ids[index], conditions[index].Condition, context);
            allBindings.Add(binding);
            operations.Add(new OperationIdentity(ids[index], operationOrdinal++, "authorizationValidation", formulas[index], binding.CanonicalSyntax,
                binding.CanonicalSyntax,
                ["authorizationTuple", "spec", "SpecProvider", "WorldState"],
                ["authorizationTuple", "spec", "SpecProvider", "WorldState"],
                ["error", "hasDelegation"], "return " + terminals[index], [effects[index]], binding));
            branches.Add(MakeBranch("authorization." + ids[index], branchOrdinal++, method, conditions[index], TerminalKind.PreparationReturn,
                [effects[index]], binding, context));
        }

        AddNamedInvocationOperation(method, context, "authorizationWarm", SemanticFormula.AuthorizationWarm, "WarmUp",
            ["authorizationTuple.Authority"], ["accessTracker"], "warm-before-code-read", [SemanticEffect.WarmsAccount], allBindings, operations, ref operationOrdinal);
        AddNamedInvocationOperation(method, context, "authorizationHasCode", SemanticFormula.AuthorizationAccountRead, "HasCode",
            ["authorizationTuple.Authority"], ["WorldState"], "code-existence-read", [SemanticEffect.ReadsWorld], allBindings, operations, ref operationOrdinal);
        AddNamedInvocationOperation(method, context, "authorizationDelegationRead", SemanticFormula.AuthorizationAccountRead, "TryGetDelegation",
            ["authorizationTuple.Authority", "spec"], ["hasDelegation"], "delegation-code-read", [SemanticEffect.ReadsWorld, SemanticEffect.PreservesDelegationIdentity], allBindings, operations, ref operationOrdinal);
        AddNamedInvocationOperation(method, context, "authorizationNonceRead", SemanticFormula.AuthorizationAccountRead, "GetNonce",
            ["authorizationTuple.Authority"], ["authNonce"], "nonce-read-after-code-validation", [SemanticEffect.ReadsWorld], allBindings, operations, ref operationOrdinal);
    }

    private static void AddDelegationEffects(
        MethodDeclarationSyntax method,
        SemanticContext context,
        List<SourceBinding> allBindings,
        List<OperationIdentity> operations,
        ref int operationOrdinal,
        List<BranchIdentity> branches,
        ref int branchOrdinal)
    {
        ForEachStatementSyntax authorizationLoop = method.DescendantNodes().OfType<ForEachStatementSyntax>()
            .Where(loop => loop.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() == method)
            .SingleOrDefault() ?? throw new ExtractionException("ProcessDelegations must retain one reachable authorization-list loop.");
        SourceBinding loopBinding = Bind(method.SyntaxTree, method, "authorizationListLoop", authorizationLoop, context);
        allBindings.Add(loopBinding);
        branches.Add(MakeBranch("authorization.authorizationListLoop", branchOrdinal++, method, authorizationLoop, TerminalKind.None,
            [SemanticEffect.ReadsWorld, SemanticEffect.WarmsAccount, SemanticEffect.ChargesStateGas, SemanticEffect.WritesWorld], loopBinding, context));
        IfStatementSyntax[] existenceGuards =
        [
            RequireExactEip8037Guard(method, context),
            .. RequireIfConditions(method,
                [
                    "!logicalAccountExists&&!TGasPolicy.TryConsumeStateGas(refgasAvailable,TGasPolicy.GetNewAccountStateCost())",
                    "!physicalAccountExists",
                ], "ProcessDelegations"),
        ];
        string[] existenceGuardIds = ["eip8037StateCharges", "newAccountStateChargeGuard", "accountMaterializationGuard"];
        SemanticEffect[][] existenceGuardEffects =
        [
            [SemanticEffect.ChargesStateGas, SemanticEffect.PreservesPhysicalExistence, SemanticEffect.PreservesLogicalExistence],
            [SemanticEffect.ChargesStateGas, SemanticEffect.PreservesLogicalExistence],
            [SemanticEffect.WritesWorld, SemanticEffect.PreservesPhysicalExistence],
        ];
        for (int index = 0; index < existenceGuards.Length; index++)
        {
            SourceBinding binding = Bind(method.SyntaxTree, method, "authorization." + existenceGuardIds[index], existenceGuards[index].Condition, context);
            allBindings.Add(binding);
            branches.Add(MakeBranch("authorization." + existenceGuardIds[index], branchOrdinal++, method,
                existenceGuards[index], TerminalKind.None, existenceGuardEffects[index], binding, context));
        }
        AddNamedInvocationOperation(method, context, "delegationStateUsedBefore", SemanticFormula.AuthorizationStateGasDelta, "GetStateGasUsed",
            ["gasAvailable"], ["preAuthorizationStateGasUsed"], "state-gas-baseline", [SemanticEffect.ReadsWorld, SemanticEffect.PreservesGas], allBindings, operations, ref operationOrdinal,
            shape: (syntax, _) => syntax.SpanStart < authorizationLoop.SpanStart);
        AddNamedInvocationOperation(method, context, "delegationAccountLogicalExistence", SemanticFormula.AuthorizationLogicalExistence, "IsDeadAccount",
            ["authority"], ["logicalAccountExists"], "logical-existence-read-before-physical-read", [SemanticEffect.ReadsWorld, SemanticEffect.PreservesLogicalExistence], allBindings, operations, ref operationOrdinal,
            receiverRoot: "WorldState",
            shape: (syntax, _) => IsExactAssignment(syntax, "logicalAccountExists=!WorldState.IsDeadAccount(authority)"),
            expected: "logicalAccountExists=!WorldState.IsDeadAccount(authority)");
        AddNamedInvocationOperation(method, context, "delegationAccountExistsEip8037", SemanticFormula.AuthorizationAccountRead, "AccountExists",
            ["authority", "logicalAccountExists"], ["physicalAccountExists"], "short-circuited-after-logical-read", [SemanticEffect.ReadsWorld, SemanticEffect.PreservesPhysicalExistence], allBindings, operations, ref operationOrdinal,
            receiverRoot: "WorldState",
            shape: (syntax, _) => IsExactEip8037ThenBranch(syntax, context.Model(method.SyntaxTree)) &&
                IsExactAssignment(syntax, "physicalAccountExists=logicalAccountExists||WorldState.AccountExists(authority)"),
            expected: "physicalAccountExists=logicalAccountExists||WorldState.AccountExists(authority)");
        AddNamedInvocationOperation(method, context, "delegationAccountExistsLegacy", SemanticFormula.AuthorizationAccountRead, "AccountExists",
            ["authority"], ["physicalAccountExists"], "physical-existence-read-pre-8037", [SemanticEffect.ReadsWorld, SemanticEffect.PreservesPhysicalExistence], allBindings, operations, ref operationOrdinal,
            receiverRoot: "WorldState",
            shape: (syntax, _) => IsExactEip8037ElseBranch(syntax, context.Model(method.SyntaxTree)) &&
                IsExactAssignment(syntax, "physicalAccountExists=WorldState.AccountExists(authority)"),
            expected: "physicalAccountExists=WorldState.AccountExists(authority)");
        AddNamedInvocationOperation(method, context, "delegationNewAccountCharge", SemanticFormula.AuthorizationNewAccountStateCharge, "TryConsumeStateGas",
            ["gasAvailable", "TGasPolicy.GetNewAccountStateCost"], ["gasAvailable"], "false=>partial-authorization-return", [SemanticEffect.ChargesStateGas], allBindings, operations, ref operationOrdinal,
            shape: static (_, operation) => HasAnyArgumentExpression(operation, "TGasPolicy.GetNewAccountStateCost()"));
        AddNamedInvocationOperation(method, context, "delegationAccountWriteSet", SemanticFormula.AuthorizationAccountWriteCharge, "Add",
            ["writtenAccounts", "authority"], ["writtenAccounts"], "first-authority-write-only", [SemanticEffect.WritesWorld], allBindings, operations, ref operationOrdinal,
            receiverRoot: "writtenAccounts", shape: static (_, operation) => HasArgumentExpression(operation, "item", "authority"));
        AddNamedInvocationOperation(method, context, "delegationAccountWriteCharge", SemanticFormula.AuthorizationAccountWriteCharge, "UpdateGas",
            ["gasAvailable", "accountWriteGas"], ["gasAvailable"], "false=>partial-authorization-return", [SemanticEffect.ChargesExecutionGas], allBindings, operations, ref operationOrdinal);
        AddNamedInvocationOperation(method, context, "delegationPerAuthSet", SemanticFormula.AuthorizationPerAuthStateCharge, "Add",
            ["delegationSetFor", "authority"], ["delegationSetFor"], "first-new-delegation-only", [SemanticEffect.WritesWorld], allBindings, operations, ref operationOrdinal, receiverRoot: "delegationSetFor");
        AddNamedInvocationOperation(method, context, "delegationPerAuthCharge", SemanticFormula.AuthorizationPerAuthStateCharge, "TryConsumeStateGas",
            ["gasAvailable", "TGasPolicy.GetPerAuthBaseStateCost"], ["gasAvailable"], "false=>partial-authorization-return", [SemanticEffect.ChargesStateGas], allBindings, operations, ref operationOrdinal,
            shape: static (_, operation) => HasAnyArgumentExpression(operation, "TGasPolicy.GetPerAuthBaseStateCost()"));
        AddNamedInvocationOperation(method, context, "delegationCreateAccount", SemanticFormula.AuthorizationCreateAccount, "CreateAccount",
            ["authority"], ["WorldState"], "account-created-with-nonce-one", [SemanticEffect.WritesWorld, SemanticEffect.PreservesPhysicalExistence], allBindings, operations, ref operationOrdinal);
        AddNamedInvocationOperation(method, context, "delegationIncrementNonce", SemanticFormula.AuthorizationIncrementNonce, "IncrementNonce",
            ["authority"], ["WorldState"], "existing-account-nonce-increment", [SemanticEffect.WritesWorld], allBindings, operations, ref operationOrdinal);
        AddNamedInvocationOperation(method, context, "delegationSetCode", SemanticFormula.AuthorizationSetDelegation, "SetDelegation",
            ["authTuple.CodeAddress", "authority", "spec"], ["_codeInfoRepository"], "delegation-code-write", [SemanticEffect.WritesWorld, SemanticEffect.PreservesDelegationIdentity], allBindings, operations, ref operationOrdinal);
        AddNamedInvocationOperation(method, context, "delegationStateUsedAfter", SemanticFormula.AuthorizationStateGasDelta, "GetStateGasUsed",
            ["gasAvailable"], ["authorizationStateGasUsed"], "state-gas-delta", [SemanticEffect.ReadsWorld, SemanticEffect.PreservesGas], allBindings, operations, ref operationOrdinal,
            shape: (syntax, _) => syntax.SpanStart > authorizationLoop.Span.End);
        AddNamedInvocationOperation(method, context, "delegationFold", SemanticFormula.AuthorizationFoldTopFrameStateGas, "FoldTopFrameStateGas",
            ["gasAvailable", "executionIntrinsicGasStandard", "authorizationStateGasUsed"], ["gasAvailable", "executionIntrinsicGasStandard"], "top-frame-reservoir-fold", [SemanticEffect.ChargesStateGas, SemanticEffect.PreservesGas], allBindings, operations, ref operationOrdinal);

        PostfixUnaryExpressionSyntax[] refunds = method.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>()
            .Where(expression => expression.IsKind(SyntaxKind.PostIncrementExpression) && Canonical(expression.Operand) == "codeInsertRefunds")
            .ToArray();
        if (refunds.Length != 1) throw new ExtractionException("ProcessDelegations must retain exactly one codeInsertRefunds increment.");
        SourceBinding refundBinding = Bind(method.SyntaxTree, method, "delegationCodeInsertRefund", refunds[0], context);
        allBindings.Add(refundBinding);
        operations.Add(new OperationIdentity("delegationCodeInsertRefund", operationOrdinal++, "authorization", SemanticFormula.AuthorizationCreateAccount,
            refundBinding.CanonicalSyntax, "codeInsertRefunds++", ["codeInsertRefunds", "spec.IsEip8037Enabled"],
            ["codeInsertRefunds", "spec.IsEip8037Enabled"], ["codeInsertRefunds"], "only-existing-account-pre-EIP-8037", [SemanticEffect.PreservesRefundCounter], refundBinding));
    }

    private static void AddDeploymentOperations(
        MethodDeclarationSyntax method,
        SemanticContext context,
        List<SourceBinding> allBindings,
        List<OperationIdentity> operations,
        ref int operationOrdinal)
    {
        AddNamedInvocationOperation(method, context, "createCollisionClassification", SemanticFormula.CreateCollisionClassification,
            "IsCreateCollision", ["contractAddress", "includeStorageCollision", "WorldState"],
            ["collision", "physicalLeafExists", "logicalAccountExists"], "false=>collision", [SemanticEffect.ReadsWorld, SemanticEffect.PreservesPhysicalExistence, SemanticEffect.PreservesLogicalExistence],
            allBindings, operations, ref operationOrdinal, receiverRoot: "WorldState");
        AddNamedInvocationOperation(method, context, "createStorageReset", SemanticFormula.CreateStorageReset,
            "ClearStorage", ["contractAddress", "includeStorageCollision", "collision"], ["WorldState"],
            "only-when-no-storage-collision", [SemanticEffect.WritesWorld, SemanticEffect.PreservesPhysicalExistence],
            allBindings, operations, ref operationOrdinal, receiverRoot: "WorldState",
            shape: (syntax, _) => IsExactStorageCollisionExclusion(syntax, context.Model(method.SyntaxTree)),
            expected: "!includeStorageCollision=>WorldState.ClearStorage(contractAddress)");
    }


    private static void AddNamedInvocationOperation(
        MethodDeclarationSyntax method,
        SemanticContext context,
        string id,
        SemanticFormula formula,
        string targetName,
        string[] reads,
        string[] writes,
        string failure,
        SemanticEffect[] effects,
        List<SourceBinding> allBindings,
        List<OperationIdentity> operations,
        ref int operationOrdinal,
        string? receiverRoot = null,
        Func<InvocationExpressionSyntax, IInvocationOperation, bool>? shape = null,
        string? expected = null)
    {
        InvocationExpressionSyntax[] matches = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() == method)
            .Where(invocation => context.Model(method.SyntaxTree).GetOperation(invocation) is IInvocationOperation operation &&
                operation.TargetMethod.Name == targetName && !HasErrorSymbol(operation.TargetMethod) &&
                (receiverRoot is null || InvocationReceiver(invocation) == receiverRoot) &&
                (shape is null || shape(invocation, operation)))
            .OrderBy(static invocation => invocation.SpanStart)
            .ToArray();
        InvocationExpressionSyntax invocation = matches.Length switch
        {
            1 => matches[0],
            0 => throw new ExtractionException($"Missing exact semantic invocation {targetName} for {id} in {method.Identifier.ValueText}."),
            _ => throw new ExtractionException($"Semantic invocation {targetName} for {id} is ambiguous in {method.Identifier.ValueText}."),
        };
        SourceBinding binding = Bind(method.SyntaxTree, method, id, invocation, context);
        if (binding.TargetSymbolKind != nameof(SymbolKind.Method) || string.IsNullOrWhiteSpace(binding.TargetSymbol) ||
            string.IsNullOrWhiteSpace(binding.TargetAssembly) || string.IsNullOrWhiteSpace(binding.Receiver) ||
            binding.ArgumentParameters.Length != binding.ArgumentRefKinds.Length || binding.ArgumentParameters.Length == 0)
        {
            throw new ExtractionException($"Semantic invocation {id} did not retain an exact owner, overload, receiver, and argument identity.");
        }
        allBindings.Add(binding);
        operations.Add(new OperationIdentity(id, operationOrdinal++, "authorization", formula, binding.CanonicalSyntax,
            expected ?? targetName, reads, reads, writes, failure, effects, binding));
    }

    private static IfStatementSyntax[] RequireIfConditions(MethodDeclarationSyntax method, string[] expected, string name)
    {
        IfStatementSyntax[] conditions = method.DescendantNodes().OfType<IfStatementSyntax>().OrderBy(static condition => condition.SpanStart).ToArray();
        List<IfStatementSyntax> selected = [];
        foreach (string canonical in expected)
        {
            IfStatementSyntax[] matches = conditions.Where(condition => Canonical(condition.Condition) == canonical).ToArray();
            if (matches.Length != 1) throw new ExtractionException($"{name} is missing one exact admitted guard: {canonical}.");
            selected.Add(matches[0]);
        }

        if (!selected.Select(static condition => condition.SpanStart).SequenceEqual(selected.Select(static condition => condition.SpanStart).OrderBy(static span => span)))
            throw new ExtractionException($"{name} admitted guards are not in source order.");
        return selected.ToArray();
    }

    private static IfStatementSyntax RequireExactEip8037Guard(MethodDeclarationSyntax method, SemanticContext context)
    {
        SemanticModel model = context.Model(method.SyntaxTree);
        IfStatementSyntax[] matches = method.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(statement => statement.Else is not null && IsExactEip8037Condition(statement.Condition, model))
            .OrderBy(static statement => statement.SpanStart)
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ExtractionException("ProcessDelegations is missing the exact EIP-8037 logical/physical existence guard."),
            _ => throw new ExtractionException("ProcessDelegations has ambiguous EIP-8037 logical/physical existence guards."),
        };
    }

    private static IfStatementSyntax[] RequireReachableIfConditions(
        MethodDeclarationSyntax method,
        SemanticContext context,
        IEnumerable<IfStatementSyntax> conditions,
        int expectedCount,
        string name)
    {
        IfStatementSyntax[] scoped = conditions
            .Where(condition => condition.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() == method)
            .OrderBy(static condition => condition.SpanStart)
            .ToArray();
        IfStatementSyntax[] unreachable = scoped.Where(condition =>
        {
            ControlFlowGraph graph = context.Graph(method)
                ?? throw new ExtractionException($"{name} has no Roslyn control-flow graph.");
            return graph.Blocks.Where(static block => block.IsReachable)
                .All(block => !block.Operations.Any(operation => OperationCovers(operation, condition.Condition) ||
                        OperationOverlaps(operation, condition.Condition)) &&
                    (block.BranchValue is null || (!OperationCovers(block.BranchValue, condition.Condition) &&
                        !OperationOverlaps(block.BranchValue, condition.Condition))));
        }).ToArray();
        if (unreachable.Length != 0)
            throw new ExtractionException($"{name} contains an unreachable source branch that cannot be admitted.");
        if (scoped.Length != expectedCount)
            throw new ExtractionException($"{name} branch shape changed: expected {expectedCount} reachable source branches, found {scoped.Length}.");
        return scoped;
    }

    private static void AddPreparationBranches(MethodDeclarationSyntax method, SemanticContext context, List<SourceBinding> allBindings, List<BranchIdentity> branches, ref int branchOrdinal)
    {
        InvocationExpressionSyntax evmCallBoundary = FindFirstInvocation(method, context, "ExecuteEvmCall");
        IfStatementSyntax[] conditions = method.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(condition => condition.SpanStart < evmCallBoundary.SpanStart)
            .OrderBy(static condition => condition.SpanStart)
            .ToArray();
        string[] ids = ["createMetrics", "authorizationGate", "authorizationSnapshot", "authorizationOog", "authorizationOogDeferral", "environmentResult", "deadRecipient", "preparationOogRollback", "preparationSnapshotRestore"];
        TerminalKind[] terminals = [TerminalKind.None, TerminalKind.None, TerminalKind.None, TerminalKind.PreparationOutOfGas, TerminalKind.PreparationOutOfGas, TerminalKind.PreparationReturn, TerminalKind.None, TerminalKind.PreparationOutOfGas, TerminalKind.None];
        SemanticEffect[][] effects =
        [
            [SemanticEffect.RecordsMetrics],
            [SemanticEffect.PreservesGas, SemanticEffect.PreservesCurrentFacts],
            [SemanticEffect.PreservesGas, SemanticEffect.PreservesCurrentFacts],
            [SemanticEffect.PreservesGas, SemanticEffect.PreservesCurrentFacts],
            [SemanticEffect.PreservesGas, SemanticEffect.PreservesCurrentFacts],
            [SemanticEffect.PreservesGas, SemanticEffect.PreservesCurrentFacts],
            [SemanticEffect.PreservesGas, SemanticEffect.PreservesCurrentFacts],
            [SemanticEffect.PreservesGas, SemanticEffect.PreservesCurrentFacts],
            [SemanticEffect.PreservesGas, SemanticEffect.PreservesCurrentFacts],
        ];
        IfStatementSyntax[] selected = RequireReachableIfConditions(method, context, conditions, ids.Length, BoundaryMember);
        for (int index = 0; index < ids.Length; index++)
        {
            SourceBinding binding = Bind(method.SyntaxTree, method, ids[index], selected[index].Condition, context);
            allBindings.Add(binding);
            branches.Add(MakeBranch("preparation." + ids[index], branchOrdinal++, method, selected[index], terminals[index],
                effects[index], binding, context));
        }
    }

    private static void AddEnvironmentBranches(MethodDeclarationSyntax method, SemanticContext context, List<SourceBinding> allBindings, List<BranchIdentity> branches, ref int branchOrdinal)
    {
        IfStatementSyntax[] conditions = method.DescendantNodes().OfType<IfStatementSyntax>().OrderBy(static condition => condition.SpanStart).ToArray();
        string[] ids = ["recipientResolved", "creationCode", "skipRecipientLoad", "preloadedCode", "delegationCode", "delegationTargetCharge", "delegationTargetOog"];
        TerminalKind[] terminals = [TerminalKind.PreparationReturn, TerminalKind.None, TerminalKind.None, TerminalKind.None, TerminalKind.None, TerminalKind.None, TerminalKind.PreparationOutOfGas];
        IfStatementSyntax[] selected = RequireReachableIfConditions(method, context, conditions, ids.Length, "BuildExecutionEnvironment");
        for (int index = 0; index < ids.Length; index++)
        {
            SourceBinding binding = Bind(method.SyntaxTree, method, ids[index], selected[index].Condition, context);
            allBindings.Add(binding);
            branches.Add(MakeBranch("environment." + ids[index], branchOrdinal++, method, selected[index], terminals[index],
                [SemanticEffect.PreservesCodeIdentity, SemanticEffect.WarmsAccount], binding, context));
        }
    }

    private static void AddCallBranches(MethodDeclarationSyntax method, SemanticContext context, List<SourceBinding> allBindings, List<BranchIdentity> branches, ref int branchOrdinal)
    {
        InvocationExpressionSyntax vmBoundary = FindVmBoundaryAnchor(method, context);
        IfStatementSyntax[] conditions = method.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(condition => condition.SpanStart < vmBoundary.SpanStart)
            .OrderBy(static condition => condition.SpanStart)
            .ToArray();
        string[] ids = ["topFrameOog", "createPath", "createStatePath", "createStateOog", "createCollision", "collisionTrace", "nullCode", "refundValidation"];
        TerminalKind[] terminals = [TerminalKind.PreparationOutOfGas, TerminalKind.None, TerminalKind.None, TerminalKind.PreparationOutOfGas, TerminalKind.Collision, TerminalKind.None, TerminalKind.NullCode, TerminalKind.None];
        SemanticEffect[][] effects =
        [
            [SemanticEffect.PreservesPhysicalExistence, SemanticEffect.PreservesLogicalExistence, SemanticEffect.PreservesStatus],
            [SemanticEffect.PreservesPhysicalExistence, SemanticEffect.PreservesLogicalExistence],
            [SemanticEffect.PreservesPhysicalExistence, SemanticEffect.PreservesLogicalExistence],
            [SemanticEffect.PreservesPhysicalExistence, SemanticEffect.PreservesLogicalExistence, SemanticEffect.PreservesStatus],
            [SemanticEffect.PreservesPhysicalExistence, SemanticEffect.PreservesLogicalExistence, SemanticEffect.PreservesStatus],
            [SemanticEffect.PreservesStatus, SemanticEffect.PreservesTraceObservations],
            [SemanticEffect.PreservesPhysicalExistence, SemanticEffect.PreservesLogicalExistence, SemanticEffect.PreservesStatus],
            [SemanticEffect.PreservesStatus, SemanticEffect.PreservesTraceObservations],
        ];
        IfStatementSyntax[] selected = RequireReachableIfConditions(method, context, conditions, ids.Length, "ExecuteEvmCall");
        for (int index = 0; index < ids.Length; index++)
        {
            SourceBinding binding = Bind(method.SyntaxTree, method, ids[index], selected[index].Condition, context);
            allBindings.Add(binding);
            branches.Add(MakeBranch("call." + ids[index], branchOrdinal++, method, selected[index], terminals[index],
                effects[index], binding, context));
        }
    }

    private static BranchIdentity MakeBranch(
        string id,
        int ordinal,
        MethodDeclarationSyntax method,
        IfStatementSyntax statement,
        TerminalKind terminal,
        SemanticEffect[] effects,
        SourceBinding binding,
        SemanticContext context)
    {
        BasicBlock block = FindBlock(context, method, statement.Condition);
        int[] successors = Successors(block).Select(static successor => successor.Ordinal).OrderBy(static value => value).ToArray();
        int[] exits = ReachableExits(block).OrderBy(static value => value).ToArray();
        return new BranchIdentity(id, ordinal, method.Identifier.ValueText, binding.CanonicalSyntax, terminal.ToString(), terminal, effects.Select(static effect => effect.ToString()).ToArray(), binding, successors.Select(static value => value.ToString()).ToArray(), exits);
    }

    private static BranchIdentity MakeBranch(
        string id,
        int ordinal,
        MethodDeclarationSyntax method,
        ForEachStatementSyntax statement,
        TerminalKind terminal,
        SemanticEffect[] effects,
        SourceBinding binding,
        SemanticContext context)
    {
        BasicBlock block = FindBlock(context, method, statement);
        int[] successors = Successors(block).Select(static successor => successor.Ordinal).OrderBy(static value => value).ToArray();
        int[] exits = ReachableExits(block).OrderBy(static value => value).ToArray();
        return new BranchIdentity(id, ordinal, method.Identifier.ValueText, binding.CanonicalSyntax, terminal.ToString(), terminal,
            effects.Select(static effect => effect.ToString()).ToArray(), binding,
            successors.Select(static value => value.ToString()).ToArray(), exits);
    }

    private static SourceBinding Bind(SyntaxTree tree, MethodDeclarationSyntax method, string member, SyntaxNode node, SemanticContext context)
    {
        SemanticModel model = context.Model(tree);
        IOperation? operation = model.GetOperation(node);
        if (operation is null || operation is IInvalidOperation)
            throw new ExtractionException($"Source node {member} has no valid Roslyn IOperation: {Canonical(node)}.");
        ValidateOperationTree(model, operation, member);
        SymbolInfo symbolInfo = model.GetSymbolInfo(node);
        if (symbolInfo.CandidateReason != CandidateReason.None || symbolInfo.CandidateSymbols.Length != 0)
            throw new ExtractionException($"Source node {member} resolved through a candidate or ambiguous symbol: {Canonical(node)}.");

        ISymbol? symbol = operation switch
        {
            IInvocationOperation invocation => invocation.TargetMethod,
            IPropertyReferenceOperation property => property.Property,
            IFieldReferenceOperation field => field.Field,
            IMethodReferenceOperation methodReference => methodReference.Method,
            ISimpleAssignmentOperation assignment => AssignmentSymbol(assignment.Target),
            IIncrementOrDecrementOperation increment => AssignmentSymbol(increment.Target),
            _ => symbolInfo.Symbol,
        };
        bool error = symbol is not null && HasErrorSymbol(symbol);
        if (error) throw new ExtractionException($"Source node {member} resolved through an error symbol: {Canonical(node)}.");
        if ((operation is IInvocationOperation or IPropertyReferenceOperation or IFieldReferenceOperation or IMethodReferenceOperation) && symbol is null)
            throw new ExtractionException($"Source node {member} has no resolved target symbol: {Canonical(node)}.");

        string receiver = operation switch
        {
            IInvocationOperation invocation when invocation.Instance?.Type is ITypeSymbol type =>
                InvocationReceiver((InvocationExpressionSyntax)node) + " :: " + type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            IPropertyReferenceOperation property when property.Instance?.Type is ITypeSymbol type => type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            _ => node is InvocationExpressionSyntax invocation ? InvocationReceiver(invocation) : string.Empty,
        };
        BasicBlock block = FindBlock(context, method, node);
        FileLinePositionSpan span = node.GetLocation().GetLineSpan();
        string[] argumentParameters = operation is IInvocationOperation invocationOperation
            ? invocationOperation.Arguments.Select(static argument => argument.Parameter?.Name ?? "missing").ToArray()
            : [];
        string[] argumentRefKinds = operation is IInvocationOperation invocationWithRefKinds
            ? invocationWithRefKinds.Arguments.Select(static argument => argument.Parameter?.RefKind.ToString() ?? "missing").ToArray()
            : [];
        return new SourceBinding(
            Normalize(tree.FilePath),
            OwnerPath(method),
            member,
            SignatureOf(method),
            node.Kind().ToString(),
            operation.Kind.ToString(),
            Canonical(node),
            TokenFingerprint(node),
            Sha256(Encoding.UTF8.GetBytes(Canonical(node))),
            SignatureOf(method),
            receiver,
            symbol?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? string.Empty,
            symbol?.Kind.ToString() ?? operation.Kind.ToString(),
            symbol?.ContainingAssembly.Identity.Name ?? string.Empty,
            argumentParameters,
            argumentRefKinds,
            symbolInfo.CandidateReason.ToString(),
            error,
            symbolInfo.CandidateSymbols.Length != 0,
            StatementOrdinal(node),
            ControlFlowPath(node),
            block.Ordinal,
            block.IsReachable,
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1,
            span.EndLinePosition.Character + 1);
    }

    private static void ValidateOperationTree(SemanticModel model, IOperation operation, string member)
    {
        switch (operation)
        {
            case IInvocationOperation invocation when HasErrorSymbol(invocation.TargetMethod):
                throw new ExtractionException($"Source node {member} has an error invocation target: {Canonical(operation.Syntax)}.");
            case IObjectCreationOperation creation when creation.Type is null || HasErrorSymbol(creation.Type):
                throw new ExtractionException($"Source node {member} has an error object-construction target: {Canonical(operation.Syntax)}.");
            case IPropertyReferenceOperation property when HasErrorSymbol(property.Property):
                throw new ExtractionException($"Source node {member} has an error property target: {Canonical(operation.Syntax)}.");
            case IFieldReferenceOperation field when HasErrorSymbol(field.Field):
                throw new ExtractionException($"Source node {member} has an error field target: {Canonical(operation.Syntax)}.");
            case IMethodReferenceOperation methodReference when HasErrorSymbol(methodReference.Method):
                throw new ExtractionException($"Source node {member} has an error method target: {Canonical(operation.Syntax)}.");
        }

        if (operation is IInvocationOperation or IObjectCreationOperation or IPropertyReferenceOperation or IFieldReferenceOperation or IMethodReferenceOperation)
        {
            SymbolInfo info = model.GetSymbolInfo(operation.Syntax);
            if (info.CandidateReason != CandidateReason.None || info.CandidateSymbols.Length != 0)
                throw new ExtractionException($"Source node {member} contains a candidate or ambiguous semantic target: {Canonical(operation.Syntax)}.");
        }

        foreach (IOperation child in operation.ChildOperations)
        {
            if (child is not IInvalidOperation) ValidateOperationTree(model, child, member);
            else throw new ExtractionException($"Source node {member} contains an invalid Roslyn operation: {Canonical(operation.Syntax)}.");
        }
    }

    private static SourceBinding BindDeclaration(SourceFile source, TypeDeclarationSyntax owner, string member, SyntaxNode node, SemanticContext context)
    {
        SemanticModel model = context.Model(source.Tree);
        ISymbol? symbol = node switch
        {
            VariableDeclaratorSyntax variable => model.GetDeclaredSymbol(variable),
            PropertyDeclarationSyntax property => model.GetDeclaredSymbol(property),
            MethodDeclarationSyntax method => model.GetDeclaredSymbol(method),
            _ => model.GetDeclaredSymbol(owner),
        };
        if (symbol is null || HasErrorSymbol(symbol)) throw new ExtractionException($"Declaration {member} has no exact semantic symbol.");
        FileLinePositionSpan span = node.GetLocation().GetLineSpan();
        string signature = node is MethodDeclarationSyntax methodDeclaration ? SignatureOf(methodDeclaration) : SignatureOf(owner);
        return new SourceBinding(
            source.RelativePath,
            TypeOwner(owner),
            member,
            signature,
            node.Kind().ToString(),
            "Declaration",
            Canonical(node),
            TokenFingerprint(node),
            Sha256(Encoding.UTF8.GetBytes(Canonical(node))),
            signature,
            string.Empty,
            symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            symbol.Kind.ToString(),
            symbol.ContainingAssembly.Identity.Name,
            [],
            [],
            CandidateReason.None.ToString(),
            false,
            false,
            0,
            "declaration",
            -1,
            true,
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1,
            span.EndLinePosition.Character + 1);
    }

    private static ISymbol? AssignmentSymbol(IOperation target) => target switch
    {
        ILocalReferenceOperation local => local.Local,
        IParameterReferenceOperation parameter => parameter.Parameter,
        IFieldReferenceOperation field => field.Field,
        IPropertyReferenceOperation property => property.Property,
        _ => null,
    };

    private static BasicBlock FindBlock(SemanticContext context, MethodDeclarationSyntax method, SyntaxNode node)
    {
        ControlFlowGraph graph = context.Graph(method)
            ?? throw new ExtractionException($"Method {method.Identifier.ValueText} has no control-flow graph.");
        BasicBlock? block = graph.Blocks
            .Where(candidate => candidate.IsReachable &&
                (candidate.Operations.Any(operation => OperationCovers(operation, node)) || candidate.BranchValue is not null && OperationCovers(candidate.BranchValue, node)))
            .OrderBy(candidate => candidate.Ordinal)
            .FirstOrDefault() ?? graph.Blocks
            .Where(candidate => candidate.IsReachable &&
                (candidate.Operations.Any(operation => OperationOverlaps(operation, node)) || candidate.BranchValue is not null && OperationOverlaps(candidate.BranchValue, node)))
            .OrderBy(candidate => candidate.Ordinal)
            .FirstOrDefault();
        return block ?? throw new ExtractionException($"Source node {Canonical(node)} is not on a reachable control-flow block.");
    }

    private static bool OperationCovers(IOperation operation, SyntaxNode node) =>
        ReferenceEquals(operation.Syntax.SyntaxTree, node.SyntaxTree) && node.FullSpan.Start >= operation.Syntax.FullSpan.Start && node.FullSpan.End <= operation.Syntax.FullSpan.End;

    private static bool OperationOverlaps(IOperation operation, SyntaxNode node) =>
        ReferenceEquals(operation.Syntax.SyntaxTree, node.SyntaxTree) && operation.Syntax.FullSpan.Start < node.FullSpan.End &&
        node.FullSpan.Start < operation.Syntax.FullSpan.End;

    private static IEnumerable<BasicBlock> Successors(BasicBlock block)
    {
        foreach (ControlFlowBranch branch in Branches(block))
        {
            if (branch.Destination is BasicBlock destination) yield return destination;
        }
    }

    private static IEnumerable<int> ReachableExits(BasicBlock start)
    {
        Queue<BasicBlock> pending = new();
        HashSet<int> visited = [];
        pending.Enqueue(start);
        while (pending.TryDequeue(out BasicBlock? block))
        {
            if (!visited.Add(block.Ordinal) || !block.IsReachable) continue;
            if (block.Kind.ToString() == "Exit")
            {
                yield return block.Ordinal;
                continue;
            }

            foreach (BasicBlock successor in Successors(block)) pending.Enqueue(successor);
        }
    }

    private sealed class SemanticContext(Dictionary<SyntaxTree, SemanticModel> models, Dictionary<string, SourceFile> sources)
    {
        private readonly Dictionary<SyntaxTree, SemanticModel> _models = models;
        private readonly Dictionary<string, SourceFile> _sources = sources;
        private readonly Dictionary<MethodDeclarationSyntax, ControlFlowGraph?> _graphs = [];

        internal SemanticModel Model(SyntaxTree tree) => _models.TryGetValue(tree, out SemanticModel? model)
            ? model
            : throw new ExtractionException($"No semantic model exists for {Normalize(tree.FilePath)}.");

        internal SourceFile Source(string path) => _sources.TryGetValue(path, out SourceFile? source)
            ? source
            : throw new ExtractionException($"No admitted source exists for {path}.");

        internal ControlFlowGraph? Graph(MethodDeclarationSyntax method)
        {
            if (_graphs.TryGetValue(method, out ControlFlowGraph? graph)) return graph;
            try
            {
                IOperation? body = Model(method.SyntaxTree).GetOperation(method);
                graph = body is IMethodBodyOperation methodBody ? ControlFlowGraph.Create(methodBody) : null;
            }
            catch (ArgumentException)
            {
                graph = null;
            }
            catch (InvalidOperationException)
            {
                graph = null;
            }

            _graphs.Add(method, graph);
            return graph;
        }
    }

    private static GasFieldIdentity[] AddGasFields(SourceFile[] sources, SemanticContext context, List<SourceBinding> bindings)
    {
        SourceFile gasSource = FindSource(sources, EthereumGasPolicyPath);
        string[] names = ["Value", "StateReservoir", "StateGasUsed", "StateGasSpill", "StateGasSpillRefunded"];
        List<GasFieldIdentity> fields = [];
        TypeDeclarationSyntax type = gasSource.Root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(declaration => declaration.Identifier.ValueText == "EthereumGasPolicy")
            ?? throw new ExtractionException("EthereumGasPolicy declaration is missing.");
        foreach (string name in names)
        {
            VariableDeclaratorSyntax[] declarations = type.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Where(variable => variable.Identifier.ValueText == name)
                .ToArray();
            if (declarations.Length != 1) throw new ExtractionException($"EthereumGasPolicy.{name} field declaration is missing or ambiguous.");
            SourceBinding binding = BindDeclaration(gasSource, type, "gasField." + name, declarations[0], context);
            bindings.Add(binding);
            IFieldSymbol field = context.Model(gasSource.Tree).GetDeclaredSymbol(declarations[0]) as IFieldSymbol
                ?? throw new ExtractionException($"EthereumGasPolicy.{name} has no field symbol.");
            if (field.Type.SpecialType is not (SpecialType.System_UInt64 or SpecialType.System_Int64))
                throw new ExtractionException($"EthereumGasPolicy.{name} is not a fixed-width UInt64/Int64 field.");
            string sourceType = field.Type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
            int width = field.Type.SpecialType is SpecialType.System_UInt64 or SpecialType.System_Int64 ? 64 : 0;
            string representation = field.Type.SpecialType == SpecialType.System_UInt64 ? "UInt64" : "Int64";
            string[] fixedWidthOperations = field.Type.SpecialType == SpecialType.System_UInt64
                ? ["range:0..18446744073709551615", "no-wrap:checked-subtraction", "adapter:ulong-to-Int"]
                : ["range:-9223372036854775808..9223372036854775807", "no-wrap:checked-addition", "adapter:long-two-complement"];
            fields.Add(new GasFieldIdentity(name, field.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), sourceType, width,
                representation, fixedWidthOperations, binding));
        }

        return fields.ToArray();
    }

    private static SnapshotIdentity[] AddSnapshots(
        MethodDeclarationSyntax execute,
        MethodDeclarationSyntax call,
        SemanticContext context,
        List<SourceBinding> bindings)
    {
        SyntaxNode preparation = FindAssignment(execute, "preExecutionSnapshot", "WorldState.TakeSnapshot()");
        SyntaxNode topLevel = FindInvocation(call, context, "TakeSnapshot");
        SourceBinding preparationBinding = Bind(execute.SyntaxTree, execute, "snapshot.preparation", preparation, context);
        SourceBinding topLevelBinding = Bind(call.SyntaxTree, call, "snapshot.topLevel", topLevel, context);
        SemanticModel executeModel = context.Model(execute.SyntaxTree);
        IfStatementSyntax[] authorizationPresenceGuards = execute.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(statement => Canonical(statement.Condition) == "spec.IsEip7702Enabled&&tx.HasAuthorizationList")
            .Where(statement => IsExactEip7702AuthorizationCondition(statement.Condition, executeModel))
            .Where(statement => statement.Else is null && statement.Statement.DescendantNodesAndSelf().Any(node => ReferenceEquals(node, preparation)))
            .ToArray();
        if (authorizationPresenceGuards.Length != 1)
            throw new ExtractionException("The pre-execution snapshot must be enclosed by the exact EIP-7702 authorization-list condition.");
        IfStatementSyntax[] preparationPresenceGuards = preparation.Ancestors().OfType<IfStatementSyntax>()
            .Where(statement => Canonical(statement.Condition) == "spec.IsEip8037Enabled")
            .Where(statement => IsExactEip8037Condition(statement.Condition, executeModel))
            .Where(statement => statement.Else is null && statement.Statement.DescendantNodesAndSelf().Any(node => ReferenceEquals(node, preparation)))
            .ToArray();
        if (preparationPresenceGuards.Length != 1)
            throw new ExtractionException("The pre-execution snapshot must be guarded by the exact EIP-8037 condition.");
        SourceBinding authorizationPresenceBinding = Bind(execute.SyntaxTree, execute, "snapshot.authorizationPresence", authorizationPresenceGuards[0].Condition, context);
        SourceBinding preparationPresenceBinding = Bind(execute.SyntaxTree, execute, "snapshot.preparationPresence", preparationPresenceGuards[0].Condition, context);
        bindings.Add(preparationBinding);
        bindings.Add(topLevelBinding);
        bindings.Add(authorizationPresenceBinding);
        bindings.Add(preparationPresenceBinding);

        SourceFile snapshotSource = context.Source(SnapshotPath);
        TypeDeclarationSyntax snapshotType = snapshotSource.Root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(declaration => declaration.Identifier.ValueText == "Snapshot")
            ?? throw new ExtractionException("Snapshot declaration is missing.");
        string[] fields = ["StorageSnapshot", "StateSnapshot", "BlockAccessListSnapshot"];
        if (fields.Any(field => snapshotType.DescendantNodes().OfType<PropertyDeclarationSyntax>().Count(property => property.Identifier.ValueText == field) != 1))
            throw new ExtractionException("Snapshot field contract changed.");
        VariableDeclaratorSyntax[] emptyPosition = snapshotType.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(variable => variable.Identifier.ValueText == "EmptyPosition")
            .ToArray();
        if (emptyPosition.Length != 1) throw new ExtractionException("Snapshot.EmptyPosition must remain the source-defined -1 sentinel.");
        EqualsValueClauseSyntax emptyPositionInitializer = emptyPosition[0].Initializer
            ?? throw new ExtractionException("Snapshot.EmptyPosition must remain the source-defined -1 sentinel.");
        if (Canonical(emptyPositionInitializer.Value) != "-1")
            throw new ExtractionException("Snapshot.EmptyPosition must remain the source-defined -1 sentinel.");
        SourceBinding emptyPositionBinding = BindDeclaration(snapshotSource, snapshotType, "snapshot.emptyPosition", emptyPosition[0], context);
        bindings.Add(emptyPositionBinding);
        return
        [
            new SnapshotIdentity("preparation", "ExecuteEvmTransaction", "pre-execution", fields, preparationBinding),
            new SnapshotIdentity("topLevel", "ExecuteEvmCall", "top-level-frame", fields, topLevelBinding),
        ];
    }

    private static SourceBinding AddTransactionAuthorizationTypeBinding(
        SourceFile[] sources,
        SemanticContext context,
        List<SourceBinding> bindings)
    {
        SourceFile source = FindSource(sources, TransactionPath);
        ClassDeclarationSyntax transaction = source.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(type => type.Identifier.ValueText == "Transaction")
            .SingleOrDefault() ?? throw new ExtractionException("Transaction declaration is missing or ambiguous.");
        PropertyDeclarationSyntax property = transaction.Members.OfType<PropertyDeclarationSyntax>()
            .Where(candidate => candidate.Identifier.ValueText == "HasAuthorizationList")
            .SingleOrDefault() ?? throw new ExtractionException("Transaction.HasAuthorizationList declaration is missing or ambiguous.");
        SemanticModel model = context.Model(source.Tree);
        BinaryExpressionSyntax[] setCodeChecks = property.DescendantNodes().OfType<BinaryExpressionSyntax>()
            .Where(expression => Canonical(expression) == "Type==TxType.SetCode")
            .ToArray();
        if (setCodeChecks.Length != 1 || !IsExactSetCodeComparison(setCodeChecks[0], model))
            throw new ExtractionException("Transaction.HasAuthorizationList must retain the exact TxType.SetCode semantic guard.");
        IsPatternExpressionSyntax[] nonNullChecks = property.DescendantNodes().OfType<IsPatternExpressionSyntax>()
            .Where(expression => Canonical(expression) == "AuthorizationListisnotnull")
            .ToArray();
        if (nonNullChecks.Length != 1 || !IsExactAuthorizationListNonNull(nonNullChecks[0], model))
            throw new ExtractionException("Transaction.HasAuthorizationList must retain the exact AuthorizationList non-null guard.");
        BinaryExpressionSyntax[] lengthChecks = property.DescendantNodes().OfType<BinaryExpressionSyntax>()
            .Where(expression => Canonical(expression) == "AuthorizationList.Length>0")
            .ToArray();
        if (lengthChecks.Length != 1 || !IsExactAuthorizationListLength(lengthChecks[0], model))
            throw new ExtractionException("Transaction.HasAuthorizationList must retain the exact AuthorizationList.Length > 0 guard.");
        SourceBinding binding = BindDeclaration(source, transaction, "transaction.hasAuthorizationListSetCode", property, context);
        bindings.Add(binding);
        return binding;
    }

    private static SourceBinding AddRecipientDerivationBinding(
        SourceFile[] sources,
        SemanticContext context,
        List<SourceBinding> bindings)
    {
        SourceFile source = FindSource(sources, EvmTransactionExtensionsPath);
        ClassDeclarationSyntax type = source.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(candidate => candidate.Identifier.ValueText == "TransactionExtensions")
            .SingleOrDefault() ?? throw new ExtractionException("EVM TransactionExtensions declaration is missing or ambiguous.");
        MethodDeclarationSyntax method = type.Members.OfType<MethodDeclarationSyntax>()
            .Where(candidate => candidate.Identifier.ValueText == "GetRecipient")
            .SingleOrDefault() ?? throw new ExtractionException("TransactionExtensions.GetRecipient declaration is missing or ambiguous.");
        SemanticModel model = context.Model(source.Tree);
        IMethodSymbol symbol = model.GetDeclaredSymbol(method)
            ?? throw new ExtractionException("TransactionExtensions.GetRecipient has no exact semantic symbol.");
        if (!symbol.IsStatic || symbol.ContainingAssembly.Identity.Name != "Nethermind.Evm" ||
            symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != "Nethermind.Evm.TransactionExtensions" ||
            symbol.ReturnType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != "Nethermind.Core.Address" ||
            symbol.Parameters.Length != 2 || !symbol.Parameters.Select(static parameter => parameter.Name)
                .SequenceEqual(["tx", "nonce"], StringComparer.Ordinal) ||
            symbol.Parameters[0].RefKind != RefKind.None || symbol.Parameters[1].RefKind != RefKind.In ||
            method.ParameterList.Parameters.Count != 2 ||
            !method.ParameterList.Parameters[0].Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.ThisKeyword)))
            throw new ExtractionException("TransactionExtensions.GetRecipient signature changed.");

        ExpressionSyntax body = method.ExpressionBody?.Expression
            ?? throw new ExtractionException("TransactionExtensions.GetRecipient must retain its expression-bodied implementation.");
        const string expectedBody = "tx.To??(tx.IsSystem()?tx.SenderAddress!:ContractAddress.From(tx.SenderAddress,nonce>0?nonce-1:nonce))";
        if (Canonical(body) != expectedBody)
            throw new ExtractionException("TransactionExtensions.GetRecipient body changed.");
        IOperation bodyOperation = model.GetOperation(body)
            ?? throw new ExtractionException("TransactionExtensions.GetRecipient body has no exact operation tree.");
        ValidateOperationTree(model, bodyOperation, "transaction.getRecipient");
        IInvocationOperation[] invocations = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(invocation => model.GetOperation(invocation) as IInvocationOperation)
            .Where(static operation => operation is not null)
            .Cast<IInvocationOperation>()
            .ToArray();
        if (invocations.Length != 2)
            throw new ExtractionException("TransactionExtensions.GetRecipient invocation structure changed.");
        IInvocationOperation isSystem = invocations.SingleOrDefault(operation => operation.TargetMethod.Name == "IsSystem")
            ?? throw new ExtractionException("TransactionExtensions.GetRecipient must call the exact IsSystem helper.");
        if (isSystem.TargetMethod.ContainingAssembly.Identity.Name != "Nethermind.Core" ||
            isSystem.TargetMethod.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != "Nethermind.Core.TransactionExtensions.extension(Nethermind.Core.Transaction)" ||
            isSystem.TargetMethod.IsStatic || isSystem.Arguments.Length != 0)
            throw new ExtractionException("TransactionExtensions.GetRecipient IsSystem target changed.");
        IInvocationOperation contractAddress = invocations.SingleOrDefault(operation => operation.TargetMethod.Name == "From")
            ?? throw new ExtractionException("TransactionExtensions.GetRecipient must call ContractAddress.From.");
        if (contractAddress.TargetMethod.ContainingAssembly.Identity.Name != "Nethermind.Evm" ||
            contractAddress.TargetMethod.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != "Nethermind.Evm.ContractAddress" ||
            !contractAddress.TargetMethod.IsStatic ||
            !contractAddress.TargetMethod.Parameters.Select(static parameter => parameter.Name)
                .SequenceEqual(["deployingAddress", "nonce"], StringComparer.Ordinal) ||
            !contractAddress.TargetMethod.Parameters.Select(static parameter => parameter.RefKind)
                .SequenceEqual([RefKind.None, RefKind.In]) ||
            !contractAddress.Arguments.Select(static argument => argument.Parameter?.Name ?? "missing")
                .SequenceEqual(["deployingAddress", "nonce"], StringComparer.Ordinal) ||
            !contractAddress.Arguments.Select(static argument => argument.Parameter?.RefKind ?? RefKind.None)
                .SequenceEqual([RefKind.None, RefKind.In]))
            throw new ExtractionException("TransactionExtensions.GetRecipient ContractAddress.From target changed.");

        SourceBinding binding = BindDeclaration(source, type, "transaction.getRecipient", method, context);
        bindings.Add(binding);
        return binding;
    }

    private static EnvironmentIdentity AddEnvironmentIdentity(MethodDeclarationSyntax environment, SemanticContext context, List<SourceBinding> bindings)
    {
        InvocationExpressionSyntax rent = FindInvocation(environment, context, "Rent");
        SourceBinding binding = Bind(environment.SyntaxTree, environment, "environment.rent", rent, context);
        bindings.Add(binding);
        SourceFile source = context.Source(ExecutionEnvironmentPath);
        ClassDeclarationSyntax type = source.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(declaration => declaration.Identifier.ValueText == "ExecutionEnvironment")
            ?? throw new ExtractionException("ExecutionEnvironment declaration is missing.");
        string[] fields = ["CodeInfo", "ExecutingAccount", "Caller", "CodeSource", "CallDepth", "Value", "InputData"];
        foreach (string field in fields)
        {
            int count = type.DescendantNodes().OfType<PropertyDeclarationSyntax>().Count(property => property.Identifier.ValueText == field) +
                type.DescendantNodes().OfType<FieldDeclarationSyntax>().SelectMany(static declaration => declaration.Declaration.Variables).Count(variable => variable.Identifier.ValueText == field);
            if (count != 1) throw new ExtractionException($"ExecutionEnvironment.{field} identity changed.");
        }

        return new EnvironmentIdentity(fields, ["CodeInfo", "CodeSource"], ["InputData", "Value"], binding);
    }

    private static ILocalReferenceOperation? UnwrapLocalReference(IOperation operation)
    {
        while (operation is IConversionOperation conversion) operation = conversion.Operand;
        if (operation is IDeclarationExpressionOperation declaration) return UnwrapLocalReference(declaration.Expression);
        return operation as ILocalReferenceOperation;
    }

    private static bool ContainsSymbolReference(IOperation operation, ISet<ISymbol> symbols)
    {
        if (operation is ILocalReferenceOperation local && symbols.Contains(local.Local) ||
            operation is IParameterReferenceOperation parameter && symbols.Contains(parameter.Parameter))
            return true;

        foreach (IOperation child in operation.ChildOperations)
        {
            if (ContainsSymbolReference(child, symbols)) return true;
        }

        return false;
    }

    private static IParameterReferenceOperation? UnwrapParameterReference(IOperation operation)
    {
        while (operation is IConversionOperation conversion) operation = conversion.Operand;
        return operation as IParameterReferenceOperation;
    }

    private static string AccessBindingSuffix(IInvocationOperation operation)
    {
        if (!operation.TargetMethod.IsGenericMethod || operation.TargetMethod.TypeArguments.Length != 1)
            throw new ExtractionException("ExecuteEvmCall must retain one exact tracing type argument.");

        return operation.TargetMethod.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) switch
        {
            "Nethermind.Core.OffFlag" => "off",
            "Nethermind.Core.OnFlag" => "on",
            _ => throw new ExtractionException("ExecuteEvmCall tracing type argument changed."),
        };
    }

    private static InvocationExpressionSyntax[] FindExecuteEvmCallAccessCalls(
        MethodDeclarationSyntax execute,
        MethodDeclarationSyntax call,
        SemanticContext context,
        ILocalSymbol tracker,
        out IMethodSymbol callSymbol,
        out IParameterSymbol accessParameter)
    {
        SemanticModel executeModel = context.Model(execute.SyntaxTree);
        IMethodSymbol declaredCallSymbol = context.Model(call.SyntaxTree).GetDeclaredSymbol(call)
            ?? throw new ExtractionException("ExecuteEvmCall has no exact semantic method symbol.");
        callSymbol = declaredCallSymbol;
        IParameterSymbol declaredAccessParameter = declaredCallSymbol.Parameters.SingleOrDefault(parameter => parameter.Name == "accessedItems")
            ?? throw new ExtractionException("ExecuteEvmCall.accessedItems parameter is missing.");
        accessParameter = declaredAccessParameter;
        if (accessParameter.RefKind != RefKind.In ||
            accessParameter.Type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != "Nethermind.Evm.StackAccessTracker")
            throw new ExtractionException("ExecuteEvmCall.accessedItems must be the exact in StackAccessTracker parameter.");

        InvocationExpressionSyntax[] calls = execute.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() == execute)
            .Where(invocation => executeModel.GetOperation(invocation) is IInvocationOperation operation &&
                SymbolEqualityComparer.Default.Equals(operation.TargetMethod.OriginalDefinition, declaredCallSymbol))
            .OrderBy(static invocation => invocation.SpanStart)
            .ToArray();
        if (calls.Length != 2 || calls.Select(invocation => executeModel.GetOperation(invocation) as IInvocationOperation)
                .Any(static operation => operation is null))
            throw new ExtractionException("ExecuteEvmTransaction must pass the exact ExecuteEvmCall overload pair.");

        foreach (InvocationExpressionSyntax invocation in calls)
        {
            IInvocationOperation operation = executeModel.GetOperation(invocation) as IInvocationOperation
                ?? throw new ExtractionException("ExecuteEvmTransaction ExecuteEvmCall call is not an invocation operation.");
            IMethodSymbol target = operation.TargetMethod;
            if (!SymbolEqualityComparer.Default.Equals(target.OriginalDefinition, declaredCallSymbol) ||
                target.ContainingAssembly.Identity.Name != "Nethermind.Evm" ||
                target.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) !=
                    declaredCallSymbol.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ||
                operation.Instance?.Type?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) !=
                    declaredCallSymbol.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ||
                !target.Parameters.Select(static parameter => parameter.Name).SequenceEqual(
                    declaredCallSymbol.Parameters.Select(static parameter => parameter.Name), StringComparer.Ordinal) ||
                !operation.Arguments.Select(static argument => argument.Parameter?.Name ?? "missing").SequenceEqual(
                    declaredCallSymbol.Parameters.Select(static parameter => parameter.Name), StringComparer.Ordinal) ||
                !target.Parameters.Select(static parameter => parameter.RefKind.ToString()).SequenceEqual(
                    declaredCallSymbol.Parameters.Select(static parameter => parameter.RefKind.ToString()), StringComparer.Ordinal) ||
                !operation.Arguments.Select(static argument => argument.Parameter?.RefKind.ToString() ?? "missing").SequenceEqual(
                    declaredCallSymbol.Parameters.Select(static parameter => parameter.RefKind.ToString()), StringComparer.Ordinal))
                throw new ExtractionException("ExecuteEvmTransaction ExecuteEvmCall owner, overload, receiver, argument, or ref-kind identity changed.");

            IArgumentOperation argument = operation.Arguments.Single(argument => argument.Parameter?.Name == declaredAccessParameter.Name);
            ILocalReferenceOperation? local = UnwrapLocalReference(argument.Value);
            if (argument.Parameter?.RefKind != RefKind.In || local is null ||
                !SymbolEqualityComparer.Default.Equals(local.Local, tracker))
                throw new ExtractionException("ExecuteEvmTransaction must pass the fresh accessTracker as in ExecuteEvmCall.accessedItems.");
        }

        return calls;
    }

    private static IMethodSymbol DeclaredMethodSymbol(MethodDeclarationSyntax method, SemanticContext context) =>
        context.Model(method.SyntaxTree).GetDeclaredSymbol(method)
        ?? throw new ExtractionException($"{method.Identifier.ValueText} has no exact semantic method symbol.");

    private static IParameterSymbol RequireParameter(IMethodSymbol method, string name)
    {
        IParameterSymbol? parameter = method.Parameters.SingleOrDefault(candidate => candidate.Name == name);
        return parameter ?? throw new ExtractionException($"{method.Name}.{name} parameter is missing.");
    }

    private static ILocalSymbol RequireLocal(MethodDeclarationSyntax method, SemanticContext context, string name)
    {
        SemanticModel model = context.Model(method.SyntaxTree);
        ILocalSymbol[] locals = method.DescendantNodes()
            .Where(variable => variable switch
            {
                VariableDeclaratorSyntax declaration => declaration.Identifier.ValueText == name,
                SingleVariableDesignationSyntax designation => designation.Identifier.ValueText == name,
                _ => false,
            })
            .Select(variable => model.GetDeclaredSymbol(variable) as ILocalSymbol)
            .Where(static local => local is not null)
            .Cast<ILocalSymbol>()
            .ToArray();
        return locals.Length switch
        {
            1 => locals[0],
            0 => throw new ExtractionException($"{method.Identifier.ValueText}.{name} local is missing."),
            _ => throw new ExtractionException($"{method.Identifier.ValueText}.{name} local is ambiguous."),
        };
    }

    private static IInvocationOperation RequireInvocationOperation(
        MethodDeclarationSyntax method,
        SemanticContext context,
        string targetName)
    {
        InvocationExpressionSyntax syntax = FindInvocation(method, context, targetName);
        return context.Model(method.SyntaxTree).GetOperation(syntax) as IInvocationOperation
            ?? throw new ExtractionException($"{method.Identifier.ValueText}.{targetName} is not an invocation operation.");
    }

    private static IArgumentOperation RequireArgument(IInvocationOperation operation, string parameterName) =>
        operation.Arguments.SingleOrDefault(argument => argument.Parameter?.Name == parameterName)
            ?? throw new ExtractionException($"{operation.TargetMethod.Name} is missing its {parameterName} argument.");

    private static void RequireInvocationShape(
        IInvocationOperation operation,
        IMethodSymbol expectedMethod,
        string[] parameterNames,
        RefKind[] refKinds,
        string description)
    {
        IMethodSymbol target = operation.TargetMethod;
        if (!SymbolEqualityComparer.Default.Equals(target.OriginalDefinition, expectedMethod.OriginalDefinition) ||
            target.ContainingAssembly.Identity.Name != expectedMethod.ContainingAssembly.Identity.Name ||
            !target.Parameters.Select(static parameter => parameter.Name).SequenceEqual(parameterNames, StringComparer.Ordinal) ||
            !target.Parameters.Select(static parameter => parameter.RefKind).SequenceEqual(refKinds) ||
            !operation.Arguments.Select(static argument => argument.Parameter?.Name ?? string.Empty).SequenceEqual(parameterNames, StringComparer.Ordinal) ||
            !operation.Arguments.Select(static argument => argument.Parameter?.RefKind ?? RefKind.None).SequenceEqual(refKinds))
            throw new ExtractionException($"{description} owner, overload, parameter, argument, or ref-kind identity changed.");
    }

    private static void RequireLocalArgument(
        IInvocationOperation operation,
        string parameterName,
        ILocalSymbol expected,
        RefKind refKind,
        string description)
    {
        IArgumentOperation argument = RequireArgument(operation, parameterName);
        ILocalReferenceOperation? local = UnwrapLocalReference(argument.Value);
        if (argument.Parameter?.RefKind != refKind || local is null ||
            !SymbolEqualityComparer.Default.Equals(local.Local, expected))
            throw new ExtractionException($"{description} must pass the exact local {expected.Name} as {refKind}.");
    }

    private static void RequireParameterArgument(
        IInvocationOperation operation,
        string parameterName,
        IParameterSymbol expected,
        RefKind refKind,
        string description)
    {
        IArgumentOperation argument = RequireArgument(operation, parameterName);
        IParameterReferenceOperation? parameter = UnwrapParameterReference(argument.Value);
        if (argument.Parameter?.RefKind != refKind || parameter is null ||
            !SymbolEqualityComparer.Default.Equals(parameter.Parameter, expected))
            throw new ExtractionException($"{description} must pass the exact parameter {expected.Name} as {refKind}.");
    }

    private static void RequireCanonicalArgument(
        IInvocationOperation operation,
        string parameterName,
        string canonical,
        RefKind refKind,
        string description)
    {
        IArgumentOperation argument = RequireArgument(operation, parameterName);
        ArgumentSyntax? sourceArgument = argument.Syntax.AncestorsAndSelf().OfType<ArgumentSyntax>().FirstOrDefault();
        SyntaxNode expression = sourceArgument?.Expression ?? argument.Value.Syntax;
        if (argument.Parameter?.RefKind != refKind || Canonical(expression) != canonical)
            throw new ExtractionException($"{description} must retain the exact argument {canonical} as {refKind}; received {Canonical(expression)} as {argument.Parameter?.RefKind}.");
    }

    private static void RequirePropertyArgument(
        IInvocationOperation operation,
        string parameterName,
        string propertyName,
        string containingType,
        string assembly,
        RefKind refKind,
        string description)
    {
        IArgumentOperation argument = RequireArgument(operation, parameterName);
        IOperation value = argument.Value;
        while (value is IConversionOperation conversion) value = conversion.Operand;
        if (argument.Parameter?.RefKind != refKind || value is not IPropertyReferenceOperation property ||
            property.Property.Name != propertyName ||
            property.Property.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != containingType ||
            property.Property.ContainingAssembly.Identity.Name != assembly)
            throw new ExtractionException($"{description} must receive the exact {propertyName} property.");
    }

    private static string ArgumentIdentity(IInvocationOperation operation, string parameterName)
    {
        IArgumentOperation argument = RequireArgument(operation, parameterName);
        return Canonical(argument.Value.Syntax) + ":" + (argument.Parameter?.RefKind.ToString() ?? "missing");
    }

    private static IFieldReferenceOperation? UnwrapFieldReference(IOperation operation)
    {
        while (operation is IConversionOperation conversion) operation = conversion.Operand;
        return operation as IFieldReferenceOperation;
    }

    private static ILocalSymbol RequireInitializerLocal(
        MethodDeclarationSyntax method,
        SemanticContext context,
        string localName,
        string initializerLocalName)
    {
        SemanticModel model = context.Model(method.SyntaxTree);
        VariableDeclaratorSyntax declarator = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .SingleOrDefault(variable => variable.Identifier.ValueText == localName)
            ?? throw new ExtractionException($"{method.Identifier.ValueText}.{localName} local is missing or ambiguous.");
        ILocalSymbol local = model.GetDeclaredSymbol(declarator) as ILocalSymbol
            ?? throw new ExtractionException($"{method.Identifier.ValueText}.{localName} local has no exact semantic symbol.");
        if (declarator.Initializer is null || model.GetOperation(declarator.Initializer.Value) is not IOperation initializer ||
            !ContainsSymbolReference(initializer, new HashSet<ISymbol>(SymbolEqualityComparer.Default) { RequireLocal(method, context, initializerLocalName) }))
            throw new ExtractionException($"{method.Identifier.ValueText}.{localName} must be initialized from {initializerLocalName}.");
        return local;
    }

    private static bool ContainsInvocation(SyntaxNode node, InvocationExpressionSyntax invocation) =>
        node.DescendantNodesAndSelf().Any(candidate => ReferenceEquals(candidate, invocation));

    private static void ValidateRecipientAndEnvironmentRent(
        MethodDeclarationSyntax environment,
        SemanticContext context)
    {
        SemanticModel model = context.Model(environment.SyntaxTree);
        IMethodSymbol environmentSymbol = DeclaredMethodSymbol(environment, context);
        IParameterSymbol txParameter = RequireParameter(environmentSymbol, "tx");
        IInvocationOperation recipient = RequireInvocationOperation(environment, context, "GetRecipient");
        IMethodSymbol recipientMethod = recipient.TargetMethod;
        if (recipientMethod.Name != "GetRecipient" || !recipientMethod.IsStatic ||
            recipientMethod.ContainingAssembly.Identity.Name != "Nethermind.Evm" ||
            recipientMethod.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != "Nethermind.Evm.TransactionExtensions" ||
            !recipientMethod.Parameters.Select(static parameter => parameter.Name).SequenceEqual(["tx", "nonce"], StringComparer.Ordinal) ||
            !recipientMethod.Parameters.Select(static parameter => parameter.RefKind).SequenceEqual([RefKind.None, RefKind.In]) ||
            !recipient.Arguments.Select(static argument => argument.Parameter?.Name ?? string.Empty).SequenceEqual(["tx", "nonce"], StringComparer.Ordinal) ||
            !recipient.Arguments.Select(static argument => argument.Parameter?.RefKind ?? RefKind.None).SequenceEqual([RefKind.None, RefKind.In]))
            throw new ExtractionException("BuildExecutionEnvironment must call the exact TransactionExtensions.GetRecipient overload.");
        RequireParameterArgument(recipient, "tx", txParameter, RefKind.None, "GetRecipient.tx");
        RequireCanonicalArgument(recipient, "nonce", "tx.IsContractCreation?WorldState.GetNonce(tx.SenderAddress!):0", RefKind.In, "GetRecipient.nonce");

        IInvocationOperation rent = RequireInvocationOperation(environment, context, "Rent");
        IMethodSymbol rentTarget = rent.TargetMethod;
        string[] rentParameters = ["codeInfo", "executingAccount", "caller", "codeSource", "callDepth", "value", "inputData"];
        RefKind[] rentRefKinds = [RefKind.None, RefKind.None, RefKind.None, RefKind.None, RefKind.None, RefKind.In, RefKind.In];
        if (rentTarget.Name != "Rent" || !rentTarget.IsStatic ||
            rentTarget.ContainingAssembly.Identity.Name != "Nethermind.Evm" ||
            rentTarget.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != "Nethermind.Evm.ExecutionEnvironment" ||
            !rentTarget.Parameters.Select(static parameter => parameter.Name).SequenceEqual(rentParameters, StringComparer.Ordinal) ||
            !rentTarget.Parameters.Select(static parameter => parameter.RefKind).SequenceEqual(rentRefKinds) ||
            !rent.Arguments.Select(static argument => argument.Parameter?.Name ?? string.Empty).SequenceEqual(rentParameters, StringComparer.Ordinal) ||
            !rent.Arguments.Select(static argument => argument.Parameter?.RefKind ?? RefKind.None).SequenceEqual(rentRefKinds))
            throw new ExtractionException("BuildExecutionEnvironment must retain the exact ExecutionEnvironment.Rent tuple.");

        ILocalSymbol codeInfo = RequireLocal(environment, context, "codeInfo");
        ILocalSymbol recipientLocal = RequireLocal(environment, context, "recipient");
        ILocalSymbol inputData = RequireLocal(environment, context, "inputData");
        RequireLocalArgument(rent, "codeInfo", codeInfo, RefKind.None, "ExecutionEnvironment.Rent.codeInfo");
        RequireLocalArgument(rent, "executingAccount", recipientLocal, RefKind.None, "ExecutionEnvironment.Rent.executingAccount");
        RequireCanonicalArgument(rent, "caller", "tx.SenderAddress!", RefKind.None, "ExecutionEnvironment.Rent.caller");
        RequireLocalArgument(rent, "codeSource", recipientLocal, RefKind.None, "ExecutionEnvironment.Rent.codeSource");
        RequireCanonicalArgument(rent, "callDepth", "0", RefKind.None, "ExecutionEnvironment.Rent.callDepth");
        RequireCanonicalArgument(rent, "value", "tx.ValueRef", RefKind.In, "ExecutionEnvironment.Rent.value");
        RequireLocalArgument(rent, "inputData", inputData, RefKind.In, "ExecutionEnvironment.Rent.inputData");

        _ = model;
    }

    private static (string[] Lineage, SourceBinding[] Bindings) ValidateSnapshotDataflow(
        MethodDeclarationSyntax execute,
        MethodDeclarationSyntax call,
        SemanticContext context,
        ILocalSymbol delegationRefunds,
        List<SourceBinding> bindings)
    {
        SemanticModel model = context.Model(execute.SyntaxTree);
        ILocalSymbol preExecutionSnapshot = RequireLocal(execute, context, "preExecutionSnapshot");
        ILocalSymbol hasPreExecutionSnapshot = RequireLocal(execute, context, "hasPreExecutionSnapshot");
        VariableDeclaratorSyntax preSnapshotDeclaration = execute.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(variable => variable.Identifier.ValueText == "preExecutionSnapshot");
        if (preSnapshotDeclaration.Initializer is null || Canonical(preSnapshotDeclaration.Initializer.Value) != "Snapshot.Empty")
            throw new ExtractionException("ExecuteEvmTransaction must initialize preExecutionSnapshot from Snapshot.Empty.");
        VariableDeclaratorSyntax hasSnapshotDeclaration = execute.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(variable => variable.Identifier.ValueText == "hasPreExecutionSnapshot");
        if (hasSnapshotDeclaration.Initializer is null || Canonical(hasSnapshotDeclaration.Initializer.Value) != "false")
            throw new ExtractionException("ExecuteEvmTransaction must initialize hasPreExecutionSnapshot to false.");
        AssignmentExpressionSyntax capture = FindAssignment(execute, "preExecutionSnapshot", "WorldState.TakeSnapshot()");
        AssignmentExpressionSyntax setPresence = FindAssignment(execute, "hasPreExecutionSnapshot", "true");
        IfStatementSyntax outerGuard = setPresence.Ancestors().OfType<IfStatementSyntax>()
            .SingleOrDefault(statement => Canonical(statement.Condition) == "spec.IsEip7702Enabled&&tx.HasAuthorizationList")
            ?? throw new ExtractionException("hasPreExecutionSnapshot=true must be under the exact EIP-7702 authorization-list guard.");
        IfStatementSyntax innerGuard = setPresence.Ancestors().OfType<IfStatementSyntax>()
            .SingleOrDefault(statement => Canonical(statement.Condition) == "spec.IsEip8037Enabled")
            ?? throw new ExtractionException("hasPreExecutionSnapshot=true must be under the exact EIP-8037 guard.");
        SemanticModel executeModel = context.Model(execute.SyntaxTree);
        if (!IsExactEip7702AuthorizationCondition(outerGuard.Condition, executeModel) ||
            !IsExactEip8037Condition(innerGuard.Condition, executeModel) ||
            innerGuard.SpanStart <= outerGuard.SpanStart ||
            !ContainsInvocation(innerGuard.Statement, capture.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Single()))
            throw new ExtractionException("The pre-execution snapshot presence assignments are not nested under the exact EIP-7702/EIP-8037 guards.");

        InvocationExpressionSyntax[] restores = execute.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() == execute)
            .Where(invocation => model.GetOperation(invocation) is IInvocationOperation operation &&
                operation.TargetMethod.Name == "Restore" && !HasErrorSymbol(operation.TargetMethod))
            .ToArray();
        if (restores.Length != 1) throw new ExtractionException("ExecuteEvmTransaction must retain one preparation WorldState.Restore call.");
        IInvocationOperation restore = model.GetOperation(restores[0]) as IInvocationOperation
            ?? throw new ExtractionException("The preparation WorldState.Restore call is not an invocation operation.");
        if (restore.TargetMethod.ContainingAssembly.Identity.Name != "Nethermind.Core" ||
            restore.TargetMethod.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != "Nethermind.Core.IJournal<Nethermind.Evm.State.Snapshot>" ||
            restore.Instance?.Type?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != "Nethermind.Evm.State.IWorldState" ||
            restore.Arguments.Length != 1)
            throw new ExtractionException("The preparation restore target changed.");
        RequireLocalArgument(restore, restore.TargetMethod.Parameters[0].Name, preExecutionSnapshot, RefKind.None, "preparation WorldState.Restore.snapshot");

        ILocalSymbol snapshot = RequireLocal(call, context, "snapshot");
        InvocationExpressionSyntax takeSnapshot = FindInvocation(call, context, "TakeSnapshot");
        VariableDeclaratorSyntax snapshotDeclaration = call.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(variable => variable.Identifier.ValueText == "snapshot");
        if (!ContainsInvocation((SyntaxNode?)snapshotDeclaration.Initializer?.Value ?? snapshotDeclaration, takeSnapshot))
            throw new ExtractionException("ExecuteEvmCall.snapshot must be initialized by the exact WorldState.TakeSnapshot call.");
        _ = snapshot;
        _ = delegationRefunds;

        SourceBinding[] dataflowBindings = bindings.Where(binding => binding.Member is
                "allocateStackAccessTracker" or "captureDelegationRefunds" or "initializePreparationSnapshot" or
                "capturePreExecutionSnapshot" or "restorePreparationSnapshot" or "processDelegations" or
                "warmTransactionAccesses" or "rentEnvironment" or "handoff.executeEvmCall.access.off" or
                "handoff.executeEvmCall.access.on" or "handoff.rentTopLevel" or "executeTransactionBoundary.off" or
                "executeTransactionBoundary.on")
            .ToArray();
        string[] requiredMembers =
        [
            "allocateStackAccessTracker", "captureDelegationRefunds", "initializePreparationSnapshot", "capturePreExecutionSnapshot",
            "restorePreparationSnapshot", "processDelegations", "warmTransactionAccesses", "rentEnvironment",
            "handoff.executeEvmCall.access.off", "handoff.executeEvmCall.access.on", "handoff.rentTopLevel",
            "executeTransactionBoundary.off", "executeTransactionBoundary.on",
        ];
        if (!requiredMembers.All(member => dataflowBindings.Any(binding => binding.Member == member)))
            throw new ExtractionException("The source dataflow is missing one or more admitted preparation/boundary bindings.");
        string[] lineage =
        [
            "preExecutionSnapshot:false-init:" + preExecutionSnapshot.Name,
            "preExecutionSnapshot:true-assignment:" + Canonical(setPresence) + ":guard=" + Canonical(innerGuard.Condition),
            "preExecutionSnapshot:capture-assignment:" + Canonical(capture),
            "preExecutionSnapshot:restore=" + ArgumentIdentity(restore, restore.TargetMethod.Parameters[0].Name),
            "RentTopLevel.snapshot=" + snapshot.Name,
        ];
        return (lineage, dataflowBindings);
    }

    private static void ValidateExecuteEvmCallArguments(
        MethodDeclarationSyntax execute,
        MethodDeclarationSyntax call,
        SemanticContext context,
        IMethodSymbol executeSymbol,
        IMethodSymbol callSymbol,
        InvocationExpressionSyntax[] calls,
        ILocalSymbol tracker,
        ILocalSymbol delegationRefunds,
        ILocalSymbol executionIntrinsicGas,
        ILocalSymbol postIntrinsicStateReservoir,
        ILocalSymbol environment,
        ILocalSymbol topFrameOutOfGas,
        IParameterSymbol gasAvailable,
        IParameterSymbol tx,
        IParameterSymbol header,
        IParameterSymbol spec,
        IParameterSymbol tracer,
        IParameterSymbol opts)
    {
        SemanticModel model = context.Model(execute.SyntaxTree);
        IParameterSymbol callTx = RequireParameter(callSymbol, "tx");
        IParameterSymbol callHeader = RequireParameter(callSymbol, "header");
        IParameterSymbol callSpec = RequireParameter(callSymbol, "spec");
        IParameterSymbol callTracer = RequireParameter(callSymbol, "tracer");
        IParameterSymbol callOpts = RequireParameter(callSymbol, "opts");
        IParameterSymbol callGasAvailable = RequireParameter(callSymbol, "gasAvailable");
        IParameterSymbol callDelegationRefunds = RequireParameter(callSymbol, "delegationRefunds");
        IParameterSymbol callGas = RequireParameter(callSymbol, "gas");
        IParameterSymbol callPostReservoir = RequireParameter(callSymbol, "postIntrinsicStateReservoir");
        IParameterSymbol callEnvironment = RequireParameter(callSymbol, "env");
        IParameterSymbol callTopFrameOutOfGas = RequireParameter(callSymbol, "topFrameOutOfGas");
        IParameterSymbol callSubstate = RequireParameter(callSymbol, "substate");
        IParameterSymbol callGasConsumed = RequireParameter(callSymbol, "gasConsumed");
        ILocalSymbol? substate = null;
        ILocalSymbol? gasConsumed = null;
        foreach (InvocationExpressionSyntax syntax in calls)
        {
            IInvocationOperation operation = model.GetOperation(syntax) as IInvocationOperation
                ?? throw new ExtractionException("ExecuteEvmTransaction ExecuteEvmCall call is not an invocation operation.");
            RequireInvocationShape(operation, callSymbol,
                callSymbol.Parameters.Select(static parameter => parameter.Name).ToArray(),
                callSymbol.Parameters.Select(static parameter => parameter.RefKind).ToArray(),
                "ExecuteEvmTransaction.ExecuteEvmCall");
            RequireParameterArgument(operation, callTx.Name, tx, RefKind.None, "ExecuteEvmCall.tx");
            RequireParameterArgument(operation, callHeader.Name, header, RefKind.None, "ExecuteEvmCall.header");
            RequireParameterArgument(operation, callSpec.Name, spec, RefKind.None, "ExecuteEvmCall.spec");
            RequireParameterArgument(operation, callTracer.Name, tracer, RefKind.None, "ExecuteEvmCall.tracer");
            RequireParameterArgument(operation, callOpts.Name, opts, RefKind.None, "ExecuteEvmCall.opts");
            RequireLocalArgument(operation, callDelegationRefunds.Name, delegationRefunds, RefKind.None, "ExecuteEvmCall.delegationRefunds");
            RequireLocalArgument(operation, callGas.Name, executionIntrinsicGas, RefKind.None, "ExecuteEvmCall.gas");
            RequireLocalArgument(operation, callPostReservoir.Name, postIntrinsicStateReservoir, RefKind.None, "ExecuteEvmCall.postIntrinsicStateReservoir");
            RequireLocalArgument(operation, "accessedItems", tracker, RefKind.In, "ExecuteEvmCall.accessedItems");
            RequireParameterArgument(operation, callGasAvailable.Name, gasAvailable, RefKind.None, "ExecuteEvmCall.gasAvailable");
            RequireLocalArgument(operation, callEnvironment.Name, environment, RefKind.None, "ExecuteEvmCall.env");
            RequireLocalArgument(operation, callTopFrameOutOfGas.Name, topFrameOutOfGas, RefKind.None, "ExecuteEvmCall.topFrameOutOfGas");

            IArgumentOperation substateArgument = RequireArgument(operation, callSubstate.Name);
            ILocalReferenceOperation? substateReference = UnwrapLocalReference(substateArgument.Value);
            if (substateArgument.Parameter?.RefKind != RefKind.Out || substateReference is null)
                throw new ExtractionException("ExecuteEvmCall.substate must be an exact out local.");
            substate ??= substateReference.Local;
            if (!SymbolEqualityComparer.Default.Equals(substate, substateReference.Local))
                throw new ExtractionException("ExecuteEvmTransaction ExecuteEvmCall calls must share the exact substate local.");

            IArgumentOperation gasConsumedArgument = RequireArgument(operation, callGasConsumed.Name);
            ILocalReferenceOperation? gasConsumedReference = UnwrapLocalReference(gasConsumedArgument.Value);
            if (gasConsumedArgument.Parameter?.RefKind != RefKind.Out || gasConsumedReference is null)
                throw new ExtractionException("ExecuteEvmCall.gasConsumed must be an exact out local.");
            gasConsumed ??= gasConsumedReference.Local;
            if (!SymbolEqualityComparer.Default.Equals(gasConsumed, gasConsumedReference.Local))
                throw new ExtractionException("ExecuteEvmTransaction ExecuteEvmCall calls must share the exact gasConsumed local.");
        }

        _ = executeSymbol;
        _ = call;
    }

    private static HandoffIdentity AddHandoff(
        MethodDeclarationSyntax execute,
        MethodDeclarationSyntax delegations,
        MethodDeclarationSyntax environment,
        MethodDeclarationSyntax call,
        SemanticContext context,
        List<SourceBinding> bindings)
    {
        SyntaxNode trackerCreation = FindAccessTrackerCreation(execute, context);
        SemanticModel executeModel = context.Model(execute.SyntaxTree);
        IMethodSymbol executeSymbol = DeclaredMethodSymbol(execute, context);
        IMethodSymbol delegationSymbol = DeclaredMethodSymbol(delegations, context);
        IMethodSymbol environmentSymbol = DeclaredMethodSymbol(environment, context);
        ValidateRecipientAndEnvironmentRent(environment, context);
        VariableDeclaratorSyntax trackerVariable = trackerCreation.AncestorsAndSelf().OfType<VariableDeclaratorSyntax>().SingleOrDefault()
            ?? throw new ExtractionException("The source accessTracker allocation is not bound to one local variable.");
        ILocalSymbol tracker = executeModel.GetDeclaredSymbol(trackerVariable) as ILocalSymbol
            ?? throw new ExtractionException("The source accessTracker local has no exact semantic symbol.");

        IParameterSymbol txParameter = RequireParameter(executeSymbol, "tx");
        IParameterSymbol headerParameter = RequireParameter(executeSymbol, "header");
        IParameterSymbol specParameter = RequireParameter(executeSymbol, "spec");
        IParameterSymbol tracerParameter = RequireParameter(executeSymbol, "tracer");
        IParameterSymbol optsParameter = RequireParameter(executeSymbol, "opts");
        IParameterSymbol gasAvailableParameter = RequireParameter(executeSymbol, "gasAvailable");
        IParameterSymbol preloadedCodeInfoParameter = RequireParameter(executeSymbol, "preloadedCodeInfo");
        IParameterSymbol preloadedDelegationAddressParameter = RequireParameter(executeSymbol, "preloadedDelegationAddress");
        ILocalSymbol delegationRefunds = RequireLocal(execute, context, "delegationRefunds");
        ILocalSymbol executionIntrinsicGasStandard = RequireLocal(execute, context, "executionIntrinsicGasStandard");
        ILocalSymbol executionIntrinsicGas = RequireLocal(execute, context, "executionIntrinsicGas");
        ILocalSymbol postIntrinsicStateReservoir = RequireLocal(execute, context, "postIntrinsicStateReservoir");
        ILocalSymbol loadRecipient = RequireLocal(execute, context, "loadRecipient");
        ILocalSymbol topFrameOutOfGas = RequireLocal(execute, context, "topFrameOutOfGas");
        ILocalSymbol outputEnvironment = RequireLocal(execute, context, "e");
        ILocalSymbol environmentLocal = RequireInitializerLocal(execute, context, "env", "e");

        IInvocationOperation processDelegations = RequireInvocationOperation(execute, context, "ProcessDelegations");
        RequireInvocationShape(processDelegations, delegationSymbol,
            ["tx", "spec", "accessTracker", "gasAvailable", "executionIntrinsicGasStandard", "codeInsertRefunds"],
            [RefKind.None, RefKind.None, RefKind.In, RefKind.Ref, RefKind.Ref, RefKind.Out],
            "ExecuteEvmTransaction.ProcessDelegations");
        RequireParameterArgument(processDelegations, "tx", txParameter, RefKind.None, "ProcessDelegations.tx");
        RequireParameterArgument(processDelegations, "spec", specParameter, RefKind.None, "ProcessDelegations.spec");
        RequireLocalArgument(processDelegations, "accessTracker", tracker, RefKind.In, "ProcessDelegations.accessTracker");
        RequireParameterArgument(processDelegations, "gasAvailable", gasAvailableParameter, RefKind.Ref, "ProcessDelegations.gasAvailable");
        RequireLocalArgument(processDelegations, "executionIntrinsicGasStandard", executionIntrinsicGasStandard, RefKind.Ref, "ProcessDelegations.executionIntrinsicGasStandard");
        RequireLocalArgument(processDelegations, "codeInsertRefunds", delegationRefunds, RefKind.Out, "ProcessDelegations.delegationRefunds");

        IInvocationOperation buildEnvironment = RequireInvocationOperation(execute, context, "BuildExecutionEnvironment");
        RequireInvocationShape(buildEnvironment, environmentSymbol,
            ["tx", "spec", "codeInfoRepository", "accessTracker", "preloadedCodeInfo", "preloadedDelegationAddress", "loadRecipient", "gasAvailable", "topFrameOutOfGas", "env"],
            [RefKind.None, RefKind.None, RefKind.None, RefKind.In, RefKind.None, RefKind.None, RefKind.None, RefKind.Ref, RefKind.Ref, RefKind.Out],
            "ExecuteEvmTransaction.BuildExecutionEnvironment");
        RequireParameterArgument(buildEnvironment, "tx", txParameter, RefKind.None, "BuildExecutionEnvironment.tx");
        RequireParameterArgument(buildEnvironment, "spec", specParameter, RefKind.None, "BuildExecutionEnvironment.spec");
        IArgumentOperation repositoryArgument = RequireArgument(buildEnvironment, "codeInfoRepository");
        IFieldReferenceOperation? repositoryField = UnwrapFieldReference(repositoryArgument.Value);
        IFieldSymbol expectedRepository = executeSymbol.ContainingType.GetMembers("_codeInfoRepository").OfType<IFieldSymbol>().SingleOrDefault()
            ?? throw new ExtractionException("TransactionProcessorBase._codeInfoRepository field is missing.");
        if (repositoryArgument.Parameter?.RefKind != RefKind.None || repositoryField is null ||
            !SymbolEqualityComparer.Default.Equals(repositoryField.Field, expectedRepository))
            throw new ExtractionException("BuildExecutionEnvironment must receive the exact _codeInfoRepository field.");
        RequireLocalArgument(buildEnvironment, "accessTracker", tracker, RefKind.In, "BuildExecutionEnvironment.accessTracker");
        RequireParameterArgument(buildEnvironment, "preloadedCodeInfo", preloadedCodeInfoParameter, RefKind.None, "BuildExecutionEnvironment.preloadedCodeInfo");
        RequireParameterArgument(buildEnvironment, "preloadedDelegationAddress", preloadedDelegationAddressParameter, RefKind.None, "BuildExecutionEnvironment.preloadedDelegationAddress");
        RequireLocalArgument(buildEnvironment, "loadRecipient", loadRecipient, RefKind.None, "BuildExecutionEnvironment.loadRecipient");
        RequireParameterArgument(buildEnvironment, "gasAvailable", gasAvailableParameter, RefKind.Ref, "BuildExecutionEnvironment.gasAvailable");
        RequireLocalArgument(buildEnvironment, "topFrameOutOfGas", topFrameOutOfGas, RefKind.Ref, "BuildExecutionEnvironment.topFrameOutOfGas");
        RequireLocalArgument(buildEnvironment, "env", outputEnvironment, RefKind.Out, "BuildExecutionEnvironment.env");

        InvocationExpressionSyntax[] accessCalls = FindExecuteEvmCallAccessCalls(execute, call, context, tracker,
            out IMethodSymbol callSymbol, out IParameterSymbol accessParameter);
        ValidateExecuteEvmCallArguments(execute, call, context, executeSymbol, callSymbol, accessCalls, tracker,
            delegationRefunds, executionIntrinsicGas, postIntrinsicStateReservoir, environmentLocal, topFrameOutOfGas,
            gasAvailableParameter, txParameter, headerParameter, specParameter, tracerParameter, optsParameter);
        InvocationExpressionSyntax rent = FindInvocation(call, context, "RentTopLevel");
        IInvocationOperation rentOperation = context.Model(call.SyntaxTree).GetOperation(rent) as IInvocationOperation
            ?? throw new ExtractionException("VmState.RentTopLevel is not an invocation operation.");
        if (rentOperation.TargetMethod.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != "Nethermind.Evm.VmState<TGasPolicy>" ||
            rentOperation.TargetMethod.ContainingAssembly.Identity.Name != "Nethermind.Evm" || HasErrorSymbol(rentOperation.TargetMethod))
        {
            throw new ExtractionException("VmState.RentTopLevel did not bind to the exact production generic method.");
        }
        if (!rentOperation.TargetMethod.Parameters.Select(static parameter => parameter.Name)
                .SequenceEqual(["gas", "executionType", "env", "accessedItems", "snapshot"], StringComparer.Ordinal) ||
            !rentOperation.Arguments.Select(static argument => argument.Parameter?.Name ?? string.Empty)
                .SequenceEqual(["gas", "executionType", "env", "accessedItems", "snapshot"], StringComparer.Ordinal))
        {
            throw new ExtractionException("VmState.RentTopLevel parameter or argument identities changed.");
        }
        string[] rentRefKinds = rentOperation.Arguments.Select(static argument => argument.Parameter?.RefKind.ToString() ?? "missing").ToArray();
        string[] rentParameterRefKinds = rentOperation.TargetMethod.Parameters.Select(static parameter => parameter.RefKind.ToString()).ToArray();
        if (!rentRefKinds.SequenceEqual(rentParameterRefKinds, StringComparer.Ordinal) || !rentRefKinds.SequenceEqual(["None", "None", "None", "In", "In"], StringComparer.Ordinal))
            throw new ExtractionException("VmState.RentTopLevel ref-kind identities changed.");
        IArgumentOperation rentAccessArgument = rentOperation.Arguments.Single(argument => argument.Parameter?.Name == "accessedItems");
        IParameterReferenceOperation? rentAccessParameter = UnwrapParameterReference(rentAccessArgument.Value);
        if (rentAccessArgument.Parameter?.RefKind != RefKind.In || rentAccessParameter is null ||
            !SymbolEqualityComparer.Default.Equals(rentAccessParameter.Parameter, accessParameter))
            throw new ExtractionException("VmState.RentTopLevel must receive the exact ExecuteEvmCall.accessedItems parameter.");
        ILocalSymbol executionType = RequireLocal(call, context, "executionType");
        ILocalSymbol snapshot = RequireLocal(call, context, "snapshot");
        VariableDeclaratorSyntax stateDeclaration = call.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .SingleOrDefault(variable => variable.Identifier.ValueText == "state")
            ?? throw new ExtractionException("ExecuteEvmCall.state local is missing or ambiguous.");
        if (!ContainsInvocation((SyntaxNode?)stateDeclaration.Initializer?.Value ?? stateDeclaration, rent))
            throw new ExtractionException("ExecuteEvmCall.state must be created by the exact VmState.RentTopLevel call.");
        InvocationExpressionSyntax takeSnapshot = FindInvocation(call, context, "TakeSnapshot");
        VariableDeclaratorSyntax snapshotDeclaration = call.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(variable => variable.Identifier.ValueText == "snapshot");
        if (!ContainsInvocation((SyntaxNode?)snapshotDeclaration.Initializer?.Value ?? snapshotDeclaration, takeSnapshot))
            throw new ExtractionException("ExecuteEvmCall.snapshot must be created by the exact WorldState.TakeSnapshot call.");
        ILocalSymbol state = context.Model(call.SyntaxTree).GetDeclaredSymbol(stateDeclaration) as ILocalSymbol
            ?? throw new ExtractionException("ExecuteEvmCall.state local has no exact semantic symbol.");
        RequireParameterArgument(rentOperation, "gas", RequireParameter(callSymbol, "gasAvailable"), RefKind.None, "VmState.RentTopLevel.gas");
        RequireLocalArgument(rentOperation, "executionType", executionType, RefKind.None, "VmState.RentTopLevel.executionType");
        RequireParameterArgument(rentOperation, "env", RequireParameter(callSymbol, "env"), RefKind.None, "VmState.RentTopLevel.env");
        RequireLocalArgument(rentOperation, "snapshot", snapshot, RefKind.In, "VmState.RentTopLevel.snapshot");
        InvocationExpressionSyntax[] vmCalls = FindVmBoundaryCalls(call, context);
        List<string> vmOverloads = [];
        List<string> vmReceivers = [];
        List<string> vmArgumentIdentities = [];
        List<string> vmRefKinds = [];
        IParameterSymbol callTracer = RequireParameter(callSymbol, "tracer");
        foreach (InvocationExpressionSyntax boundaryCall in vmCalls)
        {
            IInvocationOperation operation = context.Model(call.SyntaxTree).GetOperation(boundaryCall) as IInvocationOperation
                ?? throw new ExtractionException("The top-level VM handoff is not an invocation operation.");
            RequireLocalArgument(operation, "state", state, RefKind.None, "VirtualMachine.ExecuteTransaction.state");
            RequirePropertyArgument(operation, "worldState", "WorldState",
                "Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<TGasPolicy>", "Nethermind.Evm", RefKind.None,
                "VirtualMachine.ExecuteTransaction.worldState");
            RequireParameterArgument(operation, "txTracer", callTracer, RefKind.None, "VirtualMachine.ExecuteTransaction.txTracer");
            IMethodSymbol target = operation.TargetMethod;
            string[] argumentRefKinds = operation.Arguments.Select(static argument => argument.Parameter?.RefKind.ToString() ?? "missing").ToArray();
            vmOverloads.Add(target.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
            vmReceivers.Add(InvocationReceiver(boundaryCall) + " :: " + (operation.Instance?.Type?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? ""));
            vmArgumentIdentities.Add(string.Join(',', operation.Arguments.Select(static argument => argument.Parameter?.Name ?? "missing")));
            vmRefKinds.AddRange(argumentRefKinds);
        }
        SourceBinding rentBinding = Bind(call.SyntaxTree, call, "handoff.rentTopLevel", rent, context);
        InvocationExpressionSyntax vmCall = FindVmBoundaryOverload(vmCalls, call, context, generic: false);
        InvocationExpressionSyntax vmTracingCall = FindVmBoundaryOverload(vmCalls, call, context, generic: true);
        SourceBinding vmBinding = Bind(call.SyntaxTree, call, "handoff.executeTransaction", vmCall, context);
        SourceBinding vmTracingBinding = Bind(call.SyntaxTree, call, "handoff.executeTransaction.tracing", vmTracingCall, context);
        SourceBinding[] accessBindings = accessCalls
            .Select(invocation => Bind(execute.SyntaxTree, execute,
                "handoff.executeEvmCall.access." +
                AccessBindingSuffix(context.Model(execute.SyntaxTree).GetOperation(invocation) as IInvocationOperation
                    ?? throw new ExtractionException("ExecuteEvmTransaction ExecuteEvmCall call is not an invocation operation.")),
                invocation, context))
            .ToArray();
        bindings.Add(rentBinding);
        bindings.Add(vmBinding);
        bindings.Add(vmTracingBinding);
        bindings.AddRange(accessBindings);
        (string[] snapshotLineage, SourceBinding[] dataflowBindings) = ValidateSnapshotDataflow(execute, call, context, delegationRefunds, bindings);
        string accessParameterIdentity = callSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) + "::" +
            accessParameter.Name + ":" + accessParameter.Type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) + ":" + accessParameter.RefKind;
        string rentParameterIdentity = rentOperation.TargetMethod.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) + "::accessedItems:" +
            rentOperation.TargetMethod.Parameters[3].Type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) + ":" + rentOperation.TargetMethod.Parameters[3].RefKind;
        string[] accessLineage =
        [
            "fresh-local:accessTracker:" + tracker.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            "ExecuteEvmTransaction.accessTracker->" + accessParameterIdentity,
            accessParameterIdentity + "->" + rentParameterIdentity,
        ];
        return new HandoffIdentity(
            "pre-virtual-machine-execute-transaction",
            ["gasAvailable", "executionIntrinsicGasStandard", "executionType", "env", "accessedItems", "snapshot", "WorldState", "tracer", "opts", "hasPreExecutionSnapshot"],
            "VirtualMachine.ExecuteTransaction",
            rentOperation.TargetMethod.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            rentOperation.TargetMethod.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            InvocationReceiver(rent) + " :: " + (rentOperation.Instance?.Type?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? ""),
            rentOperation.Arguments.Select(static argument => argument.Parameter?.Name ?? "missing").ToArray(),
            rentRefKinds,
            vmCalls.Length == 0 ? string.Empty : "Nethermind.Evm.IVirtualMachine<TGasPolicy>",
            string.Join(" || ", vmOverloads),
            string.Join(" || ", vmReceivers),
            vmArgumentIdentities.ToArray(),
            vmRefKinds.ToArray(),
             [rentBinding, vmBinding, vmTracingBinding],
             accessLineage,
             [.. accessBindings, rentBinding],
             snapshotLineage,
             dataflowBindings,
             rentBinding);
    }

    private static AdapterPremise[] BuildAdapters(List<SourceBinding> bindings, List<OperationIdentity> operations)
    {
        SourceBinding[] sourceEntryOperations = operations.Where(operation => operation.Formula is
                SemanticFormula.SetTransactionContext or
                SemanticFormula.AllocateAccessTracker or
                SemanticFormula.CapturePreparationGas or
                SemanticFormula.CaptureExecutionIntrinsicGasStandard or
                SemanticFormula.CaptureDelegationRefunds or
                SemanticFormula.InitializePreparationSnapshot or
                SemanticFormula.CapturePreparationSnapshot)
            .Select(static operation => operation.Binding).ToArray();
        SourceBinding[] sourceEntryGasFields = bindings.Where(binding => binding.Member.StartsWith("gasField.", StringComparison.Ordinal)).ToArray();
        SourceBinding[] sourceEntrySnapshot =
        [
            .. bindings.Where(binding => binding.Member == "snapshot.authorizationPresence"),
            .. bindings.Where(binding => binding.Member == "snapshot.preparationPresence"),
            .. bindings.Where(binding => binding.Member == "snapshot.emptyPosition"),
        ];
        SourceBinding[] sourceEntryTransaction = bindings.Where(binding => binding.Member == "transaction.hasAuthorizationListSetCode").ToArray();
        SourceBinding[] sourceEntryRecipient = bindings.Where(binding => binding.Member == "transaction.getRecipient").ToArray();
        SourceBinding[] sourceEntryAccess = bindings.Where(binding => binding.Member.StartsWith("handoff.executeEvmCall.access.", StringComparison.Ordinal)).ToArray();
        SourceBinding[] sourceEntryEnvironment = bindings.Where(binding => binding.Member == "environment.rent").ToArray();
        SourceBinding[] sourceEntry = sourceEntryOperations.Concat(sourceEntryGasFields).Concat(sourceEntrySnapshot).Concat(sourceEntryTransaction).Concat(sourceEntryRecipient).Concat(sourceEntryAccess).Concat(sourceEntryEnvironment).ToArray();
        SourceBinding[] authorization = bindings.Where(binding => binding.ContainingMember.StartsWith("IsValidForExecution", StringComparison.Ordinal) || binding.Member.StartsWith("authorization", StringComparison.Ordinal)).ToArray();
        SourceBinding[] gas = operations.Where(operation => operation.Formula is
                SemanticFormula.AuthorizationNewAccountStateCharge or
                SemanticFormula.AuthorizationAccountWriteCharge or
                SemanticFormula.AuthorizationPerAuthStateCharge or
                SemanticFormula.AuthorizationStateGasDelta or
                SemanticFormula.AuthorizationFoldTopFrameStateGas or
                SemanticFormula.ChargeDelegatedTargetAccess or
                SemanticFormula.ChargeDeadRecipientState or
                SemanticFormula.ChargeCreateState)
            .Select(static operation => operation.Binding).ToArray();
        SourceBinding[] executionGas = operations.Where(operation => operation.Formula is
                SemanticFormula.AuthorizationAccountWriteCharge or SemanticFormula.ChargeDelegatedTargetAccess)
            .Select(static operation => operation.Binding).ToArray();
        SourceBinding[] world = operations.Where(operation => operation.Effects.Any(effect => effect is SemanticEffect.ReadsWorld or SemanticEffect.WritesWorld or SemanticEffect.RestoresWorld))
            .Select(static operation => operation.Binding).ToArray();
        return
        [
            new AdapterPremise("source-entry", "transaction-entry", "fresh-StackAccessTracker/tracing-zero-refund-entry-projection", "the source entry allocates a fresh tracker with tracer.IsTracingAccess, initializes delegationRefunds to zero, captures the world/code/snapshot/gas schedule before preparation, derives the CREATE recipient through the exact GetRecipient body, derives snapshot presence from the exact EIP-7702 authorization-list, EIP-8037 guard, and carries the same tracker through ExecuteEvmCall.accessedItems to VmState.RentTopLevel", ["StackAccessTracker", "tracer.IsTracingAccess", "delegationRefunds=0", "WorldState", "ICodeInfoRepository", "ExecutionEnvironment.Rent", "prePreparationGas", "executionIntrinsicGasStandard", "Snapshot.Empty", "Snapshot.EmptyPosition=-1", "Transaction.HasAuthorizationList:Type==TxType.SetCode", "TransactionExtensions.GetRecipient", "hasPreExecutionSnapshot=(eip7702&&hasAuthorization&&eip8037)", "accessTracker->ExecuteEvmCall.accessedItems->VmState.RentTopLevel", "EthereumGasPolicy", "EthereumGasPolicy.Value", "EthereumGasPolicy.StateReservoir", "EthereumGasPolicy.StateGasUsed", "EthereumGasPolicy.StateGasSpill", "EthereumGasPolicy.StateGasSpillRefunded"], sourceEntry),
            new AdapterPremise("authorization-state", "authorization", "five-gas-and-world-projection", "the source state operation preserves all authorization effects and its partial-failure return", ["gas.Value", "gas.StateReservoir", "gas.StateGasUsed", "gas.StateGasSpill", "gas.StateGasSpillRefunded", "WorldState"], authorization),
            new AdapterPremise("state-gas", "eip8037", "TryConsumeStateGas/UpdateGas/FoldTopFrameStateGas", "the pinned state-gas theorem identity is provenance; applying it to this transition remains an explicit external adapter obligation", ["state-gas-charge", "state-gas-transition"], gas),
            new AdapterPremise("execution-gas", "eip8037", "UpdateGas/remaining-gas debit", "the source-bound model separates ACCOUNT_WRITE and delegated-target execution debits from the state-gas reservoir; the pinned pricing identities remain provenance", ["gas.Value", "gas.StateReservoir", "gas.StateGasUsed", "gas.StateGasSpill", "gas.StateGasSpillRefunded"], executionGas),
            new AdapterPremise("world-snapshot", "world", "Snapshot and Restore", "physical/logical existence and original/current facts are modeled explicitly; relating them to the live journal remains an external adapter obligation", ["world-journal", "snapshot.preparation", "snapshot.topLevel"], world),
            new AdapterPremise("vm-boundary", "frame", "VmState.RentTopLevel", "the rented frame receives exactly the admitted gas, environment, access tracker, and snapshot identities; the access argument is symbol-identical from ExecuteEvmTransaction through ExecuteEvmCall", ["handoff.rentTopLevel", "handoff.executeTransaction", "accessTracker->ExecuteEvmCall.accessedItems->VmState.RentTopLevel"], operations.Where(operation => operation.Formula is SemanticFormula.RentTopLevel or SemanticFormula.ExecuteTransactionBoundary).Select(static operation => operation.Binding).Concat(bindings.Where(binding => binding.Member.StartsWith("handoff.executeEvmCall.access.", StringComparison.Ordinal))).ToArray()),
        ];
    }

    private static SemanticPlan BuildSemanticPlan(
        List<StageIdentity> stages,
        List<BranchIdentity> branches,
        List<OperationIdentity> operations,
        GasFieldIdentity[] gasFields,
        SnapshotIdentity[] snapshots,
        EnvironmentIdentity environment,
        HandoffIdentity handoff,
        ControlFlowIdentity[] controlFlows,
        List<AdapterPremise> adapters)
    {
        StageIdentity[] orderedStages = stages.OrderBy(static stage => stage.Ordinal).ToArray();
        BranchIdentity[] orderedBranches = branches.OrderBy(static branch => branch.Ordinal).ToArray();
        OperationIdentity[] orderedOperations = operations.OrderBy(static operation => operation.Ordinal).ToArray();
        string[] stageIds = orderedStages.Select(static stage => stage.Id).ToArray();
        string[] operationIds = orderedOperations.Select(static operation => operation.Id).ToArray();
        string[] branchIds = orderedBranches.Select(static branch => branch.Id).ToArray();
        string[] gasFieldIds = gasFields.Select(static field => field.Name).ToArray();
        string[] snapshotIds = snapshots.Select(static snapshot => snapshot.Id).ToArray();
        string[] adapterIds = adapters.Select(static adapter => adapter.Id).ToArray();
        string[] controlFlowIds = controlFlows.Select(static controlFlow => controlFlow.Id).ToArray();
        string[] effectIds = orderedOperations.SelectMany(static operation => operation.Effects.Select(effect => effect.ToString())).Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal).ToArray();
        if (operationIds.Length == 0 || branchIds.Length == 0 || controlFlowIds.Length != 7 || adapterIds.Length == 0)
            throw new ExtractionException("The normalized semantic plan is incomplete.");
        if (orderedOperations[^1].Formula != SemanticFormula.ExecuteTransactionBoundary || handoff.BoundaryCall != "VirtualMachine.ExecuteTransaction")
            throw new ExtractionException("The normalized semantic plan does not terminate at the admitted VM boundary.");

        StringBuilder canonical = new();
        AppendPlanPart(canonical, "stages", stageIds);
        AppendPlanPart(canonical, "operations", orderedOperations.Select(operation => operation.Id + ":" + operation.Formula + ":" + operation.Expression + ":" + operation.Expected + ":" + string.Join(',', operation.SourceOperands) + ":" +
            string.Join(',', operation.Reads) + ":" + string.Join(',', operation.Writes) + ":" + operation.FailureVisibility + ":" + string.Join(',', operation.Effects) + ":" +
            operation.Binding.CanonicalSyntaxSha256 + ":" + operation.Binding.TargetAssembly + ":" + operation.Binding.TargetSymbol + ":" + operation.Binding.Receiver + ":" +
            string.Join(',', operation.Binding.ArgumentParameters) + ":" + string.Join(',', operation.Binding.ArgumentRefKinds)));
        AppendPlanPart(canonical, "branches", orderedBranches.Select(branch => branch.Id + ":" + branch.Binding.CanonicalSyntaxSha256 + ":" + branch.Condition + ":" + branch.Terminal + ":" +
            string.Join(',', branch.Effects) + ":" + string.Join(',', branch.ControlFlowSuccessors) + ":" + string.Join(',', branch.ExitBlocks)));
        AppendPlanPart(canonical, "gas", gasFields.Select(field => field.Name + ":" + field.SourceType + ":" + field.Representation + ":" + string.Join(',', field.FixedWidthOperations)));
        AppendPlanPart(canonical, "snapshots", snapshots.Select(snapshot => snapshot.Id + ":" + snapshot.Kind + ":" + string.Join(',', snapshot.Fields) + ":" + snapshot.Binding.CanonicalSyntaxSha256));
        AppendPlanPart(canonical, "environment", [string.Join(',', environment.Fields), string.Join(',', environment.CodeFields), string.Join(',', environment.InputFields), environment.Binding.CanonicalSyntaxSha256]);
        AppendPlanPart(canonical, "adapters", adapters.Select(adapter => adapter.Id + ":" + adapter.Domain + ":" + adapter.Projection + ":" + adapter.Assumption + ":" +
            string.Join(',', adapter.RequiredIdentities) + ":" + string.Join(',', adapter.Bindings.Select(binding => binding.Member + "#" + binding.CanonicalSyntaxSha256))));
        AppendPlanPart(canonical, "cfg", controlFlows.Select(flow => flow.Id + ":" + string.Join(',', flow.ReachableBlocks) + ":" + string.Join(',', flow.Edges) + ":" + string.Join(',', flow.NormalExitBlocks) + ":" + string.Join(',', flow.ExceptionalExitBlocks) + ":" + string.Join(',', flow.BackEdges) + ":" +
            flow.Binding.CanonicalSyntaxSha256 + ":" + flow.Binding.TargetSymbol + ":" + flow.Binding.TargetAssembly));
        AppendPlanPart(canonical, "effects", effectIds);
        canonical.Append("handoff:").Append(handoff.RentOwner).Append('|').Append(handoff.RentOverload).Append('|')
            .Append(handoff.VmOwner).Append('|').Append(handoff.VmOverloads).Append('|').Append(string.Join(',', handoff.Fields)).Append('|')
            .Append(string.Join(',', handoff.RentRefKinds)).Append('|').Append(string.Join(',', handoff.VmRefKinds)).Append('|')
            .Append(string.Join(',', handoff.AccessLineage)).Append('|')
            .Append(string.Join(',', handoff.AccessBindings.Select(binding => binding.Member + "#" + binding.CanonicalSyntaxSha256))).Append('\n');
        return new SemanticPlan(stageIds, operationIds, branchIds, gasFieldIds, snapshotIds, adapterIds, controlFlowIds, effectIds,
            orderedOperations[^1].Id, Sha256(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void AppendPlanPart(StringBuilder builder, string name, IEnumerable<string> values)
    {
        builder.Append(name).Append(':');
        foreach (string value in values) builder.Append(value).Append('|');
        builder.Append('\n');
    }

    private static CompositionDependency[] BuildComposition(DependencyIdentity[] dependencies)
    {
        List<CompositionDependency> result = [];
        foreach (DependencyIdentity dependency in dependencies)
        {
            if (dependency.Kind == "compiler-reference") continue;
            string shape = dependency.Kind == "proof" ? "typed-theorem-identity-required" : "generated-artifact-identity-required";
            string[] fields = dependency.Name switch
            {
                "authorization-fold" or "authorization-fold-refinement" or "state-gas-charge" or "state-gas-transition" => ["gas.Value", "gas.StateReservoir", "gas.StateGasUsed", "gas.StateGasSpill", "gas.StateGasSpillRefunded"],
                "world-journal" => ["physicalExistence", "logicalExistence", "originalFacts", "currentFacts"],
                "frame-journal" or "frame-control" => ["snapshot", "status", "error", "gas"],
                "ordinary-post-nonce-ir" or "ordinary-post-nonce-manifest" or "ordinary-post-nonce-lean" or "ordinary-post-nonce-reference" or "ordinary-post-nonce-refinement" => ["EvmHandoff"],
                _ => ["source-bound-adapter"],
            };
            string id = dependency.Name.Replace("-", "_", StringComparison.Ordinal);
            string theorem = dependency.Theorems.Length == 0 ? dependency.Binding : dependency.Theorems[0];
            result.Add(new CompositionDependency(id, dependency.Kind, dependency.Path, dependency.Sha256, theorem, shape, fields));
        }

        return result.ToArray();
    }

    private static SourceFile FindSource(SourceFile[] sources, string path)
    {
        SourceFile[] matches = sources.Where(source => source.RelativePath == path).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ExtractionException($"Required admitted source is missing: {path}."),
            _ => throw new ExtractionException($"Required admitted source is ambiguous: {path}."),
        };
    }

    private static string TypeOwner(TypeDeclarationSyntax type)
    {
        List<string> containingTypes = type.AncestorsAndSelf().OfType<TypeDeclarationSyntax>()
            .Reverse()
            .Select(declaration => declaration.Identifier.ValueText + TypeParameters(declaration))
            .ToList();
        List<string> namespaces = type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse()
            .Select(namespaceDeclaration => Canonical(namespaceDeclaration.Name))
            .ToList();
        string owner = string.Join('.', containingTypes);
        string namespaceName = string.Join('.', namespaces);
        return namespaceName.Length == 0 ? owner : namespaceName + "." + owner;
    }

    private static string OwnerPath(MethodDeclarationSyntax method)
    {
        TypeDeclarationSyntax type = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()
            ?? throw new ExtractionException($"Method {method.Identifier.ValueText} has no containing type.");
        return TypeOwner(type);
    }

    private static string TypeParameters(TypeDeclarationSyntax declaration)
    {
        if (declaration.TypeParameterList is null) return string.Empty;
        return "<" + string.Join(',', declaration.TypeParameterList.Parameters.Select(static parameter => parameter.Identifier.ValueText)) + ">";
    }

    private static string MethodTypeParameters(MethodDeclarationSyntax method)
    {
        if (method.TypeParameterList is null) return string.Empty;
        return "<" + string.Join(',', method.TypeParameterList.Parameters.Select(static parameter => parameter.Identifier.ValueText)) + ">";
    }

    private static string SignatureOf(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax method => method.Identifier.ValueText + MethodTypeParameters(method) + Canonical(method.ParameterList),
        TypeDeclarationSyntax type => type.Identifier.ValueText + TypeParameters(type),
        PropertyDeclarationSyntax property => property.Identifier.ValueText + ":" + Canonical(property.Type),
        VariableDeclaratorSyntax variable => variable.Identifier.ValueText,
        _ => node.Kind().ToString(),
    };

    private static int StatementOrdinal(SyntaxNode node)
    {
        StatementSyntax? statement = node.AncestorsAndSelf().OfType<StatementSyntax>().LastOrDefault();
        if (statement?.Parent is not BlockSyntax block) return 0;
        for (int index = 0; index < block.Statements.Count; index++)
        {
            if (block.Statements[index] == statement || node.FullSpan.Start >= block.Statements[index].FullSpan.Start && node.FullSpan.End <= block.Statements[index].FullSpan.End) return index + 1;
        }

        return 0;
    }

    private static string ControlFlowPath(SyntaxNode node)
    {
        List<string> path = [];
        foreach (SyntaxNode ancestor in node.Ancestors().Reverse())
        {
            switch (ancestor)
            {
                case IfStatementSyntax:
                    path.Add("if");
                    break;
                case ForEachStatementSyntax:
                    path.Add("foreach");
                    break;
                case ConditionalExpressionSyntax:
                    path.Add("conditional");
                    break;
                case ReturnStatementSyntax:
                    path.Add("return");
                    break;
            }
        }

        return string.Join('/', path);
    }

    private static string InvocationReceiver(InvocationExpressionSyntax invocation) => invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => Canonical(member.Expression).TrimEnd('!'),
            MemberBindingExpressionSyntax memberBinding => Canonical(memberBinding),
            GenericNameSyntax generic => Canonical(generic),
            _ => string.Empty,
        };

    private static string TokenFingerprint(SyntaxNode node)
    {
        StringBuilder builder = new();
        foreach (SyntaxToken token in node.DescendantTokens(descendIntoTrivia: false))
        {
            builder.Append(token.RawKind).Append(':').Append(token.Text.Length).Append(':').Append(token.Text).Append('\n');
        }

        return Sha256(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static string Canonical(SyntaxNode node) =>
        string.Concat(node.DescendantTokens(descendIntoTrivia: false).Select(static token => token.Text));

    private static string StrictUtf8(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ExtractionException($"The admission closure is not strict UTF-8: {exception.Message}");
        }
    }

    private static string RequireSha256(string value, string field)
    {
        if (!IsSha256(value)) throw new ExtractionException($"The {field} row is not a SHA-256 identity.");
        return value.ToLowerInvariant();
    }

    private static bool IsSha256(string? value) => value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string CombinedSourceHash(SourceIdentity[] sources)
    {
        StringBuilder builder = new();
        foreach (SourceIdentity source in sources)
        {
            builder.Append(source.Role).Append('\n').Append(source.Path).Append('\n').Append(source.Sha256).Append('\n').Append(source.SyntaxSha256).Append('\n');
        }

        return Sha256(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static string CombinedMemberHash(MemberIdentity[] members)
    {
        StringBuilder builder = new();
        foreach (MemberIdentity member in members)
        {
            builder.Append(member.Path).Append('\n').Append(member.OwnerPath).Append('\n').Append(member.Name).Append('\n').Append(member.TokenSha256).Append('\n');
        }

        return Sha256(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static string CombinedBindingHash(SourceBinding[] bindings)
    {
        StringBuilder builder = new();
        foreach (SourceBinding binding in bindings)
        {
            builder.Append(binding.Path).Append('\n').Append(binding.Member).Append('\n').Append(binding.OperationKind).Append('\n').Append(binding.CanonicalSyntaxSha256).Append('\n').Append(binding.ControlFlowBlock).Append('\n');
        }

        return Sha256(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static void EnsureWithin(string parent, string child)
    {
        string parentPath = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string childPath = Path.GetFullPath(child);
        if (!childPath.StartsWith(parentPath, StringComparison.OrdinalIgnoreCase)) throw new ExtractionException($"Path escaped repository root: {childPath}.");
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static void CompareFiles(string expected, string actual)
    {
        if (!File.Exists(expected)) throw new ExtractionException($"Missing checked-in artifact: {expected}.");
        byte[] left = File.ReadAllBytes(expected);
        byte[] right = File.ReadAllBytes(actual);
        if (!left.AsSpan().SequenceEqual(right)) throw new ExtractionException($"Generated artifact drift: {expected}.");
    }

    private static byte[] Serialize<T>(T value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        string normalized = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n";
        return new UTF8Encoding(false).GetBytes(normalized);
    }

    private static IrDocument DeserializeIr(byte[] bytes)
    {
        try
        {
            IrDocument document = JsonSerializer.Deserialize<IrDocument>(bytes, JsonOptions)
                ?? throw new ExtractionException("The serialized transaction-preparation IR is empty.");
            ValidateIr(document);
            return document;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"The serialized transaction-preparation IR is invalid: {exception.Message}");
        }
    }

    private static Manifest DeserializeManifest(byte[] bytes)
    {
        try
        {
            Manifest manifest = JsonSerializer.Deserialize<Manifest>(bytes, JsonOptions)
                ?? throw new ExtractionException("The serialized transaction-preparation manifest is empty.");
            ValidateManifest(manifest);
            return manifest;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"The serialized transaction-preparation manifest is invalid: {exception.Message}");
        }
    }

    private static void ValidateRoundTrip<T>(T expected, T actual)
    {
        byte[] expectedBytes = Serialize(expected);
        byte[] actualBytes = Serialize(actual);
        if (!expectedBytes.AsSpan().SequenceEqual(actualBytes)) throw new ExtractionException("Serialization is not deterministic after round-trip.");
    }

    private static IrDocument BuildDocument(
        AdmissionClosure closure,
        SourceIdentity[] sources,
        DependencyIdentity[] dependencies,
        CompilerReferenceClosure references,
        SemanticProfile profile,
        CompositionDependency[] composition) => new(
            SchemaVersion,
            ExtractorVersion,
            Kernel,
            AcceptanceState,
            "Amsterdam",
            "EthereumGasPolicy",
            "TransactionProcessorBase<TGasPolicy>.ExecuteEvmTransaction",
            closure.Sha256,
            CombinedSourceHash(sources),
            sources,
            dependencies,
            references.Identities,
            composition,
            profile,
            [
                "EthereumGasPolicy has exactly Value:ulong, StateReservoir:long, StateGasUsed:long, StateGasSpill:long, StateGasSpillRefunded:long.",
                "Authorization state-gas charging is ordered after IsValidForExecution and may return false with prior writes/charges visible.",
                "The conditional production entry adapter binds a fresh StackAccessTracker/tracer access projection, zero delegation refunds, entry/pre-execution world facts, the opaque source Snapshot.Empty/TakeSnapshot projection, source-bound code/environment rent, and the EthereumGasPolicy schedule.",
                "EIP-8037 preparation OOG restores preExecutionSnapshot and prePreparationGas before top-frame halt handling.",
                "Top-level CREATE classifies logical existence/collision and charges create state gas before PayValue.",
                "RentTopLevel is the final admitted operation; VirtualMachine.ExecuteTransaction is represented only as a typed boundary identity.",
            ],
            [
                "Roslyn semantic model, IOperation, CFG, metadata identity, and source byte identity are obligations for the finite lowering.",
                "World-state, code repository, gas policy, cryptography, and VM execution are effectful production interfaces; this package does not reimplement them.",
                "The eventual OrdinaryPostNonceDispatchExtractor EvmHandoff is checked only by the exact artifact and theorem identities recorded here; no cross-package result equality is claimed.",
            ],
            [
                "VirtualMachine.ExecuteTransaction body, opcode dispatch, frame execution, frame/tail settlement, refund settlement, fees, receipts, and block finalization.",
                SignedArithmeticResidual,
                "Unreviewed source, alternate forks, alternate DI registrations, arbitrary caller predicates, substring grammar, byte-pin-only semantic claims.",
             ]);

    private static MemberIdentity[] AdmitMembers(SourceFile[] sources, SemanticContext context, SourceBinding[] bindings)
    {
        SourceFile processor = FindSource(sources, TransactionProcessorPath);
        MethodDeclarationSyntax[] methods =
        [
            RequireMethod(processor, BoundaryMember, 16, 0),
            RequireMethod(processor, "ValidateStatic", 5, 0),
            RequireMethod(processor, "ProcessDelegations", 6, 0),
            RequireMethod(processor, "IsValidForExecution", 5, 0),
            RequireMethod(processor, "BuildExecutionEnvironment", 10, 0),
            RequireMethod(processor, "ExecuteEvmCall", 14, 1),
            RequireMethod(processor, "PrepareDeployment", 3, 0),
        ];
        List<MemberIdentity> members = [];
        foreach (MethodDeclarationSyntax method in methods)
        {
            SourceBinding binding = BindMethodDeclaration(processor, method, "member." + method.Identifier.ValueText, context);
            members.Add(new MemberIdentity(processor.RelativePath, "Nethermind.Evm.TransactionProcessing", OwnerPath(method), "method", method.Identifier.ValueText,
                method.TypeParameterList?.Parameters.Count ?? 0, Canonical(method.ParameterList), method.Kind().ToString(), TokenFingerprint(method), binding));
        }

        if (bindings.Any(binding => !sources.Any(source => source.RelativePath == binding.Path)))
            throw new ExtractionException("A source binding escaped the reviewed source closure.");
        return members.ToArray();
    }

    private static SourceBinding BindMethodDeclaration(SourceFile source, MethodDeclarationSyntax method, string member, SemanticContext context)
    {
        SemanticModel model = context.Model(source.Tree);
        IMethodSymbol symbol = model.GetDeclaredSymbol(method)
            ?? throw new ExtractionException($"Declared symbol for member {method.Identifier.ValueText} is missing.");
        if (HasErrorSymbol(symbol)) throw new ExtractionException($"Declared member {method.Identifier.ValueText} has an error symbol.");
        FileLinePositionSpan span = method.GetLocation().GetLineSpan();
        return new SourceBinding(source.RelativePath, OwnerPath(method), member, SignatureOf(method), method.Kind().ToString(), "MethodBodyOperation",
            Canonical(method), TokenFingerprint(method), Sha256(Encoding.UTF8.GetBytes(Canonical(method))), SignatureOf(method), string.Empty,
            symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), symbol.Kind.ToString(), symbol.ContainingAssembly.Identity.Name,
            [],
            [],
            CandidateReason.None.ToString(), false, false, 0, "declaration", -1, true,
            span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1, span.EndLinePosition.Character + 1);
    }

    private static void ValidateIr(IrDocument document)
    {
        if (document.SchemaVersion != SchemaVersion || document.ExtractorVersion != ExtractorVersion || document.Kernel != Kernel ||
            document.AcceptanceState != AcceptanceState || document.TargetFork != "Amsterdam" || document.TargetGasPolicy != "EthereumGasPolicy" ||
            document.Sources is null || document.Dependencies is null || document.CompilerReferences is null || document.Composition is null ||
            document.Semantics is null || !IsSha256(document.AdmissionSha256) || !IsSha256(document.SourceClosureSha256))
        {
            throw new ExtractionException("The transaction-preparation IR header changed.");
        }

        if (!string.Equals(document.AdmissionSha256, AdmissionClosureSha256, StringComparison.Ordinal) ||
            document.Sources.Any(source => source is null || !IsSha256(source.Sha256) || !IsSha256(source.SyntaxSha256)) ||
            !string.Equals(document.SourceClosureSha256, CombinedSourceHash(document.Sources), StringComparison.Ordinal) ||
            document.Sources.Length == 0 || document.Dependencies.Length == 0 || document.Semantics.Stages is null ||
            document.Semantics.GasFields is null || document.Semantics.Snapshots is null || document.Semantics.Operations is null ||
            document.Semantics.Branches is null || document.Semantics.Bindings is null || document.Semantics.Handoff is null ||
            document.Semantics.ControlFlows is null || document.Semantics.Plan is null || document.Semantics.AdapterPremises is null || document.Semantics.Environment is null ||
            document.Semantics.Stages.Length != 5 || document.Semantics.ControlFlows.Length != 7 ||
            document.Semantics.GasFields.Length != 5 || document.Semantics.Snapshots.Length != 2 || document.Semantics.Operations.Length == 0 ||
            document.Semantics.Branches.Length == 0 || document.Semantics.Handoff.BoundaryCall != "VirtualMachine.ExecuteTransaction")
        {
            throw new ExtractionException("The transaction-preparation IR shape is incomplete.");
        }

        if (document.Exclusions is null || !document.Exclusions.Contains(SignedArithmeticResidual, StringComparer.Ordinal))
            throw new ExtractionException("The production signed-state-gas arithmetic residual must remain explicit.");

        string[] gasFields = document.Semantics.GasFields.Select(static field => field.Name).ToArray();
        if (!gasFields.SequenceEqual(["Value", "StateReservoir", "StateGasUsed", "StateGasSpill", "StateGasSpillRefunded"], StringComparer.Ordinal))
            throw new ExtractionException("The IR does not preserve the five EthereumGasPolicy fields in source order.");
        foreach (GasFieldIdentity field in document.Semantics.GasFields)
        {
            if (field.Owner != "Nethermind.Evm.GasPolicy.EthereumGasPolicy" || field.Width != 64 ||
                field.Representation is not ("UInt64" or "Int64") || field.FixedWidthOperations is null || field.FixedWidthOperations.Length < 3 ||
                field.SourceType is not ("ulong" or "long"))
                throw new ExtractionException($"The IR has incomplete fixed-width evidence for gas field {field.Name}.");
            if (field.Name == "Value" && field.Representation != "UInt64" || field.Name != "Value" && field.Representation != "Int64")
                throw new ExtractionException($"The IR has an invalid fixed-width representation for gas field {field.Name}.");
            ValidateBinding(field.Binding);
        }
        SemanticFormula[] formulas = document.Semantics.Operations.Select(static operation => operation.Formula).ToArray();
        foreach (SemanticFormula required in Enum.GetValues<SemanticFormula>())
        {
            if (!formulas.Any(formula => formula == required)) throw new ExtractionException($"The IR is missing required semantic formula {required}.");
        }
        SemanticEffect[] effects = document.Semantics.Operations.SelectMany(static operation => operation.Effects).Distinct().ToArray();
        foreach (SemanticEffect required in Enum.GetValues<SemanticEffect>())
        {
            if (!effects.Any(effect => effect == required)) throw new ExtractionException($"The IR is missing required semantic effect {required}.");
        }

        if (document.Semantics.Operations[^1].Formula != SemanticFormula.ExecuteTransactionBoundary ||
            !document.Semantics.Operations[^1].Effects.Any(effect => effect == SemanticEffect.StopsBeforeVmExecution))
            throw new ExtractionException("The IR boundary is not the operation immediately before VirtualMachine.ExecuteTransaction.");
        if (!document.Semantics.ExcludedOperations.Any(static exclusion => exclusion.IndexOf("VM execution", StringComparison.OrdinalIgnoreCase) >= 0))
            throw new ExtractionException("The IR exclusions do not state the VM execution boundary.");
        foreach (SourceBinding binding in document.Semantics.Bindings)
        {
            ValidateBinding(binding);
            if (!document.Sources.Any(source => source.Path == binding.Path))
                throw new ExtractionException($"The IR binding escaped the admitted source closure: {binding.Path}.");
        }
        foreach (OperationIdentity operation in document.Semantics.Operations)
        {
            if (string.IsNullOrWhiteSpace(operation.Id) || string.IsNullOrWhiteSpace(operation.Expression) ||
                string.IsNullOrWhiteSpace(operation.Expected) || operation.SourceOperands is null ||
                operation.SourceOperands.Any(string.IsNullOrWhiteSpace) || operation.Reads is null ||
                operation.Writes is null || operation.Effects is null || operation.Effects.Length == 0)
                throw new ExtractionException($"Operation {operation.Id} has incomplete normalized semantic evidence.");
            ValidateBinding(operation.Binding);
            if (!document.Semantics.Bindings.Any(binding => binding.Member == operation.Binding.Member &&
                    binding.CanonicalSyntaxSha256 == operation.Binding.CanonicalSyntaxSha256 &&
                    binding.TargetSymbol == operation.Binding.TargetSymbol && binding.Receiver == operation.Binding.Receiver))
                throw new ExtractionException($"Operation {operation.Id} is not backed by its admitted semantic binding.");
        }
        foreach (BranchIdentity branch in document.Semantics.Branches)
        {
            ValidateBinding(branch.Binding);
            if (!document.Semantics.Bindings.Any(binding => binding.Member == branch.Binding.Member &&
                    binding.CanonicalSyntaxSha256 == branch.Binding.CanonicalSyntaxSha256))
                throw new ExtractionException($"Branch {branch.Id} is not backed by its admitted semantic binding.");
        }
        foreach (AdapterPremise adapter in document.Semantics.AdapterPremises)
        {
            if (adapter is null || string.IsNullOrWhiteSpace(adapter.Id) || string.IsNullOrWhiteSpace(adapter.Domain) ||
                string.IsNullOrWhiteSpace(adapter.Projection) || string.IsNullOrWhiteSpace(adapter.Assumption) ||
                adapter.RequiredIdentities is null || adapter.RequiredIdentities.Length == 0 ||
                adapter.RequiredIdentities.Any(string.IsNullOrWhiteSpace) || adapter.Bindings is null || adapter.Bindings.Length == 0)
                throw new ExtractionException("The normalized adapter premise is incomplete.");
            foreach (SourceBinding binding in adapter.Bindings)
            {
                ValidateBinding(binding);
                if (!document.Semantics.Bindings.Any(candidate => candidate.Member == binding.Member &&
                        candidate.CanonicalSyntaxSha256 == binding.CanonicalSyntaxSha256 && candidate.TargetSymbol == binding.TargetSymbol &&
                        candidate.Receiver == binding.Receiver))
                    throw new ExtractionException($"Adapter {adapter.Id} contains a binding outside the admitted binding set.");
            }
        }
        AdapterPremise? sourceEntry = document.Semantics.AdapterPremises.FirstOrDefault(adapter => adapter.Id == "source-entry");
        if (sourceEntry is null || !sourceEntry.RequiredIdentities.SequenceEqual(
                ["StackAccessTracker", "tracer.IsTracingAccess", "delegationRefunds=0", "WorldState", "ICodeInfoRepository", "ExecutionEnvironment.Rent",
                    "prePreparationGas", "executionIntrinsicGasStandard", "Snapshot.Empty", "Snapshot.EmptyPosition=-1", "Transaction.HasAuthorizationList:Type==TxType.SetCode", "TransactionExtensions.GetRecipient", "hasPreExecutionSnapshot=(eip7702&&hasAuthorization&&eip8037)", "accessTracker->ExecuteEvmCall.accessedItems->VmState.RentTopLevel", "EthereumGasPolicy", "EthereumGasPolicy.Value",
                    "EthereumGasPolicy.StateReservoir", "EthereumGasPolicy.StateGasUsed", "EthereumGasPolicy.StateGasSpill", "EthereumGasPolicy.StateGasSpillRefunded"], StringComparer.Ordinal) ||
            sourceEntry.Bindings.Length != 20 ||
            !sourceEntry.Bindings.Select(static binding => binding.Member).SequenceEqual(
                ["setTransactionContext", "allocateStackAccessTracker", "capturePreparationGas", "captureExecutionIntrinsicGasStandard",
                    "captureDelegationRefunds", "initializePreparationSnapshot", "capturePreExecutionSnapshot", "gasField.Value",
                    "gasField.StateReservoir", "gasField.StateGasUsed", "gasField.StateGasSpill", "gasField.StateGasSpillRefunded",
                    "snapshot.authorizationPresence", "snapshot.preparationPresence", "snapshot.emptyPosition", "transaction.hasAuthorizationListSetCode", "transaction.getRecipient",
                    "handoff.executeEvmCall.access.off", "handoff.executeEvmCall.access.on", "environment.rent"], StringComparer.Ordinal))
            throw new ExtractionException("The source-entry adapter does not bind fresh access, zero refunds, world, code, snapshot, and gas-schedule facts.");
        SourceBinding? authorizationListBinding = document.Semantics.Bindings.SingleOrDefault(binding => binding.Member == "authorizationListNonEmpty");
        if (authorizationListBinding is null || authorizationListBinding.TargetSymbol !=
                "Nethermind.Core.Validation.SetCodeTxValidation.ValidateAuthorizationList(Nethermind.Core.Transaction)" ||
            authorizationListBinding.TargetAssembly != "Nethermind.Core")
            throw new ExtractionException("The IR does not retain the source-bound SetCode authorization-list nonempty validation.");
        foreach (SnapshotIdentity snapshot in document.Semantics.Snapshots) ValidateBinding(snapshot.Binding);
        foreach (ControlFlowIdentity controlFlow in document.Semantics.ControlFlows)
        {
            if (controlFlow is null || string.IsNullOrWhiteSpace(controlFlow.Id) || controlFlow.ReachableBlocks is null || controlFlow.ReachableBlocks.Length == 0 ||
                controlFlow.Edges is null || controlFlow.Edges.Length == 0 ||
                controlFlow.NormalExitBlocks is null || controlFlow.ExceptionalExitBlocks is null || controlFlow.BackEdges is null || controlFlow.LoopHeaders is null ||
                controlFlow.NormalExitBlocks.Intersect(controlFlow.ExceptionalExitBlocks).Any() ||
                controlFlow.Id == "processDelegations" && controlFlow.BackEdges.Length == 0)
                throw new ExtractionException("The IR control-flow admission is incomplete or contains an ambiguous exit classification.");
            ValidateBinding(controlFlow.Binding);
        }
        if (document.Semantics.Handoff.BoundaryBindings is null || document.Semantics.Handoff.BoundaryBindings.Length != 3 ||
            document.Semantics.Handoff.AccessLineage is null || document.Semantics.Handoff.AccessLineage.Length != 3 ||
            document.Semantics.Handoff.AccessBindings is null || document.Semantics.Handoff.AccessBindings.Length != 3 ||
            document.Semantics.Handoff.RentArguments is null || document.Semantics.Handoff.RentRefKinds is null ||
            document.Semantics.Handoff.VmArguments is null || document.Semantics.Handoff.VmRefKinds is null ||
            document.Semantics.Handoff.RentArguments.Length != 5 || document.Semantics.Handoff.RentRefKinds.Length != 5 ||
            document.Semantics.Handoff.VmArguments.Length != 2 || document.Semantics.Handoff.VmRefKinds.Length != 6 ||
            document.Semantics.Handoff.RentOwner != "Nethermind.Evm.VmState<TGasPolicy>" ||
            document.Semantics.Handoff.VmOwner != "Nethermind.Evm.IVirtualMachine<TGasPolicy>" ||
            document.Semantics.Handoff.Fields is null ||
            !document.Semantics.Handoff.Fields.SequenceEqual(["gasAvailable", "executionIntrinsicGasStandard", "executionType", "env", "accessedItems", "snapshot", "WorldState", "tracer", "opts", "hasPreExecutionSnapshot"], StringComparer.Ordinal) ||
            document.Semantics.Handoff.RentReceiver != "VmState<TGasPolicy> :: " ||
            string.IsNullOrWhiteSpace(document.Semantics.Handoff.VmReceiver) ||
            document.Semantics.Handoff.VmReceiver.Split(" || ", StringSplitOptions.None).Any(receiver => receiver != "VirtualMachine :: Nethermind.Evm.IVirtualMachine<TGasPolicy>") ||
            string.IsNullOrWhiteSpace(document.Semantics.Handoff.RentOverload) || string.IsNullOrWhiteSpace(document.Semantics.Handoff.VmOverloads) ||
            !document.Semantics.Handoff.RentArguments.SequenceEqual(["gas", "executionType", "env", "accessedItems", "snapshot"], StringComparer.Ordinal) ||
            !document.Semantics.Handoff.RentRefKinds.SequenceEqual(["None", "None", "None", "In", "In"], StringComparer.Ordinal) ||
            !document.Semantics.Handoff.VmArguments.SequenceEqual(["state,worldState,txTracer", "state,worldState,txTracer"], StringComparer.Ordinal) ||
            document.Semantics.Handoff.VmRefKinds.Any(kind => kind != "None") ||
            !document.Semantics.Handoff.AccessBindings.Select(static binding => binding.Member).SequenceEqual(
                ["handoff.executeEvmCall.access.off", "handoff.executeEvmCall.access.on", "handoff.rentTopLevel"], StringComparer.Ordinal) ||
            string.IsNullOrWhiteSpace(document.Semantics.Handoff.AccessLineage[0]) ||
            !document.Semantics.Handoff.AccessLineage[0].StartsWith("fresh-local:accessTracker:", StringComparison.Ordinal) ||
            !document.Semantics.Handoff.AccessLineage[1].StartsWith("ExecuteEvmTransaction.accessTracker->", StringComparison.Ordinal) ||
            !document.Semantics.Handoff.AccessLineage[2].StartsWith(
                document.Semantics.Handoff.AccessLineage[1]["ExecuteEvmTransaction.accessTracker->".Length..] + "->", StringComparison.Ordinal) ||
            !document.Semantics.Handoff.AccessLineage[2].EndsWith(
                "::accessedItems:Nethermind.Evm.StackAccessTracker:In", StringComparison.Ordinal))
            throw new ExtractionException("The VM handoff lacks complete owner, overload, receiver, argument, or ref-kind identity.");
        foreach (SourceBinding boundaryBinding in document.Semantics.Handoff.BoundaryBindings)
        {
            ValidateBinding(boundaryBinding);
            if (!document.Semantics.Bindings.Any(candidate => candidate.Member == boundaryBinding.Member &&
                    candidate.CanonicalSyntaxSha256 == boundaryBinding.CanonicalSyntaxSha256 &&
                    candidate.TargetSymbol == boundaryBinding.TargetSymbol && candidate.Receiver == boundaryBinding.Receiver))
                throw new ExtractionException("The VM handoff boundary binding is outside the admitted semantic binding set.");
        }
        foreach (SourceBinding accessBinding in document.Semantics.Handoff.AccessBindings)
        {
            ValidateBinding(accessBinding);
            if (!document.Semantics.Bindings.Any(candidate => candidate.Member == accessBinding.Member &&
                    candidate.CanonicalSyntaxSha256 == accessBinding.CanonicalSyntaxSha256 &&
                    candidate.TargetSymbol == accessBinding.TargetSymbol && candidate.Receiver == accessBinding.Receiver))
                throw new ExtractionException("The VM handoff access binding is outside the admitted semantic binding set.");
        }
        foreach (DependencyIdentity dependency in document.Dependencies)
        {
            if (dependency is null || string.IsNullOrWhiteSpace(dependency.Path) || !IsSha256(dependency.Sha256))
                throw new ExtractionException("The IR contains an incomplete dependency identity.");
        }
        ValidateCompilerReferenceIdentities(document.CompilerReferences);
        foreach (CompositionDependency dependency in document.Composition)
        {
            if (dependency is null || string.IsNullOrWhiteSpace(dependency.Id) || string.IsNullOrWhiteSpace(dependency.ArtifactPath) ||
                !IsSha256(dependency.ArtifactSha256) || string.IsNullOrWhiteSpace(dependency.Theorem) || string.IsNullOrWhiteSpace(dependency.TheoremShape) ||
                dependency.RequiredFields is null || dependency.RequiredFields.Length == 0 ||
                dependency.RequiredFields.Any(string.IsNullOrWhiteSpace))
                throw new ExtractionException("The IR contains an incomplete composition identity.");
        }
        CompositionDependency[] expectedComposition = BuildComposition(document.Dependencies);
        if (document.Composition.Length != expectedComposition.Length || document.Composition.Select(static dependency => dependency.Id).Distinct(StringComparer.Ordinal).Count() != document.Composition.Length ||
            document.Composition.Where((dependency, index) =>
                dependency.Id != expectedComposition[index].Id || dependency.Kind != expectedComposition[index].Kind ||
                dependency.ArtifactPath != expectedComposition[index].ArtifactPath || dependency.ArtifactSha256 != expectedComposition[index].ArtifactSha256 ||
                dependency.Theorem != expectedComposition[index].Theorem || dependency.TheoremShape != expectedComposition[index].TheoremShape ||
                !dependency.RequiredFields.SequenceEqual(expectedComposition[index].RequiredFields, StringComparer.Ordinal)).Any())
            throw new ExtractionException("The serialized composition closure is not derived from the accepted dependency identities.");
        if (document.Semantics.Plan.StageIds is null || document.Semantics.Plan.OperationIds is null || document.Semantics.Plan.BranchIds is null ||
            document.Semantics.Plan.GasFieldIds is null || document.Semantics.Plan.SnapshotIds is null || document.Semantics.Plan.AdapterIds is null ||
            document.Semantics.Plan.ControlFlowIds is null || document.Semantics.Plan.EffectIds is null || !IsSha256(document.Semantics.Plan.Sha256))
            throw new ExtractionException("The serialized normalized semantic plan is incomplete.");
        SemanticPlan expectedPlan = BuildSemanticPlan(document.Semantics.Stages.ToList(), document.Semantics.Branches.ToList(),
            document.Semantics.Operations.ToList(), document.Semantics.GasFields, document.Semantics.Snapshots, document.Semantics.Environment, document.Semantics.Handoff,
            document.Semantics.ControlFlows, document.Semantics.AdapterPremises.ToList());
        if (!document.Semantics.Plan.StageIds.SequenceEqual(expectedPlan.StageIds, StringComparer.Ordinal) ||
            !document.Semantics.Plan.OperationIds.SequenceEqual(expectedPlan.OperationIds, StringComparer.Ordinal) ||
            !document.Semantics.Plan.BranchIds.SequenceEqual(expectedPlan.BranchIds, StringComparer.Ordinal) ||
            !document.Semantics.Plan.GasFieldIds.SequenceEqual(expectedPlan.GasFieldIds, StringComparer.Ordinal) ||
            !document.Semantics.Plan.SnapshotIds.SequenceEqual(expectedPlan.SnapshotIds, StringComparer.Ordinal) ||
            !document.Semantics.Plan.AdapterIds.SequenceEqual(expectedPlan.AdapterIds, StringComparer.Ordinal) ||
            !document.Semantics.Plan.ControlFlowIds.SequenceEqual(expectedPlan.ControlFlowIds, StringComparer.Ordinal) ||
            !document.Semantics.Plan.EffectIds.SequenceEqual(expectedPlan.EffectIds, StringComparer.Ordinal) ||
            document.Semantics.Plan.BoundaryOperationId != expectedPlan.BoundaryOperationId ||
            document.Semantics.Plan.Sha256 != expectedPlan.Sha256)
            throw new ExtractionException("The serialized normalized semantic plan is not source-derived.");
        ValidateProfileOrder(document.Semantics);
    }

    private static void ValidateProfileOrder(SemanticProfile profile)
    {
        int[] stageOrdinals = profile.Stages.Select(static stage => stage.Ordinal).ToArray();
        if (!stageOrdinals.SequenceEqual(Enumerable.Range(0, profile.Stages.Length))) throw new ExtractionException("Stage ordinals are not canonical.");
        int[] operationOrdinals = profile.Operations.Select(static operation => operation.Ordinal).ToArray();
        if (!operationOrdinals.SequenceEqual(Enumerable.Range(0, profile.Operations.Length))) throw new ExtractionException("Operation ordinals are not canonical.");
        int[] branchOrdinals = profile.Branches.Select(static branch => branch.Ordinal).ToArray();
        if (!branchOrdinals.SequenceEqual(Enumerable.Range(0, profile.Branches.Length))) throw new ExtractionException("Branch ordinals are not canonical.");
        if (profile.Operations.Count(operation => operation.Formula == SemanticFormula.ExecuteTransactionBoundary) != 1)
            throw new ExtractionException("The VM boundary operation is not unique.");
        string[] controlFlowIds = profile.ControlFlows.Select(static flow => flow.Id).ToArray();
        if (!controlFlowIds.SequenceEqual(["executeEvmTransaction", "validateStatic", "processDelegations", "isValidForExecution", "buildExecutionEnvironment", "executeEvmCall", "prepareDeployment"], StringComparer.Ordinal))
            throw new ExtractionException("Control-flow members are not admitted in canonical boundary order.");
        if (profile.ControlFlows.Any(static flow => flow.ReachableBlocks.Length == 0 || flow.NormalExitBlocks.Intersect(flow.ExceptionalExitBlocks).Any()))
            throw new ExtractionException("An admitted control-flow identity has incomplete reachable exit evidence.");
        if (profile.Branches.Any(static branch => branch.Binding.ControlFlowBlock < 0 || !branch.Binding.IsReachable))
            throw new ExtractionException("An admitted branch is not reachable in the production CFG.");
        if (profile.Operations.Any(static operation => operation.Binding.ControlFlowBlock < 0 || !operation.Binding.IsReachable))
            throw new ExtractionException("An admitted operation is not reachable in the production CFG.");
    }

    private static void ValidateManifest(Manifest manifest)
    {
        if (manifest.SchemaVersion != SchemaVersion || manifest.ExtractorVersion != ExtractorVersion || manifest.Kernel != Kernel ||
            manifest.AcceptanceState != AcceptanceState || manifest.AdmissionSha256 != AdmissionClosureSha256 || !IsSha256(manifest.AdmissionSha256) || !IsSha256(manifest.SourceClosureSha256) ||
            !IsSha256(manifest.CombinedSourceSha256) || !IsSha256(manifest.CombinedMemberSha256) || !IsSha256(manifest.CombinedBindingSha256) ||
            !IsSha256(manifest.SemanticIrSha256) || manifest.Sources is null || manifest.Dependencies is null || manifest.CompilerReferences is null ||
            manifest.Composition is null || manifest.Members is null || manifest.Bindings is null || manifest.Ir is null || manifest.Lean is null)
        {
            throw new ExtractionException("The transaction-preparation manifest header changed.");
        }

        foreach (SourceBinding binding in manifest.Bindings) ValidateBinding(binding);
        foreach (SourceIdentity source in manifest.Sources)
        {
            if (!IsSha256(source.Sha256) || !IsSha256(source.SyntaxSha256)) throw new ExtractionException("The manifest has an invalid source identity.");
        }
        foreach (DependencyIdentity dependency in manifest.Dependencies)
        {
            if (dependency is null || !IsSha256(dependency.Sha256)) throw new ExtractionException("The manifest has an invalid dependency identity.");
        }
        ValidateCompilerReferenceIdentities(manifest.CompilerReferences);
        if (!string.Equals(manifest.SourceClosureSha256, CombinedSourceHash(manifest.Sources), StringComparison.Ordinal) ||
            !string.Equals(manifest.CombinedSourceSha256, CombinedSourceHash(manifest.Sources), StringComparison.Ordinal) ||
            !string.Equals(manifest.CombinedMemberSha256, CombinedMemberHash(manifest.Members), StringComparison.Ordinal) ||
            !string.Equals(manifest.CombinedBindingSha256, CombinedBindingHash(manifest.Bindings), StringComparison.Ordinal) ||
            !string.Equals(manifest.Ir.Path, IrFileName, StringComparison.Ordinal) ||
            !string.Equals(manifest.Lean.Path, Normalize(DefaultLeanPath), StringComparison.Ordinal) ||
            !IsSha256(manifest.Ir.Sha256) || !IsSha256(manifest.Lean.Sha256))
            throw new ExtractionException("The manifest artifact identities are incomplete or inconsistent.");
    }

    private static void ValidateBinding(SourceBinding binding)
    {
        if (string.IsNullOrWhiteSpace(binding.Path) || string.IsNullOrWhiteSpace(binding.Owner) || string.IsNullOrWhiteSpace(binding.Member) ||
            string.IsNullOrWhiteSpace(binding.Signature) || string.IsNullOrWhiteSpace(binding.NodeKind) || string.IsNullOrWhiteSpace(binding.OperationKind) ||
            string.IsNullOrWhiteSpace(binding.CanonicalSyntax) || string.IsNullOrWhiteSpace(binding.ContainingMember) || binding.CandidateReason != "None" ||
            binding.TargetSymbol.Length != 0 && string.IsNullOrWhiteSpace(binding.TargetAssembly) ||
            binding.ArgumentParameters is null || binding.ArgumentRefKinds is null || binding.ArgumentParameters.Length != binding.ArgumentRefKinds.Length ||
            binding.ArgumentParameters.Any(static parameter => string.IsNullOrWhiteSpace(parameter)) || binding.ArgumentRefKinds.Any(static kind => string.IsNullOrWhiteSpace(kind)) ||
            binding.IsErrorSymbol || binding.HasCandidateSymbols || !IsSha256(binding.TokenSha256) || !IsSha256(binding.CanonicalSyntaxSha256) ||
            binding.CanonicalSyntaxSha256 != Sha256(Encoding.UTF8.GetBytes(binding.CanonicalSyntax)) || binding.ControlFlowBlock < -1 ||
            binding.StartLine <= 0 || binding.StartColumn <= 0 || binding.EndLine <= 0 || binding.EndColumn <= 0 ||
            binding.NodeKind == nameof(SyntaxKind.InvocationExpression) &&
                 (binding.OperationKind != nameof(OperationKind.Invocation) || binding.TargetSymbolKind != nameof(SymbolKind.Method) ||
                 string.IsNullOrWhiteSpace(binding.TargetSymbol) || string.IsNullOrWhiteSpace(binding.TargetAssembly) ||
                 string.IsNullOrWhiteSpace(binding.Receiver)))
        {
            throw new ExtractionException($"The IR contains an incomplete or tampered source binding: {binding.Member}.");
        }
    }

    private static void ValidateCompilerReferenceIdentities(CompilerReferenceIdentity[] references)
    {
        if (references.Length == 0 || references.Any(reference => reference is null || string.IsNullOrWhiteSpace(reference.Path) ||
                string.IsNullOrWhiteSpace(reference.AssemblyName) || !IsSha256(reference.Sha256) || !Guid.TryParse(reference.Mvid, out _) ||
                reference.Dependencies is null))
            throw new ExtractionException("The compiler-reference identity closure is incomplete.");
    }

    private static bool HasErrorSymbol(ISymbol symbol)
    {
        if (symbol is ITypeSymbol type && HasErrorType(type)) return true;
        if (symbol is IMethodSymbol method && (HasErrorType(method.ReturnType) || method.Parameters.Any(parameter => HasErrorType(parameter.Type)))) return true;
        if (symbol is IPropertySymbol property && HasErrorType(property.Type)) return true;
        if (symbol is IFieldSymbol field && HasErrorType(field.Type)) return true;
        for (ISymbol? current = symbol; current is not null; current = current.ContainingSymbol)
        {
            if (current is IErrorTypeSymbol || current.Kind == SymbolKind.ErrorType) return true;
        }

        return false;
    }

    private static bool HasErrorType(ITypeSymbol type)
        => HasErrorType(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));

    private static bool HasErrorType(ITypeSymbol type, HashSet<ITypeSymbol> seen)
    {
        if (!seen.Add(type)) return false;
        if (type is IErrorTypeSymbol || type.TypeKind == TypeKind.Error) return true;
        if (type is INamedTypeSymbol named)
            return (named.ContainingType is not null && HasErrorType(named.ContainingType, seen)) ||
                named.TypeArguments.Any(argument => HasErrorType(argument, seen));
        if (type is IArrayTypeSymbol array) return HasErrorType(array.ElementType, seen);
        if (type is IPointerTypeSymbol pointer) return HasErrorType(pointer.PointedAtType, seen);
        return type is ITypeParameterSymbol parameter && parameter.ConstraintTypes.Any(constraint => HasErrorType(constraint, seen));
    }

    private sealed record CompilerInventory(int SchemaVersion, int Count, string AggregateSha256, CompilerReferencePin[] References);

    private sealed record CompilerReferencePin(string Path, string AssemblyName, string Sha256, string Mvid, bool Selected);

    private static ControlFlowGraph? TryGetGraph(SemanticModel model, MethodDeclarationSyntax method)
    {
        IOperation? operation = model.GetOperation(method);
        return operation is IMethodBodyOperation body ? ControlFlowGraph.Create(body) : null;
    }
}
