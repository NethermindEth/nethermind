// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Nethermind.Evm.Lean.PrecompileFrameExtractor;

internal static class PrecompileFrameProfile
{
    internal const string ExtractorVersion = "precompile-frame-stage-a-1";
    internal const string KernelName = "standard-mainnet-amsterdam-precompile-frame-stage-a";
    internal const string Eip8038Commit = "8331fb3eed0a5366b28b25a016f1ad04fac0fa8e";
    internal const string NethermindCommit = "b2478235e71e6a7ec2a509aa0155e25d5fdfff80";
    internal const string IrFileName = "precompile-frame-stage-a.ir.json";
    internal const string ManifestFileName = "precompile-frame-stage-a.source-manifest.json";
    internal const string LeanFileName = "PrecompileFrameStageA.lean";

    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default
        .WithLanguageVersion(LanguageVersion.CSharp14)
        .WithDocumentationMode(DocumentationMode.Parse)
        .WithKind(SourceCodeKind.Regular);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly PrecompileBinding[] Bindings =
    [
        Binding(
            "ECRECOVER", 0x01, "ECRecoverPrecompile", "ECRecover", ActivationRule.Always,
            "Ecrecover", "EthereumEcdsa.RecoverAddressRaw", [],
            ["short input is right padded to 128 bytes", "long input is truncated to 128 bytes"],
            ["valid signature returns 32 bytes", "invalid signature returns successful empty output"],
            ["recover failure is successful empty output"]),
        Binding(
            "SHA256", 0x02, "Sha256Precompile", "Sha256", ActivationRule.Always,
            "Sha256", "SHA256.TryHashData", ["std/Sha256Precompile.cs"],
            ["arbitrary byte input"], ["32-byte digest"], ["hash helper failure is Result failure"]),
        Binding(
            "RIPEMD160", 0x03, "Ripemd160Precompile", "Ripemd160", ActivationRule.Always,
            "Ripemd160", "Ripemd.Compute", ["std/Ripemd160Precompile.cs"],
            ["arbitrary byte input"], ["20-byte digest left padded to 32 bytes"], []),
        Binding(
            "IDENTITY", 0x04, "IdentityPrecompile", "Identity", ActivationRule.Always,
            "Identity", "inputData.ToArray", [],
            ["arbitrary byte input"], ["exact input bytes"], []),
        Binding(
            "MODEXP", 0x05, "ModExpPrecompile", "ModExp", ActivationRule.Eip198,
            "ModExp", "Gmp.mpz_powm", ["std/ModExpPrecompile.cs", "ModExpPrecompilePreEip2565.std.cs"],
            ["header and declared-length effective input", "EIP-7823 maximum dimensions"],
            ["modulus-width result", "zero-modulus result is zero width"],
            ["invalid dimension is input failure", "GMP/native failure is Result failure"]),
        Binding(
            "BN254_ADD", 0x06, "BN254AddPrecompile", "BN254Add", ActivationRule.Eip196And197,
            "Bn254Add", "BN254.Add", ["std/BN254AddPrecompile.cs"],
            ["short input right padded", "long input truncated", "128-byte effective input"],
            ["64-byte point"], ["point/native validation is Result failure"]),
        Binding(
            "BN254_MUL", 0x07, "BN254MulPrecompile", "BN254Mul", ActivationRule.Eip196And197,
            "Bn254Mul", "BN254.Mul", ["std/BN254MulPrecompile.cs"],
            ["short input right padded", "long input truncated", "96-byte effective input"],
            ["64-byte point"], ["point/native validation is Result failure"]),
        Binding(
            "BN254_PAIRING", 0x08, "BN254PairingCheckPrecompile", "BN254PairingCheck", ActivationRule.Eip196And197,
            "Bn254Pairing", "BN254.CheckPairing", ["std/BN254PairingCheckPrecompile.cs"],
            ["input length is a multiple of 192", "empty input is valid"],
            ["32-byte Boolean"], ["invalid length or native validation is Result failure"]),
        Binding(
            "BLAKE2F", 0x09, "Blake2FPrecompile", "Blake2F", ActivationRule.Eip152,
            "Blake2F", "Blake2Compression::_blake.Compress", ["std/Blake2FPrecompile.cs"],
            ["exactly 213 bytes", "final flag is 0 or 1"], ["64-byte compression result"],
            ["length/final flag failure is Result failure"]),
        Binding(
            "KZG_POINT_EVALUATION", 0x0a, "KzgPointEvaluationPrecompile", "PointEvaluation", ActivationRule.Eip4844,
            "KzgPointEvaluation", "KzgPolynomialCommitments.VerifyProof", ["std/KzgPointEvaluationPrecompile.cs"],
            ["exactly 192 bytes", "versioned hash and commitment must agree"],
            ["fixed 64-byte success word"], ["length/hash/proof failure is Result failure"]),
        Binding(
            "BLS12_G1ADD", 0x0b, "Bls12381G1AddPrecompile", "Bls12381G1Add", ActivationRule.Eip2537,
            "Bls12381G1Add", "Nethermind.Crypto.Bls.P1::Add", ["std/Bls12381G1AddPrecompile.cs"],
            ["exactly 256 bytes"], ["128-byte raw G1 point"], ["field/curve/subgroup failure"]),
        Binding(
            "BLS12_G1MSM", 0x0c, "Bls12381G1MsmPrecompile", "Bls12381G1Msm", ActivationRule.Eip2537,
            "Bls12381G1Msm", "Nethermind.Crypto.Bls.P1::MultiMultAffine", ["std/Bls12381G1MsmPrecompile.cs"],
            ["nonempty multiple of 160 bytes"], ["128-byte raw G1 point"], ["field/curve/subgroup failure"]),
        Binding(
            "BLS12_G2ADD", 0x0d, "Bls12381G2AddPrecompile", "Bls12381G2Add", ActivationRule.Eip2537,
            "Bls12381G2Add", "Nethermind.Crypto.Bls.P2::Add", ["std/Bls12381G2AddPrecompile.cs"],
            ["exactly 512 bytes"], ["256-byte raw G2 point"], ["field/curve/subgroup failure"]),
        Binding(
            "BLS12_G2MSM", 0x0e, "Bls12381G2MsmPrecompile", "Bls12381G2Msm", ActivationRule.Eip2537,
            "Bls12381G2Msm", "Nethermind.Crypto.Bls.P2::MultiMultAffine", ["std/Bls12381G2MsmPrecompile.cs"],
            ["nonempty multiple of 288 bytes"], ["256-byte raw G2 point"], ["field/curve/subgroup failure"]),
        Binding(
            "BLS12_PAIRING", 0x0f, "Bls12381PairingCheckPrecompile", "Bls12381PairingCheck", ActivationRule.Eip2537,
            "Bls12381Pairing", "Nethermind.Crypto.Bls.PT::MillerLoopN", ["std/Bls12381PairingCheckPrecompile.cs"],
            ["nonempty multiple of 384 bytes"], ["32-byte Boolean"], ["field/curve/subgroup failure"]),
        Binding(
            "BLS12_MAP_FP_TO_G1", 0x10, "Bls12381FpToG1Precompile", "Bls12381FpToG1", ActivationRule.Eip2537,
            "Bls12381FpToG1", "Nethermind.Crypto.Bls.P1::MapTo", ["std/Bls12381FpToG1Precompile.cs"],
            ["exactly 64 bytes with 16-byte zero prefix"], ["128-byte raw G1 point"], ["field/map failure"]),
        Binding(
            "BLS12_MAP_FP2_TO_G2", 0x11, "Bls12381Fp2ToG2Precompile", "Bls12381Fp2ToG2", ActivationRule.Eip2537,
            "Bls12381Fp2ToG2", "Nethermind.Crypto.Bls.P2::MapTo", ["std/Bls12381Fp2ToG2Precompile.cs"],
            ["exactly 128 bytes"], ["256-byte raw G2 point"], ["field/map failure"]),
        Binding(
            "P256VERIFY", 0x100, "SecP256r1Precompile", "P256Verify", ActivationRule.Eip7212Or7951,
            "P256Verify", "SecP256r1.VerifySignature", ["std/SecP256r1Precompile.cs"],
            ["exactly 160 bytes"], ["32-byte success word or empty output"],
            ["false signature is successful empty output"]),
    ];

