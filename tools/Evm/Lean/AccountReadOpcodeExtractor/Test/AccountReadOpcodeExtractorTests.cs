// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.AccountReadOpcodeExtractor.Test;

[TestFixture]
public class AccountReadOpcodeExtractorTests
{
    [Test]
    public void Production_sources_extract_deterministically()
    {
        string root = FindRepoRoot();
        using TemporaryDirectory first = new();
        using TemporaryDirectory second = new();
        ExtractionResult firstResult = Extract(root, first.Path);
        ExtractionResult secondResult = Extract(root, second.Path);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstResult.OpcodeCount, Is.EqualTo(4));
            Assert.That(firstResult.SpecializationCount, Is.EqualTo(16));
            Assert.That(firstResult.SourceCount, Is.EqualTo(AccountReadOpcodeProfile.SourcePaths.Length));
            Assert.That(firstResult.AdmissionCount, Is.GreaterThan(70));
            Assert.That(File.ReadAllBytes(firstResult.IrPath), Is.EqualTo(File.ReadAllBytes(secondResult.IrPath)));
            Assert.That(File.ReadAllBytes(firstResult.ManifestPath), Is.EqualTo(File.ReadAllBytes(secondResult.ManifestPath)));
            Assert.That(File.ReadAllBytes(firstResult.LeanPath), Is.EqualTo(File.ReadAllBytes(secondResult.LeanPath)));
        }
    }

    [Test]
    public void Checked_artifacts_match_fresh_extraction()
    {
        string root = FindRepoRoot();
        using TemporaryDirectory temporary = new();
        ExtractionResult result = Extract(root, temporary.Path);
        string checkedDirectory = Path.Combine(root, AccountReadOpcodeProfile.DefaultOutputRelativePath);
        string checkedLean = Path.Combine(root, AccountReadOpcodeProfile.DefaultLeanRelativePath);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(result.IrPath), Is.EqualTo(File.ReadAllBytes(Path.Combine(checkedDirectory, AccountReadOpcodeProfile.IrFileName))));
            Assert.That(File.ReadAllBytes(result.ManifestPath), Is.EqualTo(File.ReadAllBytes(Path.Combine(checkedDirectory, AccountReadOpcodeProfile.ManifestFileName))));
            Assert.That(File.ReadAllBytes(result.LeanPath), Is.EqualTo(File.ReadAllBytes(checkedLean)));
        }
    }

    [Test]
    public void Generated_Lean_is_theorem_free_and_reference_independent()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), AccountReadOpcodeProfile.DefaultLeanRelativePath));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source, Does.Not.Match(@"(?m)^\s*theorem\s"));
            Assert.That(source, Does.Not.Match(@"(?m)^\s*axiom\s"));
            Assert.That(source, Does.Not.Contain("Eip803x.Evm.AccountReadStack"));
            Assert.That(source, Does.Not.Contain("Eip803x.AccountPricing"));
            Assert.That(source, Does.Contain("def execute"));
            Assert.That(source, Does.Contain("def specializations"));
            Assert.That(source, Does.Contain("def chargesSecondRead"));
        }
    }

    [Test]
    public void Refinement_exposes_only_the_abstract_transition_boundary()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(),
            "tools/Evm/Lean/Eip803x/Refinement/AccountReadOpcode.lean"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source, Does.Contain("theorem generated_execution_refines_reference"));
            Assert.That(source, Does.Contain("`MemoryGas.prepare` foundation"));
            Assert.That(source, Does.Not.Contain("ProductionProviderObservation"));
            Assert.That(source, Does.Not.Contain("ProductionFrameObservation"));
            Assert.That(source, Does.Not.Contain("ProductionAdapterPremises"));
        }
    }

    [Test]
    public void Ir_has_exact_schedule_descriptors_and_closed_roots()
    {
        string path = Path.Combine(FindRepoRoot(), AccountReadOpcodeProfile.DefaultOutputRelativePath,
            AccountReadOpcodeProfile.IrFileName);
        IrDocument document = AccountReadOpcodeProfile.DeserializeIr(File.ReadAllBytes(path));
        HashSet<(string Opcode, string Table)> keys = [];
        foreach (OpcodeSpecialization specialization in document.Specializations)
        {
            Assert.That(keys.Add((specialization.Opcode, specialization.DispatchTable)), Is.True);
            Assert.That(specialization.ClosedRoot, Does.Not.Contain("TTracingInst"));
            Assert.That(specialization.ClosedRoot, Does.Not.Contain("TGasPolicy"));
            Assert.That(specialization.ClosedRoot, Does.Not.Contain("Eip2929"));
            Assert.That(specialization.ClosedRoot, Does.Not.Contain("Eip8038>"));
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(document.Opcodes.Select(static opcode => opcode.OpcodeByte), Is.EqualTo(new[] { 0x31, 0x3b, 0x3c, 0x3f }));
            Assert.That(document.Schedule, Is.EqualTo(new ScheduleDescriptor(0, 3000, 100, 3, 3, 512, 3)));
            Assert.That(document.Opcodes.Select(static opcode => opcode.ChargesSecondRead), Is.EqualTo(new[] { false, true, true, false }));
            Assert.That(keys, Has.Count.EqualTo(16));
            Assert.That(document.OpenExtractionObligations, Has.Length.EqualTo(6));
        }
    }

    [TestCase("opcode", "opcode descriptors")]
    [TestCase("schedule", "schedule")]
    [TestCase("reachability", "reachability")]
    [TestCase("obligation", "trust boundary")]
    [TestCase("root", "exact closed production root")]
    [TestCase("duplicate", "Duplicate specialization")]
    [TestCase("missing", "Expected 16")]
    [TestCase("unbound", "open generic placeholder")]
    public void Mutated_round_tripped_ir_is_rejected(string mutation, string expectedMessage)
    {
        string path = Path.Combine(FindRepoRoot(), AccountReadOpcodeProfile.DefaultOutputRelativePath,
            AccountReadOpcodeProfile.IrFileName);
        IrDocument original = AccountReadOpcodeProfile.DeserializeIr(File.ReadAllBytes(path));
        List<OpcodeSpecialization> roots = original.Specializations.ToList();
        IrDocument mutated = mutation switch
        {
            "opcode" => original with { Opcodes = [original.Opcodes[0] with { EffectOrder = "arbitrary" }, .. original.Opcodes[1..]] },
            "schedule" => original with { Schedule = original.Schedule with { WarmAccess = 101 } },
            "reachability" => original with { Reachability = original.Reachability with { GasPolicy = "ArbitraryPolicy" } },
            "obligation" => original with { OpenExtractionObligations = ["arbitrary"] },
            "root" => original with { Specializations = [roots[0] with { ClosedRoot = "ArbitraryRoot" }, .. roots.Skip(1)] },
            "duplicate" => original with { Specializations = [roots[1], .. roots.Skip(1)] },
            "missing" => original with { Specializations = roots.Skip(1).ToArray() },
            "unbound" => original with { Specializations = [roots[0] with { ClosedRoot = "VirtualMachine<TGasPolicy>.ExecuteOpcode<BalanceOpcode<TTracingInst,EvmInstructions.AccessSpec<Eip2929,Eip8038>>,OffFlag,OffFlag,OnFlag>" }, .. roots.Skip(1)] },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        ExtractionException exception = Assert.Throws<ExtractionException>(() => AccountReadOpcodeLeanEmitter.Validate(mutated))!;
        Assert.That(exception.Message, Does.Contain(expectedMessage));
    }

    [Test]
    public void Serialized_ir_rejects_unmapped_and_malformed_json()
    {
        string path = Path.Combine(FindRepoRoot(), AccountReadOpcodeProfile.DefaultOutputRelativePath,
            AccountReadOpcodeProfile.IrFileName);
        string json = File.ReadAllText(path);
        string withUnmappedMember = json.Insert(json.IndexOf('{') + 1, "\n  \"unexpected\": true,");

        ExtractionException unmapped = Assert.Throws<ExtractionException>(() =>
            AccountReadOpcodeProfile.DeserializeIr(Encoding.UTF8.GetBytes(withUnmappedMember)))!;
        ExtractionException malformed = Assert.Throws<ExtractionException>(() =>
            AccountReadOpcodeProfile.DeserializeIr(Encoding.UTF8.GetBytes("{")))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unmapped.Message, Does.Contain("not valid JSON"));
            Assert.That(malformed.Message, Does.Contain("not valid JSON"));
        }
    }

    [Test]
    public void Serialized_ir_rejects_null_document_and_collections()
    {
        ExtractionException nullInput = Assert.Throws<ExtractionException>(() =>
            AccountReadOpcodeProfile.DeserializeIr(null!))!;
        ExtractionException nullDocument = Assert.Throws<ExtractionException>(() =>
            AccountReadOpcodeProfile.DeserializeIr("null"u8.ToArray()))!;

        string path = Path.Combine(FindRepoRoot(), AccountReadOpcodeProfile.DefaultOutputRelativePath,
            AccountReadOpcodeProfile.IrFileName);
        IrDocument document = AccountReadOpcodeProfile.DeserializeIr(File.ReadAllBytes(path));
        ExtractionException nullOpcodes = Assert.Throws<ExtractionException>(() =>
            AccountReadOpcodeLeanEmitter.Validate(document with { Opcodes = null! }))!;
        ExtractionException nullSpecialization = Assert.Throws<ExtractionException>(() =>
            AccountReadOpcodeLeanEmitter.Validate(document with
            {
                Specializations = [null!, .. document.Specializations.Skip(1)],
            }))!;
        ExtractionException nullRoot = Assert.Throws<ExtractionException>(() =>
            AccountReadOpcodeLeanEmitter.Validate(document with
            {
                Specializations = [document.Specializations[0] with { ClosedRoot = null! },
                    .. document.Specializations.Skip(1)],
            }))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nullInput.Message, Does.Contain("input was null"));
            Assert.That(nullDocument.Message, Does.Contain("empty"));
            Assert.That(nullOpcodes.Message, Does.Contain("null value"));
            Assert.That(nullSpecialization.Message, Does.Contain("null value"));
            Assert.That(nullRoot.Message, Does.Contain("null value"));
        }
    }

    [Test]
    public void Serialized_ir_rejects_case_aliases_and_omitted_required_default_values()
    {
        byte[] bytes = ReadCheckedArtifact(AccountReadOpcodeProfile.IrFileName);

        JsonObject topCase = ParseObject(bytes);
        Rename(topCase, "schemaVersion", "SchemaVersion");
        AssertSerializedIrRejected(topCase);
        JsonObject nestedCase = ParseObject(bytes);
        Rename(nestedCase["opcodes"]!.AsArray()[0]!.AsObject(), "popsBeforeGas", "PopsBeforeGas");
        AssertSerializedIrRejected(nestedCase);

        JsonObject omittedTop = ParseObject(bytes);
        omittedTop.Remove("schemaVersion");
        AssertSerializedIrRejected(omittedTop);
        JsonObject omittedZero = ParseObject(bytes);
        omittedZero["schedule"]!.AsObject().Remove("accountReadBase");
        AssertSerializedIrRejected(omittedZero);
        JsonObject omittedFalse = ParseObject(bytes);
        omittedFalse["opcodes"]!.AsArray()[0]!.AsObject().Remove("popsBeforeGas");
        AssertSerializedIrRejected(omittedFalse);
    }

    [Test]
    public void Serialized_ir_rejects_unknown_malformed_top_and_nested_null()
    {
        byte[] bytes = ReadCheckedArtifact(AccountReadOpcodeProfile.IrFileName);
        JsonObject unknown = ParseObject(bytes);
        unknown["unknown"] = true;
        AssertSerializedIrRejected(unknown);
        Assert.That(() => AccountReadOpcodeProfile.DeserializeIr("{"u8.ToArray()), Throws.TypeOf<ExtractionException>());
        Assert.That(() => AccountReadOpcodeProfile.DeserializeIr("null"u8.ToArray()), Throws.TypeOf<ExtractionException>());

        foreach (Action<JsonObject> mutate in new Action<JsonObject>[]
        {
            root => root["reachability"] = null,
            root => root["schedule"] = null,
            root => root["opcodes"] = null,
            root => root["opcodes"]!.AsArray()[0] = null,
            root => root["opcodes"]!.AsArray()[0]!["handlerRoute"] = null,
            root => root["specializations"] = null,
            root => root["specializations"]!.AsArray()[0] = null,
            root => root["specializations"]!.AsArray()[0]!["closedRoot"] = null,
            root => root["openExtractionObligations"] = null,
            root => root["openExtractionObligations"]!.AsArray()[0] = null,
        })
        {
            JsonObject document = ParseObject(bytes);
            mutate(document);
            AssertSerializedIrRejected(document);
        }
    }

    [Test]
    public void Every_serialized_ir_field_is_validated()
    {
        byte[] bytes = ReadCheckedArtifact(AccountReadOpcodeProfile.IrFileName);
        Action<JsonObject>[] mutations =
        [
            root => root["schemaVersion"] = 2,
            root => root["extractorVersion"] = "changed",
            root => root["kernel"] = "changed",
            root => root["reachability"]!["interface"] = "changed",
            root => root["reachability"]!["implementation"] = "changed",
            root => root["reachability"]!["closedVm"] = "changed",
            root => root["reachability"]!["gasPolicy"] = "changed",
            root => root["reachability"]!["worldState"] = "changed",
            root => root["reachability"]!["codeRepository"] = "changed",
            root => root["reachability"]!["precompileProvider"] = "changed",
            root => root["reachability"]!["codeCache"] = "changed",
            root => root["reachability"]!["fork"] = "changed",
            root => root["reachability"]!["dispatchRoot"] = "changed",
            root => root["reachability"]!["buildSelection"] = "changed",
            root => root["schedule"]!["accountReadBase"] = 1,
            root => root["schedule"]!["coldAccountAccess"] = 1,
            root => root["schedule"]!["warmAccess"] = 1,
            root => root["schedule"]!["copyWord"] = 1,
            root => root["schedule"]!["memoryLinear"] = 1,
            root => root["schedule"]!["memoryQuadraticDivisor"] = 1,
            root => root["schedule"]!["veryLow"] = 1,
            root => root["opcodes"]!.AsArray()[0]!["name"] = "changed",
            root => root["opcodes"]!.AsArray()[0]!["instruction"] = "changed",
            root => root["opcodes"]!.AsArray()[0]!["opcodeByte"] = 0,
            root => root["opcodes"]!.AsArray()[0]!["handlerRoute"] = "changed",
            root => root["opcodes"]!.AsArray()[0]!["valueRule"] = "changed",
            root => root["opcodes"]!.AsArray()[0]!["stackInputs"] = 0,
            root => root["opcodes"]!.AsArray()[0]!["stackOutputs"] = 0,
            root => root["opcodes"]!.AsArray()[0]!["popsBeforeGas"] = true,
            root => root["opcodes"]!.AsArray()[0]!["chargesSecondRead"] = true,
            root => root["opcodes"]!.AsArray()[0]!["effectOrder"] = "changed",
            root => root["specializations"]!.AsArray()[0]!["opcode"] = "changed",
            root => root["specializations"]!.AsArray()[0]!["dispatchTable"] = "changed",
            root => root["specializations"]!.AsArray()[0]!["tracingFlag"] = "changed",
            root => root["specializations"]!.AsArray()[0]!["cancelableFlag"] = "changed",
            root => root["specializations"]!.AsArray()[0]!["closedRoot"] = "changed",
            root => root["openExtractionObligations"]!.AsArray()[0] = "changed",
        ];
        foreach (Action<JsonObject> mutate in mutations)
        {
            JsonObject document = ParseObject(bytes);
            mutate(document);
            AssertSerializedIrRejected(document);
        }

        JsonObject duplicate = ParseObject(bytes);
        duplicate["specializations"]!.AsArray()[1] = duplicate["specializations"]!.AsArray()[0]!.DeepClone();
        AssertSerializedIrRejected(duplicate);
    }

    [Test]
    public void Serialized_manifest_strictly_binds_sources_admissions_artifacts_and_semantics()
    {
        byte[] bytes = ReadCheckedArtifact(AccountReadOpcodeProfile.ManifestFileName);
        SourceManifest expected = AccountReadOpcodeProfile.DeserializeManifest(bytes);

        JsonObject unknown = ParseObject(bytes);
        unknown["unknown"] = true;
        AssertManifestJsonRejected(unknown);
        Assert.That(() => AccountReadOpcodeProfile.DeserializeManifest(null!), Throws.TypeOf<ExtractionException>());
        Assert.That(() => AccountReadOpcodeProfile.DeserializeManifest("{"u8.ToArray()), Throws.TypeOf<ExtractionException>());
        Assert.That(() => AccountReadOpcodeProfile.DeserializeManifest("null"u8.ToArray()), Throws.TypeOf<ExtractionException>());

        JsonObject topCase = ParseObject(bytes);
        Rename(topCase, "schemaVersion", "SchemaVersion");
        AssertManifestJsonRejected(topCase);
        JsonObject nestedCase = ParseObject(bytes);
        Rename(nestedCase["admissions"]!.AsArray()[0]!.AsObject(), "identity", "Identity");
        AssertManifestJsonRejected(nestedCase);
        JsonObject omitted = ParseObject(bytes);
        omitted.Remove("schemaVersion");
        AssertManifestJsonRejected(omitted);

        Action<JsonObject>[] mutations =
        [
            root => root["schemaVersion"] = 2,
            root => root["extractorVersion"] = "changed",
            root => root["roslynVersion"] = "changed",
            root => root["languageVersion"] = "changed",
            root => root["kernel"] = "changed",
            root => root["sources"]!.AsArray()[0]!["path"] = "changed",
            root => root["sources"]!.AsArray()[0]!["sha256"] = new string('0', 64),
            root => root["admissions"]!.AsArray()[0]!["identity"] = "changed",
            root => root["admissions"]!.AsArray()[0]!["sourcePath"] = "changed",
            root => root["admissions"]!.AsArray()[0]!["syntaxKind"] = "changed",
            root => root["admissions"]!.AsArray()[0]!["sha256"] = new string('0', 64),
            root => root["ir"]!["path"] = "changed",
            root => root["ir"]!["sha256"] = new string('0', 64),
            root => root["lean"]!["path"] = "changed",
            root => root["lean"]!["sha256"] = new string('0', 64),
            root => root["semanticBindings"]!.AsArray()[0] = "changed",
        ];
        foreach (Action<JsonObject> mutate in mutations)
        {
            JsonObject document = ParseObject(bytes);
            mutate(document);
            AssertManifestLineageRejected(document, expected);
        }

        foreach (Action<JsonObject> mutate in new Action<JsonObject>[]
        {
            root => root["sources"]!.AsArray()[0] = null,
            root => root["admissions"]!.AsArray()[0] = null,
            root => root["ir"] = null,
            root => root["semanticBindings"]!.AsArray()[0] = null,
        })
        {
            JsonObject document = ParseObject(bytes);
            mutate(document);
            AssertManifestJsonRejected(document);
        }

        JsonObject duplicateSource = ParseObject(bytes);
        duplicateSource["sources"]!.AsArray()[1] = duplicateSource["sources"]!.AsArray()[0]!.DeepClone();
        AssertManifestJsonRejected(duplicateSource);
        JsonObject duplicateAdmission = ParseObject(bytes);
        duplicateAdmission["admissions"]!.AsArray()[1] = duplicateAdmission["admissions"]!.AsArray()[0]!.DeepClone();
        AssertManifestJsonRejected(duplicateAdmission);
    }

    [TestCase(AccountReadOpcodeProfile.InstructionPath, "BALANCE = 0x31", "BALANCE = 0x3b", "exact byte")]
    [TestCase(AccountReadOpcodeProfile.InstructionPath, "BALANCE = 0x31", "BALANCE = unchecked((byte)256)", "exact byte")]
    [TestCase(AccountReadOpcodeProfile.HandlersPath, "if (SpecFlags.Eip8038(spec))", "if (false)", "EIP-8038 access dispatch")]
    [TestCase(AccountReadOpcodeProfile.HandlersPath, "BalanceOpcode<TTracingInst, EvmInstructions.AccessSpec<Eip2929, Eip8038>>", "ExtCodeHashOpcode<TTracingInst, EvmInstructions.AccessSpec<Eip2929, Eip8038>>", "dispatch target changed")]
    [TestCase(AccountReadOpcodeProfile.HandlersPath, "if (spec.ExtCodeHashOpcodeEnabled)", "if (false)", "activation gate changed")]
    [TestCase(AccountReadOpcodeProfile.EnvironmentPath, "vm.WorldState.GetBalance(address)", "vm.WorldState.GetCodeHash(address)", "BALANCE no longer contains")]
    [TestCase(AccountReadOpcodeProfile.EnvironmentPath, "state.IsDeadAccount(address)", "state.IsContract(address)", "EXTCODEHASH no longer contains")]
    [TestCase(AccountReadOpcodeProfile.EthereumGasPolicyPath, "bool isCold = accessTracker.WarmUp(address);", "bool isCold = true;", "priced warm-up")]
    [TestCase(AccountReadOpcodeProfile.CodeCopyPath, "EIP-8038 charges an extra warm access for the second DB read EXTCODESIZE performs.\n        if (Eip8038.IsActive && !TGasPolicy.UpdateGas(ref gas, Eip8038Constants.WarmAccess))", "second read removed.\n        if (false)", "EXTCODESIZE no longer contains")]
    [TestCase(AccountReadOpcodeProfile.CodeCopyPath, "EIP-8038 charges an extra warm access for the second DB read EXTCODECOPY performs.\n        if (Eip8038.IsActive && !TGasPolicy.UpdateGas(ref gas, Eip8038Constants.WarmAccess))", "second read removed.\n        if (false)", "EXTCODECOPY no longer contains")]
    [TestCase(AccountReadOpcodeProfile.CodeCopyPath, "followDelegation: false", "followDelegation: true", "no longer contains admitted ordered effect")]
    [TestCase(AccountReadOpcodeProfile.CodeCopyPath, "CopyFromZeroExtendedAfterGas(in a, externalCode, in b, (int)result)", "CopyFromZeroExtendedAfterGas(in b, externalCode, in a, (int)result)", "EXTCODECOPY no longer contains")]
    [TestCase(AccountReadOpcodeProfile.CodeCopyPath, "vm.OpCodeCount++;", "vm.OpCodeCount += 2;", "EXTCODESIZE no longer contains")]
    [TestCase(AccountReadOpcodeProfile.CodeCopyPath, "programCounter++;", "programCounter += 2;", "EXTCODESIZE no longer contains")]
    [TestCase(AccountReadOpcodeProfile.Eip8038ConstantsPath, "ColdAccountAccess = 3000", "ColdAccountAccess = 2999", "cold account gas")]
    [TestCase(AccountReadOpcodeProfile.GasCostPath, "WarmStateRead = 100", "WarmStateRead = 99", "warm access gas")]
    [TestCase(AccountReadOpcodeProfile.SpecGasCostsPath, "BalanceCost =\n            hotCold ? GasCostOf.Free", "BalanceCost =\n            hotCold ? GasCostOf.Base", "BALANCE base gas")]
    [TestCase(AccountReadOpcodeProfile.MainnetDiPath, "AddScoped<IVirtualMachine, EthereumVirtualMachine>", "AddScoped<EthereumVirtualMachine, EthereumVirtualMachine>", "mainnet IVirtualMachine registration")]
    [TestCase(AccountReadOpcodeProfile.MainnetDiPath, "AddScoped<IWorldState, WorldState>", "AddScoped<IWorldState, TracedAccessWorldState>", "mainnet IWorldState registration")]
    [TestCase(AccountReadOpcodeProfile.MainnetDiPath, "AddScoped<ICodeInfoRepository, CacheCodeInfoRepository>", "AddScoped<ICodeInfoRepository, CodeInfoRepository>", "mainnet code repository registration")]
    [TestCase(AccountReadOpcodeProfile.StandardVirtualMachinePath, "static _ => new OpcodeTable()", "static _ => new OpcodeTable { NoTrace = null }", "fresh standard opcode table factory")]
    [TestCase(AccountReadOpcodeProfile.NamedReleaseSpecPath, "new TSelf()", "Amsterdam.Instance", "singleton construction")]
    [TestCase("src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs", "NamedReleaseSpec<Amsterdam>(BPO2.Instance)", "NamedReleaseSpec<Amsterdam>(BPO1.Instance)", "exact parent")]
    [TestCase("src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs", "spec.IsEip8038Enabled = true", "spec.IsEip8038Enabled = false", "Amsterdam EIP-8038 activation")]
    [TestCase("src/Nethermind/Nethermind.Specs/Forks/11_Berlin.cs", "spec.IsEip2929Enabled = true", "spec.IsEip2929Enabled = false", "Berlin EIP-2929 activation")]
    [TestCase("src/Nethermind/Nethermind.Specs/Forks/07_Constantinople.cs", "spec.IsEip1052Enabled = true", "spec.IsEip1052Enabled = false", "Constantinople EXTCODEHASH activation")]
    [TestCase(AccountReadOpcodeProfile.BuildTargetsPath, "<Compile Remove=\"**/*.zkevm.cs\" />", "<Compile Remove=\"**/*.std.cs\" />", "Unadmitted Directory.Build.targets")]
    public void Production_semantic_mutation_is_rejected(string path, string original, string replacement, string expectedMessage)
    {
        using Fixture fixture = new();
        fixture.Replace(path, original, replacement);
        AssertRejected(fixture, expectedMessage);
    }

    [Test]
    public void Competing_later_dispatch_assignment_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(AccountReadOpcodeProfile.HandlersPath,
            "lookup[(int)Instruction.BALANCE] = OpcodeHandler<BalanceOpcode<TTracingInst, EvmInstructions.AccessSpec<Eip2929, Eip8038>>, TTracingInst, TCancelable>();",
            "lookup[(int)Instruction.BALANCE] = OpcodeHandler<BalanceOpcode<TTracingInst, EvmInstructions.AccessSpec<Eip2929, Eip8038>>, TTracingInst, TCancelable>();\n        lookup[(int)Instruction.BALANCE] = OpcodeHandler<BalanceOpcode<TTracingInst, EvmInstructions.AccessSpec<Eip2929, Eip8038>>, TTracingInst, TCancelable>();");
        AssertRejected(fixture, "standard-build dispatch assignments");
    }

    [Test]
    public void Dead_local_function_dispatch_anchor_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(AccountReadOpcodeProfile.HandlersPath,
            "lookup[(int)Instruction.BALANCE] = OpcodeHandler<BalanceOpcode<TTracingInst, EvmInstructions.AccessSpec<Eip2929, Eip8038>>, TTracingInst, TCancelable>();",
            "void ShadowBalance() => lookup[(int)Instruction.BALANCE] = OpcodeHandler<BalanceOpcode<TTracingInst, EvmInstructions.AccessSpec<Eip2929, Eip8038>>, TTracingInst, TCancelable>();");
        AssertRejected(fixture, "production assignment in the closed access configurator");
    }

    [Test]
    public void Disabled_selected_source_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(AccountReadOpcodeProfile.EnvironmentPath,
            "        Address? address = stack.PopAddress(vm.AddressCache);",
            "#if false\n        Address? ignored = null;\n#endif\n        Address? address = stack.PopAddress(vm.AddressCache);");
        AssertRejected(fixture, "contains unadmitted source");
    }

    [Test]
    public void Unmodeled_early_return_is_rejected_by_complete_admission()
    {
        using Fixture fixture = new();
        fixture.Replace(AccountReadOpcodeProfile.EnvironmentPath,
            "        IReleaseSpec spec = vm.Spec;\n        // Deduct gas cost for balance operation",
            "        IReleaseSpec spec = vm.Spec;\n        if (vm.IsTracingAccess) return EvmExceptionType.None;\n        // Deduct gas cost for balance operation");
        AssertRejected(fixture, "EvmInstructions.InstructionBalance/3");
    }

    private static void AssertRejected(Fixture fixture, string expectedMessage)
    {
        ExtractionException exception = Assert.Throws<ExtractionException>(() => fixture.Extract())!;
        Assert.That(exception.Message, Does.Contain(expectedMessage));
    }

    private static byte[] ReadCheckedArtifact(string name) => File.ReadAllBytes(Path.Combine(
        FindRepoRoot(), AccountReadOpcodeProfile.DefaultOutputRelativePath, name));

    private static JsonObject ParseObject(byte[] bytes) => JsonNode.Parse(bytes)!.AsObject();

    private static void Rename(JsonObject owner, string before, string after)
    {
        JsonNode value = owner[before]!.DeepClone();
        owner.Remove(before);
        owner[after] = value;
    }

    private static void AssertSerializedIrRejected(JsonObject document) => Assert.That(
        () => AccountReadOpcodeProfile.DeserializeIr(Encoding.UTF8.GetBytes(document.ToJsonString())),
        Throws.TypeOf<ExtractionException>());

    private static void AssertManifestJsonRejected(JsonObject document) => Assert.That(
        () => AccountReadOpcodeProfile.DeserializeManifest(Encoding.UTF8.GetBytes(document.ToJsonString())),
        Throws.TypeOf<ExtractionException>());

    private static void AssertManifestLineageRejected(JsonObject document, SourceManifest expected) => Assert.That(
        () => AccountReadOpcodeProfile.ValidateManifest(
            AccountReadOpcodeProfile.DeserializeManifest(Encoding.UTF8.GetBytes(document.ToJsonString())), expected),
        Throws.TypeOf<ExtractionException>());

    private static ExtractionResult Extract(string root, string output) => AccountReadOpcodeProfile.Extract(
        root, output, Path.Combine(output, "AccountReadOpcodeKernel.lean"));

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, AccountReadOpcodeProfile.HandlersPath))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Nethermind repository root.");
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "account-read-opcode-fixtures", Guid.NewGuid().ToString("N"));
            Output = Path.Combine(Root, "generated");
            string productionRoot = FindRepoRoot();
            foreach (string relativePath in AccountReadOpcodeProfile.SourcePaths)
                Write(relativePath, File.ReadAllText(Path.Combine(productionRoot, relativePath)));
        }

        public string Root { get; }
        public string Output { get; }

        public void Extract() => AccountReadOpcodeProfile.Extract(Root, Output, Path.Combine(Output, "AccountReadOpcodeKernel.lean"));

        public void Replace(string relativePath, string original, string replacement)
        {
            string path = Path.Combine(Root, relativePath);
            string source = File.ReadAllText(path);
            Assert.That(source, Does.Contain(original));
            Write(relativePath, source.Replace(original, replacement, StringComparison.Ordinal));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private void Write(string relativePath, string source)
        {
            string path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "account-read-opcode-output", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
