// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.CallCreateOpcodeExtractor.Test;

[TestFixture]
public sealed class CallCreateOpcodeExtractorTests
{
    private const string HandlerPath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.cs";
    private const string SpecPath = "tools/Evm/Lean/CallCreateOpcodeExtractor/Specification/CallCreateExecution.lean";
    private const string ReferenceSpecPath = "tools/Evm/Lean/CallCreateOpcodeExtractor/Specification/CallCreateOperational.lean";

    [Test]
    public void Production_sources_extract_to_byte_identical_artifacts()
    {
        using TemporaryDirectory first = new("call-create-extractor-first");
        using TemporaryDirectory second = new("call-create-extractor-second");
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
        using TemporaryDirectory fresh = new("call-create-extractor-fresh");
        ExtractionResult result = Extract(RepoRoot(), fresh.Path);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(result.IrPath), Is.EqualTo(ReadChecked(CallCreateOpcodeProfile.IrFileName)));
            Assert.That(File.ReadAllBytes(result.ManifestPath), Is.EqualTo(ReadChecked(CallCreateOpcodeProfile.ManifestFileName)));
            Assert.That(File.ReadAllBytes(result.LeanPath), Is.EqualTo(ReadChecked(CallCreateOpcodeProfile.LeanFileName)));
        }
    }

    [Test]
    public void Ir_closes_seven_opcodes_over_four_dispatch_tables()
    {
        IrDocument document = ReadIr();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(document.Opcodes, Has.Length.EqualTo(7));
            Assert.That(document.Specializations, Has.Length.EqualTo(28));
            Assert.That(document.Opcodes.Select(static opcode => opcode.OpcodeByte),
                Is.EquivalentTo(new[] { 0xf0, 0xf1, 0xf2, 0xf4, 0xf5, 0xfa, 0xff }));
            Assert.That(document.Specializations.GroupBy(static root => root.Opcode)
                .All(static roots => roots.Count() == 4), Is.True);
            Assert.That(document.Specializations.Select(static root => root.ClosedRoot)
                .Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(28));
            Assert.That(document.Specializations.All(static root =>
                !root.ClosedRoot.Contains("TTracingInst", StringComparison.Ordinal) &&
                !root.ClosedRoot.Contains("TCancelable", StringComparison.Ordinal) &&
                !root.ClosedRoot.Contains("TGasPolicy", StringComparison.Ordinal)), Is.True);
        }
    }

    [Test]
    public void Ir_pins_failure_order_and_create_trace_ownership()
    {
        IrDocument document = ReadIr();
        OpcodeDescriptor call = document.Opcodes.Single(static opcode => opcode.Name == "call");
        OpcodeDescriptor callCode = document.Opcodes.Single(static opcode => opcode.Name == "callcode");
        OpcodeDescriptor delegateCall = document.Opcodes.Single(static opcode => opcode.Name == "delegatecall");
        OpcodeDescriptor create = document.Opcodes.Single(static opcode => opcode.Name == "create");
        OpcodeDescriptor create2 = document.Opcodes.Single(static opcode => opcode.Name == "create2");
        OpcodeDescriptor staticCall = document.Opcodes.Single(static opcode => opcode.Name == "staticcall");
        OpcodeDescriptor selfDestruct = document.Opcodes.Single(static opcode => opcode.Name == "selfdestruct");
        OpcodeDescriptor[] callFamily = [call, callCode, delegateCall, staticCall];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(call.OwnsPreChildTraceEnd, Is.False);
            Assert.That(create.OwnsPreChildTraceEnd, Is.True);
            Assert.That(create2.OwnsPreChildTraceEnd, Is.True);
            Assert.That(callFamily.All(static opcode =>
                    Array.IndexOf(opcode.EffectOrder, "normalizeCodeSourceLow160") ==
                    Array.IndexOf(opcode.EffectOrder, "popCodeSource") + 1),
                Is.True, "every CALL-family root normalizes its popped code source immediately");
            Assert.That(Array.IndexOf(call.EffectOrder, "chargeValue"),
                Is.LessThan(Array.IndexOf(call.EffectOrder, "expandAndChargeInputMemory")));
            Assert.That(Array.IndexOf(call.EffectOrder, "chargeNewAccountState"),
                Is.LessThan(Array.IndexOf(call.EffectOrder, "reserveEip150Execution")));
            Assert.That(Array.IndexOf(create.EffectOrder, "traceFinishBeforeReservation"),
                Is.LessThan(Array.IndexOf(create.EffectOrder, "reserveEip150AllExecution")));
            Assert.That(create.EffectOrder, Does.Contain("skipGenericCreateTraceClosure"));
            Assert.That(Array.IndexOf(staticCall.EffectOrder, "excludeRipemd160DirectPrecompile"),
                Is.LessThan(Array.IndexOf(staticCall.EffectOrder, "tryStandardInlinePrecompile")));
            Assert.That(Array.IndexOf(staticCall.EffectOrder, "boundReturnedGasToReservation"),
                Is.LessThan(Array.IndexOf(staticCall.EffectOrder, "tryStandardInlinePrecompile")));
            Assert.That(Array.IndexOf(create.EffectOrder, "resumeRefundChildGas"),
                Is.LessThan(Array.IndexOf(create.EffectOrder, "chargeParentDepositExecution")));
            Assert.That(Array.IndexOf(create.EffectOrder, "chargeParentDepositState"),
                Is.LessThan(Array.IndexOf(create.EffectOrder, "commitChild")));
            Assert.That(Array.IndexOf(create.EffectOrder, "commitChild"),
                Is.LessThan(Array.IndexOf(create.EffectOrder, "repayStateSpill")));
            Assert.That(selfDestruct.Terminates, Is.True);
            Assert.That(Array.IndexOf(selfDestruct.EffectOrder, "chargeSelfDestructBase"),
                Is.LessThan(Array.IndexOf(selfDestruct.EffectOrder, "popBeneficiary")));
            Assert.That(Array.IndexOf(selfDestruct.EffectOrder, "normalizeBeneficiaryLow160"),
                Is.EqualTo(Array.IndexOf(selfDestruct.EffectOrder, "popBeneficiary") + 1));
            Assert.That(Array.IndexOf(selfDestruct.EffectOrder, "normalizeBeneficiaryLow160"),
                Is.LessThan(Array.IndexOf(selfDestruct.EffectOrder, "warmAndChargeBeneficiary")));
            Assert.That(Array.IndexOf(selfDestruct.EffectOrder, "chargeAccountWrite"),
                Is.LessThan(Array.IndexOf(selfDestruct.EffectOrder, "chargeNewAccountState")));
        }
    }

    [Test]
    public void Generated_transition_is_theorem_free_and_independent_of_handwritten_targets()
    {
        string generated = File.ReadAllText(Path.Combine(GeneratedDirectory(), CallCreateOpcodeProfile.LeanFileName));
        string representation = File.ReadAllText(Path.Combine(RepoRoot(), SpecPath));
        string reference = File.ReadAllText(Path.Combine(RepoRoot(), ReferenceSpecPath));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(generated, Does.Not.Match(@"(?m)^\s*(theorem|axiom|example|admit|sorry)\b"));
            Assert.That(generated, Does.Not.Contain("Eip803x.Evm.CallCreateFrame"));
            Assert.That(generated, Does.Not.Contain("Eip803x.Evm.SelfDestruct"));
            Assert.That(generated, Does.Not.Contain("CallCreateOpcodeExtractor.Specification.CallCreateOperational"));
            Assert.That(generated, Does.Not.Contain("CallCreateExecution.Reference"));
            Assert.That(generated, Does.Not.Contain("DispatchTable.tracing"));
            Assert.That(generated, Does.Not.Contain("DispatchTable.cancelable"));
            Assert.That(generated, Does.Not.Contain("productionAddressOfWord"));
            Assert.That(generated, Does.Not.Contain("productionCreateDepositOrder"));
            Assert.That(generated, Does.Not.Contain("prepareCall"));
            Assert.That(generated, Does.Not.Contain("prepareCreate"));
            Assert.That(generated, Does.Not.Contain("finishFrame"));
            Assert.That(generated, Does.Contain("def executeExtracted"));
            Assert.That(generated, Does.Contain("def resumeChild"));
            Assert.That(generated, Does.Contain("def runSelfDestruct"));
            Assert.That(generated, Does.Contain("def tableTracing"));
            Assert.That(reference, Does.Contain("def referenceTableTracing"));
            Assert.That(generated, Does.Contain("def addressOfWord"));
            Assert.That(reference, Does.Contain("def addressOfWord"));
            Assert.That(generated, Does.Contain("def extractedCreateDepositOrder"));
            Assert.That(reference, Does.Contain("def createDepositOrder"));
            Assert.That(representation, Does.Not.Contain("def tracing"));
            Assert.That(representation, Does.Not.Contain("def cancelable"));
            Assert.That(representation, Does.Not.Contain("def productionAddressOfWord"));
            Assert.That(representation, Does.Not.Contain("def productionCreateDepositOrder"));
        }
    }

    [Test]
    public void Universal_refinement_covers_immediate_and_resumed_execution_without_equality_oracle()
    {
        string reference = File.ReadAllText(Path.Combine(RepoRoot(), ReferenceSpecPath));
        string refinement = File.ReadAllText(Path.Combine(
            RepoRoot(), "tools/Evm/Lean/CallCreateOpcodeExtractor/Refinement/CallCreateOpcode.lean"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reference, Does.Contain("def executeAmsterdam"));
            Assert.That(reference, Does.Contain("def resumeAmsterdam"));
            Assert.That(reference, Does.Contain("def executeSelfDestruct"));
            Assert.That(reference, Does.Not.Contain("Eip803x.Generated.CallCreateOpcodeKernel"));
            Assert.That(refinement, Does.Contain("theorem closed_amsterdam_refines"));
            Assert.That(refinement, Does.Contain("(table : DispatchTable) (opcode : Opcode) (oracle : HandlerOracle)"));
            Assert.That(refinement, Does.Contain("(state : MachineState) (child : ChildOutcome)"));
            Assert.That(refinement, Does.Contain("executeAmsterdam table opcode oracle state ="));
            Assert.That(refinement, Does.Contain("resumeChild amsterdamSchedule table child state ="));
            Assert.That(refinement, Does.Contain("theorem selfdestruct_high_bit_beneficiary_normalizes_before_effects"));
            Assert.That(refinement, Does.Contain("chargeAccess schedule (addressOfWord alias) state"));
            Assert.That(refinement, Does.Contain("TraceEvent.selfdestruct source (addressOfWord alias) value"));
            Assert.That(refinement, Does.Not.Contain("oracleRefines"));
            Assert.That(refinement, Does.Not.Match(@"(?m)^\s*(axiom|admit|sorry)\b"));
        }
    }

    [Test]
    public void Generated_transition_pins_call_create_trace_ownership_and_direct_precompile_guards()
    {
        string generated = File.ReadAllText(Path.Combine(GeneratedDirectory(), CallCreateOpcodeProfile.LeanFileName));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(generated, Does.Contain("let instructionEnded := finishInstruction table staged"));
            Assert.That(generated, Does.Contain("fail .suspended (startChildAction label frame instructionEnded)"));
            Assert.That(generated, Does.Contain("let preEnded := finishInstruction table charged"));
            Assert.That(generated, Does.Contain("pushZeroAfterOwnedFinish table"));
            Assert.That(generated, Does.Contain("!tableTracing table && !state.traceActions"));
            Assert.That(generated, Does.Contain("def canExecutePrecompileCallDirectly"));
            Assert.That(generated, Does.Contain("def addressOfWord (word : UInt256) : Address"));
            Assert.That(generated, Does.Contain("Word.ofNat (word.val % (2 ^ 160))"));
            Assert.That(generated, Does.Contain("some (codeSourceWord, afterSource)"));
            Assert.That(generated, Does.Contain("let codeSource := addressOfWord codeSourceWord"));
            Assert.That(generated, Does.Contain("(addressOfWord codeSource).val != 3"));
            Assert.That(generated, Does.Contain("canExecutePrecompileCallDirectly operands.codeSource"));
            Assert.That(generated, Does.Contain("def inlinePrecompileGasConsistent"));
            Assert.That(generated, Does.Contain("if !inlinePrecompileGasConsistent entry oracle then fail .oracleMismatch stipendTraced"));
            Assert.That(generated, Does.Contain("def completeBlockedCall"));
            Assert.That(generated, Does.Contain(".instructionError .notEnoughBalance"));
            Assert.That(generated, Does.Contain(".gasUpdate entry.child.gas.gasLeft gas.gasLeft"));
            Assert.That(generated, Does.Contain("def callCreatesAccount"));
            Assert.That(generated, Does.Contain("kind == .call && value.val != 0 && targetDead"));
            Assert.That(generated, Does.Contain("def createFactsConsistent"));
            Assert.That(generated, Does.Contain("if !createFactsConsistent oracle.facts then fail .oracleMismatch state"));
            Assert.That(generated, Does.Contain("Stack.push reserved.stack (Eip803x.Evm.Word.ofNat 1) with"));
            Assert.That(generated, Does.Contain("memory := stipendTraced.memory.writeRange operands.outputOffset.val clipped"));
            Assert.That(generated, Does.Not.Contain("codeAnalysisAccepted"));
        }
    }

    [Test]
    public void Generated_resume_refunds_before_parent_deposit_and_pushes_before_output_copy()
    {
        string generated = File.ReadAllText(Path.Combine(GeneratedDirectory(), CallCreateOpcodeProfile.LeanFileName));
        string reference = File.ReadAllText(Path.Combine(RepoRoot(), ReferenceSpecPath));
        using (Assert.EnterMultipleScope())
        {
            int refund = generated.IndexOf("gas := mergeBeforeStateSpillRepayment frame.gasEntry child.gas", StringComparison.Ordinal);
            int executionCharge = generated.IndexOf("match debitExecution executionCost refunded with", StringComparison.Ordinal);
            int stateCharge = generated.IndexOf("match debitState stateCost executionCharged with", StringComparison.Ordinal);
            int commit = generated.IndexOf("journal := child.journal", stateCharge, StringComparison.Ordinal);
            int repay = generated.IndexOf("gas := repayStateFromGasLeft committed.gas", StringComparison.Ordinal);
            Assert.That(refund, Is.GreaterThanOrEqualTo(0));
            Assert.That(executionCharge, Is.GreaterThan(refund));
            Assert.That(stateCharge, Is.GreaterThan(executionCharge));
            Assert.That(commit, Is.GreaterThan(stateCharge));
            Assert.That(repay, Is.GreaterThan(commit));
            Assert.That(generated, Does.Contain("def revertRefundedCreateToHalt"));
            Assert.That(generated, Does.Contain("let pushed := pushOne table (endChildAction child frame memoryTraced)"));
            Assert.That(generated, Does.Contain("writeReturnedOutput frame child.output pushed"));
            Assert.That(generated, Does.Contain("let pushed := pushZero table actionTraced"));
            Assert.That(generated, Does.Contain("let actionTraced := endChildAction child frame gasMerged"));
            Assert.That(generated, Does.Contain("pushZero table (restoreFailureWorld frame actionTraced)"));
            Assert.That(generated, Does.Contain("some (beneficiaryWord, tail)"));
            Assert.That(generated, Does.Contain("let beneficiary := addressOfWord beneficiaryWord"));
            Assert.That(generated, Does.Contain("chargeAccess schedule beneficiary popped"));
            Assert.That(generated, Does.Contain("appendTrace (.selfdestruct marked.environment.executingAccount beneficiary"));
            Assert.That(reference, Does.Contain("some (beneficiaryWord, tail)"));
            Assert.That(reference, Does.Contain("let beneficiary := addressOfWord beneficiaryWord"));
            Assert.That(reference, Does.Contain("payAccess schedule beneficiary popped"));
        }
    }

    [Test]
    public void Generated_executable_is_a_function_of_semantic_ir()
    {
        IrDocument admitted = ReadIr();
        OpcodeDescriptor[] changedOpcodes = [.. admitted.Opcodes];
        OpcodeDescriptor call = changedOpcodes.Single(static opcode => opcode.Name == "call");
        changedOpcodes[Array.IndexOf(changedOpcodes, call)] = call with
        {
            Semantics = call.Semantics with { Operation = "callcode" },
        };
        IrDocument changedOperation = admitted with { Opcodes = changedOpcodes };
        IrDocument changedSchedule = admitted with
        {
            Schedule = admitted.Schedule with { CallValue = admitted.Schedule.CallValue + 1 },
        };
        string digest = new('a', 64);

        string original = Encoding.UTF8.GetString(
            CallCreateOpcodeLeanEmitter.EmitFromSemanticIr(admitted, digest, digest));
        string operation = Encoding.UTF8.GetString(
            CallCreateOpcodeLeanEmitter.EmitFromSemanticIr(changedOperation, digest, digest));
        string schedule = Encoding.UTF8.GetString(
            CallCreateOpcodeLeanEmitter.EmitFromSemanticIr(changedSchedule, digest, digest));
        string reference = File.ReadAllText(Path.Combine(RepoRoot(), ReferenceSpecPath));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(operation, Is.Not.EqualTo(original));
            Assert.That(operation, Does.Contain("operation := \"callcode\""));
            Assert.That(operation, Does.Contain("semanticOpcode item.operation"));
            Assert.That(schedule, Is.Not.EqualTo(original));
            Assert.That(original, Does.Contain("callBase := 0, callValue := 11300"));
            Assert.That(schedule, Does.Contain("callBase := 0, callValue := 11301"));
            Assert.That(schedule, Does.Contain("debitExecution schedule.callValue"));
            Assert.That(reference, Does.Contain("callBase := 0, callValue := 11300"));
            Assert.That(reference, Does.Not.Contain("callValue := 11301"));
        }
    }

    [Test]
    public void Ir_rejects_every_property_omission_null_case_alias_duplicate_and_leaf_change() =>
        AssertEveryPropertyMutationRejected(
            ReadChecked(CallCreateOpcodeProfile.IrFileName), CallCreateOpcodeProfile.DeserializeIr);

    [Test]
    public void Manifest_rejects_every_property_omission_null_case_alias_duplicate_and_leaf_change() =>
        AssertEveryPropertyMutationRejected(
            ReadChecked(CallCreateOpcodeProfile.ManifestFileName), CallCreateOpcodeProfile.DeserializeManifest);

    [Test]
    public void Ir_rejects_null_changed_and_reordered_scalar_array_elements()
    {
        byte[] bytes = ReadChecked(CallCreateOpcodeProfile.IrFileName);
        JsonNode root = JsonNode.Parse(bytes)!;
        foreach (string property in new[] { "forkLineage", "openExtractionObligations" })
            AssertScalarArrayMutationsRejected(root, [property], bytes, CallCreateOpcodeProfile.DeserializeIr);

        JsonArray opcodes = root["opcodes"]!.AsArray();
        for (int index = 0; index < opcodes.Count; index++)
            AssertScalarArrayMutationsRejected(root, ["opcodes", index, "effectOrder"], bytes,
                CallCreateOpcodeProfile.DeserializeIr);
    }

    [Test]
    public void Manifest_rejects_null_changed_and_reordered_semantic_bindings()
    {
        byte[] bytes = ReadChecked(CallCreateOpcodeProfile.ManifestFileName);
        AssertScalarArrayMutationsRejected(JsonNode.Parse(bytes)!, ["semanticBindings"], bytes,
            CallCreateOpcodeProfile.DeserializeManifest);
    }

    [TestCase("ir")]
    [TestCase("lean")]
    public void Manifest_rejects_alternate_valid_artifact_digest(string artifact)
    {
        JsonObject manifest = JsonNode.Parse(ReadChecked(CallCreateOpcodeProfile.ManifestFileName))!.AsObject();
        manifest[artifact]!["sha256"] = new string('a', 64);
        Assert.That(() => CallCreateOpcodeProfile.DeserializeManifest(ToBytes(manifest)),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Manifest_rejects_uppercase_and_newline_in_every_digest()
    {
        JsonNode admitted = JsonNode.Parse(ReadChecked(CallCreateOpcodeProfile.ManifestFileName))!;
        PropertyPath[] digests = EnumerateProperties(admitted)
            .Where(static property => property.Name == "sha256" ||
                property.Name.EndsWith("Sha256", StringComparison.Ordinal)).ToArray();
        Assert.That(digests, Is.Not.Empty);
        foreach (PropertyPath digest in digests)
            foreach (Func<string, string> mutation in new Func<string, string>[]
                     {
                     static value => value.ToUpperInvariant(),
                     static value => value + "\n",
                     })
            {
                JsonNode changed = admitted.DeepClone();
                JsonObject owner = LocateOwner(changed, digest);
                owner[digest.Name] = mutation(owner[digest.Name]!.GetValue<string>());
                Assert.That(() => CallCreateOpcodeProfile.DeserializeManifest(ToBytes(changed)),
                    Throws.TypeOf<ExtractionException>(), digest.Display);
            }
    }

    [TestCase("ir", "short")]
    [TestCase("ir", "long")]
    [TestCase("ir", "nonhex")]
    [TestCase("ir", "upper")]
    [TestCase("ir", "leading-space")]
    [TestCase("ir", "newline")]
    [TestCase("source", "short")]
    [TestCase("source", "long")]
    [TestCase("source", "nonhex")]
    [TestCase("source", "upper")]
    [TestCase("source", "leading-space")]
    [TestCase("source", "newline")]
    public void Emitter_rejects_every_noncanonical_digest_shape(string target, string mutation)
    {
        IrDocument document = ReadIr();
        string valid = new('a', 64);
        string invalid = mutation switch
        {
            "short" => valid[..63],
            "long" => valid + "a",
            "nonhex" => valid[..63] + "g",
            "upper" => valid.ToUpperInvariant(),
            "leading-space" => " " + valid,
            "newline" => valid[..32] + "\n" + valid[32..],
            _ => throw new AssertionException($"Unknown digest mutation {mutation}."),
        };
        Assert.That(() => CallCreateOpcodeLeanEmitter.EmitFromSemanticIr(
                document, target == "ir" ? invalid : valid, target == "source" ? invalid : valid),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Emitter_embeds_alternate_valid_lowercase_digests_exactly()
    {
        string ir = new('a', 64);
        string source = new('b', 64);
        string lean = Encoding.UTF8.GetString(CallCreateOpcodeLeanEmitter.EmitFromSemanticIr(ReadIr(), ir, source));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(lean, Does.Contain($"-- Canonical IR SHA-256: {ir}\n"));
            Assert.That(lean, Does.Contain($"-- Source closure SHA-256: {source}\n"));
        }
    }

    [Test]
    public void Checked_artifacts_are_utf8_without_bom_lf_only_and_newline_terminated()
    {
        foreach (string fileName in new[]
                 {
                     CallCreateOpcodeProfile.IrFileName,
                     CallCreateOpcodeProfile.ManifestFileName,
                     CallCreateOpcodeProfile.LeanFileName,
                 })
        {
            byte[] bytes = ReadChecked(fileName);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(bytes, Has.Length.GreaterThan(0), fileName);
                Assert.That(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }), Is.False, fileName);
                Assert.That(bytes, Does.Not.Contain((byte)'\r'), fileName);
                Assert.That(bytes[^1], Is.EqualTo((byte)'\n'), fileName);
                Assert.That(() => new UTF8Encoding(false, true).GetString(bytes), Throws.Nothing, fileName);
            }
        }
    }

    [Test]
    public void Every_admitted_source_build_selector_and_representation_rejects_one_byte_change()
    {
        foreach (string path in CallCreateOpcodeProfile.InputPaths)
        {
            using Fixture fixture = new();
            fixture.AppendByte(path, (byte)'\n');
            Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>(), path);
        }
    }

    [TestCase("CallOpcode")]
    [TestCase("CreateOpcode")]
    [TestCase("SelfDestructOpcode")]
    public void Competing_standard_handler_declaration_is_rejected(string handler)
    {
        using Fixture fixture = new();
        fixture.WriteAdditional("src/Nethermind/Nethermind.Evm/CompetingCallCreateOpcode.cs", $$"""
            namespace Nethermind.Evm;
            public partial class VirtualMachine<TGasPolicy>
            {
                private readonly struct {{handler}} { }
            }
            """);
        Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());
    }

    [TestCase("traceActions : Bool", "traceActions : Nat", TestName = "MutationRejectsActionTraceCapability")]
    [TestCase("rollbackWorld : WorldToken", "rollbackWorld : JournalToken", TestName = "MutationRejectsRollbackSnapshotType")]
    [TestCase("exceptionStatus : Status", "exceptionStatus : ChildExit", TestName = "MutationRejectsChildErrorTraceType")]
    [TestCase("precompileGasRemaining : Nat", "precompileGasRemaining : Int", TestName = "MutationRejectsDirectPrecompileGasType")]
    public void Representation_mutations_cannot_reuse_admission(string before, string after)
    {
        using Fixture fixture = new();
        fixture.Replace(SpecPath, before, after);
        Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());
    }

    [TestCase("| .traced | .tracedCancelable => true", "| .traced | .tracedCancelable => false",
        TestName = "ReferenceTracingMutationCannotReuseAdmission")]
    [TestCase("word.val % (2 ^ 160)", "word.val % (2 ^ 161)",
        TestName = "ReferenceAddressMutationCannotReuseAdmission")]
    [TestCase("[.refundChild, .chargeParentExecution, .chargeParentState, .commitChild, .repayStateSpill]",
        "[.chargeParentExecution, .refundChild, .chargeParentState, .commitChild, .repayStateSpill]",
        TestName = "ReferenceDepositOrderMutationCannotReuseAdmission")]
    public void Independent_reference_semantic_mutation_cannot_reuse_admission(string before, string after)
    {
        using Fixture fixture = new();
        fixture.Replace(ReferenceSpecPath, before, after);
        Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());
    }

    private static IrDocument ReadIr() =>
        CallCreateOpcodeProfile.DeserializeIr(ReadChecked(CallCreateOpcodeProfile.IrFileName));

    private static byte[] ToBytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

    private static void AssertScalarArrayMutationsRejected<T>(JsonNode root, object[] path,
        byte[] admittedBytes, Func<byte[], T> deserialize)
    {
        JsonArray array = FollowPath(root, path).AsArray();
        Assert.That(array, Is.Not.Empty, string.Join(".", path));
        for (int index = 0; index < array.Count; index++)
        {
            foreach (bool useNull in new[] { true, false })
            {
                JsonNode changed = JsonNode.Parse(admittedBytes)!;
                JsonArray changedArray = FollowPath(changed, path).AsArray();
                changedArray[index] = useNull ? null : ChangedValue(changedArray[index]!);
                Assert.That(() => deserialize(ToBytes(changed)), Throws.TypeOf<ExtractionException>(),
                    $"{string.Join(".", path)}[{index}] {(useNull ? "null" : "leaf change")}");
            }
        }
        if (array.Count > 1)
        {
            JsonNode changed = JsonNode.Parse(admittedBytes)!;
            JsonArray changedArray = FollowPath(changed, path).AsArray();
            JsonNode first = changedArray[0]!.DeepClone();
            changedArray[0] = changedArray[1]!.DeepClone();
            changedArray[1] = first;
            Assert.That(() => deserialize(ToBytes(changed)), Throws.TypeOf<ExtractionException>(),
                $"{string.Join(".", path)} reordered");
        }
    }

    private static JsonNode FollowPath(JsonNode root, IEnumerable<object> path)
    {
        JsonNode current = root;
        foreach (object part in path)
            current = part is string property ? current[property]! : current[(int)part]!;
        return current;
    }

    private static void AssertEveryPropertyMutationRejected<T>(byte[] bytes, Func<byte[], T> deserialize)
    {
        JsonNode root = JsonNode.Parse(bytes)!;
        PropertyPath[] properties = EnumerateProperties(root).ToArray();
        Assert.That(properties, Is.Not.Empty);
        foreach (PropertyPath property in properties)
        {
            foreach (PropertyMutation mutation in new[]
                     {
                         PropertyMutation.Omit, PropertyMutation.Null, PropertyMutation.CaseAlias,
                     })
            {
                JsonNode changed = root.DeepClone();
                Mutate(LocateOwner(changed, property), property.Name, mutation);
                Assert.That(() => deserialize(ToBytes(changed)), Throws.TypeOf<ExtractionException>(),
                    $"{property.Display} {mutation}");
            }

            Assert.That(() => deserialize(DuplicateProperty(root, property)),
                Throws.TypeOf<ExtractionException>(), $"{property.Display} duplicate");

            JsonNode? value = LocateOwner(root, property)[property.Name];
            if (value is JsonValue)
            {
                JsonNode changed = root.DeepClone();
                JsonObject owner = LocateOwner(changed, property);
                owner[property.Name] = ChangedValue(owner[property.Name]!);
                Assert.That(() => deserialize(ToBytes(changed)), Throws.TypeOf<ExtractionException>(),
                    $"{property.Display} leaf change");
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

    private static JsonObject LocateOwner(JsonNode root, PropertyPath property)
    {
        JsonNode current = root;
        foreach (PathPart part in property.Parent)
            current = part.Name is not null ? current[part.Name]! : current[part.Index!.Value]!;
        return current.AsObject();
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
                JsonNode value = owner[name]!.DeepClone();
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
        if (value.TryGetValue(out long signed)) return JsonValue.Create(signed + 1)!;
        if (value.TryGetValue(out ulong unsigned)) return JsonValue.Create(unsigned + 1)!;
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

    private static byte[] ReadChecked(string name) => File.ReadAllBytes(Path.Combine(GeneratedDirectory(), name));

    private static string GeneratedDirectory() =>
        Path.Combine(RepoRoot(), "tools/Evm/Lean/CallCreateOpcodeExtractor/Generated");

    private static ExtractionResult Extract(string root, string output) => CallCreateOpcodeProfile.Extract(
        root, output, Path.Combine(output, CallCreateOpcodeProfile.LeanFileName));

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, HandlerPath))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Nethermind repository root.");
    }

    private enum PropertyMutation { Omit, Null, CaseAlias }

    private sealed record PathPart(string? Name, int? Index);

    private sealed record PropertyPath(IReadOnlyList<PathPart> Parent, string Name)
    {
        public string Display => string.Concat(Parent.Select(static part => part.Name ?? $"[{part.Index}]")) + "." + Name;
    }

    private sealed class Fixture : IDisposable
    {
        internal Fixture()
        {
            Root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "call-create-fixtures", Guid.NewGuid().ToString("N"));
            Output = Path.Combine(Root, "generated");
            foreach (string relativePath in CallCreateOpcodeProfile.InputPaths)
            {
                string target = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)), target);
            }
        }

        internal string Root { get; }
        internal string Output { get; }

        internal void Extract() => CallCreateOpcodeProfile.Extract(
            Root, Output, Path.Combine(Output, CallCreateOpcodeProfile.LeanFileName));

        internal void AppendByte(string relativePath, byte value)
        {
            using FileStream stream = File.Open(
                Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)),
                FileMode.Append, FileAccess.Write, FileShare.None);
            stream.WriteByte(value);
        }

        internal void Replace(string relativePath, string before, string after)
        {
            string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            string source = File.ReadAllText(path);
            Assert.That(source, Does.Contain(before));
            File.WriteAllText(path, source.Replace(before, after, StringComparison.Ordinal), new UTF8Encoding(false));
        }

        internal void WriteAdditional(string relativePath, string source)
        {
            string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source, new UTF8Encoding(false));
        }

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
