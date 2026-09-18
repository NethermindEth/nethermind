// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.EvmFrameDriverExtractor;

/// <summary>Resolves the exact admitted frame-driver source closure and emits deterministic artifacts.</summary>
internal static partial class EvmFrameDriverProfile
{
    internal const string Package = "tools/Evm/Lean/EvmFrameDriverExtractor/";
    internal const string IrFileName = "EvmFrameDriverKernel.ir.json";
    internal const string ManifestFileName = "EvmFrameDriverKernel.source-manifest.json";
    internal const string LeanFileName = "EvmFrameDriverKernel.lean";
    internal const string ExtractorVersion = "1.2.0-stage-e-source-attached-topology-audit";
    internal const string KernelName = "standard-mainnet-amsterdam-evm-frame-driver-one-step-stage-e";
    internal const string AcceptanceState = "stage-e-source-attached-parametric-shell";
    internal const string TargetFork = "Nethermind.Specs.Forks.Amsterdam";
    internal const string TargetGasPolicy = "Nethermind.Evm.GasPolicy.EthereumGasPolicy";
    internal const string TargetRoot =
        "Nethermind.Evm.VirtualMachine<TGasPolicy>.ExecuteTransaction<TTracingInst>(Nethermind.Evm.VmState<TGasPolicy>,Nethermind.Evm.State.IWorldState,Nethermind.Evm.Tracing.ITxTracer)";

