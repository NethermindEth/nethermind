// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Evm.Lean.EvmFrameMachineExtractor;

internal static partial class EvmFrameMachineProfile
{
    internal const string IrFileName = "EvmFrameMachineKernel.ir.json";
    internal const string ManifestFileName = "EvmFrameMachineKernel.source-manifest.json";
    internal const string LeanFileName = "EvmFrameMachineKernel.lean";

    private const string ExtractorVersion = "1.0.0-stage-a";
    private const string KernelName = "standard-mainnet-amsterdam-evm-frame-machine";
    private const string Pending = "pending";
    private const string StageAAdmitted = "stage-a-admitted";
    private const string Unresolved = "unresolved";
    private const string TransactionRoot = "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteTransaction<TTracingInst>(Nethermind.Evm.VmState<Nethermind.Evm.GasPolicy.EthereumGasPolicy>,Nethermind.Evm.State.IWorldState,Nethermind.Evm.Tracing.ITxTracer)";
    private const string ClosedGasPolicy = "Nethermind.Evm.GasPolicy.EthereumGasPolicy";
    private const string ClosedFork = "Nethermind.Specs.Forks.Amsterdam";

    private static readonly string[] DispatchTableNames = ["NoTrace", "NoTraceCancelable", "Traced", "TracedCancelable"];
    private static readonly string[] FramePhaseNames = ["fresh", "continuation", "running"];
    private static readonly string[] FrameExitNames = ["success", "revert", "exception"];
    private static readonly string[] FrameMergeNames = ["mergeSuccess", "mergeRevert", "mergeException"];
    private static readonly string[] DispatchOrder = ["clearFreshReturnData", "selectPrecompileOrBytecode", "execute", "classifyStep"];
    private static readonly string[] FreshFrameEntryOrder = ["traceActionStart", "addTransferLog", "runFrame"];
    private static readonly string[] TopLevelCloseOrder = ["traceActionEnd", "prepareTopLevelSubstate", "disposeFrames"];
    private static readonly string[] SettlementDiscriminants =
    [
        "ExecutionType",
        "IsCreateOnPreExistingAccount",
        "IsCreateStateGasCharged",
        "NewAccountCharged",
        "InitialStateGasUsed",
        "StateGasRefundAdvanced",
        "Snapshot",
        "_shouldRestoreRipemdTouch",
    ];
    private static readonly string[] TransactionWideFields =
    [
        "_previousCallResult",
        "_previousCallOutputDestination",
        "previousCallOutputLength",
        "ReturnDataBuffer",
        "_shouldRestoreRipemdTouch",
    ];
    private static readonly string[] FailureOriginNames =
    [
        "ordinary",
        "handleException",
        "handleFailure",
        "nestedPrecompileSoftFailure",
        "directPrecompileSoftFailure",
    ];
    private static readonly string[] SuccessSettlementOrder =
    [
        "popParent",
        "markContinuation",
        "classifyCreate",
        "incorporateAdvancedRefundBeforeGasForNonCreate",
        "refundChildGas",
        "finishReturnOrCodeDeposit",
        "commitChildOnSuccess",
        "incorporateAdvancedRefundAfterCommitForCreate",
        "repayStateGasSpill",
    ];
    private static readonly string[] RevertSettlementOrder =
    [
        "popParent",
        "markContinuation",
        "returnExecutionGas",
        "removeAdvancedRefund",
        "restoreChildStateGas",
        "creditCreateOrNewAccount",
        "restoreSnapshot",
        "restoreRipemdTouch",
        "prepareRevertReturn",
    ];
    private static readonly string[] HandleExceptionOrder =
    [
        "traceActionError",
        "restoreSnapshot",
        "restoreRipemdTouch",
        "classifyTopLevel",
        "clearReturnState",
        "popParent",
        "removeAdvancedRefund",
        "restoreChildStateGasOnHalt",
        "creditCreateOrNewAccount",
    ];
    private static readonly string[] HandleFailureOrder =
    [
        "restoreSnapshot",
        "restoreRipemdTouch",
        "traceOperationRemainingGasZero",
        "traceOperationError",
        "traceActionError",
        "classifyTopLevel",
        "clearReturnState",
        "popParent",
        "removeAdvancedRefund",
        "restoreChildStateGasOnHalt",
        "creditCreateOrNewAccount",
    ];
    private static readonly string[] NestedPrecompileFailureOrder =
    [
        "clearExecutionGas",
        "classifyShouldRevert",
        "popParent",
        "markContinuation",
        "returnExecutionGas",
        "removeAdvancedRefund",
        "restoreChildStateGas",
        "creditCreateOrNewAccount",
        "restoreSnapshot",
        "restoreRipemdTouch",
        "prepareRevertReturn",
    ];
    private static readonly string[] DirectPrecompileFailureOrder =
    [
        "runInlinePrecompile",
        "classifyManagedException",
        "pushFailureToCurrentFrame",
        "continueCurrentFrame",
    ];
    private static readonly string[] CodeDepositFailureOrder =
    [
        "calculateDepositCost",
        "checkInvalidCode",
        "chargeParent",
        "revertRefundToHalt",
        "creditCreateStateGas",
        "removeAdvancedRefund",
        "restoreSnapshot",
        "deletePhysicalNewAccountIfNeeded",
        "restoreRipemdTouch",
        "reportActionError",
    ];

    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    internal static ExtractionResult Extract(string repoRoot, string outputDirectory, string? leanOutputPath = null)
        => ExtractStageA(repoRoot, outputDirectory, leanOutputPath);

    internal static IrDocument DesignIr() => new(
        SchemaVersion: 1,
        ExtractorVersion,
        KernelName,
        AcceptanceState: Pending,
        Boundary: new(
            TransactionRoot,
            ClosedGasPolicy,
            ClosedFork,
            [.. DispatchTableNames],
            [.. FramePhaseNames],
            [.. FrameExitNames],
            [.. FrameExitNames]),
        FrameDriver: new(
            [.. DispatchOrder],
            [.. FreshFrameEntryOrder],
            "lifo",
            [
                new("success", "mergeSuccess"),
                new("revert", "mergeRevert"),
                new("exception", "mergeException"),
            ],
            [.. TopLevelCloseOrder],
            [.. SettlementDiscriminants],
            [.. TransactionWideFields],
            [.. FailureOriginNames],
            [.. SuccessSettlementOrder],
            [.. RevertSettlementOrder],
            [.. HandleExceptionOrder],
            [.. HandleFailureOrder],
            [.. NestedPrecompileFailureOrder],
            [.. DirectPrecompileFailureOrder],
            [.. CodeDepositFailureOrder]),
        Sources: BuildSources(),
        Admissions: BuildAdmissions(),
        OpcodePackages: BuildOpcodePackages(),
        OpcodeRoutes: BuildPendingOpcodeRoutes(),
        PrecompileRoutes: BuildPrecompileRoutes(),
        SemanticBindings:
        [
            "all 1024 dispatch-table and byte pairs resolve through the admitted Amsterdam opcode tables to exactly one enabled or bad-instruction route",
            "the four tracing and cancellation tables preserve the same opcode meaning while retaining their distinct production roots",
            "bytecode frames dispatch until continue, suspend, terminal return, explicit revert, or exceptional halt",
            "suspended parents resume only after child gas, world, access, log, destroy, returndata, and result-push effects are merged",
            "code-deposit invalid-code and out-of-gas branches are internal outcomes of successful child CREATE settlement rather than frame exits",
            "HandleException traces before restoring its current-frame snapshot while HandleFailure restores before instruction and action failure tracing",
            "nested non-direct precompile soft failure clears child execution gas before ordinary ShouldRevert settlement while direct precompile failure remains an inline current-frame continuation",
            "precompile frames use the provider-selected address and fork gate before gas debit and native-or-formal execution",
            "only source-projected, completed, fuel-adequate top-level frame results may project toward TransactionReference.Oracle.executeEvmCall",
            "the completed projection carries the transaction-wide RIPEMD touch latch for consumption after any outer transaction rollback",
            "the TransactionReference refund projection uses VmState.Refund and requires equality with the opcode gas refund counter",
        ],
        OpenExtractionObligations:
        [
            "C# compilation, CLR/JIT/AOT, function-pointer tail calls, unsafe stack layout, pooling, and hardware remain explicit runtime premises.",
            "Concrete trie, database, code-cache, journal-storage, and tracer implementations remain adapter premises until separately refined.",
            "Cryptographic and native precompile implementations must be supplied by their accepted wrapper refinements; the frame driver proves routing and merge only.",
            "The dispatch fuel used by the executable Lean driver requires an adequacy theorem against execution-gas progress and terminal zero-cost instructions.",
            "TransactionReference projection requires proofs that the generated input projector is total on admitted EVM entries and preserves every lifecycle-owned field in executionControlOf.",
            "TransactionReference currently has no RIPEMD dirty-touch field, so installing executeEvmCall remains gated on a transaction-adapter theorem that consumes the carried latch after outer rollback.",
        ],
        IncompleteGates:
        [
            "pin-complete-source-bytes-and-canonical-roslyn",
            "implement-and-pin-exact-member-selectors",
            "derive-frame-transition-from-source-admitted-semantic-ir",
            "implement-production-derived-theorem-free-lean-emitter",
            "implement-strict-source-manifest-roundtrip-validation",
            "implement-schema-conformance-tests",
            "extract-and-bind-settlement-primitive-leaves",
            "bind-exact-32-dependency-identities-and-recompute-digests",
            "resolve-each-of-1024-dispatch-table-byte-routes",
            "resolve-duplicate-sibling-package-ownership",
            "compose-all-accepted-opcode-package-refinements-into-frame-theorem",
            "admit-every-amsterdam-precompile-route-and-wrapper-theorem",
            "emit-and-byte-pin-canonical-ir-manifest-and-lean",
            "implement-accepted-artifact-extraction-and-write-path",
            "prove-generated-frame-driver-refines-independent-reference",
            "instantiate-refinement-relations-with-named-generated-definitions",
            "prove-transaction-reference-execute-evm-call-adapter",
            "bind-generated-production-input-projection",
            "prove-completion-and-fuel-adequacy-for-admitted-executions",
            "carry-and-consume-ripemd-latch-after-outer-rollback",
            "prove-vmstate-refund-agrees-with-opcode-refund-counter",
            "prove-failure-origin-classification-and-control-order",
            "add-field-complete-mutation-and-determinism-tests",
            "run-warning-as-error-dotnet-and-lean-gates",
            "complete-independent-review",
        ]);

