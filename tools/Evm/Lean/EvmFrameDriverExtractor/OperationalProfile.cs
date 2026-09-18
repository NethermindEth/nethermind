// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Evm.Lean.EvmFrameDriverExtractor;

/// <summary>Builds the separate Stage F source-derived finite-fuel frame-driver package.</summary>
internal static class OperationalProfile
{
    internal const string IrFileName = "EvmFrameDriverOperationalKernel.ir.json";
    internal const string ManifestFileName = "EvmFrameDriverOperationalKernel.source-manifest.json";
    internal const string LeanFileName = "EvmFrameDriverOperationalKernel.lean";
    internal const string ExtractorVersion = "1.0.0-stage-f-source-derived-operational";
    internal const string KernelName = "standard-mainnet-amsterdam-evm-frame-driver-operational-stage-f";
    internal const string AcceptanceState = "stage-f-source-derived-finite-fuel-operational-refinement";
    internal const string StageEBoundary = "stage-e-source-attached-parametric-shell";
    internal const string FrameMachineManifestPath =
        "tools/Evm/Lean/EvmFrameMachineExtractor/Generated/EvmFrameMachineKernel.source-manifest.json";
    internal const string FrameMachineManifestSha256 =
        "a80e85437f6e62ac696b478979a910322d7067ad04e127dabf2e2330270c0857";
    internal const string AcceptedStageEIrSha256 =
        "ba1a55b8cba1be2154290f22fec0928d7c5b26ec427886eeff5c72606e3315c4";
    // Filled from the canonical source-derived arrays by the release-lane generation step.
    internal const string AcceptedOperationalEvidenceSha256 =
        "abb81be9e3932a14b539935b6d9ea8da3f287e5392a1927006de82ba5a13f0d6";
    internal const int ExpectedOperationalSourceCount = 12;
    internal const int ExpectedOperationalMemberCount = 21;
    internal const int ExpectedOperationalBranchBindingCount = 28;
    private const string CompilerReferenceInventoryPath =
        "tools/Evm/Lean/OrdinaryPostNonceDispatchExtractor/COMPILER_REFERENCE_PINS.json";
    private const int CompilerReferenceInventorySchemaVersion = 1;
    private const int CompilerReferenceInventoryCount = 434;
    private const int SelectedCompilerReferenceCount = 329;
    private const int EmittedCompilerReferenceCount = SelectedCompilerReferenceCount + 1;
    private const string CompilerReferenceInventorySha256 =
        "268ba44f24f3a45ba314ff09377212496c0e8dca45a738e13c72378398d59cb4";
    private const string CompilerReferenceInventoryAggregateSha256 =
        "a563dcff8680800e3928451615182f7f0057f43f0488ce3aa3480cd13946bb9b";
    private static readonly string[] CompilerReferenceClosurePaths =
    [
        "src/Nethermind/artifacts/bin/Nethermind.Init/release",
        "tools/artifacts/bin/Evm/release",
        "src/Nethermind/artifacts/bin/Nethermind.Network.Enr.Test/release",
    ];
    private static readonly string[] RequiredCompilerAssemblyNames =
    [
        "Autofac", "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.ObjectPool",
        "Nethermind.Api", "Nethermind.Blockchain", "Nethermind.Config", "Nethermind.Consensus",
        "Nethermind.Core", "Nethermind.Crypto", "Nethermind.Db", "Nethermind.Evm",
        "Nethermind.Evm.Precompiles", "Nethermind.Facade", "Nethermind.Init", "Nethermind.Int256",
        "Nethermind.Logging", "Nethermind.Network", "Nethermind.Network.Contract", "Nethermind.Network.Enr",
        "Nethermind.Serialization.Json", "Nethermind.Serialization.Rlp", "Nethermind.Serialization.Ssz",
        "Nethermind.Specs", "Nethermind.State", "Nethermind.Trie", "Nethermind.TxPool",
    ];

    // Source branch records are decoded once into typed predicate/effect/
    // settlement identities.  This table is the finite semantic lowering from
    // that typed source AST to the executable action field; branch/profile
    // labels are deliberately not part of the lookup key.
    private static readonly IReadOnlyDictionary<(OperationalControlStageIdentity Stage,
        OperationalControlPredicateIdentity Predicate), OperationalControlActionIdentity>
        OperationalActionLowering =
        new Dictionary<(OperationalControlStageIdentity Stage, OperationalControlPredicateIdentity Predicate),
            OperationalControlActionIdentity>
        {
            [(OperationalControlStageIdentity.Prepare, OperationalControlPredicateIdentity.FreshFrame)] =
                OperationalControlActionIdentity.PrepareFresh,
            [(OperationalControlStageIdentity.Prepare, OperationalControlPredicateIdentity.ContinuationFrame)] =
                OperationalControlActionIdentity.PrepareContinuation,
            [(OperationalControlStageIdentity.Dispatch, OperationalControlPredicateIdentity.BytecodeFrame)] =
                OperationalControlActionIdentity.DispatchBytecode,
            [(OperationalControlStageIdentity.Dispatch, OperationalControlPredicateIdentity.FullPrecompileFrame)] =
                OperationalControlActionIdentity.DispatchFullPrecompile,
            [(OperationalControlStageIdentity.Dispatch, OperationalControlPredicateIdentity.PrecompileOutOfGasNested)] =
                OperationalControlActionIdentity.PrecompileFailure,
            [(OperationalControlStageIdentity.Dispatch, OperationalControlPredicateIdentity.PrecompileOutOfGasTop)] =
                OperationalControlActionIdentity.PrecompileFailure,
            [(OperationalControlStageIdentity.Dispatch, OperationalControlPredicateIdentity.PrecompileReturnedFailure)] =
                OperationalControlActionIdentity.PrecompileFailure,
            [(OperationalControlStageIdentity.Dispatch, OperationalControlPredicateIdentity.PrecompileManagedException)] =
                OperationalControlActionIdentity.PrecompileFailure,
            [(OperationalControlStageIdentity.Classify, OperationalControlPredicateIdentity.BytecodeContinue)] =
                OperationalControlActionIdentity.ClassifyContinue,
            [(OperationalControlStageIdentity.Classify, OperationalControlPredicateIdentity.BytecodeSuspend)] =
                OperationalControlActionIdentity.ClassifySuspend,
            [(OperationalControlStageIdentity.Classify, OperationalControlPredicateIdentity.NestedRegularSuccess)] =
                OperationalControlActionIdentity.ClassifyHalt,
            [(OperationalControlStageIdentity.Classify, OperationalControlPredicateIdentity.NestedCreateSuccess)] =
                OperationalControlActionIdentity.ClassifyHalt,
            [(OperationalControlStageIdentity.Classify, OperationalControlPredicateIdentity.NestedRevert)] =
                OperationalControlActionIdentity.ClassifyHalt,
            [(OperationalControlStageIdentity.Classify, OperationalControlPredicateIdentity.NestedException)] =
                OperationalControlActionIdentity.ClassifyHalt,
            [(OperationalControlStageIdentity.Classify, OperationalControlPredicateIdentity.TopLevelSuccess)] =
                OperationalControlActionIdentity.ClassifyHalt,
            [(OperationalControlStageIdentity.Classify, OperationalControlPredicateIdentity.TopLevelRevert)] =
                OperationalControlActionIdentity.ClassifyHalt,
            [(OperationalControlStageIdentity.Classify, OperationalControlPredicateIdentity.TopLevelException)] =
                OperationalControlActionIdentity.ClassifyHalt,
            [(OperationalControlStageIdentity.Settle, OperationalControlPredicateIdentity.NestedRegularSuccess)] =
                OperationalControlActionIdentity.SettleNestedRegularSuccess,
            [(OperationalControlStageIdentity.Settle, OperationalControlPredicateIdentity.NestedCreateSuccess)] =
                OperationalControlActionIdentity.SettleNestedCreateSuccess,
            [(OperationalControlStageIdentity.Settle, OperationalControlPredicateIdentity.CreateDepositInvalidCode)] =
                OperationalControlActionIdentity.SettleNestedCreateInvalidCode,
            [(OperationalControlStageIdentity.Settle, OperationalControlPredicateIdentity.CreateDepositOutOfGas)] =
                OperationalControlActionIdentity.SettleNestedCreateOutOfGas,
            [(OperationalControlStageIdentity.Settle, OperationalControlPredicateIdentity.NestedRevert)] =
                OperationalControlActionIdentity.SettleNestedRevert,
            [(OperationalControlStageIdentity.Settle, OperationalControlPredicateIdentity.NestedException)] =
                OperationalControlActionIdentity.SettleNestedException,
            [(OperationalControlStageIdentity.Settle, OperationalControlPredicateIdentity.ResumeParent)] =
                OperationalControlActionIdentity.SettleResume,
            [(OperationalControlStageIdentity.Settle, OperationalControlPredicateIdentity.TopLevelSuccess)] =
                OperationalControlActionIdentity.SettleTopLevelSuccess,
            [(OperationalControlStageIdentity.Settle, OperationalControlPredicateIdentity.TopLevelRevert)] =
                OperationalControlActionIdentity.SettleTopLevelRevert,
            [(OperationalControlStageIdentity.Settle, OperationalControlPredicateIdentity.TopLevelException)] =
                OperationalControlActionIdentity.SettleTopLevelException,
            [(OperationalControlStageIdentity.Cleanup, OperationalControlPredicateIdentity.Cancelled)] =
                OperationalControlActionIdentity.CleanupCancelled,
            [(OperationalControlStageIdentity.Cleanup, OperationalControlPredicateIdentity.Escaped)] =
                OperationalControlActionIdentity.CleanupEscaped,
            [(OperationalControlStageIdentity.Cleanup, OperationalControlPredicateIdentity.InvalidControl)] =
                OperationalControlActionIdentity.CleanupInvalidControl,
            [(OperationalControlStageIdentity.Cleanup, OperationalControlPredicateIdentity.Completed)] =
                OperationalControlActionIdentity.CleanupCompleted,
        };

    private static readonly IReadOnlyDictionary<string, OperationalControlPredicateIdentity>
        PredicateLowering = new Dictionary<string, OperationalControlPredicateIdentity>(StringComparer.Ordinal)
        {
            ["fresh"] = OperationalControlPredicateIdentity.FreshFrame,
            ["continuation"] = OperationalControlPredicateIdentity.ContinuationFrame,
            ["bytecode"] = OperationalControlPredicateIdentity.BytecodeFrame,
            ["fullPrecompile"] = OperationalControlPredicateIdentity.FullPrecompileFrame,
            ["returned/continue"] = OperationalControlPredicateIdentity.BytecodeContinue,
            ["returned/suspend"] = OperationalControlPredicateIdentity.BytecodeSuspend,
            ["halt/success/nested/call"] = OperationalControlPredicateIdentity.NestedRegularSuccess,
            ["halt/success/nested/create"] = OperationalControlPredicateIdentity.NestedCreateSuccess,
            ["createDeposit/invalid"] = OperationalControlPredicateIdentity.CreateDepositInvalidCode,
            ["createDeposit/outOfGas"] = OperationalControlPredicateIdentity.CreateDepositOutOfGas,
            ["halt/revert/nested"] = OperationalControlPredicateIdentity.NestedRevert,
            ["halt/exception/nested"] = OperationalControlPredicateIdentity.NestedException,
            ["resumeParent"] = OperationalControlPredicateIdentity.ResumeParent,
            ["halt/success/top"] = OperationalControlPredicateIdentity.TopLevelSuccess,
            ["halt/revert/top"] = OperationalControlPredicateIdentity.TopLevelRevert,
            ["halt/exception/top"] = OperationalControlPredicateIdentity.TopLevelException,
            ["precompile/outOfGas/nested"] = OperationalControlPredicateIdentity.PrecompileOutOfGasNested,
            ["precompile/outOfGas/top"] = OperationalControlPredicateIdentity.PrecompileOutOfGasTop,
            ["precompile/returnedFailure"] = OperationalControlPredicateIdentity.PrecompileReturnedFailure,
            ["precompile/managedException"] = OperationalControlPredicateIdentity.PrecompileManagedException,
            ["bytecode/cancelled"] = OperationalControlPredicateIdentity.Cancelled,
            ["callback/escaped"] = OperationalControlPredicateIdentity.Escaped,
            ["invalidControl"] = OperationalControlPredicateIdentity.InvalidControl,
            ["completed"] = OperationalControlPredicateIdentity.Completed,
        };

    private static readonly IReadOnlyDictionary<string, OperationalControlSettlementIdentity>
        SettlementLowering = new Dictionary<string, OperationalControlSettlementIdentity>(StringComparer.Ordinal)
        {
            ["preparation"] = OperationalControlSettlementIdentity.Preparation,
            ["invocation"] = OperationalControlSettlementIdentity.Invocation,
            ["continued"] = OperationalControlSettlementIdentity.Continued,
            ["suspended"] = OperationalControlSettlementIdentity.Suspended,
            ["childSuccess"] = OperationalControlSettlementIdentity.ChildSuccess,
            ["childCreateSuccess"] = OperationalControlSettlementIdentity.ChildCreateSuccess,
            ["childCreateInvalidCode"] = OperationalControlSettlementIdentity.ChildCreateInvalidCode,
            ["childCreateOutOfGas"] = OperationalControlSettlementIdentity.ChildCreateOutOfGas,
            ["childRevert"] = OperationalControlSettlementIdentity.ChildRevert,
            ["childException"] = OperationalControlSettlementIdentity.ChildException,
            ["topLevelSuccess"] = OperationalControlSettlementIdentity.TopLevelSuccess,
            ["topLevelRevert"] = OperationalControlSettlementIdentity.TopLevelRevert,
            ["topLevelException"] = OperationalControlSettlementIdentity.TopLevelException,
            ["fullPrecompileOutOfGasNested"] = OperationalControlSettlementIdentity.FullPrecompileOutOfGasNested,
            ["fullPrecompileOutOfGasTop"] = OperationalControlSettlementIdentity.FullPrecompileOutOfGasTop,
            ["fullPrecompileReturnedFailure"] = OperationalControlSettlementIdentity.FullPrecompileReturnedFailure,
            ["fullPrecompileManagedException"] = OperationalControlSettlementIdentity.FullPrecompileManagedException,
            ["cancelled"] = OperationalControlSettlementIdentity.Cancelled,
            ["escaped"] = OperationalControlSettlementIdentity.Escaped,
            ["invalidControl"] = OperationalControlSettlementIdentity.InvalidControl,
            ["completed"] = OperationalControlSettlementIdentity.Completed,
        };