    private static readonly SourceSpec[] CoreSourceSpecs =
    [
        Source("Directory.Packages.props", "package versions", true),
        Source("src/Nethermind/Directory.Build.props", "standard build selector", true),
        Source("src/Nethermind/Directory.Build.targets", "standard build selector", true),
        Source("src/Nethermind/Nethermind.Evm.Precompiles/Nethermind.Evm.Precompiles.csproj", "precompile package closure", true),
        Source("src/Nethermind/Nethermind.Blockchain/EthereumPrecompileProvider.cs", "provider registry", true),
        Source("src/Nethermind/Nethermind.Core/Precompiles/PrecompiledAddresses.cs", "address registry", true),
        Source("src/Nethermind/Nethermind.Core/Address.cs", "precompile-number projection", true),
        Source("src/Nethermind/Nethermind.Core/Specs/IReleaseSpec.cs", "release membership contract", true),
        Source("src/Nethermind/Nethermind.Specs/ReleaseSpec.cs", "release membership implementation", true),
        Source("src/Nethermind/Nethermind.Specs/MainnetSpecProvider.cs", "mainnet fork schedule", true),
        Source("src/Nethermind/Nethermind.Specs/ForkScheduleSpecProvider.cs", "fork selection", true),
        Source("src/Nethermind/Nethermind.Specs/Forks/06_Byzantium.cs", "precompile activation ancestry", true),
        Source("src/Nethermind/Nethermind.Specs/Forks/09_Istanbul.cs", "precompile activation ancestry", true),
        Source("src/Nethermind/Nethermind.Specs/Forks/11_Berlin.cs", "precompile activation ancestry", true),
        Source("src/Nethermind/Nethermind.Specs/Forks/17_Cancun.cs", "precompile activation ancestry", true),
        Source("src/Nethermind/Nethermind.Specs/Forks/18_Prague.cs", "precompile activation ancestry", true),
        Source("src/Nethermind/Nethermind.Specs/Forks/19_Osaka.cs", "precompile activation ancestry", true),
        Source("src/Nethermind/Nethermind.Specs/Forks/20_BPO1.cs", "precompile activation ancestry", true),
        Source("src/Nethermind/Nethermind.Specs/Forks/21_BPO2.cs", "precompile activation ancestry", true),
        Source("src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs", "Amsterdam activation", true),
        Source("src/Nethermind/Nethermind.Specs/Forks/00_Olympic.cs", "Amsterdam activation ancestry", true),
        Source("src/Nethermind/Nethermind.Specs/Forks/01_Frontier.cs", "Amsterdam activation ancestry", true),
        Source("src/Nethermind/Nethermind.Specs/Forks/02_Homestead.cs", "Amsterdam activation ancestry", true),
        Source("src/Nethermind/Nethermind.Specs/Forks/03_Dao.cs", "Amsterdam activation ancestry", true),
        Source("src/Nethermind/Nethermind.Specs/Forks/04_TangerineWhistle.cs", "Amsterdam activation ancestry", true),
        Source("src/Nethermind/Nethermind.Specs/Forks/05_SpuriousDragon.cs", "Amsterdam activation ancestry", true),
        Source("src/Nethermind/Nethermind.Specs/Forks/07_Constantinople.cs", "Amsterdam activation ancestry", true),
        Source("src/Nethermind/Nethermind.Specs/Forks/08_ConstantinopleFix.cs", "Amsterdam activation ancestry", true),
        Source("src/Nethermind/Nethermind.Specs/Forks/10_MuirGlacier.cs", "Amsterdam activation ancestry", true),
        Source("src/Nethermind/Nethermind.Specs/Forks/12_London.cs", "Amsterdam activation ancestry", true),
        Source("src/Nethermind/Nethermind.Specs/Forks/13_ArrowGlacier.cs", "Amsterdam activation ancestry", true),
        Source("src/Nethermind/Nethermind.Specs/Forks/14_GrayGlacier.cs", "Amsterdam activation ancestry", true),
        Source("src/Nethermind/Nethermind.Specs/Forks/15_Paris.cs", "Amsterdam activation ancestry", true),
        Source("src/Nethermind/Nethermind.Specs/Forks/16_Shanghai.cs", "Amsterdam activation ancestry", true),
        Source("src/Nethermind/Nethermind.Specs/Forks/Fork.cs", "fork ancestry contract", true),
        Source("src/Nethermind/Nethermind.Specs/Forks/NamedReleaseSpec.cs", "fork ancestry contract", true),
        Source("src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs", "standard DI", true),
        Source("src/Nethermind/Nethermind.Init/Modules/PrewarmerModule.cs", "cache decorator activation", true),
        Source("src/Nethermind/Nethermind.Init/Steps/InitializePrecompiles.cs", "native precompile initialization", true),
        Source("src/Nethermind/Nethermind.Evm/CodeInfoRepository.cs", "code and precompile resolution", true),
        Source("src/Nethermind/Nethermind.Evm/IPrecompileProvider.cs", "precompile provider contract", true),
        Source("src/Nethermind/Nethermind.Evm/ICodeInfoRepository.cs", "code repository contract and no-delegation selector", true),
        Source("src/Nethermind/Nethermind.Evm/ExecutionType.cs", "execution balance-credit selector", true),
        Source("src/Nethermind/Nethermind.Evm/CacheCodeInfoRepository.cs", "code cache resolution", true),
        Source("src/Nethermind/Nethermind.Blockchain/PrecompileCachedCodeInfoRepository.cs", "precompile cache resolution", true),
        Source("src/Nethermind/Nethermind.Evm/CodeAnalysis/CodeInfo.cs", "precompile code identity", true),
        Source("src/Nethermind/Nethermind.Evm/Precompiles/IPrecompile.cs", "precompile contract", true),
        Source("src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs", "precompile gas contract", true),
        Source("src/Nethermind/Nethermind.Evm/GasPolicy/PrecompileGasPricingKernel.cs", "precompile gas kernel", true),
        Source("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "precompile gas adapter", true),
        Source("src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionAdapterKernel.cs", "state gas adapter transition", true),
        Source("src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs", "state gas pure transition", true),
        Source("src/Nethermind/Nethermind.Evm.Precompiles/Errors.cs", "precompile error identities", true),
        Source("src/Nethermind/Nethermind.Evm.Precompiles/PrecompileDecorator.cs", "known decorator contract", true),
        Source("src/Nethermind/Nethermind.Evm/State/PrecompileCaches.cs", "precompile cache key", true),
        Source("src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.cs", "call frame routing", true),
        Source("src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.std.cs", "standard direct routing", true),
        Source("src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.zkevm.cs", "excluded zk routing", false),
        Source("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "frame driver", true),
        Source("src/Nethermind/Nethermind.Evm/VirtualMachine.ExecutionHandlers.cs", "fork-specialized frame handlers", true),
        Source("src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs", "actual InstructionCall selector", true),
        Source("src/Nethermind/Nethermind.Evm/VirtualMachine.CallResult.cs", "precompile result carrier", true),
        Source("src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs", "dispatch entry", true),
        Source("src/Nethermind/Nethermind.Evm/VirtualMachine.std.cs", "standard VM partial", true),
        Source("src/Nethermind/Nethermind.Evm/VirtualMachine.zkevm.cs", "excluded zk frame path", false),
        Source("src/Nethermind/Nethermind.Evm/VmState.cs", "frame state", true),
        Source("src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs", "direct output-copy memory bounds", true),
        Source("src/Nethermind/Nethermind.Evm/ExecutionEnvironment.cs", "execution environment", true),
        Source("src/Nethermind/Nethermind.Evm/StackAccessTracker.cs", "access rollback", true),
        Source("src/Nethermind/Nethermind.Evm/State/IWorldState.cs", "world callback contract", true),
        Source("src/Nethermind/Nethermind.Evm/State/WorldStateExtensions.cs", "world balance touch", true),
        Source("src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "transaction warmup", true),
        Source("src/Nethermind/Nethermind.State/TracedAccessWorldState.cs", "world tracing adapter", true),
        Source("src/Nethermind/Nethermind.Evm.Precompiles/BN254.cs", "BN254 oracle bridge", true),
        Source("src/Nethermind/Nethermind.Evm.Precompiles/Eip2537.cs", "BLS oracle bridge", true),
        Source("src/Nethermind/Nethermind.Evm.Precompiles/IdentityPrecompileKernel.cs", "identity pricing kernel", true),
        Source("src/Nethermind/Nethermind.Evm.Precompiles/ModExpPrecompilePreEip2565.cs", "MODEXP legacy pricing helper", true),
        Source("src/Nethermind/Nethermind.Crypto/Ripemd.cs", "RIPEMD oracle bridge", true),
        Source("src/Nethermind/Nethermind.Crypto/Blake2/Blake2Compression.cs", "Blake2 oracle bridge", true),
        Source("src/Nethermind/Nethermind.Crypto/Blake2/Blake2Compression.Scalar.cs", "Blake2 standard scalar oracle", true),
        Source("src/Nethermind/Nethermind.Crypto/Blake2/Blake2Compression.Sse41.cs", "Blake2 standard SIMD oracle", true),
        Source("src/Nethermind/Nethermind.Crypto/Blake2/Blake2Compression.Avx2.cs", "Blake2 standard SIMD oracle", true),
        Source("src/Nethermind/Nethermind.Crypto/EthereumEcdsa.std.cs", "ECRecover standard oracle", true),
        Source("src/Nethermind/Nethermind.Core/Crypto/KeccakCache.cs", "Keccak oracle bridge", true),
        Source("src/Nethermind/Nethermind.Core/Crypto/KeccakCache.std.cs", "Keccak standard oracle", true),
        Source("src/Nethermind/Nethermind.Crypto/KzgPolynomialCommitments.cs", "KZG oracle bridge", true),
        Source("src/Nethermind/Nethermind.Crypto/KzgPolynomialCommitments.std.cs", "KZG standard oracle", true),
    ];

    private static readonly NativePackageSpec[] NativePackages =
    [
        new("Nethermind.Crypto.Bls", "BLS native oracle package"),
        new("Nethermind.Crypto.SecP256r1", "P-256 native oracle package"),
        new("Nethermind.GmpBindings", "MODEXP native oracle package"),
        new("Nethermind.MclBindings", "BN254 native oracle package"),
    ];

    internal static ExtractionResult Extract(string repoRoot, string outputDirectory, string? leanOutputPath = null,
        bool enableZkEvm = false)
    {
        string root = Path.GetFullPath(repoRoot);
        if (enableZkEvm)
        {
            throw new ExtractionException("Precompile frame Stage A is standard-build only; EnableZkEvm=true is rejected.");
        }

        Snapshot snapshot = ReadSnapshot(root);
        ValidateProduction(snapshot);
        IrDocument ir = BuildIr(snapshot);
        byte[] irBytes = SerializeCanonical(ir);
        string irSha256 = Hash(irBytes);
        byte[] leanBytes = PrecompileFrameLeanEmitter.Emit(ir, irSha256, snapshot.CombinedSourceSha256);
        SourceManifest manifest = BuildManifest(snapshot, irBytes, leanBytes);
        byte[] manifestBytes = SerializeCanonical(manifest);
        ValidateArtifacts(snapshot, ir, manifest, irBytes, leanBytes, manifestBytes);

        string output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        string irPath = Path.Combine(output, IrFileName);
        string manifestPath = Path.Combine(output, ManifestFileName);
        string leanPath = leanOutputPath is null ? Path.Combine(output, LeanFileName) : Path.GetFullPath(leanOutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(leanPath)!);
        WriteDeterministic(irPath, irBytes);
        WriteDeterministic(manifestPath, manifestBytes);
        WriteDeterministic(leanPath, leanBytes);

        StageBExtractionResult stageB = PrecompileFrameStageBProfile.Extract(
            root,
            output,
            ir,
            manifest,
            irBytes,
            manifestBytes,
            leanBytes);

        return new(
            irPath,
            manifestPath,
            leanPath,
            snapshot.Sources.Length,
            snapshot.Members.Length,
            snapshot.Dependencies.Length,
            ir.Routes.Length,
            irSha256,
            Hash(manifestBytes),
            Hash(leanBytes),
            stageB);
    }

    internal static void ValidateExistingArtifacts(string repoRoot, string irPath, string manifestPath, string leanPath)
    {
        string root = Path.GetFullPath(repoRoot);
        Snapshot snapshot = ReadSnapshot(root);
        ValidateProduction(snapshot);
        byte[] irBytes = File.ReadAllBytes(irPath);
        IrDocument ir = Deserialize<IrDocument>(irBytes, "IR");
        byte[] leanBytes = File.ReadAllBytes(leanPath);
        SourceManifest manifest = Deserialize<SourceManifest>(File.ReadAllBytes(manifestPath), "manifest");
        ValidateArtifacts(snapshot, ir, manifest, irBytes, leanBytes, File.ReadAllBytes(manifestPath));
    }

    private static Snapshot ReadSnapshot(string root)
    {
        Dictionary<string, SourceSpec> specifications = new(StringComparer.Ordinal);
        foreach (SourceSpec specification in BuildSourceSpecs())
        {
            if (!specifications.TryAdd(specification.Path, specification))
            {
                SourceSpec existing = specifications[specification.Path];
                if (existing.IncludedInStandardBuild != specification.IncludedInStandardBuild)
                {
                    throw new ExtractionException($"Source closure assigns conflicting standard-build status to {specification.Path}.");
                }
            }
        }

        List<SourceIdentity> sources = [];
        Dictionary<string, ParsedSource> parsed = new(StringComparer.Ordinal);
        foreach (SourceSpec specification in specifications.Values.OrderBy(static item => item.Path, StringComparer.Ordinal))
        {
            string fullPath = ResolveExactPath(root, specification.Path);
            byte[] bytes = File.ReadAllBytes(fullPath);
            string sourceHash = Hash(bytes);
            if (!IsSha256(sourceHash))
            {
                throw new ExtractionException($"Source closure produced no SHA-256 for {specification.Path}.");
            }

            bool isCSharp = Path.GetExtension(specification.Path).Equals(".cs", StringComparison.OrdinalIgnoreCase);
            string syntaxHash = "-";
            CompilationUnitSyntax? syntax = null;
            if (isCSharp)
            {
                syntax = ParseCSharp(bytes, fullPath, specification.Role);
                syntaxHash = Hash(CanonicalTokens(syntax));
                if (!IsSha256(syntaxHash))
                {
                    throw new ExtractionException($"Roslyn syntax closure produced no SHA-256 for {specification.Path}.");
                }
                parsed.Add(specification.Path, new(specification.Path, fullPath, bytes, syntax));
            }
            else if (string.IsNullOrEmpty(syntaxHash) || syntaxHash != "-")
            {
                throw new ExtractionException($"Raw source {specification.Path} has an invalid syntax digest.");
            }

            sources.Add(new(
                specification.Path,
                specification.Role,
                sourceHash,
                syntaxHash,
                !isCSharp,
                specification.IncludedInStandardBuild));
        }

        List<DependencyIdentity> dependencies = [];
        string wrapperPath = "tools/Evm/Lean/Eip803x/Precompiles/Wrapper.lean";
        dependencies.Add(ReadDependency(root, "handwrittenWrapper", "Wrapper", wrapperPath,
            "Eip803x.Precompiles.Wrapper", "tryConsumePrecompileGas", oracleOnly: true));
        foreach (PrecompileBinding binding in Bindings)
        {
            string oraclePath = $"tools/Evm/Lean/Eip803x/Precompiles/{OracleFileName(binding)}";
            dependencies.Add(ReadDependency(root, "handwrittenOracle", binding.Name, oraclePath,
                binding.OracleNamespace, binding.OracleEntrySymbol, oracleOnly: true));
        }

        string projectPath = "src/Nethermind/Nethermind.Evm.Precompiles/Nethermind.Evm.Precompiles.csproj";
        string projectHash = sources.Single(static source => source.Path ==
            "src/Nethermind/Nethermind.Evm.Precompiles/Nethermind.Evm.Precompiles.csproj").Sha256;
        foreach (NativePackageSpec package in NativePackages)
        {
            dependencies.Add(new(
                "nativePackage",
                package.Name,
                projectPath,
                projectHash,
                $"PackageReference:{package.Name}",
                true));
        }

        MemberIdentity[] members = CollectMembers(parsed);
        string combinedSource = CombinedSourceHash(sources, dependencies);
        string combinedMember = CombinedMemberHash(members);
        return new(root, [.. sources], members, [.. dependencies], parsed, combinedSource, combinedMember);
    }

    private static List<SourceSpec> BuildSourceSpecs()
    {
        Dictionary<string, SourceSpec> result = new(StringComparer.Ordinal);
        foreach (SourceSpec source in CoreSourceSpecs)
        {
            result[source.Path] = source;
        }

        foreach (PrecompileBinding binding in Bindings)
        {
            result[binding.BaseSourcePath] = Source(binding.BaseSourcePath, "precompile declaration", true);
            foreach (string path in binding.StandardSourcePaths)
            {
                string fullPath = $"src/Nethermind/Nethermind.Evm.Precompiles/{path}";
                result[fullPath] = Source(fullPath, "standard precompile implementation", true);
            }
        }

        return [.. result.Values];
    }

    private static DependencyIdentity ReadDependency(string root, string kind, string name, string relativePath,
        string binding, string entrySymbol, bool oracleOnly)
    {
        string fullPath = ResolveExactPath(root, relativePath);
        byte[] bytes = File.ReadAllBytes(fullPath);
        string text = Encoding.UTF8.GetString(bytes);
        if (bytes.Length == 0 || string.IsNullOrWhiteSpace(text) || !IsSha256(Hash(bytes)))
        {
            throw new ExtractionException($"Dependency {name} at {relativePath} is empty or unhashable.");
        }

        if ((kind == "handwrittenWrapper" || kind == "handwrittenOracle") &&
            (!HasExactLeanNamespace(text, binding) || !HasExactLeanDefinition(text, entrySymbol)))
        {
            throw new ExtractionException($"Dependency {name} at {relativePath} does not expose the exact pinned Lean namespace and entry symbol.");
        }

        return new(kind, name, relativePath, Hash(bytes), binding, oracleOnly);
    }

    private static void ValidateProduction(Snapshot snapshot)
    {
        ValidateStandardBuild(snapshot);
        ValidateProvider(snapshot);
        ValidateActivation(snapshot);
        ValidateResolutionAndCache(snapshot);
        ValidateGasPricing(snapshot);
        ValidateVmRouting(snapshot);
        _ = ReadTrySaveBoundsAdmission(snapshot);
        ValidateLeafBindings(snapshot);
        ValidateNativeDependencies(snapshot);
        ValidateOracleBindings(snapshot);
    }

    private static void ValidateStandardBuild(Snapshot snapshot)
    {
        string props = Text(snapshot, "src/Nethermind/Directory.Build.props");
        string targets = Text(snapshot, "src/Nethermind/Directory.Build.targets");
        if (!Canonical(props).Contains("<UsingInclude=\"System.Runtime.Intrinsics.Vector256&lt;byte&gt;\"Alias=\"EvmWord\"/>", StringComparison.Ordinal))
        {
            throw new ExtractionException("Standard-build EvmWord alias binding changed.");
        }

        string canonicalTargets = Canonical(targets);
        if (!canonicalTargets.Contains("ItemGroupCondition=\"'$(EnableZkEvm)'=='true'\"", StringComparison.Ordinal) ||
            !canonicalTargets.Contains("CompileRemove=\"**/std/**/*.cs\"", StringComparison.Ordinal) ||
            !canonicalTargets.Contains("CompileRemove=\"**/*.std.cs\"", StringComparison.Ordinal) ||
            !canonicalTargets.Contains("ItemGroupCondition=\"'$(EnableZkEvm)'!='true'\"", StringComparison.Ordinal) ||
            !canonicalTargets.Contains("CompileRemove=\"**/zkevm/**/*.cs\"", StringComparison.Ordinal) ||
            !canonicalTargets.Contains("CompileRemove=\"**/*.zkevm.cs\"", StringComparison.Ordinal))
        {
            throw new ExtractionException("Standard-build source selection no longer excludes zkevm and selects std sources exactly.");
        }

        foreach (SourceIdentity source in snapshot.Sources)
        {
            if (source.Path.Contains("zkevm", StringComparison.OrdinalIgnoreCase) && source.IncludedInStandardBuild)
            {
                throw new ExtractionException($"ZK source is marked included in the standard closure: {source.Path}.");
            }
        }
    }

    private static void ValidateProvider(Snapshot snapshot)
    {
        ParsedSource providerSource = snapshot.Parsed["src/Nethermind/Nethermind.Blockchain/EthereumPrecompileProvider.cs"];
        string provider = Canonical(Text(snapshot, "src/Nethermind/Nethermind.Blockchain/EthereumPrecompileProvider.cs"));
        if (!provider.Contains("classEthereumPrecompileProvider", StringComparison.Ordinal) ||
            !provider.Contains("FrozenDictionary<AddressAsKey,CodeInfo>", StringComparison.Ordinal) ||
            !provider.Contains("GetPrecompiles()", StringComparison.Ordinal) ||
            provider.Contains("GetTypes", StringComparison.Ordinal) ||
            provider.Contains("Activator", StringComparison.Ordinal) ||
            provider.Contains("Assembly", StringComparison.Ordinal) ||
            provider.Contains("foreach", StringComparison.Ordinal))
        {
            throw new ExtractionException("Provider registry is not the exact explicit standard Ethereum registry.");
        }

        ProviderEntry[] entries = ParseProviderEntries(providerSource);
        if (entries.Length != Bindings.Length)
        {
            throw new ExtractionException($"Provider registry initializer contains {entries.Length} entries; expected exactly {Bindings.Length}.");
        }

        HashSet<int> providerAddresses = [];
        foreach (ProviderEntry entry in entries)
        {
            PrecompileBinding? binding = Bindings.SingleOrDefault(candidate => candidate.ProviderMember == entry.ProviderMember);
            if (binding is null || entry.Address != binding.Address || entry.TypeName != binding.TypeName ||
                entry.Name != binding.Name || !entry.IsStandardBuild || !providerAddresses.Add(entry.Address))
            {
                throw new ExtractionException($"Provider registry binding changed or duplicated at {entry.ProviderMember}.");
            }
        }

        AddressEntry[] addresses = ParseAddressEntries(snapshot.Parsed["src/Nethermind/Nethermind.Core/Precompiles/PrecompiledAddresses.cs"]);
        if (addresses.Length != Bindings.Length)
        {
            throw new ExtractionException($"Address registry initializer contains {addresses.Length} entries; expected exactly {Bindings.Length}.");
        }

        HashSet<int> addressValues = [];
        foreach (AddressEntry entry in addresses)
        {
            PrecompileBinding? binding = Bindings.SingleOrDefault(candidate => candidate.AddressField == entry.Name);
            if (binding is null || entry.Address != binding.Address || !addressValues.Add(entry.Address))
            {
                throw new ExtractionException($"Address registry binding changed or duplicated at {entry.Name}.");
            }
        }

        if (!providerAddresses.SetEquals(addressValues))
        {
            throw new ExtractionException("Provider and address initializer sets do not agree.");
        }
    }

    private static ProviderEntry[] ParseProviderEntries(ParsedSource source)
    {
        ClassDeclarationSyntax provider = FindClass(source.Root, "EthereumPrecompileProvider");
        PropertyDeclarationSyntax property = provider.Members.OfType<PropertyDeclarationSyntax>()
            .SingleOrDefault(candidate => candidate.Identifier.ValueText == "Precompiles")
            ?? throw new ExtractionException("EthereumPrecompileProvider.Precompiles initializer is missing.");
        ExpressionSyntax value = property.ExpressionBody?.Expression
            ?? property.AccessorList?.Accessors.Select(accessor => accessor.ExpressionBody?.Expression)
                .OfType<ExpressionSyntax>().SingleOrDefault()
            ?? property.AccessorList?.Accessors.SelectMany(accessor => accessor.DescendantNodes().OfType<ReturnStatementSyntax>())
                .Select(statement => statement.Expression).OfType<ExpressionSyntax>().SingleOrDefault()
            ?? throw new ExtractionException("EthereumPrecompileProvider.Precompiles has no returned initializer.");
        InitializerExpressionSyntax initializer = value.DescendantNodesAndSelf().OfType<InitializerExpressionSyntax>()
            .SingleOrDefault(candidate => candidate.IsKind(SyntaxKind.CollectionInitializerExpression) ||
                candidate.IsKind(SyntaxKind.ObjectInitializerExpression))
            ?? throw new ExtractionException("EthereumPrecompileProvider.Precompiles collection initializer is missing.");

        List<ProviderEntry> entries = [];
        foreach (ExpressionSyntax expression in initializer.Expressions)
        {
            string item = Canonical(expression);
            int equals = item.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0 || equals == item.Length - 1)
            {
                throw new ExtractionException($"Provider initializer item is not an address assignment: {item}.");
            }

            string key = item[..equals].Trim('[', ']');
            string valueText = item[(equals + 1)..];
            PrecompileBinding binding = Bindings.SingleOrDefault(candidate => candidate.ProviderMember == key)
                ?? throw new ExtractionException($"Provider initializer contains an unexpected key: {key}.");
            string expectedValue = $"new({binding.TypeName}.Instance)";
            if (!valueText.Equals(expectedValue, StringComparison.Ordinal))
            {
                throw new ExtractionException($"Provider initializer value changed for {binding.Name}: {valueText}.");
            }

            entries.Add(new(binding.Name, binding.Address, binding.TypeName, binding.Activation, key, true));
        }

        return [.. entries];
    }

    private static AddressEntry[] ParseAddressEntries(ParsedSource source)
    {
        ClassDeclarationSyntax addresses = FindClass(source.Root, "PrecompiledAddresses");
        List<AddressEntry> entries = [];
        foreach (FieldDeclarationSyntax field in addresses.Members.OfType<FieldDeclarationSyntax>())
        {
            foreach (VariableDeclaratorSyntax variable in field.Declaration.Variables)
            {
                if (variable.Initializer?.Value is null)
                {
                    throw new ExtractionException($"Address initializer is missing for {variable.Identifier.ValueText}.");
                }

                string initializer = Canonical(variable.Initializer.Value);
                const string prefix = "Address.FromNumber(";
                if (!initializer.StartsWith(prefix, StringComparison.Ordinal))
                {
                    throw new ExtractionException($"Address initializer for {variable.Identifier.ValueText} is not Address.FromNumber.");
                }

                if (!initializer.EndsWith(")", StringComparison.Ordinal))
                {
                    throw new ExtractionException($"Address initializer is malformed for {variable.Identifier.ValueText}.");
                }

                string literal = initializer[prefix.Length..^1];
                entries.Add(new(variable.Identifier.ValueText, ParseAddressNumber(literal, variable.Identifier.ValueText)));
            }
        }

        return [.. entries];
    }

    private static int ParseAddressNumber(string literal, string subject)
    {
        NumberStyles styles = literal.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? NumberStyles.AllowHexSpecifier
            : NumberStyles.Integer;
        string value = literal.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? literal[2..] : literal;
        if (!int.TryParse(value, styles, CultureInfo.InvariantCulture, out int number) || number < 0)
        {
            throw new ExtractionException($"Address initializer for {subject} is not a non-negative integer: {literal}.");
        }

        return number;
    }

    private static ForkActivationStep[] ParseForkActivations(Snapshot snapshot)
    {
        Dictionary<string, (ParsedSource Source, ClassDeclarationSyntax Declaration)> forks = new(StringComparer.Ordinal);
        foreach (ParsedSource source in snapshot.Parsed.Values.Where(source =>
                     source.RelativePath.StartsWith("src/Nethermind/Nethermind.Specs/Forks/", StringComparison.Ordinal) &&
                     source.RelativePath.EndsWith(".cs", StringComparison.Ordinal)))
        {
            foreach (ClassDeclarationSyntax declaration in source.Root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (declaration.BaseList?.Types.Count != 1 ||
                    !Canonical(declaration.BaseList.Types[0]).StartsWith("NamedReleaseSpec<", StringComparison.Ordinal)) continue;
                if (!forks.TryAdd(declaration.Identifier.ValueText, (source, declaration)))
                {
                    throw new ExtractionException($"Fork activation class is duplicated: {declaration.Identifier.ValueText}.");
                }
            }
        }

        List<ForkActivationStep> result = [];
        HashSet<string> visited = new(StringComparer.Ordinal);
        string current = "Amsterdam";
        while (true)
        {
            if (!visited.Add(current) || !forks.TryGetValue(current, out (ParsedSource Source, ClassDeclarationSyntax Declaration) fork))
            {
                throw new ExtractionException($"Amsterdam fork ancestry cannot resolve {current}.");
            }

            string baseText = Canonical(fork.Declaration.BaseList!.Types[0]);
            int open = baseText.LastIndexOf('(');
            int parentEnd = baseText.IndexOf(".Instance", open + 1, StringComparison.Ordinal);
            if (open < 0 || parentEnd <= open + 1)
            {
                throw new ExtractionException($"Fork {current} has no resolvable parent instance.");
            }

            string parent = baseText[(open + 1)..parentEnd];
            string[] enabled = fork.Declaration.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(assignment => Canonical(assignment.Right) == "true")
                .Select(assignment => Canonical(assignment.Left))
                .Select(static left => left.StartsWith("spec.", StringComparison.Ordinal) ? left[5..] : left)
                .Where(static property => property.StartsWith("IsEip", StringComparison.Ordinal) ||
                    property.StartsWith("IsRip", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            result.Add(new(current, parent, fork.Source.RelativePath, enabled));
            if (current == "Frontier") break;
            current = parent;
        }

        return [.. result];
    }

    private static void ValidateActivation(Snapshot snapshot)
    {
        string release = Canonical(Text(snapshot, "src/Nethermind/Nethermind.Specs/ReleaseSpec.cs"));
        string amsterdam = Canonical(Text(snapshot, "src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs"));
        string mainnet = Canonical(Text(snapshot, "src/Nethermind/Nethermind.Specs/MainnetSpecProvider.cs"));
        string schedule = Canonical(Text(snapshot, "src/Nethermind/Nethermind.Specs/ForkScheduleSpecProvider.cs"));

        ForkActivationStep[] ancestry = ParseForkActivations(snapshot);
        if (ancestry.Length < 20 || ancestry[0].Fork != "Amsterdam" || ancestry[^1].Fork != "Frontier" ||
            ancestry.Any(step => string.IsNullOrEmpty(step.Parent) || string.IsNullOrEmpty(step.SourcePath)))
        {
            throw new ExtractionException("Amsterdam activation ancestry is incomplete or cyclic.");
        }

        Dictionary<string, string[]> requiredAssignments = new(StringComparer.Ordinal)
        {
            ["Byzantium"] = ["IsEip196Enabled", "IsEip197Enabled", "IsEip198Enabled"],
            ["Istanbul"] = ["IsEip152Enabled"],
            ["Cancun"] = ["IsEip4844Enabled"],
            ["Prague"] = ["IsEip2537Enabled"],
            ["Osaka"] = ["IsEip7951Enabled"],
        };
        foreach ((string fork, string[] properties) in requiredAssignments)
        {
            ForkActivationStep step = ancestry.SingleOrDefault(candidate => candidate.Fork == fork)
                ?? throw new ExtractionException($"Amsterdam ancestry does not include {fork}.");
            if (!properties.All(property => step.EnabledProperties.Contains(property, StringComparer.Ordinal)))
            {
                throw new ExtractionException($"{fork} no longer enables the required precompile EIP flags.");
            }
        }

        string[] requiredReleaseFragments =
        [
            "PrecompiledAddresses.ECRecover",
            "PrecompiledAddresses.Sha256",
            "PrecompiledAddresses.Ripemd160",
            "PrecompiledAddresses.Identity",
            "if(IsEip198Enabled)cache.Add(PrecompiledAddresses.ModExp)",
            "if(IsEip196Enabled&&IsEip197Enabled)",
            "PrecompiledAddresses.BN254Add",
            "PrecompiledAddresses.BN254Mul",
            "PrecompiledAddresses.BN254PairingCheck",
            "if(IsEip152Enabled)cache.Add(PrecompiledAddresses.Blake2F)",
            "if(IsEip4844Enabled)cache.Add(PrecompiledAddresses.PointEvaluation)",
            "if(IsEip2537Enabled)",
            "PrecompiledAddresses.Bls12381G1Add",
            "PrecompiledAddresses.Bls12381G1Msm",
            "PrecompiledAddresses.Bls12381G2Add",
            "PrecompiledAddresses.Bls12381G2Msm",
            "PrecompiledAddresses.Bls12381PairingCheck",
            "PrecompiledAddresses.Bls12381FpToG1",
            "PrecompiledAddresses.Bls12381Fp2ToG2",
            "if(IsRip7212Enabled||IsEip7951Enabled)cache.Add(PrecompiledAddresses.P256Verify)",
            "publicboolIsPrecompile(Addressaddress)",
        ];
        foreach (string fragment in requiredReleaseFragments)
        {
            if (!release.Contains(fragment, StringComparison.Ordinal))
            {
                throw new ExtractionException($"ReleaseSpec activation fragment changed: {fragment}.");
            }
        }

        if (!amsterdam.Contains("classAmsterdam():NamedReleaseSpec<Amsterdam>(BPO2.Instance)", StringComparison.Ordinal) ||
            !amsterdam.Contains("IsEip8038Enabled=true", StringComparison.Ordinal) ||
            !mainnet.Contains("AmsterdamBlockTimestamp=ulong.MaxValue-1", StringComparison.Ordinal) ||
            !mainnet.Contains("[AmsterdamBlockTimestamp]=Amsterdam.Instance", StringComparison.Ordinal) ||
            !mainnet.Contains("AmsterdamActivation", StringComparison.Ordinal) ||
            !schedule.Contains("Timestampisulongts", StringComparison.Ordinal) ||
            !schedule.Contains("forkActivation.BlockNumber>=idx.LastBlockKey", StringComparison.Ordinal) ||
            !schedule.Contains("idx.LookupTimestamp(ts)", StringComparison.Ordinal))
        {
            throw new ExtractionException("Amsterdam fork selection or timestamp activation changed.");
        }
    }

    private static void ValidateResolutionAndCache(Snapshot snapshot)
    {
        string repository = Canonical(Text(snapshot, "src/Nethermind/Nethermind.Evm/CodeInfoRepository.cs"));
        if (!repository.Contains("if(vmSpec.IsPrecompile(codeSource))", StringComparison.Ordinal) ||
            !repository.Contains("_worldState.AddAccountRead(codeSource)", StringComparison.Ordinal) ||
            !repository.Contains("_worldState.RecordAccountAccess(codeSource)", StringComparison.Ordinal) ||
            !repository.Contains("delegationAddress=null", StringComparison.Ordinal) ||
            !repository.Contains("MaxIndexedNumber=0x100", StringComparison.Ordinal) ||
            !repository.Contains("PrecompileIndexOrNegative", StringComparison.Ordinal) ||
            !repository.Contains("BuildPrecompileArray", StringComparison.Ordinal))
        {
            throw new ExtractionException("Base precompile resolution no longer has its read/access, no-delegation, or low-0x100 path.");
        }

        string cached = Canonical(Text(snapshot, "src/Nethermind/Nethermind.Blockchain/PrecompileCachedCodeInfoRepository.cs"));
        if (!cached.Contains("if(vmSpec.IsPrecompile(codeSource)&&TryGetCachedPrecompile", StringComparison.Ordinal) ||
            !cached.Contains("_worldState.AddAccountRead(codeSource)", StringComparison.Ordinal) ||
            !cached.Contains("delegationAddress=null", StringComparison.Ordinal) ||
            !cached.Contains("BuildPrecompileArray", StringComparison.Ordinal) ||
            !cached.Contains("boolTryGetCachedPrecompile", StringComparison.Ordinal) ||
            !cached.Contains("CodeInfoCreateCachedPrecompile", StringComparison.Ordinal) ||
            !cached.Contains("!precompile.SupportsCaching", StringComparison.Ordinal) ||
            !cached.Contains("caches.TryGetPartition", StringComparison.Ordinal) ||
            !cached.Contains("ReadOnlyMemory<byte>effectiveInput=precompile.NormalizeInput(inputData)", StringComparison.Ordinal) ||
            !cached.Contains("new(address,effectiveInput,releaseSpec)", StringComparison.Ordinal) ||
            !cached.Contains("cache.TryGet(key,outResult<byte[]>result)", StringComparison.Ordinal) ||
            !cached.Contains("result=precompile.Run(inputData,releaseSpec)", StringComparison.Ordinal) ||
            !cached.Contains("if(resultis{IsError:true,Error:Errors.InvalidInputLength})returnresult", StringComparison.Ordinal) ||
            !cached.Contains("cache.TryAdd(key,result)", StringComparison.Ordinal))
        {
            throw new ExtractionException("Cached precompile resolution no longer has its exact key, normalization, or effects.");
        }

        string caches = Canonical(Text(snapshot, "src/Nethermind/Nethermind.Evm/State/PrecompileCaches.cs"));
        if (!caches.Contains("ReferenceEquals(Spec,other.Spec)", StringComparison.Ordinal) ||
            !caches.Contains("Address==other.Address", StringComparison.Ordinal) ||
            !caches.Contains("Data.Span.SequenceEqual(other.Data.Span)", StringComparison.Ordinal))
        {
            throw new ExtractionException("Precompile cache key identity changed.");
        }

        string prewarmer = Canonical(Text(snapshot, "src/Nethermind/Nethermind.Init/Modules/PrewarmerModule.cs"));
        int decoratorCount = Count(prewarmer, "AddDecorator<ICodeInfoRepository>");
        if (decoratorCount != 1 || !prewarmer.Contains("newPrecompileCachedCodeInfoRepository", StringComparison.Ordinal))
        {
            throw new ExtractionException("Unknown or missing ICodeInfoRepository cache decorator.");
        }

        string cacheContract = Canonical(Text(snapshot, "src/Nethermind/Nethermind.Evm/Precompiles/IPrecompile.cs"));
        if (!cacheContract.Contains("ReadOnlyMemory<byte>NormalizeInput(ReadOnlyMemory<byte>inputData)", StringComparison.Ordinal) ||
            !cacheContract.Contains("boolSupportsCaching=>true", StringComparison.Ordinal))
        {
            throw new ExtractionException("IPrecompile normalization/cache contract changed.");
        }

        string repositoryContract = Canonical(Text(snapshot, "src/Nethermind/Nethermind.Evm/ICodeInfoRepository.cs"));
        if (!repositoryContract.Contains("publicstaticCodeInfoGetCachedCodeInfoNoDelegation(thisICodeInfoRepositorycodeInfoRepository,AddresscodeSource,IReleaseSpecvmSpec)", StringComparison.Ordinal) ||
            !repositoryContract.Contains("GetCachedCodeInfo(codeSource,false,vmSpec,out_)", StringComparison.Ordinal))
        {
            throw new ExtractionException("The no-delegation code-info selector changed.");
        }
    }

    private static void ValidateVmRouting(Snapshot snapshot)
    {
        string call = Canonical(Text(snapshot, "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.cs"));
        string direct = Canonical(Text(snapshot, "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.std.cs"));
        string vm = Canonical(Text(snapshot, "src/Nethermind/Nethermind.Evm/VirtualMachine.cs"));
        string result = Canonical(Text(snapshot, "src/Nethermind/Nethermind.Evm/VirtualMachine.CallResult.cs"));
        string selector = Canonical(Text(snapshot, "src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs"));
        string dispatch = Canonical(Text(snapshot, "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs"));

        string[] callOrder =
        [
            "TryConsumeAccountAccessGas",
            "GetCachedCodeInfo(codeSource,followDelegation:false",
            "GetCachedCodeInfoNoDelegation(delegated,spec)",
            "CreateFullCallFrame",
        ];
        RequireOrdered(call, callOrder, "CALL-family route");
        if (!call.Contains("Addresstarget=TOpCall.ExecutionType!=ExecutionType.DELEGATECALL&&TOpCall.ExecutionType!=ExecutionType.CALLCODE?codeSource:env.ExecutingAccount;", StringComparison.Ordinal) ||
            !call.Contains("chargesNewAccount", StringComparison.Ordinal))
        {
            throw new ExtractionException("CALLCODE executing-account target or new-account charge binding changed.");
        }

        if (!direct.Contains("TTracingInst.IsActive||vm.IsTracingActions||!vm.CanExecutePrecompileCallDirectly", StringComparison.Ordinal) ||
            !direct.Contains("TryConsumePrecompileGas", StringComparison.Ordinal) ||
            !direct.Contains("TryRunPrecompileDirectly", StringComparison.Ordinal) ||
            !direct.Contains("AddToBalanceAndCreateIfNotExists", StringComparison.Ordinal) ||
            !direct.Contains("vm.ReturnDataBuffer=output", StringComparison.Ordinal))
        {
            throw new ExtractionException("Standard direct STATICCALL route or its guard changed.");
        }

        if (!call.Contains("if(TOpCall.ExecutionType==ExecutionType.STATICCALL&&codeInfo.Precompileis{}precompile&&TryInlineStaticPrecompileCall", StringComparison.Ordinal) ||
            !call.Contains("codeInfo=spec.IsPrecompile(delegated)?CodeInfo.Empty:vm.CodeInfoRepository.GetCachedCodeInfoNoDelegation(delegated,spec)", StringComparison.Ordinal))
        {
            throw new ExtractionException("Direct STATICCALL selection or delegated-precompile suppression changed.");
        }

        if (direct.Contains("Snapshot", StringComparison.Ordinal) || direct.Contains("RestoreSnapshot", StringComparison.Ordinal) ||
            !direct.Contains("if(!TGasPolicy.TryConsumePrecompileGas", StringComparison.Ordinal) ||
            !direct.Contains("TGasPolicy.RestoreChildStateGasOnHalt(refgas,inchildGas)", StringComparison.Ordinal) ||
            !direct.Contains("vm.ReturnDataBuffer=default", StringComparison.Ordinal) ||
            !direct.Contains("stack.PushZero<TTracingInst,OnFlag>()", StringComparison.Ordinal))
        {
            throw new ExtractionException("Direct pricing/leaf failure must restore state gas and clear return data without a snapshot restore.");
        }

        RequireOrdered(direct,
            [
                "TryConsumePrecompileGas",
                "RestoreChildStateGasOnHalt",
                "vm.ReturnDataBuffer=default",
                "stack.PushZero<TTracingInst,OnFlag>()",
            ],
            "direct pricing-failure residue");
        RequireOrdered(direct,
            [
                "TryRunPrecompileDirectly",
                "TGasPolicy.ClearExecutionGas(refchildGas)",
                "TGasPolicy.RestoreChildStateGasOnHalt(refgas,inchildGas)",
                "vm.ReturnDataBuffer=default",
                "stack.PushZero<TTracingInst,OnFlag>()",
            ],
            "direct leaf-failure residue");

        string[] directOrder =
        [
            "TryLoad",
            "TryConsumePrecompileGas",
            "TryRunPrecompileDirectly",
            "AddToBalanceAndCreateIfNotExists",
            "vm.ReturnDataBuffer=output",
            "TrySave",
        ];
        RequireOrdered(direct, directOrder, "direct STATICCALL order");
        ValidateDirectOutputCopyOrdering(snapshot);

        if (!vm.Contains("_currentState.IsPrecompile", StringComparison.Ordinal) ||
            !vm.Contains("ExecutePrecompile(_currentState", StringComparison.Ordinal) ||
            !vm.Contains("RunPrecompile(currentState)", StringComparison.Ordinal) ||
            !vm.Contains("TryConsumePrecompileGas", StringComparison.Ordinal) ||
            !vm.Contains("ExecutePrecompileCall", StringComparison.Ordinal) ||
            !vm.Contains("PrecompileFailure", StringComparison.Ordinal) ||
            !vm.Contains("PrecompileOutOfGasException", StringComparison.Ordinal) ||
            !vm.Contains("PrecompileExecutionFailureException", StringComparison.Ordinal) ||
            !vm.Contains("Environment.Exit(ExitCodes.MissingPrecompile)", StringComparison.Ordinal) ||
            !vm.Contains("!codeSource.Equals(Ripemd160Address)", StringComparison.Ordinal) ||
            !vm.Contains("currentState.IsPrecompile&&currentState.IsTopLevel", StringComparison.Ordinal) ||
            !vm.Contains("failure=VirtualMachineStatics.PrecompileExecutionFailureException", StringComparison.Ordinal) ||
            !vm.Contains("exceptionType:!success?EvmExceptionType.PrecompileFailure:EvmExceptionType.None", StringComparison.Ordinal))
        {
            throw new ExtractionException("Full-frame precompile execution or result classification changed.");
        }

        if (!vm.Contains("isTracingActions", StringComparison.Ordinal) ||
            !vm.Contains("state.ExecutionType.GetBalanceCredit(instate.Env.Value)", StringComparison.Ordinal) ||
            !vm.Contains("TGasPolicy.RestoreChildStateGasOnHalt(ref_currentState.Gas,inchildState.Gas)", StringComparison.Ordinal))
        {
            throw new ExtractionException("Full-frame action, balance-credit, or halt state-gas bindings changed.");
        }

        if (!selector.Contains("EvmInstructions.InstructionCall<TGasPolicy,TOpCall,TTracingInst,TEip8037,TEip7708,TSpec>(refstack,refgas,vm)", StringComparison.Ordinal))
        {
            throw new ExtractionException("The actual standard InstructionCall opcode selector is not admitted.");
        }

        if (!dispatch.Contains("CancellationCheckMask=1023", StringComparison.Ordinal) ||
            !dispatch.Contains("cancelableState.OpCodeCount&CancellationCheckMask", StringComparison.Ordinal) ||
            !dispatch.Contains("ThrowOperationCanceledException()", StringComparison.Ordinal))
        {
            throw new ExtractionException("Cancelable dispatch boundary or cancellation exit changed.");
        }

        MethodDeclarationSyntax dispatchLoop = FindMethod(
            snapshot,
            "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs",
            "VirtualMachine",
            "RunDispatchLoop",
            method => method.TypeParameterList?.Parameters.Count == 2);
        IfStatementSyntax[] cancellationPolls = dispatchLoop.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(statement => Canonical(statement.Condition).Contains("_txTracer.IsCancelled", StringComparison.Ordinal))
            .ToArray();
        IfStatementSyntax[] boundaryCandidates = dispatchLoop.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(statement => Canonical(statement.Condition).Contains("exceptionType!=EvmExceptionType.None", StringComparison.Ordinal) &&
                Canonical(statement.Condition).Contains("cancelableState.OpCodeCount&CancellationCheckMask", StringComparison.Ordinal) &&
                Canonical(statement.Condition).Contains("(nuint)cancelableState.FinalProgramCounter>=(nuint)stack.CodeLength", StringComparison.Ordinal))
            .ToArray();
        if (cancellationPolls.Length != 2 || boundaryCandidates.Length != 1)
        {
            throw new ExtractionException("Cancelable dispatch must have exactly two cancellation polls and one 1024-opcode boundary check.");
        }

        IfStatementSyntax boundaryCheck = boundaryCandidates[0];
        if (cancellationPolls.Any(static statement => !IsExactCancellationPoll(statement)) ||
            !IsExactCancellationBoundaryCondition(boundaryCheck.Condition) ||
            !IsSingleBreak(boundaryCheck.Statement) ||
            !IsImmediatelyFollowedBy(boundaryCheck, cancellationPolls[1]))
        {
            throw new ExtractionException("Cancelable dispatch cancellation operators, polarities, body, or boundary composition changed.");
        }

        IfStatementSyntax specializedDispatch = dispatchLoop.DescendantNodes().OfType<IfStatementSyntax>()
            .SingleOrDefault(statement => Canonical(statement.Condition) == "!TCancelable.IsActive")
            ?? throw new ExtractionException("Cancelable dispatch specialization guard is missing.");

        RequireAstOrdered(
            dispatchLoop,
            [
                node => node == specializedDispatch,
                node => node == cancellationPolls[0],
                node => node == boundaryCheck,
                node => node == cancellationPolls[1],
            ],
            "cancelable dispatch poll/boundary order");
        if (dispatchLoop.DescendantNodes().OfType<IfStatementSyntax>()
            .Count(statement => Canonical(statement.Condition).Contains("_txTracer.IsCancelled", StringComparison.Ordinal)) != 2)
        {
            throw new ExtractionException("Cancelable dispatch contains an unexpected cancellation poll.");
        }

        ValidateFrameOutcomeOrdering(snapshot);

        string executionType = Canonical(Text(snapshot, "src/Nethermind/Nethermind.Evm/ExecutionType.cs"));
        if (!executionType.Contains("GetBalanceCredit(thisExecutionTypeexecutionType,inUInt256value)", StringComparison.Ordinal) ||
            !executionType.Contains("ExecutionType.CALLCODE", StringComparison.Ordinal))
        {
            throw new ExtractionException("ExecutionType.GetBalanceCredit or CALLCODE balance-credit semantics changed.");
        }

        string gasPolicy = Canonical(Text(snapshot, "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs"));
        string adapter = Canonical(Text(snapshot, "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionAdapterKernel.cs"));
        string transition = Canonical(Text(snapshot, "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs"));
        if (!gasPolicy.Contains("publicstaticvoidRefund(refEthereumGasPolicygas,inEthereumGasPolicychildGas)", StringComparison.Ordinal) ||
            !gasPolicy.Contains("publicstaticvoidRestoreChildStateGasOnHalt(refEthereumGasPolicyparentGas,inEthereumGasPolicychildGas)", StringComparison.Ordinal) ||
            !gasPolicy.Contains("StateGasTransitionAdapterKernel.Refund(", StringComparison.Ordinal) ||
            !gasPolicy.Contains("StateGasTransitionAdapterKernel.RestoreChildStateGasOnHalt(", StringComparison.Ordinal) ||
            !adapter.Contains("publicstaticStateGasTransitionAdapterOutcomeRefund(", StringComparison.Ordinal) ||
            !adapter.Contains("publicstaticStateGasTransitionAdapterOutcomeRestoreChildStateGasOnHalt(", StringComparison.Ordinal) ||
            !adapter.Contains("StateGasTransitionKernel.Refund(", StringComparison.Ordinal) ||
            !adapter.Contains("StateGasTransitionKernel.RestoreChildStateGasOnHalt(", StringComparison.Ordinal) ||
            !transition.Contains("publicstaticStateGasTransitionResultRefund(", StringComparison.Ordinal) ||
            !transition.Contains("publicstaticStateGasTransitionResultRestoreChildStateGasOnHalt(", StringComparison.Ordinal))
        {
            throw new ExtractionException("Refund/halt state-gas adapter-to-kernel transitive path changed.");
        }

        if (!result.Contains("PrecompileSuccess", StringComparison.Ordinal) ||
            !result.Contains("SubstateError", StringComparison.Ordinal) ||
            !result.Contains("ExceptionType", StringComparison.Ordinal))
        {
            throw new ExtractionException("CallResult no longer exposes the required precompile status fields.");
        }

        string handlers = Canonical(Text(snapshot, "src/Nethermind/Nethermind.Evm/VirtualMachine.ExecutionHandlers.cs"));
        if (!handlers.Contains("RunPrecompile", StringComparison.Ordinal) ||
            !handlers.Contains("Eip158", StringComparison.Ordinal))
        {
            throw new ExtractionException("EIP-158 precompile handler selection changed.");
        }
    }

    private static void ValidateDirectOutputCopyOrdering(Snapshot snapshot)
    {
        MethodDeclarationSyntax direct = FindMethod(
            snapshot,
            "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.std.cs",
            "EvmInstructions",
            "TryInlineStaticPrecompileCall",
            method => method.TypeParameterList?.Parameters.Count == 2);
        IfStatementSyntax[] outputCopies = direct.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(IsOutputCopyGuard)
            .ToArray();
        if (outputCopies.Length != 1)
        {
            throw new ExtractionException("Direct precompile output-copy guard is missing or ambiguous.");
        }

        IfStatementSyntax outputCopy = outputCopies[0];
        IfStatementSyntax[] saveFailures = outputCopy.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(IsTrySaveFailure)
            .ToArray();
        if (saveFailures.Length != 1 || !IsDirectOutOfGasFailureBody(saveFailures[0].Statement))
        {
            throw new ExtractionException("Direct output-copy OOG residue must be the exact TrySave failure branch.");
        }

        IfStatementSyntax saveFailure = saveFailures[0];
        if (direct.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Count(invocation => Canonical(invocation.Expression) == "vm.VmState.Memory.TrySave") != 1)
        {
            throw new ExtractionException("Direct output-copy OOG must contain exactly one Memory.TrySave operation.");
        }

        RequireAstOrdered(
            direct,
            [
                node => IsInvocation(node, "TGasPolicy.TryConsumePrecompileGas"),
                node => IsInvocation(node, "vm.TryRunPrecompileDirectly"),
                node => IsInvocation(node, "vm.WorldState.AddToBalanceAndCreateIfNotExists"),
                node => IsInvocation(node, "TGasPolicy.Refund"),
                node => IsAssignment(node, "vm.ReturnDataBuffer", "outputData"),
                node => node == outputCopy,
                node => node == saveFailure,
                node => IsAssignment(node, "result", "EvmExceptionType.OutOfGas"),
            ],
            "direct output-copy OOG residue");
    }

    private static TrySaveBoundsDescriptor ReadTrySaveBoundsAdmission(Snapshot snapshot)
    {
        const string path = "src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs";
        MethodDeclarationSyntax method = FindMethod(
            snapshot,
            path,
            "EvmPooledMemory",
            "TrySave",
            candidate => candidate.ParameterList.Parameters.Count == 2 &&
                Canonical(candidate.ReturnType) == "bool" &&
                candidate.Modifiers.Any(token => token.IsKind(SyntaxKind.PublicKeyword)) &&
                candidate.ParameterList.Parameters[0].Modifiers.Count == 1 &&
                candidate.ParameterList.Parameters[0].Modifiers[0].IsKind(SyntaxKind.InKeyword) &&
                Canonical(candidate.ParameterList.Parameters[0].Type!) == "UInt256" &&
                candidate.ParameterList.Parameters[1].Modifiers.Count == 0 &&
                Canonical(candidate.ParameterList.Parameters[1].Type!) == "ReadOnlySpan<byte>");

        if (method.Body is not BlockSyntax body || method.ExpressionBody is not null)
        {
            throw new ExtractionException("EvmPooledMemory.TrySave must have a block body for bounds admission.");
        }

        StatementSyntax[] statements = body.Statements.ToArray();
        if (statements.Length != 6 ||
            statements[0] is not IfStatementSyntax emptyValue || !IsTrySaveEmptyValueGuard(emptyValue) ||
            statements[1] is not ExpressionStatementSyntax checkStatement ||
            checkStatement.Expression is not InvocationExpressionSyntax validationCall ||
            !IsTrySaveValidationCall(validationCall) ||
            statements[2] is not IfStatementSyntax failureGuard || !IsTrySaveViolationGuard(failureGuard) ||
            statements[3] is not ExpressionStatementSyntax updateStatement ||
            updateStatement.Expression is not InvocationExpressionSyntax updateSize ||
            !IsTrySaveUpdateSize(updateSize) ||
            statements[4] is not ExpressionStatementSyntax saveStatement ||
            saveStatement.Expression is not InvocationExpressionSyntax saveAfterGas ||
            !IsTrySaveSaveAfterGas(saveAfterGas) ||
            !IsSingleReturn(statements[5], expected: true))
        {
            throw new ExtractionException("EvmPooledMemory.TrySave bounds validation or mutation order changed.");
        }

        int failureIndex = body.Statements.IndexOf(failureGuard);
        int updateIndex = body.Statements.IndexOf(updateStatement);
        int saveIndex = body.Statements.IndexOf(saveStatement);
        if (validationCall.Expression is not IdentifierNameSyntax validationIdentifier ||
            UnwrapParentheses(failureGuard.Condition) is not IdentifierNameSyntax failureIdentifier)
        {
            throw new ExtractionException("EvmPooledMemory.TrySave bounds helper or failure flag is not an exact identifier.");
        }

        if (failureIndex < 0 || updateIndex <= failureIndex || saveIndex <= failureIndex ||
            ContainsTrySaveMutation(failureGuard.Statement) ||
            statements.Take(failureIndex).Any(statement => ContainsTrySaveMutation(statement)))
        {
            throw new ExtractionException(
                "EvmPooledMemory.TrySave must return false from the bounds branch before UpdateSize, SaveAfterGas, or any memory mutation.");
        }

        return new(
            path,
            "EvmPooledMemory",
            "TrySave",
            validationIdentifier.Identifier.ValueText,
            failureIdentifier.Identifier.ValueText,
            [
                TrySaveFailureEffect.ReturnFalse,
                TrySaveFailureEffect.NoUpdateSize,
                TrySaveFailureEffect.NoSaveAfterGas,
                TrySaveFailureEffect.NoMemoryMutation,
                TrySaveFailureEffect.NoOutputWrite,
            ],
            updateIndex > failureIndex && saveIndex > failureIndex && !ContainsTrySaveMutation(failureGuard.Statement));
    }

    private static bool IsTrySaveEmptyValueGuard(IfStatementSyntax statement) =>
        statement.Else is null &&
        UnwrapParentheses(statement.Condition) is BinaryExpressionSyntax comparison &&
        comparison.IsKind(SyntaxKind.EqualsExpression) &&
        IsMemberAccess(comparison.Left, "value", "Length") &&
        IsLiteralZero(comparison.Right) &&
        IsSingleReturn(statement.Statement, expected: true);

    private static bool IsTrySaveValidationCall(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not IdentifierNameSyntax identifier ||
            identifier.Identifier.ValueText != "CheckMemoryAccessViolation" ||
            invocation.ArgumentList.Arguments.Count != 4)
        {
            return false;
        }

        SeparatedSyntaxList<ArgumentSyntax> arguments = invocation.ArgumentList.Arguments;
        return IsTrySaveArgument(arguments[0], SyntaxKind.InKeyword, "location") &&
            arguments[1].RefKindKeyword.IsKind(SyntaxKind.None) &&
            arguments[1].Expression is CastExpressionSyntax cast &&
            Canonical(cast.Type) == "ulong" &&
            IsMemberAccess(cast.Expression, "value", "Length") &&
            IsTrySaveOutDeclaration(arguments[2], "ulong", "newLength") &&
            IsTrySaveOutDeclaration(arguments[3], "bool", "isViolation");
    }

    private static bool IsTrySaveArgument(ArgumentSyntax argument, SyntaxKind refKind, string identifier) =>
        argument.RefKindKeyword.IsKind(refKind) && IsIdentifier(argument.Expression, identifier);

    private static bool IsTrySaveOutDeclaration(ArgumentSyntax argument, string type, string identifier) =>
        argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) &&
        argument.Expression is DeclarationExpressionSyntax declaration &&
        Canonical(declaration.Type) == type &&
        declaration.Designation is SingleVariableDesignationSyntax variable &&
        variable.Identifier.ValueText == identifier;

    private static bool IsTrySaveViolationGuard(IfStatementSyntax statement) =>
        statement.Else is null && IsIdentifier(statement.Condition, "isViolation") &&
        IsSingleReturn(statement.Statement, expected: false);

    private static bool IsTrySaveUpdateSize(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not IdentifierNameSyntax identifier ||
            identifier.Identifier.ValueText != "UpdateSize" ||
            invocation.ArgumentList.Arguments.Count != 2)
        {
            return false;
        }

        ArgumentSyntax[] arguments = invocation.ArgumentList.Arguments.ToArray();
        return arguments[0].RefKindKeyword.IsKind(SyntaxKind.None) &&
            IsIdentifier(arguments[0].Expression, "newLength") &&
            arguments[1].NameColon?.Name.Identifier.ValueText == "rentIfNeeded" &&
            arguments[1].RefKindKeyword.IsKind(SyntaxKind.None) &&
            IsLiteralFalse(arguments[1].Expression);
    }

    private static bool IsTrySaveSaveAfterGas(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not IdentifierNameSyntax identifier ||
            identifier.Identifier.ValueText != "SaveAfterGas" ||
            invocation.ArgumentList.Arguments.Count != 2)
        {
            return false;
        }

        ArgumentSyntax[] arguments = invocation.ArgumentList.Arguments.ToArray();
        return arguments[0].RefKindKeyword.IsKind(SyntaxKind.InKeyword) &&
            IsIdentifier(arguments[0].Expression, "location") &&
            arguments[1].RefKindKeyword.IsKind(SyntaxKind.None) &&
            IsIdentifier(arguments[1].Expression, "value");
    }