    internal static void ValidateShape(IrDocument document)
    {
        if (document.Boundary is null || document.FrameDriver is null || document.Sources is null || document.Admissions is null ||
            document.OpcodePackages is null || document.OpcodeRoutes is null || document.PrecompileRoutes is null ||
            document.SemanticBindings is null || document.OpenExtractionObligations is null || document.IncompleteGates is null)
            throw new ExtractionException("Frame-machine IR contains a null top-level field.");
        if (document.SchemaVersion != 1 || document.ExtractorVersion != ExtractorVersion ||
            document.Kernel != KernelName || document.AcceptanceState is not (Pending or StageAAdmitted))
            throw new ExtractionException("Frame-machine IR header does not match the design profile.");
        ValidateBoundary(document.Boundary);
        ValidateFrameDriver(document.FrameDriver);
        ValidateStrings(document.SemanticBindings, "semantic bindings", requireNonEmpty: true);
        ValidateStrings(document.OpenExtractionObligations, "open extraction obligations", requireNonEmpty: true);
        ValidateStrings(document.IncompleteGates, "incomplete gates", requireNonEmpty: false);
        if (document.OpcodeRoutes.Length != 1024)
            throw new ExtractionException("Frame-machine IR must contain exactly 1024 dispatch-table and opcode-byte routes.");
        for (int index = 0; index < document.OpcodeRoutes.Length; index++)
        {
            OpcodeRouteDescriptor route = document.OpcodeRoutes[index] ??
                throw new ExtractionException($"Opcode route {index} is null.");
            string expectedTable = DispatchTableNames[index / 256];
            int expectedByte = index % 256;
            if (route.DispatchTable != expectedTable || route.Byte != expectedByte ||
                !IsNonEmpty(route.Instruction) || !IsNonEmpty(route.RouteKind) ||
                !IsNonEmpty(route.ActivationRule) || !IsNonEmpty(route.Package) || !IsNonEmpty(route.ClosedHandlerRoot))
                throw new ExtractionException($"Opcode route index {index} does not carry {expectedTable}/0x{expectedByte:x2}.");
            if (route.RouteKind is not (Unresolved or "enabled" or "disabled" or "badInstruction"))
                throw new ExtractionException($"Opcode route 0x{route.Byte:x2} has unknown kind {route.RouteKind}.");
        }

        HashSet<string> paths = new(StringComparer.Ordinal);
        foreach (ProductionSourceDescriptor source in document.Sources)
        {
            if (source is null || !IsNonEmpty(source.Path) || !IsNonEmpty(source.Role) || !paths.Add(source.Path) ||
                source.Path.Contains('\\') || Path.IsPathRooted(source.Path) || source.Path.Split('/').Contains(".."))
                throw new ExtractionException("Frame-machine source paths must be non-empty and unique.");
            if (source.IsRaw && source.ExpectedRoslynSyntaxSha256 is not null)
                throw new ExtractionException($"Raw source {source.Path} may not carry a Roslyn syntax digest.");
        }

        HashSet<string> admissionKeys = new(StringComparer.Ordinal);
        foreach (MemberAdmissionDescriptor admission in document.Admissions)
        {
            if (admission is null || !IsNonEmpty(admission.SourcePath) || !IsNonEmpty(admission.Namespace) ||
                !IsNonEmpty(admission.OwnerPath) || !IsNonEmpty(admission.MemberKind) ||
                !IsNonEmpty(admission.MemberName) || admission.ParameterTypes is null || admission.MemberGenericArity < 0 ||
                !paths.Contains(admission.SourcePath) || !admissionKeys.Add(AdmissionKey(admission)))
                throw new ExtractionException("Frame-machine member admissions must be complete, unique, and inside the source closure.");
            if (admission.MemberKind is not ("type" or "constructor" or "method" or "property" or "field" or "operator"))
                throw new ExtractionException($"Unknown member admission kind {admission.MemberKind}.");
        }

        if (document.OpcodePackages.Length != 14)
            throw new ExtractionException("Frame-machine IR must contain exactly 14 opcode package dependencies.");
        HashSet<string> packageNames = new(StringComparer.Ordinal);
        HashSet<string> dependencyPaths = new(StringComparer.Ordinal);
        foreach (OpcodePackageDescriptor package in document.OpcodePackages)
        {
            if (package is null || !IsNonEmpty(package.Name) || !IsNonEmpty(package.ManifestPath) ||
                !IsNonEmpty(package.LeanModule) || !IsNonEmpty(package.ProofModulePath) ||
                package.RequiredTheorems is null || package.RequiredTheorems.Any(static theorem => theorem is null ||
                    !IsNonEmpty(theorem.FullyQualifiedName)) ||
                package.RequiredTheorems.Select(static theorem => theorem.FullyQualifiedName)
                    .Distinct(StringComparer.Ordinal).Count() != package.RequiredTheorems.Length ||
                package.DeclaredOpcodeCount is < 1 or > 256 || !packageNames.Add(package.Name) ||
                !IsSafeRelativePath(package.ManifestPath) || !IsSafeRelativePath(package.ProofModulePath) ||
                !dependencyPaths.Add(package.ManifestPath) || !dependencyPaths.Add(package.ProofModulePath))
                throw new ExtractionException("Frame-machine opcode package names must be non-null and unique.");
        }

        if (document.PrecompileRoutes.Length != 18)
            throw new ExtractionException("Frame-machine IR must contain exactly 18 mainnet precompile routes.");
        HashSet<int> precompileAddresses = [];
        HashSet<string> precompileNames = new(StringComparer.Ordinal);
        foreach (PrecompileRouteDescriptor precompile in document.PrecompileRoutes)
        {
            if (precompile is null || !IsNonEmpty(precompile.Name) || precompile.Address is < 1 or > 0x100 ||
                !IsNonEmpty(precompile.ActivationRule) || !IsNonEmpty(precompile.ProviderRoot) ||
                !IsNonEmpty(precompile.WrapperManifestPath) ||
                !IsNonEmpty(precompile.LeanModule) || precompile.FullyQualifiedTheorem is not null ||
                !precompileNames.Add(precompile.Name) || !precompileAddresses.Add(precompile.Address) ||
                !IsSafeRelativePath(precompile.WrapperManifestPath) || !dependencyPaths.Add(precompile.WrapperManifestPath))
                throw new ExtractionException("Frame-machine precompile addresses must be non-null and unique.");
        }
    }

