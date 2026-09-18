// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.EvmFrameMachineExtractor.Test;

[TestFixture]
public sealed class EvmFrameMachineExtractorTests
{
    private const string RootMarker = "src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs";

    [Test]
    public void Production_sources_extract_to_byte_identical_artifacts()
    {
        using TemporaryDirectory first = new("frame-stage-a-first");
        using TemporaryDirectory second = new("frame-stage-a-second");
        ExtractionResult left = Extract(RepoRoot(), first.Path);
        ExtractionResult right = Extract(RepoRoot(), second.Path);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(left.IrPath), Is.EqualTo(File.ReadAllBytes(right.IrPath)));
            Assert.That(File.ReadAllBytes(left.ManifestPath), Is.EqualTo(File.ReadAllBytes(right.ManifestPath)));
            Assert.That(File.ReadAllBytes(left.LeanPath), Is.EqualTo(File.ReadAllBytes(right.LeanPath)));
        }
    }

    [Test]
    public void Checked_artifacts_equal_fresh_extraction()
    {
        using TemporaryDirectory fresh = new("frame-stage-a-fresh");
        ExtractionResult result = Extract(RepoRoot(), fresh.Path);
        string generated = GeneratedDirectory();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(result.IrPath), Is.EqualTo(ReadChecked(EvmFrameMachineProfile.IrFileName)));
            Assert.That(File.ReadAllBytes(result.ManifestPath), Is.EqualTo(ReadChecked(EvmFrameMachineProfile.ManifestFileName)));
            Assert.That(File.ReadAllBytes(result.LeanPath), Is.EqualTo(ReadChecked(EvmFrameMachineProfile.LeanFileName)));
            Assert.That(generated, Is.Not.Empty);
        }
    }

    [Test]
    public void Stage_a_closes_exact_source_member_and_route_cardinalities()
    {
        IrDocument document = ReadIr();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(document.AcceptanceState, Is.EqualTo("stage-a-admitted"));
            Assert.That(document.Sources, Has.Length.EqualTo(114));
            Assert.That(document.Admissions, Has.Length.EqualTo(181));
            Assert.That(document.OpcodeRoutes, Has.Length.EqualTo(1024));
            Assert.That(document.OpcodeRoutes.Count(static route => route.RouteKind == "enabled"), Is.EqualTo(612));
            Assert.That(document.OpcodeRoutes.Count(static route => route.RouteKind == "badInstruction"), Is.EqualTo(412));
            Assert.That(document.OpcodeRoutes.Count(static route => route.RouteKind == "enabled" && route.Admitted), Is.EqualTo(612));
            Assert.That(document.OpcodeRoutes.Count(static route => route.RouteKind == "enabled" && !route.Admitted), Is.Zero);
            Assert.That(document.OpcodeRoutes, Has.None.Property(nameof(OpcodeRouteDescriptor.RouteKind)).EqualTo("disabled"));
            Assert.That(document.OpcodeRoutes.Select(static route => (route.DispatchTable, route.Byte)).Distinct().Count(), Is.EqualTo(1024));
            Assert.That(document.OpcodeRoutes.Where(static route => route.RouteKind == "badInstruction")
                .GroupBy(static route => route.DispatchTable).Select(static group => group.Count()),
                Is.All.EqualTo(103));
        }
    }

    [TestCase("NoTrace")]
    [TestCase("NoTraceCancelable")]
    [TestCase("Traced")]
    [TestCase("TracedCancelable")]
    public void Every_dispatch_table_has_every_byte_exactly_once(string table)
    {
        OpcodeRouteDescriptor[] routes = ReadIr().OpcodeRoutes.Where(route => route.DispatchTable == table).ToArray();
        Assert.That(routes.Select(static route => route.Byte), Is.EqualTo(Enumerable.Range(0, 256)));
    }

    [Test]
    public void Overlap_and_composed_package_ownership_are_explicit()
    {
        IrDocument document = ReadIr();
        OpcodeRouteDescriptor[] slotNum = document.OpcodeRoutes.Where(static route => route.Byte == 0x4b).ToArray();
        OpcodeRouteDescriptor[] callDataLoad = document.OpcodeRoutes.Where(static route => route.Byte == 0x35).ToArray();
        OpcodeRouteDescriptor[] callCreate = document.OpcodeRoutes.Where(static route =>
            route.Package == "CallCreateOpcodeExtractor").ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(slotNum, Has.Length.EqualTo(4));
            Assert.That(slotNum, Has.All.Property(nameof(OpcodeRouteDescriptor.Package)).EqualTo("ControlFlowOpcodeExtractor"));
            Assert.That(callDataLoad, Has.Length.EqualTo(4));
            Assert.That(callDataLoad, Has.All.Property(nameof(OpcodeRouteDescriptor.Package)).EqualTo("CallDataLoadOpcodeExtractor"));
            Assert.That(callDataLoad, Has.All.Property(nameof(OpcodeRouteDescriptor.Admitted)).True);
            Assert.That(callCreate, Has.Length.EqualTo(28));
            Assert.That(callCreate, Has.All.Property(nameof(OpcodeRouteDescriptor.RouteKind)).EqualTo("enabled"));
            Assert.That(callCreate, Has.All.Property(nameof(OpcodeRouteDescriptor.Admitted)).True);
            Assert.That(document.IncompleteGates, Does.Not.Contain("admit-operational-calldataload-refinement"));
            Assert.That(document.IncompleteGates, Does.Not.Contain("admit-environment-opcode-operational-refinement"));
            Assert.That(document.IncompleteGates, Does.Not.Contain("admit-call-create-opcode-operational-refinement"));
        }
    }

    [Test]
    public void Bad_instruction_routes_are_explicit_and_table_closed()
    {
        IrDocument document = ReadIr();
        foreach (OpcodeRouteDescriptor route in document.OpcodeRoutes.Where(static route => route.RouteKind == "badInstruction"))
        {
            Assert.That(route.Package, Is.EqualTo("dispatcher"));
            Assert.That(route.ClosedHandlerRoot, route.Instruction == "INVALID"
                ? Does.Contain("InvalidOpcode")
                : Does.Contain("BadInstructionOpcode"));
            Assert.That(route.ClosedHandlerRoot, Does.Not.Contain("TTracingInst"));
            Assert.That(route.ClosedHandlerRoot, Does.Not.Contain("TCancelable"));
        }
    }

    [Test]
    public void Dependencies_have_global_unique_paths_and_honest_admission_status()
    {
        SourceManifest manifest = ReadManifest();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(manifest.Dependencies, Has.Length.EqualTo(32));
            Assert.That(manifest.Dependencies.Select(static dependency => dependency.Path).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(32));
            Assert.That(manifest.Dependencies.Take(14), Has.All.Property(nameof(DependencyIdentity.Admitted)).True);
            Assert.That(manifest.Dependencies.Skip(14), Has.All.Property(nameof(DependencyIdentity.Admitted)).False);
            Assert.That(manifest.Dependencies.Take(14), Has.All.Property(nameof(DependencyIdentity.ProofModule)).Not.Null);
            Assert.That(manifest.Dependencies.Take(14), Has.All.Property(nameof(DependencyIdentity.ProofModulePath)).Not.Null);
            Assert.That(manifest.Dependencies.Take(14), Has.All.Property(nameof(DependencyIdentity.ProofModuleSha256)).Not.Null);
            Assert.That(manifest.Dependencies.Take(14).Sum(static dependency => dependency.RequiredTheorems.Length), Is.EqualTo(16));
            Assert.That(manifest.Dependencies.Take(14),
                Has.All.Property(nameof(DependencyIdentity.RequiredTheorems)).Not.Empty);
            Assert.That(manifest.Dependencies.Skip(14), Has.All.Property(nameof(DependencyIdentity.ProofModule)).Null);
            DependencyIdentity environment = manifest.Dependencies.Single(static dependency =>
                dependency.Name == "EnvironmentOpcodeExtractor");
            Assert.That(environment.RequiredTheorems.Select(static theorem => theorem.FullyQualifiedName),
                Is.EqualTo(new[] { "Eip803x.Refinement.EnvironmentOpcode.closed_amsterdam_refines" }));
            DependencyIdentity callDataLoad = manifest.Dependencies.Single(static dependency =>
                dependency.Name == "CallDataLoadOpcodeExtractor");
            Assert.That(callDataLoad.RequiredTheorems.Select(static theorem => theorem.FullyQualifiedName),
                Is.EqualTo(new[] { "Eip803x.Generated.CallDataLoadOpcodeRefinement.closed_amsterdam_refines" }));
            DependencyIdentity callCreate = manifest.Dependencies.Single(static dependency =>
                dependency.Name == "CallCreateOpcodeExtractor");
            Assert.That(callCreate.RequiredTheorems.Select(static theorem => theorem.FullyQualifiedName),
                Is.EqualTo(new[] { "Eip803x.Generated.CallCreateOpcodeRefinement.closed_amsterdam_refines" }));
            Assert.That(EvmFrameMachineProfile.InputPaths.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(174));
        }
    }

    [Test]
    public void Generated_module_is_theorem_free_and_has_no_frame_transition()
    {
        string generated = Encoding.UTF8.GetString(ReadChecked(EvmFrameMachineProfile.LeanFileName));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(generated, Does.Not.Match(@"(?m)^\s*(theorem|lemma|axiom|example|admit|sorry)\b"));
            Assert.That(generated, Does.Not.Contain("FrameMachineExecution"));
            Assert.That(generated, Does.Not.Contain("TransactionAdapter"));
            Assert.That(generated, Does.Not.Contain("def stepFrame"));
            Assert.That(generated, Does.Not.Contain("def settleChild"));
            Assert.That(generated, Does.Contain("def routeAt"));
            Assert.That(generated, Does.Contain("def precompileAt"));
            Assert.That(generated, Does.Contain("import Eip803x.Refinement.PureWordOpcode"));
            Assert.That(generated, Does.Contain("#check @Eip803x.Refinement.PureWordOpcode.extracted_execute_refines"));
            Assert.That(generated, Does.Contain("#check @PersistentStorageOpcodeExtractor.Refinement.PersistentStorageOpcode.sload_byte_refines"));
            Assert.That(generated, Does.Contain("#check @PersistentStorageOpcodeExtractor.Refinement.PersistentStorageOpcode.sstore_byte_refines"));
            Assert.That(generated, Does.Contain("import CallDataLoadOpcodeExtractor.Refinement.CallDataLoadOpcode"));
            Assert.That(generated, Does.Contain("#check @Eip803x.Generated.CallDataLoadOpcodeRefinement.closed_amsterdam_refines"));
            Assert.That(generated, Does.Contain("import Eip803x.Refinement.EnvironmentOpcode"));
            Assert.That(generated, Does.Contain("#check @Eip803x.Refinement.EnvironmentOpcode.closed_amsterdam_refines"));
            Assert.That(generated, Does.Contain("import CallCreateOpcodeExtractor.Refinement.CallCreateOpcode"));
            Assert.That(generated, Does.Contain("#check @Eip803x.Generated.CallCreateOpcodeRefinement.closed_amsterdam_refines"));
        }
    }

    [Test]
    public void Generated_output_depends_on_route_and_precompile_ir()
    {
        IrDocument admitted = ReadIr();
        string digest = new('a', 64);
        byte[] original = EvmFrameMachineLeanEmitter.Emit(admitted, digest, digest);
        OpcodeRouteDescriptor[] routes = [.. admitted.OpcodeRoutes];
        routes[0] = routes[0] with { ClosedHandlerRoot = routes[0].ClosedHandlerRoot + "Mutated" };
        PrecompileRouteDescriptor[] precompiles = [.. admitted.PrecompileRoutes];
        precompiles[0] = precompiles[0] with { ProviderRoot = precompiles[0].ProviderRoot + "Mutated" };
        OpcodePackageDescriptor[] packages = [.. admitted.OpcodePackages];
        ProofTheoremIdentity[] theorems = [.. packages[0].RequiredTheorems];
        theorems[0] = theorems[0] with { FullyQualifiedName = theorems[0].FullyQualifiedName + "Mutated" };
        packages[0] = packages[0] with { RequiredTheorems = theorems };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(EvmFrameMachineLeanEmitter.Emit(admitted with { OpcodeRoutes = routes }, digest, digest), Is.Not.EqualTo(original));
            Assert.That(EvmFrameMachineLeanEmitter.Emit(admitted with { PrecompileRoutes = precompiles }, digest, digest), Is.Not.EqualTo(original));
            Assert.That(EvmFrameMachineLeanEmitter.Emit(admitted with { OpcodePackages = packages }, digest, digest), Is.Not.EqualTo(original));
        }
    }

    [Test]
    public void Ir_rejects_every_property_omission_null_case_alias_duplicate_and_leaf_change()
    {
        byte[] bytes = ReadChecked(EvmFrameMachineProfile.IrFileName);
        IrDocument expected = EvmFrameMachineProfile.DeserializeIr(bytes, RepoRoot());
        AssertEveryPropertyMutationRejected(bytes,
            changed => EvmFrameMachineProfile.DeserializeIrAgainstExpected(changed, expected));
    }

    [Test]
    public void Manifest_rejects_every_property_omission_null_case_alias_duplicate_and_leaf_change()
    {
        byte[] bytes = ReadChecked(EvmFrameMachineProfile.ManifestFileName);
        byte[] ir = ReadChecked(EvmFrameMachineProfile.IrFileName);
        byte[] lean = ReadChecked(EvmFrameMachineProfile.LeanFileName);
        SourceManifest expected = EvmFrameMachineProfile.DeserializeManifest(bytes, RepoRoot());
        AssertEveryPropertyMutationRejected(bytes,
            changed => EvmFrameMachineProfile.DeserializeManifestAgainstExpected(changed, expected, ir, lean));
    }

    [Test]
    public void Ir_rejects_every_scalar_array_element_null_and_leaf_change()
    {
        byte[] bytes = ReadChecked(EvmFrameMachineProfile.IrFileName);
        JsonNode root = JsonNode.Parse(bytes)!;
        IrDocument expected = EvmFrameMachineProfile.DeserializeIr(bytes, RepoRoot());
        foreach (NodePath element in EnumerateScalarArrayElements(root).DistinctBy(static value => value.Shape))
        {
            foreach (bool useNull in new[] { true, false })
            {
                JsonNode changed = root.DeepClone();
                JsonArray array = LocateArray(changed, element.Parent);
                array[element.Index] = useNull ? null : ChangedValue(array[element.Index]!);
                Assert.That(() => EvmFrameMachineProfile.DeserializeIrAgainstExpected(ToBytes(changed), expected),
                    Throws.TypeOf<ExtractionException>(), $"{element.Display} {(useNull ? "null" : "leaf")}");
            }
        }
    }

    [Test]
    public void Every_pinned_source_and_dependency_rejects_a_byte_change()
    {
        using Fixture fixture = new();
        foreach (string path in EvmFrameMachineProfile.InputPaths)
        {
            byte[] original = fixture.Read(path);
            try
            {
                fixture.AppendByte(path, (byte)'\n');
                Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>(), path);
            }
            finally
            {
                fixture.Write(path, original);
            }
        }
    }

    [Test]
    public void Member_signature_and_production_assignment_mutations_fail_closed()
    {
        using Fixture fixture = new();
        fixture.Replace("src/Nethermind/Nethermind.Evm/VirtualMachine.cs",
            "protected TransactionSubstate PrepareTopLevelSubstate(scoped in CallResult callResult)",
            "protected TransactionSubstate PrepareTopLevelSubstate(in CallResult callResult)");
        Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());

        fixture.Reset("src/Nethermind/Nethermind.Evm/VirtualMachine.cs");
        fixture.Replace(RootMarker, "lookup[(int)Instruction.CALLDATALOAD]", "lookup[(int)Instruction.CALLDATASIZE]");
        Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Repinned_semantic_assignment_rhs_and_table_substitution_mutations_fail_closed()
    {
        IrDocument expected = ReadIr();
        string handler = File.ReadAllText(Path.Combine(RepoRoot(), RootMarker));
        const string dispatchPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs";
        string dispatch = File.ReadAllText(Path.Combine(RepoRoot(), dispatchPath));

        Assert.That(() => EvmFrameMachineProfile.ValidateProductionRoutingAgainstIr(RepoRoot(),
            handler.Replace("lookup[(int)Instruction.CALLDATALOAD]", "lookup[(int)Instruction.CALLDATASIZE]",
                StringComparison.Ordinal), dispatch, expected), Throws.TypeOf<ExtractionException>());
        Assert.That(() => EvmFrameMachineProfile.ValidateProductionRoutingAgainstIr(RepoRoot(),
            handler.Replace("OpcodeHandler<CallDataLoadOpcode<TTracingInst>, TTracingInst, TCancelable>()",
                "OpcodeHandler<CodeSizeOpcode<TTracingInst>, TTracingInst, TCancelable>()", StringComparison.Ordinal),
            dispatch, expected), Throws.TypeOf<ExtractionException>());
        Assert.That(() => EvmFrameMachineProfile.ValidateProductionRoutingAgainstIr(RepoRoot(), handler,
            dispatch.Replace("TCancelable.IsActive ? ref TracedCancelable : ref Traced",
                "TCancelable.IsActive ? ref Traced : ref TracedCancelable", StringComparison.Ordinal), expected),
            Throws.TypeOf<ExtractionException>());

        Assert.That(() => EvmFrameMachineProfile.ValidateProductionRoutingAgainstIr(RepoRoot(),
            handler.Replace("SpecFlags.Eip160(spec)", "!SpecFlags.Eip160(spec)", StringComparison.Ordinal),
            dispatch, expected), Throws.TypeOf<ExtractionException>());
        const string stopAssignment =
            "lookup[(int)Instruction.STOP] = TerminatingOpcodeHandler<StopOpcode, TTracingInst, TCancelable>();";
        string duplicatedAssignment = handler.Replace(stopAssignment,
            $"{stopAssignment}{Environment.NewLine}        {stopAssignment}", StringComparison.Ordinal);
        Assert.That(duplicatedAssignment, Is.Not.EqualTo(handler));
        Assert.That(() => EvmFrameMachineProfile.ValidateProductionRoutingAgainstIr(RepoRoot(),
            duplicatedAssignment, dispatch, expected), Throws.TypeOf<ExtractionException>());
        Assert.That(() => EvmFrameMachineProfile.ValidateProductionRoutingAgainstIr(RepoRoot(),
            handler.Replace("lookup[(int)Instruction.STOP]", "UnknownRoutingMutation(); lookup[(int)Instruction.STOP]",
                StringComparison.Ordinal), dispatch, expected), Throws.TypeOf<ExtractionException>());

        const string forkPath = "src/Nethermind/Nethermind.Specs/Forks/05_SpuriousDragon.cs";
        string fork = File.ReadAllText(Path.Combine(RepoRoot(), forkPath));
        string mutatedFork = fork.Replace("spec.IsEip160Enabled = true;", "spec.IsEip160Enabled = false;",
            StringComparison.Ordinal);
        Assert.That(mutatedFork, Is.Not.EqualTo(fork));
        Assert.That(() => EvmFrameMachineProfile.ValidateProductionRoutingAgainstIr(RepoRoot(), handler,
            dispatch, expected, forkPath, mutatedFork), Throws.TypeOf<ExtractionException>());
        string conditionalFork = fork.Replace("spec.IsEip160Enabled = true;",
            "if (false) spec.IsEip160Enabled = true;", StringComparison.Ordinal);
        Assert.That(conditionalFork, Is.Not.EqualTo(fork));
        Assert.That(() => EvmFrameMachineProfile.ValidateProductionRoutingAgainstIr(RepoRoot(), handler,
            dispatch, expected, forkPath, conditionalFork), Throws.TypeOf<ExtractionException>());
        string compoundFork = fork.Replace("spec.IsEip160Enabled = true;",
            "spec.IsEip160Enabled &= true;", StringComparison.Ordinal);
        Assert.That(compoundFork, Is.Not.EqualTo(fork));
        Assert.That(() => EvmFrameMachineProfile.ValidateProductionRoutingAgainstIr(RepoRoot(), handler,
            dispatch, expected, forkPath, compoundFork), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Proof_module_theorem_name_and_signature_mutations_fail_closed()
    {
        using Fixture fixture = new();
        const string path = "tools/Evm/Lean/Keccak256OpcodeExtractor/Refinement/Keccak256Opcode.lean";
        fixture.Replace(path, "theorem execute_refines ", "theorem execute_refines_mutated ");
        Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());

        fixture.Reset(path);
        fixture.Replace(path, "(hash : Keccak256OpcodeKernel.HashOracle)",
            "(hash : Keccak256OpcodeKernel.HashOracle) (extra : Bool)");
        Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());

        fixture.Reset(path);
        const string callDataLoadPath =
            "tools/Evm/Lean/CallDataLoadOpcodeExtractor/Refinement/CallDataLoadOpcode.lean";
        fixture.Replace(callDataLoadPath, "theorem closed_amsterdam_refines ",
            "theorem closed_amsterdam_refines_mutated ");
        Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());

        fixture.Reset(callDataLoadPath);
        fixture.Replace(callDataLoadPath,
            "theorem closed_amsterdam_refines (table : R.DispatchTable) (state : R.MachineState) :",
            "theorem closed_amsterdam_refines (table : R.DispatchTable) (state : R.MachineState) (extra : Bool) :");
        Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());

        fixture.Reset(callDataLoadPath);
        const string environmentPath = "tools/Evm/Lean/Eip803x/Refinement/EnvironmentOpcode.lean";
        fixture.Replace(environmentPath, "theorem closed_amsterdam_refines",
            "theorem closed_amsterdam_refines_mutated");
        Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());

        fixture.Reset(environmentPath);
        fixture.Replace(environmentPath,
            "(table : G.DispatchTable) (opcode : G.Opcode) (state : G.State) :",
            "(table : G.DispatchTable) (opcode : G.Opcode) (state : G.State) (extra : Bool) :");
        Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());

        fixture.Reset(environmentPath);
        const string callCreatePath =
            "tools/Evm/Lean/CallCreateOpcodeExtractor/Refinement/CallCreateOpcode.lean";
        fixture.Replace(callCreatePath, "theorem closed_amsterdam_refines",
            "theorem closed_amsterdam_refines_mutated");
        Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());

        fixture.Reset(callCreatePath);
        fixture.Replace(callCreatePath,
            "(state : MachineState) (child : ChildOutcome) :",
            "(state : MachineState) (child : ChildOutcome) (extra : Bool) :");
        Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Opcode_routing_root_dependency_mutation_fails_closed()
    {
        using Fixture fixture = new();
        const string path = "tools/Evm/Lean/ControlFlowOpcodeExtractor/Generated/ControlFlowOpcodeKernel.ir.json";
        fixture.Replace(path, "StopOpcode,OffFlag,OffFlag,OffFlag", "StopOpcode,OffFlag,OffFlag,OnFlag");
        Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());
    }

    [TestCase("ir")]
    [TestCase("lean")]
    public void Manifest_rejects_an_alternate_valid_artifact_digest(string artifact)
    {
        JsonObject manifest = JsonNode.Parse(ReadChecked(EvmFrameMachineProfile.ManifestFileName))!.AsObject();
        manifest[artifact]!["sha256"] = new string('a', 64);
        Assert.That(() => EvmFrameMachineProfile.DeserializeManifest(ToBytes(manifest), RepoRoot()),
            Throws.TypeOf<ExtractionException>());
    }

    [TestCase("ir", "A")]
    [TestCase("ir", "\n")]
    [TestCase("source", "A")]
    [TestCase("source", "\n")]
    public void Emitter_rejects_noncanonical_digests(string target, string injection)
    {
        IrDocument document = ReadIr();
        string valid = new('a', 64);
        string invalid = injection == "A" ? valid.ToUpperInvariant() : valid + injection;
        Assert.That(() => EvmFrameMachineLeanEmitter.Emit(document,
            target == "ir" ? invalid : valid, target == "source" ? invalid : valid),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Whole_frame_profile_remains_fail_closed_and_emits_nothing()
    {
        using TemporaryDirectory output = new("frame-stage-b-closed");
        IrDocument design = EvmFrameMachineProfile.DesignIr();
        Assert.That(() => EvmFrameMachineProfile.ValidateReadyForExtraction(design),
            Throws.TypeOf<IncompleteProfileException>());
        Assert.That(Directory.Exists(output.Path), Is.False);
    }

    [Test]
    public void Json_schemas_are_strict_stage_a_shapes()
    {
        string schemaDirectory = Path.Combine(RepoRoot(), "tools/Evm/Lean/EvmFrameMachineExtractor/Schema");
        using JsonDocument ir = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(schemaDirectory,
            "evm-frame-machine-ir.schema.json")));
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(schemaDirectory,
            "evm-frame-machine-source-manifest.schema.json")));
        JsonElement irProperties = ir.RootElement.GetProperty("properties");
        JsonElement manifestProperties = manifest.RootElement.GetProperty("properties");
        JsonElement environmentIrBinding = irProperties.GetProperty("opcodePackages").GetProperty("allOf")[0]
            .GetProperty("contains").GetProperty("properties");
        JsonElement callCreateIrBinding = irProperties.GetProperty("opcodePackages").GetProperty("allOf")[1]
            .GetProperty("contains").GetProperty("properties");
        JsonElement environmentManifestBinding = manifestProperties.GetProperty("dependencies")
            .GetProperty("prefixItems")[2].GetProperty("allOf")[1].GetProperty("properties");
        JsonElement callCreateManifestBinding = manifestProperties.GetProperty("dependencies")
            .GetProperty("prefixItems")[10].GetProperty("allOf")[1].GetProperty("properties");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(irProperties.GetProperty("acceptanceState").GetProperty("enum")[1].GetString(),
                Is.EqualTo("stage-a-admitted"));
            Assert.That(irProperties.GetProperty("sources").GetProperty("minItems").GetInt32(), Is.EqualTo(114));
            Assert.That(irProperties.GetProperty("admissions").GetProperty("minItems").GetInt32(), Is.EqualTo(181));
            Assert.That(irProperties.GetProperty("opcodeRoutes").GetProperty("minItems").GetInt32(), Is.EqualTo(1024));
            Assert.That(irProperties.GetProperty("opcodePackages").GetProperty("minItems").GetInt32(), Is.EqualTo(14));
            Assert.That(manifestProperties.GetProperty("dependencies").GetProperty("minItems").GetInt32(), Is.EqualTo(32));
            Assert.That(manifest.RootElement.GetProperty("$defs").GetProperty("dependencyIdentity")
                .GetProperty("required").EnumerateArray().Select(static value => value.GetString()), Does.Contain("admitted"));
            Assert.That(manifest.RootElement.GetProperty("$defs").GetProperty("dependencyIdentity")
                .GetProperty("required").EnumerateArray().Select(static value => value.GetString()),
                Does.Contain("requiredTheorems"));
            Assert.That(environmentIrBinding.GetProperty("admitted").GetProperty("const").GetBoolean(), Is.True);
            Assert.That(environmentIrBinding.GetProperty("requiredTheorems").GetProperty("prefixItems")[0]
                .GetProperty("properties").GetProperty("fullyQualifiedName").GetProperty("const").GetString(),
                Is.EqualTo("Eip803x.Refinement.EnvironmentOpcode.closed_amsterdam_refines"));
            Assert.That(environmentManifestBinding.GetProperty("admitted").GetProperty("const").GetBoolean(), Is.True);
            Assert.That(environmentManifestBinding.GetProperty("proofModuleSha256").GetProperty("const").GetString(),
                Is.EqualTo("d9461ae1535c877e1625295421aafe2f8bc4bf1b37d0310cd1dfaeac2801a357"));
            Assert.That(callCreateIrBinding.GetProperty("admitted").GetProperty("const").GetBoolean(), Is.True);
            Assert.That(callCreateIrBinding.GetProperty("requiredTheorems").GetProperty("prefixItems")[0]
                .GetProperty("properties").GetProperty("fullyQualifiedName").GetProperty("const").GetString(),
                Is.EqualTo("Eip803x.Generated.CallCreateOpcodeRefinement.closed_amsterdam_refines"));
            Assert.That(callCreateManifestBinding.GetProperty("sha256").GetProperty("const").GetString(),
                Is.EqualTo("a8ad6c834d521ba0b55e3ef28ce994663c0099ba78046d5970830cd11a526164"));
            Assert.That(callCreateManifestBinding.GetProperty("proofModuleSha256").GetProperty("const").GetString(),
                Is.EqualTo("9f14f31975a4b50cddf577d308b841cc4a8e7a21d2f12ec00173c45b814f1ad0"));
        }
    }

    private static IrDocument ReadIr() => EvmFrameMachineProfile.DeserializeIr(
        ReadChecked(EvmFrameMachineProfile.IrFileName), RepoRoot());

    private static SourceManifest ReadManifest() => EvmFrameMachineProfile.DeserializeManifest(
        ReadChecked(EvmFrameMachineProfile.ManifestFileName), RepoRoot());

    private static void AssertEveryPropertyMutationRejected<T>(byte[] bytes, Func<byte[], T> deserialize)
    {
        JsonNode root = JsonNode.Parse(bytes)!;
        foreach (PropertyPath property in EnumerateProperties(root).DistinctBy(static value => value.Shape))
        {
            foreach (PropertyMutation mutation in Enum.GetValues<PropertyMutation>())
            {
                JsonNode changed = root.DeepClone();
                Mutate(LocateOwner(changed, property), property.Name, mutation);
                Assert.That(() => deserialize(ToBytes(changed)), Throws.TypeOf<ExtractionException>(),
                    $"{property.Display} {mutation}");
            }
            Assert.That(() => deserialize(DuplicateProperty(root, property)),
                Throws.TypeOf<ExtractionException>(), $"{property.Display} duplicate");
            if (LocateOwner(root, property)[property.Name] is JsonValue)
            {
                JsonNode changed = root.DeepClone();
                JsonObject owner = LocateOwner(changed, property);
                owner[property.Name] = ChangedValue(owner[property.Name]!);
                Assert.That(() => deserialize(ToBytes(changed)), Throws.TypeOf<ExtractionException>(),
                    $"{property.Display} leaf");
            }
        }
    }

    private static IEnumerable<PropertyPath> EnumerateProperties(JsonNode node) => EnumerateProperties(node, []);

    private static IEnumerable<PropertyPath> EnumerateProperties(JsonNode node, IReadOnlyList<PathPart> parent)
    {
        if (node is JsonObject obj)
        {
            foreach ((string name, JsonNode? value) in obj)
            {
                yield return new([.. parent], name);
                if (value is not null)
                    foreach (PropertyPath nested in EnumerateProperties(value, [.. parent, new(name, null)]))
                        yield return nested;
            }
        }
        else if (node is JsonArray array)
        {
            for (int index = 0; index < array.Count; index++)
                if (array[index] is JsonNode item)
                    foreach (PropertyPath nested in EnumerateProperties(item, [.. parent, new(null, index)]))
                        yield return nested;
        }
    }

    private static IEnumerable<NodePath> EnumerateScalarArrayElements(JsonNode node) =>
        EnumerateScalarArrayElements(node, []);

    private static IEnumerable<NodePath> EnumerateScalarArrayElements(JsonNode node, IReadOnlyList<PathPart> parent)
    {
        if (node is JsonObject obj)
        {
            foreach ((string name, JsonNode? value) in obj)
                if (value is not null)
                    foreach (NodePath nested in EnumerateScalarArrayElements(value, [.. parent, new(name, null)]))
                        yield return nested;
        }
        else if (node is JsonArray array)
        {
            for (int index = 0; index < array.Count; index++)
            {
                if (array[index] is JsonValue) yield return new([.. parent], index);
                else if (array[index] is JsonNode item)
                    foreach (NodePath nested in EnumerateScalarArrayElements(item, [.. parent, new(null, index)]))
                        yield return nested;
            }
        }
    }

    private static JsonObject LocateOwner(JsonNode root, PropertyPath property)
    {
        JsonNode current = Follow(root, property.Parent);
        return current.AsObject();
    }

    private static JsonArray LocateArray(JsonNode root, IReadOnlyList<PathPart> path) => Follow(root, path).AsArray();

    private static JsonNode Follow(JsonNode root, IReadOnlyList<PathPart> path)
    {
        JsonNode current = root;
        foreach (PathPart part in path)
            current = part.Name is not null ? current[part.Name]! : current[part.Index!.Value]!;
        return current;
    }

    private static void Mutate(JsonObject owner, string name, PropertyMutation mutation)
    {
        switch (mutation)
        {
            case PropertyMutation.Omit:
                owner.Remove(name);
                break;
            case PropertyMutation.Null:
                owner[name] = null;
                break;
            case PropertyMutation.CaseAlias:
                JsonNode? original = owner[name];
                JsonNode? value = original?.DeepClone();
                owner.Remove(name);
                owner[char.ToUpperInvariant(name[0]) + name[1..]] = value;
                break;
        }
    }

    private static JsonNode ChangedValue(JsonNode node)
    {
        JsonValue value = node.AsValue();
        if (value.TryGetValue(out string? text)) return JsonValue.Create(text + "x")!;
        if (value.TryGetValue(out bool boolean)) return JsonValue.Create(!boolean)!;
        if (value.TryGetValue(out int integer)) return JsonValue.Create(integer + 1)!;
        if (value.TryGetValue(out long longInteger)) return JsonValue.Create(longInteger + 1)!;
        throw new AssertionException($"Unsupported JSON primitive {node}.");
    }

    private static byte[] DuplicateProperty(JsonNode root, PropertyPath target)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream)) WriteNode(writer, root, [], target);
        return stream.ToArray();
    }

    private static void WriteNode(Utf8JsonWriter writer, JsonNode? node, IReadOnlyList<PathPart> path,
        PropertyPath target)
    {
        switch (node)
        {
            case JsonObject obj:
                writer.WriteStartObject();
                foreach ((string name, JsonNode? value) in obj)
                {
                    writer.WritePropertyName(name);
                    WriteNode(writer, value, [.. path, new(name, null)], target);
                    if (target.Name == name && SamePath(path, target.Parent))
                    {
                        writer.WritePropertyName(name);
                        WriteNode(writer, value, [.. path, new(name, null)], target);
                    }
                }
                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                for (int index = 0; index < array.Count; index++)
                    WriteNode(writer, array[index], [.. path, new(null, index)], target);
                writer.WriteEndArray();
                break;
            case null:
                writer.WriteNullValue();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }

    private static bool SamePath(IReadOnlyList<PathPart> left, IReadOnlyList<PathPart> right) =>
        left.Count == right.Count && left.Zip(right).All(static pair => pair.First == pair.Second);

    private static byte[] ToBytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

    private static byte[] ReadChecked(string name) => File.ReadAllBytes(Path.Combine(GeneratedDirectory(), name));

    private static string GeneratedDirectory() => Path.Combine(RepoRoot(), "tools/Evm/Lean/EvmFrameMachineExtractor/Generated");

    private static ExtractionResult Extract(string root, string output) => EvmFrameMachineProfile.Extract(
        root, output, Path.Combine(output, EvmFrameMachineProfile.LeanFileName));

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, RootMarker))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Nethermind repository root.");
    }

    private enum PropertyMutation { Omit, Null, CaseAlias }

    private sealed record PathPart(string? Name, int? Index);

    private sealed record PropertyPath(IReadOnlyList<PathPart> Parent, string Name)
    {
        public string Display => string.Concat(Parent.Select(static part => part.Name ?? $"[{part.Index}]")) + "." + Name;
        public string Shape => string.Concat(Parent.Select(static part => part.Name ?? "[]")) + "." + Name;
    }

    private sealed record NodePath(IReadOnlyList<PathPart> Parent, int Index)
    {
        public string Display => string.Concat(Parent.Select(static part => part.Name ?? $"[{part.Index}]")) + $"[{Index}]";
        public string Shape => string.Concat(Parent.Select(static part => part.Name ?? "[]")) + "[]";
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _repoRoot = RepoRoot();

        internal Fixture()
        {
            Root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "frame-stage-a-fixtures", Guid.NewGuid().ToString("N"));
            Output = Path.Combine(Root, "generated");
            foreach (string relativePath in EvmFrameMachineProfile.InputPaths.Distinct(StringComparer.Ordinal))
            {
                string target = FullPath(relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(_repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)), target);
            }
        }

        internal string Root { get; }
        internal string Output { get; }

        internal void Extract() => EvmFrameMachineProfile.Extract(Root, Output,
            Path.Combine(Output, EvmFrameMachineProfile.LeanFileName));

        internal byte[] Read(string relativePath) => File.ReadAllBytes(FullPath(relativePath));

        internal void Write(string relativePath, byte[] bytes) => File.WriteAllBytes(FullPath(relativePath), bytes);

        internal void AppendByte(string relativePath, byte value)
        {
            using FileStream stream = File.Open(FullPath(relativePath), FileMode.Append, FileAccess.Write, FileShare.None);
            stream.WriteByte(value);
        }

        internal void Replace(string relativePath, string before, string after)
        {
            string path = FullPath(relativePath);
            string source = File.ReadAllText(path);
            Assert.That(source, Does.Contain(before));
            File.WriteAllText(path, source.Replace(before, after, StringComparison.Ordinal), new UTF8Encoding(false));
        }

        internal void Reset(string relativePath) => File.Copy(
            Path.Combine(_repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)),
            FullPath(relativePath), overwrite: true);

        private string FullPath(string relativePath) => Path.Combine(Root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class TemporaryDirectory(string prefix) : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(
            TestContext.CurrentContext.WorkDirectory, prefix, Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