    private static bool IsSingleReturn(StatementSyntax statement, bool expected)
    {
        if (statement is BlockSyntax block)
        {
            if (block.Statements.Count != 1)
            {
                return false;
            }

            statement = block.Statements[0];
        }

        return statement is ReturnStatementSyntax
        {
            Expression: LiteralExpressionSyntax expression,
        } && (expected ? IsLiteralTrue(expression) : IsLiteralFalse(expression));
    }

    private static bool IsLiteralFalse(ExpressionSyntax expression) =>
        UnwrapParentheses(expression) is LiteralExpressionSyntax literal &&
        literal.IsKind(SyntaxKind.FalseLiteralExpression);

    private static bool ContainsTrySaveMutation(SyntaxNode node) =>
        node.DescendantNodesAndSelf().Any(candidate => candidate switch
        {
            AssignmentExpressionSyntax => true,
            PrefixUnaryExpressionSyntax prefix => prefix.IsKind(SyntaxKind.PreIncrementExpression) ||
                prefix.IsKind(SyntaxKind.PreDecrementExpression),
            PostfixUnaryExpressionSyntax postfix => postfix.IsKind(SyntaxKind.PostIncrementExpression) ||
                postfix.IsKind(SyntaxKind.PostDecrementExpression),
            InvocationExpressionSyntax invocation => IsTrySaveMutationInvocation(invocation),
            _ => false,
        });

