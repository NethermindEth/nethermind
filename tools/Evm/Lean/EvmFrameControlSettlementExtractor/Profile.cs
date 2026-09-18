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

namespace Nethermind.Evm.Lean.EvmFrameControlSettlementExtractor;

/// <summary>Reads the reviewed Stage D closure and emits deterministic identity artifacts plus a reviewed operational profile.</summary>
internal static class EvmFrameControlSettlementProfile
{
    internal const string Package = "tools/Evm/Lean/EvmFrameControlSettlementExtractor/";
    internal const string IrFileName = "EvmFrameControlSettlementKernel.ir.json";
    internal const string ManifestFileName = "EvmFrameControlSettlementKernel.source-manifest.json";
    internal const string LeanFileName = "EvmFrameControlSettlementKernel.lean";

    internal const string ExtractorVersion = "1.1.0-stage-d-single-iteration";
    internal const string KernelName = "standard-mainnet-amsterdam-evm-frame-control-settlement-single-iteration-stage-d";
    internal const string AcceptanceState = "stage-d-single-iteration-admitted";
    internal const string ClosedFork = "Nethermind.Specs.Forks.Amsterdam";
    internal const string ClosedGasPolicy = "Nethermind.Evm.GasPolicy.EthereumGasPolicy";
    internal const string ClosedRoot =
        "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteTransaction<TTracingInst>(Nethermind.Evm.VmState<Nethermind.Evm.GasPolicy.EthereumGasPolicy>,Nethermind.Evm.State.IWorldState,Nethermind.Evm.Tracing.ITxTracer)";

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
        "build", "frame", "dispatch", "frame-state", "code-deposit", "precompile", "world-adapter",
        "bytecode", "child-preparation", "gas-settlement", "tracing", "processing", "di", "fork",
    };

    private static readonly HashSet<string> DependencyKinds = new(StringComparer.Ordinal)
    {
        "generated", "proof",
    };

    internal static ExtractionResult Extract(string repoRoot, string outputDirectory, string? leanOutputPath = null)
    {
        string root = Path.GetFullPath(repoRoot);
        Admission admission = ReadAdmission(root, requireEmbeddedIdentity: true);
        (IrDocument ir, byte[] irBytes, byte[] leanBytes, byte[] manifestBytes) = BuildArtifacts(root, admission);

        Directory.CreateDirectory(outputDirectory);
        string irPath = Path.Combine(outputDirectory, IrFileName);
        string manifestPath = Path.Combine(outputDirectory, ManifestFileName);
        string leanPath = leanOutputPath ?? Path.Combine(outputDirectory, LeanFileName);
        WriteIfChanged(irPath, irBytes);
        WriteIfChanged(manifestPath, manifestBytes);
        WriteIfChanged(leanPath, leanBytes);

        return new(
            irPath,
            manifestPath,
            leanPath,
            ir.Sources.Length,
            ir.Members.Length,
            ir.Dependencies.Length,
            ir.Branches.Length,
            Hash(irBytes),
            Hash(manifestBytes),
            Hash(leanBytes));
    }

    /// <summary>Builds all artifacts in memory so tests can exercise determinism without filesystem writes.</summary>
    internal static (IrDocument Ir, byte[] IrBytes, byte[] LeanBytes, byte[] ManifestBytes) BuildForTest(string repoRoot)
    {
        Admission admission = ReadAdmission(Path.GetFullPath(repoRoot), requireEmbeddedIdentity: false);
        return BuildArtifacts(Path.GetFullPath(repoRoot), admission);
    }

    internal static void ValidateExistingArtifacts(string repoRoot, string artifactDirectory, string? leanPath = null)
    {
        string root = Path.GetFullPath(repoRoot);
        Admission admission = ReadAdmission(root, requireEmbeddedIdentity: true);
        (IrDocument expectedIr, byte[] expectedIrBytes, byte[] expectedLeanBytes, byte[] expectedManifestBytes) =
            BuildArtifacts(root, admission);
        RequireEqual(Path.Combine(artifactDirectory, IrFileName), expectedIrBytes, "IR");
        RequireEqual(Path.Combine(artifactDirectory, ManifestFileName), expectedManifestBytes, "source manifest");
        RequireEqual(leanPath ?? Path.Combine(artifactDirectory, LeanFileName), expectedLeanBytes, "Lean");
        ValidateShape(expectedIr);
    }

    internal static IrDocument DesignIr() => BuildDesign([], [], []);

    internal static void ValidateEmitterInput(IrDocument document, string irSha256)
    {
        ValidateShape(document);
        EvmFrameControlSettlementLeanEmitter.ValidateSha256(irSha256, "Canonical IR");
        if (document.AcceptanceState != AcceptanceState || document.Boundary.Adapters.Length < 10 ||
            document.Boundary.Oracles.Length < 2 || document.Branches.Length < 24)
            throw new ExtractionException("Stage D emitter input is not an admitted single-iteration frame-control profile.");
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
                throw new ExtractionException($"Stage D exact source profile rejected {source.Path}: expected {source.Sha256}, got {actualHash}.");

            string text = StrictUtf8.GetString(bytes);
            if (!source.Path.EndsWith(".cs", StringComparison.Ordinal))
            {
                // MSBuild inputs are pinned raw bytes; only C# production sources enter
                // Roslyn member extraction.  Reusing the byte digest keeps the manifest
                // schema uniform without pretending XML is C# syntax.
                sources.Add(new(source.Role, source.Path, actualHash, actualHash));
                continue;
            }

            SyntaxTree tree = CSharpSyntaxTree.ParseText(
                text,
                new CSharpParseOptions(LanguageVersion.CSharp14, DocumentationMode.Parse, SourceCodeKind.Regular),
                source.Path,
                StrictUtf8);
            foreach (Diagnostic diagnostic in tree.GetDiagnostics())
                if (diagnostic.Severity == DiagnosticSeverity.Error)
                    throw new ExtractionException($"Stage D Roslyn parse failed for {source.Path}: {diagnostic.Id}.");

            trees.Add(source.Path, tree);
            CompilationUnitSyntax syntaxRoot = tree.GetCompilationUnitRoot();
            string syntaxHash = Hash(StrictUtf8.GetBytes(syntaxRoot.NormalizeWhitespace().ToFullString()));
            sources.Add(new(source.Role, source.Path, actualHash, syntaxHash));
        }

        List<MemberIdentity> members = [];
        foreach (MemberAdmission admissionEntry in admission.Members)
        {
            if (!trees.TryGetValue(admissionEntry.Path, out SyntaxTree? tree))
                throw new ExtractionException($"Stage D member is outside the source closure: {admissionEntry.Path}.");
            CollectMember(tree.GetCompilationUnitRoot(), admissionEntry, members);
        }

        List<DependencyIdentity> dependencies = [];
        foreach (DependencyAdmission dependency in admission.Dependencies)
        {
            string path = ResolveCanonical(root, dependency.Path);
            byte[] bytes = File.ReadAllBytes(path);
            string actualHash = Hash(bytes);
            if (!actualHash.Equals(dependency.Sha256, StringComparison.Ordinal))
                throw new ExtractionException($"Stage D exact dependency profile rejected {dependency.Path}: expected {dependency.Sha256}, got {actualHash}.");
            dependencies.Add(new(dependency.Kind, dependency.Name, dependency.Path, actualHash, dependency.Binding,
                [.. dependency.Theorems]));
        }

        IrDocument ir = BuildDesign(sources.ToArray(), members.ToArray(), dependencies.ToArray());
        ValidateShape(ir);
        byte[] irBytes = Serialize(ir);
        byte[] leanBytes = EvmFrameControlSettlementLeanEmitter.Emit(ir, Hash(irBytes));
        SourceManifest manifest = new(
            SchemaVersion: 1,
            ExtractorVersion,
            RoslynVersion: typeof(CSharpSyntaxTree).Assembly.GetName().Version?.ToString() ?? "unknown",
            LanguageVersion: LanguageVersion.CSharp14.ToString(),
            KernelName,
            ir.Sources,
            ir.Members,
            ir.Dependencies,
            new(IrFileName, Hash(irBytes)),
            new(LeanFileName, Hash(leanBytes)),
            CombinedDigest(ir.Sources.Select(static source => $"{source.Role}|{source.Path}|{source.Sha256}")),
            CombinedDigest(ir.Members.Select(static member =>
                $"{member.SourcePath}|{member.Owner}|{member.Member}|{member.SyntaxKind}|{member.Signature}")),
            [.. ir.ReviewedOperationalBindings]);
        byte[] manifestBytes = Serialize(manifest);
        ValidateManifest(manifest, irBytes, leanBytes);
        return (ir, irBytes, leanBytes, manifestBytes);
    }

    private static IrDocument BuildDesign(SourceIdentity[] sources, MemberIdentity[] members,
        DependencyIdentity[] dependencies) => new(
        SchemaVersion: 1,
        ExtractorVersion,
        KernelName,
        AcceptanceState,
        ClosedRoot,
        ClosedFork,
        ClosedGasPolicy,
        new(
            Adapters:
            [
                "FreshFramePreparationAdapter",
                "ContinuationFramePreparationAdapter",
                "ReturnDataClearAdapter",
                "BytecodeFrameDispatchAdapter",
                "FullPrecompileFrameDispatchAdapter",
                "DirectInlineStaticPrecompileOpcodeOutcomeAdapter",
                "NestedCreateCodeDepositSettlementAdapter",
                "GasAndRefundSettlementAdapter",
                "WorldJournalSettlementAdapter",
                "TraceLifecycleSettlementAdapter",
                "FrameCleanupScopeAdapter",
                "CallerOwnedTopStateAndRollbackAdapter",
            ],
            Oracles:
            [
                "BytecodeOpcodeOutcomeOracle",
                "FullPrecompileFrameOutcomeOracle",
            ],
            FixedWidth:
            [
                "Every admitted input, fresh intermediate, prepared frame, dispatch outcome, settlement outcome, cleanup outcome, and one-iteration output satisfies the fixed-width admission predicate.",
                "opcodeCount, outputLength, all execution/state-gas counters and baselines, advanced state-gas refund, refundCounter, and VmState execution refund use explicit FrameFieldBounds.",
                "Fixed-width facts are scoped to reachable admitted states and adapter outputs, never universally quantified over arbitrary Machine values.",
                "Lean Nat arithmetic is related to production checked or saturating arithmetic only through explicit adapter simulation obligations.",
            ],
            Cancellation:
            [
                "Only InvocationResult.cancelled denotes cancelable bytecode RunDispatchLoop polling; a full precompile frame has no blanket driver poll.",
                "The entry poll occurs after the code-boundary early return and before the first opcode.",
                "The later poll occurs only after a normal positive completed 1024-opcode batch with a successor program counter.",
                "Terminal or exceptional dispatch results at a count boundary are not overwritten by cancellation.",
                "CancellationTxTracer callbacks can abruptly throw outside RunDispatchLoop, including action tracing around a full precompile; Stage D represents that only as escaped invocation/unwind, never as a driver poll.",
                "OperationCanceledException unwinds ExecuteTransaction through FrameCleanupScope; top-state disposal and world rollback are explicit external caller obligations.",
            ],
            Fuel:
            [
                "The public claim is one iteration and contains no run-fuel or whole-loop theorem.",
                "Whole-loop induction and reachability of successive admitted states remain open obligations.",
                "Outer transaction processing, deployment, refund, finalization, and caller rollback remain outside Stage D.",
            ]),
        ["fresh", "continuation", "running"],
        ["bytecode", "precompile"],
        ["success", "revert", "exception", "incomplete"],
        [
            "bytecodeFrame",
            "fullPrecompileFrame",
            "continued",
            "suspended",
            "nestedSettlement",
            "topLevelSubstate",
            "cancelledUnwind",
            "escapedUnwind",
        ],
        [
            "continued",
            "suspend",
            "regularSuccessNested",
            "topLevelSuccess",
            "createSuccessNested",
            "codeDepositInvalidNested",
            "codeDepositOutOfGasNested",
            "revertNested",
            "revertTop",
            "exceptionTop",
            "exceptionNested",
            "fullPrecompileOutOfGasNested",
            "fullPrecompileOutOfGasTop",
            "fullPrecompileReturnedFailureNested",
            "fullPrecompileReturnedFailureTop",
            "fullPrecompileManagedExceptionNested",
            "fullPrecompileManagedExceptionTop",
            "cancelled",
            "escaped",
        ],
        [
            "clearReturnDataOnFreshOnly",
            "freshOrContinuationPreparation",
            "selectCurrentFrameBytecodeOrFullPrecompile",
            "executeSelectedFrame",
            "bytecodeDispatchCancellationAtEntryOrCompletedBatchWithSuccessor",
            "recordBytecodeDirectInlineStaticPrecompileOutcome",
            "classifyReturnedThrownOrFullPrecompileOutcome",
            "settleNestedCreateDepositAfterInitcodeSuccess",
            "applyVmFrameExitSettlement",
            "cleanupFrameUnwindWhenIterationLeavesVm",
        ],
        Branches(),
        sources,
        members,
        dependencies,
        ReviewedOperationalBindings(),
        Exclusions());

    internal static BranchDescriptor[] Branches() =>
    [
        new("FreshFramePreparation", "fresh", ["clear-return-data-before-fresh-dispatch", "initialize-current-frame"], "preparation"),
        new("ContinuationFramePreparation", "continuation", ["retain-return-data", "retain-child-copy-metadata"], "preparation"),
        new("BytecodeFrameDispatch", "bytecodeFrame", ["trace-entry-when-fresh", "execute-call-or-invalid-code"], "bytecode-frame-result"),
        new("FullPrecompileFrameDispatch", "fullPrecompileFrame", ["execute-current-precompile-frame", "complete-current-non-create-frame-or-named-failure"], "full-precompile-result"),
        new("DirectInlineStaticPrecompileOpcodeOutcome", "bytecodeOpcode", ["STATICCALL-direct-leaf-before-child-rent", "remain-in-bytecode-frame"], "bytecode-frame-result"),
        new("ReturnedContinue", "returned", ["retain-current-frame", "advance-loop"], "continued"),
        new("ReturnedSuspend", "returned", ["prepare-child-frame", "retain-parent"], "suspend"),
        new("ReturnedRegularSuccessNested", "returned", ["pop-parent", "merge-child", "repay-state-gas-spill"], "regularSuccessNested"),
        new("ReturnedTopLevelSuccess", "returned", ["trace-action-end", "prepare-top-level-substate"], "topLevelSuccess"),
        new("ReturnedCreateSuccessNested", "returned", ["refund-initcode-gas", "enter-nested-code-deposit-settlement"], "createSuccessNested"),
        new("NestedCreateCodeDepositInvalid", "nestedCreateDeposit", ["apply-invalid-code-create-failure", "do-not-dispatch-as-opcode"], "codeDepositInvalidNested"),
        new("NestedCreateCodeDepositOutOfGas", "nestedCreateDeposit", ["apply-code-deposit-out-of-gas", "do-not-dispatch-as-opcode"], "codeDepositOutOfGasNested"),
        new("ReturnedRevertNested", "returned", ["restore-snapshot", "restore-child-state-gas", "prepare-revert-output"], "revertNested"),
        new("ReturnedRevertTop", "returned", ["prepare-top-level-substate", "top-level-result"], "revertTop"),
        new("ThrownEvm", "thrownEvm", ["route-to-failure-handler", "settle-top-or-nested"], "exceptionTop-or-exceptionNested"),
        new("ThrownOverflow", "thrownOverflow", ["route-to-failure-handler", "settle-top-or-nested"], "exceptionTop-or-exceptionNested"),
        new("EscapedInvocation", "escapedInvocation", ["abrupt-unwind", "do-not-synthesize-success"], "escaped"),
        new("CancelledBeforeFirstOpcode", "cancelled", ["cancelable-bytecode-only", "abrupt-unwind"], "cancelled"),
        new("CancelledAfterCompletedBatch", "cancelled", ["normal-1024-opcode-batch", "successor-required", "abrupt-unwind"], "cancelled"),
        new("FullPrecompileOutOfGasNested", "fullPrecompileOutOfGas", ["price-before-leaf", "nested-managed-failure"], "fullPrecompileOutOfGasNested"),
        new("FullPrecompileOutOfGasTop", "fullPrecompileOutOfGas", ["price-before-leaf", "top-managed-failure"], "fullPrecompileOutOfGasTop"),
        new("FullPrecompileReturnedFailureNested", "fullPrecompileReturnedFailure", ["returned-false", "nested-managed-failure"], "fullPrecompileReturnedFailureNested"),
        new("FullPrecompileReturnedFailureTop", "fullPrecompileReturnedFailure", ["returned-false", "top-managed-failure"], "fullPrecompileReturnedFailureTop"),
        new("FullPrecompileManagedExceptionNested", "fullPrecompileManagedException", ["managed-exception", "nested-failure-handler"], "fullPrecompileManagedExceptionNested"),
        new("FullPrecompileManagedExceptionTop", "fullPrecompileManagedException", ["managed-exception", "top-failure-handler"], "fullPrecompileManagedExceptionTop"),
        new("FrameCleanupScope", "abrupt-unwind", ["dispose-active-child-frames", "drain-state-stack", "leave-normal-exit-to-settlement-and-caller", "leave-top-state-to-caller"], "cleanup"),
    ];

    private static string[] ReviewedOperationalBindings() =>
    [
        "Exact source bytes, syntax identities, member selectors, and imported theorem names are derived from the admission profile; C# statement bodies are not translated into Lean semantics.",
        "The generated driver and independently planned reference are separate handwritten operational transcriptions over one shared CanonicalLeaves record; their theorem checks control agreement, not production leaf simulation.",
        "ExecuteTransaction clears ReturnDataBuffer only on a non-continuation frame before fresh dispatch; continuation preserves the prior child return and copy metadata.",
        "The current VmState IsPrecompile/CodeInfo state selects full-frame precompile execution; its top-versus-nested settlement classification reads IsTopLevel.",
        "The STATICCALL direct precompile fast path is an opcode outcome in a bytecode parent before a child frame is rented, never a current-frame dispatch subject.",
        "Returned child results either suspend a parent or restore it in LIFO order; top-versus-nested CREATE classification reads the current VmState.ExecutionType, while a top frame produces PrepareTopLevelSubstate and leaves outer transaction work excluded.",
        "Nested CREATE code deposit occurs after successful initcode return and gas refund in HandleCreate/TryChargeAndDepositCode, not as an opcode invocation tag.",
        "Regular, create, revert, and exceptional VM-frame settlement expose state, every observed gas/refund field, returndata, journal, log, trace, and lifecycle effects through individual simulation obligations.",
        "Full-frame precompile success completes its current non-CREATE frame with nullable PrecompileSuccess=true; out-of-gas, returned failure, and managed exception have separate top and nested settlement leaves, with the split derived from the current IsTopLevel flag.",
        "InvocationResult.cancelled models only RunDispatchLoop entry and positive completed-batch-with-successor polling; terminal and exceptional opcode results retain their own result, while CancellationTxTracer callback cancellation is an escaped invocation/unwind rather than a driver poll.",
        "FrameCleanupScope disposes only exceptional-unwind active child frames and drains the state stack; normal completed exits are settled before the scope's no-op, while top VmState, pooled memory, and world rollback remain caller-owned external obligations.",
        "Critical driver helpers for cancellation, parent restoration, CREATE settlement, full/inline precompile execution, dispatch batching, VmState ownership, and pooled memory disposal are named member admissions.",
        "SourceAdapterBindings is an explicit assumption interface: it carries identity-checked Stage A, Stage B, Stage C, state-gas, and pricing theorem witnesses and assumes each canonical bytecode/full-precompile dispatch leaf equals its caller oracle on admitted states; it does not discharge semantic adapter composition, which remains open alongside ProductionLeafSimulation.",
        "The central theorem is a single admitted iteration control-agreement check over shared canonical leaves; ProductionLeafSimulation separately states per-leaf production obligations only for AdmittedStep-derived invocation/route pairs.",
        "AdmissionWitnesses constructively instantiates the complete control-agreement and production-obligation interfaces for terminal bytecode, continuation/suspend/direct-inline bytecode, and full-frame precompile fixtures using nonproduction synthetic leaves.",
        "Whole-loop induction, reachability of successive admissions, outer transaction deployment/refund/finalization, and caller rollback are intentionally open.",
    ];

    private static string[] Exclusions() =>
    [
        "whole-EVM verification and whole-loop induction",
        "opcode body semantics and opcode package coverage beyond named adapter theorems",
        "cryptographic, native, and precompile leaf implementation correctness",
        "concrete trie, database, persistence, commit, and crash durability behavior",
        "CLR, JIT, AOT, unsafe layout, function-pointer dispatch, pooling implementation, operating system, and hardware correctness",
        "async scheduling, RPC, networking, TxPool, block-production concurrency, and receipt serialization",
        "transaction validation, outer deployment/refund/finalization, caller rollback, and complete block-processing semantics",
        "arbitrary fork, gas-policy, tracing, or cancellation implementations not admitted by the closed root",
        "reachability of every production exception, cancellation unwind, successive loop state, and every native process termination path",
        "correctness of external adapter and oracle implementations beyond their supplied contracts",
    ];

    private static void CollectMember(CompilationUnitSyntax root, MemberAdmission admission, List<MemberIdentity> output)
    {
        int found = 0;
        foreach (SyntaxNode node in root.DescendantNodes())
        {
            if (!MatchesKind(node, admission.SyntaxKind) || !MatchesName(node, admission.Name))
                continue;
            string owner = OwnerName(node);
            if (!owner.Equals(admission.Owner, StringComparison.Ordinal))
                continue;
            found++;
            output.Add(new(admission.Path, owner, admission.Name, node.Kind().ToString(), CanonicalSignature(node)));
        }

        if (found == 0)
            throw new ExtractionException($"Stage D member selector did not resolve: {admission.Path}|{admission.Owner}|{admission.Name}|{admission.SyntaxKind}.");
    }

    private static bool MatchesKind(SyntaxNode node, string expected) => expected switch
    {
        "type" => node is TypeDeclarationSyntax,
        // C# primary constructors are represented by the declaring type rather
        // than a ConstructorDeclarationSyntax node.
        "constructor" => node is ConstructorDeclarationSyntax ||
            node is TypeDeclarationSyntax type && IsPrimaryConstructor(type),
        "method" => node is MethodDeclarationSyntax,
        "property" => node is PropertyDeclarationSyntax,
        "field" => node is FieldDeclarationSyntax,
        "operator" => node is OperatorDeclarationSyntax,
        _ => throw new ExtractionException($"Unknown Stage D member syntax kind {expected}."),
    };

    private static bool MatchesName(SyntaxNode node, string expected) => node switch
    {
        TypeDeclarationSyntax type => type.Identifier.ValueText.Equals(expected, StringComparison.Ordinal),
        ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText.Equals(expected, StringComparison.Ordinal),
        MethodDeclarationSyntax method => method.Identifier.ValueText.Equals(expected, StringComparison.Ordinal),
        PropertyDeclarationSyntax property => property.Identifier.ValueText.Equals(expected, StringComparison.Ordinal),
        FieldDeclarationSyntax field => field.Declaration.Variables.Any(variable =>
            variable.Identifier.ValueText.Equals(expected, StringComparison.Ordinal)),
        OperatorDeclarationSyntax @operator => @operator.OperatorToken.Text.Equals(expected, StringComparison.Ordinal),
        _ => false,
    };

    private static bool IsPrimaryConstructor(TypeDeclarationSyntax type) =>
        type.ChildNodes().OfType<ParameterListSyntax>().Any();

    private static string OwnerName(SyntaxNode node) => node switch
    {
        TypeDeclarationSyntax type => type.Identifier.ValueText,
        _ => node.Ancestors().OfType<TypeDeclarationSyntax>().Select(static type => type.Identifier.ValueText).FirstOrDefault()
            ?? throw new ExtractionException($"Stage D member has no declaring type: {node.Kind()}"),
    };

    private static string CanonicalSignature(SyntaxNode node)
    {
        SyntaxNode signature = node switch
        {
            MethodDeclarationSyntax method => method.WithBody(null).WithExpressionBody(null),
            ConstructorDeclarationSyntax constructor => constructor.WithBody(null).WithExpressionBody(null),
            TypeDeclarationSyntax type when IsPrimaryConstructor(type) =>
                type.ChildNodes().OfType<ParameterListSyntax>().Single(),
            _ => node,
        };
        return signature.NormalizeWhitespace().ToFullString();
    }

    internal static void ValidateEmbeddedAdmission(string repoRoot) =>
        _ = ReadAdmission(Path.GetFullPath(repoRoot), requireEmbeddedIdentity: true);

    private static Admission ReadAdmission(string root, bool requireEmbeddedIdentity)
    {
        string admissionPath = ResolveCanonical(root, Package + AdmissionPath);
        byte[] bytes;
        if (requireEmbeddedIdentity)
        {
            bytes = ReadResource(AdmissionResourceSuffix);
            RequireEqual(admissionPath, bytes, "embedded Stage D admission");
        }
        else
        {
            bytes = File.ReadAllBytes(admissionPath);
        }
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
                        if (!sourcePaths.Add(fields[2])) throw new ExtractionException($"Duplicate Stage D source {fields[2]}.");
                        sources.Add(new(fields[1], fields[2], fields[3]));
                        break;
                    case "member" when fields.Length == 5:
                        RequirePath(fields[1]);
                        RequireText(fields[2], "member owner");
                        RequireText(fields[3], "member name");
                        RequireText(fields[4], "member syntax kind");
                        if (!memberKeys.Add(string.Join('|', fields[1..])))
                            throw new ExtractionException($"Duplicate Stage D member {string.Join('|', fields[1..])}.");
                        members.Add(new(fields[1], fields[2], fields[3], fields[4]));
                        break;
                    case "dependency" when fields.Length == 7:
                        RequireRole(fields[1], DependencyKinds, "dependency kind");
                        RequireText(fields[2], "dependency name");
                        RequirePath(fields[3]);
                        RequireHash(fields[4]);
                        RequireText(fields[5], "dependency binding");
                        string[] theorems = fields[6].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        if (theorems.Length == 0 || theorems.Any(static theorem => theorem.Contains('|')))
                            throw new ExtractionException("A Stage D dependency must name at least one theorem.");
                        if (!dependencyKeys.Add(string.Join('|', fields[1..5])))
                            throw new ExtractionException($"Duplicate Stage D dependency {fields[2]}.");
                        dependencies.Add(new(fields[1], fields[2], fields[3], fields[4], fields[5], theorems));
                        break;
                    default:
                        throw new ExtractionException("expected source|role|path|sha256, member|path|owner|name|syntaxKind, or dependency|kind|name|path|sha256|binding|theorems");
                }
            }
            catch (ExtractionException exception)
            {
                throw new ExtractionException($"Invalid Stage D admission line {lineNumber}: {exception.Message}");
            }
        }

        if (sources.Count == 0 || members.Count == 0 || dependencies.Count == 0)
            throw new ExtractionException("Stage D admission must contain nonempty source, member, and dependency closures.");
        foreach (MemberAdmission member in members)
            if (!sourcePaths.Contains(member.Path))
                throw new ExtractionException($"Stage D member is not inside source closure: {member.Path}.");
        return new(sources.ToArray(), members.ToArray(), dependencies.ToArray());
    }

    private static void ValidateShape(IrDocument ir)
    {
        if (ir.SchemaVersion != 1 || ir.ExtractorVersion != ExtractorVersion || ir.Kernel != KernelName ||
            ir.AcceptanceState != AcceptanceState || ir.ClosedRoot != ClosedRoot || ir.ClosedFork != ClosedFork ||
            ir.ClosedGasPolicy != ClosedGasPolicy)
            throw new ExtractionException("Stage D IR header does not match the closed Amsterdam profile.");
        if (ir.Boundary is null || ir.Boundary.Adapters is null || ir.Boundary.Oracles is null ||
            ir.Boundary.FixedWidth is null || ir.Boundary.Cancellation is null || ir.Boundary.Fuel is null)
            throw new ExtractionException("Stage D IR boundary is incomplete.");
        RequireDistinctNonempty(ir.Boundary.Adapters, "adapters");
        RequireDistinctNonempty(ir.Boundary.Oracles, "oracles");
        RequireDistinctNonempty(ir.Boundary.FixedWidth, "fixed-width assumptions");
        RequireDistinctNonempty(ir.Boundary.Cancellation, "cancellation assumptions");
        RequireDistinctNonempty(ir.Boundary.Fuel, "fuel assumptions");
        RequireSequence(ir.Phases, ["fresh", "continuation", "running"], "phases");
        RequireSequence(ir.FrameKinds, ["bytecode", "precompile"], "source frame kinds");
        RequireSequence(ir.ExitKinds, ["success", "revert", "exception", "incomplete"], "exit kinds");
        RequireSequence(ir.DispatchOrder, [
            "clearReturnDataOnFreshOnly", "freshOrContinuationPreparation", "selectCurrentFrameBytecodeOrFullPrecompile",
            "executeSelectedFrame", "bytecodeDispatchCancellationAtEntryOrCompletedBatchWithSuccessor",
            "recordBytecodeDirectInlineStaticPrecompileOutcome", "classifyReturnedThrownOrFullPrecompileOutcome",
            "settleNestedCreateDepositAfterInitcodeSuccess", "applyVmFrameExitSettlement",
            "cleanupFrameUnwindWhenIterationLeavesVm",
        ], "dispatch order");
        if (ir.Branches is null || ir.Branches.Length < 24 || ir.Branches.Any(static branch => branch is null ||
            string.IsNullOrWhiteSpace(branch.Name) || string.IsNullOrWhiteSpace(branch.Invocation) ||
            string.IsNullOrWhiteSpace(branch.Settlement) || branch.Effects is null || branch.Effects.Length == 0))
            throw new ExtractionException("Stage D IR must carry every named control/settlement branch.");
        RequireUnique(ir.Branches.Select(static branch => branch.Name), "branch names");
        if (ir.Sources is null || ir.Members is null || ir.Dependencies is null || ir.ReviewedOperationalBindings is null || ir.Exclusions is null)
            throw new ExtractionException("Stage D IR closure fields may not be null.");
        RequireUnique(ir.Sources.Select(static source => source.Path), "source paths");
        RequireUnique(ir.Members.Select(static member =>
            $"{member.SourcePath}|{member.Owner}|{member.Member}|{member.SyntaxKind}|{member.Signature}"), "resolved members");
        RequireUnique(ir.Dependencies.Select(static dependency => dependency.Path), "dependency paths");
        RequireDistinctNonempty(ir.ReviewedOperationalBindings, "reviewed operational bindings");
        RequireDistinctNonempty(ir.Exclusions, "exclusions");
        if (ir.Sources.Length < 80 || ir.Members.Length < 70 || ir.Dependencies.Length < 10)
            throw new ExtractionException("Stage D production closure is smaller than the reviewed frame-control surface.");
    }

    private static void ValidateManifest(SourceManifest manifest, byte[] irBytes, byte[] leanBytes)
    {
        if (manifest.SchemaVersion != 1 || manifest.ExtractorVersion != ExtractorVersion || manifest.Kernel != KernelName ||
            manifest.Ir is null || manifest.Lean is null || manifest.Sources is null || manifest.Members is null || manifest.Dependencies is null)
            throw new ExtractionException("Stage D source manifest is incomplete.");
        if (manifest.Ir.Path != IrFileName || manifest.Ir.Sha256 != Hash(irBytes) ||
            manifest.Lean.Path != LeanFileName || manifest.Lean.Sha256 != Hash(leanBytes))
            throw new ExtractionException("Stage D artifact identities do not match emitted bytes.");
        if (manifest.ReviewedOperationalBindings is null || manifest.ReviewedOperationalBindings.Length < 10)
            throw new ExtractionException("Stage D manifest lost reviewed operational bindings.");
    }

    private static string CombinedDigest(IEnumerable<string> values) => Hash(StrictUtf8.GetBytes(string.Join('\n', values) + "\n"));

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
                    throw new ExtractionException($"Stage D path is missing, ambiguous, or noncanonical: {relative}.");
                exact = candidate;
            }
            current = exact ?? throw new ExtractionException($"Stage D path is missing: {relative}.");
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ExtractionException($"Stage D source closure disallows reparse points: {relative}.");
        }
        return current;
    }

    private static byte[] ReadResource(string suffix)
    {
        Assembly assembly = typeof(EvmFrameControlSettlementProfile).Assembly;
        string resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(suffix, StringComparison.Ordinal))
            ?? throw new ExtractionException($"Missing embedded Stage D resource {suffix}.");
        using Stream resource = assembly.GetManifestResourceStream(resourceName)
            ?? throw new ExtractionException($"Cannot read embedded Stage D resource {resourceName}.");
        using MemoryStream bytes = new();
        resource.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static void WriteIfChanged(string path, byte[] bytes)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            File.WriteAllBytes(path, bytes);
    }

    private static void RequireEqual(string path, byte[] expected, string description)
    {
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(expected))
            throw new ExtractionException($"Stage D deterministic {description} mismatch: {path}.");
    }

    private static void RequirePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || Path.IsPathRooted(path) ||
            path.Split('/').Any(static segment => segment is "" or "." or ".."))
            throw new ExtractionException($"Unsafe or noncanonical Stage D relative path {path}.");
    }

    private static void RequireHash(string hash)
    {
        if (hash.Length != 64 || hash.Any(static character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ExtractionException("Stage D admission hashes must be lowercase SHA-256 values.");
    }

    private static void RequireRole(string value, HashSet<string> allowed, string description)
    {
        if (!allowed.Contains(value)) throw new ExtractionException($"Unknown Stage D {description} {value}.");
    }

    private static void RequireText(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('|'))
            throw new ExtractionException($"Stage D {description} is empty or contains a field separator.");
    }

    private static void RequireDistinctNonempty(IEnumerable<string> values, string description)
    {
        string[] array = [.. values];
        if (array.Length == 0 || array.Any(string.IsNullOrWhiteSpace) || array.Distinct(StringComparer.Ordinal).Count() != array.Length)
            throw new ExtractionException($"Stage D {description} must be nonempty and distinct.");
    }

    private static void RequireSequence(string[] actual, string[] expected, string description)
    {
        if (actual is null || !actual.SequenceEqual(expected, StringComparer.Ordinal))
            throw new ExtractionException($"Stage D {description} changed from the admitted order.");
    }

    private static void RequireUnique(IEnumerable<string> values, string description)
    {
        string[] array = [.. values];
        if (array.Distinct(StringComparer.Ordinal).Count() != array.Length)
            throw new ExtractionException($"Stage D {description} must be unique.");
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static byte[] Serialize<T>(T value) => StrictUtf8.GetBytes(JsonSerializer.Serialize(value, JsonOptions) + "\n");

    private sealed record Admission(SourceAdmission[] Sources, MemberAdmission[] Members, DependencyAdmission[] Dependencies);
}