    internal static void ValidateReadyForExtraction(IrDocument document)
    {
        ValidateShape(document);
        List<string> failures = [];
        if (document.AcceptanceState != "admitted") failures.Add("acceptanceState is not admitted");
        if (document.IncompleteGates.Length != 0) failures.Add($"{document.IncompleteGates.Length} incomplete gates remain");

        foreach (ProductionSourceDescriptor source in document.Sources)
        {
            if (!IsSha256(source.ExpectedSha256)) failures.Add($"source byte digest is unpinned: {source.Path}");
            if (!source.IsRaw && !IsSha256(source.ExpectedRoslynSyntaxSha256))
                failures.Add($"source syntax digest is unpinned: {source.Path}");
        }

        foreach (MemberAdmissionDescriptor admission in document.Admissions)
        {
            if (!PathsContain(document.Sources, admission.SourcePath))
                failures.Add($"admission source is outside the source closure: {admission.SourcePath}");
            if (string.IsNullOrWhiteSpace(admission.ExpectedSyntaxKind) || !IsSha256(admission.ExpectedSha256))
                failures.Add($"member admission is unpinned: {AdmissionKey(admission)}");
        }

        foreach (OpcodePackageDescriptor package in document.OpcodePackages)
        {
            if (!package.Admitted || !IsSha256(package.ManifestSha256) ||
                !IsSha256(package.ProofModuleSha256) || package.RequiredTheorems.Length == 0 ||
                package.RequiredTheorems.Any(static theorem => !IsSha256(theorem.SignatureSha256)) ||
                package.LeanModule == Unresolved || package.ProofModulePath == Unresolved ||
                package.RequiredTheorems.Any(static theorem => theorem.FullyQualifiedName == Unresolved))
                failures.Add($"opcode package is unadmitted: {package.Name}");
        }

        foreach (OpcodeRouteDescriptor route in document.OpcodeRoutes)
        {
            if (!route.Admitted || route.Instruction == Unresolved || route.Package == Unresolved ||
                route.ActivationRule == Unresolved || route.ClosedHandlerRoot == Unresolved ||
                route.RouteKind == Unresolved ||
                (route.Package != "dispatcher" && !document.OpcodePackages.Any(package => package.Name == route.Package)))
                failures.Add($"opcode route 0x{route.Byte:x2} is unresolved");
        }

        foreach (PrecompileRouteDescriptor precompile in document.PrecompileRoutes)
        {
            if (!precompile.Admitted || !IsSha256(precompile.WrapperManifestSha256) ||
                precompile.WrapperManifestPath.StartsWith(Unresolved + "/", StringComparison.Ordinal) ||
                precompile.LeanModule == Unresolved ||
                precompile.FullyQualifiedTheorem is not null)
                failures.Add($"precompile route is unadmitted: {precompile.Name}");
        }

        if (failures.Count != 0)
            throw new IncompleteProfileException("Frame-machine extraction is fail-closed:\n- " + string.Join("\n- ", failures));
    }