    private const string AdmissionResourceSuffix = "Admission.ProductionClosure.txt";
    private const string AdmissionPath = "Admission/ProductionClosure.txt";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };
    private static readonly HashSet<string> SourceRoles = new(StringComparer.Ordinal)
    {
        "build", "frame", "dispatch", "frame-state", "settlement", "tracing",
    };
    private static readonly HashSet<string> DependencyKinds = new(StringComparer.Ordinal)
    {
        "generated", "proof",
    };
    private static readonly (string Name, string Path, string Theorem)[] AcceptedProofDependencies =
    [
        ("accepted-frame-machine-routing-proof",
            "tools/Evm/Lean/EvmFrameMachineExtractor/Refinement/StageARouting.lean",
            "EvmFrameMachineExtractor.Refinement.StageARouting.generated_route_lookup_refines_independent_spec"),
        ("accepted-frame-journal-proof",
            "tools/Evm/Lean/FrameJournalExtractor/Refinement/FrameJournal.lean",
            "FrameJournalExtractor.Refinement.FrameJournal.transition_refines"),
        ("accepted-precompile-full-frame-proof",
            "tools/Evm/Lean/PrecompileFullFrameExtractor/Refinement/PrecompileFullFrame.lean",
            "Eip803x.PrecompileFullFrame.Refinement.source_full_frame_refines_reference"),
        ("accepted-frame-driver-leaf-proof",
            "tools/Evm/Lean/EvmFrameControlSettlementExtractor/Refinement/FrameControlSettlement.lean",
            "EvmFrameControlSettlementExtractor.Refinement.hash_pinned_canonical_frame_control_settlement_single_iteration_control_agreement"),
    ];

    internal static ExtractionResult Extract(string repoRoot, string outputDirectory, string? leanOutputPath = null)
    {
        string root = Path.GetFullPath(repoRoot);
        Admission admission = ReadAdmission(root, true);
        (IrDocument ir, byte[] irBytes, byte[] leanBytes, byte[] manifestBytes) = BuildArtifacts(root, admission);
        Directory.CreateDirectory(outputDirectory);
        string irPath = Path.Combine(outputDirectory, IrFileName);
        string manifestPath = Path.Combine(outputDirectory, ManifestFileName);
        string leanPath = leanOutputPath ?? Path.Combine(outputDirectory, LeanFileName);
        WriteIfChanged(irPath, irBytes);
        WriteIfChanged(manifestPath, manifestBytes);
        WriteIfChanged(leanPath, leanBytes);
        return new(irPath, manifestPath, leanPath, ir.Sources.Length, ir.Members.Length,
            ir.Dependencies.Length, ir.SourceControl.Length, ir.Branches.Length,
            Hash(irBytes), Hash(manifestBytes), Hash(leanBytes));
    }

    internal static (IrDocument Ir, byte[] IrBytes, byte[] LeanBytes, byte[] ManifestBytes) BuildForTest(
        string repoRoot)
    {
        string root = Path.GetFullPath(repoRoot);
        return BuildArtifacts(root, ReadAdmission(root, false));
    }

    internal static void ValidateExistingArtifacts(string repoRoot, string artifactDirectory, string? leanPath = null)
    {
        string root = Path.GetFullPath(repoRoot);
        (IrDocument ir, byte[] expectedIr, byte[] expectedLean, byte[] expectedManifest) =
            BuildArtifacts(root, ReadAdmission(root, true));
        RequireEqual(Path.Combine(artifactDirectory, IrFileName), expectedIr, "IR");
        RequireEqual(Path.Combine(artifactDirectory, ManifestFileName), expectedManifest, "source manifest");
        RequireEqual(leanPath ?? Path.Combine(artifactDirectory, LeanFileName), expectedLean, "Lean");
        ValidateShape(ir);
    }

    internal static IrDocument DesignIr() => BuildDesign([], [], [], []);

    internal static void ValidateEmitterInput(IrDocument document, string irSha256)
    {
        ValidateShape(document);
        EvmFrameDriverLeanEmitter.ValidateSha256(irSha256, "Canonical IR");
        if (document.AcceptanceState != AcceptanceState || document.SourceControl.Length != 4 ||
            document.ControlPlan.Length != 5 || document.Branches.Length < 18 ||
            document.Boundary.LeafBindings.Length < 4)
            throw new ExtractionException("Stage E emitter input is not an admitted source-attached one-step profile.");
    }

    private static (IrDocument Ir, byte[] IrBytes, byte[] LeanBytes, byte[] ManifestBytes) BuildArtifacts(
        string root, Admission admission)
    {
        List<SourceIdentity> sources = [];
        Dictionary<string, SyntaxTree> trees = new(StringComparer.Ordinal);
        foreach (SourceAdmission source in admission.Sources)
        {
            string path = ResolveCanonical(root, source.Path);
            byte[] bytes = File.ReadAllBytes(path);
            string actualHash = Hash(bytes);
            if (!actualHash.Equals(source.Sha256, StringComparison.Ordinal))
                throw new ExtractionException("Stage E source hash changed for " + source.Path + ".");
            if (!source.Path.EndsWith(".cs", StringComparison.Ordinal))
            {
                sources.Add(new(source.Role, source.Path, actualHash, actualHash));
                continue;
            }
            SyntaxTree tree = CSharpSyntaxTree.ParseText(StrictUtf8.GetString(bytes),
                new CSharpParseOptions(LanguageVersion.CSharp14, DocumentationMode.Parse, SourceCodeKind.Regular),
                source.Path, StrictUtf8);
            foreach (Diagnostic diagnostic in tree.GetDiagnostics())
                if (diagnostic.Severity == DiagnosticSeverity.Error)
                    throw new ExtractionException("Stage E Roslyn parse failed for " + source.Path + ".");
            trees.Add(source.Path, tree);
            sources.Add(new(source.Role, source.Path, actualHash,
                Hash(CompleteCanonical(tree.GetCompilationUnitRoot()))));
        }

        List<MemberIdentity> members = [];
        Dictionary<string, MemberDeclarationSyntax> resolvedMembers = new(StringComparer.Ordinal);
        foreach (MemberAdmission selector in admission.Members)
        {
            if (!trees.TryGetValue(selector.Path, out SyntaxTree? tree))
                throw new ExtractionException("Stage E member is outside the source closure: " + selector.Path + ".");
            MemberDeclarationSyntax member = ResolveMember(tree.GetCompilationUnitRoot(), selector);
            string actualKind = member.Kind().ToString();
            string actualHash = Hash(CompleteCanonical(member));
            if (!actualKind.Equals(selector.SyntaxKind, StringComparison.Ordinal) ||
                !actualHash.Equals(selector.Sha256, StringComparison.Ordinal))
                throw new ExtractionException("Stage E member identity changed for " +
                    selector.OwnerPath + "." + selector.Name + ".");
            string key = MemberKey(selector);
            if (!resolvedMembers.TryAdd(key, member))
                throw new ExtractionException("Duplicate Stage E member selector " + key + ".");
            members.Add(new(selector.Path, selector.Namespace, selector.OwnerPath, selector.MemberKind,
                selector.Name, selector.GenericArity, selector.ParameterTypes, actualKind, actualHash));
        }

        List<SourceControlIdentity> controls = [];
        (string Member, string Owner)[] controlSelectors =
        [
            ("ExecuteTransaction", "VirtualMachine`1"),
            ("RunByteCode", "VirtualMachine`1"),
            ("RunDispatchLoop", "VirtualMachine`1"),
            ("Dispose", "VirtualMachine`1.FrameCleanupScope"),
        ];
        foreach ((string member, string owner) in controlSelectors)
        {
            MemberAdmission selector = admission.Members.SingleOrDefault(candidate =>
                candidate.Name == member && candidate.OwnerPath == owner)
                ?? throw new ExtractionException("Stage E control member is not admitted: " + owner + "." + member + ".");
            MemberDeclarationSyntax syntax = resolvedMembers[MemberKey(selector)];
            if (syntax is not MethodDeclarationSyntax method)
                throw new ExtractionException("Stage E control anchor is not a method: " + owner + "." + member + ".");
            string[] operations = ExtractControlOperations(method);
            ValidateControlAnchor(member, operations);
            ControlNodeIdentity[] topology = ExtractControlTopology(method);
            ValidateControlTopology(member, topology);
            controls.Add(new(selector.Path, selector.OwnerPath, selector.Name,
                Hash(CompleteCanonical(method)), operations, topology,
                CombinedDigest(topology.Select(static node => ControlNodeDigest(node)))));
        }

        List<DependencyIdentity> dependencies = [];
        foreach (DependencyAdmission dependency in admission.Dependencies)
        {
            string path = ResolveCanonical(root, dependency.Path);
            string actualHash = Hash(File.ReadAllBytes(path));
            if (!actualHash.Equals(dependency.Sha256, StringComparison.Ordinal))
                throw new ExtractionException("Stage E dependency hash changed for " + dependency.Path + ".");
            dependencies.Add(new(dependency.Kind, dependency.Name, dependency.Path, actualHash,
                dependency.Binding, [.. dependency.Theorems]));
        }

        IrDocument ir = BuildDesign(sources.ToArray(), members.ToArray(), dependencies.ToArray(), controls.ToArray());
        ValidateShape(ir);
        byte[] irBytes = Serialize(ir);
        byte[] leanBytes = EvmFrameDriverLeanEmitter.Emit(ir, Hash(irBytes));
        SourceManifest manifest = new(
            1, ExtractorVersion, typeof(CSharpSyntaxTree).Assembly.GetName().Version?.ToString() ?? "unknown",
            LanguageVersion.CSharp14.ToString(), KernelName, ir.Sources, ir.Members, ir.Dependencies,
            ir.SourceControl, ir.ControlPlan, new(IrFileName, Hash(irBytes)), new(LeanFileName, Hash(leanBytes)),
            CombinedDigest(ir.Sources.Select(static source =>
                source.Role + "|" + source.Path + "|" + source.Sha256 + "|" + source.SyntaxSha256)),
            CombinedDigest(ir.Members.Select(static member =>
                member.SourcePath + "|" + member.Namespace + "|" + member.OwnerPath + "|" +
                member.MemberKind + "|" + member.Member + "|" + member.GenericArity + "|" +
                member.ParameterTypes + "|" + member.SyntaxKind + "|" + member.Sha256)),
            CombinedDigest(ir.SourceControl.Select(static control =>
                control.SourcePath + "|" + control.OwnerPath + "|" + control.Member + "|" +
                control.Sha256 + "|" + string.Join(';', control.Operations) + "|" +
                control.TopologySha256 + "|" + string.Join(';', control.Topology.Select(ControlNodeDigest)))
                .Concat(ir.ControlPlan.Select(static step =>
                    step.Stage + "|" + step.Member + "|" + step.NodeId + "|" + step.Kind + "|" +
                    step.SourceArm + "|" + step.SourceSha256))),
            [.. ir.ReviewedOperationalBindings]);
        byte[] manifestBytes = Serialize(manifest);
        ValidateManifest(manifest, irBytes, leanBytes);
        return (ir, irBytes, leanBytes, manifestBytes);
    }

    private static IrDocument BuildDesign(
        SourceIdentity[] sources,
        MemberIdentity[] members,
        DependencyIdentity[] dependencies,
        SourceControlIdentity[] controls) => new(
        1,
        ExtractorVersion,
        KernelName,
        AcceptanceState,
        TargetRoot,
        TargetFork,
        TargetGasPolicy,
        new(
            [
                "Stage A opcode routing is an identity-pinned accepted leaf; opcode-body simulation remains its package boundary.",
                "Stage C full-precompile execution is an identity-pinned accepted leaf; native and cryptographic execution remain open.",
                "FrameJournal and Stage D settlement artifacts are identity-pinned accepted leaves; concrete journal composition remains an adapter obligation.",
                "Each preparation, dispatch, create-deposit, settlement, and cleanup callback needs a separate production simulation; this theorem currently assumes same-leaf equality.",
                "Every emitted branch is attached to a Roslyn control-topology node by member, node id, kind, and source arm.",
            ],
            [
                "RunDispatchLoop entry polling is represented by BytecodeInvocation.cancelled beforeFirstOpcode.",
                "The afterCompleteBatch cancellation boundary records source-reported count/successor data; positivity, 1024-opcode completion, and successor validity are admitted premises, not proved here.",
                "Full-precompile execution has no blanket driver cancellation branch; an escaping callback is escaped.",
                "OperationCanceledException unwinds through FrameCleanupScope; caller-owned rollback is not claimed.",
            ],
            [
                "Frame-machine Nat/Int fields model production checked or saturating arithmetic through explicit adapters.",
                "This package does not quantify fixed-width validity over arbitrary Machine values.",
            ],
            [
                "fuel=0 returns exhausted without invoking preparation, dispatch, or settlement leaves.",
                "fuel>0 executes exactly one step; runOne is non-recursive even when fuel is greater than one.",
                "No theorem claims whole-run induction, successor reachability, transaction completion, or block processing.",
            ]),
        ["fresh", "continuation", "running"],
        ["bytecode", "fullPrecompile"],
        [
            "continued", "suspended", "childSuccess", "childCreateSuccess",
            "childCreateInvalidCode", "childCreateOutOfGas", "childRevert", "childException",
            "topLevelSuccess", "topLevelRevert", "topLevelException",
            "fullPrecompileOutOfGasNested", "fullPrecompileOutOfGasTop",
            "fullPrecompileReturnedFailureNested", "fullPrecompileReturnedFailureTop",
            "fullPrecompileManagedExceptionNested", "fullPrecompileManagedExceptionTop",
            "cancelled", "escaped", "invalidControl",
        ],
        [
            "clearReturnDataOnFreshOnly",
            "prepareFreshOrContinuation",
            "selectBytecodeOrFullPrecompile",
            "invokeRunByteCodeOrRunPrecompile",
            "classifyContinueSuspendOrReturnedChild",
            "classifyTopLevelOrNestedReturn",
            "classifyNestedCreateCodeDeposit",
            "invokeAcceptedSettlementLeaf",
            "cleanupCancellationEscapeOrInvalidControl",
        ],
        ControlPlan(controls),
        Branches(controls),
        controls,
        sources,
        members,
        dependencies,
        [
            "VirtualMachine.ExecuteTransaction loop control",
            "VirtualMachine.RunByteCode dispatch-result control",
            "VirtualMachine.Dispatch.RunDispatchLoop cancellation boundary",
            "VirtualMachine.FrameCleanupScope.Dispose exceptional unwind",
            "Roslyn condition/return/goto/try/catch/using topology and branch bindings",
            "same-leaf algebra equality premises; production leaf simulation remains open",
        ],
        [
            "whole transaction processing and intrinsic-gas validation",
            "block processing, caller rollback, and finalization",
            "opcode body, precompile native/cryptographic, CLR/JIT, and OS correctness",
            "concrete WorldJournal, state database, and tracer implementation",
            "fuel adequacy, reachability, and induction over successive frames",
        ]);

    private static ControlPlanStepIdentity[] ControlPlan(SourceControlIdentity[] controls) =>
    [
        PlanStep(controls, "prepare", "ExecuteTransaction", IfWithProperty(CurrentStateIsContinuation), first: true),
        PlanStep(controls, "dispatch", "ExecuteTransaction", IfWithProperty(CurrentStateIsPrecompile)),
        PlanStep(controls, "classify", "ExecuteTransaction", IfWithProperty(CallResultIsReturn)),
        PlanStep(controls, "settle", "ExecuteTransaction", NodeHasCallSequence("HandleRegularReturn")),
        PlanStep(controls, "cleanup", "Dispose", NodeHasCallSequence("DisposeActiveFrames")),
    ];

    private static ControlPlanStepIdentity PlanStep(
        SourceControlIdentity[] controls,
        string stage,
        string member,
        Func<ControlNodeIdentity, bool> predicate,
        bool first = false)
    {
        if (controls.Length == 0)
            return new(stage, member, "n000", "unknown", "body", new string('0', 64));
        SourceControlIdentity control = controls.Single(candidate => candidate.Member == member);
        ControlNodeIdentity node = (first
                ? control.Topology.FirstOrDefault(predicate)
                : control.Topology.LastOrDefault(predicate))
            ?? throw new ExtractionException("Stage E control plan could not bind " + stage + " to " + member + ".");
        return new(stage, member, node.Id, node.Kind, node.Arm, node.Sha256);
    }

    private static BranchDescriptor[] Branches(SourceControlIdentity[] controls) =>
    [
        new("FreshPreparation", "fresh", ["clearReturnData", "prepareFresh"], "preparation",
            Bind(controls, "ExecuteTransaction", IfWithProperty(CurrentStateIsContinuation), "then", first: true)),
        new("ContinuationPreparation", "continuation", ["retainReturnData", "prepareContinuation"], "preparation",
            Bind(controls, "ExecuteTransaction", IfWithProperty(CurrentStateIsContinuation), "else", first: true)),
        new("BytecodeDispatch", "bytecode", ["RunByteCode", "RunDispatchLoop"], "invocation",
            [.. Bind(controls, "ExecuteTransaction", IfWithProperty(CurrentStateIsPrecompile), "else"),
                .. Bind(controls, "RunByteCode", NodeHasCallSequence("RunDispatchLoop"))]),
        new("FullPrecompileDispatch", "fullPrecompile", ["RunPrecompile", "ExecutePrecompile"], "invocation",
            [.. Bind(controls, "ExecuteTransaction", IfWithProperty(CurrentStateIsPrecompile), "then"),
                .. Bind(controls, "ExecuteTransaction", NodeHasCallSequence("ExecutePrecompile"))]),
        new("BytecodeContinue", "returned/continue", ["retainCurrentFrame"], "continued",
            Bind(controls, "ExecuteTransaction", IfWithProperty(CallResultIsReturn), "continue")),
        new("BytecodeSuspend", "returned/suspend", ["prepareChildFrame", "retainParent"], "suspended",
            Bind(controls, "ExecuteTransaction", NodeHasCallSequence("PrepareNextCallFrame"))),
        new("NestedRegularSuccess", "halt/success/nested/call", ["popParent", "mergeChild", "repayStateGasSpill"], "childSuccess",
            Bind(controls, "ExecuteTransaction", IfWithLocal(NegatedIsCreate), "regular")),
        new("NestedCreateSuccess", "halt/success/nested/create", ["prepareCreateData", "HandleCreate"], "childCreateSuccess",
            Bind(controls, "ExecuteTransaction", NodeHasCallSequence("PrepareCreateData", "HandleCreate"))),
        new("NestedCreateInvalidCode", "createDeposit/invalid", ["restoreWorld", "creditParent"], "childCreateInvalidCode",
            Bind(controls, "ExecuteTransaction", NodeHasCallSequence("HandleCreate"), "invalidCode")),
        new("NestedCreateOutOfGas", "createDeposit/outOfGas", ["burnDepositGas", "restoreWorld"], "childCreateOutOfGas",
            Bind(controls, "ExecuteTransaction", NodeHasCallSequence("HandleCreate"), "outOfGas")),
        new("NestedRevert", "halt/revert/nested", ["restoreSnapshot", "restoreChildGas", "HandleRevert"], "childRevert",
            Bind(controls, "ExecuteTransaction", NodeHasCallSequence("HandleRevert"))),
        new("NestedException", "halt/exception/nested", ["restoreFailureControl", "resumeParent"], "childException",
            Bind(controls, "ExecuteTransaction", NodeHasCallSequence("HandleException"))),
        new("TopLevelSuccess", "halt/success/top", ["PrepareTopLevelSubstate"], "topLevelSuccess",
            Bind(controls, "ExecuteTransaction", NodeHasCallSequence("PrepareTopLevelSubstate"))),
        new("TopLevelRevert", "halt/revert/top", ["refundRevertedStateGas"], "topLevelRevert",
            Bind(controls, "ExecuteTransaction", NodeHasCallSequence("PrepareTopLevelSubstate"), "revert")),
        new("TopLevelException", "halt/exception/top", ["HandleExceptionOrFailure"], "topLevelException",
            Bind(controls, "ExecuteTransaction", NodeHasCallSequence("HandleFailure"))),
        new("FullPrecompileOutOfGasNested", "precompile/outOfGas/nested", ["failureSettlement"], "fullPrecompileOutOfGasNested",
            Bind(controls, "ExecuteTransaction", NodeHasCallSequence("ExecutePrecompile"), "outOfGas")),
        new("FullPrecompileOutOfGasTop", "precompile/outOfGas/top", ["failureSettlement"], "fullPrecompileOutOfGasTop",
            Bind(controls, "ExecuteTransaction", NodeHasCallSequence("ExecutePrecompile"), "outOfGas")),
        new("FullPrecompileReturnedFailure", "precompile/returnedFailure", ["failureSettlement"], "fullPrecompileReturnedFailure",
            Bind(controls, "ExecuteTransaction", NodeHasCallSequence("ExecutePrecompile"), "returnedFailure")),
        new("FullPrecompileManagedException", "precompile/managedException", ["failureSettlement"], "fullPrecompileManagedException",
            Bind(controls, "ExecuteTransaction", NodeHasCallSequence("ExecutePrecompile"), "managedException")),
        new("Cancelled", "bytecode/cancelled", ["FrameCleanupScope.Dispose"], "cancelled",
            BindCancellationSites(controls)),
        new("Escaped", "callback/escaped", ["FrameCleanupScope.Dispose"], "escaped",
            [.. Bind(controls, "ExecuteTransaction", NodeKind("try")),
                .. Bind(controls, "Dispose", NodeHasCallSequence("DisposeActiveFrames"))]),
        new("InvalidControl", "invalidControl", ["failClosed"], "invalidControl",
            Bind(controls, "RunDispatchLoop", NodeKind("while"), "failClosed")),
    ];

    private static string[] Bind(
        SourceControlIdentity[] controls,
        string member,
        Func<ControlNodeIdentity, bool> predicate,
        string arm = "node",
        bool first = false)
    {
        if (controls.Length == 0)
            return [$"unbound:n000:unknown:body:{new string('0', 64)}:{arm}"];
        SourceControlIdentity control = controls.SingleOrDefault(candidate => candidate.Member == member)
            ?? throw new ExtractionException("Stage E branch has no admitted control member " + member + ".");
        ControlNodeIdentity node = (first
                ? control.Topology.FirstOrDefault(predicate)
                : control.Topology.LastOrDefault(predicate))
            ?? throw new ExtractionException("Stage E branch could not bind to " + member + " control topology.");
        return [$"{member}:{node.Id}:{node.Kind}:{node.Arm}:{node.Sha256}:{arm}"];
    }

    private static string[] BindAll(
        SourceControlIdentity[] controls,
        string member,
        Func<ControlNodeIdentity, bool> predicate,
        string arm,
        int expectedCount)
    {
        if (controls.Length == 0)
            return [$"unbound:n000:unknown:body:{new string('0', 64)}:{arm}"];
        SourceControlIdentity control = controls.SingleOrDefault(candidate => candidate.Member == member)
            ?? throw new ExtractionException("Stage E branch has no admitted control member " + member + ".");
        ControlNodeIdentity[] nodes = control.Topology.Where(predicate).ToArray();
        if (nodes.Length != expectedCount)
            throw new ExtractionException("Stage E branch expected exactly " + expectedCount + " " + member +
                " invocation statement nodes; found " + nodes.Length + ".");
        return [.. nodes.Select(node => $"{member}:{node.Id}:{node.Kind}:{node.Arm}:{node.Sha256}:{arm}")];
    }

    internal static string[] BindCancellationSites(SourceControlIdentity[] controls) =>
        BindAll(controls, "RunDispatchLoop", NodeCallStatement("ThrowOperationCanceledException"),
            "cancelled", expectedCount: 2);

    private static Func<ControlNodeIdentity, bool> NodeKind(string kind) =>
        node => node.Kind == kind;

    private static readonly ControlConditionIdentity CurrentStateIsContinuation =
        new(ControlConditionKind.Property, "_currentState", "IsContinuation", true);

    private static readonly ControlConditionIdentity CurrentStateIsPrecompile =
        new(ControlConditionKind.Property, "_currentState", "IsPrecompile", false);

    private static readonly ControlConditionIdentity CallResultIsReturn =
        new(ControlConditionKind.Property, "callResult", "IsReturn", true);

    private static readonly ControlConditionIdentity NegatedIsCreate =
        new(ControlConditionKind.Local, string.Empty, "isCreate", true);

    private static Func<ControlNodeIdentity, bool> IfWithProperty(ControlConditionIdentity expected) =>
        node => node.Kind == "if" &&
            node.ConditionIdentity.Kind == ControlConditionKind.Property &&
            node.ConditionIdentity.Receiver == expected.Receiver &&
            node.ConditionIdentity.Member == expected.Member &&
            node.ConditionIdentity.Negated == expected.Negated;

    private static Func<ControlNodeIdentity, bool> IfWithLocal(ControlConditionIdentity expected) =>
        node => node.Kind == "if" &&
            node.ConditionIdentity.Kind == ControlConditionKind.Local &&
            node.ConditionIdentity.Receiver == expected.Receiver &&
            node.ConditionIdentity.Member == expected.Member &&
            node.ConditionIdentity.Negated == expected.Negated;

    private static Func<ControlNodeIdentity, bool> NodeHasCallSequence(params string[] names) => node =>
    {
        int next = 0;
        foreach (string operation in node.Operations)
            if (next < names.Length && operation == "call:" + names[next])
                next++;
        return next == names.Length;
    };

    private static Func<ControlNodeIdentity, bool> NodeCallStatement(string name) =>
        node => node.Kind == "statement" && node.Operations.Length == 1 &&
            node.Operations[0] == "call:" + name;

    private static string[] ExtractControlOperations(MethodDeclarationSyntax method)
    {
        List<string> operations = [];
        foreach (SyntaxNode node in method.DescendantNodes())
        {
            switch (node)
            {
                case WhileStatementSyntax:
                    operations.Add("while");
                    break;
                case ForStatementSyntax:
                    operations.Add("for");
                    break;
                case ForEachStatementSyntax:
                case ForEachVariableStatementSyntax:
                    operations.Add("foreach");
                    break;
                case DoStatementSyntax:
                    operations.Add("do");
                    break;
                case IfStatementSyntax:
                    operations.Add("if");
                    break;
                case TryStatementSyntax:
                    operations.Add("try");
                    break;
                case CatchClauseSyntax:
                    operations.Add("catch");
                    break;
                case FinallyClauseSyntax:
                    operations.Add("finally");
                    break;
                case GotoStatementSyntax:
                    operations.Add("goto");
                    break;
                case ReturnStatementSyntax:
                    operations.Add("return");
                    break;
                case ContinueStatementSyntax:
                    operations.Add("continue");
                    break;
                case BreakStatementSyntax:
                    operations.Add("break");
                    break;
                case ThrowStatementSyntax:
                    operations.Add("throw");
                    break;
                case LabeledStatementSyntax:
                    operations.Add("label");
                    break;
                case UsingStatementSyntax:
                    operations.Add("using");
                    break;
                case LocalDeclarationStatementSyntax declaration when declaration.UsingKeyword.IsKind(SyntaxKind.UsingKeyword):
                    operations.Add("using");
                    break;
                case InvocationExpressionSyntax invocation:
                    string name = InvocationName(invocation.Expression);
                    if (name.Length != 0)
                        operations.Add("call:" + name);
                    break;
            }
        }
        return [.. operations];
    }

    private static ControlNodeIdentity[] ExtractControlTopology(MethodDeclarationSyntax method)
    {
        List<ControlNodeIdentity> nodes = [];
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
                        Hash(CompleteCanonical(child)), ControlNodeOperations(child))
                    {
                        ConditionIdentity = ExtractConditionIdentity(child),
                    });
                    Visit(child, id, "body");
                }
                else
                {
                    Visit(child, parentId, arm);
                }
            }
        }

        Visit(method, "root", "body");
        return [.. nodes];
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

    internal static ControlConditionIdentity ExtractConditionIdentity(SyntaxNode node) => node switch
    {
        IfStatementSyntax value => DescribeCondition(value.Condition),
        _ => ControlConditionIdentity.Unknown,
    };

    private static ControlConditionIdentity DescribeCondition(ExpressionSyntax condition)
    {
        ExpressionSyntax expression = condition;
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

        return expression switch
        {
            MemberAccessExpressionSyntax member when member.Name is SimpleNameSyntax name =>
                new(ControlConditionKind.Property, ReceiverIdentity(member.Expression),
                    name.Identifier.ValueText, negated),
            InvocationExpressionSyntax invocation =>
                new(ControlConditionKind.Invocation,
                    invocation.Expression is MemberAccessExpressionSyntax member
                        ? ReceiverIdentity(member.Expression) : string.Empty,
                    InvocationName(invocation.Expression), negated),
            IdentifierNameSyntax identifier =>
                new(ControlConditionKind.Local, string.Empty, identifier.Identifier.ValueText, negated),
            _ => ControlConditionIdentity.Unknown,
        };
    }

    internal static string ReceiverIdentity(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        ThisExpressionSyntax => "this",
        MemberAccessExpressionSyntax member when member.Name is SimpleNameSyntax name =>
            ReceiverIdentity(member.Expression) + "." + name.Identifier.ValueText,
        _ => string.Empty,
    };

    private static string[] ControlNodeOperations(SyntaxNode node)
    {
        List<string> operations = [];
        foreach (InvocationExpressionSyntax invocation in node.DescendantNodesAndSelf()
                     .OfType<InvocationExpressionSyntax>())
        {
            string name = InvocationName(invocation.Expression);
            if (name.Length != 0)
                operations.Add("call:" + name);
        }
        return [.. operations];
    }

    private static void ValidateControlTopology(string member, ControlNodeIdentity[] topology)
    {
        if (topology.Length == 0 || topology.Any(static node =>
                string.IsNullOrWhiteSpace(node.Id) || string.IsNullOrWhiteSpace(node.Kind) ||
                string.IsNullOrWhiteSpace(node.ParentId) || string.IsNullOrWhiteSpace(node.Condition) ||
                !IsSha256(node.Sha256) || node.Operations is null))
            throw new ExtractionException("Stage E control topology is empty or malformed for " + member + ".");
        if (topology.Select(static node => node.Id).Distinct(StringComparer.Ordinal).Count() != topology.Length)
            throw new ExtractionException("Stage E control topology has duplicate node ids for " + member + ".");
        Dictionary<string, int> positions = topology
            .Select((node, index) => new { node.Id, Index = index })
            .ToDictionary(static item => item.Id, static item => item.Index, StringComparer.Ordinal);
        for (int index = 0; index < topology.Length; index++)
        {
            ControlNodeIdentity node = topology[index];
            string expectedId = "n" + index.ToString("D3", CultureInfo.InvariantCulture);
            if (node.Id != expectedId || (node.ParentId != "root" &&
                    (!positions.TryGetValue(node.ParentId, out int parentIndex) || parentIndex >= index)))
                throw new ExtractionException("Stage E control topology is not a preorder tree for " + member + ".");
        }
    }

    private static string ControlNodeDigest(ControlNodeIdentity node) =>
        node.Id + "\u001f" + node.Kind + "\u001f" + node.ParentId + "\u001f" + node.Arm +
        "\u001f" + node.Condition +
        "\u001f" + node.Sha256 + "\u001f" + string.Join(';', node.Operations);

    private static string InvocationName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => InvocationName(member.Name),
        GenericNameSyntax generic => generic.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        _ => string.Empty,
    };

    private static void ValidateControlAnchor(string member, string[] operations)
    {
        if (operations.Length == 0)
            throw new ExtractionException("Stage E control anchor has no executable markers: " + member + ".");
        string[] required = member switch
        {
            "ExecuteTransaction" =>
            [
                "while", "call:ExecutePrecompile", "call:ExecuteCall", "call:PrepareNextCallFrame",
                "call:HandleException", "call:HandleCreate", "call:HandleRegularReturn",
                "call:HandleRevert", "call:HandleFailure",
            ],
            "RunByteCode" => ["call:RunDispatchLoop"],
            "RunDispatchLoop" => ["while", "call:ThrowOperationCanceledException"],
            "Dispose" => ["call:DisposeActiveFrames"],
            _ => throw new ExtractionException("Unknown Stage E control anchor " + member + "."),
        };
        int cursor = 0;
        foreach (string marker in required)
        {
            int found = Array.FindIndex(operations, cursor, value => value == marker);
            if (found < 0)
                throw new ExtractionException("Stage E control anchor " + member +
                    " lost required marker " + marker + ".");
            cursor = found + 1;
        }
    }

    private static MemberDeclarationSyntax ResolveMember(CompilationUnitSyntax root, MemberAdmission selector)
    {
        BaseTypeDeclarationSyntax owner = ResolveOwner(root, selector.OwnerPath);
        if (selector.MemberKind == "type" && owner.Identifier.ValueText == selector.Name)
            return owner;
        MemberDeclarationSyntax[] matches = DirectMembers(owner).Where(member =>
            selector.MemberKind switch
            {
                "method" when member is MethodDeclarationSyntax method =>
                    method.Identifier.ValueText == selector.Name &&
                    (method.TypeParameterList?.Parameters.Count ?? 0) == selector.GenericArity &&
                    ParameterTypes(method) == selector.ParameterTypes,
                "property" when member is PropertyDeclarationSyntax property =>
                    property.Identifier.ValueText == selector.Name,
                "field" when member is FieldDeclarationSyntax field =>
                    field.Declaration.Variables.Any(variable =>
                        variable.Identifier.ValueText == selector.Name),
                _ => false,
            }).ToArray();
        if (matches.Length != 1)
            throw new ExtractionException("Expected exactly one Stage E member " +
                selector.OwnerPath + "." + selector.Name + "; found " + matches.Length + ".");
        BaseNamespaceDeclarationSyntax[] namespaces = matches[0].AncestorsAndSelf()
            .OfType<BaseNamespaceDeclarationSyntax>().ToArray();
        if (namespaces.Length != 1 ||
            !namespaces[0].Name.ToString().Equals(selector.Namespace, StringComparison.Ordinal))
            throw new ExtractionException("Stage E member namespace mismatch for " +
                selector.OwnerPath + "." + selector.Name + ".");
        return matches[0];
    }

    private static BaseTypeDeclarationSyntax ResolveOwner(CompilationUnitSyntax root, string ownerPath)
    {
        BaseTypeDeclarationSyntax? current = null;
        foreach (string segment in ownerPath.Split('.'))
        {
            (string name, int arity) = ParseOwnerSegment(segment);
            IEnumerable<BaseTypeDeclarationSyntax> candidates = current is null
                ? root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
                    .Where(type => !type.Ancestors().OfType<BaseTypeDeclarationSyntax>().Any())
                : current is TypeDeclarationSyntax parent
                    ? parent.Members.OfType<BaseTypeDeclarationSyntax>()
                    : [];
            BaseTypeDeclarationSyntax[] matches = candidates.Where(type =>
                type.Identifier.ValueText == name && TypeArity(type) == arity).ToArray();
            if (matches.Length != 1)
                throw new ExtractionException("Expected exactly one Stage E owner segment " +
                    segment + " in " + ownerPath + "; found " + matches.Length + ".");
            current = matches[0];
        }
        return current ?? throw new ExtractionException("Empty Stage E owner path.");
    }

    private static IEnumerable<MemberDeclarationSyntax> DirectMembers(BaseTypeDeclarationSyntax owner)
    {
        if (owner is EnumDeclarationSyntax value)
        {
            foreach (EnumMemberDeclarationSyntax member in value.Members)
                yield return member;
            yield break;
        }
        if (owner is not TypeDeclarationSyntax type)
            yield break;
        foreach (MemberDeclarationSyntax member in type.Members)
        {
            if (member is ExtensionBlockDeclarationSyntax extension)
                foreach (MemberDeclarationSyntax extensionMember in extension.Members)
                    yield return extensionMember;
            else
                yield return member;
        }
    }

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

    private static int TypeArity(BaseTypeDeclarationSyntax type) => type is TypeDeclarationSyntax declaration
        ? declaration.TypeParameterList?.Parameters.Count ?? 0
        : 0;

    private static (string Name, int Arity) ParseOwnerSegment(string value)
    {
        int marker = value.LastIndexOf((char)96);
        if (marker < 0)
            return (value, 0);
        if (marker == 0 || !int.TryParse(value.AsSpan(marker + 1), NumberStyles.None,
                CultureInfo.InvariantCulture, out int arity) || arity < 0)
            throw new ExtractionException("Invalid Stage E owner generic arity " + value + ".");
        return (value[..marker], arity);
    }

    private static string MemberKey(MemberAdmission member) =>
        member.Path + "|" + member.Namespace + "|" + member.OwnerPath + "|" + member.MemberKind + "|" +
        member.Name + "|" + member.GenericArity + "|" + member.ParameterTypes;

    internal static string ResolveCanonical(string root, string relative)
    {
        RequirePath(relative);
        string current = root;
        foreach (string segment in relative.Split('/'))
        {
            string? exact = null;
            foreach (string candidate in Directory.EnumerateFileSystemEntries(current))
            {
                string name = Path.GetFileName(candidate);
                if (!name.Equals(segment, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (exact is not null || !name.Equals(segment, StringComparison.Ordinal))
                    throw new ExtractionException(
                        "Stage E path is missing, ambiguous, or noncanonical: " + relative + ".");
                exact = candidate;
            }
            current = exact ?? throw new ExtractionException("Stage E path is missing: " + relative + ".");
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ExtractionException("Stage E source closure disallows reparse points: " + relative + ".");
        }
        return current;
    }

    internal static void ValidateEmbeddedAdmission(string repoRoot) =>
        _ = ReadAdmission(Path.GetFullPath(repoRoot), true);

    private static Admission ReadAdmission(string root, bool requireEmbeddedIdentity)
    {
        string path = ResolveCanonical(root, Package + AdmissionPath);
        byte[] bytes = requireEmbeddedIdentity ? ReadResource(AdmissionResourceSuffix) : File.ReadAllBytes(path);
        if (requireEmbeddedIdentity)
            RequireEqual(path, bytes, "embedded Stage E admission");

        List<SourceAdmission> sources = [];
        List<MemberAdmission> members = [];
        List<DependencyAdmission> dependencies = [];
        HashSet<string> sourcePaths = new(StringComparer.Ordinal);
        HashSet<string> memberKeys = new(StringComparer.Ordinal);
        HashSet<string> dependencyKeys = new(StringComparer.Ordinal);
        int lineNumber = 0;
        foreach (string line in StrictUtf8.GetString(bytes).Split('\n'))
        {
            lineNumber++;
            string value = line.TrimEnd('\r');
            if (value.Length == 0 || value.StartsWith('#'))
                continue;
            string[] fields = value.Split('|');
            try
            {
                switch (fields[0])
                {
                    case "source" when fields.Length == 4:
                        RequireRole(fields[1], SourceRoles, "source role");
                        RequirePath(fields[2]);
                        RequireHash(fields[3]);
                        if (!sourcePaths.Add(fields[2]))
                            throw new ExtractionException("Duplicate Stage E source " + fields[2] + ".");
                        sources.Add(new(fields[1], fields[2], fields[3]));
                        break;
                    case "member" when fields.Length == 10:
                        RequirePath(fields[1]);
                        RequireText(fields[2], "member namespace");
                        RequireText(fields[3], "member owner");
                        RequireText(fields[4], "member kind");
                        RequireText(fields[5], "member name");
                        if (!int.TryParse(fields[6], NumberStyles.None, CultureInfo.InvariantCulture,
                                out int arity) || arity < 0)
                            throw new ExtractionException("Member generic arity must be nonnegative.");
                        RequireCanonicalParameterTypes(fields[7]);
                        RequireText(fields[8], "member syntax kind");
                        RequireHash(fields[9]);
                        MemberAdmission member = new(fields[1], fields[2], fields[3], fields[4], fields[5],
                            arity, fields[7], fields[8], fields[9]);
                        if (!memberKeys.Add(MemberKey(member)))
                            throw new ExtractionException("Duplicate Stage E member " + MemberKey(member) + ".");
                        members.Add(member);
                        break;
                    case "dependency" when fields.Length == 7:
                        RequireRole(fields[1], DependencyKinds, "dependency kind");
                        RequireText(fields[2], "dependency name");
                        RequirePath(fields[3]);
                        RequireHash(fields[4]);
                        RequireText(fields[5], "dependency binding");
                        string[] theorems = fields[6] == "-"
                            ? []
                            : fields[6].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        if (fields[1] == "proof" && theorems.Length == 0)
                            throw new ExtractionException("A proof dependency must name a theorem.");
                        string dependencyKey = string.Join('|', fields[1..5]);
                        if (!dependencyKeys.Add(dependencyKey))
                            throw new ExtractionException("Duplicate Stage E dependency " + dependencyKey + ".");
                        dependencies.Add(new(fields[1], fields[2], fields[3], fields[4], fields[5], theorems));
                        break;
                    default:
                        throw new ExtractionException("Expected source, member, or dependency admission line.");
                }
            }
            catch (ExtractionException exception)
            {
                throw new ExtractionException("Invalid Stage E admission line " + lineNumber + ": " +
                    exception.Message);
            }
        }
        if (sources.Count == 0 || members.Count == 0 || dependencies.Count == 0)
            throw new ExtractionException("Stage E admission must contain nonempty source/member/dependency closures.");
        foreach (MemberAdmission member in members)
            if (!sourcePaths.Contains(member.Path))
                throw new ExtractionException("Stage E member is outside source closure: " + member.Path + ".");
        return new(sources.ToArray(), members.ToArray(), dependencies.ToArray());
    }

    private static void ValidateShape(IrDocument ir)
    {
        if (ir.SchemaVersion != 1 || ir.ExtractorVersion != ExtractorVersion || ir.Kernel != KernelName ||
            ir.AcceptanceState != AcceptanceState || ir.TargetRoot != TargetRoot ||
            ir.TargetFork != TargetFork || ir.TargetGasPolicy != TargetGasPolicy)
            throw new ExtractionException("Stage E IR header changed from the admitted profile.");
        if (ir.Boundary is null || ir.Boundary.LeafBindings is null ||
            ir.Boundary.Cancellation is null || ir.Boundary.FixedWidth is null || ir.Boundary.Fuel is null)
            throw new ExtractionException("Stage E boundary is incomplete.");
        RequireDistinctNonempty(ir.Boundary.LeafBindings, "leaf bindings");
        RequireDistinctNonempty(ir.Boundary.Cancellation, "cancellation assumptions");
        RequireDistinctNonempty(ir.Boundary.FixedWidth, "fixed-width assumptions");
        RequireDistinctNonempty(ir.Boundary.Fuel, "fuel assumptions");
        RequireSequence(ir.Phases, ["fresh", "continuation", "running"], "phases");
        RequireSequence(ir.Subjects, ["bytecode", "fullPrecompile"], "subjects");
        RequireSequence(ir.Routes,
        [
            "continued", "suspended", "childSuccess", "childCreateSuccess",
            "childCreateInvalidCode", "childCreateOutOfGas", "childRevert", "childException",
            "topLevelSuccess", "topLevelRevert", "topLevelException",
            "fullPrecompileOutOfGasNested", "fullPrecompileOutOfGasTop",
            "fullPrecompileReturnedFailureNested", "fullPrecompileReturnedFailureTop",
            "fullPrecompileManagedExceptionNested", "fullPrecompileManagedExceptionTop",
            "cancelled", "escaped", "invalidControl",
        ], "routes");
        RequireSequence(ir.DispatchOrder,
        [
            "clearReturnDataOnFreshOnly", "prepareFreshOrContinuation",
            "selectBytecodeOrFullPrecompile", "invokeRunByteCodeOrRunPrecompile",
            "classifyContinueSuspendOrReturnedChild", "classifyTopLevelOrNestedReturn",
            "classifyNestedCreateCodeDeposit", "invokeAcceptedSettlementLeaf",
            "cleanupCancellationEscapeOrInvalidControl",
        ], "dispatch order");
        if (ir.ControlPlan is null || ir.ControlPlan.Length != 5 ||
            !ir.ControlPlan.Select(static step => step.Stage).SequenceEqual(
                ["prepare", "dispatch", "classify", "settle", "cleanup"], StringComparer.Ordinal) ||
            ir.ControlPlan.Any(static step => step is null || string.IsNullOrWhiteSpace(step.Member) ||
                string.IsNullOrWhiteSpace(step.NodeId) || string.IsNullOrWhiteSpace(step.Kind) ||
                string.IsNullOrWhiteSpace(step.SourceArm) || !IsSha256(step.SourceSha256)))
            throw new ExtractionException("Stage E source-derived control plan is incomplete or reordered.");
        if (ir.Branches is null || ir.Branches.Length < 18 ||
            ir.Branches.Any(static branch => branch is null ||
                string.IsNullOrWhiteSpace(branch.Name) ||
                string.IsNullOrWhiteSpace(branch.Invocation) ||
                string.IsNullOrWhiteSpace(branch.Settlement) ||
                branch.Effects is null || branch.Effects.Length == 0 ||
                branch.SourceBindings is null || branch.SourceBindings.Length == 0 ||
                branch.SourceBindings.Any(string.IsNullOrWhiteSpace)))
            throw new ExtractionException("Stage E IR lost one-step control branches.");
        RequireDistinctNonempty(ir.Branches.Select(static branch => branch.Name), "branch names");
        foreach (BranchDescriptor branch in ir.Branches)
            foreach (string binding in branch.SourceBindings)
            {
                string[] fields = binding.Split(':');
                if (fields.Length != 6 || !IsSha256(fields[4]) || ir.SourceControl is null || !ir.SourceControl.Any(control => control.Member == fields[0] &&
                        control.Topology.Any(node => node.Id == fields[1] && node.Kind == fields[2] &&
                            node.Arm == fields[3] && node.Sha256 == fields[4])))
                    throw new ExtractionException("Stage E branch binding is not attached to admitted topology: " +
                        branch.Name + ".");
            }
        if (ir.SourceControl is null || ir.SourceControl.Length != 4 ||
            !ir.SourceControl.Select(static control => control.Member).SequenceEqual(
                ["ExecuteTransaction", "RunByteCode", "RunDispatchLoop", "Dispose"], StringComparer.Ordinal))
            throw new ExtractionException("Stage E source control anchors are incomplete or reordered.");
        foreach (SourceControlIdentity control in ir.SourceControl)
        {
            if (string.IsNullOrWhiteSpace(control.SourcePath) || string.IsNullOrWhiteSpace(control.OwnerPath) ||
                string.IsNullOrWhiteSpace(control.Member) || control.Operations is null ||
                control.Operations.Length == 0 || !IsSha256(control.Sha256) || control.Topology is null ||
                control.Topology.Length == 0 || !IsSha256(control.TopologySha256) ||
                control.Topology.Any(static node => node is null || string.IsNullOrWhiteSpace(node.Id) ||
                    string.IsNullOrWhiteSpace(node.Kind) || string.IsNullOrWhiteSpace(node.ParentId) ||
                    string.IsNullOrWhiteSpace(node.Arm) || string.IsNullOrWhiteSpace(node.Condition) ||
                    !IsSha256(node.Sha256) ||
                    node.Operations is null))
                throw new ExtractionException("Stage E source control anchor is incomplete.");
            RequireDistinctNonempty(control.Topology.Select(static node => node.Id),
                "control topology node ids");
        }
        foreach (ControlPlanStepIdentity step in ir.ControlPlan)
            if (!ir.SourceControl.Any(control => control.Member == step.Member &&
                    control.Topology.Any(node => node.Id == step.NodeId && node.Kind == step.Kind &&
                        node.Arm == step.SourceArm && node.Sha256 == step.SourceSha256)))
                throw new ExtractionException("Stage E control plan step is not attached to admitted topology: " +
                    step.Stage + ".");
        if (ir.Sources is null || ir.Members is null || ir.Dependencies is null ||
            ir.ReviewedOperationalBindings is null || ir.Exclusions is null)
            throw new ExtractionException("Stage E closure fields may not be null.");
        RequireDistinctNonempty(ir.Sources.Select(static source => source.Path), "source paths");
        RequireDistinctNonempty(ir.Members.Select(static member =>
            member.SourcePath + "|" + member.OwnerPath + "|" + member.Member + "|" +
            member.GenericArity + "|" + member.ParameterTypes), "resolved members");
        RequireDistinctNonempty(ir.Dependencies.Select(static dependency => dependency.Path), "dependency paths");
        RequireDistinctNonempty(ir.ReviewedOperationalBindings, "reviewed operational bindings");
        RequireDistinctNonempty(ir.Exclusions, "exclusions");
        if (ir.Sources.Length < 12 || ir.Members.Length < 20 || ir.Dependencies.Length < 8)
            throw new ExtractionException("Stage E closure is smaller than the reviewed production surface.");
        if (ir.Dependencies.Count(static dependency => dependency.Kind == "proof") !=
                AcceptedProofDependencies.Length ||
            AcceptedProofDependencies.Any(expected => !ir.Dependencies.Any(dependency =>
                dependency.Kind == "proof" && dependency.Name == expected.Name &&
                dependency.Path == expected.Path && dependency.Theorems is { Length: 1 } theorems &&
                theorems[0] == expected.Theorem)))
            throw new ExtractionException("Stage E proof dependency identities are incomplete or changed.");
    }

    private static void ValidateManifest(SourceManifest manifest, byte[] irBytes, byte[] leanBytes)
    {
        if (manifest.SchemaVersion != 1 || manifest.ExtractorVersion != ExtractorVersion ||
            manifest.Kernel != KernelName || manifest.Sources is null || manifest.Members is null ||
            manifest.Dependencies is null || manifest.SourceControl is null || manifest.Ir is null ||
            manifest.ControlPlan is null || manifest.ControlPlan.Length != 5 || manifest.Lean is null ||
            manifest.Ir.Path != IrFileName ||
            manifest.Ir.Sha256 != Hash(irBytes) || manifest.Lean.Path != LeanFileName ||
            manifest.Lean.Sha256 != Hash(leanBytes) || !IsSha256(manifest.CombinedSourceSha256) ||
            !IsSha256(manifest.CombinedMemberSha256) || !IsSha256(manifest.CombinedControlSha256))
            throw new ExtractionException("Stage E manifest is incomplete or not bound to artifacts.");
        if (manifest.SourceControl.Length != 4 || manifest.ControlPlan.Length != 5 ||
            manifest.Dependencies.Length < 8 ||
            !manifest.ControlPlan.Select(static step => step.Stage).SequenceEqual(
                ["prepare", "dispatch", "classify", "settle", "cleanup"], StringComparer.Ordinal) ||
            manifest.ReviewedOperationalBindings is null ||
            manifest.ReviewedOperationalBindings.Length < 5)
            throw new ExtractionException("Stage E manifest lost control anchors or obligations.");
    }

    private static string CombinedDigest(IEnumerable<string> values) =>
        Hash(StrictUtf8.GetBytes(string.Join('\n', values) + "\n"));

    private static void RequirePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') ||
            Path.IsPathRooted(path) || path.Split('/').Any(static segment => segment is "" or "." or ".."))
            throw new ExtractionException("Unsafe or noncanonical Stage E relative path " + path + ".");
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(static character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static void RequireHash(string hash)
    {
        if (!IsSha256(hash))
            throw new ExtractionException("Stage E admission hashes must be lowercase SHA-256 values.");
    }

    private static void RequireRole(string value, HashSet<string> allowed, string description)
    {
        if (!allowed.Contains(value))
            throw new ExtractionException("Unknown Stage E " + description + " " + value + ".");
    }

    private static void RequireText(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains("|", StringComparison.Ordinal))
            throw new ExtractionException("Stage E " + description + " is empty or contains '|'.");
    }

    private static void RequireCanonicalParameterTypes(string value)
    {
        if (value.Contains("|", StringComparison.Ordinal) ||
            value.Any(static character => char.IsWhiteSpace(character)))
            throw new ExtractionException("Stage E member parameters must be empty or canonical.");
    }

    private static void RequireDistinctNonempty(IEnumerable<string> values, string description)
    {
        string[] array = [.. values];
        if (array.Length == 0 || array.Any(string.IsNullOrWhiteSpace) ||
            array.Distinct(StringComparer.Ordinal).Count() != array.Length)
            throw new ExtractionException("Stage E " + description + " must be nonempty and distinct.");
    }

    private static void RequireSequence(string[] actual, string[] expected, string description)
    {
        if (actual is null || !actual.SequenceEqual(expected, StringComparer.Ordinal))
            throw new ExtractionException("Stage E " + description + " changed from the admitted order.");
    }

    private static byte[] ReadResource(string suffix)
    {
        Assembly assembly = typeof(EvmFrameDriverProfile).Assembly;
        string resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(suffix, StringComparison.Ordinal))
            ?? throw new ExtractionException("Missing embedded Stage E resource " + suffix + ".");
        using Stream resource = assembly.GetManifestResourceStream(resourceName)
            ?? throw new ExtractionException("Cannot read embedded Stage E resource " + resourceName + ".");
        using MemoryStream bytes = new();
        resource.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static void WriteIfChanged(string path, byte[] bytes)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            File.WriteAllBytes(path, bytes);
    }

    private static void RequireEqual(string path, byte[] expected, string description)
    {
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(expected))
            throw new ExtractionException("Stage E deterministic " + description + " mismatch: " + path + ".");
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static byte[] Serialize<T>(T value) =>
        StrictUtf8.GetBytes(JsonSerializer.Serialize(value, JsonOptions) + "\n");

    private sealed record Admission(
        SourceAdmission[] Sources,
        MemberAdmission[] Members,
        DependencyAdmission[] Dependencies);
}
