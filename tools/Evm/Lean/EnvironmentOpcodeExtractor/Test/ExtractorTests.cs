// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.EnvironmentOpcodeExtractor.Test;

[TestFixture]
public class ExtractorTests
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
            Assert.That(firstResult.OpcodeCount, Is.EqualTo(20));
            Assert.That(firstResult.SpecializationCount, Is.EqualTo(80));
            Assert.That(firstResult.SourceCount, Is.EqualTo(37));
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
        string checkedDirectory = System.IO.Path.Combine(root, "tools/Evm/Lean/EnvironmentOpcodeExtractor/Generated");
        string checkedLean = System.IO.Path.Combine(root, "tools/Evm/Lean/Eip803x/Generated/EnvironmentOpcodeKernel.lean");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(result.IrPath), Is.EqualTo(File.ReadAllBytes(System.IO.Path.Combine(checkedDirectory, "EnvironmentOpcodeKernel.ir.json"))));
            Assert.That(File.ReadAllBytes(result.ManifestPath), Is.EqualTo(File.ReadAllBytes(System.IO.Path.Combine(checkedDirectory, "EnvironmentOpcodeKernel.source-manifest.json"))));
            Assert.That(File.ReadAllBytes(result.LeanPath), Is.EqualTo(File.ReadAllBytes(checkedLean)));
        }
    }

    [Test]
    public void Ir_is_complete_unique_and_names_open_obligations()
    {
        string root = FindRepoRoot();
        using JsonDocument ir = JsonDocument.Parse(File.ReadAllBytes(System.IO.Path.Combine(
            root, "tools/Evm/Lean/EnvironmentOpcodeExtractor/Generated/EnvironmentOpcodeKernel.ir.json")));
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(System.IO.Path.Combine(
            root, "tools/Evm/Lean/EnvironmentOpcodeExtractor/Generated/EnvironmentOpcodeKernel.source-manifest.json")));
        JsonElement opcodes = ir.RootElement.GetProperty("opcodes");
        JsonElement specializations = ir.RootElement.GetProperty("specializations");
        HashSet<int> bytes = [];
        foreach (JsonElement opcode in opcodes.EnumerateArray())
            Assert.That(bytes.Add(opcode.GetProperty("opcodeByte").GetInt32()), Is.True, opcode.GetProperty("name").GetString());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(opcodes.GetArrayLength(), Is.EqualTo(20));
            Assert.That(specializations.GetArrayLength(), Is.EqualTo(80));
            Assert.That(bytes, Has.Count.EqualTo(20));
            Assert.That(ir.RootElement.GetProperty("externalObligations").GetArrayLength(), Is.EqualTo(5));
            Assert.That(ir.RootElement.GetProperty("reachability").GetProperty("fork").GetString(), Is.EqualTo("Amsterdam"));
            Assert.That(manifest.RootElement.GetProperty("admittedNodes").GetArrayLength(), Is.EqualTo(100));
        }
    }

    [Test]
    public void Admission_identities_are_owner_qualified_unique_and_duplicate_rejected()
    {
        string path = System.IO.Path.Combine(FindRepoRoot(),
            "tools/Evm/Lean/EnvironmentOpcodeExtractor/Generated/EnvironmentOpcodeKernel.source-manifest.json");
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(path));
        string[] identities = manifest.RootElement.GetProperty("admittedNodes").EnumerateArray()
            .Select(static node => node.GetProperty("identity").GetString()!).ToArray();

        ExtractionException duplicate = Assert.Throws<ExtractionException>(() =>
            Extractor.ValidateAdmissionKeys([identities[0], identities[0]]))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(identities, Has.Length.EqualTo(100));
            Assert.That(identities.Distinct(StringComparer.Ordinal).ToArray(), Has.Length.EqualTo(identities.Length));
            Assert.That(identities, Has.Some.Contains(":compilation-unit/0:type:"));
            Assert.That(identities, Has.Some.Contains(":EvmInstructions/0:method:"));
            Assert.That(duplicate.Message, Does.Contain("Duplicate or empty owner-qualified admission identity"));
        }
    }

    [Test]
    public void Serialized_ir_rejects_unknown_malformed_and_null_json()
    {
        string path = System.IO.Path.Combine(FindRepoRoot(),
            "tools/Evm/Lean/EnvironmentOpcodeExtractor/Generated/EnvironmentOpcodeKernel.ir.json");
        string json = File.ReadAllText(path);
        string unknown = json.Insert(json.IndexOf('{') + 1, "\n  \"unexpected\": true,");

        ExtractionException unmapped = Assert.Throws<ExtractionException>(() =>
            Extractor.DeserializeIr(Encoding.UTF8.GetBytes(unknown)))!;
        ExtractionException malformed = Assert.Throws<ExtractionException>(() =>
            Extractor.DeserializeIr(Encoding.UTF8.GetBytes("{")))!;
        ExtractionException nullDocument = Assert.Throws<ExtractionException>(() =>
            Extractor.DeserializeIr("null"u8.ToArray()))!;
        ExtractionException nullCollection = Assert.Throws<ExtractionException>(() =>
            Extractor.ValidateIr(Extractor.DeserializeIr(File.ReadAllBytes(path)) with { Opcodes = null! }))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unmapped.Message, Does.Contain("not valid JSON"));
            Assert.That(malformed.Message, Does.Contain("not valid JSON"));
            Assert.That(nullDocument.Message, Does.Contain("empty"));
            Assert.That(nullCollection.Message, Does.Contain("null value"));
        }
    }

    [TestCase("topCase")]
    [TestCase("nestedCase")]
    [TestCase("topOmitted")]
    [TestCase("nestedZeroOmitted")]
    [TestCase("nestedFalseOmitted")]
    public void Serialized_ir_rejects_case_aliases_and_omitted_constructor_fields(string mutation)
    {
        string path = System.IO.Path.Combine(FindRepoRoot(),
            "tools/Evm/Lean/EnvironmentOpcodeExtractor/Generated/EnvironmentOpcodeKernel.ir.json");
        JsonObject document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        switch (mutation)
        {
            case "topCase":
                Rename(document, "schemaVersion", "SchemaVersion");
                break;
            case "nestedCase":
                Rename(document["opcodes"]!.AsArray()[0]!.AsObject(), "fixedGas", "FixedGas");
                break;
            case "topOmitted":
                document.Remove("schemaVersion");
                break;
            case "nestedZeroOmitted":
                document["opcodes"]!.AsArray()[0]!.AsObject().Remove("dispatchStackInputs");
                break;
            case "nestedFalseOmitted":
                document["opcodes"]!.AsArray()[0]!.AsObject().Remove("checksAvailabilityBeforeGas");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }

        Assert.That(() => Extractor.DeserializeIr(Encoding.UTF8.GetBytes(document.ToJsonString())),
            Throws.TypeOf<ExtractionException>());
    }

    [TestCase("reachability")]
    [TestCase("forkFlags")]
    [TestCase("opcodes")]
    [TestCase("opcodeEntry")]
    [TestCase("opcodeField")]
    [TestCase("specializations")]
    [TestCase("specializationEntry")]
    [TestCase("specializationField")]
    [TestCase("obligations")]
    [TestCase("obligationEntry")]
    public void Serialized_ir_rejects_nested_nulls(string mutation)
    {
        string path = System.IO.Path.Combine(FindRepoRoot(),
            "tools/Evm/Lean/EnvironmentOpcodeExtractor/Generated/EnvironmentOpcodeKernel.ir.json");
        JsonObject document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        switch (mutation)
        {
            case "reachability":
                document["reachability"] = null;
                break;
            case "forkFlags":
                document["reachability"]!["enabledForkFlags"] = null;
                break;
            case "opcodes":
                document["opcodes"] = null;
                break;
            case "opcodeEntry":
                document["opcodes"]!.AsArray()[0] = null;
                break;
            case "opcodeField":
                document["opcodes"]!.AsArray()[0]!["handlerRoute"] = null;
                break;
            case "specializations":
                document["specializations"] = null;
                break;
            case "specializationEntry":
                document["specializations"]!.AsArray()[0] = null;
                break;
            case "specializationField":
                document["specializations"]!.AsArray()[0]!["closedRoot"] = null;
                break;
            case "obligations":
                document["externalObligations"] = null;
                break;
            case "obligationEntry":
                document["externalObligations"]!.AsArray()[0] = null;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }

        ExtractionException exception = Assert.Throws<ExtractionException>(() =>
            Extractor.DeserializeIr(Encoding.UTF8.GetBytes(document.ToJsonString())))!;
        Assert.That(exception.Message, Does.Contain("null value"));
    }

    [TestCase("header")]
    [TestCase("reachability")]
    [TestCase("descriptor")]
    [TestCase("obligation")]
    public void Arbitrary_serialized_ir_fields_are_rejected(string mutation)
    {
        string path = System.IO.Path.Combine(FindRepoRoot(),
            "tools/Evm/Lean/EnvironmentOpcodeExtractor/Generated/EnvironmentOpcodeKernel.ir.json");
        IrDocument document = Extractor.DeserializeIr(File.ReadAllBytes(path));
        IrDocument mutated = mutation switch
        {
            "header" => document with { Kernel = "arbitrary" },
            "reachability" => document with { Reachability = document.Reachability with { Fork = "Frontier" } },
            "descriptor" => document with
            {
                Opcodes = [document.Opcodes[0] with { FixedGas = 3 }, .. document.Opcodes.Skip(1)],
            },
            "obligation" => document with { ExternalObligations = ["arbitrary"] },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        Assert.That(() => Extractor.ValidateIr(mutated), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Generated_Lean_is_theorem_free_and_reference_independent()
    {
        string root = FindRepoRoot();
        string generated = File.ReadAllText(System.IO.Path.Combine(root, "tools/Evm/Lean/Eip803x/Generated/EnvironmentOpcodeKernel.lean"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(generated, Does.Not.Contain("theorem "));
            Assert.That(generated, Does.Not.Contain("axiom "));
            Assert.That(generated, Does.Not.Contain("Eip803x.Evm.Environment"));
            Assert.That(generated, Does.Contain("def execute"));
            Assert.That(generated, Does.Contain("def executeAmsterdam (table : DispatchTable)"));
            Assert.That(generated, Does.Contain("def handlerRoute"));
            Assert.That(generated, Does.Contain("def specializations"));
        }
    }

    [Test]
    public void Package_wide_operational_refinement_is_concrete_and_reference_independent()
    {
        string root = FindRepoRoot();
        string reference = File.ReadAllText(System.IO.Path.Combine(
            root, "tools/Evm/Lean/Eip803x/Evm/EnvironmentStack.lean"));
        string refinement = File.ReadAllText(System.IO.Path.Combine(
            root, "tools/Evm/Lean/Eip803x/Refinement/EnvironmentOpcode.lean"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reference, Does.Contain("def executeAmsterdam (table : DispatchTable)"));
            Assert.That(reference, Does.Not.Contain("Generated.EnvironmentOpcodeKernel"));
            Assert.That(refinement, Does.Contain("theorem closed_amsterdam_refines"));
            Assert.That(refinement, Does.Contain("(table : G.DispatchTable) (opcode : G.Opcode) (state : G.State)"));
            Assert.That(refinement, Does.Contain("toReferenceMachineOutcome (G.executeAmsterdam table context opcode state) ="));
            Assert.That(refinement, Does.Contain("RS.executeAmsterdam (toReferenceDispatchTable table)"));
            Assert.That(refinement, Does.Not.Contain("(relation :"));
        }
    }

    [Test]
    public void Every_specialization_has_the_exact_fully_closed_root()
    {
        string generated = System.IO.Path.Combine(FindRepoRoot(), "tools/Evm/Lean/EnvironmentOpcodeExtractor/Generated");
        IrDocument document = Extractor.DeserializeIr(File.ReadAllBytes(System.IO.Path.Combine(
            generated, "EnvironmentOpcodeKernel.ir.json")));
        Dictionary<string, OpcodeDescriptor> opcodes = document.Opcodes.ToDictionary(static opcode => opcode.Name, StringComparer.Ordinal);
        Dictionary<string, (string Tracing, string Cancelable)> tables = new(StringComparer.Ordinal)
        {
            ["NoTrace"] = ("OffFlag", "OffFlag"),
            ["NoTraceCancelable"] = ("OffFlag", "OnFlag"),
            ["Traced"] = ("OnFlag", "OffFlag"),
            ["TracedCancelable"] = ("OnFlag", "OnFlag"),
        };
        HashSet<(string Opcode, string Table)> keys = [];
        List<string> mismatches = [];
        foreach (OpcodeSpecialization specialization in document.Specializations)
        {
            OpcodeDescriptor opcode = opcodes[specialization.Opcode];
            (string tracing, string cancelable) = tables[specialization.DispatchTable];
            string closedHandler = opcode.HandlerRoute
                .Replace("TTracingInst", tracing, StringComparison.Ordinal)
                .Replace("TGasPolicy", "EthereumGasPolicy", StringComparison.Ordinal);
            string expected =
                $"VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<{closedHandler},{tracing},{cancelable},OnFlag>";
            if (!keys.Add((specialization.Opcode, specialization.DispatchTable)) ||
                specialization.TracingFlag != tracing || specialization.CancelableFlag != cancelable ||
                specialization.ClosedRoot != expected ||
                specialization.ClosedRoot.Contains("TTracingInst", StringComparison.Ordinal) ||
                specialization.ClosedRoot.Contains("TGasPolicy", StringComparison.Ordinal))
                mismatches.Add($"{specialization.Opcode}/{specialization.DispatchTable}");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mismatches, Is.Empty);
            Assert.That(keys, Has.Count.EqualTo(80));
            Assert.That(document.Specializations.Single(static item =>
                item.Opcode == "address" && item.DispatchTable == "NoTrace").ClosedRoot, Is.EqualTo(
                "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<EnvAddressOpcode<EvmInstructions.OpAddress<EthereumGasPolicy>,OffFlag>,OffFlag,OffFlag,OnFlag>"));
            Assert.That(document.Specializations.Single(static item =>
                item.Opcode == "slotnum" && item.DispatchTable == "TracedCancelable").ClosedRoot, Is.EqualTo(
                "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SlotNumOpcode<OnFlag>,OnFlag,OnFlag,OnFlag>"));
        }
    }

    [TestCase("arbitrary", "does not match its exact closed production root")]
    [TestCase("duplicate", "Duplicate specialization")]
    [TestCase("unbound", "is not closed")]
    public void Mutated_duplicate_or_unbound_specialization_is_rejected(string mutation, string expectedMessage)
    {
        string path = System.IO.Path.Combine(
            FindRepoRoot(),
            "tools/Evm/Lean/EnvironmentOpcodeExtractor/Generated/EnvironmentOpcodeKernel.ir.json");
        IrDocument document = Extractor.DeserializeIr(File.ReadAllBytes(path));
        List<OpcodeSpecialization> specializations = document.Specializations.ToList();
        specializations[0] = mutation switch
        {
            "arbitrary" => specializations[0] with { ClosedRoot = "ArbitraryRoot" },
            "duplicate" => specializations[1],
            "unbound" => specializations[0] with
            {
                ClosedRoot = "VirtualMachine<TGasPolicy>.ExecuteOpcode<EnvAddressOpcode<EvmInstructions.OpAddress<TGasPolicy>,TTracingInst>,OffFlag,OffFlag,OnFlag>",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        ExtractionException exception = Assert.Throws<ExtractionException>(() =>
            Extractor.ValidateSpecializations(document.Opcodes, specializations))!;
        Assert.That(exception.Message, Does.Contain(expectedMessage));
    }

    [Test]
    public void Duplicate_selected_opcode_byte_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.InstructionPath, "ORIGIN = 0x32", "ORIGIN = 0x30");
        AssertRejected(fixture, "duplicated");
    }

    [Test]
    public void Out_of_range_selected_opcode_byte_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.InstructionPath, "SLOTNUM = 0x4b", "SLOTNUM = unchecked((byte)256)");
        AssertRejected(fixture, "byte-range");
    }

    [Test]
    public void Competing_later_dispatch_assignment_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.HandlersPath,
            "lookup[(int)Instruction.ADDRESS] = OpcodeHandler<EnvAddressOpcode<EvmInstructions.OpAddress<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();",
            "lookup[(int)Instruction.ADDRESS] = OpcodeHandler<EnvAddressOpcode<EvmInstructions.OpAddress<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();\n" +
            "        lookup[(int)Instruction.ADDRESS] = OpcodeHandler<EnvAddressOpcode<EvmInstructions.OpCaller<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();");
        AssertRejected(fixture, "exactly one executable dispatch assignment");
    }

    [Test]
    public void Dead_local_function_dispatch_anchor_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.HandlersPath,
            "lookup[(int)Instruction.ADDRESS] = OpcodeHandler<EnvAddressOpcode<EvmInstructions.OpAddress<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();",
            "void ShadowAddress() => lookup[(int)Instruction.ADDRESS] = OpcodeHandler<EnvAddressOpcode<EvmInstructions.OpAddress<TGasPolicy>, TTracingInst>, TTracingInst, TCancelable>();");
        AssertRejected(fixture, "exactly one executable dispatch assignment");
    }

    [Test]
    public void Wrong_handler_generic_argument_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.HandlersPath,
            "EnvAddressOpcode<EvmInstructions.OpAddress<TGasPolicy>, TTracingInst>",
            "EnvAddressOpcode<EvmInstructions.OpCaller<TGasPolicy>, TTracingInst>");
        AssertRejected(fixture, "dispatch target or generic arguments changed");
    }

    [Test]
    public void Wrong_activation_gate_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.HandlersPath, "if (spec.BaseFeeEnabled)", "if (spec.ChainIdOpcodeEnabled)");
        AssertRejected(fixture, "activation gate changed");
    }

    [Test]
    public void Provider_target_mutation_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.EnvironmentPath, "=> vmState.Env.ExecutingAccount;", "=> vmState.Env.Caller;");
        AssertRejected(fixture, "OpAddress value provider target changed");
    }

    [Test]
    public void Unmodeled_early_return_in_dispatch_root_is_rejected_by_complete_fingerprint()
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.DispatchPath,
            "        // Only a traced run reads the opcode out of the bytecode.",
            "        if (stack.Head < 0) return EvmExceptionType.BadInstruction;\n\n        // Only a traced run reads the opcode out of the bytecode.");
        AssertRejected(fixture, "VirtualMachine.ExecuteOpcode<TOpcode,TTracingInst,TCancelable,TContinuable>/5");
    }

    [Test]
    public void Disabled_source_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.HandlersPath,
            "        lookup[(int)Instruction.ADDRESS]",
            "#if false\n        lookup[(int)Instruction.CALLER] = null;\n#endif\n        lookup[(int)Instruction.ADDRESS]");
        AssertRejected(fixture, "preprocessor, disabled, or skipped source");
    }

    [Test]
    public void Fixed_gas_constant_mutation_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.GasCostPath, "public const ulong BlockHash = 20;", "public const ulong BlockHash = 19;");
        AssertRejected(fixture, "GasCostOf.BlockHash must remain 20");
    }

    [Test]
    public void Missing_context_check_moved_after_gas_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Swap(
            Extractor.EnvironmentPath,
            "if (!context.Header.ExcessBlobGas.HasValue) goto BadInstruction;",
            "if (!TGasPolicy.UpdateGas<BaseGasCost>(ref gas)) return EvmExceptionType.OutOfGas;");
        AssertRejected(fixture, "must check context availability before charging gas");
    }

    [Test]
    public void Mainnet_vm_registration_replacement_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.MainnetDiPath,
            ".AddScoped<IVirtualMachine, EthereumVirtualMachine>()",
            ".AddScoped<EthereumVirtualMachine, EthereumVirtualMachine>()");
        AssertRejected(fixture, "IVirtualMachine/EthereumVirtualMachine registration");
    }

    [Test]
    public void Standard_vm_alternate_prepopulated_opcode_table_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.StandardVirtualMachinePath,
            "_opcodeTablesBySpec.GetValue(Spec, static _ => new OpcodeTable());",
            "new OpcodeTable { NoTrace = null };");
        AssertRejected(fixture, "fresh empty opcode table per release spec");
    }

    [Test]
    public void Standard_build_source_selection_mutation_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.BuildTargetsPath,
            "<Compile Remove=\"**/*.zkevm.cs\" />",
            "<Compile Remove=\"**/*.std.cs\" />");
        AssertRejected(fixture, "one exact standard source-selection group");
    }

    [Test]
    public void Zk_build_source_selection_mutation_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.BuildTargetsPath,
            "<None Include=\"**/*.std.cs\" />",
            "<None Include=\"**/*.zkevm.cs\" />");
        AssertRejected(fixture, "one exact zkEVM source-selection group");
    }

    [Test]
    public void Amsterdam_fork_flag_removal_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace("src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs",
            "spec.IsEip7843Enabled = true;", "spec.IsEip7843Enabled = false;");
        AssertRejected(fixture, "Amsterdam ancestry no longer enables IsEip7843Enabled");
    }

    [Test]
    public void Amsterdam_parent_chain_shortening_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace("src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs",
            "NamedReleaseSpec<Amsterdam>(BPO2.Instance)", "NamedReleaseSpec<Amsterdam>(BPO1.Instance)");
        AssertRejected(fixture, "must traverse all");
    }

    [Test]
    public void Amsterdam_child_deactivation_of_inherited_flag_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace("src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs",
            "spec.IsEip7843Enabled = true;",
            "spec.IsEip7843Enabled = true;\n        spec.IsEip4844Enabled = false;");
        AssertRejected(fixture, "Amsterdam ancestry no longer enables IsEip4844Enabled");
    }

    [Test]
    public void Fork_replay_order_mutation_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Swap(Extractor.NamedReleaseSpecPath, "ReplayAncestors(fork.Parent);", "fork.Apply(this);");
        AssertRejected(fixture, "apply ancestors from root to child");
    }

    [Test]
    public void Named_fork_singleton_type_mutation_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.NamedReleaseSpecPath, "new TSelf()", "Amsterdam.Instance");
        AssertRejected(fixture, "Instance must construct the requested fork type");
    }

    private static void AssertRejected(Fixture fixture, string expectedMessage)
    {
        ExtractionException exception = Assert.Throws<ExtractionException>(() => fixture.Extract())!;
        Assert.That(exception.Message, Does.Contain(expectedMessage));
    }

    private static void Rename(JsonObject document, string oldName, string newName)
    {
        JsonNode? value = document[oldName];
        Assert.That(value, Is.Not.Null);
        document.Remove(oldName);
        document[newName] = value;
    }

    private static ExtractionResult Extract(string root, string output) => Extractor.Extract(
        root, output, System.IO.Path.Combine(output, "EnvironmentOpcodeKernel.lean"));

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, Extractor.HandlersPath)))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Nethermind repository root.");
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "environment-opcode-fixtures", Guid.NewGuid().ToString("N"));
            Output = System.IO.Path.Combine(Root, "generated");
            string productionRoot = FindRepoRoot();
            foreach (string relativePath in Extractor.SourcePaths)
                Write(relativePath, File.ReadAllText(System.IO.Path.Combine(productionRoot, relativePath)));
        }

        public string Root { get; }
        public string Output { get; }

        public void Extract() => Extractor.Extract(Root, Output, System.IO.Path.Combine(Output, "EnvironmentOpcodeKernel.lean"));

        public void Replace(string relativePath, string original, string replacement)
        {
            string source = File.ReadAllText(System.IO.Path.Combine(Root, relativePath));
            Assert.That(source, Does.Contain(original));
            Write(relativePath, source.Replace(original, replacement, StringComparison.Ordinal));
        }

        public void Swap(string relativePath, string first, string second)
        {
            string source = File.ReadAllText(System.IO.Path.Combine(Root, relativePath));
            int firstIndex = source.IndexOf(first, StringComparison.Ordinal);
            int secondIndex = source.IndexOf(second, firstIndex + first.Length, StringComparison.Ordinal);
            Assert.That(firstIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(secondIndex, Is.GreaterThan(firstIndex));
            StringBuilder result = new(source.Length);
            result.Append(source.AsSpan(0, firstIndex));
            result.Append(second);
            result.Append(source.AsSpan(firstIndex + first.Length, secondIndex - firstIndex - first.Length));
            result.Append(first);
            result.Append(source.AsSpan(secondIndex + second.Length));
            Write(relativePath, result.ToString());
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private void Write(string relativePath, string source)
        {
            string path = System.IO.Path.Combine(Root, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "environment-opcode-output", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