    internal static byte[] Serialize(IrDocument document)
    {
        ValidateShape(document);
        string json = JsonSerializer.Serialize(document, JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        return Utf8WithoutBom.GetBytes(json);
    }

    internal static IrDocument DeserializeForEmission(byte[] bytes)
    {
        RejectDuplicateProperties(bytes);
        try
        {
            IrDocument document = JsonSerializer.Deserialize<IrDocument>(bytes, JsonOptions) ??
                throw new ExtractionException("Serialized frame-machine IR is empty.");
            ValidateReadyForExtraction(document);
            if (!Serialize(document).AsSpan().SequenceEqual(bytes))
                throw new ExtractionException("Serialized frame-machine IR bytes are not canonical.");
            return document;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Serialized frame-machine IR is invalid: {exception.Message}");
        }
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static void ValidateDependencyIdentities(SourceManifest manifest, IrDocument document)
    {
        ValidateShape(document);
        if (manifest.Dependencies is null || manifest.Dependencies.Length != 32)
            throw new ExtractionException("Frame-machine manifest must contain exactly 32 dependency identities.");

        HashSet<string> paths = new(StringComparer.Ordinal);
        for (int index = 0; index < manifest.Dependencies.Length; index++)
        {
            DependencyIdentity dependency = manifest.Dependencies[index] ??
                throw new ExtractionException($"Frame-machine manifest dependency {index} is null.");
            string expectedKind;
            string expectedName;
            string expectedPath;
            string? expectedSha256;
            string? expectedProofModule;
            string? expectedProofModulePath;
            string? expectedProofModuleSha256;
            ProofTheoremIdentity[] expectedRequiredTheorems;
            if (index < document.OpcodePackages.Length)
            {
                OpcodePackageDescriptor package = document.OpcodePackages[index];
                expectedKind = "opcodePackage";
                expectedName = package.Name;
                expectedPath = package.ManifestPath;
                expectedSha256 = package.ManifestSha256;
                expectedProofModule = package.LeanModule;
                expectedProofModulePath = package.ProofModulePath;
                expectedProofModuleSha256 = package.ProofModuleSha256;
                expectedRequiredTheorems = package.RequiredTheorems;
            }
            else
            {
                PrecompileRouteDescriptor precompile = document.PrecompileRoutes[index - document.OpcodePackages.Length];
                expectedKind = "precompileWrapper";
                expectedName = precompile.Name;
                expectedPath = precompile.WrapperManifestPath;
                expectedSha256 = precompile.WrapperManifestSha256;
                expectedProofModule = null;
                expectedProofModulePath = null;
                expectedProofModuleSha256 = null;
                expectedRequiredTheorems = [];
            }

            if (dependency.Kind != expectedKind || dependency.Name != expectedName ||
                dependency.Path != expectedPath || dependency.Sha256 != expectedSha256 ||
                dependency.ProofModule != expectedProofModule ||
                dependency.ProofModulePath != expectedProofModulePath ||
                dependency.ProofModuleSha256 != expectedProofModuleSha256 ||
                dependency.RequiredTheorems is null ||
                !dependency.RequiredTheorems.SequenceEqual(expectedRequiredTheorems) ||
                !IsSafeRelativePath(dependency.Path) || !paths.Add(dependency.Path) || !IsSha256(dependency.Sha256))
                throw new ExtractionException($"Frame-machine manifest dependency {index} does not match its unique admitted identity.");
            if (dependency.Kind == "opcodePackage")
            {
                if (!IsNonEmpty(dependency.ProofModule) || dependency.ProofModulePath is not string proofModulePath ||
                    !IsSafeRelativePath(proofModulePath) ||
                    !IsSha256(dependency.ProofModuleSha256) || !paths.Add(proofModulePath) ||
                    dependency.RequiredTheorems.Select(static theorem => theorem.FullyQualifiedName)
                        .Distinct(StringComparer.Ordinal).Count() != dependency.RequiredTheorems.Length ||
                    dependency.RequiredTheorems.Any(static theorem => !IsNonEmpty(theorem.FullyQualifiedName) ||
                        !IsSha256(theorem.SignatureSha256)) ||
                    dependency.Admitted == (dependency.RequiredTheorems.Length == 0))
                    throw new ExtractionException($"Opcode dependency {dependency.Name} has an invalid proof-module or theorem identity set.");
            }
            else if (dependency.Admitted || dependency.ProofModule is not null || dependency.ProofModulePath is not null ||
                dependency.ProofModuleSha256 is not null || dependency.RequiredTheorems.Length != 0)
            {
                throw new ExtractionException($"Unadmitted dependency {dependency.Name} fabricates a theorem identity.");
            }
        }
    }

    private static ProductionSourceDescriptor[] BuildSources()
    {
        List<ProductionSourceDescriptor> sources =
        [
            Raw("src/Nethermind/Directory.Build.props", "standard EvmWord alias"),
            Raw("src/Nethermind/Directory.Build.targets", "standard versus zkEVM partial-file selection"),
            Source("src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs", "production VM, precompile provider, and code repository wiring"),
            Source("src/Nethermind/Nethermind.Blockchain/EthereumPrecompileProvider.cs", "mainnet precompile address routing"),
            Source("src/Nethermind/Nethermind.Core/Address.cs", "precompile address shape and low-index conversion"),
            Source("src/Nethermind/Nethermind.Core/BlockHeader.cs", "slot and block execution context"),
            Source("src/Nethermind/Nethermind.Core/GasCostOf.cs", "frame-visible gas constants"),
            Source("src/Nethermind/Nethermind.Core/IJournal.cs", "world-state rollback contract"),
            Source("src/Nethermind/Nethermind.Core/Collections/JournalCollection.cs", "log journal rollback implementation"),
            Source("src/Nethermind/Nethermind.Core/Collections/JournalSet.cs", "access and destroy journal rollback implementation"),
            Source("src/Nethermind/Nethermind.Core/Precompiles/PrecompiledAddresses.cs", "canonical mainnet precompile addresses"),
            Source("src/Nethermind/Nethermind.Core/TypeFlags.cs", "closed compile-time flag values"),
            Source("src/Nethermind/Nethermind.Core/Extensions/Bytes.cs", "standard byte helper owner"),
            Source("src/Nethermind/Nethermind.Core/Extensions/Bytes.std.cs", "standard endian implementation"),
            Source("src/Nethermind/Nethermind.Core/Extensions/EvmWordExtensions.cs", "standard EVM-word conversion"),
            Source("src/Nethermind/Nethermind.Core/Specs/IReleaseSpec.cs", "fork feature interface"),
            Source("src/Nethermind/Nethermind.Core/Specs/IReleaseSpecExtensions.cs", "fork feature adapters"),
            Source("src/Nethermind/Nethermind.Core/Specs/IReleaseSpecExtensions.std.cs", "standard fork feature adapters"),
            Source("src/Nethermind/Nethermind.Evm/IVirtualMachine.cs", "public execution boundary"),
            Source("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "transaction frame driver and merge implementation"),
            Source("src/Nethermind/Nethermind.Evm/VirtualMachine.CallResult.cs", "frame result classification"),
            Source("src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs", "opcode dispatch loop and four tables"),
            Source("src/Nethermind/Nethermind.Evm/VirtualMachine.ExecutionHandlers.cs", "fork-closed frame/precompile helper roots"),
            Source("src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs", "256-byte handler table construction"),
            Source("src/Nethermind/Nethermind.Evm/VirtualMachine.std.cs", "standard VM partial and opcode-table cache"),
            Source("src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs", "standard trace and cancellation table choice"),
            Source("src/Nethermind/Nethermind.Evm/SpecFlags.std.cs", "standard fork-feature specialization flags"),
            Source("src/Nethermind/Nethermind.Evm/Instruction.cs", "opcode byte identities"),
            Source("src/Nethermind/Nethermind.Evm/EvmException.cs", "exception classification"),
            Source("src/Nethermind/Nethermind.Evm/StatusCode.cs", "success and failure returndata constants"),
            Source("src/Nethermind/Nethermind.Evm/BlockExecutionContext.cs", "closed block and fork execution context"),
            Source("src/Nethermind/Nethermind.Evm/TxExecutionContext.cs", "code repository and transaction execution context"),
            Source("src/Nethermind/Nethermind.Evm/ExecutionEnvironment.cs", "frame environment and ownership"),
            Source("src/Nethermind/Nethermind.Evm/ExecutionType.cs", "CALL and CREATE frame classification"),
            Source("src/Nethermind/Nethermind.Evm/ExecutionMetricsCounters.cs", "execution-scoped metrics side effects"),
            Source("src/Nethermind/Nethermind.Evm/TransactionSubstate.cs", "top-level execution observation"),
            Source("src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "transaction rollback and RIPEMD touch-latch consumer"),
            Source("src/Nethermind/Nethermind.Evm/VmState.cs", "frame snapshots, stacks, journals, and parent commit"),
            Source("src/Nethermind/Nethermind.Evm/VmStateStack.cs", "bounded suspended-parent ownership"),
            Source("src/Nethermind/Nethermind.Evm/PoppedAddressCache.cs", "execution-scoped address decoding cache"),
            Source("src/Nethermind/Nethermind.Evm/EvmStack.cs", "logical stack and result push/pop behavior"),
            Source("src/Nethermind/Nethermind.Evm/EvmStack.std.cs", "standard stack word conversion"),
            Source("src/Nethermind/Nethermind.Evm/StackPool.cs", "frame stack owner"),
            Source("src/Nethermind/Nethermind.Evm/StackPool.std.cs", "standard frame stack allocation"),
            Source("src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs", "frame memory, returndata copy, and ownership"),
            Source("src/Nethermind/Nethermind.Evm/StackAccessTracker.cs", "access, log, destroy, and refund journals"),
            Source("src/Nethermind/Nethermind.Evm/CodeAnalysis/CodeInfo.cs", "bytecode versus precompile route"),
            Source("src/Nethermind/Nethermind.Evm/CodeAnalysis/CodeInfoFactory.cs", "analyzed bytecode construction"),
            Source("src/Nethermind/Nethermind.Evm/CodeAnalysis/JumpDestinationAnalyzer.cs", "jump boundary representation"),
            Source("src/Nethermind/Nethermind.Evm/CodeAnalysis/JumpDestinationAnalyzer.std.cs", "standard jump analysis"),
            Source("src/Nethermind/Nethermind.Evm/ICodeInfoRepository.cs", "code and delegation adapter boundary"),
            Source("src/Nethermind/Nethermind.Evm/CodeInfoRepository.cs", "main code/precompile/delegation lookup"),
            Source("src/Nethermind/Nethermind.Evm/CacheCodeInfoRepository.cs", "production cached code adapter"),
            Source("src/Nethermind/Nethermind.Evm/IPrecompileProvider.cs", "precompile provider boundary"),
            Source("src/Nethermind/Nethermind.Evm/Precompiles/IPrecompile.cs", "precompile gas and execution boundary"),
            Source("src/Nethermind/Nethermind.Evm.Precompiles/ECRecoverPrecompile.cs", "ECREC address and wrapper boundary"),
            Source("src/Nethermind/Nethermind.Evm.Precompiles/Sha256Precompile.cs", "SHA256 address and wrapper boundary"),
            Source("src/Nethermind/Nethermind.Evm.Precompiles/Ripemd160Precompile.cs", "RIPEMD160 address and wrapper boundary"),
            Source("src/Nethermind/Nethermind.Evm.Precompiles/IdentityPrecompile.cs", "IDENTITY address and wrapper boundary"),
            Source("src/Nethermind/Nethermind.Evm.Precompiles/ModExpPrecompile.cs", "MODEXP address and wrapper boundary"),
            Source("src/Nethermind/Nethermind.Evm.Precompiles/BN254AddPrecompile.cs", "BN254_ADD address and wrapper boundary"),
            Source("src/Nethermind/Nethermind.Evm.Precompiles/BN254MulPrecompile.cs", "BN254_MUL address and wrapper boundary"),
            Source("src/Nethermind/Nethermind.Evm.Precompiles/BN254PairingCheckPrecompile.cs", "BN254_PAIRING address and wrapper boundary"),
            Source("src/Nethermind/Nethermind.Evm.Precompiles/Blake2FPrecompile.cs", "BLAKE2F address and wrapper boundary"),
            Source("src/Nethermind/Nethermind.Evm.Precompiles/KzgPointEvaluationPrecompile.cs", "KZG_POINT_EVALUATION address and wrapper boundary"),
            Source("src/Nethermind/Nethermind.Evm.Precompiles/Bls12381G1AddPrecompile.cs", "BLS12_G1ADD address and wrapper boundary"),
            Source("src/Nethermind/Nethermind.Evm.Precompiles/Bls12381G1MsmPrecompile.cs", "BLS12_G1MSM address and wrapper boundary"),
            Source("src/Nethermind/Nethermind.Evm.Precompiles/Bls12381G2AddPrecompile.cs", "BLS12_G2ADD address and wrapper boundary"),
            Source("src/Nethermind/Nethermind.Evm.Precompiles/Bls12381G2MsmPrecompile.cs", "BLS12_G2MSM address and wrapper boundary"),
            Source("src/Nethermind/Nethermind.Evm.Precompiles/Bls12381PairingCheckPrecompile.cs", "BLS12_PAIRING address and wrapper boundary"),
            Source("src/Nethermind/Nethermind.Evm.Precompiles/Bls12381FpToG1Precompile.cs", "BLS12_MAP_FP_TO_G1 address and wrapper boundary"),
            Source("src/Nethermind/Nethermind.Evm.Precompiles/Bls12381Fp2ToG2Precompile.cs", "BLS12_MAP_FP2_TO_G2 address and wrapper boundary"),
            Source("src/Nethermind/Nethermind.Evm.Precompiles/SecP256r1Precompile.cs", "P256VERIFY address and wrapper boundary"),
            Source("src/Nethermind/Nethermind.Evm/CodeDepositHandler.cs", "CREATE runtime-code settlement"),
            Source("src/Nethermind/Nethermind.Evm/Instructions/EvmCalculations.cs", "checked code-size calculations"),
            Source("src/Nethermind/Nethermind.Evm/GasPolicy/IGasCost.cs", "closed gas tags"),
            Source("src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs", "frame gas interface"),
            Source("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "closed execution and state gas implementation"),
            Source("src/Nethermind/Nethermind.Evm/GasPolicy/PrecompileGasPricingKernel.cs", "precompile gas debit"),
            Source("src/Nethermind/Nethermind.Evm/GasPolicy/StateGasChargeKernel.cs", "state reservoir charge"),
            Source("src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs", "child gas merge and restoration"),
            Source("src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionAdapterKernel.cs", "production state-gas adapter"),
            Source("src/Nethermind/Nethermind.Evm/State/IWorldState.cs", "world-state snapshot interface"),
            Source("src/Nethermind/Nethermind.Evm/State/Snapshot.cs", "world/storage/access snapshot token"),
            Source("src/Nethermind/Nethermind.Evm/State/WorldStateExtensions.cs", "frame world-state adapter calls"),
            Source("src/Nethermind/Nethermind.Evm/Tracing/ITxTracer.cs", "action, instruction, refund, and result callback boundary"),
            Source("src/Nethermind/Nethermind.Evm/Tracing/TracerExtensions.cs", "trace payload adapters"),
            Source("src/Nethermind/Nethermind.Evm/Tracing/TraceStack.cs", "stack trace representation"),
            Source("src/Nethermind/Nethermind.Evm/Tracing/TraceMemory.cs", "memory trace representation"),
            Source("src/Nethermind/Nethermind.Specs/ReleaseSpec.cs", "precompile membership cache and low-address mask"),
            Source("src/Nethermind/Nethermind.Specs/Forks/NamedReleaseSpec.cs", "fork inheritance replay"),
        ];

        string[] forks =
        [
            "00_Olympic", "01_Frontier", "02_Homestead", "03_Dao", "04_TangerineWhistle",
            "05_SpuriousDragon", "06_Byzantium", "07_Constantinople", "08_ConstantinopleFix",
            "09_Istanbul", "10_MuirGlacier", "11_Berlin", "12_London", "13_ArrowGlacier",
            "14_GrayGlacier", "15_Paris", "16_Shanghai", "17_Cancun", "18_Prague", "19_Osaka",
            "20_BPO1", "21_BPO2", "25_Amsterdam",
        ];
        foreach (string fork in forks)
            sources.Add(Source($"src/Nethermind/Nethermind.Specs/Forks/{fork}.cs", "Amsterdam fork lineage"));
        return [.. sources];
    }

    private static MemberAdmissionDescriptor[] BuildAdmissions() =>
    [
        Member("src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs", "Nethermind.Init.Modules", "BlockProcessingModule", "method", "Load", 0, "ContainerBuilder"),
        Member("src/Nethermind/Nethermind.Core/IJournal.cs", "Nethermind.Core", "IJournal`1", "method", "TakeSnapshot", 0, ""),
        Member("src/Nethermind/Nethermind.Core/IJournal.cs", "Nethermind.Core", "IJournal`1", "method", "Restore", 0, "TSnapshot"),
        Member("src/Nethermind/Nethermind.Core/Collections/JournalCollection.cs", "Nethermind.Core.Collections", "JournalCollection`1", "method", "TakeSnapshot", 0, ""),
        Member("src/Nethermind/Nethermind.Core/Collections/JournalCollection.cs", "Nethermind.Core.Collections", "JournalCollection`1", "method", "Restore", 0, "int"),
        Member("src/Nethermind/Nethermind.Core/Collections/JournalSet.cs", "Nethermind.Core.Collections", "JournalSet`1", "method", "TakeSnapshot", 0, ""),
        Member("src/Nethermind/Nethermind.Core/Collections/JournalSet.cs", "Nethermind.Core.Collections", "JournalSet`1", "method", "Restore", 0, "int"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "EthereumVirtualMachine", "type", "EthereumVirtualMachine", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "field", "_currentState", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "field", "_stateStack", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "field", "_txTracer", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "field", "_shouldRestoreRipemdTouch", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "field", "_isCancelableCached", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "field", "_returnDataBuffer", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "field", "_previousCallResult", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "field", "_previousCallOutputDestination", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "property", "ReturnDataBuffer", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "property", "IsTracingActions", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "ExecuteTransaction", 1, "VmState<TGasPolicy>,IWorldState,ITxTracer"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachineStatics", "method", "RestoreRipemdTouch", 0, "IWorldState,IReleaseSpec,bool"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "DisposeActiveFrames", 0, "VmState<TGasPolicy>"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1.FrameCleanupScope", "method", "Dispose", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "PrepareNextCallFrame", 0, "inCallResult,refnuint"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "HandleException", 0, "scopedinCallResult,scopedrefnuint,outbool"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "HandleFailure", 1, "Exception,string?,scopedrefnuint,outbool"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "HandleRegularReturn", 1, "scopedinCallResult,VmState<TGasPolicy>"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "HandleCreate", 0, "inCallResult,VmState<TGasPolicy>,ulong,refbool"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "HandleRevert", 0, "VmState<TGasPolicy>,inCallResult,refnuint"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "PrepareCreateData", 0, "VmState<TGasPolicy>,refnuint"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "PrepareTopLevelSubstate", 0, "scopedinCallResult"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "TraceTransactionActionStart", 0, "VmState<TGasPolicy>"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "TraceTransactionActionEnd", 0, "VmState<TGasPolicy>,inCallResult"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "AddTransferLog", 0, "VmState<TGasPolicy>"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "TryChargeAndDepositCode", 0, "VmState<TGasPolicy>,ulong,refbool,ulong,long,bool,ReadOnlyMemory<byte>"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "PopAndRestoreParentState", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "RefundRevertedTopLevelStateGas", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "CreditStateGasRefund", 1, "refTGasPolicy,long,bool"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "IncorporateChildStateGasRefunds", 0, "VmState<TGasPolicy>"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "RemoveAdvancedStateGasRefund", 0, "VmState<TGasPolicy>,refTGasPolicy"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "ExecutePrecompile", 0, "VmState<TGasPolicy>,bool,outException?,outstring?"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "RunPrecompile", 1, "VmState<TGasPolicy>"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "ExecutePrecompileCall", 0, "VmState<TGasPolicy>,IPrecompile,ReadOnlyMemory<byte>,IReleaseSpec"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "ExecuteCall", 1, "in(Address?CreatedAddress,bool?Success),nuint,scopedinUInt256"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "RunByteCode", 2, "scopedrefEvmStack,scopedrefTGasPolicy"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "RunDispatchLoop", 2, "scopedrefEvmStack,scopedrefTGasPolicy,refnint"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "ExecuteOpcode", 4, "refEvmStack,refTGasPolicy,refDispatchState,nint,int"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "ExecuteJumpIfOpcode", 2, "refEvmStack,refTGasPolicy,refDispatchState,nint,int"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs", "Nethermind.Evm", "VirtualMachine`1", "field", "_opcodeHandlers", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "GetOpcodeHandlers", 2, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "PrepareOpcodes", 1, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "PrepareOpcodes", 2, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs", "Nethermind.Evm", "VirtualMachine`1.OpcodeTable", "type", "OpcodeTable", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs", "Nethermind.Evm", "VirtualMachine`1.OpcodeTable", "field", "NoTrace", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs", "Nethermind.Evm", "VirtualMachine`1.OpcodeTable", "field", "NoTraceCancelable", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs", "Nethermind.Evm", "VirtualMachine`1.OpcodeTable", "field", "Traced", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs", "Nethermind.Evm", "VirtualMachine`1.OpcodeTable", "field", "TracedCancelable", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs", "Nethermind.Evm", "VirtualMachine`1.OpcodeTable", "method", "GetExecutionHandlers", 0, "IReleaseSpec"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs", "Nethermind.Evm", "VirtualMachine`1.OpcodeTable", "method", "GetHandlers", 2, "IReleaseSpec"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs", "Nethermind.Evm", "VirtualMachine`1.OpcodeTable", "method", "RefreshNonTraced", 0, "IReleaseSpec"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "GenerateOpcodeHandlers", 2, "IReleaseSpec"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.std.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "GetOpcodeTable", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.std.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "ShouldRefreshOpcodes", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.std.cs", "Nethermind.Evm", "VirtualMachine`1", "property", "ReturnData", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs", "Nethermind.Evm", "DispatchFlags", "type", "DispatchFlags", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs", "Nethermind.Evm", "DispatchFlags", "method", "Tracing", 0, "bool"),
        Member("src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs", "Nethermind.Evm", "DispatchFlags", "method", "Cancelable", 0, "bool"),
        Member("src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs", "Nethermind.Evm", "DispatchFlags", "method", "Validate", 0, "ITxTracer"),
        Member("src/Nethermind/Nethermind.Evm/SpecFlags.std.cs", "Nethermind.Evm", "SpecFlags", "type", "SpecFlags", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/Instruction.cs", "Nethermind.Evm", "Instruction", "type", "Instruction", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.ExecutionHandlers.cs", "Nethermind.Evm", "VirtualMachine`1", "field", "_executionHandlers", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.ExecutionHandlers.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "GetExecutionHandlers", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.ExecutionHandlers.cs", "Nethermind.Evm", "VirtualMachine`1.ExecutionHandlers", "type", "ExecutionHandlers", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.ExecutionHandlers.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "InitializeFrameCore", 1, "VirtualMachine<TGasPolicy>,VmState<TGasPolicy>"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.ExecutionHandlers.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "RunPrecompileCore", 1, "VirtualMachine<TGasPolicy>,VmState<TGasPolicy>"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.ExecutionHandlers.cs", "Nethermind.Evm", "VirtualMachine`1", "method", "CreditStateGasRefundCore", 1, "VirtualMachine<TGasPolicy>,refTGasPolicy,long,bool"),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.CallResult.cs", "Nethermind.Evm", "VirtualMachine`1.CallResult", "type", "CallResult", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.CallResult.cs", "Nethermind.Evm", "VirtualMachine`1.CallResult", "property", "StateToExecute", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.CallResult.cs", "Nethermind.Evm", "VirtualMachine`1.CallResult", "property", "Output", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.CallResult.cs", "Nethermind.Evm", "VirtualMachine`1.CallResult", "property", "ExceptionType", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.CallResult.cs", "Nethermind.Evm", "VirtualMachine`1.CallResult", "property", "ShouldRevert", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.CallResult.cs", "Nethermind.Evm", "VirtualMachine`1.CallResult", "property", "PrecompileSuccess", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.CallResult.cs", "Nethermind.Evm", "VirtualMachine`1.CallResult", "property", "IsReturn", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.CallResult.cs", "Nethermind.Evm", "VirtualMachine`1.CallResult", "property", "IsException", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VirtualMachine.CallResult.cs", "Nethermind.Evm", "VirtualMachine`1.CallResult", "property", "SubstateError", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/TransactionSubstate.cs", "Nethermind.Evm", "TransactionSubstate", "type", "TransactionSubstate", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/TransactionSubstate.cs", "Nethermind.Evm", "TransactionSubstate", "property", "IsError", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/TransactionSubstate.cs", "Nethermind.Evm", "TransactionSubstate", "property", "EvmExceptionType", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/TransactionSubstate.cs", "Nethermind.Evm", "TransactionSubstate", "property", "Output", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/TransactionSubstate.cs", "Nethermind.Evm", "TransactionSubstate", "property", "ShouldRevert", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/TransactionSubstate.cs", "Nethermind.Evm", "TransactionSubstate", "property", "Refund", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/TransactionSubstate.cs", "Nethermind.Evm", "TransactionSubstate", "property", "Logs", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/TransactionSubstate.cs", "Nethermind.Evm", "TransactionSubstate", "property", "DestroyList", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/TransactionSubstate.cs", "Nethermind.Evm", "TransactionSubstate", "property", "ShouldRestoreRipemdTouch", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "Nethermind.Evm.TransactionProcessing", "TransactionProcessorBase`1", "method", "ExecuteEvmCall", 1, "Transaction,BlockHeader,IReleaseSpec,ITxTracer,ExecutionOptions,long,IntrinsicGas<TGasPolicy>,long,inStackAccessTracker,TGasPolicy,ExecutionEnvironment,bool,outTransactionSubstate,outGasConsumed"),
        Member("src/Nethermind/Nethermind.Evm/Tracing/ITxTracer.cs", "Nethermind.Evm.Tracing", "ITxTracer", "property", "IsCancelable", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/Tracing/ITxTracer.cs", "Nethermind.Evm.Tracing", "ITxTracer", "property", "IsTracingActions", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/Tracing/ITxTracer.cs", "Nethermind.Evm.Tracing", "ITxTracer", "property", "IsTracingInstructions", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/Tracing/ITxTracer.cs", "Nethermind.Evm.Tracing", "ITxTracer", "method", "ReportOperationRemainingGas", 0, "ulong"),
        Member("src/Nethermind/Nethermind.Evm/Tracing/ITxTracer.cs", "Nethermind.Evm.Tracing", "ITxTracer", "method", "ReportOperationError", 0, "EvmExceptionType"),
        Member("src/Nethermind/Nethermind.Evm/Tracing/ITxTracer.cs", "Nethermind.Evm.Tracing", "ITxTracer", "method", "ReportActionError", 0, "EvmExceptionType"),
        Member("src/Nethermind/Nethermind.Evm/EvmException.cs", "Nethermind.Evm", "EvmExceptionType", "type", "EvmExceptionType", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/ExecutionType.cs", "Nethermind.Evm", "ExecutionType", "type", "ExecutionType", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/ExecutionType.cs", "Nethermind.Evm", "ExecutionTypeExtensions", "method", "IsAnyCreate", 0, "thisExecutionType"),
        Member("src/Nethermind/Nethermind.Evm/VmState.cs", "Nethermind.Evm", "VmState`1", "type", "VmState", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VmState.cs", "Nethermind.Evm", "VmState`1", "field", "Gas", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VmState.cs", "Nethermind.Evm", "VmState`1", "field", "InitialStateGasUsed", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VmState.cs", "Nethermind.Evm", "VmState`1", "field", "StateGasRefundAdvanced", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VmState.cs", "Nethermind.Evm", "VmState`1", "property", "OutputDestination", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VmState.cs", "Nethermind.Evm", "VmState`1", "property", "OutputLength", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VmState.cs", "Nethermind.Evm", "VmState`1", "property", "Refund", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VmState.cs", "Nethermind.Evm", "VmState`1", "property", "ExecutionType", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VmState.cs", "Nethermind.Evm", "VmState`1", "property", "IsTopLevel", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VmState.cs", "Nethermind.Evm", "VmState`1", "property", "IsContinuation", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VmState.cs", "Nethermind.Evm", "VmState`1", "property", "IsCreateOnPreExistingAccount", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VmState.cs", "Nethermind.Evm", "VmState`1", "property", "IsCreateStateGasCharged", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VmState.cs", "Nethermind.Evm", "VmState`1", "property", "NewAccountCharged", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VmState.cs", "Nethermind.Evm", "VmState`1", "property", "Snapshot", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VmState.cs", "Nethermind.Evm", "VmState`1", "method", "Dispose", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/VmState.cs", "Nethermind.Evm", "VmState`1", "method", "CommitToParent", 0, "VmState<TGasPolicy>"),
        Member("src/Nethermind/Nethermind.Evm/VmState.cs", "Nethermind.Evm", "VmState`1", "method", "RestoreStack", 1, "ITxTracer,ReadOnlySpan<byte>,outEvmStack"),
        Member("src/Nethermind/Nethermind.Evm/VmStateStack.cs", "Nethermind.Evm", "VmStateStack`1", "method", "Push", 0, "VmState<TGasPolicy>"),
        Member("src/Nethermind/Nethermind.Evm/VmStateStack.cs", "Nethermind.Evm", "VmStateStack`1", "method", "Pop", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/StackAccessTracker.cs", "Nethermind.Evm", "StackAccessTracker", "method", "TakeSnapshot", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/StackAccessTracker.cs", "Nethermind.Evm", "StackAccessTracker", "method", "Restore", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "Nethermind.Evm.GasPolicy", "EthereumGasPolicy", "method", "Refund", 0, "refEthereumGasPolicy,inEthereumGasPolicy"),
        Member("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "Nethermind.Evm.GasPolicy", "EthereumGasPolicy", "method", "RepayStateGasSpill", 0, "refEthereumGasPolicy"),
        Member("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "Nethermind.Evm.GasPolicy", "EthereumGasPolicy", "method", "RestoreChildStateGas", 0, "refEthereumGasPolicy,inEthereumGasPolicy"),
        Member("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "Nethermind.Evm.GasPolicy", "EthereumGasPolicy", "method", "RestoreChildStateGasOnHalt", 0, "refEthereumGasPolicy,inEthereumGasPolicy"),
        Member("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "Nethermind.Evm.GasPolicy", "EthereumGasPolicy", "method", "RevertRefundToHalt", 0, "refEthereumGasPolicy,inEthereumGasPolicy"),
        Member("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "Nethermind.Evm.GasPolicy", "EthereumGasPolicy", "method", "RefundStateGas", 0, "refEthereumGasPolicy,long,long,bool"),
        Member("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "Nethermind.Evm.GasPolicy", "EthereumGasPolicy", "method", "DiscardStateGas", 0, "refEthereumGasPolicy,long,long"),
        Member("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "Nethermind.Evm.GasPolicy", "EthereumGasPolicy", "method", "AddStateGasRefundToReservoir", 0, "refEthereumGasPolicy,long,bool"),
        Member("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "Nethermind.Evm.GasPolicy", "EthereumGasPolicy", "method", "RemoveStateGasRefundFromReservoir", 0, "refEthereumGasPolicy,long"),
        Member("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "Nethermind.Evm.GasPolicy", "EthereumGasPolicy", "method", "TryConsumeStateAndExecutionGas", 0, "refEthereumGasPolicy,long,ulong"),
        Member("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "Nethermind.Evm.GasPolicy", "EthereumGasPolicy", "method", "CalculateStateGasSpill", 0, "inEthereumGasPolicy,long"),
        Member("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "Nethermind.Evm.GasPolicy", "EthereumGasPolicy", "method", "GetRemainingGas", 0, "inEthereumGasPolicy"),
        Member("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "Nethermind.Evm.GasPolicy", "EthereumGasPolicy", "method", "UpdateGas", 0, "refEthereumGasPolicy,ulong"),
        Member("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "Nethermind.Evm.GasPolicy", "EthereumGasPolicy", "method", "UpdateGasUp", 0, "refEthereumGasPolicy,ulong"),
        Member("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "Nethermind.Evm.GasPolicy", "EthereumGasPolicy", "method", "ClearExecutionGas", 0, "refEthereumGasPolicy"),
        Member("src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs", "Nethermind.Evm.GasPolicy", "IGasPolicy`1", "method", "GetCreateStateCost", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs", "Nethermind.Evm.GasPolicy", "IGasPolicy`1", "method", "GetNewAccountStateCost", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/GasPolicy/StateGasChargeKernel.cs", "Nethermind.Evm.GasPolicy", "StateGasChargeKernel", "type", "StateGasChargeKernel", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs", "Nethermind.Evm.GasPolicy", "StateGasTransitionKernel", "type", "StateGasTransitionKernel", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionAdapterKernel.cs", "Nethermind.Evm.GasPolicy", "StateGasTransitionAdapterKernel", "type", "StateGasTransitionAdapterKernel", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/CodeDepositHandler.cs", "Nethermind.Evm", "CodeDepositHandler", "method", "CalculateCost", 1, "IReleaseSpec,int,inTGasPolicy,outulong,outlong"),
        Member("src/Nethermind/Nethermind.Evm/CodeDepositHandler.cs", "Nethermind.Evm", "CodeDepositHandler", "method", "CodeIsInvalid", 0, "IReleaseSpec,ReadOnlyMemory<byte>"),
        Member("src/Nethermind/Nethermind.Evm/ICodeInfoRepository.cs", "Nethermind.Evm", "ICodeInfoRepository", "method", "InsertCode", 0, "ReadOnlyMemory<byte>,Address,IReleaseSpec"),
        Member("src/Nethermind/Nethermind.Evm/CodeInfoRepository.cs", "Nethermind.Evm", "CodeInfoRepository", "method", "InsertCode", 0, "ReadOnlyMemory<byte>,Address,IReleaseSpec"),
        Member("src/Nethermind/Nethermind.Evm/CodeInfoRepository.cs", "Nethermind.Evm", "CodeInfoRepository", "method", "InsertCode", 0, "IWorldState,ReadOnlyMemory<byte>,Address,IReleaseSpec,outValueHash256"),
        Member("src/Nethermind/Nethermind.Evm/CacheCodeInfoRepository.cs", "Nethermind.Evm", "CacheCodeInfoRepository", "method", "InsertCode", 0, "ReadOnlyMemory<byte>,Address,IReleaseSpec"),
        Member("src/Nethermind/Nethermind.Evm/State/IWorldState.cs", "Nethermind.Evm.State", "IWorldState", "type", "IWorldState", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/State/IWorldState.cs", "Nethermind.Evm.State", "IWorldState", "method", "DeleteAccount", 0, "Address"),
        Member("src/Nethermind/Nethermind.Evm/State/IWorldState.cs", "Nethermind.Evm.State", "IWorldState", "method", "InsertCode", 0, "Address,inValueHash256,ReadOnlyMemory<byte>,IReleaseSpec,bool"),
        Member("src/Nethermind/Nethermind.Blockchain/EthereumPrecompileProvider.cs", "Nethermind.Blockchain", "EthereumPrecompileProvider", "property", "Precompiles", 0, ""),
        Member("src/Nethermind/Nethermind.Blockchain/EthereumPrecompileProvider.cs", "Nethermind.Blockchain", "EthereumPrecompileProvider", "method", "GetPrecompiles", 0, ""),
        Member("src/Nethermind/Nethermind.Evm/Precompiles/IPrecompile.cs", "Nethermind.Evm.Precompiles", "IPrecompile", "method", "Run", 0, "ReadOnlyMemory<byte>,IReleaseSpec"),
        Member("src/Nethermind/Nethermind.Core/Address.cs", "Nethermind.Core", "Address", "type", "Address", 0, ""),
        Member("src/Nethermind/Nethermind.Core/Address.cs", "Nethermind.Core", "Address", "method", "CouldBePrecompile", 0, ""),
        Member("src/Nethermind/Nethermind.Core/Address.cs", "Nethermind.Core", "Address", "method", "PrecompileIndexOrNegative", 0, ""),
        Member("src/Nethermind/Nethermind.Core/Precompiles/PrecompiledAddresses.cs", "Nethermind.Core.Precompiles", "PrecompiledAddresses", "type", "PrecompiledAddresses", 0, ""),
        Member("src/Nethermind/Nethermind.Specs/ReleaseSpec.cs", "Nethermind.Specs", "ReleaseSpec", "type", "ReleaseSpec", 0, ""),
        Member("src/Nethermind/Nethermind.Specs/ReleaseSpec.cs", "Nethermind.Specs", "ReleaseSpec", "method", "BuildPrecompilesCache", 0, ""),
        Member("src/Nethermind/Nethermind.Specs/ReleaseSpec.cs", "Nethermind.Specs", "ReleaseSpec", "method", "IsPrecompile", 0, "Address"),
        Member("src/Nethermind/Nethermind.Evm.Precompiles/ECRecoverPrecompile.cs", "Nethermind.Evm.Precompiles", "ECRecoverPrecompile", "type", "ECRecoverPrecompile", 0, ""),
        Member("src/Nethermind/Nethermind.Evm.Precompiles/Sha256Precompile.cs", "Nethermind.Evm.Precompiles", "Sha256Precompile", "type", "Sha256Precompile", 0, ""),
        Member("src/Nethermind/Nethermind.Evm.Precompiles/Ripemd160Precompile.cs", "Nethermind.Evm.Precompiles", "Ripemd160Precompile", "type", "Ripemd160Precompile", 0, ""),
        Member("src/Nethermind/Nethermind.Evm.Precompiles/IdentityPrecompile.cs", "Nethermind.Evm.Precompiles", "IdentityPrecompile", "type", "IdentityPrecompile", 0, ""),
        Member("src/Nethermind/Nethermind.Evm.Precompiles/ModExpPrecompile.cs", "Nethermind.Evm.Precompiles", "ModExpPrecompile", "type", "ModExpPrecompile", 0, ""),
        Member("src/Nethermind/Nethermind.Evm.Precompiles/BN254AddPrecompile.cs", "Nethermind.Evm.Precompiles", "BN254AddPrecompile", "type", "BN254AddPrecompile", 0, ""),
        Member("src/Nethermind/Nethermind.Evm.Precompiles/BN254MulPrecompile.cs", "Nethermind.Evm.Precompiles", "BN254MulPrecompile", "type", "BN254MulPrecompile", 0, ""),
        Member("src/Nethermind/Nethermind.Evm.Precompiles/BN254PairingCheckPrecompile.cs", "Nethermind.Evm.Precompiles", "BN254PairingCheckPrecompile", "type", "BN254PairingCheckPrecompile", 0, ""),
        Member("src/Nethermind/Nethermind.Evm.Precompiles/Blake2FPrecompile.cs", "Nethermind.Evm.Precompiles", "Blake2FPrecompile", "type", "Blake2FPrecompile", 0, ""),
        Member("src/Nethermind/Nethermind.Evm.Precompiles/KzgPointEvaluationPrecompile.cs", "Nethermind.Evm.Precompiles", "KzgPointEvaluationPrecompile", "type", "KzgPointEvaluationPrecompile", 0, ""),
        Member("src/Nethermind/Nethermind.Evm.Precompiles/Bls12381G1AddPrecompile.cs", "Nethermind.Evm.Precompiles", "Bls12381G1AddPrecompile", "type", "Bls12381G1AddPrecompile", 0, ""),
        Member("src/Nethermind/Nethermind.Evm.Precompiles/Bls12381G1MsmPrecompile.cs", "Nethermind.Evm.Precompiles", "Bls12381G1MsmPrecompile", "type", "Bls12381G1MsmPrecompile", 0, ""),
        Member("src/Nethermind/Nethermind.Evm.Precompiles/Bls12381G2AddPrecompile.cs", "Nethermind.Evm.Precompiles", "Bls12381G2AddPrecompile", "type", "Bls12381G2AddPrecompile", 0, ""),
        Member("src/Nethermind/Nethermind.Evm.Precompiles/Bls12381G2MsmPrecompile.cs", "Nethermind.Evm.Precompiles", "Bls12381G2MsmPrecompile", "type", "Bls12381G2MsmPrecompile", 0, ""),
        Member("src/Nethermind/Nethermind.Evm.Precompiles/Bls12381PairingCheckPrecompile.cs", "Nethermind.Evm.Precompiles", "Bls12381PairingCheckPrecompile", "type", "Bls12381PairingCheckPrecompile", 0, ""),
        Member("src/Nethermind/Nethermind.Evm.Precompiles/Bls12381FpToG1Precompile.cs", "Nethermind.Evm.Precompiles", "Bls12381FpToG1Precompile", "type", "Bls12381FpToG1Precompile", 0, ""),
        Member("src/Nethermind/Nethermind.Evm.Precompiles/Bls12381Fp2ToG2Precompile.cs", "Nethermind.Evm.Precompiles", "Bls12381Fp2ToG2Precompile", "type", "Bls12381Fp2ToG2Precompile", 0, ""),
        Member("src/Nethermind/Nethermind.Evm.Precompiles/SecP256r1Precompile.cs", "Nethermind.Evm.Precompiles", "SecP256r1Precompile", "type", "SecP256r1Precompile", 0, ""),
    ];

    private static OpcodePackageDescriptor[] BuildOpcodePackages() =>
    [
        Package("PureWordOpcodeExtractor", 26),
        Package("Keccak256OpcodeExtractor", 1),
        Package("EnvironmentOpcodeExtractor", 20),
        Package("AccountReadOpcodeExtractor", 4),
        Package("MemoryCopyOpcodeExtractor", 9),
        Package("PersistentStorageOpcodeExtractor", 2),
        Package("TransientStorageOpcodeExtractor", 2),
        Package("StackRearrangementOpcodeExtractor", 33),
        Package("PushOpcodeExtractor", 33),
        Package("LogOpcodeExtractor", 5),
        Package("CallCreateOpcodeExtractor", 7),
        Package("ControlFlowOpcodeExtractor", 8),
        Package("ExtendedStackOpcodeExtractor", 3),
        Package("CallDataLoadOpcodeExtractor", 1),
    ];

    private static OpcodeRouteDescriptor[] BuildPendingOpcodeRoutes()
    {
        OpcodeRouteDescriptor[] routes = new OpcodeRouteDescriptor[1024];
        for (int tableIndex = 0; tableIndex < DispatchTableNames.Length; tableIndex++)
            for (int value = 0; value < 256; value++)
                routes[tableIndex * 256 + value] = new(
                    DispatchTableNames[tableIndex], value, Unresolved, Unresolved, Unresolved, Unresolved, Unresolved, false);
        return routes;
    }

    private static PrecompileRouteDescriptor[] BuildPrecompileRoutes() =>
    [
        Precompile("ECRECOVER", 0x01, "Amsterdam.IsPrecompile(address)"),
        Precompile("SHA256", 0x02, "Amsterdam.IsPrecompile(address)"),
        Precompile("RIPEMD160", 0x03, "Amsterdam.IsPrecompile(address)"),
        Precompile("IDENTITY", 0x04, "Amsterdam.IsPrecompile(address)"),
        Precompile("MODEXP", 0x05, "Amsterdam.IsPrecompile(address)"),
        Precompile("BN254_ADD", 0x06, "Amsterdam.IsPrecompile(address)"),
        Precompile("BN254_MUL", 0x07, "Amsterdam.IsPrecompile(address)"),
        Precompile("BN254_PAIRING", 0x08, "Amsterdam.IsPrecompile(address)"),
        Precompile("BLAKE2F", 0x09, "Amsterdam.IsPrecompile(address)"),
        Precompile("KZG_POINT_EVALUATION", 0x0a, "Amsterdam.IsPrecompile(address)"),
        Precompile("BLS12_G1ADD", 0x0b, "Amsterdam.IsPrecompile(address)"),
        Precompile("BLS12_G1MSM", 0x0c, "Amsterdam.IsPrecompile(address)"),
        Precompile("BLS12_G2ADD", 0x0d, "Amsterdam.IsPrecompile(address)"),
        Precompile("BLS12_G2MSM", 0x0e, "Amsterdam.IsPrecompile(address)"),
        Precompile("BLS12_PAIRING", 0x0f, "Amsterdam.IsPrecompile(address)"),
        Precompile("BLS12_MAP_FP_TO_G1", 0x10, "Amsterdam.IsPrecompile(address)"),
        Precompile("BLS12_MAP_FP2_TO_G2", 0x11, "Amsterdam.IsPrecompile(address)"),
        Precompile("P256VERIFY", 0x100, "Amsterdam.IsPrecompile(address)"),
    ];

    private static ProductionSourceDescriptor Source(string path, string role) => new(path, role, false, null, null);

    private static ProductionSourceDescriptor Raw(string path, string role) => new(path, role, true, null, null);

    private static MemberAdmissionDescriptor Member(string path, string ns, string owner, string kind, string name,
        int arity, string parameters) => new(path, ns, owner, kind, name, arity, parameters, null, null);

    private static OpcodePackageDescriptor Package(string name, int count) => new(
        name,
        $"tools/Evm/Lean/{name}/Generated/{name.Replace("Extractor", "Kernel", StringComparison.Ordinal)}.source-manifest.json",
        Unresolved,
        $"{Unresolved}/{name}/proof.lean",
        [],
        null,
        count,
        false,
        null);

    private static PrecompileRouteDescriptor Precompile(string name, int address, string activation) => new(
        name,
        address,
        activation,
        "Nethermind.Blockchain.EthereumPrecompileProvider.Precompiles",
        $"{Unresolved}/precompile/{name}.source-manifest.json",
        Unresolved,
        null,
        false,
        null);

    private static string AdmissionKey(MemberAdmissionDescriptor admission) =>
        $"{admission.SourcePath}|{admission.Namespace}|{admission.OwnerPath}|{admission.MemberKind}|{admission.MemberName}|{admission.MemberGenericArity}|{admission.ParameterTypes}";

    private static bool PathsContain(ProductionSourceDescriptor[] sources, string path)
    {
        foreach (ProductionSourceDescriptor source in sources)
            if (source.Path == path) return true;
        return false;
    }

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64) return false;
        foreach (char character in value)
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) return false;
        return true;
    }

    private static bool IsNonEmpty(string? value) => !string.IsNullOrWhiteSpace(value);

    private static bool IsSafeRelativePath(string path) =>
        IsNonEmpty(path) && !path.Contains('\\') && !Path.IsPathRooted(path) && !path.Split('/').Contains("..");

    private static void ValidateBoundary(FrameBoundaryDescriptor boundary)
    {
        if (boundary.TransactionRoot != TransactionRoot || boundary.ClosedGasPolicy != ClosedGasPolicy ||
            boundary.Fork != ClosedFork || boundary.DispatchTables is null ||
            boundary.FramePhases is null || boundary.ChildExitKinds is null || boundary.TopLevelExitKinds is null ||
            !boundary.DispatchTables.SequenceEqual(DispatchTableNames, StringComparer.Ordinal) ||
            !boundary.FramePhases.SequenceEqual(FramePhaseNames, StringComparer.Ordinal) ||
            !boundary.ChildExitKinds.SequenceEqual(FrameExitNames, StringComparer.Ordinal) ||
            !boundary.TopLevelExitKinds.SequenceEqual(FrameExitNames, StringComparer.Ordinal))
            throw new ExtractionException("Frame-machine boundary is not the closed standard Amsterdam root.");
    }

    private static void ValidateFrameDriver(FrameDriverDescriptor driver)
    {
        if (driver.DispatchOrder is null || driver.FreshFrameEntryOrder is null || driver.ChildExitBindings is null ||
            driver.TopLevelCloseOrder is null || driver.SettlementDiscriminants is null ||
            driver.TransactionWideFields is null || driver.FailureOrigins is null ||
            driver.SuccessSettlementOrder is null || driver.RevertSettlementOrder is null ||
            driver.HandleExceptionOrder is null || driver.HandleFailureOrder is null ||
            driver.NestedPrecompileFailureOrder is null || driver.DirectPrecompileFailureOrder is null ||
            driver.CodeDepositFailureOrder is null || driver.ParentDiscipline != "lifo")
            throw new ExtractionException("Frame-machine driver descriptor is incomplete.");
        ValidateExactSequence(driver.DispatchOrder, DispatchOrder, "dispatch order");
        ValidateExactSequence(driver.FreshFrameEntryOrder, FreshFrameEntryOrder, "fresh-frame entry order");
        ValidateExactSequence(driver.TopLevelCloseOrder, TopLevelCloseOrder, "top-level close order");
        ValidateExactSequence(driver.SettlementDiscriminants, SettlementDiscriminants, "settlement discriminants");
        ValidateExactSequence(driver.TransactionWideFields, TransactionWideFields, "transaction-wide fields");
        ValidateExactSequence(driver.FailureOrigins, FailureOriginNames, "failure origins");
        ValidateExactSequence(driver.SuccessSettlementOrder, SuccessSettlementOrder, "success settlement order");
        ValidateExactSequence(driver.RevertSettlementOrder, RevertSettlementOrder, "revert settlement order");
        ValidateExactSequence(driver.HandleExceptionOrder, HandleExceptionOrder, "HandleException order");
        ValidateExactSequence(driver.HandleFailureOrder, HandleFailureOrder, "HandleFailure order");
        ValidateExactSequence(driver.NestedPrecompileFailureOrder, NestedPrecompileFailureOrder, "nested precompile soft-failure order");
        ValidateExactSequence(driver.DirectPrecompileFailureOrder, DirectPrecompileFailureOrder, "direct precompile soft-failure order");
        ValidateExactSequence(driver.CodeDepositFailureOrder, CodeDepositFailureOrder, "code-deposit failure order");
        if (driver.ChildExitBindings.Length != FrameExitNames.Length || driver.ChildExitBindings.Any(static binding => binding is null) ||
            !driver.ChildExitBindings.Select(static binding => binding.ExitKind).SequenceEqual(FrameExitNames, StringComparer.Ordinal) ||
            !driver.ChildExitBindings.Select(static binding => binding.MergeOperation).SequenceEqual(FrameMergeNames, StringComparer.Ordinal))
            throw new ExtractionException("Frame-machine driver must bind each closed child exit exactly once and in production order.");
        foreach (FrameExitBindingDescriptor binding in driver.ChildExitBindings)
        {
            _ = LeanExitKind(binding.ExitKind);
            _ = LeanMergeOperation(binding.MergeOperation);
        }
    }

    internal static string LeanExitKind(string exitKind) => exitKind switch
    {
        "success" => ".success",
        "revert" => ".revert",
        "exception" => ".exception _",
        _ => throw new ExtractionException($"Unknown child exit kind {exitKind}."),
    };

    internal static string LeanMergeOperation(string operation) => operation switch
    {
        "mergeSuccess" => "mergeSuccess",
        "mergeRevert" => "mergeRevert",
        "mergeException" => "mergeException",
        _ => throw new ExtractionException($"Unknown child merge operation {operation}."),
    };

    private static void ValidateStrings(string[] values, string description, bool requireNonEmpty)
    {
        if ((requireNonEmpty && values.Length == 0) || values.Any(static value => string.IsNullOrWhiteSpace(value)) ||
            values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new ExtractionException($"Frame-machine {description} must be non-empty and unique.");
    }

    private static void ValidateExactSequence(string[] actual, string[] expected, string description)
    {
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
            throw new ExtractionException($"Frame-machine {description} does not match the closed production vocabulary and order.");
    }

    private static void RejectDuplicateProperties(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        RejectDuplicateProperties(document.RootElement, "$", new HashSet<string>(StringComparer.Ordinal));
    }

    private static void RejectDuplicateProperties(JsonElement element, string path, HashSet<string> scratch)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            scratch.Clear();
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!scratch.Add(property.Name))
                    throw new ExtractionException($"Duplicate frame-machine IR property at {path}.{property.Name}.");
            }

            foreach (JsonProperty property in element.EnumerateObject())
                RejectDuplicateProperties(property.Value, $"{path}.{property.Name}", new HashSet<string>(StringComparer.Ordinal));
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item, $"{path}[{index}]", new HashSet<string>(StringComparer.Ordinal));
                index++;
            }
        }
    }
}