    private static readonly IReadOnlyDictionary<string, OperationalControlEffectIdentity>
        EffectLowering = new Dictionary<string, OperationalControlEffectIdentity>(StringComparer.Ordinal)
        {
            ["clearReturnData"] = OperationalControlEffectIdentity.ClearReturnData,
            ["prepareFresh"] = OperationalControlEffectIdentity.PrepareFresh,
            ["retainReturnData"] = OperationalControlEffectIdentity.RetainReturnData,
            ["prepareContinuation"] = OperationalControlEffectIdentity.PrepareContinuation,
            ["RunByteCode"] = OperationalControlEffectIdentity.RunBytecode,
            ["RunDispatchLoop"] = OperationalControlEffectIdentity.RunDispatchLoop,
            ["RunPrecompile"] = OperationalControlEffectIdentity.RunFullPrecompile,
            ["ExecutePrecompile"] = OperationalControlEffectIdentity.ExecutePrecompile,
            ["retainCurrentFrame"] = OperationalControlEffectIdentity.RetainCurrentFrame,
            ["prepareChildFrame"] = OperationalControlEffectIdentity.PrepareChildFrame,
            ["retainParent"] = OperationalControlEffectIdentity.RetainParent,
            ["popParent"] = OperationalControlEffectIdentity.PopParent,
            ["mergeChild"] = OperationalControlEffectIdentity.MergeChild,
            ["repayStateGasSpill"] = OperationalControlEffectIdentity.RepayStateGasSpill,
            ["prepareCreateData"] = OperationalControlEffectIdentity.PrepareCreateData,
            ["HandleCreate"] = OperationalControlEffectIdentity.HandleCreate,
            ["restoreWorld"] = OperationalControlEffectIdentity.RestoreWorld,
            ["creditParent"] = OperationalControlEffectIdentity.CreditParent,
            ["burnDepositGas"] = OperationalControlEffectIdentity.BurnDepositGas,
            ["restoreSnapshot"] = OperationalControlEffectIdentity.RestoreSnapshot,
            ["restoreChildGas"] = OperationalControlEffectIdentity.RestoreChildGas,
            ["HandleRevert"] = OperationalControlEffectIdentity.HandleRevert,
            ["restoreFailureControl"] = OperationalControlEffectIdentity.RestoreFailureControl,
            ["resumeParent"] = OperationalControlEffectIdentity.ResumeParent,
            ["PrepareTopLevelSubstate"] = OperationalControlEffectIdentity.PrepareTopLevelSubstate,
            ["refundRevertedStateGas"] = OperationalControlEffectIdentity.RefundRevertedStateGas,
            ["HandleExceptionOrFailure"] = OperationalControlEffectIdentity.HandleExceptionOrFailure,
            ["failureSettlement"] = OperationalControlEffectIdentity.FailureSettlement,
            ["FrameCleanupScope.Dispose"] = OperationalControlEffectIdentity.Dispose,
            ["failClosed"] = OperationalControlEffectIdentity.FailClosed,
            ["DisposeActiveFrames"] = OperationalControlEffectIdentity.DisposeActiveFrames,
        };

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };
    private static readonly JsonSerializerOptions CompilerReferenceJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    private static readonly (string Stage, string ManifestPath, string ManifestSha256,
        string ProofPath, string ProofSha256, string Theorem, string? SignatureSha256, string Binding)[]
        AdditionalDependencies =
    [
        ("Stage A routing", FrameMachineManifestPath, FrameMachineManifestSha256,
            "tools/Evm/Lean/EvmFrameMachineExtractor/Refinement/StageARouting.lean",
            "64256f92a7013579d271cf88a88fb98e2a3bb879da16fcd0238502f7a6867c8e",
            "EvmFrameMachineExtractor.Refinement.StageARouting.generated_route_lookup_refines_independent_spec",
            "5505aed4bd63c22a301117f4ef3ab74912f4475769ed405427e5af14fba2a592",
            "Stage A exact routing"),
        ("Stage B precompile", "tools/Evm/Lean/PrecompileFrameExtractor/Generated/precompile-frame-stage-b.source-manifest.json",
            "4c79d8ddc0ffce7f71cda214f548d95231c8e8ccf14ded80e29e4e17ad990ba0",
            "tools/Evm/Lean/PrecompileFrameExtractor/Refinement/PrecompileFrameStageB.lean",
            "3a926b90ce1322300b842a804691047f9297347afc06726b3e7b0b9951a33435",
            "Eip803x.PrecompileFrame.StageB.Refinement.source_execute_precompile_refines_reference",
            "79f13378836e80e8b70c7f4c4f1fcdd120a7f3f24a7b4b051ff4f93c0b173ece", "full-precompile adapter"),
        ("Stage C precompile", "tools/Evm/Lean/PrecompileFullFrameExtractor/Generated/precompile-full-frame-stage-c.source-manifest.json",
            "0aea227cfdfaa960fb48bf4c32dcf12a9d10a73979da54b854735dd714c038ab",
            "tools/Evm/Lean/PrecompileFullFrameExtractor/Refinement/PrecompileFullFrame.lean",
            "505cc7f5d31c9a9d8055be3f957d1625bcc71d5e8aaded4a9efee4fad977b27a",
            "Eip803x.PrecompileFullFrame.Refinement.source_full_frame_refines_reference",
            "8efc42f9c6227d53a95d38335d7087df889b98e288e58c6d8bb4dc3385537bd6", "full-precompile frame settlement"),
        ("FrameJournal", "tools/Evm/Lean/FrameJournalExtractor/Generated/FrameJournalKernel.source-manifest.json",
            "7459576674f44b1b1b47ba2c9f57a4fccea7bb17d56fd0e5f550e64a1e577634",
            "tools/Evm/Lean/FrameJournalExtractor/Refinement/FrameJournal.lean",
            "36fbd6da282c88a590b038d8824a4149f03c1b9d2abcce61b1ed498524948737",
            "FrameJournalExtractor.Refinement.FrameJournal.transition_refines",
            "54fcea6c3e4b4d89ef460b41b986abfef7ade6e16c2975a0631014c40ce03988", "frame journal transition"),
        ("WorldJournal", "tools/Evm/Lean/WorldJournalExtractor/Generated/WorldJournalKernel.source-manifest.json",
            "5c22d3c7649fc110fbb40cdaeacf49dfe4d7a87b529d27ea3f79a6d316bf1ffb",
            "tools/Evm/Lean/WorldJournalExtractor/Refinement/WorldJournal.lean",
            "f37d5ebd59a9f6353bf5200f6dd1952c1591cde70f408d23403fb9f0ecd91407",
            "WorldJournalExtractor.Refinement.WorldJournal.transition_refines",
            "54fcea6c3e4b4d89ef460b41b986abfef7ade6e16c2975a0631014c40ce03988", "world journal transition"),
        ("State gas", "tools/Evm/Lean/Extractor/Generated/StateGasTransitionKernel.source-manifest.json",
            "362031db8ef6bbfdcab657694f69a810369273f2ca41b7e1a233465e05c4cf85",
            "tools/Evm/Lean/Eip803x/Refinement/StateGasTransition.lean",
            "a3d3beb8004c1431e2550a702f84d816751657ad90c8320ddc5809956da41cec",
            "Eip803x.Refinement.StateGasTransition.generated_refund_matches_spec",
            "eace38840b2ec0a52c58f1c29cdf4bad7512ae74e7af3902e8c6f25fce4d44e8", "state gas reservoir/refund"),
        ("Precompile pricing", "tools/Evm/Lean/Extractor/Generated/PrecompileGasPricingKernel.source-manifest.json",
            "6586179cdc3274d772f998ef2a9eb35f389754cd97a2a5e059ba8a953179bb2f",
            "tools/Evm/Lean/Eip803x/Refinement/PrecompileGasPricing.lean",
            "8ba4eeb00a7858e65dd2e338119c3f60319716c7283d1d18aa2ced6b6f6d8c43",
            "Eip803x.Refinement.PrecompileGasPricing.tryConsume_refines_wrapper",
            "20f84d065e99741941c260bc4ca19a0c2d7bc575f125e3efc858e673aee89523", "precompile gas pricing"),
        ("Stage D settlement", "tools/Evm/Lean/EvmFrameControlSettlementExtractor/Generated/EvmFrameControlSettlementKernel.source-manifest.json",
            "307cd1ea14e6542b732cdf6f3c2591f54e2375392f92d043205b754ed86b85f9",
            "tools/Evm/Lean/EvmFrameControlSettlementExtractor/Refinement/FrameControlSettlement.lean",
            "a3a4e2b02216bb484ce5f3164c95a3887df648d3fb0afd79ad22393da490a266",
            "EvmFrameControlSettlementExtractor.Refinement.hash_pinned_canonical_frame_control_settlement_single_iteration_control_agreement",
            "a695dd832039880148670d41d3f4558337352b8c4a19ea3f728a48887e8d69cb", "settlement control"),
    ];

    internal static OperationalExtractionResult Extract(
        string repoRoot, string outputDirectory, string? leanOutputPath = null)
    {
        string root = Path.GetFullPath(repoRoot);
        EvmFrameDriverProfile.ValidateEmbeddedAdmission(root);
        (IrDocument stageE, byte[] stageEIrBytes, _, _) = EvmFrameDriverProfile.BuildForTest(root);
        CompilerReferenceIdentity[] compilerReferences = ValidateRoslynOperationalClosure(root, stageE);
        OperationalDependencyIdentity[] dependencies = ResolveDependencies(root);
        OperationalIrDocument ir = BuildIr(root, stageE, EvmFrameDriverProfile.Hash(stageEIrBytes),
            compilerReferences, dependencies);
        ValidateIr(ir, dependencies);
        byte[] irBytes = Serialize(ir);
        byte[] leanBytes = OperationalLeanEmitter.Emit(ir, EvmFrameDriverProfile.Hash(irBytes));
        OperationalSourceManifest manifest = BuildManifest(ir, EvmFrameDriverProfile.Hash(irBytes),
            EvmFrameDriverProfile.Hash(leanBytes));
        ValidateManifest(root, manifest, irBytes, leanBytes);

        Directory.CreateDirectory(outputDirectory);
        string irPath = Path.Combine(outputDirectory, IrFileName);
        string manifestPath = Path.Combine(outputDirectory, ManifestFileName);
        string leanPath = leanOutputPath ?? Path.Combine(outputDirectory, LeanFileName);
        WriteIfChanged(irPath, irBytes);
        WriteIfChanged(manifestPath, Serialize(manifest));
        WriteIfChanged(leanPath, leanBytes);
        byte[] manifestBytes = Serialize(manifest);
        return new(irPath, manifestPath, leanPath, ir.Sources.Length, ir.Members.Length,
            ir.SourceControl.Length, dependencies.Length, ir.Adapters.Length, ir.MutationVectors.Length,
            EvmFrameDriverProfile.Hash(irBytes), EvmFrameDriverProfile.Hash(manifestBytes),
            EvmFrameDriverProfile.Hash(leanBytes));
    }

    internal static (OperationalIrDocument Ir, byte[] IrBytes, byte[] LeanBytes, byte[] ManifestBytes)
        BuildForTest(string repoRoot)
    {
        string root = Path.GetFullPath(repoRoot);
        (IrDocument stageE, byte[] stageEIrBytes, _, _) = EvmFrameDriverProfile.BuildForTest(root);
        CompilerReferenceIdentity[] compilerReferences = ValidateRoslynOperationalClosure(root, stageE);
        OperationalDependencyIdentity[] dependencies = ResolveDependencies(root);
        OperationalIrDocument ir = BuildIr(root, stageE, EvmFrameDriverProfile.Hash(stageEIrBytes),
            compilerReferences, dependencies);
        ValidateIr(ir, dependencies);
        byte[] irBytes = Serialize(ir);
        byte[] leanBytes = OperationalLeanEmitter.Emit(ir, EvmFrameDriverProfile.Hash(irBytes));
        OperationalSourceManifest manifest = BuildManifest(ir, EvmFrameDriverProfile.Hash(irBytes),
            EvmFrameDriverProfile.Hash(leanBytes));
        ValidateManifest(root, manifest, irBytes, leanBytes);
        return (ir, irBytes, leanBytes, Serialize(manifest));
    }

    private static OperationalIrDocument BuildIr(string root, IrDocument stageE, string stageEIrSha256,
        CompilerReferenceIdentity[] compilerReferences, OperationalDependencyIdentity[] dependencies)
    {
        string compilerVersion = typeof(CSharpSyntaxTree).Assembly.GetName().Version?.ToString() ?? "unknown";
        string compilerMvid = typeof(CSharpSyntaxTree).Assembly.ManifestModule.ModuleVersionId.ToString("D");
        RouteClosure routeClosure = LoadRouteClosure(root);
        OperationalLoopIdentity loop = ExtractLoopIdentity(root, stageE);
        OperationalBranchBindingIdentity[] branchBindings =
            BranchBindings(stageE.Branches, stageE.SourceControl);
        OperationalControlPlanStepIdentity[] controlPlan =
            ControlPlan(stageE.ControlPlan, branchBindings);
        return new(
            1, ExtractorVersion, compilerVersion, compilerMvid, LanguageVersion.CSharp14.ToString(), KernelName,
            AcceptanceState, EvmFrameDriverProfile.TargetRoot, EvmFrameDriverProfile.TargetFork,
            EvmFrameDriverProfile.TargetGasPolicy, StageEBoundary, stageEIrSha256, compilerReferences,
            stageE.Sources,
            stageE.Members, stageE.SourceControl, controlPlan,
             Topology(stageE.SourceControl), branchBindings, routeClosure.OpcodeRoutes,
             routeClosure.PrecompileRoutes,
            loop,
            Adapters(dependencies), dependencies,
            [.. stageE.ReviewedOperationalBindings,
                "Stage F generated loop is separate from Stage E one-step kernel",
                "accepted theorem identities are #check witnesses, not production adapter proofs",
                "clearFreshReturnData is the only concrete entry adapter; continuation preparation remains fail-closed",
                "route PCs/bytes are adapter-supplied evidence; per-op successors and PUSH widths are not inferred by Stage F",
                "CREATE collision is delegated to the accepted CALL/CREATE opcode route; deposit runs only after child success",
                "top-level completion requires a full TransactionSubstate adapter and a separate settlement adapter",
                "typed source branch instructions are interpreted before each frame-driver control stage",
                "STATICCALL/direct invocation behavior belongs to ExecuteCall/CALL/CREATE opcode leaves; Stage F has no direct-inline frame field or CALL source closure",
                "Stage F starts from a preconstructed VM/frame state and excludes TransactionProcessor.CompleteWithoutFrame and TransactionProcessor.FailContractCreate bypass paths"],
            ["refund-merge", "return-data-copy", "world-rollback", "cleanup-disposal",
                 "route-evidence-cardinality", "route-evidence-duplicate", "malformed-control-route",
                 "cancellation-epoch-1-terminal", "cancellation-epoch-1-nonterminal",
                 "cancellation-epoch-2-terminal", "cancellation-epoch-2-nonterminal"],
            ["ProductionAdapterObligations per callback (including top-level preparation/settlement and typed createDeposit) remain uninstantiated",
             "concrete C# bytecode handlers, exact 1024-op epochs, and successor arithmetic remain adapter obligations",
             "route PCs/bytes are adapter-supplied dispatch evidence; exact per-op successor/control and variable-width PUSH handling remain unproved",
             "full precompile native/cryptographic execution remains adapter-obligated",
             "adequate fuel for continue and suspend/push from gas/PC/frame-stack measure is not proved",
             "resume validation is limited to parent LIFO stack shape/call depth; PC, gas, operand stack, memory, world, and output continuation equality remains a production adapter obligation",
             "transaction caller rollback, outer substate commit, CLR/JIT/pooled-memory behavior remain outside this stage",
             "TransactionProcessor.CompleteWithoutFrame and TransactionProcessor.FailContractCreate are excluded transaction-processing bypass paths",
             "direct invocation/STATICCALL semantics and CALL/CREATE source closure remain delegated to the accepted opcode leaf",
             "generated plan, topology, branch bindings, and exact route tables must remain hash-bound and reachable",
             "Roslyn call-bearing nodes require exact IMethodSymbol target binding across the real reference closure"]);
    }

    private static OperationalLoopIdentity ExtractLoopIdentity(string root, IrDocument stageE)
    {
        SourceIdentity dispatchSource = stageE.Sources.SingleOrDefault(static source =>
            source.Path.EndsWith("/VirtualMachine.Dispatch.cs", StringComparison.Ordinal))
            ?? throw new ExtractionException("Stage F loop source is not present in the accepted closure.");
        string path = EvmFrameDriverProfile.ResolveCanonical(root, dispatchSource.Path);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(StrictUtf8.GetString(File.ReadAllBytes(path)),
            new CSharpParseOptions(LanguageVersion.CSharp14, DocumentationMode.Parse,
                SourceCodeKind.Regular), dispatchSource.Path, StrictUtf8);
        if (tree.GetDiagnostics().Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            throw new ExtractionException("Stage F loop source does not parse: " + dispatchSource.Path + ".");
        CompilationUnitSyntax syntax = tree.GetCompilationUnitRoot();
        FieldDeclarationSyntax mask = syntax.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .SingleOrDefault(field => field.Modifiers.Any(static modifier =>
                    modifier.IsKind(SyntaxKind.ConstKeyword)) &&
                field.Declaration.Type is PredefinedTypeSyntax predefined &&
                predefined.Keyword.IsKind(SyntaxKind.IntKeyword) &&
                field.Declaration.Variables.Count == 1 &&
                field.Declaration.Variables[0].Identifier.ValueText == "CancellationCheckMask")
            ?? throw new ExtractionException("Stage F cancellation mask declaration is missing or ambiguous.");
        EqualsValueClauseSyntax initializer = mask.Declaration.Variables[0].Initializer
            ?? throw new ExtractionException("Stage F cancellation mask has no constant initializer.");
        if (initializer.Value is not LiteralExpressionSyntax literal ||
            !literal.IsKind(SyntaxKind.NumericLiteralExpression) ||
            literal.Token.Value is not int maskValue || maskValue < 0)
            throw new ExtractionException("Stage F cancellation mask is not an integer literal.");

        ClassDeclarationSyntax opcodeTable = syntax.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .SingleOrDefault(static candidate => candidate.Identifier.ValueText == "OpcodeTable")
            ?? throw new ExtractionException("Stage F opcode table declaration is missing or ambiguous.");
        string[] dispatchModes = [.. opcodeTable.Members.OfType<FieldDeclarationSyntax>()
            .Where(static field => field.Declaration.Type.DescendantNodesAndSelf()
                .OfType<FunctionPointerTypeSyntax>().Any())
            .SelectMany(static field => field.Declaration.Variables.Select(static variable =>
                variable.Identifier.ValueText))];
        string[] expectedModes = ["NoTrace", "NoTraceCancelable", "Traced", "TracedCancelable"];
        if (!dispatchModes.SequenceEqual(expectedModes, StringComparer.Ordinal))
            throw new ExtractionException("Stage F opcode table fields are not the exact four dispatch modes.");

        MethodDeclarationSyntax runDispatchLoop = syntax.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .SingleOrDefault(static method => method.Identifier.ValueText == "RunDispatchLoop")
            ?? throw new ExtractionException("Stage F RunDispatchLoop declaration is missing or ambiguous.");
        WhileStatementSyntax[] loops = runDispatchLoop.Body?.DescendantNodes()
            .OfType<WhileStatementSyntax>().ToArray() ?? [];
        if (runDispatchLoop.Body is null ||
            loops.Length != 1 ||
            loops[0].Condition is not LiteralExpressionSyntax whileCondition ||
            !whileCondition.IsKind(SyntaxKind.TrueLiteralExpression) ||
            runDispatchLoop.Body.DescendantNodes().OfType<InvocationExpressionSyntax>().Count(invocation =>
                InvocationNameForValidation(invocation.Expression) ==
                "ThrowOperationCanceledException") != 2 ||
            !runDispatchLoop.Body.DescendantNodes().OfType<BinaryExpressionSyntax>().Any(binary =>
                binary.IsKind(SyntaxKind.BitwiseAndExpression) &&
                binary.Right is IdentifierNameSyntax identifier &&
                identifier.Identifier.ValueText == "CancellationCheckMask"))
            throw new ExtractionException("Stage F RunDispatchLoop cancellation epoch shape drifted.");

        return new(
            checked(maskValue + 1), dispatchModes,
            ["clearReturnData iff !IsContinuation", "PrepareFresh", "PrepareContinuation"],
            ["None", "Suspend child", "Stop/Revert", "EVM exception", "Overflow", "OperationCanceledException", "escaped"],
            ["success regular", "success CREATE deposit", "CREATE collision delegated to accepted CALL/CREATE routing",
                "CREATE deposit invalid code", "CREATE deposit OOG", "revert", "exception",
                "top-level success/revert/exception", "precompile out-of-gas/returned-failure/managed-exception"],
            ["refund child gas", "state reservoir", "state gas used", "state gas spill", "spill refund", "refund merge/rollback",
                "world snapshot", "access/log/destroy", "RIPEMD latch", "return data and bounded parent output copy", "trace/substate/status"],
            ["cancelled cleanup", "escaped cleanup", "exception cleanup", "DisposeActiveFrames", "fuel exhausted"],
            ["fuel decrements once per driver iteration", "cancelable nonterminal dispatch epochs are exactly 1024 opcodes",
                "a cancelable poll follows every completed nonterminal epoch",
                "a cancelable poll uses cumulative 1024/2048+ counts",
                "noncancelable tail-call execution reports one exact terminal chain count", "successor PC is <= code length",
                "adapter-supplied route and PC evidence has one aligned witness per reported completed opcode with cardinality/byte checks",
                "route and PC evidence does not prove per-op successor/control transitions", "child suspension enters only a fresh frame",
            "LIFO parent push/resume stack shape"],
            "gasLeft + remainingCode + parentStackDepth", "blocked: no adequate-fuel proof for all admitted production transitions");
    }

    // Keep the source-shape helper local to the operational extractor. Stage E
    // intentionally exposes only its canonical topology and does not widen its
    // accepted public model with a dispatch-loop-specific invocation API.
    private static string InvocationNameForValidation(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => InvocationNameForValidation(member.Name),
        GenericNameSyntax generic => generic.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        _ => string.Empty,
    };

    private static OperationalTopologyNodeIdentity[] Topology(SourceControlIdentity[] controls) =>
        [.. controls.SelectMany(control => control.Topology.Select(node =>
            new OperationalTopologyNodeIdentity(control.Member, node.Id, node.Kind, node.ParentId,
                node.Arm, node.Condition, node.Sha256, node.Operations)))];

    private static OperationalControlPlanStepIdentity[] ControlPlan(
        ControlPlanStepIdentity[] sourcePlan,
        OperationalBranchBindingIdentity[] branchBindings) =>
        [.. sourcePlan.Select(step =>
        {
            OperationalControlActionIdentity[] actions = [.. branchBindings
                .Where(binding => binding.Stages.Contains(step.Stage, StringComparer.Ordinal))
                .SelectMany(binding => binding.Stages.Select((stage, index) =>
                    (stage, action: binding.Actions[index])))
                .Where(pair => pair.stage == step.Stage)
                .Select(pair => pair.action)
                .Distinct()];
            if (actions.Length == 0)
                throw new ExtractionException("Stage F source control plan has no typed actions for " +
                    step.Stage + ".");
            return new OperationalControlPlanStepIdentity(step.Stage, step.Member, step.NodeId,
                step.Kind, step.SourceArm, step.SourceSha256, actions);
        })];

    private static OperationalBranchBindingIdentity[] BranchBindings(BranchDescriptor[] branches,
        SourceControlIdentity[] controls)
    {
        List<OperationalBranchBindingIdentity> result =
        [.. branches.SelectMany(branch => branch.SourceBindings.Select(binding =>
        {
            string[] fields = binding.Split(':');
            if (fields.Length != 6 || string.IsNullOrWhiteSpace(fields[0]) ||
                string.IsNullOrWhiteSpace(fields[1]) || string.IsNullOrWhiteSpace(fields[2]) ||
                string.IsNullOrWhiteSpace(fields[3]) || !IsSha256(fields[4]) ||
                string.IsNullOrWhiteSpace(fields[5]) || string.IsNullOrWhiteSpace(branch.Invocation) ||
                branch.Effects is null || branch.Effects.Length == 0 ||
                branch.Effects.Any(string.IsNullOrWhiteSpace) || string.IsNullOrWhiteSpace(branch.Settlement))
                throw new ExtractionException("Stage F branch binding is not a six-field source binding: " +
                    branch.Name + ".");
            OperationalControlPredicateIdentity predicate = ParsePredicate(branch.Invocation);
            OperationalControlEffectIdentity[] effectKinds = [.. branch.Effects.Select(ParseEffect)];
            OperationalControlSettlementIdentity settlement = ParseSettlement(branch.Settlement);
            string[] stages = BranchStages(predicate, settlement);
            OperationalControlActionIdentity[] actions =
                [.. stages.Select(stage => LowerAction(ParseStage(stage), predicate))];
            return new OperationalBranchBindingIdentity(branch.Name, fields[0], fields[1], fields[2],
                fields[3], fields[4], fields[5], branch.Invocation, predicate, branch.Effects, effectKinds,
                branch.Settlement, settlement, actions, stages);
        }))];
        SourceControlIdentity dispose = controls.Single(control => control.Member == "Dispose");
        ControlNodeIdentity node = dispose.Topology.Single(candidate => candidate.Kind == "statement" &&
            candidate.Arm == "then" && candidate.Operations.SequenceEqual(
                ["call:DisposeActiveFrames"], StringComparer.Ordinal));
        result.Add(new("CompletedCleanup", dispose.Member, node.Id, node.Kind, node.Arm, node.Sha256, "node",
            "completed", OperationalControlPredicateIdentity.Completed, ["DisposeActiveFrames"],
            [OperationalControlEffectIdentity.DisposeActiveFrames], "completed",
            OperationalControlSettlementIdentity.Completed,
            [OperationalControlActionIdentity.CleanupCompleted], ["cleanup"]));
        SourceControlIdentity execute = controls.Single(control => control.Member == "ExecuteTransaction");
        ControlNodeIdentity loop = execute.Topology.Single(candidate => candidate.Kind == "while" &&
            candidate.ParentId == "root" && candidate.Arm == "body" && candidate.Condition == "true" &&
            candidate.Operations.Contains("call:PrepareNextCallFrame", StringComparer.Ordinal) &&
            candidate.Operations.Contains("call:HandleRegularReturn", StringComparer.Ordinal) &&
            candidate.Operations.Contains("call:HandleRevert", StringComparer.Ordinal) &&
            candidate.Operations.Contains("call:HandleException", StringComparer.Ordinal));
        result.Add(new("ParentResume", execute.Member, loop.Id, loop.Kind, loop.Arm, loop.Sha256, "node",
            "resumeParent", OperationalControlPredicateIdentity.ResumeParent, ["resumeParent"],
            [OperationalControlEffectIdentity.ResumeParent], "continued",
            OperationalControlSettlementIdentity.Continued,
            [OperationalControlActionIdentity.SettleResume], ["settle"]));
        return result.ToArray();
    }

    private static OperationalControlStageIdentity ParseStage(string stage) => stage switch
    {
        "prepare" => OperationalControlStageIdentity.Prepare,
        "dispatch" => OperationalControlStageIdentity.Dispatch,
        "classify" => OperationalControlStageIdentity.Classify,
        "settle" => OperationalControlStageIdentity.Settle,
        "cleanup" => OperationalControlStageIdentity.Cleanup,
        _ => throw new ExtractionException("Unknown Stage F source control stage " + stage + "."),
    };

    private static OperationalControlPredicateIdentity ParsePredicate(string invocation)
    {
        if (PredicateLowering.TryGetValue(invocation, out OperationalControlPredicateIdentity predicate))
            return predicate;
        throw new ExtractionException("Stage F source branch has no typed predicate for " + invocation + ".");
    }

    private static OperationalControlEffectIdentity ParseEffect(string effect)
    {
        if (EffectLowering.TryGetValue(effect, out OperationalControlEffectIdentity typedEffect))
            return typedEffect;
        throw new ExtractionException("Stage F source branch has no typed effect for " + effect + ".");
    }

    private static OperationalControlSettlementIdentity ParseSettlement(string settlement)
    {
        if (SettlementLowering.TryGetValue(settlement, out OperationalControlSettlementIdentity typedSettlement))
            return typedSettlement;
        throw new ExtractionException("Stage F source branch has no typed settlement for " + settlement + ".");
    }

    private static OperationalControlActionIdentity LowerAction(
        OperationalControlStageIdentity stage, OperationalControlPredicateIdentity predicate)
    {
        if (OperationalActionLowering.TryGetValue((stage, predicate), out OperationalControlActionIdentity action))
            return action;
        throw new ExtractionException("Stage F source branch has no typed action for " + predicate + ".");
    }

    private static string[] BranchStages(OperationalControlPredicateIdentity predicate,
        OperationalControlSettlementIdentity settlement) => predicate switch
    {
        OperationalControlPredicateIdentity.FreshFrame or
            OperationalControlPredicateIdentity.ContinuationFrame => ["prepare"],
        OperationalControlPredicateIdentity.BytecodeFrame or
            OperationalControlPredicateIdentity.FullPrecompileFrame or
            OperationalControlPredicateIdentity.PrecompileOutOfGasNested or
            OperationalControlPredicateIdentity.PrecompileOutOfGasTop or
            OperationalControlPredicateIdentity.PrecompileReturnedFailure or
            OperationalControlPredicateIdentity.PrecompileManagedException => ["dispatch"],
        OperationalControlPredicateIdentity.BytecodeContinue or
            OperationalControlPredicateIdentity.BytecodeSuspend => ["classify"],
        OperationalControlPredicateIdentity.NestedRegularSuccess or
            OperationalControlPredicateIdentity.NestedCreateSuccess or
            OperationalControlPredicateIdentity.NestedRevert or
            OperationalControlPredicateIdentity.NestedException or
            OperationalControlPredicateIdentity.TopLevelSuccess or
            OperationalControlPredicateIdentity.TopLevelRevert or
            OperationalControlPredicateIdentity.TopLevelException =>
            ["classify", "settle"],
        OperationalControlPredicateIdentity.CreateDepositInvalidCode or
            OperationalControlPredicateIdentity.CreateDepositOutOfGas or
            OperationalControlPredicateIdentity.ResumeParent => ["settle"],
        OperationalControlPredicateIdentity.Cancelled or
            OperationalControlPredicateIdentity.Escaped or
            OperationalControlPredicateIdentity.InvalidControl or
            OperationalControlPredicateIdentity.Completed => ["cleanup"],
        _ => throw new ExtractionException("Stage F source invocation has no control stage: " +
            predicate + " (" + settlement + ")."),
    };

    private static RouteClosure LoadRouteClosure(string root)
    {
        string manifestPath = EvmFrameDriverProfile.ResolveCanonical(root, FrameMachineManifestPath);
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        JsonElement artifact = manifest.RootElement.TryGetProperty("ir", out JsonElement ir)
            ? ir
            : throw new ExtractionException("Stage F Stage A manifest has no IR artifact.");
        string irRelative = RequiredString(artifact, "path");
        string irPath = ResolveManifestArtifact(root, FrameMachineManifestPath, irRelative);
        RequireHash(irPath, RequiredString(artifact, "sha256"), irRelative);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(irPath));
        JsonElement rootElement = document.RootElement;
        if (!rootElement.TryGetProperty("opcodeRoutes", out JsonElement opcodeElement) ||
            opcodeElement.ValueKind != JsonValueKind.Array ||
            !rootElement.TryGetProperty("precompileRoutes", out JsonElement precompileElement) ||
            precompileElement.ValueKind != JsonValueKind.Array)
            throw new ExtractionException("Stage F Stage A IR has no typed route closure.");

        OperationalOpcodeRouteIdentity[] opcodes = [.. opcodeElement.EnumerateArray().Select(ParseOpcodeRoute)];
        OperationalPrecompileRouteIdentity[] precompiles =
            [.. precompileElement.EnumerateArray().Select(ParsePrecompileRoute)];
        if (opcodes.Length != 1024 ||
            opcodes.GroupBy(static route => route.DispatchTable + ":" + route.Byte)
                .Any(static group => group.Count() != 1) ||
            !opcodes.Select(static route => route.DispatchTable).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).SequenceEqual(
                    ["NoTrace", "NoTraceCancelable", "Traced", "TracedCancelable"],
                    StringComparer.Ordinal) ||
             opcodes.Any(static route => route.Byte is < 0 or >= 256 || !route.Admitted ||
                 string.IsNullOrWhiteSpace(route.Instruction) ||
                string.IsNullOrWhiteSpace(route.RouteKind) ||
                string.IsNullOrWhiteSpace(route.ActivationRule) ||
                string.IsNullOrWhiteSpace(route.Package) ||
                string.IsNullOrWhiteSpace(route.ClosedHandlerRoot)))
            throw new ExtractionException("Stage F Stage A opcode route table is not exactly four complete 256-byte tables.");
        if (precompiles.Length != 18 ||
            precompiles.Select(static route => route.Address).Distinct().Count() != precompiles.Length ||
            precompiles.Select(static route => route.Name).Distinct(StringComparer.Ordinal).Count() != precompiles.Length ||
             precompiles.Any(static route => !route.Admitted || string.IsNullOrWhiteSpace(route.Name) ||
                string.IsNullOrWhiteSpace(route.ActivationRule) || string.IsNullOrWhiteSpace(route.ProviderRoot) ||
                string.IsNullOrWhiteSpace(route.WrapperManifestPath) ||
                !IsSha256(route.WrapperManifestSha256) || string.IsNullOrWhiteSpace(route.LeanModule)))
            throw new ExtractionException("Stage F Stage A precompile route table is not an exact 18-address closure.");
        return new(opcodes, precompiles);
    }

    private static OperationalOpcodeRouteIdentity ParseOpcodeRoute(JsonElement value) =>
        new(RequiredString(value, "dispatchTable"), RequiredInt(value, "byte"),
            RequiredString(value, "instruction"), RequiredString(value, "routeKind"),
            RequiredString(value, "activationRule"), RequiredString(value, "package"),
            RequiredString(value, "closedHandlerRoot"), RequiredBool(value, "admitted"));

    private static OperationalPrecompileRouteIdentity ParsePrecompileRoute(JsonElement value)
    {
        string? theorem = value.TryGetProperty("fullyQualifiedTheorem", out JsonElement theoremElement) &&
            theoremElement.ValueKind != JsonValueKind.Null
            ? RequiredString(value, "fullyQualifiedTheorem")
            : null;
        return new(RequiredString(value, "name"), RequiredInt(value, "address"),
            RequiredString(value, "activationRule"), RequiredString(value, "providerRoot"),
            RequiredString(value, "wrapperManifestPath"), RequiredString(value, "wrapperManifestSha256"),
            RequiredString(value, "leanModule"), theorem, RequiredBool(value, "admitted"));
    }

    private static string ResolveManifestArtifact(string root, string manifestPath, string relative)
    {
        string manifestDirectory = (Path.GetDirectoryName(manifestPath) ?? string.Empty).Replace('\\', '/');
        string? candidate = relative.StartsWith("tools/", StringComparison.Ordinal)
            ? TryResolve(root, relative)
            : TryResolve(root, string.IsNullOrEmpty(manifestDirectory)
                ? relative
                : manifestDirectory + "/" + relative);
        candidate ??= TryResolve(root, relative);
        if (candidate is null)
        {
            const string generatedMarker = "/Generated/";
            int marker = manifestPath.LastIndexOf(generatedMarker, StringComparison.Ordinal);
            if (marker >= 0)
                candidate = TryResolve(root, manifestPath[..marker] + relative);
        }
        return candidate ?? throw new ExtractionException("Stage F cannot resolve Stage A route IR " + relative + ".");
    }

    private static int RequiredInt(JsonElement value, string property) =>
        value.TryGetProperty(property, out JsonElement element) && element.TryGetInt32(out int result)
            ? result
            : throw new ExtractionException("Stage F route field is missing or not an integer: " + property + ".");

    private static bool RequiredBool(JsonElement value, string property) =>
        value.TryGetProperty(property, out JsonElement element) &&
        (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ? element.GetBoolean()
            : throw new ExtractionException("Stage F route field is missing or not boolean: " + property + ".");

    private sealed record RouteClosure(
        OperationalOpcodeRouteIdentity[] OpcodeRoutes,
        OperationalPrecompileRouteIdentity[] PrecompileRoutes);

    private static CompilerReferenceIdentity[] ValidateRoslynOperationalClosure(string root, IrDocument stageE)
    {
        SourceIdentity[] csharpSources = stageE.Sources
            .Where(static source => source.Path.EndsWith(".cs", StringComparison.Ordinal))
            .ToArray();
        if (csharpSources.Length == 0)
            throw new ExtractionException("Stage F Roslyn closure has no C# source trees.");

        List<SyntaxTree> trees = [];
        foreach (SourceIdentity source in csharpSources)
        {
            string path = EvmFrameDriverProfile.ResolveCanonical(root, source.Path);
            SyntaxTree tree = CSharpSyntaxTree.ParseText(StrictUtf8.GetString(File.ReadAllBytes(path)),
                new CSharpParseOptions(LanguageVersion.CSharp14, DocumentationMode.Parse,
                    SourceCodeKind.Regular), source.Path, StrictUtf8);
            if (tree.GetDiagnostics().Any(static diagnostic => diagnostic.Severity != DiagnosticSeverity.Hidden))
                throw new ExtractionException("Stage F Roslyn parse failed for " + source.Path + ".");
            trees.Add(tree);
        }

        CompilerReferenceClosure closure = LoadCompilerReferenceClosure(root);
        CompilerReferenceIdentity[] referenceIdentities = closure.Identities;
        MetadataReference[] references = closure.References;
        CSharpCompilation compilation = CSharpCompilation.Create(
            "EvmFrameDriverStageFClosure", trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                allowUnsafe: true,
                metadataImportOptions: MetadataImportOptions.All));
        if (compilation.SyntaxTrees.Length != csharpSources.Length ||
            compilation.References.Count() != references.Length)
            throw new ExtractionException("Stage F Roslyn closure contains extra sources or references.");
        Diagnostic[] diagnostics = compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity != DiagnosticSeverity.Hidden)
            .ToArray();
        if (diagnostics.Length != 0)
            throw new ExtractionException("Stage F Roslyn semantic closure has diagnostics/errors: " +
                string.Join("; ", diagnostics.Take(200).Select(static diagnostic => diagnostic.ToString())));

        foreach (SourceControlIdentity control in stageE.SourceControl)
        {
            SyntaxTree tree = trees.Single(candidate =>
                candidate.FilePath.Equals(control.SourcePath, StringComparison.Ordinal));
            SemanticModel semanticModel = compilation.GetSemanticModel(tree, true);
            MemberIdentity admittedMember = stageE.Members.SingleOrDefault(member =>
                member.SourcePath == control.SourcePath && member.OwnerPath == control.OwnerPath &&
                member.MemberKind == "method" && member.Member == control.Member)
                ?? throw new ExtractionException("Stage F Roslyn control member admission is missing for " +
                    control.Member + ".");
            MethodDeclarationSyntax[] methods = tree.GetRoot().DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(method => MethodMatchesAdmission(method, admittedMember))
                .Where(method => semanticModel.GetDeclaredSymbol(method) is IMethodSymbol symbol &&
                    SymbolOwnerMatches(symbol, control.OwnerPath))
                .ToArray();
            if (methods.Length != 1)
                throw new ExtractionException("Stage F Roslyn symbol is missing or ambiguous for " + control.Member + ".");
            MethodDeclarationSyntax method = methods[0];
            if (semanticModel.GetDeclaredSymbol(method) is not IMethodSymbol symbol ||
                symbol.Name != control.Member || !SymbolOwnerMatches(symbol, control.OwnerPath) ||
                HasErrorSymbol(symbol) || symbol.MethodKind != MethodKind.Ordinary)
                throw new ExtractionException("Stage F Roslyn type/symbol validation failed for " + control.Member + ".");
            BlockSyntax body = method.Body ??
                throw new ExtractionException("Stage F Roslyn method body is missing for " + control.Member + ".");
            if (semanticModel.GetOperation(body) is not IBlockOperation)
                throw new ExtractionException("Stage F Roslyn IOperation validation failed for " + control.Member + ".");
            if (semanticModel.GetOperation(method) is not IMethodBodyOperation methodBody)
                throw new ExtractionException("Stage F Roslyn method-body operation validation failed for " + control.Member + ".");
            ControlFlowGraph graph;
            try
            {
                graph = ControlFlowGraph.Create(methodBody);
            }
            catch (ArgumentException exception)
            {
                throw new ExtractionException("Stage F Roslyn CFG validation failed for " + control.Member +
                    ": " + exception.Message);
            }
            catch (InvalidOperationException exception)
            {
                throw new ExtractionException("Stage F Roslyn CFG validation failed for " + control.Member +
                    ": " + exception.Message);
            }
            if (graph.Blocks.Length < 2)
                throw new ExtractionException("Stage F Roslyn CFG validation failed for " + control.Member + ".");
            ValidateControlCalls(semanticModel, method, control);
        }
        return referenceIdentities;
    }

    private static void ValidateControlCalls(
        SemanticModel semanticModel, MethodDeclarationSyntax method, SourceControlIdentity control)
    {
        List<ControlNodeBinding> sourceTopology = ExtractControlTopology(method);
        if (sourceTopology.Count != control.Topology.Length)
            throw new ExtractionException("Stage F Roslyn control topology count changed for " + control.Member + ".");

        for (int index = 0; index < sourceTopology.Count; index++)
        {
            ControlNodeBinding sourceNode = sourceTopology[index];
            ControlNodeIdentity admittedNode = control.Topology[index];
            if (sourceNode.Kind != admittedNode.Kind || sourceNode.ParentId != admittedNode.ParentId ||
                sourceNode.Arm != admittedNode.Arm || sourceNode.Condition != admittedNode.Condition ||
                sourceNode.Sha256 != admittedNode.Sha256 ||
                !sourceNode.Operations.SequenceEqual(admittedNode.Operations, StringComparer.Ordinal) ||
                sourceNode.ConditionIdentity != admittedNode.ConditionIdentity)
                throw new ExtractionException("Stage F Roslyn control topology drifted for " + control.Member +
                    " at " + admittedNode.Id + ".");
            ValidateConditionTarget(semanticModel, sourceNode.Node, sourceNode.ConditionIdentity, control.Member,
                admittedNode.Id);
            ValidateInvocationTargets(semanticModel, sourceNode.Node, sourceNode.Operations, control.Member,
                admittedNode.Id);
        }

        string[] admittedCalls = control.Operations
            .Where(static operation => operation.StartsWith("call:", StringComparison.Ordinal))
            .ToArray();
        string[] sourceCalls = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(static invocation => "call:" + InvocationName(invocation.Expression))
            .ToArray();
        if (sourceCalls.Any(static operation => operation == "call:") ||
            !sourceCalls.SequenceEqual(admittedCalls, StringComparer.Ordinal))
            throw new ExtractionException("Stage F Roslyn call-bearing topology is incomplete for " + control.Member + ".");
    }

    private static void ValidateInvocationTargets(
        SemanticModel semanticModel, SyntaxNode node, string[] admittedOperations, string member, string nodeId)
    {
        InvocationExpressionSyntax[] invocations = node.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>().ToArray();
        string[] admittedCalls = admittedOperations
            .Where(static operation => operation.StartsWith("call:", StringComparison.Ordinal))
            .ToArray();
        string[] actualCalls = invocations.Select(static invocation => "call:" + InvocationName(invocation.Expression)).ToArray();
        if (actualCalls.Any(static operation => operation == "call:") ||
            !actualCalls.SequenceEqual(admittedCalls, StringComparer.Ordinal))
            throw new ExtractionException("Stage F call-bearing node drifted for " + member + "." + nodeId + ".");

        foreach (InvocationExpressionSyntax invocation in invocations)
        {
            SymbolInfo symbolInfo = semanticModel.GetSymbolInfo(invocation);
            if (symbolInfo.CandidateReason != CandidateReason.None || symbolInfo.CandidateSymbols.Length != 0)
                throw new ExtractionException("Stage F call-bearing node resolved through a candidate or ambiguous symbol: " +
                    member + "." + nodeId + ".");
            if (semanticModel.GetOperation(invocation) is not IInvocationOperation operation ||
                operation.TargetMethod is not IMethodSymbol target ||
                symbolInfo.Symbol is not IMethodSymbol resolved ||
                !SymbolEqualityComparer.Default.Equals(resolved, target) ||
                target.Name != InvocationName(invocation.Expression) || HasErrorSymbol(target) ||
                target.OriginalDefinition is null || HasErrorSymbol(target.OriginalDefinition) ||
                target.ContainingAssembly is null || target.ContainingType is null ||
                target.ContainingType.TypeKind == TypeKind.Error ||
                string.IsNullOrWhiteSpace(target.ContainingAssembly.Identity.Name) ||
                string.IsNullOrWhiteSpace(target.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)))
                throw new ExtractionException("Stage F call-bearing node has no exact IMethodSymbol target: " +
                    member + "." + nodeId + ".");
        }
    }

    private static void ValidateConditionTarget(
        SemanticModel semanticModel, SyntaxNode node, ControlConditionIdentity expected, string member, string nodeId)
    {
        if (expected.Kind == ControlConditionKind.Unknown)
            return;
        if (node is not IfStatementSyntax ifStatement)
            throw new ExtractionException("Stage F condition identity is attached to a non-if node: " +
                member + "." + nodeId + ".");

        ExpressionSyntax expression = ifStatement.Condition;
        bool negated = false;
        while (true)
        {
            if (expression is ParenthesizedExpressionSyntax parenthesized)
            {
                expression = parenthesized.Expression;
                continue;
            }
            if (expression is PrefixUnaryExpressionSyntax prefix &&
                prefix.IsKind(SyntaxKind.LogicalNotExpression))
            {
                negated = !negated;
                expression = prefix.Operand;
                continue;
            }
            break;
        }
        if (negated != expected.Negated)
            throw new ExtractionException("Stage F condition negation identity drifted for " + member + "." + nodeId + ".");

        switch (expected.Kind)
        {
            case ControlConditionKind.Property:
                if (expression is not MemberAccessExpressionSyntax propertyAccess ||
                    propertyAccess.Name is not SimpleNameSyntax propertyName ||
                    !propertyName.Identifier.ValueText.Equals(expected.Member, StringComparison.Ordinal) ||
                    !EvmFrameDriverProfile.ReceiverIdentity(propertyAccess.Expression)
                        .Equals(expected.Receiver, StringComparison.Ordinal) ||
                    HasAmbiguousSymbol(semanticModel, propertyAccess) ||
                    semanticModel.GetSymbolInfo(propertyAccess).Symbol is not IPropertySymbol propertySymbol ||
                    !propertySymbol.Name.Equals(expected.Member, StringComparison.Ordinal) ||
                    HasErrorSymbol(propertySymbol) ||
                    !ReceiverSymbolMatches(semanticModel, propertyAccess.Expression, expected.Receiver))
                    throw new ExtractionException("Stage F condition property identity is not exact for " +
                        member + "." + nodeId + ".");
                break;
            case ControlConditionKind.Invocation:
                if (expression is not InvocationExpressionSyntax invocation ||
                    HasAmbiguousSymbol(semanticModel, invocation) ||
                    semanticModel.GetOperation(invocation) is not IInvocationOperation invocationOperation ||
                    invocationOperation.TargetMethod is not IMethodSymbol targetMethod ||
                    semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol resolvedMethod ||
                    !SymbolEqualityComparer.Default.Equals(resolvedMethod, targetMethod) ||
                    !targetMethod.Name.Equals(expected.Member, StringComparison.Ordinal) ||
                    HasErrorSymbol(targetMethod) ||
                    !InvocationReceiverMatches(semanticModel, invocation, expected.Receiver))
                    throw new ExtractionException("Stage F condition call identity is not exact for " +
                        member + "." + nodeId + ".");
                break;
            case ControlConditionKind.Local:
                if (expression is not IdentifierNameSyntax identifier ||
                    !identifier.Identifier.ValueText.Equals(expected.Member, StringComparison.Ordinal) ||
                    HasAmbiguousSymbol(semanticModel, identifier) ||
                    semanticModel.GetSymbolInfo(identifier).Symbol is not ILocalSymbol localSymbol ||
                    !localSymbol.Name.Equals(expected.Member, StringComparison.Ordinal) ||
                    HasErrorSymbol(localSymbol))
                    throw new ExtractionException("Stage F condition local identity is not exact for " +
                        member + "." + nodeId + ".");
                break;
            default:
                throw new ExtractionException("Stage F condition identity kind is unsupported for " + member + "." + nodeId + ".");
        }
    }

    private static bool ReceiverSymbolMatches(SemanticModel semanticModel, ExpressionSyntax expression,
        string expectedReceiver)
    {
        if (expectedReceiver.Length == 0)
            return true;
        SymbolInfo symbolInfo = semanticModel.GetSymbolInfo(expression);
        if (symbolInfo.CandidateReason != CandidateReason.None || symbolInfo.CandidateSymbols.Length != 0)
            return false;
        ISymbol? symbol = symbolInfo.Symbol;
        if (symbol is null || HasErrorSymbol(symbol))
            return false;
        string expectedName = expectedReceiver[(expectedReceiver.LastIndexOf('.') + 1)..];
        return symbol.Name.Equals(expectedName, StringComparison.Ordinal) &&
            symbol.Kind is SymbolKind.Field or SymbolKind.Property or SymbolKind.Local or SymbolKind.Parameter;
    }

    private static bool InvocationReceiverMatches(SemanticModel semanticModel,
        InvocationExpressionSyntax invocation, string expectedReceiver)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return expectedReceiver.Length == 0;
        return EvmFrameDriverProfile.ReceiverIdentity(memberAccess.Expression)
                   .Equals(expectedReceiver, StringComparison.Ordinal) &&
            ReceiverSymbolMatches(semanticModel, memberAccess.Expression, expectedReceiver);
    }

    private static bool HasAmbiguousSymbol(SemanticModel semanticModel, SyntaxNode node)
    {
        SymbolInfo symbolInfo = semanticModel.GetSymbolInfo(node);
        return symbolInfo.CandidateReason != CandidateReason.None || symbolInfo.CandidateSymbols.Length != 0;
    }

    private static CompilerReferenceClosure LoadCompilerReferenceClosure(string root)
    {
        string inventoryPath = EvmFrameDriverProfile.ResolveCanonical(root, CompilerReferenceInventoryPath);
        byte[] inventoryBytes = File.ReadAllBytes(inventoryPath);
        if (!EvmFrameDriverProfile.Hash(inventoryBytes).Equals(
                CompilerReferenceInventorySha256, StringComparison.Ordinal))
            throw new ExtractionException("Stage F compiler reference inventory bytes changed from the reviewed pin.");
        CompilerReferenceInventory inventory;
        try
        {
            inventory = JsonSerializer.Deserialize<CompilerReferenceInventory>(inventoryBytes, JsonOptions)
                ?? throw new ExtractionException("Stage F compiler reference inventory is empty.");
        }
        catch (JsonException exception)
        {
            throw new ExtractionException("Stage F compiler reference inventory is invalid: " + exception.Message);
        }
        byte[] canonicalInventoryBytes = StrictUtf8.GetBytes(
            JsonSerializer.Serialize(inventory, CompilerReferenceJsonOptions) + "\n");
        if (inventory.SchemaVersion != CompilerReferenceInventorySchemaVersion ||
            inventory.References is null || inventory.References.Any(static reference => reference is null) ||
            inventory.Count != CompilerReferenceInventoryCount ||
            inventory.References.Length != CompilerReferenceInventoryCount ||
            !inventory.AggregateSha256.Equals(CompilerReferenceInventoryAggregateSha256, StringComparison.Ordinal) ||
            !inventory.AggregateSha256.Equals(CompilerReferenceAggregateSha256(inventory.References),
                StringComparison.Ordinal) ||
            !canonicalInventoryBytes.AsSpan().SequenceEqual(inventoryBytes))
        {
            throw new ExtractionException("Stage F compiler reference inventory header or canonical bytes changed.");
        }
        ValidateCompilerReferencePins(inventory.References);

        string platformDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)
            ?? throw new ExtractionException("Stage F runtime platform directory is unavailable.");
        string[] platformPaths = Directory.EnumerateFiles(platformDirectory, "*.dll", SearchOption.TopDirectoryOnly)
            .Where(IsManagedAssembly)
            .Select(path => "platform/" + Path.GetFileName(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
        string[] closureDirectories = CompilerReferenceClosurePaths.Select(relative =>
            EvmFrameDriverProfile.ResolveCanonical(root, relative)).ToArray();
        if (closureDirectories.Any(directory => !Directory.Exists(directory)))
            throw new ExtractionException("Stage F compiler reference closure directory is missing.");
        HashSet<string> actualPaths = platformPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in closureDirectories)
            foreach (string path in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
            {
                string logical = NormalizePath(Path.GetRelativePath(root, path));
                if (!actualPaths.Add(logical))
                    throw new ExtractionException("Stage F compiler reference closure contains a duplicate path: " + logical + ".");
            }
        HashSet<string> expectedPaths = inventory.References
            .Select(static reference => reference.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] additions = actualPaths.Except(expectedPaths, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.Ordinal).ToArray();
        string[] removals = expectedPaths.Except(actualPaths, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.Ordinal).ToArray();
        if (additions.Length != 0 || removals.Length != 0)
            throw new ExtractionException("Stage F compiler reference path set changed. Additions: [" +
                string.Join(", ", additions) + "]. Removals: [" + string.Join(", ", removals) + "].");

        List<MetadataReference> selectedReferences = [];
        List<CompilerReferenceIdentity> identities = [];
        foreach (CompilerReferencePin pin in inventory.References)
        {
            string path = ResolveCompilerReferencePath(root, platformDirectory, pin.Path);
            if (!File.Exists(path))
                throw new ExtractionException("Stage F compiler reference inventory member does not exist: " + pin.Path + ".");
            (string assemblyName, string mvid) = ReadCompilerReferenceMetadata(pin.Path, path);
            if (!EvmFrameDriverProfile.Hash(File.ReadAllBytes(path)).Equals(pin.Sha256, StringComparison.Ordinal) ||
                !assemblyName.Equals(pin.AssemblyName, StringComparison.Ordinal) ||
                !mvid.Equals(pin.Mvid, StringComparison.Ordinal))
                throw new ExtractionException("Stage F compiler reference hash, assembly identity, or MVID drifted: " +
                    pin.Path + ".");
            // Validate every inventory member above, including the unselected
            // closure pins.  Only the reviewed compilation set is emitted as
            // semantic references; the path-set/hash/MVID checks still make
            // the complete closure tamper-evident before this projection.
            if (pin.Selected)
            {
                identities.Add(new(pin.Path, pin.Sha256, pin.Mvid));
                selectedReferences.Add(MetadataReference.CreateFromFile(path));
            }
        }

        string compilerPath = Path.GetFullPath(typeof(CSharpSyntaxTree).Assembly.Location);
        if (string.IsNullOrWhiteSpace(compilerPath) || !File.Exists(compilerPath))
            throw new ExtractionException("Stage F Roslyn compiler assembly path is unavailable.");
        (string compilerAssembly, string compilerMvid) = ReadCompilerReferenceMetadata(compilerPath, compilerPath);
        _ = compilerAssembly;
        identities.Add(new(compilerPath, EvmFrameDriverProfile.Hash(File.ReadAllBytes(compilerPath)), compilerMvid));
        identities = [.. identities.OrderBy(static identity => identity.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static identity => identity.Path, StringComparer.Ordinal)];
        if (identities.Select(static identity => identity.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != identities.Count)
            throw new ExtractionException("Stage F compiler reference identity paths are duplicated.");
        if (selectedReferences.Count != SelectedCompilerReferenceCount ||
            identities.Count != EmittedCompilerReferenceCount)
            throw new ExtractionException("Stage F compiler reference selection changed from the reviewed closure.");
        return new(selectedReferences.ToArray(), identities.ToArray());
    }

    private static bool IsManagedAssembly(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using PEReader reader = new(stream);
            return reader.HasMetadata;
        }
        catch (Exception exception)
        {
            throw new ExtractionException("Stage F runtime platform metadata inspection failed for " + path +
                ": " + exception.Message);
        }
    }

    private static string ResolveCompilerReferencePath(string root, string platformDirectory, string logicalPath) =>
        logicalPath.StartsWith("platform/", StringComparison.Ordinal)
            ? Path.Combine(platformDirectory, logicalPath["platform/".Length..])
            : EvmFrameDriverProfile.ResolveCanonical(root, logicalPath);

    private static (string AssemblyName, string Mvid) ReadCompilerReferenceMetadata(string logicalPath, string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using PEReader peReader = new(stream);
            if (!peReader.HasMetadata)
                throw new ExtractionException("Stage F compiler reference has no managed metadata: " + logicalPath + ".");
            using MetadataReaderProvider provider = MetadataReaderProvider.FromMetadataImage(
                peReader.GetMetadata().GetContent());
            MetadataReader reader = provider.GetMetadataReader();
            AssemblyDefinition assembly = reader.GetAssemblyDefinition();
            return (reader.GetString(assembly.Name),
                reader.GetGuid(reader.GetModuleDefinition().Mvid).ToString("D"));
        }
        catch (ExtractionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ExtractionException("Stage F compiler reference metadata is invalid for " + logicalPath +
                ": " + exception.Message);
        }
    }

    private static void ValidateCompilerReferencePins(CompilerReferencePin[] references)
    {
        if (references.Length == 0)
            throw new ExtractionException("Stage F compiler reference inventory is empty.");
        string[] orderedPaths = references.Select(static reference => reference.Path)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static path => path, StringComparer.Ordinal).ToArray();
        if (!references.Select(static reference => reference.Path).SequenceEqual(orderedPaths, StringComparer.Ordinal))
            throw new ExtractionException("Stage F compiler reference inventory is not in canonical path order.");
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
        foreach (CompilerReferencePin reference in references)
        {
            string normalized = NormalizePath(reference.Path);
            if (!paths.Add(reference.Path) || reference.Path != normalized ||
                string.IsNullOrWhiteSpace(reference.AssemblyName) ||
                !reference.AssemblyName.Equals(Path.GetFileNameWithoutExtension(reference.Path),
                    StringComparison.OrdinalIgnoreCase) || !IsSha256(reference.Sha256) ||
                !Guid.TryParseExact(reference.Mvid, "D", out _))
                throw new ExtractionException("Stage F compiler reference inventory contains malformed identities.");
            bool platform = normalized.StartsWith("platform/", StringComparison.Ordinal);
            bool closure = CompilerReferenceClosurePaths.Any(relative =>
                normalized.StartsWith(NormalizePath(relative) + "/", StringComparison.Ordinal));
            if ((!platform && !closure) || (platform && Path.GetFileName(normalized["platform/".Length..]) !=
                normalized["platform/".Length..]))
                throw new ExtractionException("Stage F compiler reference inventory escapes the admitted closure: " +
                    reference.Path + ".");
            if (reference.Selected && !selected.Add(reference.AssemblyName))
                throw new ExtractionException("Stage F compiler reference inventory selects an assembly twice: " +
                    reference.AssemblyName + ".");
        }
        string[] missing = RequiredCompilerAssemblyNames.Where(name => !selected.Contains(name)).ToArray();
        if (missing.Length != 0)
            throw new ExtractionException("Stage F compiler reference inventory misses selected assemblies: " +
                string.Join(", ", missing) + ".");
    }

    private static string CompilerReferenceAggregateSha256(IEnumerable<CompilerReferencePin> references) =>
        EvmFrameDriverProfile.Hash(StrictUtf8.GetBytes(string.Join('\n', references.Select(reference =>
            reference.Path + "\0" + reference.AssemblyName + "\0" + reference.Sha256 + "\0" +
            reference.Mvid + "\0" + reference.Selected)) + "\n"));

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private static List<ControlNodeBinding> ExtractControlTopology(MethodDeclarationSyntax method)
    {
        List<ControlNodeBinding> nodes = [];
        int nextId = 0;

        void Visit(SyntaxNode parent, string parentId, string inheritedArm)
        {
            foreach (SyntaxNode child in parent.ChildNodes())
            {
                string arm = ControlNodeArm(parent, child) ?? inheritedArm;
                if (ControlNodeKind(child) is string kind)
                {
                    string id = "n" + nextId.ToString("D3", CultureInfo.InvariantCulture);
                    nextId++;
                    nodes.Add(new(id, kind, parentId, arm, ControlNodeCondition(child),
                        EvmFrameDriverProfile.Hash(CompleteCanonical(child)),
                        ControlNodeOperations(child), EvmFrameDriverProfile.ExtractConditionIdentity(child), child));
                    Visit(child, id, "body");
                }
                else
                {
                    Visit(child, parentId, arm);
                }
            }
        }

        Visit(method, "root", "body");
        return nodes;
    }

    private static string? ControlNodeArm(SyntaxNode parent, SyntaxNode child) =>
        parent switch
        {
            IfStatementSyntax value when ReferenceEquals(child, value.Statement) => "then",
            IfStatementSyntax value when ReferenceEquals(child, value.Else) => "else",
            ElseClauseSyntax => "else",
            WhileStatementSyntax value when ReferenceEquals(child, value.Statement) => "body",
            ForStatementSyntax value when ReferenceEquals(child, value.Statement) => "body",
            ForEachStatementSyntax value when ReferenceEquals(child, value.Statement) => "body",
            ForEachVariableStatementSyntax value when ReferenceEquals(child, value.Statement) => "body",
            DoStatementSyntax value when ReferenceEquals(child, value.Statement) => "body",
            TryStatementSyntax value when ReferenceEquals(child, value.Block) => "try",
            TryStatementSyntax value when value.Catches.Any(catchClause => ReferenceEquals(catchClause, child)) => "catch",
            TryStatementSyntax value when ReferenceEquals(child, value.Finally) => "finally",
            CatchClauseSyntax value when ReferenceEquals(child, value.Block) => "catch",
            FinallyClauseSyntax value when ReferenceEquals(child, value.Block) => "finally",
            UsingStatementSyntax value when ReferenceEquals(child, value.Statement) => "body",
            _ => null,
        };

    private static string? ControlNodeKind(SyntaxNode node) => node switch
    {
        IfStatementSyntax => "if",
        WhileStatementSyntax => "while",
        ForStatementSyntax => "for",
        ForEachStatementSyntax or ForEachVariableStatementSyntax => "foreach",
        DoStatementSyntax => "do",
        TryStatementSyntax => "try",
        CatchClauseSyntax => "catch",
        FinallyClauseSyntax => "finally",
        ReturnStatementSyntax => "return",
        ContinueStatementSyntax => "continue",
        BreakStatementSyntax => "break",
        GotoStatementSyntax => "goto",
        ThrowStatementSyntax => "throw",
        LabeledStatementSyntax => "label",
        UsingStatementSyntax => "using",
        LocalDeclarationStatementSyntax value when value.UsingKeyword.IsKind(SyntaxKind.UsingKeyword) => "using",
        ExpressionStatementSyntax value when value.Expression.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>().Any() => "statement",
        LocalDeclarationStatementSyntax => "statement",
        _ => null,
    };

    private static string ControlNodeCondition(SyntaxNode node) => node switch
    {
        IfStatementSyntax value => Canonical(value.Condition),
        WhileStatementSyntax value => Canonical(value.Condition),
        ForStatementSyntax value => value.Condition is null ? "-" : Canonical(value.Condition),
        ForEachStatementSyntax value => Canonical(value.Expression),
        ForEachVariableStatementSyntax value => Canonical(value.Expression),
        DoStatementSyntax value => Canonical(value.Condition),
        CatchClauseSyntax value => (value.Declaration is null ? "-" : Canonical(value.Declaration)) +
            (value.Filter is null ? string.Empty : Canonical(value.Filter.FilterExpression)),
        ReturnStatementSyntax value => value.Expression is null ? "-" : Canonical(value.Expression),
        GotoStatementSyntax value => value.Expression is null ? "-" : Canonical(value.Expression),
        ThrowStatementSyntax value => value.Expression is null ? "-" : Canonical(value.Expression),
        LabeledStatementSyntax value => value.Identifier.ValueText,
        UsingStatementSyntax value => value.Expression is null
            ? (value.Declaration is null ? "-" : Canonical(value.Declaration))
            : Canonical(value.Expression),
        LocalDeclarationStatementSyntax value when value.UsingKeyword.IsKind(SyntaxKind.UsingKeyword) =>
            Canonical(value.Declaration),
        _ => "-",
    };

    private static string[] ControlNodeOperations(SyntaxNode node) => [..
        node.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
            .Select(static invocation => "call:" + InvocationName(invocation.Expression))];

    private static string InvocationName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => InvocationName(member.Name),
        GenericNameSyntax generic => generic.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        _ => string.Empty,
    };

    private static bool MethodMatchesAdmission(MethodDeclarationSyntax method, MemberIdentity admission) =>
        method.Identifier.ValueText == admission.Member &&
        (method.TypeParameterList?.Parameters.Count ?? 0) == admission.GenericArity &&
        ParameterTypes(method) == admission.ParameterTypes &&
        method.Kind().ToString() == admission.SyntaxKind;

    private static string ParameterTypes(MethodDeclarationSyntax method) => string.Join(",",
        method.ParameterList.Parameters.Select(static parameter =>
            string.Concat(parameter.Modifiers.Select(static modifier => modifier.Text)) +
            Canonical(parameter.Type!)));

    private static string Canonical(SyntaxNode node) => string.Concat(
        node.DescendantTokens(descendIntoTrivia: false).Select(static token => token.Text));

    private static byte[] CompleteCanonical(SyntaxNode node)
    {
        StringBuilder result = new();
        foreach (SyntaxToken token in node.DescendantTokens(descendIntoTrivia: false))
            result.Append('T').Append(token.RawKind).Append(':').Append(token.Text).Append('\0');
        return StrictUtf8.GetBytes(result.ToString());
    }

    private static bool HasErrorSymbol(ISymbol symbol)
    {
        if (symbol is IErrorTypeSymbol || symbol.Kind == SymbolKind.ErrorType)
            return true;
        if (symbol is IMethodSymbol method &&
            (HasErrorType(method.ReturnType) || method.Parameters.Any(parameter => HasErrorType(parameter.Type)) ||
             method.TypeParameters.Any(parameter => parameter.ConstraintTypes.Any(HasErrorType)) ||
             method.ContainingType is not null && HasErrorType(method.ContainingType)))
            return true;
        for (ISymbol? current = symbol.ContainingSymbol; current is not null; current = current.ContainingSymbol)
            if (current is IErrorTypeSymbol || current.Kind == SymbolKind.ErrorType ||
                current is ITypeSymbol type && HasErrorType(type))
                return true;
        return false;
    }

    private static bool HasErrorType(ITypeSymbol type)
    {
        if (type is IErrorTypeSymbol || type.TypeKind == TypeKind.Error)
            return true;
        if (type is INamedTypeSymbol named &&
            (named.TypeArguments.Any(HasErrorType) || named.ContainingType is not null && HasErrorType(named.ContainingType)))
            return true;
        if (type is IArrayTypeSymbol array)
            return HasErrorType(array.ElementType);
        if (type is IPointerTypeSymbol pointer)
            return HasErrorType(pointer.PointedAtType);
        return false;
    }

    private static bool SymbolOwnerMatches(IMethodSymbol symbol, string ownerPath)
    {
        string[] expected = ownerPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        INamedTypeSymbol? containingType = symbol.ContainingType;
        for (int index = expected.Length - 1; index >= 0; index--)
        {
            if (containingType is null || containingType.MetadataName != expected[index])
                return false;
            containingType = containingType.ContainingType;
        }
        return containingType is null;
    }

    private static OperationalAdapterIdentity[] Adapters(OperationalDependencyIdentity[] dependencies)
    {
        string[] opcodeTheorems = [.. dependencies
            .Where(static dependency => dependency.Stage is "Stage A/opcode" or "Stage A routing")
            .Select(static dependency => dependency.Theorem)];
        string[] precompileTheorems = [.. dependencies
            .Where(static dependency => dependency.Stage is "Stage B precompile" or "Stage C precompile" or
                "Precompile pricing")
            .Select(static dependency => dependency.Theorem)];
        string[] settlementTheorems = [.. dependencies
            .Where(static dependency => dependency.Stage is "FrameJournal" or "WorldJournal" or "State gas" or
                "Precompile pricing" or "Stage D settlement")
            .Select(static dependency => dependency.Theorem)];
        string[] createTheorems = [.. dependencies
            .Where(static dependency => dependency.Theorem.Contains("CallCreate", StringComparison.Ordinal) ||
                dependency.Stage == "Stage D settlement")
            .Select(static dependency => dependency.Theorem)];
        return
        [
            Adapter("clearReturnData", "ExecuteTransaction fresh branch", "returnDataBuffer", "source-derived structural adapter; production binding unresolved", "none -> incomplete"),
            Adapter("prepareFresh", "ExecuteTransaction fresh branch", "VmState preparation/transfer/access", "unresolved", "none -> incomplete"),
            Adapter("prepareContinuation", "ExecuteTransaction continuation branch", "LIFO parent stack shape/call depth; continuation PC, gas, stack, memory, world, and output remain unresolved", "source-derived structural adapter; production binding unresolved", "none -> incomplete"),
            Adapter("runNoTrace", "RunDispatchLoop !traced && !cancelable", "adapter-supplied route/PC evidence with cardinality/byte-alignment checks, plus successor/fuel accounting", "unresolved", "none -> incomplete", opcodeTheorems),
            Adapter("runNoTraceCancelable", "RunDispatchLoop !traced && cancelable", "adapter-supplied route/PC evidence, entry/post-batch cancellation, and 1024-op batches", "unresolved", "none -> incomplete", opcodeTheorems),
            Adapter("runTraced", "RunDispatchLoop traced && !cancelable", "instruction trace, adapter-supplied route/PC evidence, and handler chain", "unresolved", "none -> incomplete", opcodeTheorems),
            Adapter("runTracedCancelable", "RunDispatchLoop traced && cancelable", "trace, adapter-supplied route/PC evidence, and entry/post-batch cancellation", "unresolved", "none -> incomplete", opcodeTheorems),
            Adapter("runFullPrecompile", "ExecuteTransaction IsPrecompile branch", "full-frame precompile result and gas/output effects", "unresolved", "none -> incomplete", precompileTheorems),
            Adapter("failureResult", "HandleException/HandleFailure", "exception gas clear, rollback, tracing, status", "unresolved", "none -> incomplete", settlementTheorems),
            Adapter("precompileFailureResult", "ExecutePrecompile failure routes", "out-of-gas, returned failure, managed exception", "unresolved", "none -> incomplete", precompileTheorems),
             Adapter("settleChild", "HandleRegularReturn/HandleCreate/HandleRevert", "FrameJournal, state gas, refund, world, output, parent LIFO", "unresolved", "none -> incomplete", settlementTheorems),
             Adapter("createDeposit", "HandleCreate/TryChargeAndDepositCode", "post-child-success code validity, deposit charge/insertion/deletion, and matching parent-continuation success/failure marker; collision delegated to CALL/CREATE", "unresolved", "none -> incomplete", createTheorems),
             Adapter("prepareTopLevelSubstate", "PrepareTopLevelSubstate", "full TransactionSubstate status/error/substate-error/should-revert/output/refund/log/destroy/RIPEMD fields", "unresolved", "none -> incomplete", settlementTheorems),
             Adapter("settleTopLevel", "ExecuteTransaction no-parent settlement", "top-level substate commit/status/tracing and output copy", "unresolved", "none -> incomplete", settlementTheorems),
             Adapter("cleanup", "FrameCleanupScope.Dispose", "DisposeActiveFrames and state-stack disposal", "unresolved", "none -> incomplete"),
        ];
    }

    private static OperationalAdapterIdentity Adapter(string name, string boundary, string surface,
        string status, string failureMode, params string[] acceptedTheorems) =>
        new(name, boundary, surface, status, failureMode, acceptedTheorems,
            "prove source call and output/effect equality on an admitted typed domain; prove Option presence on every admitted invocation");

    private static OperationalDependencyIdentity[] ResolveDependencies(string root)
    {
        string machineManifest = EvmFrameDriverProfile.ResolveCanonical(root, FrameMachineManifestPath);
        RequireHash(machineManifest, FrameMachineManifestSha256, FrameMachineManifestPath);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(machineManifest));
        if (!document.RootElement.TryGetProperty("dependencies", out JsonElement dependencies) ||
            dependencies.ValueKind != JsonValueKind.Array)
            throw new ExtractionException("Stage F cannot consume the Stage A dependency manifest.");

        List<OperationalDependencyIdentity> result = [];
        foreach (JsonElement dependency in dependencies.EnumerateArray())
        {
            if (!dependency.TryGetProperty("proofModulePath", out JsonElement proofPathElement) ||
                proofPathElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(proofPathElement.GetString()) ||
                !dependency.TryGetProperty("requiredTheorems", out JsonElement theoremArray) ||
                theoremArray.ValueKind != JsonValueKind.Array)
                continue;
            string manifestPath = RequiredString(dependency, "path");
            string manifestSha = RequiredString(dependency, "sha256");
            string proofPath = proofPathElement.GetString()!;
            string proofSha = RequiredString(dependency, "proofModuleSha256");
            string name = RequiredString(dependency, "name");
            ValidateDependencyFiles(root, manifestPath, manifestSha, proofPath, proofSha);
            foreach (JsonElement theorem in theoremArray.EnumerateArray())
            {
                string theoremName = RequiredString(theorem, "fullyQualifiedName");
                string signatureSha = RequiredString(theorem, "signatureSha256");
                RequireTheorem(root, proofPath, theoremName, signatureSha);
                result.Add(new("Stage A/opcode", name, manifestPath, manifestSha, proofPath, proofSha,
                    theoremName, signatureSha, "accepted opcode operational package"));
            }
        }

        foreach ((string stage, string manifestPath, string manifestSha, string proofPath, string proofSha,
                  string theorem, string? expectedSignatureSha, string binding) in AdditionalDependencies)
        {
            ValidateDependencyFiles(root, manifestPath, manifestSha, proofPath, proofSha);
            string signatureSha = RequireTheorem(root, proofPath, theorem, expectedSignatureSha);
            result.Add(new(stage, Path.GetFileNameWithoutExtension(manifestPath), manifestPath, manifestSha,
                proofPath, proofSha, theorem, signatureSha, binding));
        }
        if (result.Count < 24)
            throw new ExtractionException("Stage F accepted dependency closure is incomplete.");
        return result.ToArray();
    }

    private static void ValidateDependencyFiles(string root, string manifestPath, string manifestSha,
        string proofPath, string proofSha)
    {
        string manifest = EvmFrameDriverProfile.ResolveCanonical(root, manifestPath);
        RequireHash(manifest, manifestSha, manifestPath);
        string proof = EvmFrameDriverProfile.ResolveCanonical(root, proofPath);
        RequireHash(proof, proofSha, proofPath);
        ValidateManifestArtifacts(root, manifestPath);
    }

    private static void ValidateManifestArtifacts(string root, string manifestPath)
    {
        string manifest = EvmFrameDriverProfile.ResolveCanonical(root, manifestPath);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifest));
        ValidateManifestArtifact(document.RootElement, "ir", root, manifestPath);
        ValidateManifestArtifact(document.RootElement, "lean", root, manifestPath);
        ValidateManifestArtifact(document.RootElement, "artifact", root, manifestPath);
        ValidateManifestArtifact(document.RootElement, "leanArtifact", root, manifestPath);
        if (document.RootElement.TryGetProperty("source", out JsonElement source))
            ValidatePathHash(root, manifestPath, source);
        if (document.RootElement.TryGetProperty("supportingSources", out JsonElement supporting) &&
            supporting.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in supporting.EnumerateArray())
                ValidatePathHash(root, manifestPath, item);
    }

    private static void ValidateManifestArtifact(JsonElement rootElement, string property, string root,
        string manifestPath)
    {
        if (rootElement.TryGetProperty(property, out JsonElement artifact) &&
            artifact.ValueKind == JsonValueKind.Object)
            ValidatePathHash(root, manifestPath, artifact);
    }

    private static void ValidatePathHash(string root, string manifestPath, JsonElement value)
    {
        if (!value.TryGetProperty("path", out JsonElement pathElement) ||
            !value.TryGetProperty("sha256", out JsonElement hashElement) ||
            pathElement.ValueKind != JsonValueKind.String || hashElement.ValueKind != JsonValueKind.String)
            return;
        string relative = pathElement.GetString()!;
        string relativePath = relative.Replace('\\', '/');
        string manifestDirectory = (Path.GetDirectoryName(manifestPath) ?? string.Empty).Replace('\\', '/');
        string? candidate = relativePath.StartsWith("tools/", StringComparison.Ordinal)
            ? TryResolve(root, relativePath)
            : TryResolve(root, string.IsNullOrEmpty(manifestDirectory)
                ? relativePath
                : manifestDirectory + "/" + relativePath);
        candidate ??= TryResolve(root, relativePath);
        if (candidate is null)
        {
            string generatedMarker = "/Generated/";
            int marker = manifestPath.LastIndexOf(generatedMarker, StringComparison.Ordinal);
            if (marker >= 0)
                candidate = TryResolve(root, manifestPath[..marker] + relativePath);
        }
        if (candidate is null || !EvmFrameDriverProfile.Hash(File.ReadAllBytes(candidate))
                .Equals(hashElement.GetString(), StringComparison.Ordinal))
            throw new ExtractionException("Stage F dependency artifact hash changed for " + relative + ".");
    }

    private static string? TryResolve(string root, string relative)
    {
        try { return File.Exists(EvmFrameDriverProfile.ResolveCanonical(root, relative))
                ? EvmFrameDriverProfile.ResolveCanonical(root, relative) : null; }
        catch (ExtractionException) { return null; }
    }

    private static string RequireTheorem(string root, string proofPath, string theorem, string? expectedSignatureSha)
    {
        string text = StrictUtf8.GetString(File.ReadAllBytes(EvmFrameDriverProfile.ResolveCanonical(root, proofPath)));
        string signature = ExtractLeanTheoremSignature(text, theorem);
        string signatureSha = EvmFrameDriverProfile.Hash(StrictUtf8.GetBytes(signature));
        if (expectedSignatureSha is not null && !signatureSha.Equals(expectedSignatureSha, StringComparison.Ordinal))
            throw new ExtractionException("Stage F accepted theorem signature changed: " + theorem + ".");
        return signatureSha;
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

    private static string RequiredString(JsonElement value, string property) =>
        value.TryGetProperty(property, out JsonElement element) && element.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(element.GetString())
            ? element.GetString()!
            : throw new ExtractionException("Stage F dependency field is missing: " + property + ".");

    private static void RequireHash(string path, string expected, string description)
    {
        if (!EvmFrameDriverProfile.Hash(File.ReadAllBytes(path)).Equals(expected, StringComparison.Ordinal))
            throw new ExtractionException("Stage F hash changed for " + description + ".");
    }

    private static void ValidateIr(OperationalIrDocument ir, OperationalDependencyIdentity[] dependencies)
    {
        if (ir.SchemaVersion != 1 || ir.ExtractorVersion != ExtractorVersion || ir.Kernel != KernelName ||
            ir.AcceptanceState != AcceptanceState || ir.StageEBoundary != StageEBoundary ||
            ir.TargetRoot != EvmFrameDriverProfile.TargetRoot ||
            ir.TargetFork != EvmFrameDriverProfile.TargetFork ||
            ir.TargetGasPolicy != EvmFrameDriverProfile.TargetGasPolicy ||
            ir.StageEIrSha256 != AcceptedStageEIrSha256 ||
            ir.Loop.BatchLimit != 1024 || ir.Loop.DispatchModes.Length != 4 ||
             !ir.Loop.DispatchModes.SequenceEqual(
                 ["NoTrace", "NoTraceCancelable", "Traced", "TracedCancelable"], StringComparer.Ordinal) ||
             ir.Loop.EntryTransitions.Length < 3 || ir.Loop.BytecodeOutcomes.Length < 5 ||
             ir.Loop.SettlementRoutes.Length < 5 || ir.Loop.StateEffects.Length < 8 ||
             ir.Loop.CleanupRoutes.Length < 4 || ir.Loop.FuelRules.Length < 4 ||
             string.IsNullOrWhiteSpace(ir.Loop.Measure) || string.IsNullOrWhiteSpace(ir.Loop.AdequacyStatus) ||
             ir.ControlPlan.Length != 5 || !ir.ControlPlan.Select(static step => step.Stage).SequenceEqual(
                 ["prepare", "dispatch", "classify", "settle", "cleanup"], StringComparer.Ordinal) ||
             ir.ControlPlan.Any(static step => step.Actions is null || step.Actions.Length == 0 ||
                 step.Actions.Distinct().Count() != step.Actions.Length ||
                 step.Actions.Any(static action => !Enum.IsDefined(action))) ||
              ir.Topology.Length == 0 || ir.BranchBindings.Length != ExpectedOperationalBranchBindingCount || ir.OpcodeRoutes.Length != 1024 ||
             ir.PrecompileRoutes.Length != 18 || ir.Adapters.Length < 15 || ir.MutationVectors.Length < 6 ||
             dependencies.Length < 24 ||
            ir.CompilerReferences.Length != 330 ||
            ir.CompilerReferences.Select(static reference => reference.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != ir.CompilerReferences.Length ||
            ir.CompilerReferences.Any(static reference =>
                !IsSha256(reference.Sha256) || !Guid.TryParseExact(reference.ModuleMvid, "D", out _)) ||
            ir.CompilerReferences.All(reference => !reference.Path.Equals(
                Path.GetFullPath(typeof(CSharpSyntaxTree).Assembly.Location), StringComparison.OrdinalIgnoreCase)) ||
            !ir.CompilerReferences.Single(reference => reference.Path.Equals(
                Path.GetFullPath(typeof(CSharpSyntaxTree).Assembly.Location), StringComparison.OrdinalIgnoreCase))
                .ModuleMvid.Equals(ir.CompilerMvid, StringComparison.OrdinalIgnoreCase))
            throw new ExtractionException("Stage F operational IR is incomplete or reordered.");
        string[] admittedTheorems = dependencies.Select(static dependency => dependency.Theorem).ToArray();
        if (ir.Sources.Length != ExpectedOperationalSourceCount ||
            ir.Members.Length != ExpectedOperationalMemberCount ||
            ir.SourceControl.Length != 4 ||
            !ir.SourceControl.Select(static control => control.Member).SequenceEqual(
                ["ExecuteTransaction", "RunByteCode", "RunDispatchLoop", "Dispose"], StringComparer.Ordinal) ||
            ir.SourceControl.Any(static control => control.Member == "ExecuteCall") ||
             ir.BranchBindings.Any(static binding => string.IsNullOrWhiteSpace(binding.Invocation) ||
                 binding.Effects is null || binding.Effects.Length == 0 ||
                 binding.Effects.Any(string.IsNullOrWhiteSpace) || string.IsNullOrWhiteSpace(binding.Settlement) ||
                 !Enum.IsDefined(binding.Predicate) || binding.EffectKinds is null ||
                 binding.EffectKinds.Length != binding.Effects.Length ||
                 binding.EffectKinds.Any(static effect => !Enum.IsDefined(effect)) ||
                 !Enum.IsDefined(binding.SettlementKind) ||
                 binding.Stages is null || binding.Stages.Length == 0 ||
                 binding.Actions is null || binding.Actions.Length != binding.Stages.Length ||
                 binding.Actions.Any(static action => !Enum.IsDefined(action)) ||
                 binding.Stages.Any(static stage => stage is not ("prepare" or "dispatch" or "classify" or "settle" or "cleanup"))) ||
            !ir.Adapters.Any(static adapter => adapter.Name == "createDeposit") ||
            !ir.Adapters.Any(static adapter => adapter.Name == "prepareTopLevelSubstate") ||
            !ir.Adapters.Any(static adapter => adapter.Name == "settleTopLevel") ||
            ir.Adapters.Any(adapter => adapter.AcceptedTheorems.Any(theorem =>
                !admittedTheorems.Contains(theorem, StringComparer.Ordinal))))
            throw new ExtractionException("Stage F did not preserve the accepted source topology boundary.");
        ValidateOperationalTopology(ir);
        ValidateOperationalRoutes(ir);
        if (ir.ExplicitOpenObligations.Length < 5 || ir.ReviewedOperationalBindings.Length < 5)
            throw new ExtractionException("Stage F open operational obligations are not visible.");
        ValidateSourceActions(ir.BranchBindings);
        ValidateSourceActionEvidence(ir);
        ValidateControlPlanActions(ir);
    }

    private static void ValidateOperationalTopology(OperationalIrDocument ir)
    {
        OperationalTopologyNodeIdentity[] expectedTopology =
            [.. ir.SourceControl.SelectMany(control => control.Topology.Select(node =>
                new OperationalTopologyNodeIdentity(control.Member, node.Id, node.Kind, node.ParentId,
                    node.Arm, node.Condition, node.Sha256, node.Operations)))];
        if (!ir.Topology.SequenceEqual(expectedTopology))
            throw new ExtractionException("Stage F operational topology drifted from the source control anchors.");
        if (ir.Topology.Select(static node => node.Member + ":" + node.Id)
                .Distinct(StringComparer.Ordinal).Count() != ir.Topology.Length ||
            ir.Topology.Any(static node => string.IsNullOrWhiteSpace(node.Member) ||
                string.IsNullOrWhiteSpace(node.Id) || string.IsNullOrWhiteSpace(node.Kind) ||
                string.IsNullOrWhiteSpace(node.ParentId) || string.IsNullOrWhiteSpace(node.Arm) ||
                string.IsNullOrWhiteSpace(node.Condition) || !IsSha256(node.Sha256) ||
                node.Operations is null))
            throw new ExtractionException("Stage F operational topology contains an unbound or duplicate node.");
        if (ir.BranchBindings.Select(static binding => binding.Branch + ":" + binding.Member + ":" +
                    binding.NodeId + ":" + binding.BindingArm)
                .Distinct(StringComparer.Ordinal).Count() != ir.BranchBindings.Length ||
            ir.BranchBindings.Any(binding =>
                 string.IsNullOrWhiteSpace(binding.Branch) || string.IsNullOrWhiteSpace(binding.Member) ||
                  string.IsNullOrWhiteSpace(binding.NodeId) || string.IsNullOrWhiteSpace(binding.Kind) ||
                  string.IsNullOrWhiteSpace(binding.SourceArm) || !IsSha256(binding.SourceSha256) ||
                  string.IsNullOrWhiteSpace(binding.BindingArm) ||
                  string.IsNullOrWhiteSpace(binding.Invocation) || binding.Effects is null ||
                  binding.Effects.Length == 0 || binding.Effects.Any(string.IsNullOrWhiteSpace) ||
                  string.IsNullOrWhiteSpace(binding.Settlement) ||
                  !Enum.IsDefined(binding.Predicate) || binding.EffectKinds is null ||
                  binding.EffectKinds.Length != binding.Effects.Length ||
                  binding.EffectKinds.Any(static effect => !Enum.IsDefined(effect)) ||
                  !Enum.IsDefined(binding.SettlementKind) ||
                  binding.Stages is null || binding.Stages.Length == 0 || binding.Actions is null ||
                  binding.Actions.Length != binding.Stages.Length ||
                  binding.Actions.Any(static action => !Enum.IsDefined(action)) ||
                  binding.Stages.Distinct(StringComparer.Ordinal).Count() != binding.Stages.Length ||
                  binding.Stages.Any(static stage => stage is not ("prepare" or "dispatch" or "classify" or "settle" or "cleanup")) ||
                  !ir.Topology.Any(node => node.Member == binding.Member && node.Id == binding.NodeId &&
                     node.Kind == binding.Kind && node.Arm == binding.SourceArm &&
                     node.Sha256 == binding.SourceSha256)))
            throw new ExtractionException("Stage F branch binding is not attached to the typed source topology.");
    }

    private static void ValidateSourceActions(OperationalBranchBindingIdentity[] bindings)
    {
        foreach (OperationalBranchBindingIdentity binding in bindings)
        {
            if (binding.Predicate != ParsePredicate(binding.Invocation) ||
                binding.SettlementKind != ParseSettlement(binding.Settlement) ||
                !binding.EffectKinds.SequenceEqual(binding.Effects.Select(ParseEffect)))
                throw new ExtractionException("Stage F typed source control identity does not match its source labels: " +
                    binding.Branch + ".");
            for (int index = 0; index < binding.Stages.Length; index++)
                if (binding.Actions[index] != LowerAction(ParseStage(binding.Stages[index]), binding.Predicate))
                    throw new ExtractionException("Stage F source action does not match its typed predicate/stage binding: " +
                        binding.Branch + " at " + binding.Stages[index] + ".");
        }
    }

    private static void ValidateControlPlanActions(OperationalIrDocument ir)
    {
        foreach (OperationalControlPlanStepIdentity step in ir.ControlPlan)
        {
            OperationalControlActionIdentity[] expected = [.. ir.BranchBindings
                .Where(binding => binding.Stages.Contains(step.Stage, StringComparer.Ordinal))
                .SelectMany(binding => binding.Stages.Select((stage, index) =>
                    (stage, action: binding.Actions[index])))
                .Where(pair => pair.stage == step.Stage)
                .Select(pair => pair.action)
                .Distinct()];
            if (!step.Actions.SequenceEqual(expected))
                throw new ExtractionException("Stage F typed control-plan action set is not source-derived for " +
                    step.Stage + ".");
        }
    }

    private static bool HasOperation(OperationalTopologyNodeIdentity node, string operation) =>
        node.Operations.Contains("call:" + operation, StringComparer.Ordinal);

    private static bool HasOperations(OperationalTopologyNodeIdentity node, params string[] operations) =>
        operations.All(operation => HasOperation(node, operation));

    private static bool IsBindingArm(OperationalBranchBindingIdentity binding, params string[] arms) =>
        arms.Contains(binding.BindingArm, StringComparer.Ordinal);

    private static bool SourceActionEvidenceMatches(
        OperationalBranchBindingIdentity binding,
        OperationalTopologyNodeIdentity node,
        OperationalControlActionIdentity action) => action switch
        {
            OperationalControlActionIdentity.PrepareFresh =>
                binding.Member == "ExecuteTransaction" && node.Id == "n005" && node.Kind == "if" &&
                node.Condition == "!_currentState.IsContinuation" && binding.SourceArm == "body" &&
                binding.BindingArm == "then",
            OperationalControlActionIdentity.PrepareContinuation =>
                binding.Member == "ExecuteTransaction" && node.Id == "n005" && node.Kind == "if" &&
                node.Condition == "!_currentState.IsContinuation" && binding.SourceArm == "body" &&
                binding.BindingArm == "else",
            OperationalControlActionIdentity.DispatchBytecode =>
                (binding.BindingArm == "else" && binding.Member == "ExecuteTransaction" &&
                 node.Id == "n010" && node.Kind == "if" && node.Condition == "_currentState.IsPrecompile") ||
                (binding.BindingArm == "node" && binding.Member == "RunByteCode" &&
                 node.Id == "n001" && HasOperation(node, "RunDispatchLoop")),
            OperationalControlActionIdentity.DispatchFullPrecompile =>
                (binding.BindingArm == "then" && binding.Member == "ExecuteTransaction" &&
                 node.Id == "n010" && node.Kind == "if" && node.Condition == "_currentState.IsPrecompile") ||
                (binding.BindingArm == "node" && binding.Member == "ExecuteTransaction" &&
                 node.Id == "n011" && HasOperation(node, "ExecutePrecompile")),
            OperationalControlActionIdentity.PrecompileFailure =>
                binding.Member == "ExecuteTransaction" && node.Id == "n011" &&
                HasOperation(node, "ExecutePrecompile") &&
                IsBindingArm(binding, "outOfGas", "returnedFailure", "managedException"),
            OperationalControlActionIdentity.ClassifyContinue =>
                binding.Member == "ExecuteTransaction" && node.Id == "n020" && node.Kind == "if" &&
                node.Condition == "!callResult.IsReturn" && binding.BindingArm == "continue",
            OperationalControlActionIdentity.ClassifySuspend =>
                binding.Member == "ExecuteTransaction" && node.Id == "n021" &&
                HasOperation(node, "PrepareNextCallFrame") && binding.BindingArm == "node",
            OperationalControlActionIdentity.ClassifyHalt =>
                binding.Member == "ExecuteTransaction" &&
                IsBindingArm(binding, "regular", "create", "node", "revert") &&
                node.Kind is "if" or "statement",
            OperationalControlActionIdentity.SettleNestedRegularSuccess =>
                binding.Member == "ExecuteTransaction" && node.Id == "n038" &&
                HasOperation(node, "IncorporateChildStateGasRefunds") && binding.BindingArm == "regular",
            OperationalControlActionIdentity.SettleNestedCreateSuccess =>
                binding.Member == "ExecuteTransaction" && node.Id == "n041" &&
                HasOperations(node, "PrepareCreateData", "HandleCreate") && binding.BindingArm == "node",
            OperationalControlActionIdentity.SettleNestedCreateInvalidCode =>
                binding.Member == "ExecuteTransaction" && node.Id == "n044" &&
                HasOperation(node, "HandleCreate") && binding.BindingArm == "invalidCode",
            OperationalControlActionIdentity.SettleNestedCreateOutOfGas =>
                binding.Member == "ExecuteTransaction" && node.Id == "n044" &&
                HasOperation(node, "HandleCreate") && binding.BindingArm == "outOfGas",
            OperationalControlActionIdentity.SettleNestedRevert =>
                binding.Member == "ExecuteTransaction" && node.Id == "n058" &&
                HasOperation(node, "HandleRevert") && binding.BindingArm == "node",
            OperationalControlActionIdentity.SettleNestedException =>
                binding.Member == "ExecuteTransaction" && node.Id == "n024" &&
                HasOperation(node, "HandleException") && binding.BindingArm == "node",
            OperationalControlActionIdentity.SettleResume =>
                binding.Member == "ExecuteTransaction" && node.Kind == "while" && node.Condition == "true" &&
                HasOperations(node, "PrepareNextCallFrame", "HandleRegularReturn", "HandleRevert", "HandleException") &&
                binding.BindingArm == "node",
            OperationalControlActionIdentity.SettleTopLevelSuccess =>
                binding.Member == "ExecuteTransaction" && node.Id == "n031" &&
                HasOperation(node, "PrepareTopLevelSubstate") && binding.BindingArm == "node",
            OperationalControlActionIdentity.SettleTopLevelRevert =>
                binding.Member == "ExecuteTransaction" && node.Id == "n031" &&
                HasOperation(node, "PrepareTopLevelSubstate") && binding.BindingArm == "revert",
            OperationalControlActionIdentity.SettleTopLevelException =>
                binding.Member == "ExecuteTransaction" && node.Id == "n063" &&
                HasOperation(node, "HandleFailure") && binding.BindingArm == "node",
            OperationalControlActionIdentity.CleanupCancelled =>
                binding.Member == "RunDispatchLoop" && HasOperation(node, "ThrowOperationCanceledException") &&
                binding.BindingArm == "cancelled",
            OperationalControlActionIdentity.CleanupEscaped =>
                binding.BindingArm == "node" &&
                ((binding.Member == "ExecuteTransaction" && node.Kind == "try") ||
                 (binding.Member == "Dispose" && HasOperation(node, "DisposeActiveFrames"))),
            OperationalControlActionIdentity.CleanupInvalidControl =>
                binding.Member == "RunDispatchLoop" && node.Id == "n014" && node.Kind == "while" &&
                binding.BindingArm == "failClosed",
            OperationalControlActionIdentity.CleanupCompleted =>
                binding.Member == "Dispose" && node.Id == "n001" &&
                HasOperation(node, "DisposeActiveFrames") && binding.BindingArm == "node",
            _ => false,
        };

    private static void ValidateSourceActionEvidence(OperationalIrDocument ir)
    {
        foreach (OperationalBranchBindingIdentity binding in ir.BranchBindings)
        {
            OperationalTopologyNodeIdentity node = ir.Topology.SingleOrDefault(candidate =>
                candidate.Member == binding.Member && candidate.Id == binding.NodeId) ??
                throw new ExtractionException("Stage F source action has no topology node: " + binding.Branch + ".");
            for (int index = 0; index < binding.Stages.Length; index++)
                if (!SourceActionEvidenceMatches(binding, node, binding.Actions[index]))
                    throw new ExtractionException("Stage F typed action is not tied to the admitted Roslyn/CFG node: " +
                        binding.Branch + " at " + binding.Stages[index] + ".");
        }
    }

    private static void ValidateOperationalRoutes(OperationalIrDocument ir)
    {
        if (ir.OpcodeRoutes.GroupBy(static route => route.DispatchTable + ":" + route.Byte)
                 .Any(static group => group.Count() != 1) ||
             ir.OpcodeRoutes.Any(static route => route.Byte is < 0 or >= 256 ||
                 route.DispatchTable is not ("NoTrace" or "NoTraceCancelable" or "Traced" or "TracedCancelable") ||
                 route.RouteKind is not ("enabled" or "disabled" or "badInstruction") ||
                 !route.Admitted ||
                 string.IsNullOrWhiteSpace(route.Instruction) || string.IsNullOrWhiteSpace(route.ActivationRule) ||
                 string.IsNullOrWhiteSpace(route.Package) || string.IsNullOrWhiteSpace(route.ClosedHandlerRoot)))
            throw new ExtractionException("Stage F operational opcode routes are malformed or duplicated.");
        foreach (string table in new[] { "NoTrace", "NoTraceCancelable", "Traced", "TracedCancelable" })
            if (ir.OpcodeRoutes.Count(route => route.DispatchTable == table) != 256)
                throw new ExtractionException("Stage F operational opcode route table is not a complete 256-byte table.");
        if (ir.PrecompileRoutes.Select(static route => route.Address).Distinct().Count() != 18 ||
             ir.PrecompileRoutes.Any(static route => string.IsNullOrWhiteSpace(route.Name) ||
                 !route.Admitted ||
                 string.IsNullOrWhiteSpace(route.ActivationRule) || string.IsNullOrWhiteSpace(route.ProviderRoot) ||
                string.IsNullOrWhiteSpace(route.WrapperManifestPath) ||
                !IsSha256(route.WrapperManifestSha256) || string.IsNullOrWhiteSpace(route.LeanModule)))
            throw new ExtractionException("Stage F operational precompile routes are malformed or duplicated.");
    }

    private static OperationalSourceManifest BuildManifest(OperationalIrDocument ir, string irSha256,
        string leanSha256)
    {
        string sourceDigest = Digest(ir.Sources.Select(static source =>
            source.Role + "|" + source.Path + "|" + source.Sha256 + "|" + source.SyntaxSha256));
        string memberDigest = Digest(ir.Members.Select(static member =>
            member.SourcePath + "|" + member.OwnerPath + "|" + member.Member + "|" + member.GenericArity +
            "|" + member.ParameterTypes + "|" + member.SyntaxKind + "|" + member.Sha256));
        string controlDigest = Digest(ir.SourceControl.Select(static control =>
            control.SourcePath + "|" + control.OwnerPath + "|" + control.Member + "|" + control.Sha256 +
            "|" + control.TopologySha256).Concat(ir.ControlPlan.Select(static step =>
            step.Stage + "|" + step.Member + "|" + step.NodeId + "|" + step.Kind + "|" +
            step.SourceArm + "|" + step.SourceSha256 + "|" +
            string.Join(';', step.Actions.Select(static action => action.ToString())))).Concat(ir.Topology.Select(static node =>
            node.Member + "|" + node.Id + "|" + node.Kind + "|" + node.ParentId + "|" + node.Arm +
            "|" + node.Condition + "|" + node.Sha256 + "|" + string.Join(';', node.Operations)))
             .Concat(ir.BranchBindings.Select(static binding =>
                 binding.Branch + "|" + binding.Member + "|" + binding.NodeId + "|" + binding.Kind + "|" +
                 binding.SourceArm + "|" + binding.SourceSha256 + "|" + binding.BindingArm + "|" +
                 binding.Invocation + "|" + string.Join(';', binding.Effects) + "|" + binding.Settlement +
                  "|" + binding.Predicate + "|" + string.Join(';', binding.EffectKinds) +
                  "|" + binding.SettlementKind +
                  "|" + string.Join(';', binding.Actions.Select(static action => action.ToString())) +
                  "|" + string.Join(';', binding.Stages)))
            .Concat(ir.OpcodeRoutes.Select(static route =>
                route.DispatchTable + "|" + route.Byte + "|" + route.Instruction + "|" + route.RouteKind +
                "|" + route.ActivationRule + "|" + route.Package + "|" + route.ClosedHandlerRoot + "|" +
                route.Admitted))
            .Concat(ir.PrecompileRoutes.Select(static route =>
                route.Name + "|" + route.Address + "|" + route.ActivationRule + "|" + route.ProviderRoot +
                "|" + route.WrapperManifestPath + "|" + route.WrapperManifestSha256 + "|" + route.LeanModule +
                "|" + route.FullyQualifiedTheorem + "|" + route.Admitted))
            .Concat([
                "loop|" + ir.Loop.BatchLimit + "|" + string.Join(';', ir.Loop.DispatchModes) + "|" +
                string.Join(';', ir.Loop.EntryTransitions) + "|" + string.Join(';', ir.Loop.BytecodeOutcomes) + "|" +
                string.Join(';', ir.Loop.SettlementRoutes) + "|" + string.Join(';', ir.Loop.StateEffects) + "|" +
                string.Join(';', ir.Loop.CleanupRoutes) + "|" + string.Join(';', ir.Loop.FuelRules) + "|" +
                ir.Loop.Measure + "|" + ir.Loop.AdequacyStatus]));
        string dependencyDigest = Digest(ir.AcceptedDependencies.Select(static dependency =>
            dependency.Stage + "|" + dependency.Name + "|" + dependency.ManifestPath + "|" +
            dependency.ManifestSha256 + "|" + dependency.ProofPath + "|" + dependency.ProofSha256 + "|" +
            dependency.Theorem + "|" + dependency.SignatureSha256));
        return new(1, ExtractorVersion, ir.RoslynVersion, ir.CompilerMvid, ir.LanguageVersion, KernelName,
            AcceptanceState, ir.StageEIrSha256, ir.CompilerReferences, ir.Sources, ir.Members,
            ir.SourceControl, ir.ControlPlan, ir.Topology, ir.BranchBindings, ir.OpcodeRoutes,
            ir.PrecompileRoutes, ir.Loop,
            ir.AcceptedDependencies, new(IrFileName, irSha256), new(LeanFileName, leanSha256), sourceDigest,
            memberDigest, controlDigest, dependencyDigest, ir.ReviewedOperationalBindings,
            ir.ExplicitOpenObligations);
    }

    private static void ValidateManifest(string root, OperationalSourceManifest manifest, byte[] irBytes,
        byte[] leanBytes)
    {
        if (manifest.SchemaVersion != 1 || manifest.ExtractorVersion != ExtractorVersion ||
            manifest.Kernel != KernelName || manifest.AcceptanceState != AcceptanceState ||
            manifest.StageEIrSha256 != AcceptedStageEIrSha256 ||
            manifest.Ir.Path != IrFileName || manifest.Lean.Path != LeanFileName ||
            manifest.Ir.Sha256 != EvmFrameDriverProfile.Hash(irBytes) ||
            manifest.Lean.Sha256 != EvmFrameDriverProfile.Hash(leanBytes) ||
             manifest.Sources.Length != ExpectedOperationalSourceCount ||
             manifest.Members.Length != ExpectedOperationalMemberCount ||
             manifest.SourceControl.Length != 4 ||
             !manifest.SourceControl.Select(static control => control.Member).SequenceEqual(
                 ["ExecuteTransaction", "RunByteCode", "RunDispatchLoop", "Dispose"], StringComparer.Ordinal) ||
             manifest.AcceptedDependencies.Length < 24 || manifest.ControlPlan.Length != 5 ||
             manifest.ControlPlan.Any(static step => step.Actions is null || step.Actions.Length == 0 ||
                 step.Actions.Distinct().Count() != step.Actions.Length ||
                 step.Actions.Any(static action => !Enum.IsDefined(action))) ||
             manifest.Topology.Length == 0 || manifest.BranchBindings.Length != ExpectedOperationalBranchBindingCount ||
             manifest.BranchBindings.Any(static binding => !Enum.IsDefined(binding.Predicate) ||
                 binding.EffectKinds is null || binding.EffectKinds.Length != binding.Effects.Length ||
                 binding.EffectKinds.Any(static effect => !Enum.IsDefined(effect)) ||
                 !Enum.IsDefined(binding.SettlementKind) || binding.Actions is null ||
                 binding.Actions.Length != binding.Stages.Length ||
                 binding.Actions.Any(static action => !Enum.IsDefined(action))) ||
             manifest.OpcodeRoutes.Length != 1024 || manifest.PrecompileRoutes.Length != 18 ||
             manifest.Loop.BatchLimit != 1024 || manifest.Loop.DispatchModes.Length != 4 ||
             !manifest.Loop.DispatchModes.SequenceEqual(
                 ["NoTrace", "NoTraceCancelable", "Traced", "TracedCancelable"], StringComparer.Ordinal) ||
             manifest.CompilerReferences.Length != 330 || manifest.CompilerReferences.Any(static reference =>
                !IsSha256(reference.Sha256) || !Guid.TryParseExact(reference.ModuleMvid, "D", out _)) ||
            manifest.CompilerReferences.All(reference => !reference.Path.Equals(
                Path.GetFullPath(typeof(CSharpSyntaxTree).Assembly.Location), StringComparison.OrdinalIgnoreCase)) ||
            !manifest.CompilerReferences.Single(reference => reference.Path.Equals(
                Path.GetFullPath(typeof(CSharpSyntaxTree).Assembly.Location), StringComparison.OrdinalIgnoreCase))
                .ModuleMvid.Equals(manifest.CompilerMvid, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(manifest.CompilerMvid))
            throw new ExtractionException("Stage F source manifest is not bound to the operational artifacts.");
        if (!manifest.Sources.All(source => IsSha256(source.Sha256)) ||
            !manifest.Members.All(member => IsSha256(member.Sha256)) ||
            !manifest.AcceptedDependencies.All(dependency => IsSha256(dependency.ManifestSha256) &&
                IsSha256(dependency.ProofSha256) && IsSha256(dependency.SignatureSha256)) ||
            manifest.AcceptedDependencies.Select(static dependency => dependency.Theorem).Distinct(
                StringComparer.Ordinal).Count() != manifest.AcceptedDependencies.Length)
            throw new ExtractionException("Stage F manifest contains an invalid hash closure.");
        if (manifest.Topology is null || manifest.BranchBindings is null || manifest.OpcodeRoutes is null ||
            manifest.PrecompileRoutes is null || manifest.Loop is null)
            throw new ExtractionException("Stage F manifest is missing typed operational evidence.");
        _ = root;
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(static character =>
        character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static string Digest(IEnumerable<string> values) =>
        EvmFrameDriverProfile.Hash(StrictUtf8.GetBytes(string.Join('\n', values) + "\n"));

    internal static string OperationalEvidenceDigest(OperationalIrDocument ir) =>
        Digest(OperationalEvidenceLines(ir));

    private static IEnumerable<string> OperationalEvidenceLines(OperationalIrDocument ir) =>
        ir.ControlPlan.Select(static step =>
            "plan|" + step.Stage + "|" + step.Member + "|" + step.NodeId + "|" + step.Kind + "|" +
            step.SourceArm + "|" + step.SourceSha256 + "|" +
            string.Join(';', step.Actions.Select(static action => action.ToString())))
        .Concat(ir.Topology.Select(static node =>
            "topology|" + node.Member + "|" + node.Id + "|" + node.Kind + "|" + node.ParentId + "|" +
            node.Arm + "|" + node.Condition + "|" + node.Sha256 + "|" + string.Join(';', node.Operations)))
        .Concat(ir.BranchBindings.Select(static binding =>
            "branch|" + binding.Branch + "|" + binding.Member + "|" + binding.NodeId + "|" + binding.Kind +
            "|" + binding.SourceArm + "|" + binding.SourceSha256 + "|" + binding.BindingArm + "|" +
             binding.Invocation + "|" + string.Join(';', binding.Effects) + "|" + binding.Settlement +
            "|" + binding.Predicate + "|" + string.Join(';', binding.EffectKinds) +
            "|" + binding.SettlementKind +
            "|" + string.Join(';', binding.Actions.Select(static action => action.ToString())) +
            "|" + string.Join(';', binding.Stages)))
        .Concat(ir.OpcodeRoutes.Select(static route =>
            "opcode|" + route.DispatchTable + "|" + route.Byte + "|" + route.Instruction + "|" +
            route.RouteKind + "|" + route.ActivationRule + "|" + route.Package + "|" +
            route.ClosedHandlerRoot + "|" + route.Admitted))
        .Concat(ir.PrecompileRoutes.Select(static route =>
            "precompile|" + route.Name + "|" + route.Address + "|" + route.ActivationRule + "|" +
            route.ProviderRoot + "|" + route.WrapperManifestPath + "|" + route.WrapperManifestSha256 + "|" +
            route.LeanModule + "|" + route.FullyQualifiedTheorem + "|" + route.Admitted));

    private static byte[] Serialize<T>(T value) =>
        StrictUtf8.GetBytes(JsonSerializer.Serialize(value, JsonOptions) + "\n");

    private static void WriteIfChanged(string path, byte[] bytes)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            File.WriteAllBytes(path, bytes);
    }

    private sealed record CompilerReferencePin(
        string Path,
        string AssemblyName,
        string Sha256,
        string Mvid,
        bool Selected);

    private sealed record CompilerReferenceInventory(
        int SchemaVersion,
        int Count,
        string AggregateSha256,
        CompilerReferencePin[] References);

    private sealed record CompilerReferenceClosure(
        MetadataReference[] References,
        CompilerReferenceIdentity[] Identities);

    private sealed record ControlNodeBinding(
        string Id,
        string Kind,
        string ParentId,
        string Arm,
        string Condition,
        string Sha256,
        string[] Operations,
        ControlConditionIdentity ConditionIdentity,
        SyntaxNode Node);
}
