// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.EvmFrameMachineExtractor;

internal static partial class EvmFrameMachineProfile
{
    private const string SourceClosureResourceSuffix = ".Admission.ProductionClosure.txt";
    private const string DependencyClosureResourceSuffix = ".Admission.DependencyClosure.txt";
    private const string OpcodeDispatchSourcePath = "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs";
    private const string OpcodeHandlerSourcePath = "src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs";
    private const string InstructionSourcePath = "src/Nethermind/Nethermind.Evm/Instruction.cs";
    private static readonly CSharpParseOptions StageAParseOptions =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp14);

    private static readonly (string Name, string LeanModule, string ProofModulePath, string[] RequiredTheorems, bool Admitted)[] OpcodePackageBindings =
    [
        ("PureWordOpcodeExtractor", "Eip803x.Refinement.PureWordOpcode", "tools/Evm/Lean/Eip803x/Refinement/PureWordOpcode.lean", ["Eip803x.Refinement.PureWordOpcode.extracted_execute_refines"], true),
        ("Keccak256OpcodeExtractor", "Keccak256OpcodeExtractor.Refinement.Keccak256Opcode", "tools/Evm/Lean/Keccak256OpcodeExtractor/Refinement/Keccak256Opcode.lean", ["Eip803x.Generated.Keccak256OpcodeRefinement.execute_refines"], true),
        ("EnvironmentOpcodeExtractor", "Eip803x.Refinement.EnvironmentOpcode", "tools/Evm/Lean/Eip803x/Refinement/EnvironmentOpcode.lean", ["Eip803x.Refinement.EnvironmentOpcode.closed_amsterdam_refines"], true),
        ("AccountReadOpcodeExtractor", "Eip803x.Refinement.AccountReadOpcode", "tools/Evm/Lean/Eip803x/Refinement/AccountReadOpcode.lean", ["Eip803x.Refinement.AccountReadOpcode.generated_execution_refines_reference"], true),
        ("MemoryCopyOpcodeExtractor", "MemoryCopyOpcodeExtractor.Refinement.MemoryCopyOpcode", "tools/Evm/Lean/MemoryCopyOpcodeExtractor/Refinement/MemoryCopyOpcode.lean", ["Eip803x.Generated.MemoryCopyOpcodeRefinement.extracted_amsterdam_closed_refines"], true),
        ("PersistentStorageOpcodeExtractor", "PersistentStorageOpcodeExtractor.Refinement.PersistentStorageOpcode", "tools/Evm/Lean/PersistentStorageOpcodeExtractor/Refinement/PersistentStorageOpcode.lean", ["PersistentStorageOpcodeExtractor.Refinement.PersistentStorageOpcode.sload_byte_refines", "PersistentStorageOpcodeExtractor.Refinement.PersistentStorageOpcode.sstore_byte_refines"], true),
        ("TransientStorageOpcodeExtractor", "TransientStorageOpcodeExtractor.Refinement.TransientStorageOpcode", "tools/Evm/Lean/TransientStorageOpcodeExtractor/Refinement/TransientStorageOpcode.lean", ["TransientStorageOpcodeExtractor.Refinement.TransientStorageOpcode.tload_byte_refines", "TransientStorageOpcodeExtractor.Refinement.TransientStorageOpcode.tstore_byte_refines"], true),
        ("StackRearrangementOpcodeExtractor", "Eip803x.Refinement.StackRearrangementOpcode", "tools/Evm/Lean/Eip803x/Refinement/StackRearrangementOpcode.lean", ["Eip803x.Evm.Refinement.StackRearrangementOpcode.generated_execute_refines_reference"], true),
        ("PushOpcodeExtractor", "PushOpcodeExtractor.Refinement.PushOpcode", "tools/Evm/Lean/PushOpcodeExtractor/Refinement/PushOpcode.lean", ["PushOpcodeExtractor.Refinement.PushOpcode.generated_execute_agrees"], true),
        ("LogOpcodeExtractor", "LogOpcodeExtractor.Refinement.LogOpcode", "tools/Evm/Lean/LogOpcodeExtractor/Refinement/LogOpcode.lean", ["LogOpcodeExtractor.Refinement.generated_execute_refines_reference"], true),
        ("CallCreateOpcodeExtractor", "CallCreateOpcodeExtractor.Refinement.CallCreateOpcode", "tools/Evm/Lean/CallCreateOpcodeExtractor/Refinement/CallCreateOpcode.lean", ["Eip803x.Generated.CallCreateOpcodeRefinement.closed_amsterdam_refines"], true),
        ("ControlFlowOpcodeExtractor", "ControlFlowOpcodeExtractor.Refinement.ControlFlowOpcode", "tools/Evm/Lean/ControlFlowOpcodeExtractor/Refinement/ControlFlowOpcode.lean", ["Eip803x.Generated.ControlFlowOpcodeRefinement.extracted_amsterdam_refines"], true),
        ("ExtendedStackOpcodeExtractor", "ExtendedStackOpcodeExtractor.Refinement.ExtendedStackOpcode", "tools/Evm/Lean/ExtendedStackOpcodeExtractor/Refinement/ExtendedStackOpcode.lean", ["ExtendedStackOpcodeExtractor.Refinement.execute_refines_handwritten_extended_stack"], true),
        ("CallDataLoadOpcodeExtractor", "CallDataLoadOpcodeExtractor.Refinement.CallDataLoadOpcode", "tools/Evm/Lean/CallDataLoadOpcodeExtractor/Refinement/CallDataLoadOpcode.lean", ["Eip803x.Generated.CallDataLoadOpcodeRefinement.closed_amsterdam_refines"], true),
    ];

    private static readonly (string Name, string FileName, string LeanModule)[] PrecompileBindings =
    [
        ("ECRECOVER", "Ecrecover.lean", "Eip803x.Precompiles.Ecrecover"),
        ("SHA256", "Sha256.lean", "Eip803x.Precompiles.Sha256"),
        ("RIPEMD160", "Ripemd160.lean", "Eip803x.Precompiles.Ripemd160"),
        ("IDENTITY", "Identity.lean", "Eip803x.Precompiles.Identity"),
        ("MODEXP", "ModExp.lean", "Eip803x.Precompiles.ModExp"),
        ("BN254_ADD", "Bn254Add.lean", "Eip803x.Precompiles.Bn254Add"),
        ("BN254_MUL", "Bn254Mul.lean", "Eip803x.Precompiles.Bn254Mul"),
        ("BN254_PAIRING", "Bn254Pairing.lean", "Eip803x.Precompiles.Bn254Pairing"),
        ("BLAKE2F", "Blake2F.lean", "Eip803x.Precompiles.Blake2F"),
        ("KZG_POINT_EVALUATION", "KzgPointEvaluation.lean", "Eip803x.Precompiles.KzgPointEvaluation"),
        ("BLS12_G1ADD", "Bls12381G1Add.lean", "Eip803x.Precompiles.Bls12381G1Add"),
        ("BLS12_G1MSM", "Bls12381G1Msm.lean", "Eip803x.Precompiles.Bls12381G1Msm"),
        ("BLS12_G2ADD", "Bls12381G2Add.lean", "Eip803x.Precompiles.Bls12381G2Add"),
        ("BLS12_G2MSM", "Bls12381G2Msm.lean", "Eip803x.Precompiles.Bls12381G2Msm"),
        ("BLS12_PAIRING", "Bls12381Pairing.lean", "Eip803x.Precompiles.Bls12381Pairing"),
        ("BLS12_MAP_FP_TO_G1", "Bls12381FpToG1.lean", "Eip803x.Precompiles.Bls12381FpToG1"),
        ("BLS12_MAP_FP2_TO_G2", "Bls12381Fp2ToG2.lean", "Eip803x.Precompiles.Bls12381Fp2ToG2"),
        ("P256VERIFY", "P256Verify.lean", "Eip803x.Precompiles.P256Verify"),
    ];

    private static readonly IReadOnlyDictionary<int, string> PreferredOpcodeOwners =
        new Dictionary<int, string> { [0x4b] = "ControlFlowOpcodeExtractor" };

    internal static IReadOnlyList<string> InputPaths =>
    [
        .. DesignIr().Sources.Select(static source => source.Path),
        .. LoadDependencyPins().SelectMany(static dependency => dependency.InputPaths),
    ];

    internal static ExtractionResult ExtractStageA(string repoRoot, string outputDirectory, string? leanOutputPath)
    {
        string root = Path.GetFullPath(repoRoot);
        StageABuild build = BuildStageA(root);
        byte[] irBytes = Serialize(build.Ir);
        IrDocument roundTrip = DeserializeIr(irBytes, root);
        string irSha256 = Hash(irBytes);
        string combinedSourceSha256 = CombinedSourceHash(build.SourceIdentities, build.AdmissionIdentities, build.Dependencies);
        byte[] leanBytes = EvmFrameMachineLeanEmitter.Emit(roundTrip, irSha256, combinedSourceSha256);
        SourceManifest manifest = BuildManifest(build, irBytes, leanBytes, combinedSourceSha256);
        byte[] manifestBytes = SerializeCanonical(manifest);
        ValidateManifest(DeserializeManifest(manifestBytes, root), manifest, irBytes, leanBytes);

        Directory.CreateDirectory(outputDirectory);
        string irPath = Path.Combine(outputDirectory, IrFileName);
        string manifestPath = Path.Combine(outputDirectory, ManifestFileName);
        string leanPath = leanOutputPath ?? Path.Combine(outputDirectory, LeanFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(leanPath)!);
        WriteDeterministic(irPath, irBytes);
        WriteDeterministic(manifestPath, manifestBytes);
        WriteDeterministic(leanPath, leanBytes);
        return new(irPath, manifestPath, leanPath, roundTrip.OpcodeRoutes.Length,
            roundTrip.Sources.Length, roundTrip.Admissions.Length);
    }

    internal static IrDocument DeserializeIr(byte[] bytes, string repoRoot)
    {
        IrDocument expected = BuildStageA(Path.GetFullPath(repoRoot)).Ir;
        return DeserializeIrAgainstExpected(bytes, expected);
    }

    internal static IrDocument DeserializeIrAgainstExpected(byte[] bytes, IrDocument expected)
    {
        RejectDuplicateProperties(bytes);
        try
        {
            IrDocument document = JsonSerializer.Deserialize<IrDocument>(bytes, JsonOptions) ??
                throw new ExtractionException("Serialized frame-machine Stage A IR is empty.");
            ValidateStageAReady(document);
            if (!Serialize(document).AsSpan().SequenceEqual(Serialize(expected)) ||
                !Serialize(document).AsSpan().SequenceEqual(bytes))
                throw new ExtractionException("Serialized frame-machine Stage A IR is not the exact canonical admitted profile.");
            return document;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Serialized frame-machine Stage A IR is invalid: {exception.Message}");
        }
    }

    internal static SourceManifest DeserializeManifest(byte[] bytes, string repoRoot)
    {
        RejectDuplicateProperties(bytes);
        try
        {
            SourceManifest manifest = JsonSerializer.Deserialize<SourceManifest>(bytes, JsonOptions) ??
                throw new ExtractionException("Serialized frame-machine Stage A manifest is empty.");
            StageABuild build = BuildStageA(Path.GetFullPath(repoRoot));
            byte[] irBytes = Serialize(build.Ir);
            string combined = CombinedSourceHash(build.SourceIdentities, build.AdmissionIdentities, build.Dependencies);
            byte[] leanBytes = EvmFrameMachineLeanEmitter.Emit(build.Ir, Hash(irBytes), combined);
            SourceManifest expected = BuildManifest(build, irBytes, leanBytes, combined);
            return ValidateManifestAgainstExpected(bytes, manifest, expected, irBytes, leanBytes);
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Serialized frame-machine Stage A manifest is invalid: {exception.Message}");
        }
    }

    internal static SourceManifest DeserializeManifestAgainstExpected(byte[] bytes, SourceManifest expected,
        byte[] irBytes, byte[] leanBytes)
    {
        RejectDuplicateProperties(bytes);
        try
        {
            SourceManifest manifest = JsonSerializer.Deserialize<SourceManifest>(bytes, JsonOptions) ??
                throw new ExtractionException("Serialized frame-machine Stage A manifest is empty.");
            return ValidateManifestAgainstExpected(bytes, manifest, expected, irBytes, leanBytes);
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Serialized frame-machine Stage A manifest is invalid: {exception.Message}");
        }
    }

    private static SourceManifest ValidateManifestAgainstExpected(byte[] bytes, SourceManifest manifest,
        SourceManifest expected, byte[] irBytes, byte[] leanBytes)
    {
        ValidateManifest(manifest, expected, irBytes, leanBytes);
        if (!SerializeCanonical(manifest).AsSpan().SequenceEqual(bytes))
            throw new ExtractionException("Serialized frame-machine Stage A manifest bytes are not canonical.");
        return manifest;
    }

    internal static void ValidateStageAReady(IrDocument document)
    {
        ValidateShape(document);
        if (document.AcceptanceState != StageAAdmitted)
            throw new IncompleteProfileException("Frame-machine routing extraction is not Stage A admitted.");
        if (document.Sources.Length != 114 || document.Admissions.Length != 181 ||
            document.OpcodePackages.Length != 14 || document.OpcodeRoutes.Length != 1024 ||
            document.PrecompileRoutes.Length != 18)
            throw new ExtractionException("Frame-machine Stage A exact cardinalities changed.");
        if (document.Sources.Any(static source => !IsSha256(source.ExpectedSha256) ||
            (!source.IsRaw && !IsSha256(source.ExpectedRoslynSyntaxSha256))))
            throw new ExtractionException("Frame-machine Stage A contains an unpinned source identity.");
        if (document.Admissions.Any(static admission => string.IsNullOrWhiteSpace(admission.ExpectedSyntaxKind) ||
            !IsSha256(admission.ExpectedSha256)))
            throw new ExtractionException("Frame-machine Stage A contains an unpinned exact member admission.");
        foreach (OpcodePackageDescriptor package in document.OpcodePackages)
        {
            if (!IsSha256(package.ManifestSha256) || !IsSafeRelativePath(package.ProofModulePath) ||
                !IsSha256(package.ProofModuleSha256) ||
                package.RequiredTheorems.Any(static theorem => !IsNonEmpty(theorem.FullyQualifiedName) ||
                    !IsSha256(theorem.SignatureSha256)))
                throw new ExtractionException($"Frame-machine Stage A contains an invalid opcode dependency {package.Name}.");
            if (!package.Admitted || package.RequiredTheorems.Length == 0)
                throw new ExtractionException($"Frame-machine Stage A opcode proof admission changed for {package.Name}.");
        }
        if (document.OpcodePackages.Count(static package => package.Admitted) != 14 ||
            document.OpcodePackages.Sum(static package => package.RequiredTheorems.Length) != 16)
            throw new ExtractionException("Frame-machine Stage A must bind exactly 14 adequate package refinements and 16 theorem identities.");
        if (document.OpcodeRoutes.Any(static route => route.RouteKind == Unresolved ||
            route.Instruction == Unresolved || route.ActivationRule == Unresolved || route.Package == Unresolved ||
            route.ClosedHandlerRoot == Unresolved))
            throw new ExtractionException("Frame-machine Stage A contains an unresolved dispatch route.");
        if (document.OpcodeRoutes.Any(static route => !route.Admitted))
            throw new ExtractionException("Frame-machine Stage A contains an operationally unadmitted opcode route.");
        if (document.OpcodeRoutes.Count(static route => route.RouteKind == "enabled" && route.Admitted) != 612)
            throw new ExtractionException("Frame-machine Stage A must have exactly 612 operationally admitted enabled routes.");
        if (document.PrecompileRoutes.Any(static route => !IsSha256(route.WrapperManifestSha256) ||
            route.WrapperManifestPath.StartsWith(Unresolved + "/", StringComparison.Ordinal)))
            throw new ExtractionException("Frame-machine Stage A contains an unbound precompile wrapper identity.");
        if (document.PrecompileRoutes.Any(static route => route.Admitted))
            throw new ExtractionException("Frame-machine Stage A must not claim unproved production precompile wrappers.");
        if (document.PrecompileRoutes.Any(static route => route.FullyQualifiedTheorem is not null))
            throw new ExtractionException("Frame-machine Stage A must not fabricate precompile theorem identities.");
        string[] requiredOpenGates =
        [
            "derive-frame-transition-from-source-admitted-semantic-ir",
            "extract-and-bind-settlement-primitive-leaves",
            "prove-generated-frame-driver-refines-independent-reference",
            "prove-transaction-reference-execute-evm-call-adapter",
            "prove-completion-and-fuel-adequacy-for-admitted-executions",
            "admit-every-amsterdam-precompile-body-and-wrapper-refinement",
        ];
        if (requiredOpenGates.Any(gate => !document.IncompleteGates.Contains(gate, StringComparer.Ordinal)))
            throw new ExtractionException("Frame-machine Stage A improperly closes a later execution or adapter gate.");
    }

    private static StageABuild BuildStageA(string root)
    {
        IrDocument design = DesignIr();
        IReadOnlyDictionary<string, SourcePin> pins = LoadSourcePins();
        Dictionary<string, ParsedSource> parsed = LoadAndValidateSources(root, design.Sources, pins);
        ProductionSourceDescriptor[] sources = design.Sources.Select(source =>
        {
            SourcePin pin = pins[source.Path];
            return source with { ExpectedSha256 = pin.RawSha256, ExpectedRoslynSyntaxSha256 = pin.SyntaxSha256 };
        }).ToArray();
        AdmissionIdentity[] admissionIdentities = ResolveAdmissions(design.Admissions, parsed);
        MemberAdmissionDescriptor[] admissions = design.Admissions.Zip(admissionIdentities, static (selector, identity) =>
            selector with { ExpectedSyntaxKind = identity.SyntaxKind, ExpectedSha256 = identity.Sha256 }).ToArray();
        DependencyPin[] dependencyPins = LoadDependencyPins();
        OpcodePackageDescriptor[] packages = ResolveOpcodePackages(root, design.OpcodePackages, dependencyPins);
        OpcodeRouteDescriptor[] routes = ResolveOpcodeRoutes(root, parsed, packages, dependencyPins);
        PrecompileRouteDescriptor[] precompiles = ResolvePrecompiles(root, design.PrecompileRoutes, dependencyPins);
        DependencyIdentity[] dependencies = packages.Select(static package =>
                new DependencyIdentity("opcodePackage", package.Name, package.ManifestPath, package.ManifestSha256!,
                    package.LeanModule, package.ProofModulePath, package.ProofModuleSha256,
                    package.RequiredTheorems, package.Admitted))
            .Concat(precompiles.Select(static precompile =>
                new DependencyIdentity("precompileWrapper", precompile.Name, precompile.WrapperManifestPath,
                    precompile.WrapperManifestSha256!, null, null, null, [], false))).ToArray();
        IrDocument ir = design with
        {
            AcceptanceState = StageAAdmitted,
            Sources = sources,
            Admissions = admissions,
            OpcodePackages = packages,
            OpcodeRoutes = routes,
            PrecompileRoutes = precompiles,
            SemanticBindings =
            [
                "all 1024 ordered DispatchTable and byte routes are extracted by evaluating the pinned Amsterdam fork predicates over the production table assignments and accepted sibling routing IRs",
                "the SLOTNUM sibling overlap is resolved exclusively to ControlFlowOpcodeExtractor and every other opcode byte has at most one owner",
                "all 412 bad-instruction routes (103 per table) are explicit entries with a source-derived closed table-specific BadInstructionOpcode root",
                "all 14 opcode packages bind 16 adequate operational-refinement theorem identities",
                "all 612 enabled table-byte routes are operationally admitted with no composition-incomplete enabled route",
                "CALLDATALOAD routing composes the independently accepted CallDataLoadOpcodeExtractor artifact triple and closed Amsterdam operational theorem",
                "CALL, CALLCODE, DELEGATECALL, STATICCALL, CREATE, CREATE2, and SELFDESTRUCT routing composes the independently accepted CallCreateOpcodeExtractor artifact triple and closed Amsterdam operational theorem",
                "all 76 Environment routes compose the independently accepted EnvironmentOpcodeExtractor artifact triple and package-wide closed Amsterdam operational theorem",
                "all 18 Amsterdam precompile address identities are byte-pinned while their production body and wrapper refinements remain unadmitted",
                "the emitted Lean module is theorem-free routing metadata and contains no frame execution or handwritten transition body",
            ],
            OpenExtractionObligations =
            [
                "C# compilation, CLR/JIT/AOT, function-pointer execution, unsafe stack layout, pooling, and hardware remain runtime premises.",
                "The fourteen adequate opcode-package refinements are not yet composed into a whole-frame execution theorem.",
                "All 18 precompile identities are routed but their native or cryptographic bodies and production wrappers remain unadmitted.",
                "World-state, journal, tracing, frame settlement, fuel adequacy, transaction installation, and block composition remain outside Stage A.",
            ],
            IncompleteGates =
            [
                "derive-frame-transition-from-source-admitted-semantic-ir",
                "implement-production-derived-theorem-free-frame-transition-emitter",
                "extract-and-bind-settlement-primitive-leaves",
                "admit-every-amsterdam-precompile-body-and-wrapper-refinement",
                "prove-generated-frame-driver-refines-independent-reference",
                "instantiate-refinement-relations-with-named-generated-definitions",
                "prove-transaction-reference-execute-evm-call-adapter",
                "bind-generated-production-input-projection",
                "prove-completion-and-fuel-adequacy-for-admitted-executions",
                "carry-and-consume-ripemd-latch-after-outer-rollback",
                "prove-vmstate-refund-agrees-with-opcode-refund-counter",
                "prove-failure-origin-classification-and-control-order",
                "verify-world-journal-and-concrete-tracer-adapters",
                "complete-independent-stage-b-review",
            ],
        };
        ValidateStageAReady(ir);
        ValidateDependencyArray(dependencies, ir);
        SourceIdentity[] sourceIdentities = sources.Select(source => new SourceIdentity(
            source.Path, source.ExpectedSha256!, source.ExpectedRoslynSyntaxSha256)).ToArray();
        return new(ir, sourceIdentities, admissionIdentities, dependencies);
    }

    private static Dictionary<string, ParsedSource> LoadAndValidateSources(string root,
        ProductionSourceDescriptor[] descriptors, IReadOnlyDictionary<string, SourcePin> pins)
    {
        if (pins.Count != descriptors.Length || descriptors.Any(source => !pins.ContainsKey(source.Path)))
            throw new ExtractionException("Production closure resource does not exactly match the 114 required source paths.");
        Dictionary<string, ParsedSource> result = new(StringComparer.Ordinal);
        foreach (ProductionSourceDescriptor descriptor in descriptors)
        {
            string fullPath = ResolveExactPath(root, descriptor.Path);
            byte[] bytes = File.ReadAllBytes(fullPath);
            SourcePin pin = pins[descriptor.Path];
            string rawSha256 = Hash(bytes);
            if (rawSha256 != pin.RawSha256)
                throw new ExtractionException($"Unadmitted complete source content for {descriptor.Path}; found SHA-256 {rawSha256}.");
            CompilationUnitSyntax? syntax = null;
            if (descriptor.IsRaw)
            {
                if (pin.SyntaxSha256 is not null)
                    throw new ExtractionException($"Raw source {descriptor.Path} unexpectedly has a Roslyn digest.");
            }
            else
            {
                syntax = CSharpSyntaxTree.ParseText(Encoding.UTF8.GetString(bytes), StageAParseOptions, descriptor.Path)
                    .GetCompilationUnitRoot();
                Diagnostic? error = syntax.SyntaxTree.GetDiagnostics()
                    .FirstOrDefault(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
                if (error is not null) throw new ExtractionException($"Roslyn rejected {descriptor.Path}: {error}.");
                string syntaxSha256 = Hash(CompleteCanonical(syntax));
                if (syntaxSha256 != pin.SyntaxSha256)
                    throw new ExtractionException($"Unadmitted canonical Roslyn syntax for {descriptor.Path}; found SHA-256 {syntaxSha256}.");
            }
            result.Add(descriptor.Path, new(descriptor.Path, fullPath, bytes, syntax));
        }
        ValidateRawBuildSelection(result);
        return result;
    }

    private static AdmissionIdentity[] ResolveAdmissions(MemberAdmissionDescriptor[] selectors,
        IReadOnlyDictionary<string, ParsedSource> sources)
    {
        List<AdmissionIdentity> identities = [];
        foreach (MemberAdmissionDescriptor selector in selectors)
        {
            CompilationUnitSyntax root = sources[selector.SourcePath].Syntax ??
                throw new ExtractionException($"Member admission points at raw source {selector.SourcePath}.");
            MemberDeclarationSyntax member = ResolveMember(root, selector);
            BaseNamespaceDeclarationSyntax[] namespaces = member.AncestorsAndSelf()
                .OfType<BaseNamespaceDeclarationSyntax>().ToArray();
            if (namespaces.Length != 1)
                throw new ExtractionException($"Expected one file or block namespace for {AdmissionKey(selector)}; found {namespaces.Length}; member parent is {member.Parent?.Kind().ToString() ?? "none"}.");
            string actualNamespace = namespaces[0].Name.ToString();
            if (actualNamespace != selector.Namespace)
                throw new ExtractionException($"Expected namespace {selector.Namespace} for {AdmissionKey(selector)}; found {actualNamespace}.");
            identities.Add(new(selector.SourcePath, selector.Namespace, selector.OwnerPath, selector.MemberKind,
                selector.MemberName, selector.MemberGenericArity, selector.ParameterTypes,
                member.Kind().ToString(), Hash(CompleteCanonical(member))));
        }
        if (identities.Select(AdmissionIdentityKey).Distinct(StringComparer.Ordinal).Count() != identities.Count)
            throw new ExtractionException("Resolved Stage A member admissions collide.");
        return [.. identities];
    }

    private static MemberDeclarationSyntax ResolveMember(CompilationUnitSyntax root, MemberAdmissionDescriptor selector)
    {
        BaseTypeDeclarationSyntax owner = ResolveOwner(root, selector.OwnerPath);
        if (selector.MemberKind == "type" && owner.Identifier.ValueText == selector.MemberName)
            return owner;
        MemberDeclarationSyntax[] matches = DirectMembers(owner).Where(member => Matches(member, selector)).ToArray();
        if (matches.Length != 1)
        {
            string candidates = string.Join("; ", DirectMembers(owner).OfType<MethodDeclarationSyntax>()
                .Where(method => method.Identifier.ValueText == selector.MemberName)
                .Select(method => $"{method.Identifier.ValueText}/{method.TypeParameterList?.Parameters.Count ?? 0}/{ParameterTypes(method)}"));
            throw new ExtractionException($"Expected exactly one {AdmissionKey(selector)}; found {matches.Length}. Candidates: {candidates}");
        }
        return matches[0];
    }

    private static BaseTypeDeclarationSyntax ResolveOwner(CompilationUnitSyntax root, string ownerPath)
    {
        string[] segments = ownerPath.Split('.');
        BaseTypeDeclarationSyntax? current = null;
        foreach (string segment in segments)
        {
            (string name, int arity) = ParseOwnerSegment(segment);
            IEnumerable<BaseTypeDeclarationSyntax> candidates = current is null
                ? root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
                    .Where(type => !type.Ancestors().OfType<BaseTypeDeclarationSyntax>().Any())
                : current is TypeDeclarationSyntax parent ? parent.Members.OfType<BaseTypeDeclarationSyntax>() : [];
            BaseTypeDeclarationSyntax[] matches = candidates.Where(type =>
                type.Identifier.ValueText == name && TypeArity(type) == arity).ToArray();
            if (matches.Length != 1)
                throw new ExtractionException($"Expected exactly one owner segment {segment} in {ownerPath}; found {matches.Length}.");
            current = matches[0];
        }
        return current!;
    }

    private static bool Matches(MemberDeclarationSyntax member, MemberAdmissionDescriptor selector) =>
        selector.MemberKind switch
        {
            "method" when member is MethodDeclarationSyntax method =>
                method.Identifier.ValueText == selector.MemberName &&
                (method.TypeParameterList?.Parameters.Count ?? 0) == selector.MemberGenericArity &&
                ParameterTypes(method) == selector.ParameterTypes,
            "property" when member is PropertyDeclarationSyntax property =>
                property.Identifier.ValueText == selector.MemberName,
            "field" when member is FieldDeclarationSyntax field =>
                field.Declaration.Variables.Any(variable => variable.Identifier.ValueText == selector.MemberName),
            _ => false,
        };

    private static IEnumerable<MemberDeclarationSyntax> DirectMembers(BaseTypeDeclarationSyntax owner)
    {
        if (owner is EnumDeclarationSyntax value)
        {
            foreach (EnumMemberDeclarationSyntax member in value.Members)
                yield return member;
            yield break;
        }
        if (owner is not TypeDeclarationSyntax type) yield break;
        foreach (MemberDeclarationSyntax member in type.Members)
        {
            if (member is ExtensionBlockDeclarationSyntax extension)
            {
                foreach (MemberDeclarationSyntax extensionMember in extension.Members)
                    yield return extensionMember;
            }
            else
            {
                yield return member;
            }
        }
    }

    private static OpcodePackageDescriptor[] ResolveOpcodePackages(string root, OpcodePackageDescriptor[] packages,
        DependencyPin[] pins)
    {
        Dictionary<string, DependencyPin> expected = pins.Where(static pin => pin.Kind == "opcodePackage")
            .ToDictionary(static pin => pin.Name, StringComparer.Ordinal);
        if (expected.Count != OpcodePackageBindings.Length)
            throw new ExtractionException("Dependency closure must contain exactly 14 opcode package entries.");
        return packages.Select((package, index) =>
        {
            (string name, string module, string proofModulePath, string[] requiredTheorems, bool admitted) =
                OpcodePackageBindings[index];
            if (package.Name != name || !expected.TryGetValue(name, out DependencyPin? pin) ||
                package.ManifestPath != pin.IdentityPath ||
                pin.BindingState != (admitted ? "theoremBound" : "compositionIncomplete") ||
                pin.ProofModule != module || pin.ProofModulePath != proofModulePath ||
                !pin.RequiredTheorems.Select(static theorem => theorem.FullyQualifiedName)
                    .SequenceEqual(requiredTheorems, StringComparer.Ordinal))
                throw new ExtractionException($"Opcode package order or identity changed at {package.Name}.");
            ValidatePinnedFile(root, pin.IdentityPath, pin.IdentitySha256);
            ValidatePinnedFile(root, pin.RoutingIrPath!, pin.RoutingIrSha256!);
            ValidatePinnedFile(root, pin.ProofModulePath!, pin.ProofModuleSha256!);
            string proofSource = File.ReadAllText(ResolveExactPath(root, pin.ProofModulePath!), Encoding.UTF8);
            foreach (ProofTheoremIdentity theorem in pin.RequiredTheorems)
            {
                string signature = ExtractLeanTheoremSignature(proofSource, theorem.FullyQualifiedName);
                string actualSha256 = Hash(Encoding.UTF8.GetBytes(signature));
                if (actualSha256 != theorem.SignatureSha256)
                    throw new ExtractionException($"Theorem signature identity changed for {theorem.FullyQualifiedName}; found SHA-256 {actualSha256}.");
            }
            return package with
            {
                LeanModule = module,
                ProofModulePath = proofModulePath,
                RequiredTheorems = pin.RequiredTheorems,
                ProofModuleSha256 = pin.ProofModuleSha256,
                Admitted = admitted,
                ManifestSha256 = pin.IdentitySha256,
            };
        }).ToArray();
    }

    private static OpcodeRouteDescriptor[] ResolveOpcodeRoutes(string root,
        IReadOnlyDictionary<string, ParsedSource> sources, OpcodePackageDescriptor[] packages, DependencyPin[] pins)
    {
        Dictionary<int, List<OwnedOpcode>> contenders = [];
        foreach (OpcodePackageDescriptor package in packages)
        {
            DependencyPin pin = pins.Single(value => value.Kind == "opcodePackage" && value.Name == package.Name);
            foreach (OwnedOpcode opcode in ReadOwnedOpcodes(root, package, pin.RoutingIrPath!))
            {
                if (!contenders.TryGetValue(opcode.Byte, out List<OwnedOpcode>? values))
                    contenders.Add(opcode.Byte, values = []);
                values.Add(opcode);
            }
        }
        ProductionRouting production = DeriveProductionRouting(
            sources[OpcodeHandlerSourcePath].Syntax!, sources[OpcodeDispatchSourcePath].Syntax!,
            AmsterdamForkPredicates.Derive(sources));
        int[] overlaps = contenders.Where(static pair => pair.Value.Count > 1).Select(static pair => pair.Key).ToArray();
        if (!overlaps.SequenceEqual(PreferredOpcodeOwners.Keys.Order()))
            throw new ExtractionException("Opcode package ownership overlaps changed from the explicit Stage A resolution set.");
        Dictionary<int, OwnedOpcode> owners = contenders.ToDictionary(static pair => pair.Key, pair =>
        {
            if (pair.Value.Count == 1) return pair.Value[0];
            string preferred = PreferredOpcodeOwners[pair.Key];
            return pair.Value.Single(value => value.Package == preferred);
        });
        if (owners.Count != 153)
            throw new ExtractionException($"Expected 153 unique Amsterdam opcode owners after overlap resolution; found {owners.Count}.");
        string[] expectedInstructions = owners.Values.Select(static owner => owner.Instruction).Append("INVALID")
            .Order(StringComparer.Ordinal).ToArray();
        if (!production.InstructionNames.Order(StringComparer.Ordinal)
            .SequenceEqual(expectedInstructions, StringComparer.Ordinal))
        {
            string[] missing = expectedInstructions.Except(production.InstructionNames, StringComparer.Ordinal).ToArray();
            string[] extra = production.InstructionNames.Except(expectedInstructions, StringComparer.Ordinal).ToArray();
            throw new ExtractionException($"Production active Amsterdam instruction assignments do not exactly match the owned route set; missing={string.Join(',', missing)}; extra={string.Join(',', extra)}.");
        }
        Dictionary<int, string> instructionNames = ReadInstructionNames(sources[InstructionSourcePath].Syntax!);
        owners = owners.ToDictionary(static pair => pair.Key,
            pair => production.BindSibling(pair.Value));
        OpcodeRouteDescriptor[] routes = new OpcodeRouteDescriptor[1024];
        for (int tableIndex = 0; tableIndex < DispatchTableNames.Length; tableIndex++)
            for (int value = 0; value < 256; value++)
            {
                string table = DispatchTableNames[tableIndex];
                if (owners.TryGetValue(value, out OwnedOpcode? owner))
                {
                    string rootForTable = owner.Roots.Single(pair => pair.Table == table).Root;
                    bool admitted = packages.Single(package => package.Name == owner.Package).Admitted;
                    routes[tableIndex * 256 + value] = new(table, value, owner.Instruction, "enabled",
                        owner.Activation, owner.Package, rootForTable, admitted);
                }
                else
                {
                    bool explicitlyInvalid = instructionNames.TryGetValue(value, out string? instruction) && instruction == "INVALID";
                    string kind = explicitlyInvalid || instruction is null ? "badInstruction" : "disabled";
                    string label = instruction ?? $"UNASSIGNED_0x{value:x2}";
                    string handlerRoot = explicitlyInvalid
                        ? production.BindDirect("INVALID").Single(pair => pair.Table == table).Root
                        : production.BadInstructionRoots.Single(pair => pair.Table == table).Root;
                    routes[tableIndex * 256 + value] = new(table, value, label, kind,
                        explicitlyInvalid ? "explicit-invalid-opcode" : instruction is null ? "unassigned-byte" : "disabled-in-Amsterdam",
                        "dispatcher", handlerRoot, true);
                }
            }
        int enabledCount = routes.Count(static route => route.RouteKind == "enabled");
        int badInstructionCount = routes.Count(static route => route.RouteKind == "badInstruction");
        int disabledCount = routes.Count(static route => route.RouteKind == "disabled");
        if (enabledCount != 612 || badInstructionCount != 412 || disabledCount != 0)
            throw new ExtractionException($"Amsterdam route cardinalities changed: enabled={enabledCount}, bad={badInstructionCount}, disabled={disabledCount} ({string.Join(", ", routes.Where(static route => route.RouteKind == "disabled").Select(static route => route.Instruction).Distinct())}).");
        foreach (string table in DispatchTableNames)
            if (routes.Count(route => route.DispatchTable == table && route.RouteKind == "badInstruction") != 103)
                throw new ExtractionException($"Amsterdam dispatch table {table} does not have exactly 103 bad-instruction routes.");
        return routes;
    }

    private static IEnumerable<OwnedOpcode> ReadOwnedOpcodes(string root, OpcodePackageDescriptor package, string irPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(ResolveExactPath(root, irPath)));
        JsonElement rootElement = document.RootElement;
        List<(string Name, string Instruction, int Byte, string Activation)> opcodes = [];
        if (rootElement.TryGetProperty("opcode", out JsonElement singleton) && singleton.ValueKind == JsonValueKind.Object)
            opcodes.Add(ReadOpcode(singleton));
        else if (rootElement.TryGetProperty("opcodes", out JsonElement array) && array.ValueKind == JsonValueKind.Array)
            foreach (JsonElement opcode in array.EnumerateArray()) opcodes.Add(ReadOpcode(opcode));
        else
            throw new ExtractionException($"Opcode package {package.Name} has no exact opcode descriptors.");
        if (opcodes.Count != package.DeclaredOpcodeCount)
            throw new ExtractionException($"Opcode package {package.Name} declared {package.DeclaredOpcodeCount} rows but its IR has {opcodes.Count}.");
        JsonElement specializations = rootElement.GetProperty("specializations");
        foreach ((string name, string instruction, int value, string activation) in opcodes)
        {
            List<(string Table, string Root)> roots = [];
            foreach (JsonElement specialization in specializations.EnumerateArray())
            {
                string opcode = specialization.GetProperty("opcode").GetString()!;
                if (!opcode.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    !opcode.Equals(instruction, StringComparison.OrdinalIgnoreCase)) continue;
                string table = GetString(specialization, "dispatchTable", "table");
                string closedRoot = GetString(specialization, "closedRoot", "enabledRoot");
                roots.Add((table, closedRoot));
            }
            if (roots.Count != 4 || !roots.Select(static item => item.Table)
                    .Order(StringComparer.Ordinal).SequenceEqual(DispatchTableNames.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
                roots.Any(static item => string.IsNullOrWhiteSpace(item.Root)))
                throw new ExtractionException($"Opcode package {package.Name}/{instruction} does not have four exact closed roots.");
            yield return new(package.Name, name, instruction, value, activation, roots);
        }
    }

    private static (string Name, string Instruction, int Byte, string Activation) ReadOpcode(JsonElement opcode)
    {
        string name = opcode.GetProperty("name").GetString()!;
        string instruction = opcode.GetProperty("instruction").GetString()!;
        int value = opcode.GetProperty("opcodeByte").GetInt32();
        string activation = "Amsterdam";
        foreach (string key in new[] { "activation", "activationEvidence", "activationGate", "forkGate" })
            if (opcode.TryGetProperty(key, out JsonElement property))
            {
                activation = property.ValueKind == JsonValueKind.String ? property.GetString()! : property.GetRawText();
                break;
            }
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(instruction) || value is < 0 or > 255)
            throw new ExtractionException("Opcode package contains an invalid opcode identity.");
        return (name, instruction, value, activation);
    }

    private static PrecompileRouteDescriptor[] ResolvePrecompiles(string root, PrecompileRouteDescriptor[] routes,
        DependencyPin[] pins)
    {
        DependencyPin[] expected = pins.Where(static pin => pin.Kind == "precompileWrapper").ToArray();
        if (expected.Length != PrecompileBindings.Length)
            throw new ExtractionException("Dependency closure must contain exactly 18 precompile wrapper identities.");
        return routes.Select((route, index) =>
        {
            (string name, string fileName, string module) = PrecompileBindings[index];
            DependencyPin pin = expected[index];
            string path = $"tools/Evm/Lean/Eip803x/Precompiles/{fileName}";
            if (route.Name != name || pin.Name != name || pin.IdentityPath != path ||
                pin.BindingState != "unadmitted" || pin.ProofModule is not null ||
                pin.ProofModulePath is not null || pin.ProofModuleSha256 is not null ||
                pin.RequiredTheorems.Length != 0)
                throw new ExtractionException($"Precompile wrapper identity changed at {name}.");
            ValidatePinnedFile(root, path, pin.IdentitySha256);
            return route with
            {
                WrapperManifestPath = path,
                LeanModule = module,
                FullyQualifiedTheorem = null,
                Admitted = false,
                WrapperManifestSha256 = pin.IdentitySha256,
            };
        }).ToArray();
    }

    private static SourceManifest BuildManifest(StageABuild build, byte[] irBytes, byte[] leanBytes,
        string combinedSourceSha256) => new(
        1,
        ExtractorVersion,
        typeof(CSharpSyntaxTree).Assembly.GetName().Version?.ToString() ?? "unknown",
        "CSharp14",
        KernelName,
        build.SourceIdentities,
        build.AdmissionIdentities,
        new(IrFileName, Hash(irBytes)),
        new(LeanFileName, Hash(leanBytes)),
        combinedSourceSha256,
        build.Dependencies,
        build.Ir.SemanticBindings);

    private static void ValidateManifest(SourceManifest actual, SourceManifest expected, byte[] irBytes, byte[] leanBytes)
    {
        if (actual.Sources is null || actual.Admissions is null || actual.Dependencies is null ||
            actual.Ir is null || actual.Lean is null || actual.SemanticBindings is null ||
            actual.Sources.Any(static item => item is null) || actual.Admissions.Any(static item => item is null) ||
            actual.Dependencies.Any(static item => item is null))
            throw new ExtractionException("Serialized frame-machine Stage A manifest contains null fields or entries.");
        if (!SerializeCanonical(actual).AsSpan().SequenceEqual(SerializeCanonical(expected)))
            throw new ExtractionException("Serialized frame-machine Stage A manifest does not exactly match recomputed canonical identities.");
        if (actual.Ir.Path != IrFileName || actual.Ir.Sha256 != Hash(irBytes) ||
            actual.Lean.Path != LeanFileName || actual.Lean.Sha256 != Hash(leanBytes))
            throw new ExtractionException("Frame-machine Stage A artifact digest binding changed.");
        ValidateDependencyArray(actual.Dependencies, BuildIrForDependencyValidation(expected));
        if (actual.CombinedSourceSha256 != CombinedSourceHash(actual.Sources, actual.Admissions, actual.Dependencies))
            throw new ExtractionException("Frame-machine Stage A combined source digest is inconsistent.");
    }

    private static IrDocument BuildIrForDependencyValidation(SourceManifest manifest)
    {
        IrDocument design = DesignIr();
        OpcodePackageDescriptor[] packages = design.OpcodePackages.Select((package, index) => package with
        {
            ManifestPath = manifest.Dependencies[index].Path,
            ManifestSha256 = manifest.Dependencies[index].Sha256,
            LeanModule = manifest.Dependencies[index].ProofModule!,
            ProofModulePath = manifest.Dependencies[index].ProofModulePath!,
            ProofModuleSha256 = manifest.Dependencies[index].ProofModuleSha256,
            RequiredTheorems = manifest.Dependencies[index].RequiredTheorems,
            Admitted = manifest.Dependencies[index].Admitted,
        }).ToArray();
        PrecompileRouteDescriptor[] precompiles = design.PrecompileRoutes.Select((route, index) => route with
        {
            WrapperManifestPath = manifest.Dependencies[packages.Length + index].Path,
            WrapperManifestSha256 = manifest.Dependencies[packages.Length + index].Sha256,
            LeanModule = PrecompileBindings[index].LeanModule,
            FullyQualifiedTheorem = null,
            Admitted = manifest.Dependencies[packages.Length + index].Admitted,
        }).ToArray();
        return design with { OpcodePackages = packages, PrecompileRoutes = precompiles };
    }

    private static void ValidateDependencyArray(DependencyIdentity[] dependencies, IrDocument document)
    {
        SourceManifest shell = new(1, ExtractorVersion, "", "", KernelName, [], [],
            new("", new string('0', 64)), new("", new string('0', 64)), new string('0', 64), dependencies, []);
        ValidateDependencyIdentities(shell, document);
        for (int index = 0; index < dependencies.Length; index++)
        {
            bool expected = index < document.OpcodePackages.Length
                ? document.OpcodePackages[index].Admitted
                : document.PrecompileRoutes[index - document.OpcodePackages.Length].Admitted;
            if (dependencies[index].Admitted != expected)
                throw new ExtractionException($"Dependency admission status changed at index {index}.");
        }
    }

    private static IReadOnlyDictionary<string, SourcePin> LoadSourcePins()
    {
        string[] lines = LoadResourceLines(SourceClosureResourceSuffix);
        Dictionary<string, SourcePin> result = new(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            string[] fields = line.Split('|');
            if (fields.Length != 3 || !IsSha256(fields[1]) ||
                (fields[2] != "-" && !IsSha256(fields[2])) ||
                !result.TryAdd(fields[0], new(fields[1], fields[2] == "-" ? null : fields[2])))
                throw new ExtractionException("Malformed or duplicate production closure resource entry.");
        }
        return result;
    }

    private static DependencyPin[] LoadDependencyPins()
    {
        string[] lines = LoadResourceLines(DependencyClosureResourceSuffix);
        List<DependencyPin> result = [];
        HashSet<string> paths = new(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            string[] fields = line.Split('|');
            if (fields.Length == 7 && fields[0] == "precompileWrapper" && fields[4] == "-" &&
                fields[5] == "-" && fields[6] == "false" && IsSafeRelativePath(fields[2]) &&
                IsSha256(fields[3]) && paths.Add(fields[2]))
            {
                result.Add(new(fields[0], fields[1], fields[2], fields[3], null, null, "unadmitted",
                    null, null, null, []));
                continue;
            }
            if (fields.Length != 11 || fields[0] != "opcodePackage" ||
                !IsSafeRelativePath(fields[2]) || !IsSha256(fields[3]) ||
                (fields[4] != "-" && !IsSafeRelativePath(fields[4])) ||
                (fields[5] != "-" && !IsSha256(fields[5])) ||
                fields[6] is not ("theoremBound" or "compositionIncomplete") ||
                (fields[7] != "-" && string.IsNullOrWhiteSpace(fields[7])) ||
                (fields[8] != "-" && !IsSafeRelativePath(fields[8])) ||
                (fields[9] != "-" && !IsSha256(fields[9])) ||
                (fields[10] != "-" && string.IsNullOrWhiteSpace(fields[10])) || !paths.Add(fields[2]))
                throw new ExtractionException("Malformed or duplicate dependency closure resource entry.");
            bool theoremBound = fields[6] == "theoremBound";
            if (fields[4] == "-" || fields[5] == "-" || fields[7] == "-" || fields[8] == "-" ||
                fields[9] == "-" || theoremBound == (fields[10] == "-"))
                throw new ExtractionException("Dependency theorem binding or composition-incomplete identity is malformed.");
            foreach (int pathIndex in new[] { 4, 8 })
                if (fields[pathIndex] != "-" && !paths.Add(fields[pathIndex]))
                    throw new ExtractionException("Dependency identity, routing, and proof-module paths must be globally unique.");
            ProofTheoremIdentity[] theoremIdentities = fields[10] == "-" ? [] : fields[10].Split(';').Select(value =>
            {
                string[] identity = value.Split('=');
                if (identity.Length != 2 || string.IsNullOrWhiteSpace(identity[0]) || !IsSha256(identity[1]))
                    throw new ExtractionException("Malformed theorem identity set in dependency closure resource.");
                return new ProofTheoremIdentity(identity[0], identity[1]);
            }).ToArray();
            if (theoremIdentities.Select(static theorem => theorem.FullyQualifiedName)
                .Distinct(StringComparer.Ordinal).Count() != theoremIdentities.Length)
                throw new ExtractionException("Dependency theorem identities must be unique within a package.");
            result.Add(new(fields[0], fields[1], fields[2], fields[3],
                fields[4] == "-" ? null : fields[4], fields[5] == "-" ? null : fields[5], fields[6],
                fields[7] == "-" ? null : fields[7], fields[8] == "-" ? null : fields[8],
                fields[9] == "-" ? null : fields[9], theoremIdentities));
        }
        if (result.Count != 32)
            throw new ExtractionException($"Expected 32 dependency closure entries; found {result.Count}.");
        return [.. result];
    }

    private static string[] LoadResourceLines(string suffix)
    {
        Assembly assembly = typeof(EvmFrameMachineProfile).Assembly;
        string resourceName = assembly.GetManifestResourceNames().SingleOrDefault(name => name.EndsWith(suffix, StringComparison.Ordinal)) ??
            throw new ExtractionException($"Missing embedded Stage A resource {suffix}.");
        using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
        using StreamReader reader = new(stream);
        List<string> lines = [];
        string? line;
        while ((line = reader.ReadLine()) is not null)
            if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith('#')) lines.Add(line);
        return [.. lines];
    }

    private static string ExtractLeanTheoremSignature(string source, string fullyQualifiedTheorem)
    {
        string theorem = fullyQualifiedTheorem[(fullyQualifiedTheorem.LastIndexOf('.') + 1)..];
        MatchCollection matches = Regex.Matches(source,
            $@"(?ms)^[\t ]*theorem[\t ]+{Regex.Escape(theorem)}\b.*?(?=:=\s*by\b)");
        if (matches.Count != 1)
            throw new ExtractionException($"Expected exactly one theorem declaration for {fullyQualifiedTheorem}.");
        return Regex.Replace(matches[0].Value, @"\s+", " ").Trim();
    }

    private static Dictionary<int, string> ReadInstructionNames(CompilationUnitSyntax root)
    {
        EnumDeclarationSyntax instruction = root.DescendantNodes().OfType<EnumDeclarationSyntax>()
            .Single(value => value.Identifier.ValueText == "Instruction");
        Dictionary<int, string> result = [];
        foreach (EnumMemberDeclarationSyntax member in instruction.Members)
        {
            string literal = member.EqualsValue?.Value.ToString() ??
                throw new ExtractionException($"Instruction {member.Identifier.ValueText} has no explicit byte value.");
            int value = literal.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? int.Parse(literal.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : int.Parse(literal, CultureInfo.InvariantCulture);
            if (!result.TryAdd(value, member.Identifier.ValueText))
                throw new ExtractionException($"Instruction byte 0x{value:x2} is duplicated.");
        }
        return result;
    }

    private static string GetString(JsonElement element, string first, string second)
    {
        if (element.TryGetProperty(first, out JsonElement value) || element.TryGetProperty(second, out value))
            return value.GetString() ?? throw new ExtractionException($"Routing property {first}/{second} is null.");
        throw new ExtractionException($"Routing property {first}/{second} is missing.");
    }

    private static void ValidateRawBuildSelection(IReadOnlyDictionary<string, ParsedSource> sources)
    {
        string props = Encoding.UTF8.GetString(sources["src/Nethermind/Directory.Build.props"].Bytes);
        string targets = Encoding.UTF8.GetString(sources["src/Nethermind/Directory.Build.targets"].Bytes);
        if (!props.Contains("<Using Include=\"System.Runtime.Intrinsics.Vector256&lt;byte&gt;\" Alias=\"EvmWord\" />", StringComparison.Ordinal) ||
            !targets.Contains("<Compile Remove=\"**/*.std.cs\" />", StringComparison.Ordinal) ||
            !targets.Contains("<Compile Remove=\"**/*.zkevm.cs\" />", StringComparison.Ordinal))
            throw new ExtractionException("Pinned standard-build EvmWord or partial-source selection changed.");
    }

    private static void ValidatePinnedFile(string root, string path, string expectedSha256)
    {
        string actual = Hash(File.ReadAllBytes(ResolveExactPath(root, path)));
        if (actual != expectedSha256)
            throw new ExtractionException($"Unadmitted dependency content for {path}; found SHA-256 {actual}.");
    }

    private static string CombinedSourceHash(IEnumerable<SourceIdentity> sources,
        IEnumerable<AdmissionIdentity> admissions, IEnumerable<DependencyIdentity> dependencies)
    {
        StringBuilder value = new();
        foreach (SourceIdentity source in sources)
            value.Append("source|").Append(source.Path).Append('|').Append(source.Sha256).Append('|')
                .Append(source.RoslynSyntaxSha256 ?? "-").Append('\n');
        foreach (AdmissionIdentity admission in admissions)
            value.Append("admission|").Append(AdmissionIdentityKey(admission)).Append('|')
                .Append(admission.SyntaxKind).Append('|').Append(admission.Sha256).Append('\n');
        foreach (DependencyIdentity dependency in dependencies)
            value.Append("dependency|").Append(dependency.Kind).Append('|').Append(dependency.Name).Append('|')
                .Append(dependency.Path).Append('|').Append(dependency.Sha256).Append('|')
                .Append(dependency.ProofModule ?? "-").Append('|')
                .Append(dependency.ProofModulePath ?? "-").Append('|')
                .Append(dependency.ProofModuleSha256 ?? "-").Append('|')
                .Append(string.Join(';', dependency.RequiredTheorems.Select(static theorem =>
                    $"{theorem.FullyQualifiedName}={theorem.SignatureSha256}"))).Append('|')
                .Append(dependency.Admitted).Append('\n');
        return Hash(Encoding.UTF8.GetBytes(value.ToString()));
    }

    private static string AdmissionIdentityKey(AdmissionIdentity admission) =>
        $"{admission.SourcePath}|{admission.Namespace}|{admission.OwnerPath}|{admission.MemberKind}|{admission.MemberName}|{admission.MemberGenericArity}|{admission.ParameterTypes}";

    private static (string Name, int Arity) ParseOwnerSegment(string value)
    {
        int marker = value.LastIndexOf('`');
        return marker < 0 ? (value, 0) :
            (value[..marker], int.Parse(value.AsSpan(marker + 1), CultureInfo.InvariantCulture));
    }

    private static int TypeArity(BaseTypeDeclarationSyntax type) => type is TypeDeclarationSyntax declaration
        ? declaration.TypeParameterList?.Parameters.Count ?? 0 : 0;

    private static string ParameterTypes(MethodDeclarationSyntax method) => string.Join(",",
        method.ParameterList.Parameters.Select(static parameter =>
            string.Concat(parameter.Modifiers.Select(static modifier => modifier.Text)) + Canonical(parameter.Type!)));

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
        string resolved = Path.GetFullPath(current);
        string rooted = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(rooted, StringComparison.OrdinalIgnoreCase))
            throw new ExtractionException($"Path escapes repository root: {relativePath}.");
        return resolved;
    }

    private static byte[] SerializeCanonical<T>(T value) => Utf8WithoutBom.GetBytes(
        JsonSerializer.Serialize(value, JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");

    private static void WriteDeterministic(string path, byte[] bytes)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return;
        File.WriteAllBytes(path, bytes);
    }

    private sealed record SourcePin(string RawSha256, string? SyntaxSha256);
    private sealed record ParsedSource(string RelativePath, string FullPath, byte[] Bytes, CompilationUnitSyntax? Syntax);
    private sealed record DependencyPin(string Kind, string Name, string IdentityPath, string IdentitySha256,
        string? RoutingIrPath, string? RoutingIrSha256, string BindingState, string? ProofModule,
        string? ProofModulePath, string? ProofModuleSha256, ProofTheoremIdentity[] RequiredTheorems)
    {
        internal IEnumerable<string> InputPaths
        {
            get
            {
                yield return IdentityPath;
                if (RoutingIrPath is not null) yield return RoutingIrPath;
                if (ProofModulePath is not null) yield return ProofModulePath;
            }
        }
    }
    private sealed record OwnedOpcode(string Package, string Name, string Instruction, int Byte, string Activation,
        List<(string Table, string Root)> Roots);
    private sealed record StageABuild(IrDocument Ir, SourceIdentity[] SourceIdentities,
        AdmissionIdentity[] AdmissionIdentities, DependencyIdentity[] Dependencies);
}