    private static bool IsTrySaveMutationInvocation(InvocationExpressionSyntax invocation)
    {
        string name = invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            _ => string.Empty,
        };
        return name is "UpdateSize" or "SaveAfterGas" or "Save" or "CopyTo" or "CommitOverwrite" or
            "PrepareOverwriteAfterGas" or "Clear" or "Write";
    }

    private static void ValidateFrameOutcomeOrdering(Snapshot snapshot)
    {
        const string vmPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.cs";
        MethodDeclarationSyntax executeTransaction = FindMethod(
            snapshot, vmPath, "VirtualMachine", "ExecuteTransaction",
            method => method.TypeParameterList?.Parameters.Count == 1);
        MethodDeclarationSyntax executePrecompile = FindMethod(snapshot, vmPath, "VirtualMachine", "ExecutePrecompile");
        MethodDeclarationSyntax runPrecompile = FindMethod(
            snapshot, vmPath, "VirtualMachine", "RunPrecompile",
            method => method.TypeParameterList?.Parameters.Count == 1);
        MethodDeclarationSyntax handleFailure = FindMethod(
            snapshot, vmPath, "VirtualMachine", "HandleFailure",
            method => method.TypeParameterList?.Parameters.Count == 1);
        MethodDeclarationSyntax handleRevert = FindMethod(snapshot, vmPath, "VirtualMachine", "HandleRevert");
        MethodDeclarationSyntax handleReturn = FindMethod(
            snapshot, vmPath, "VirtualMachine", "HandleRegularReturn",
            method => method.TypeParameterList?.Parameters.Count == 1);
        MethodDeclarationSyntax parentRestore = FindMethod(snapshot, vmPath, "VirtualMachine", "PopAndRestoreParentState");

        RequireAstOrdered(
            runPrecompile,
            [
                node => IsInvocation(node, "TGasPolicy.TryConsumePrecompileGas"),
                node => IsInvocation(node, "ExecutePrecompileCall"),
            ],
            "full-frame price-before-run");
        IfStatementSyntax pricingFailure = runPrecompile.DescendantNodes().OfType<IfStatementSyntax>()
            .SingleOrDefault(statement => Canonical(statement.Condition).StartsWith("!TGasPolicy.TryConsumePrecompileGas", StringComparison.Ordinal))
            ?? throw new ExtractionException("Full-frame pricing failure guard is missing.");
        if (!pricingFailure.DescendantNodes().OfType<ReturnStatementSyntax>().Any(statement =>
                statement.Expression is not null && Canonical(statement.Expression).StartsWith("new(default", StringComparison.Ordinal)) ||
            pricingFailure.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Any(invocation => Canonical(invocation.Expression) == "ExecutePrecompileCall"))
        {
            throw new ExtractionException("Full-frame pricing failure must return before ExecutePrecompileCall.");
        }

        RequireAstOrdered(
            handleFailure,
            [
                node => IsInvocation(node, "_worldState.Restore"),
                node => IsAssignment(node, "ReturnDataBuffer", "default"),
                node => IsInvocation(node, "PopAndRestoreParentState"),
            ],
            "hard returned/pricing failure residue");

        RequireAstOrdered(
            parentRestore,
            [
                node => IsInvocation(node, "RemoveAdvancedStateGasRefund"),
                node => IsInvocation(node, "TGasPolicy.RestoreChildStateGasOnHalt"),
            ],
            "hard failure parent state-gas restoration");

        RequireAstOrdered(
            handleRevert,
            [
                node => IsInvocation(node, "_worldState.Restore"),
                node => IsAssignment(node, "ReturnDataBuffer", "outputBytes"),
            ],
            "managed nested HandleRevert snapshot/returndata");

        RequireAstOrdered(
            executeTransaction,
            [
                node => IsInvocation(node, "TGasPolicy.RestoreChildStateGas"),
                node => IsInvocation(node, "HandleRevert"),
            ],
            "managed nested exception state-gas/HandleRevert order");

        RequireAstOrdered(
            executeTransaction,
            [
                node => IsInvocation(node, "TGasPolicy.Refund"),
                node => node is InvocationExpressionSyntax invocation &&
                    Canonical(invocation.Expression).StartsWith("HandleRegularReturn<", StringComparison.Ordinal),
                node => IsInvocation(node, "previousState.CommitToParent"),
            ],
            "successful child refund/return/commit order");

        RequireAstOrdered(
            executePrecompile,
            [
                node => IsInvocation(node, "TGasPolicy.ClearExecutionGas"),
                node => node is ReturnStatementSyntax statement && statement.Expression is not null &&
                    Canonical(statement.Expression) == "callResult",
            ],
            "managed precompile exception execution-gas clear");

        RequireAstOrdered(
            executePrecompile,
            [
                node => node is IfStatementSyntax statement &&
                    Canonical(statement.Condition) == "callResult.PrecompileSuccess==false",
                node => node is IfStatementSyntax statement &&
                    Canonical(statement.Condition) == "callResult.IsException",
                node => IsAssignment(node, "failure", "PrecompileOutOfGasException"),
                node => node is IfStatementSyntax statement &&
                    Canonical(statement.Condition) == "currentState.IsPrecompile&&currentState.IsTopLevel",
                node => IsAssignment(node, "failure", "VirtualMachineStatics.PrecompileExecutionFailureException"),
            ],
            "precompile returned-failure hard classification");

        if (!handleReturn.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Any(assignment => IsAssignment(assignment, "ReturnDataBuffer", "callResult.Output")))
        {
            throw new ExtractionException("Successful precompile return is missing the HandleRegularReturn returndata binding.");
        }
    }

    private static void ValidateGasPricing(Snapshot snapshot)
    {
        string kernel = Canonical(Text(snapshot, "src/Nethermind/Nethermind.Evm/GasPolicy/PrecompileGasPricingKernel.cs"));
        if (!kernel.Contains("publicstaticPrecompileGasPricingResultTryConsume(ulonggas,ulongbaseGasCost,ulongdataGasCost)", StringComparison.Ordinal) ||
            !kernel.Contains("if(baseGasCost>ulong.MaxValue-dataGasCost)", StringComparison.Ordinal) ||
            !kernel.Contains("PrecompileGasPricingOutcome.BaseDataOverflow,gas,0", StringComparison.Ordinal) ||
            !kernel.Contains("if(gas<totalGasCost)", StringComparison.Ordinal) ||
            !kernel.Contains("PrecompileGasPricingOutcome.OutOfGas,0,0", StringComparison.Ordinal) ||
            !kernel.Contains("PrecompileGasPricingOutcome.Success,gas-totalGasCost,totalGasCost", StringComparison.Ordinal))
        {
            throw new ExtractionException("Precompile gas kernel no longer has the pinned overflow/OOG/success cases.");
        }

        string adapter = Canonical(Text(snapshot, "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs"));
        if (!adapter.Contains("PrecompileGasPricingKernel.TryConsume(gas.Value,baseGasCost,dataGasCost)", StringComparison.Ordinal) ||
            !adapter.Contains("gas.Value=result.RemainingGas", StringComparison.Ordinal) ||
            !adapter.Contains("result.OutcomeisPrecompileGasPricingOutcome.Success", StringComparison.Ordinal))
        {
            throw new ExtractionException("EthereumGasPolicy precompile gas adapter changed.");
        }

        string generic = Canonical(Text(snapshot, "src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs"));
        if (!generic.Contains("baseGasCost<=ulong.MaxValue-dataGasCost", StringComparison.Ordinal) ||
            !generic.Contains("TSelf.UpdateGas(refgas,baseGasCost+dataGasCost)", StringComparison.Ordinal))
        {
            throw new ExtractionException("Generic precompile gas route no longer retains its overflow-before-debit ordering.");
        }
    }

    private static void ValidateLeafBindings(Snapshot snapshot)
    {
        foreach (PrecompileBinding binding in Bindings)
        {
            ParsedSource baseSource = snapshot.Parsed[binding.BaseSourcePath];
            ClassDeclarationSyntax declaration = FindClass(baseSource.Root, binding.TypeName);
            RequireMember(declaration, "Address", binding.Name);
            RequireMember(declaration, binding.BaseGasMember, binding.Name);
            RequireMember(declaration, binding.DataGasMember, binding.Name);
            RequireMember(declaration, binding.RunMember, binding.Name);
            if (!HasMember(declaration, binding.NormalizeMember))
            {
                TypeDeclarationSyntax contract = FindType(snapshot.Parsed["src/Nethermind/Nethermind.Evm/Precompiles/IPrecompile.cs"].Root,
                    "IPrecompile", binding.NormalizeMember);
                RequireMember(contract, binding.NormalizeMember, $"{binding.Name} default input normalization");
            }

            foreach (string standardPath in binding.StandardSourcePaths)
            {
                string fullPath = $"src/Nethermind/Nethermind.Evm.Precompiles/{standardPath}";
                ClassDeclarationSyntax standard = FindClass(snapshot.Parsed[fullPath].Root, StandardOwner(standardPath, binding.TypeName));
                RequireMember(standard, StandardMember(standardPath, binding.RunMember), binding.Name);
                if (fullPath.Contains("zkevm", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ExtractionException($"ZK implementation entered standard leaf closure: {fullPath}.");
                }
            }
        }
    }

    private static void ValidateNativeDependencies(Snapshot snapshot)
    {
        string project = Canonical(Text(snapshot, "src/Nethermind/Nethermind.Evm.Precompiles/Nethermind.Evm.Precompiles.csproj"));
        foreach (NativePackageSpec package in NativePackages)
        {
            if (!project.Contains($"PackageReferenceInclude=\"{package.Name}\"", StringComparison.Ordinal))
            {
                throw new ExtractionException($"Native/helper route {package.Name} is unresolved in the precompile project.");
            }
        }

        string[] requiredHelpers =
        [
            "EthereumEcdsa.RecoverAddressRaw",
            "KeccakCache",
            "Ripemd",
            "Blake2Compression",
            "KzgPolynomialCommitments",
            "BN254",
            "Eip2537",
        ];
        foreach (string helper in requiredHelpers)
        {
            if (!snapshot.Sources.Any(source => Text(snapshot, source.Path).Contains(helper, StringComparison.Ordinal)))
            {
                throw new ExtractionException($"Named native/helper route {helper} is not present in the closed source set.");
            }
        }

        string initializer = Canonical(Text(snapshot, "src/Nethermind/Nethermind.Init/Steps/InitializePrecompiles.cs"));
        if (!initializer.Contains("KzgPolynomialCommitments.InitializeAsync", StringComparison.Ordinal) ||
            !initializer.Contains("IsEip4844Enabled", StringComparison.Ordinal))
        {
            throw new ExtractionException("KZG initialization prerequisite is unresolved.");
        }
    }

    private static void ValidateOracleBindings(Snapshot snapshot)
    {
        foreach (PrecompileBinding binding in Bindings)
        {
            string path = $"tools/Evm/Lean/Eip803x/Precompiles/{OracleFileName(binding)}";
            DependencyIdentity dependency = snapshot.Dependencies.Single(item => item.Path == path);
            string source = File.ReadAllText(ResolveExactPath(snapshot.Root, path));
            if (!dependency.OracleOnly || !HasExactLeanNamespace(source, binding.OracleNamespace) ||
                CountExactLeanDefinitions(source, binding.OracleEntrySymbol) != 1 ||
                ParseLeanAddress(source) != binding.Address || !HasExactProductionOracleSymbol(snapshot, binding))
            {
                throw new ExtractionException($"Handwritten oracle binding or production symbol is unresolved for {binding.Name}.");
            }
        }
    }

    private static bool HasExactProductionOracleSymbol(Snapshot snapshot, PrecompileBinding binding)
    {
        string[] paths = [
            binding.BaseSourcePath,
            ..binding.StandardSourcePaths.Select(path => $"src/Nethermind/Nethermind.Evm.Precompiles/{path}"),
        ];
        int matchingSources = 0;
        foreach (string path in paths.Distinct(StringComparer.Ordinal))
        {
            if (snapshot.Parsed.TryGetValue(path, out ParsedSource? source) &&
                HasExactProductionOracleSymbol(source.Root, binding.OracleSymbol))
            {
                matchingSources++;
            }
        }

        return matchingSources == 1;
    }

    private static bool HasExactProductionOracleSymbol(CompilationUnitSyntax root, string symbol)
    {
        string[] qualified = symbol.Split("::", StringSplitOptions.None);
        if (qualified.Length == 1)
        {
            string expected = Canonical(symbol);
            return root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                .Count(access => Canonical(access) == expected && IsInvocationTarget(access)) == 1;
        }

        if (qualified.Length != 2 || string.IsNullOrWhiteSpace(qualified[0]) || string.IsNullOrWhiteSpace(qualified[1]))
        {
            return false;
        }

        string owner = Canonical(qualified[0]);
        string expression = Canonical(qualified[1]);
        string[] expressionParts = expression.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (expressionParts.Length == 0)
        {
            return false;
        }

        string member = expressionParts[^1];
        if (expressionParts.Length == 2)
        {
            string receiver = expressionParts[0];
            return root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                .Count(access => Canonical(access) == expression &&
                    IsInvocationTarget(access) && HasProductionReceiver(root, access.Expression, receiver, owner)) == 1;
        }

        if (expressionParts.Length != 1)
        {
            return false;
        }

        string? alias = root.Usings
            .Where(usingDirective => usingDirective.Name is not null && usingDirective.Alias is not null && Canonical(usingDirective.Name) == owner)
            .Select(usingDirective => Canonical(usingDirective.Alias!.Name))
            .SingleOrDefault();
        if (alias is null)
        {
            return false;
        }

        return root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(access => access.Name.Identifier.ValueText == member)
            .Count(access => IsInvocationTarget(access) && HasProductionReceiver(root, access.Expression, alias, alias)) == 1;
    }

    private static bool IsInvocationTarget(MemberAccessExpressionSyntax access) =>
        access.Parent is InvocationExpressionSyntax invocation && invocation.Expression == access;

    private static bool HasProductionReceiver(
        CompilationUnitSyntax root,
        ExpressionSyntax expression,
        string receiver,
        string owner)
    {
        string canonicalReceiver = Canonical(expression);
        if (expression is ObjectCreationExpressionSyntax creation && Canonical(creation.Type) == receiver)
        {
            return true;
        }

        if (canonicalReceiver.Length == 0)
        {
            return false;
        }

        return root.DescendantNodes().OfType<VariableDeclarationSyntax>().Any(declaration =>
            (Canonical(declaration.Type) == receiver || Canonical(declaration.Type) == owner) &&
            declaration.Variables.Any(variable => variable.Identifier.ValueText == canonicalReceiver));
    }

    private static bool HasExactLeanNamespace(string source, string expected)
    {
        string[] parts = expected.Split('.', StringSplitOptions.RemoveEmptyEntries);
        string dotted = $"namespace {expected}";
        if (source.Split('\n').Select(static line => line.Trim()).Any(line => line.Equals(dotted, StringComparison.Ordinal)))
            return true;

        if (parts.Length == 3)
        {
            string[] lines = source.Split('\n').Select(static line => line.Trim()).ToArray();
            for (int index = 0; index + 2 < lines.Length; index++)
            {
                if (lines[index].Equals($"namespace {parts[0]}", StringComparison.Ordinal) &&
                    lines[index + 1].Equals($"namespace {parts[1]}", StringComparison.Ordinal) &&
                    lines[index + 2].Equals($"namespace {parts[2]}", StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    private static bool HasExactLeanDefinition(string source, string symbol) =>
        CountExactLeanDefinitions(source, symbol) == 1;

    private static int CountExactLeanDefinitions(string source, string symbol)
    {
        string prefix = $"def {symbol}";
        return source.Split('\n').Select(static line => line.TrimStart()).Count(line =>
            line.StartsWith(prefix, StringComparison.Ordinal) &&
            (line.Length == prefix.Length || line[prefix.Length] is ' ' or '\t' or '(' or ':'));
    }

    private static int ParseLeanAddress(string source)
    {
        const string prefix = "def address : Nat :=";
        string[] candidates = source.Split('\n').Select(static line => line.Trim())
            .Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (candidates.Length != 1)
        {
            throw new ExtractionException("A handwritten oracle must contain exactly one address definition.");
        }

        return ParseAddressNumber(candidates[0][prefix.Length..].Trim(), "Lean oracle address");
    }

    private static IrDocument BuildIr(Snapshot snapshot)
    {
        TrySaveBoundsDescriptor trySaveBounds = ReadTrySaveBoundsAdmission(snapshot);
        ProviderEntry[] providers = ParseProviderEntries(
            snapshot.Parsed["src/Nethermind/Nethermind.Blockchain/EthereumPrecompileProvider.cs"]);
        AddressEntry[] addresses = ParseAddressEntries(
            snapshot.Parsed["src/Nethermind/Nethermind.Core/Precompiles/PrecompiledAddresses.cs"]);
        ForkActivationStep[] ancestry = ParseForkActivations(snapshot);
        CallKind[] callKinds = [CallKind.Call, CallKind.CallCode, CallKind.DelegateCall, CallKind.StaticCall];
        CallTargetKind[] callTargets =
            [CallTargetKind.CodeSource, CallTargetKind.ExecutingAccount, CallTargetKind.ExecutingAccount, CallTargetKind.CodeSource];
        CancellationDescriptor cancellation = new(
            true,
            1023,
            true,
            true,
            "cancelable && completedWithoutException && (opcodeCount & checkMask) = 0 && nextProgramCounter < codeLength",
            "_txTracer.IsCancelled before first opcode",
            "_txTracer.IsCancelled after a complete batch and successor bounds check",
            "pre-dispatch poll -> opcode batch -> boundary eligibility -> boundary poll");
        FrameOutcomeDescriptor[] fullFrameOutcomes =
        [
            new(
                FrameOutcome.PricingHardFailure,
                [
                    FrameEffect.TransferLog,
                    FrameEffect.AccountTouchOrCreate,
                    FrameEffect.RipemdTouchLatch,
                    FrameEffect.Pricing,
                    FrameEffect.SnapshotRestore,
                    FrameEffect.ReturnDataClear,
                    FrameEffect.StateGasRestore,
                    FrameEffect.StackFailure,
                ],
                false,
                true,
                false,
                true,
                false,
                true),
            new(
                FrameOutcome.ReturnedLeafHardFailure,
                [
                    FrameEffect.TransferLog,
                    FrameEffect.AccountTouchOrCreate,
                    FrameEffect.RipemdTouchLatch,
                    FrameEffect.Pricing,
                    FrameEffect.Run,
                    FrameEffect.SnapshotRestore,
                    FrameEffect.ReturnDataClear,
                    FrameEffect.StateGasRestore,
                    FrameEffect.StackFailure,
                ],
                true,
                true,
                false,
                true,
                false,
                true),
            new(
                FrameOutcome.ManagedNestedSoftRevert,
                [
                    FrameEffect.TransferLog,
                    FrameEffect.AccountTouchOrCreate,
                    FrameEffect.RipemdTouchLatch,
                    FrameEffect.Pricing,
                    FrameEffect.Run,
                    FrameEffect.ExecutionGasClear,
                    FrameEffect.StateGasRestore,
                    FrameEffect.HandleRevert,
                    FrameEffect.SnapshotRestore,
                    FrameEffect.ReturnData,
                    FrameEffect.StackFailure,
                ],
                true,
                true,
                false,
                false,
                false,
                true),
            new(
                FrameOutcome.Success,
                [
                    FrameEffect.TransferLog,
                    FrameEffect.AccountTouchOrCreate,
                    FrameEffect.RipemdTouchLatch,
                    FrameEffect.Pricing,
                    FrameEffect.Run,
                    FrameEffect.ChildRefund,
                    FrameEffect.HandleReturn,
                    FrameEffect.ReturnData,
                    FrameEffect.ChildCommit,
                    FrameEffect.StackSuccess,
                ],
                true,
                true,
                true,
                false,
                true,
                false),
        ];
        DirectFrameOutcomeDescriptor[] directOutcomes =
        [
            new(
                DirectFrameOutcome.PricingHardFailure,
                DirectResult.StackFailure,
                [
                    FrameEffect.Pricing,
                    FrameEffect.StateGasRestore,
                    FrameEffect.ReturnDataClear,
                    FrameEffect.StackFailure,
                ],
                false,
                false,
                false,
                true,
                false,
                false),
            new(
                DirectFrameOutcome.ReturnedLeafHardFailure,
                DirectResult.StackFailure,
                [
                    FrameEffect.Pricing,
                    FrameEffect.Run,
                    FrameEffect.ExecutionGasClear,
                    FrameEffect.StateGasRestore,
                    FrameEffect.ReturnDataClear,
                    FrameEffect.StackFailure,
                ],
                true,
                false,
                false,
                true,
                false,
                false),
            new(
                DirectFrameOutcome.ManagedNestedSoftRevert,
                DirectResult.StackFailure,
                [
                    FrameEffect.Pricing,
                    FrameEffect.Run,
                    FrameEffect.ExecutionGasClear,
                    FrameEffect.StateGasRestore,
                    FrameEffect.ReturnDataClear,
                    FrameEffect.StackFailure,
                ],
                true,
                false,
                false,
                true,
                false,
                false),
            new(
                DirectFrameOutcome.OutputCopyOutOfGas,
                DirectResult.OutOfGas,
                [
                    FrameEffect.Pricing,
                    FrameEffect.Run,
                    FrameEffect.AccountTouchOrCreate,
                    FrameEffect.ChildRefund,
                    FrameEffect.ReturnData,
                    FrameEffect.ReturnOutOfGas,
                ],
                true,
                true,
                true,
                false,
                false,
                false),
            new(
                DirectFrameOutcome.Success,
                DirectResult.StackSuccess,
                [
                    FrameEffect.Pricing,
                    FrameEffect.Run,
                    FrameEffect.AccountTouchOrCreate,
                    FrameEffect.ChildRefund,
                    FrameEffect.ReturnData,
                    FrameEffect.OutputCopy,
                    FrameEffect.StackSuccess,
                ],
                true,
                true,
                true,
                false,
                true,
                false),
        ];
        string directSuccessOrder = DescribeDirectSuccessOrder(directOutcomes);
        string directFailureOrder = DescribeDirectFailureOrder(directOutcomes);
        CacheBranch[] cacheBranches =
        [
            new(CacheOutcome.Uncached, [CacheEffect.ReturnUncached], false, true, false),
            new(CacheOutcome.Hit, [CacheEffect.NormalizeInput, CacheEffect.BuildKey, CacheEffect.Lookup, CacheEffect.ReturnCached], true, false, false),
            new(CacheOutcome.Miss, [CacheEffect.NormalizeInput, CacheEffect.BuildKey, CacheEffect.Lookup, CacheEffect.RunOriginalInput, CacheEffect.UpdateCache], false, true, true),
            new(CacheOutcome.InvalidInput, [CacheEffect.NormalizeInput, CacheEffect.BuildKey, CacheEffect.Lookup, CacheEffect.RunOriginalInput, CacheEffect.SkipCacheUpdate], false, true, false),
        ];

        RouteDescriptor[] routes = Bindings.Select(binding => new RouteDescriptor(
            binding.Name,
            binding.Address,
            ActivationText(binding.Activation),
            ["CALL", "CALLCODE", "DELEGATECALL", "STATICCALL"],
            ["STATICCALL"],
            RouteMode.DelegatedPrecompileSuppressed,
            "instruction trace off && action trace off && codeSource != RIPEMD160",
            "CreateFullCallFrame -> ExecuteTransaction -> ExecutePrecompile",
            "action trace -> transfer log -> account touch/create -> RIPEMD latch -> price -> Run",
            "returned Result.failure => PrecompileFailure hard exception; managed exception => nested soft revert; pricing OOG => hard failure",
            directSuccessOrder,
            directFailureOrder,
            ["account read/access before resolution", "full frame balance touch", "direct success zero-value touch", "full-frame snapshot/commit/restore; direct route has no snapshot", "refund"],
             ["WorldJournal relation", "native/CLR execution", "full transaction reachability"],
            callKinds,
            callTargets,
            cancellation,
            true,
            fullFrameOutcomes,
            directOutcomes))
            .ToArray();

        return new(
            1,
            ExtractorVersion,
            KernelName,
            "stage-a-admitted-routing-and-classification",
            new(
                "standard (EnableZkEvm != true)",
                "Amsterdam",
                "Typed registry/cache/route/pricing/result/frame-residue classification; leaf execution and WorldJournal effects remain relations/oracles.",
                [
                    "explicit 18-address provider map",
                    "ReleaseSpec fork-gated membership",
                    "CodeInfoRepository and optional cache resolution",
                    "standard full-frame and direct STATICCALL route order",
                    "precompile gas pricing and result classification",
                    "cache hit/miss/invalid-input update semantics",
                    "EvmPooledMemory.TrySave bounds-failure control-flow admission",
                    "CALL-family target and cancellation-boundary semantics",
                    "named handwritten leaf oracle identities",
                ],
                [
                    "cryptographic/native computation",
                    "concrete trie/storage/world journal",
                    "transaction/block reachability",
                    "ZK inline route",
                    "CLR exception/process behavior proof",
                ],
                ["WorldJournal composition", "leaf oracle refinement", "production route reachability"],
                [
                    "EIP-8038 source pin " + Eip8038Commit,
                    "Nethermind source pin " + NethermindCommit,
                    "native package behavior is an oracle",
                    "missing native dependency exits the process",
                ]),
            new(
                "Nethermind.Blockchain.EthereumPrecompileProvider",
                "Precompiles",
                "Nethermind.Core.Precompiles.PrecompiledAddresses",
                "Nethermind.Specs.ReleaseSpec",
                "IsPrecompile",
                "Nethermind.Specs.MainnetSpecProvider",
                "Nethermind.Specs.ForkScheduleSpecProvider",
                "Nethermind.Specs.Forks.Amsterdam",
                0x100,
                providers,
                addresses,
                ancestry,
                [
                    ..ancestry.Select(step => $"{step.Fork} <- {step.Parent}: {string.Join(",", step.EnabledProperties)}"),
                ],
                [
                    "EnableZkEvm != true selects standard sources",
                    "standard source closure excludes zkevm files",
                    "Amsterdam timestamp is ulong.MaxValue - 1",
                ],
                [
                    "inactive address uses ordinary code/delegation lookup",
                    "active address resolves provider CodeInfo and no delegation",
                ]),
            new(
                "Nethermind.Evm.CodeInfoRepository",
                "Nethermind.Blockchain.PrecompileCachedCodeInfoRepository",
                "provider map snapshot at construction; array index then dictionary fallback",
                "(address, NormalizeInput(input), reference-equal releaseSpec)",
                "IPrecompile.NormalizeInput",
                "cache miss invokes Run(originalInput, releaseSpec)",
                "Errors.InvalidInputLength is not cached",
                ["AddAccountRead", "RecordAccountAccess", "delegationAddress = null"],
                ["AddAccountRead", "delegationAddress = null", "cache hit avoids Run"],
                [
                    "precompile membership is tested before provider lookup",
                    "low-number array cap is 0x100",
                    "invalid input is observed only after a cache miss",
                    "unknown cache decorators are rejected",
                ],
                new(["address", "normalizedInput", "releaseSpec"], true, true, true),
                cacheBranches),
            new(
                "EthereumGasPolicy",
                "TryConsumePrecompileGas",
                "full frame uses a local gas copy; direct route debits child gas",
                "base + data overflow preserves gas and skips Run",
                "ordinary affordability failure clears execution gas and restores state gas through caller",
                "pricing must precede Run",
                [PricingOutcome.Success, PricingOutcome.BaseDataOverflow, PricingOutcome.OutOfGas],
                ["BaseGasCost and DataGasCost are ulong", "leaf computation is not generated or proved"]),
            Bindings,
            routes,
            new(
                "WorldJournalRelation",
                "WorldJournalRelation",
                ["frame input", "account/read/access facts", "snapshot token supplied by adapter"],
                ["account/write/read journal", "commit/restore decision", "returndata and refund facts"],
                ["callback is supplied by a separately closed WorldJournal package", "no opaque state token stands in for effects"],
                ["full frame transfer log/touch", "direct success touch", "RIPEMD latch", "access-set rollback mode"],
                true),
            MutationChecks(),
            snapshot.Sources,
            snapshot.Members,
            snapshot.Dependencies,
            trySaveBounds,
            [
                "provider map -> ReleaseSpec.IsPrecompile gate",
                "inactive membership -> ordinary code/delegation fallback",
                "active precompile -> no delegation and account read/access facts",
                "cache key -> normalized input/address/reference-equal spec",
                "CALLCODE target -> env.ExecutingAccount and no dead-recipient creation",
                "delegated precompile -> access charged, body suppressed",
                "direct route -> STATICCALL only and RIPEMD excluded",
                "full route -> trace/log/touch/latch/price/Run order",
                "direct pricing failure -> no Run/no snapshot restore; direct leaf failure -> Run/state-gas restore/no snapshot restore",
                "CALLCODE and DELEGATECALL target env.ExecutingAccount; CALL and STATICCALL target codeSource",
                "cancelable dispatch has separate pre-dispatch and boundary cancellation polls; boundary eligibility alone is not cancellation",
                "full-frame pricing hard, returned leaf hard, managed nested soft-revert, and success residues have distinct ordered effects",
            "returned failure -> hard PrecompileFailure exception",
            "managed exception -> nested soft revert",
            "direct output-copy TrySave bounds failure -> false before UpdateSize/SaveAfterGas and output mutation",
            "WorldJournal callback relation composition gate is explicitly open",
        ]);
    }

    private static SourceManifest BuildManifest(Snapshot snapshot, byte[] irBytes, byte[] leanBytes) => new(
        1,
        ExtractorVersion,
        typeof(CSharpSyntaxTree).Assembly.GetName().Version?.ToString() ?? "unknown",
        "CSharp14",
        KernelName,
        snapshot.Sources,
        snapshot.Members,
        snapshot.Dependencies,
        new(IrFileName, Hash(irBytes)),
        new(LeanFileName, Hash(leanBytes)),
        snapshot.CombinedSourceSha256,
        snapshot.CombinedMemberSha256,
        [
            "registry:explicit-18-entry-provider",
            "activation:replayed-Amsterdam-ancestry-and-ReleaseSpec.BuildPrecompilesCache",
            "resolution:precompile-before-delegation",
            "cache:SupportsCaching-partition-key-hit-miss-invalid-input-update",
            "routing:standard-full-frame-or-direct-staticcall",
            "routing:CALLCODE-executing-account-and-cancelable-selector",
            "classification:pricing-before-run",
            "residue:typed-outcome-specific-return-data-state-gas-snapshot-and-commit-order",
            "memory:TrySave bounds failure precedes UpdateSize/SaveAfterGas/output mutation",
            "oracles:exact-Lean-identity-and-production-symbol-bindings",
            "world:WorldJournal-relation-open-composition-gate",
        ]);

    private static void ValidateArtifacts(
        Snapshot snapshot,
        IrDocument ir,
        SourceManifest manifest,
        byte[] irBytes,
        byte[] leanBytes,
        byte[] manifestBytes)
    {
        if (ir is null || ir.Scope is null || ir.Registry is null || ir.Cache is null || ir.Pricing is null ||
            ir.PrecompileBindings is null || ir.Routes is null || ir.WorldRelation is null || ir.MutationChecks is null ||
            ir.Sources is null || ir.Members is null || ir.Dependencies is null || ir.TrySaveBounds is null ||
            manifest is null || manifest.Sources is null || manifest.Members is null || manifest.Dependencies is null ||
            manifest.Ir is null || manifest.Lean is null || manifest.SemanticBindings is null ||
            ir.SchemaVersion != 1 || ir.Kernel != KernelName || ir.PrecompileBindings.Length != 18 ||
            ir.Routes.Length != 18 || ir.Registry.ProviderEntries is null || ir.Registry.AddressEntries is null ||
            ir.Registry.ActivationAncestry is null || ir.Registry.ProviderEntries.Length != 18 ||
            ir.Registry.AddressEntries.Length != 18 || ir.Registry.ActivationAncestry.Length < 20 ||
            ir.Cache.Key is null || ir.Cache.Branches is null || ir.Cache.Branches.Length != 4 ||
            ir.Routes.Any(route => route.CallKinds is null || route.CallTargets is null || route.Cancellation is null ||
                route.Cancellation.PreDispatchPoll is null || route.Cancellation.BoundaryPoll is null || route.Cancellation.PollOrder is null ||
                route.FullFrameOutcomes is null || route.DirectOutcomes is null || route.FullFrameOutcomes.Length != 4 ||
                route.DirectOutcomes.Length != 5) || !ir.WorldRelation.CompositionGateOpen ||
            ir.MutationChecks.Length != 26 || ir.Dependencies.Length != snapshot.Dependencies.Length ||
            ir.Sources.Length != snapshot.Sources.Length || ir.Members.Length != snapshot.Members.Length)
        {
            throw new ExtractionException("Precompile frame Stage A IR cardinalities or kernel identity changed.");
        }

        IrDocument expectedIr = BuildIr(snapshot);
        if (!SerializeCanonical(expectedIr).AsSpan().SequenceEqual(SerializeCanonical(ir)))
        {
            throw new ExtractionException("Precompile frame Stage A IR does not exactly match the admitted production profile.");
        }

        SourceManifest expectedManifest = BuildManifest(snapshot, irBytes, leanBytes);
        if (!SerializeCanonical(expectedManifest).AsSpan().SequenceEqual(SerializeCanonical(manifest)))
        {
            throw new ExtractionException("Precompile frame Stage A manifest does not exactly match the admitted source graph.");
        }

        byte[] expectedLean = PrecompileFrameLeanEmitter.Emit(expectedIr, Hash(irBytes), snapshot.CombinedSourceSha256);
        if (!expectedLean.AsSpan().SequenceEqual(leanBytes))
        {
            throw new ExtractionException("Precompile frame Stage A Lean bytes do not match the canonical typed IR emission.");
        }

        if (!manifest.Sources.SequenceEqual(snapshot.Sources) ||
            !manifest.Members.SequenceEqual(snapshot.Members) ||
            !manifest.Dependencies.SequenceEqual(snapshot.Dependencies) ||
            manifest.Kernel != KernelName || manifest.SchemaVersion != 1 ||
            manifest.Ir.Path != IrFileName || manifest.Ir.Sha256 != Hash(irBytes) ||
            manifest.Lean.Path != LeanFileName || manifest.Lean.Sha256 != Hash(leanBytes) ||
            manifest.CombinedSourceSha256 != snapshot.CombinedSourceSha256 ||
            manifest.CombinedMemberSha256 != snapshot.CombinedMemberSha256 ||
            !IsSha256(Hash(manifestBytes)))
        {
            throw new ExtractionException("Precompile frame Stage A manifest does not exactly match the closed source graph.");
        }

        if (!SerializeCanonical(ir).AsSpan().SequenceEqual(irBytes) ||
            !SerializeCanonical(manifest).AsSpan().SequenceEqual(manifestBytes))
        {
            throw new ExtractionException("Precompile frame Stage A artifacts are not canonical JSON.");
        }
    }

    private static MutationCheck[] MutationChecks() =>
    [
        new("provider-entry-swap", "EthereumPrecompileProvider.Precompiles", "rejected by explicit address/type binding"),
        new("provider-entry-removal", "EthereumPrecompileProvider.Precompiles", "rejected by 18-entry completeness"),
        new("activation-flag-drop", "ReleaseSpec.BuildPrecompilesCache", "rejected by exact EIP gate fragment"),
        new("address-literal-change", "PrecompiledAddresses", "rejected by address owner literal"),
        new("zk-selector-inversion", "Directory.Build.targets", "rejected by standard selector validation"),
        new("wildcard-provider", "EthereumPrecompileProvider", "rejected by reflection/assembly/wildcard scan"),
        new("unknown-cache-decorator", "PrewarmerModule", "rejected by known decorator count/type"),
        new("low-array-cap-change", "CodeInfoRepository.MaxIndexedNumber", "rejected by 0x100 boundary binding"),
        new("delegation-before-precompile", "CodeInfoRepository.GetCachedCodeInfo", "rejected by no-delegation order"),
        new("cache-key-address-drop", "PrecompileCaches.Key", "rejected by key identity binding"),
        new("cache-key-spec-drop", "PrecompileCaches.Key", "rejected by reference-equal spec binding"),
        new("cache-normalization-drop", "CachedPrecompile.Run", "rejected by NormalizeInput-before-key binding"),
        new("cache-invalid-input-write", "CachedPrecompile.Run", "rejected by InvalidInputLength exclusion"),
        new("direct-ripemd-enable", "VirtualMachine.CanExecutePrecompileCallDirectly", "rejected by RIPEMD guard"),
        new("direct-route-for-call", "EvmInstructions.Call.std", "rejected by STATICCALL-only route"),
        new("price-after-run", "VirtualMachine.RunPrecompile<Eip158>", "rejected by exact AST TryConsumePrecompileGas -> ExecutePrecompileCall order"),
        new("pricing-failure-runs-leaf", "EvmInstructions.Call.std", "rejected by TryRun ordering"),
        new("returned-failure-softened", "VirtualMachine.ExecutePrecompile", "rejected by PrecompileFailure classification"),
        new("managed-exception-hardened", "VirtualMachine.ExecutePrecompileCall", "rejected by managed exception classification"),
        new("touch-before-direct-run", "EvmInstructions.Call.std", "rejected by direct success order"),
        new("returndata-before-direct-run", "EvmInstructions.Call.std", "rejected by direct output order"),
        new("callcode-dead-recipient", "EvmInstructions.Call", "rejected by executing-account target binding"),
        new("delegated-precompile-execution", "EvmInstructions.Call", "rejected by CodeInfo.Empty suppression"),
        new("kzg-initialization-drop", "InitializePrecompiles", "rejected by EIP-4844 initialization binding"),
        new("oracle-binding-swap", "production precompile helper to handwritten Eip803x leaf mapping", "rejected by exact production symbol identity"),
        new("try-save-pre-failure-mutation", "EvmPooledMemory.TrySave", "rejected by exact bounds-failure control-flow admission"),
    ];

    private static MemberIdentity[] CollectMembers(IReadOnlyDictionary<string, ParsedSource> parsed)
    {
        List<MemberSpec> specifications =
        [
            new("src/Nethermind/Nethermind.Blockchain/EthereumPrecompileProvider.cs", "EthereumPrecompileProvider", "Precompiles"),
            new("src/Nethermind/Nethermind.Blockchain/EthereumPrecompileProvider.cs", "EthereumPrecompileProvider", "GetPrecompiles"),
            new("src/Nethermind/Nethermind.Evm/IPrecompileProvider.cs", "IPrecompileProvider", "GetPrecompiles"),
            new("src/Nethermind/Nethermind.Core/Specs/IReleaseSpec.cs", "IReleaseSpec", "IsPrecompile"),
            new("src/Nethermind/Nethermind.Specs/ReleaseSpec.cs", "ReleaseSpec", "IsPrecompile"),
            new("src/Nethermind/Nethermind.Specs/ReleaseSpec.cs", "ReleaseSpec", "BuildPrecompilesCache"),
            new("src/Nethermind/Nethermind.Evm/CodeInfoRepository.cs", "CodeInfoRepository", "BuildPrecompileArray"),
            new("src/Nethermind/Nethermind.Evm/CodeInfoRepository.cs", "CodeInfoRepository", "GetCachedCodeInfo"),
            new("src/Nethermind/Nethermind.Evm/ICodeInfoRepository.cs", "CodeInfoRepositoryExtensions", "GetCachedCodeInfoNoDelegation"),
            new("src/Nethermind/Nethermind.Evm/ExecutionType.cs", "ExecutionTypeExtensions", "GetBalanceCredit"),
            new("src/Nethermind/Nethermind.Blockchain/PrecompileCachedCodeInfoRepository.cs", "PrecompileCachedCodeInfoRepository", "GetCachedCodeInfo"),
            new("src/Nethermind/Nethermind.Blockchain/PrecompileCachedCodeInfoRepository.cs", "PrecompileCachedCodeInfoRepository", "TryGetCachedPrecompile"),
            new("src/Nethermind/Nethermind.Blockchain/PrecompileCachedCodeInfoRepository.cs", "PrecompileCachedCodeInfoRepository", "CreateCachedPrecompile"),
            new("src/Nethermind/Nethermind.Blockchain/PrecompileCachedCodeInfoRepository.cs", "CachedPrecompile", "Run"),
            new("src/Nethermind/Nethermind.Evm/State/PrecompileCaches.cs", "Key", "Equals"),
            new("src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.cs", "EvmInstructions", "CreateFullCallFrame"),
            new("src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.std.cs", "EvmInstructions", "TryInlineStaticPrecompileCall"),
            new("src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs", "EvmPooledMemory", "TrySave",
                "bool TrySave(UInt256 location,ReadOnlySpan<byte> value)"),
            new("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "VirtualMachine", "ExecuteTransaction"),
            new("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "VirtualMachine", "ExecutePrecompile"),
            new("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "VirtualMachine", "RunPrecompile"),
            new("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "VirtualMachine", "ExecutePrecompileCall"),
            new("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "VirtualMachine", "HandleRegularReturn"),
            new("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "VirtualMachine", "HandleRevert"),
            new("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "VirtualMachine", "HandleFailure"),
            new("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "VirtualMachine", "PopAndRestoreParentState"),
            new("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "VirtualMachine", "CanExecutePrecompileCallDirectly"),
            new("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "VirtualMachine", "TryRunPrecompileDirectly"),
            new("src/Nethermind/Nethermind.Evm/VirtualMachine.CallResult.cs", "CallResult", "CallResult"),
            new("src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs", "CallOpcode", "Execute"),
            new("src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs", "VirtualMachine", "RunDispatchLoop"),
            new("src/Nethermind/Nethermind.Evm/GasPolicy/PrecompileGasPricingKernel.cs", "PrecompileGasPricingKernel", "TryConsume"),
            new("src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs", "IGasPolicy", "ClearExecutionGas"),
            new("src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs", "IGasPolicy", "TryConsumePrecompileGas"),
            new("src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs", "IGasPolicy", "Refund"),
            new("src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs", "IGasPolicy", "RestoreChildStateGasOnHalt"),
            new("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "EthereumGasPolicy", "ClearExecutionGas"),
            new("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "EthereumGasPolicy", "TryConsumePrecompileGas"),
            new("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "EthereumGasPolicy", "Refund"),
            new("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "EthereumGasPolicy", "RestoreChildStateGasOnHalt"),
            new("src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionAdapterKernel.cs", "StateGasTransitionAdapterKernel", "Refund"),
            new("src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionAdapterKernel.cs", "StateGasTransitionAdapterKernel", "RestoreChildStateGasOnHalt"),
            new("src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs", "StateGasTransitionKernel", "Refund"),
            new("src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs", "StateGasTransitionKernel", "RestoreChildStateGasOnHalt"),
            new("src/Nethermind/Nethermind.Evm/StackAccessTracker.cs", "StackAccessTracker", "Restore"),
            new("src/Nethermind/Nethermind.Evm/StackAccessTracker.cs", "StackAccessTracker", "WarmUp"),
            new("src/Nethermind/Nethermind.Evm/State/IWorldState.cs", "IWorldState", "RecordAccountAccess"),
            new("src/Nethermind/Nethermind.Evm/State/WorldStateExtensions.cs", "WorldStateExtensions", "AddToBalanceAndCreateIfNotExists"),
            new("src/Nethermind/Nethermind.State/TracedAccessWorldState.cs", "TracedAccessWorldState", "AddAccountRead"),
            new("src/Nethermind/Nethermind.State/TracedAccessWorldState.cs", "TracedAccessWorldState", "Restore"),
        ];

        foreach (PrecompileBinding binding in Bindings)
        {
            specifications.Add(new(binding.BaseSourcePath, binding.TypeName, "Address"));
            specifications.Add(new(binding.BaseSourcePath, binding.TypeName, binding.BaseGasMember));
            specifications.Add(new(binding.BaseSourcePath, binding.TypeName, binding.DataGasMember));
            specifications.Add(new(binding.BaseSourcePath, binding.TypeName, binding.RunMember));
            if (HasMember(FindClass(parsed[binding.BaseSourcePath].Root, binding.TypeName), binding.NormalizeMember))
            {
                specifications.Add(new(binding.BaseSourcePath, binding.TypeName, binding.NormalizeMember));
            }
            else
            {
                specifications.Add(new("src/Nethermind/Nethermind.Evm/Precompiles/IPrecompile.cs", "IPrecompile", "NormalizeInput"));
            }

            foreach (string standardPath in binding.StandardSourcePaths)
            {
                string fullPath = $"src/Nethermind/Nethermind.Evm.Precompiles/{standardPath}";
                specifications.Add(new(fullPath, StandardOwner(standardPath, binding.TypeName),
                    StandardMember(standardPath, binding.RunMember)));
            }
        }

        List<MemberIdentity> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (MemberSpec specification in specifications)
        {
            if (!parsed.TryGetValue(specification.SourcePath, out ParsedSource? source))
            {
                throw new ExtractionException($"Member source {specification.SourcePath} is not in the closed graph.");
            }

            TypeDeclarationSyntax owner = FindType(source.Root, specification.Owner, specification.Member);
            foreach (MemberDeclarationSyntax member in owner.Members.Where(member =>
                (member switch
                {
                    BaseTypeDeclarationSyntax nested => nested.Identifier.ValueText == specification.Member,
                    MethodDeclarationSyntax method => method.Identifier.ValueText == specification.Member,
                    PropertyDeclarationSyntax property => property.Identifier.ValueText == specification.Member,
                    FieldDeclarationSyntax field => field.Declaration.Variables.Any(variable => variable.Identifier.ValueText == specification.Member),
                    ConstructorDeclarationSyntax constructor => specification.Member == owner.Identifier.ValueText,
                    _ => false,
                }) && (specification.RequiredSignature is null || Signature(member) == specification.RequiredSignature)))
            {
                string key = $"{specification.SourcePath}|{specification.Owner}|{specification.Member}|{Canonical(member)}";
                if (!seen.Add(key)) continue;
                result.Add(new(
                    specification.SourcePath,
                    specification.Owner,
                    specification.Member,
                    Signature(member),
                    member.Kind().ToString(),
                    Hash(CanonicalTokens(member))));
            }

            if (!result.Any(item => item.SourcePath == specification.SourcePath && item.Owner == specification.Owner &&
                item.Member == specification.Member &&
                (specification.RequiredSignature is null || item.Signature == specification.RequiredSignature)))
            {
                throw new ExtractionException($"Required member {specification.Owner}.{specification.Member} is missing from {specification.SourcePath}.");
            }
        }

        return result.OrderBy(static member => member.SourcePath, StringComparer.Ordinal)
            .ThenBy(static member => member.Owner, StringComparer.Ordinal)
            .ThenBy(static member => member.Member, StringComparer.Ordinal)
            .ThenBy(static member => member.Signature, StringComparer.Ordinal)
            .ToArray();
    }

    private static string Text(Snapshot snapshot, string path)
    {
        SourceIdentity source = snapshot.Sources.Single(item => item.Path == path);
        return Encoding.UTF8.GetString(File.ReadAllBytes(ResolveExactPath(snapshot.Root, source.Path)));
    }

    private static CompilationUnitSyntax ParseCSharp(byte[] bytes, string path, string role)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            SourceText.From(bytes, bytes.Length, Encoding.UTF8, canBeEmbedded: true),
            ParseOptions,
            path);
        Diagnostic[] errors = tree.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length != 0)
        {
            throw new ExtractionException($"{role} {path} did not parse without diagnostics: " +
                string.Join(Environment.NewLine, errors.Select(static diagnostic => diagnostic.ToString())));
        }

        return tree.GetCompilationUnitRoot();
    }

    private static ClassDeclarationSyntax FindClass(CompilationUnitSyntax root, string name) =>
        root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .SingleOrDefault(declaration => declaration.Identifier.ValueText == name)
        ?? throw new ExtractionException($"Required precompile class {name} is missing.");

    private static TypeDeclarationSyntax FindType(CompilationUnitSyntax root, string name, string? member = null)
    {
        TypeDeclarationSyntax[] matches = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(declaration => declaration.Identifier.ValueText == name).ToArray();
        if (matches.Length == 1) return matches[0];

        if (member is not null)
        {
            TypeDeclarationSyntax[] memberMatches = matches.Where(declaration => HasMember(declaration, member) ||
                declaration.Identifier.ValueText == member && declaration.Members.OfType<ConstructorDeclarationSyntax>().Any()).ToArray();
            if (memberMatches.Length == 1) return memberMatches[0];
        }

        TypeDeclarationSyntax? generic = matches.SingleOrDefault(static declaration =>
            declaration is ClassDeclarationSyntax { TypeParameterList.Parameters.Count: > 0 });
        return generic ?? throw new ExtractionException(
            matches.Length == 0
                ? $"Required owner type {name} is missing."
                : $"Required owner type {name} is ambiguous.");
    }

    private static bool HasMember(TypeDeclarationSyntax declaration, string name) => declaration.Members.Any(member =>
        member switch
        {
            MethodDeclarationSyntax method => method.Identifier.ValueText == name,
            PropertyDeclarationSyntax property => property.Identifier.ValueText == name,
            FieldDeclarationSyntax field => field.Declaration.Variables.Any(variable => variable.Identifier.ValueText == name),
            _ => false,
        });

    private static void RequireMember(TypeDeclarationSyntax declaration, string name, string subject)
    {
        if (!HasMember(declaration, name))
        {
            throw new ExtractionException($"Precompile {subject} is missing required member {name}.");
        }
    }

    private static string Signature(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax method =>
            $"{Canonical(method.ReturnType)} {method.Identifier.ValueText}({string.Join(",", method.ParameterList.Parameters.Select(parameter =>
                $"{Canonical(parameter.Type!)} {parameter.Identifier.ValueText}"))})",
        PropertyDeclarationSyntax property => $"{Canonical(property.Type)} {property.Identifier.ValueText}",
        FieldDeclarationSyntax field => $"{Canonical(field.Declaration.Type)} {string.Join(",", field.Declaration.Variables.Select(variable => variable.Identifier.ValueText))}",
        ConstructorDeclarationSyntax constructor => $"{constructor.Identifier.ValueText}({string.Join(",", constructor.ParameterList.Parameters.Select(parameter =>
                $"{Canonical(parameter.Type!)} {parameter.Identifier.ValueText}"))})",
        _ => member.Kind().ToString(),
    };

    private static void RequireOrdered(string source, IReadOnlyList<string> fragments, string subject)
    {
        int previous = -1;
        foreach (string fragment in fragments)
        {
            int current = source.IndexOf(fragment, previous + 1, StringComparison.Ordinal);
            if (current < 0)
            {
                throw new ExtractionException($"{subject} is missing ordered fragment {fragment}.");
            }

            previous = current;
        }
    }

    private static MethodDeclarationSyntax FindMethod(
        Snapshot snapshot,
        string sourcePath,
        string owner,
        string member,
        Func<MethodDeclarationSyntax, bool>? predicate = null)
    {
        TypeDeclarationSyntax declaration = FindType(snapshot.Parsed[sourcePath].Root, owner, member);
        MethodDeclarationSyntax[] matches = declaration.Members.OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == member && (predicate is null || predicate(method)))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new ExtractionException($"Required method {owner}.{member} is missing or ambiguous in {sourcePath}.");
        }

        return matches[0];
    }

    private static void RequireAstOrdered(
        SyntaxNode root,
        IReadOnlyList<Func<SyntaxNode, bool>> predicates,
        string subject)
    {
        SyntaxNode[] nodes = root.DescendantNodes().ToArray();
        int previous = -1;
        for (int predicateIndex = 0; predicateIndex < predicates.Count; predicateIndex++)
        {
            Func<SyntaxNode, bool> predicate = predicates[predicateIndex];
            int current = -1;
            for (int index = previous + 1; index < nodes.Length; index++)
            {
                if (predicate(nodes[index]))
                {
                    current = index;
                    break;
                }
            }

            if (current < 0)
            {
                throw new ExtractionException($"{subject} is missing ordered AST operation {predicateIndex}.");
            }

            previous = current;
        }
    }

    private static bool IsInvocation(SyntaxNode node, string expression) =>
        node is InvocationExpressionSyntax invocation && Canonical(invocation.Expression) == expression;

    private static bool IsAssignment(SyntaxNode node, string left, string right) =>
        node is AssignmentExpressionSyntax assignment &&
        assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
        Canonical(assignment.Left) == left && Canonical(assignment.Right) == right;

    private static ExpressionSyntax UnwrapParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    private static bool IsIdentifier(ExpressionSyntax expression, string name) =>
        UnwrapParentheses(expression) is IdentifierNameSyntax identifier && identifier.Identifier.ValueText == name;

    private static bool IsMemberAccess(ExpressionSyntax expression, string receiver, string member)
    {
        expression = UnwrapParentheses(expression);
        return expression is MemberAccessExpressionSyntax access &&
            access.Name.Identifier.ValueText == member && IsIdentifier(access.Expression, receiver);
    }

    private static bool IsLiteralZero(ExpressionSyntax expression) =>
        UnwrapParentheses(expression) is LiteralExpressionSyntax literal &&
        literal.IsKind(SyntaxKind.NumericLiteralExpression) && literal.Token.ValueText == "0";

    private static bool IsLiteralTrue(ExpressionSyntax expression) =>
        UnwrapParentheses(expression) is LiteralExpressionSyntax literal &&
        literal.IsKind(SyntaxKind.TrueLiteralExpression);

    private static bool IsExactCancellationPoll(IfStatementSyntax statement) =>
        statement.Else is null &&
        IsMemberAccess(statement.Condition, "_txTracer", "IsCancelled") &&
        IsCancellationThrowStatement(statement.Statement);

    private static bool IsCancellationThrowStatement(StatementSyntax statement)
    {
        if (statement is BlockSyntax block)
        {
            if (block.Statements.Count != 1)
            {
                return false;
            }

            statement = block.Statements[0];
        }

        return statement is ExpressionStatementSyntax
        {
            Expression: InvocationExpressionSyntax invocation,
        } && Canonical(invocation.Expression) == "ThrowOperationCanceledException" &&
            invocation.ArgumentList.Arguments.Count == 0;
    }

    private static bool IsSingleBreak(StatementSyntax statement)
    {
        if (statement is BlockSyntax block)
        {
            return block.Statements.Count == 1 && block.Statements[0] is BreakStatementSyntax;
        }

        return statement is BreakStatementSyntax;
    }

    private static bool IsImmediatelyFollowedBy(SyntaxNode first, SyntaxNode second) =>
        first.Parent is BlockSyntax block && second.Parent == block &&
        block.Statements.IndexOf((StatementSyntax)first) + 1 == block.Statements.IndexOf((StatementSyntax)second);

    private static bool IsExactCancellationBoundaryCondition(ExpressionSyntax condition)
    {
        ExpressionSyntax outer = UnwrapParentheses(condition);
        if (outer is not BinaryExpressionSyntax outerOr || !outerOr.IsKind(SyntaxKind.LogicalOrExpression))
        {
            return false;
        }

        ExpressionSyntax middle = UnwrapParentheses(outerOr.Left);
        return middle is BinaryExpressionSyntax middleOr &&
            middleOr.IsKind(SyntaxKind.LogicalOrExpression) &&
            IsCancellationExceptionClause(middleOr.Left) &&
            IsCancellationOpcodeClause(middleOr.Right) &&
            IsCancellationProgramCounterClause(outerOr.Right);
    }

    private static bool IsCancellationExceptionClause(ExpressionSyntax expression)
    {
        expression = UnwrapParentheses(expression);
        return expression is BinaryExpressionSyntax comparison &&
            comparison.IsKind(SyntaxKind.NotEqualsExpression) &&
            IsIdentifier(comparison.Left, "exceptionType") &&
            IsMemberAccess(comparison.Right, "EvmExceptionType", "None");
    }

    private static bool IsCancellationOpcodeClause(ExpressionSyntax expression)
    {
        expression = UnwrapParentheses(expression);
        if (expression is not BinaryExpressionSyntax comparison ||
            !comparison.IsKind(SyntaxKind.NotEqualsExpression) ||
            !IsLiteralZero(comparison.Right))
        {
            return false;
        }

        ExpressionSyntax bitwise = UnwrapParentheses(comparison.Left);
        return bitwise is BinaryExpressionSyntax bitwiseAnd &&
            bitwiseAnd.IsKind(SyntaxKind.BitwiseAndExpression) &&
            IsMemberAccess(bitwiseAnd.Left, "cancelableState", "OpCodeCount") &&
            IsIdentifier(bitwiseAnd.Right, "CancellationCheckMask");
    }

    private static bool IsCancellationProgramCounterClause(ExpressionSyntax expression)
    {
        expression = UnwrapParentheses(expression);
        if (expression is not BinaryExpressionSyntax comparison ||
            !comparison.IsKind(SyntaxKind.GreaterThanOrEqualExpression))
        {
            return false;
        }

        return IsNuintMember(comparison.Left, "cancelableState", "FinalProgramCounter") &&
            IsNuintMember(comparison.Right, "stack", "CodeLength");
    }

    private static bool IsNuintMember(ExpressionSyntax expression, string receiver, string member)
    {
        expression = UnwrapParentheses(expression);
        return expression is CastExpressionSyntax cast &&
            Canonical(cast.Type) == "nuint" && IsMemberAccess(cast.Expression, receiver, member);
    }

    private static bool IsOutputCopyGuard(IfStatementSyntax statement)
    {
        ExpressionSyntax condition = UnwrapParentheses(statement.Condition);
        return condition is BinaryExpressionSyntax comparison &&
            comparison.IsKind(SyntaxKind.GreaterThanExpression) &&
            IsIdentifier(comparison.Left, "copyLength") && IsLiteralZero(comparison.Right);
    }

    private static bool IsTrySaveFailure(IfStatementSyntax statement)
    {
        if (statement.Else is not null)
        {
            return false;
        }

        ExpressionSyntax condition = UnwrapParentheses(statement.Condition);
        if (condition is not PrefixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.LogicalNotExpression,
                Operand: InvocationExpressionSyntax invocation,
            } || Canonical(invocation.Expression) != "vm.VmState.Memory.TrySave" ||
            invocation.ArgumentList.Arguments.Count != 2)
        {
            return false;
        }

        ArgumentSyntax offset = invocation.ArgumentList.Arguments[0];
        ArgumentSyntax output = invocation.ArgumentList.Arguments[1];
        return offset.RefKindKeyword.IsKind(SyntaxKind.InKeyword) &&
            IsIdentifier(offset.Expression, "outputOffset") &&
            output.RefKindKeyword.IsKind(SyntaxKind.None) && IsOutputSlice(output.Expression);
    }

    private static bool IsOutputSlice(ExpressionSyntax? expression)
    {
        if (expression is not ElementAccessExpressionSyntax element ||
            Canonical(element.Expression) != "outputData.Span" ||
            element.ArgumentList.Arguments.Count != 1)
        {
            return false;
        }

        ArgumentSyntax argument = element.ArgumentList.Arguments[0];
        return argument.Expression is RangeExpressionSyntax range && range.LeftOperand is null &&
            range.RightOperand is not null && IsIdentifier(range.RightOperand, "copyLength");
    }

    private static bool IsDirectOutOfGasFailureBody(StatementSyntax statement)
    {
        StatementSyntax[] statements = statement is BlockSyntax block
            ? block.Statements.ToArray()
            : [statement];
        return statements.Length == 2 &&
            statements[0] is ExpressionStatementSyntax
            {
                Expression: AssignmentExpressionSyntax assignment,
            } && IsAssignment(assignment, "result", "EvmExceptionType.OutOfGas") &&
            statements[1] is ReturnStatementSyntax
            {
                Expression: LiteralExpressionSyntax expression,
            } && IsLiteralTrue(expression);
    }

    private static int Count(string source, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static string Canonical(string value) => string.Concat(value.Where(static character => !char.IsWhiteSpace(character)));

    private static string Canonical(SyntaxNode node) => Canonical(node.ToString());

    private static byte[] CanonicalTokens(SyntaxNode node)
    {
        StringBuilder value = new();
        foreach (SyntaxToken token in node.DescendantTokens(descendIntoTrivia: false))
        {
            value.Append('T').Append(token.RawKind).Append(':').Append(token.Text).Append('\0');
        }

        return Utf8WithoutBom.GetBytes(value.ToString());
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsSha256(string value) => value.Length == 64 &&
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string AddressLiteral(int address) => address == 0x100
        ? "0x0100"
        : $"0x{address:x2}";

    private static string DescribeDirectSuccessOrder(DirectFrameOutcomeDescriptor[] outcomes) =>
        DescribeDirectEffects(outcomes.Single(outcome => outcome.Outcome == DirectFrameOutcome.Success));

    private static string DescribeDirectFailureOrder(DirectFrameOutcomeDescriptor[] outcomes) =>
        $"pricing hard: {DescribeDirectEffects(outcomes.Single(outcome => outcome.Outcome == DirectFrameOutcome.PricingHardFailure))}; " +
        $"leaf failure: {DescribeDirectEffects(outcomes.Single(outcome => outcome.Outcome == DirectFrameOutcome.ReturnedLeafHardFailure))}; " +
        $"output-copy OOG: {DescribeDirectEffects(outcomes.Single(outcome => outcome.Outcome == DirectFrameOutcome.OutputCopyOutOfGas))}";

    private static string DescribeDirectEffects(DirectFrameOutcomeDescriptor outcome) =>
        string.Join(" -> ", outcome.Effects.Select(static effect => effect switch
        {
            FrameEffect.Pricing => "price",
            FrameEffect.Run => "Run",
            FrameEffect.AccountTouchOrCreate => "account touch",
            FrameEffect.ChildRefund => "refund child gas",
            FrameEffect.ExecutionGasClear => "execution-gas clear",
            FrameEffect.StateGasRestore => "parent state-gas restore",
            FrameEffect.ReturnDataClear => "returndata clear",
            FrameEffect.ReturnData => "returndata",
            FrameEffect.ReturnOutOfGas => "return OutOfGas",
            FrameEffect.OutputCopy => "output copy",
            FrameEffect.StackSuccess => "stack success",
            FrameEffect.StackFailure => "stack failure",
            _ => throw new ExtractionException($"Unsupported direct-route effect {effect}."),
        }));

    private static string ActivationText(ActivationRule activation) => activation switch
    {
        ActivationRule.Always => "always",
        ActivationRule.Eip198 => "EIP-198",
        ActivationRule.Eip196And197 => "EIP-196 && EIP-197",
        ActivationRule.Eip152 => "EIP-152",
        ActivationRule.Eip4844 => "EIP-4844",
        ActivationRule.Eip2537 => "EIP-2537",
        ActivationRule.Eip7212Or7951 => "EIP-7212 || EIP-7951",
        _ => throw new ArgumentOutOfRangeException(nameof(activation)),
    };

    private static string OracleFileName(PrecompileBinding binding) => binding.Name switch
    {
        "ECRECOVER" => "Ecrecover.lean",
        "SHA256" => "Sha256.lean",
        "RIPEMD160" => "Ripemd160.lean",
        "IDENTITY" => "Identity.lean",
        "MODEXP" => "ModExp.lean",
        "BN254_ADD" => "Bn254Add.lean",
        "BN254_MUL" => "Bn254Mul.lean",
        "BN254_PAIRING" => "Bn254Pairing.lean",
        "BLAKE2F" => "Blake2F.lean",
        "KZG_POINT_EVALUATION" => "KzgPointEvaluation.lean",
        "BLS12_G1ADD" => "Bls12381G1Add.lean",
        "BLS12_G1MSM" => "Bls12381G1Msm.lean",
        "BLS12_G2ADD" => "Bls12381G2Add.lean",
        "BLS12_G2MSM" => "Bls12381G2Msm.lean",
        "BLS12_PAIRING" => "Bls12381Pairing.lean",
        "BLS12_MAP_FP_TO_G1" => "Bls12381FpToG1.lean",
        "BLS12_MAP_FP2_TO_G2" => "Bls12381Fp2ToG2.lean",
        "P256VERIFY" => "P256Verify.lean",
        _ => throw new ExtractionException($"No handwritten oracle file is assigned to {binding.Name}."),
    };

    private static string CombinedSourceHash(IEnumerable<SourceIdentity> sources, IEnumerable<DependencyIdentity> dependencies)
    {
        StringBuilder value = new();
        foreach (SourceIdentity source in sources.OrderBy(static source => source.Path, StringComparer.Ordinal))
        {
            value.Append("source|").Append(source.Path).Append('|').Append(source.Role).Append('|')
                .Append(source.Sha256).Append('|').Append(source.RoslynSyntaxSha256).Append('|')
                .Append(source.IsRaw).Append('|').Append(source.IncludedInStandardBuild).Append('\n');
        }

        foreach (DependencyIdentity dependency in dependencies.OrderBy(static dependency => dependency.Kind, StringComparer.Ordinal)
                     .ThenBy(static dependency => dependency.Name, StringComparer.Ordinal))
        {
            value.Append("dependency|").Append(dependency.Kind).Append('|').Append(dependency.Name).Append('|')
                .Append(dependency.Path).Append('|').Append(dependency.Sha256).Append('|')
                .Append(dependency.Binding).Append('|').Append(dependency.OracleOnly).Append('\n');
        }

        return Hash(Utf8WithoutBom.GetBytes(value.ToString()));
    }

    private static string CombinedMemberHash(IEnumerable<MemberIdentity> members)
    {
        StringBuilder value = new();
        foreach (MemberIdentity member in members.OrderBy(static member => member.SourcePath, StringComparer.Ordinal)
                     .ThenBy(static member => member.Owner, StringComparer.Ordinal)
                     .ThenBy(static member => member.Member, StringComparer.Ordinal)
                     .ThenBy(static member => member.Signature, StringComparer.Ordinal))
        {
            value.Append(member.SourcePath).Append('|').Append(member.Owner).Append('|').Append(member.Member).Append('|')
                .Append(member.Signature).Append('|').Append(member.SyntaxKind).Append('|').Append(member.Sha256).Append('\n');
        }

        return Hash(Utf8WithoutBom.GetBytes(value.ToString()));
    }

    private static T Deserialize<T>(byte[] bytes, string subject)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
                ?? throw new ExtractionException($"Serialized {subject} is empty.");
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Serialized {subject} is invalid: {exception.Message}");
        }
    }

    private static byte[] SerializeCanonical<T>(T value) => Utf8WithoutBom.GetBytes(
        JsonSerializer.Serialize(value, JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");

    private static void WriteDeterministic(string path, byte[] bytes)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return;
        File.WriteAllBytes(path, bytes);
    }

    private static string ResolveExactPath(string root, string relativePath)
    {
        string current = root;
        foreach (string segment in relativePath.Split('/'))
        {
            string[] matches = Directory.EnumerateFileSystemEntries(current)
                .Where(path => Path.GetFileName(path).Equals(segment, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
            {
                throw new ExtractionException($"Required case-sensitive path '{relativePath}' is missing or ambiguous at '{segment}'.");
            }

            current = matches[0];
        }

        string resolved = Path.GetFullPath(current);
        string rooted = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(rooted, StringComparison.OrdinalIgnoreCase))
        {
            throw new ExtractionException($"Path escapes repository root: {relativePath}.");
        }

        return resolved;
    }

    private static SourceSpec Source(string path, string role, bool included) => new(path, role, included);

    private static string StandardOwner(string standardPath, string typeName) =>
        standardPath == "ModExpPrecompilePreEip2565.std.cs" ? "ModExpPrecompilePreEip2565" : typeName;

    private static string StandardMember(string standardPath, string runMember) => standardPath switch
    {
        "std/BN254AddPrecompile.cs" => "Add",
        "std/BN254MulPrecompile.cs" => "Mul",
        _ => runMember,
    };

    private static PrecompileBinding Binding(
        string name,
        int address,
        string typeName,
        string addressField,
        ActivationRule activation,
        string oracleModule,
        string oracleSymbol,
        string[] standardSources,
        string[] inputRules,
        string[] outputRules,
        string[] failureRules) => new(
        name,
        address,
        typeName,
        $"src/Nethermind/Nethermind.Evm.Precompiles/{typeName}.cs",
        standardSources,
        activation,
        addressField,
        $"{typeName}.Address",
        "BaseGasCost",
        "DataGasCost",
        "Run",
        "NormalizeInput",
        oracleModule,
        $"Eip803x.Precompiles.{oracleModule}",
        "run",
        oracleSymbol,
        inputRules,
        outputRules,
        failureRules);

    private sealed record SourceSpec(string Path, string Role, bool IncludedInStandardBuild);

    private sealed record NativePackageSpec(string Name, string Role);

    private sealed record MemberSpec(string SourcePath, string Owner, string Member, string? RequiredSignature = null);

    private sealed record ParsedSource(string RelativePath, string FullPath, byte[] Bytes, CompilationUnitSyntax Root);

    private sealed record Snapshot(
        string Root,
        SourceIdentity[] Sources,
        MemberIdentity[] Members,
        DependencyIdentity[] Dependencies,
        IReadOnlyDictionary<string, ParsedSource> Parsed,
        string CombinedSourceSha256,
        string CombinedMemberSha256);
}
