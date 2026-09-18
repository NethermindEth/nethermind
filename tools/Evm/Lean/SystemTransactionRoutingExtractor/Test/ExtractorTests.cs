// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.SystemTransactionRoutingExtractor.Test;

[TestFixture]
public class ExtractorTests
{
    [Test]
    public void Extraction_is_deterministic_theorem_free_and_records_all_semantic_bindings()
    {
        using Fixture fixture = new();

        string leanOutput = Path.Combine(fixture.Output, "SystemTransactionRoutingKernel.lean");
        ExtractionResult first = Extractor.Extract(fixture.Root, fixture.Output, leanOutput);
        byte[] firstIr = File.ReadAllBytes(first.IrPath);
        byte[] firstManifest = File.ReadAllBytes(first.ManifestPath);
        byte[] firstLean = File.ReadAllBytes(first.LeanPath);
        ExtractionResult second = Extractor.Extract(fixture.Root, fixture.Output, leanOutput);
        using JsonDocument ir = JsonDocument.Parse(firstIr);
        using JsonDocument manifest = JsonDocument.Parse(firstManifest);
        JsonElement program = ir.RootElement.GetProperty("program");
        string leanText = Encoding.UTF8.GetString(firstLean);
        string irHash = Convert.ToHexString(SHA256.HashData(firstIr)).ToLowerInvariant();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(second.IrPath), Is.EqualTo(firstIr));
            Assert.That(File.ReadAllBytes(second.ManifestPath), Is.EqualTo(firstManifest));
            Assert.That(File.ReadAllBytes(second.LeanPath), Is.EqualTo(firstLean));
            Assert.That(first.SourceCount, Is.EqualTo(7));
            Assert.That(
                ir.RootElement.GetProperty("ancestorBaselineCommit").GetString(),
                Is.EqualTo(Extractor.AncestorBaselineCommit));
            Assert.That(
                ir.RootElement.GetProperty("sourceIdentityAuthority").GetString(),
                Is.EqualTo(Extractor.SourceIdentityAuthority));
            Assert.That(
                manifest.RootElement.GetProperty("ancestorBaselineCommit").GetString(),
                Is.EqualTo(Extractor.AncestorBaselineCommit));
            Assert.That(
                manifest.RootElement.GetProperty("sourceIdentityAuthority").GetString(),
                Is.EqualTo(Extractor.SourceIdentityAuthority));
            Assert.That(manifest.RootElement.GetProperty("sources").GetArrayLength(), Is.EqualTo(7));
            Assert.That(manifest.RootElement.GetProperty("admissions").GetArrayLength(), Is.EqualTo(7));
            Assert.That(manifest.RootElement.GetProperty("semanticBindings").GetArrayLength(), Is.EqualTo(9));
            JsonElement adapter = ir.RootElement.GetProperty("adapter");
            Assert.That(adapter.GetProperty("payValueUsesAssignedField").GetBoolean(), Is.True);
            Assert.That(adapter.GetProperty("payValueOverridesBase").GetBoolean(), Is.True);
            Assert.That(adapter.GetProperty("virtualPayValueCallsBound").GetBoolean(), Is.True);
            Assert.That(adapter.GetProperty("scopedBindingImplementationBound").GetBoolean(), Is.True);
            Assert.That(adapter.GetProperty("routedSystemForwardingMustReach").GetBoolean(), Is.True);
            Assert.That(adapter.GetProperty("admittedArgumentBindingsRemainDirect").GetBoolean(), Is.True);
            Assert.That(adapter.GetProperty("boundReferenceArgumentsFailClosed").GetBoolean(), Is.True);
            Assert.That(adapter.GetProperty("mainnetRegistrationDominatesLoadExit").GetBoolean(), Is.True);
            Assert.That(
                adapter.GetProperty("counterClaim").GetString(),
                Is.EqualTo("normal counter-gate decision only; virtual PayFees effects are outside the claim"));
            Assert.That(
                manifest.RootElement.GetProperty("lean").GetProperty("sha256").GetString(),
                Is.EqualTo(Convert.ToHexString(SHA256.HashData(firstLean)).ToLowerInvariant()));
            Assert.That(leanText, Does.Contain($"-- IR SHA-256: {irHash}"));
            Assert.That(
                leanText,
                Does.Contain($"-- useSystemProcessor: {program.GetProperty("useSystemProcessor").GetString()}"));
            Assert.That(
                leanText,
                Does.Contain($"-- shouldPayOriginalValue: {program.GetProperty("shouldPayOriginalValue").GetString()}"));
            Assert.That(
                leanText,
                Does.Contain($"-- getSystemExecutionOptions: {program.GetProperty("getSystemExecutionOptions").GetString()}"));
            Assert.That(
                leanText,
                Does.Contain($"-- participatesInNormalBlockCounters: {program.GetProperty("participatesInNormalBlockCounters").GetString()}"));
            Assert.That(
                leanText,
                Does.Contain("isSystemTransaction || (options.skipValidation && !options.commit && !options.restore && !options.warmup && !options.buildUp)"));
            Assert.That(leanText, Does.Contain("!options.skipValidation"));
            Assert.That(
                leanText,
                Does.Contain("if payOriginalValue then { options with commit := true, skipValidation := true } else options"));
            Assert.That(leanText, Does.Contain("!options.skipValidation && !parallel"));
            Assert.That(leanText, Does.Not.Contain("theorem"));
        }
    }

    [Test]
    public void Serialized_ir_rejects_malformed_unknown_and_null_values()
    {
        byte[] sourceDerived = ReadCheckedIr();

        JsonObject unknownRoot = ParseSerializedIr(sourceDerived);
        unknownRoot["unexpected"] = JsonValue.Create(true);
        JsonObject unknownProgram = ParseSerializedIr(sourceDerived);
        unknownProgram["program"]!.AsObject()["unexpected"] = JsonValue.Create(true);
        JsonObject casedRoot = ParseSerializedIr(sourceDerived);
        casedRoot["SchemaVersion"] = casedRoot["schemaVersion"]!.DeepClone();
        casedRoot.Remove("schemaVersion");
        JsonObject casedAdapter = ParseSerializedIr(sourceDerived);
        casedAdapter["adapter"]!.AsObject()["KernelCallsBound"] =
            casedAdapter["adapter"]!.AsObject()["kernelCallsBound"]!.DeepClone();
        casedAdapter["adapter"]!.AsObject().Remove("kernelCallsBound");
        JsonObject missingProgramField = ParseSerializedIr(sourceDerived);
        missingProgramField["program"]!.AsObject().Remove("none");
        JsonObject missingAdapterBool = ParseSerializedIr(sourceDerived);
        missingAdapterBool["adapter"]!.AsObject().Remove("isSystemClassifierBound");
        JsonObject nullKernel = ParseSerializedIr(sourceDerived);
        nullKernel["kernel"] = null;
        JsonObject nullProgram = ParseSerializedIr(sourceDerived);
        nullProgram["program"] = null;
        JsonObject nullProgramField = ParseSerializedIr(sourceDerived);
        nullProgramField["program"]!.AsObject()["useSystemProcessor"] = null;
        JsonObject nullAdapter = ParseSerializedIr(sourceDerived);
        nullAdapter["adapter"] = null;
        JsonObject nullAdapterField = ParseSerializedIr(sourceDerived);
        nullAdapterField["adapter"]!.AsObject()["counterClaim"] = null;

        AssertSerializedIrRejected(sourceDerived, Encoding.UTF8.GetBytes("{"), "not valid JSON");
        AssertSerializedIrRejected(sourceDerived, Encoding.UTF8.GetBytes("null"), "empty");
        AssertSerializedIrRejected(sourceDerived, SerializeSerializedIr(unknownRoot), "not valid JSON");
        AssertSerializedIrRejected(sourceDerived, SerializeSerializedIr(unknownProgram), "not valid JSON");
        AssertSerializedIrRejected(sourceDerived, SerializeSerializedIr(casedRoot), "not valid JSON");
        AssertSerializedIrRejected(sourceDerived, SerializeSerializedIr(casedAdapter), "not valid JSON");
        AssertSerializedIrRejected(sourceDerived, SerializeSerializedIr(missingProgramField), "not valid JSON");
        AssertSerializedIrRejected(sourceDerived, SerializeSerializedIr(missingAdapterBool), "not valid JSON");
        AssertSerializedIrRejected(sourceDerived, SerializeSerializedIr(nullKernel), "Kernel was null");
        AssertSerializedIrRejected(sourceDerived, SerializeSerializedIr(nullProgram), "Program was null");
        AssertSerializedIrRejected(sourceDerived, SerializeSerializedIr(nullProgramField), "Program.UseSystemProcessor was null");
        AssertSerializedIrRejected(sourceDerived, SerializeSerializedIr(nullAdapter), "Adapter was null");
        AssertSerializedIrRejected(sourceDerived, SerializeSerializedIr(nullAdapterField), "Adapter.CounterClaim was null");
        AssertSerializedIrRejected(sourceDerived, null!, "input was null");
    }

    [Test]
    public void Serialized_ir_binds_program_adapter_and_aggregate_claims()
    {
        byte[] sourceDerived = ReadCheckedIr();

        string[] programFields =
        [
            "none", "commit", "restore", "skipValidation", "warmup", "buildUp",
            "useSystemProcessor", "shouldPayOriginalValue", "getSystemExecutionOptions",
            "participatesInNormalBlockCounters",
        ];
        foreach (string field in programFields)
        {
            AssertSerializedIrMutationRejected(sourceDerived,
                root => MutateScalar(root["program"]!.AsObject(), field),
                string.Empty);
        }

        string[] adapterFields =
        [
            "classifier", "route", "systemFactory", "systemOverride", "counterGate", "mainnetProcessor",
            "mainnetRegistration", "isSystemClassifierBound", "kernelCallsBound", "systemExecuteOverridesBase",
            "standardProcessorInheritsDefaultFactory", "mainnetRegistrationBound", "optionsForwardedToCounterGate",
            "payValueUsesAssignedField", "payValueOverridesBase", "virtualPayValueCallsBound",
            "scopedBindingImplementationBound", "routedSystemForwardingMustReach",
            "admittedArgumentBindingsRemainDirect", "boundReferenceArgumentsFailClosed",
            "mainnetRegistrationDominatesLoadExit", "counterClaim",
        ];
        foreach (string field in adapterFields)
        {
            AssertSerializedIrMutationRejected(sourceDerived,
                root => MutateScalar(root["adapter"]!.AsObject(), field),
                string.Empty);
        }

        AssertSerializedIrMutationRejected(sourceDerived,
            static root => root["schemaVersion"] = JsonValue.Create(2),
            "SchemaVersion");
        AssertSerializedIrMutationRejected(sourceDerived,
            static root => root["program"] = new JsonObject(),
            "not valid JSON");
        AssertSerializedIrMutationRejected(sourceDerived,
            static root => root["adapter"] = new JsonObject(),
            "not valid JSON");
    }

    [Test]
    public void Serialized_manifest_strictly_binds_build_sources_admissions_artifacts_and_semantics()
    {
        byte[] sourceDerived = ReadCheckedManifest();
        JsonObject unknownRoot = ParseSerializedIr(sourceDerived);
        unknownRoot["unexpected"] = true;
        JsonObject unknownNested = ParseSerializedIr(sourceDerived);
        unknownNested["sources"]!.AsArray()[0]!.AsObject()["unexpected"] = true;
        JsonObject casedRoot = ParseSerializedIr(sourceDerived);
        Rename(casedRoot, "schemaVersion", "SchemaVersion");
        JsonObject casedNested = ParseSerializedIr(sourceDerived);
        Rename(casedNested["admissions"]!.AsArray()[0]!.AsObject(), "key", "Key");
        JsonObject omittedTop = ParseSerializedIr(sourceDerived);
        omittedTop.Remove("schemaVersion");
        JsonObject omittedNested = ParseSerializedIr(sourceDerived);
        omittedNested["sources"]!.AsArray()[0]!.AsObject().Remove("path");
        JsonObject nullSource = ParseSerializedIr(sourceDerived);
        nullSource["sources"]!.AsArray()[0] = null;
        JsonObject nullAdmission = ParseSerializedIr(sourceDerived);
        nullAdmission["admissions"]!.AsArray()[0] = null;
        JsonObject nullArtifact = ParseSerializedIr(sourceDerived);
        nullArtifact["ir"] = null;
        JsonObject nullBinding = ParseSerializedIr(sourceDerived);
        nullBinding["semanticBindings"]!.AsArray()[0] = null;

        AssertSerializedManifestRejected(sourceDerived, "{"u8.ToArray(), "not valid JSON");
        AssertSerializedManifestRejected(sourceDerived, "null"u8.ToArray(), "empty");
        AssertSerializedManifestRejected(sourceDerived, Encoding.UTF8.GetBytes(unknownRoot.ToJsonString()), "not valid JSON");
        AssertSerializedManifestRejected(sourceDerived, Encoding.UTF8.GetBytes(unknownNested.ToJsonString()), "not valid JSON");
        AssertSerializedManifestRejected(sourceDerived, Encoding.UTF8.GetBytes(casedRoot.ToJsonString()), "not valid JSON");
        AssertSerializedManifestRejected(sourceDerived, Encoding.UTF8.GetBytes(casedNested.ToJsonString()), "not valid JSON");
        AssertSerializedManifestRejected(sourceDerived, Encoding.UTF8.GetBytes(omittedTop.ToJsonString()), "not valid JSON");
        AssertSerializedManifestRejected(sourceDerived, Encoding.UTF8.GetBytes(omittedNested.ToJsonString()), "not valid JSON");
        AssertSerializedManifestRejected(sourceDerived, Encoding.UTF8.GetBytes(nullSource.ToJsonString()), "null collections or entries");
        AssertSerializedManifestRejected(sourceDerived, Encoding.UTF8.GetBytes(nullAdmission.ToJsonString()), "null collections or entries");
        AssertSerializedManifestRejected(sourceDerived, Encoding.UTF8.GetBytes(nullArtifact.ToJsonString()), "null collections or entries");
        AssertSerializedManifestRejected(sourceDerived, Encoding.UTF8.GetBytes(nullBinding.ToJsonString()), "null collections or entries");
        AssertSerializedManifestRejected(sourceDerived, null!, "input was null");

        Action<JsonObject>[] mutations =
        [
            root => root["schemaVersion"] = 2,
            root => root["extractorVersion"] = "changed",
            root => root["compilerVersion"] = "changed",
            root => root["languageVersion"] = "changed",
            root => root["ancestorBaselineCommit"] = "changed",
            root => root["sourceIdentityAuthority"] = "changed",
            root => root["kernel"] = "changed",
            root => root["sources"]!.AsArray()[0]!["path"] = "changed",
            root => root["sources"]!.AsArray()[0]!["sha256"] = new string('0', 64),
            root => root["admissions"]!.AsArray()[0]!["key"] = "changed",
            root => root["admissions"]!.AsArray()[0]!["sourceSha256"] = new string('0', 64),
            root => root["ir"]!["path"] = "changed",
            root => root["ir"]!["sha256"] = new string('0', 64),
            root => root["lean"]!["path"] = "changed",
            root => root["lean"]!["sha256"] = new string('0', 64),
            root => root["combinedSha256"] = new string('0', 64),
            root => root["semanticBindings"]!.AsArray()[0] = "changed",
        ];
        foreach (Action<JsonObject> mutation in mutations)
        {
            JsonObject candidate = ParseSerializedIr(sourceDerived);
            mutation(candidate);
            AssertSerializedManifestRejected(sourceDerived, Encoding.UTF8.GetBytes(candidate.ToJsonString()), string.Empty);
        }

        JsonObject duplicate = ParseSerializedIr(sourceDerived);
        duplicate["admissions"]!.AsArray()[1] = duplicate["admissions"]!.AsArray()[0]!.DeepClone();
        AssertSerializedManifestRejected(sourceDerived, Encoding.UTF8.GetBytes(duplicate.ToJsonString()),
            "Duplicate owner-qualified routing admission identity");
    }

    [TestCase(
        Extractor.KernelPath,
        "options == ExecutionOptions.SkipValidation",
        "(options & ExecutionOptions.SkipValidation) == ExecutionOptions.SkipValidation",
        TestName = "Rejects_route_predicate_drift")]
    [TestCase(
        Extractor.TransactionProcessorPath,
        "SystemTransactionRoutingKernel.UseSystemProcessor(tx.IsSystem(), opts)",
        "tx.IsSystem()",
        TestName = "Rejects_route_kernel_bypass")]
    [TestCase(
        Extractor.TransactionProcessorPath,
        "SystemTransactionRoutingKernel.ParticipatesInNormalBlockCounters(opts, _parallel)",
        "!_parallel",
        TestName = "Rejects_counter_exclusion_bypass")]
    [TestCase(
        Extractor.TransactionProcessorPath,
        "UpdateHeaderGasUsedAndPayFees(tx, header, spec, tracer, opts, in substate",
        "UpdateHeaderGasUsedAndPayFees(tx, header, spec, tracer, ExecutionOptions.None, in substate",
        TestName = "Rejects_options_counter_forwarding_drift")]
    [TestCase(
        Extractor.SystemProcessorPath,
        "SystemTransactionRoutingKernel.GetSystemExecutionOptions(opts, _payOriginalValue)",
        "opts",
        TestName = "Rejects_system_option_projection_bypass")]
    [TestCase(
        Extractor.SystemProcessorPath,
        "protected override TransactionResult Execute",
        "protected new TransactionResult Execute",
        TestName = "Rejects_system_execute_override_drift")]
    [TestCase(
        Extractor.SystemProcessorPath,
        "protected override void PayValue",
        "protected new void PayValue",
        TestName = "Rejects_system_pay_value_override_drift")]
    [TestCase(
        Extractor.SystemProcessorPath,
        "private bool _payOriginalValue;",
        "private bool _payOriginalValue { get; set; }",
        TestName = "Rejects_pay_value_field_rebinding")]
    [TestCase(
        Extractor.SystemProcessorPath,
        "_payOriginalValue = SystemTransactionRoutingKernel.ShouldPayOriginalValue(opts);",
        "bool _payOriginalValue = SystemTransactionRoutingKernel.ShouldPayOriginalValue(opts);",
        TestName = "Rejects_pay_value_local_shadow")]
    [TestCase(
        Extractor.TransactionExtensionsPath,
        "tx is SystemTransaction ||",
        "false ||",
        TestName = "Rejects_system_classifier_drift")]
    [TestCase(
        Extractor.TransactionProcessorPath,
        "protected virtual SystemTransactionProcessor<TGasPolicy> CreateSystemTransactionProcessor()",
        "protected SystemTransactionProcessor<TGasPolicy> CreateSystemTransactionProcessor()",
        TestName = "Rejects_factory_override_surface_drift")]
    [TestCase(
        Extractor.MainnetDiPath,
        ".AddScoped<ITransactionProcessor, EthereumTransactionProcessor>()",
        ".AddScoped<ITransactionProcessor, AlternateTransactionProcessor>()",
        TestName = "Rejects_standard_mainnet_route_drift")]
    [TestCase(
        Extractor.ContainerRegistrationPath,
        "builder.BindScoped<T, TImpl>();",
        "builder.BindScoped<TImpl, TImpl>();",
        TestName = "Rejects_production_scoped_registration_drift")]
    public void Semantic_route_and_override_mutations_are_rejected(
        string relativePath,
        string original,
        string replacement)
    {
        using Fixture fixture = new();
        fixture.Replace(relativePath, original, replacement);

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => Extractor.Extract(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Is.Not.Empty);
    }

    [Test]
    public void No_op_bind_scoped_implementation_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.ContainerRegistrationPath,
            """
                public static ContainerBuilder BindScoped<TTo, TFrom>(this ContainerBuilder builder) where TFrom : TTo where TTo : notnull
                {
                    builder.Register(static (it) => it.Resolve<TFrom>())
                        .As<TTo>()
                        .InstancePerLifetimeScope()
                        .ExternallyOwned();

                    return builder;
                }
            """,
            """
                public static ContainerBuilder BindScoped<TTo, TFrom>(this ContainerBuilder builder) where TFrom : TTo where TTo : notnull
                {
                    return builder;
                }
            """);

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => Extractor.Extract(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("BindScoped"));
    }

    [TestCase("it.Resolve<TFrom>()", "it.Resolve<TTo>()", TestName = "Rejects_BindScoped_resolve_target_drift")]
    [TestCase(".As<TTo>()", ".As<TFrom>()", TestName = "Rejects_BindScoped_service_target_drift")]
    public void Wrong_bind_scoped_generic_target_is_rejected(string original, string replacement)
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.ContainerRegistrationPath, original, replacement);

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => Extractor.Extract(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("BindScoped"));
    }

    [Test]
    public void File_and_namespace_aliases_are_rejected_before_binding()
    {
        using Fixture fileAlias = new();
        fileAlias.Replace(
            Extractor.TransactionProcessorPath,
            "using System.Threading;",
            "using System.Threading;\nusing RoutingKernel = Nethermind.Evm.TransactionProcessing.SystemTransactionRoutingKernel;");
        ExtractionException fileException = Assert.Throws<ExtractionException>(
            () => Extractor.Extract(fileAlias.Root, fileAlias.Output))!;
        Assert.That(fileException.Message, Does.Contain("aliases"));

        using Fixture namespaceAlias = new();
        namespaceAlias.Replace(
            Extractor.TransactionExtensionsPath,
            "namespace Nethermind.Core\n{",
            "namespace Nethermind.Core\n{\n    using SystemTx = Nethermind.Core.SystemTransaction;");
        ExtractionException namespaceException = Assert.Throws<ExtractionException>(
            () => Extractor.Extract(namespaceAlias.Root, namespaceAlias.Output))!;
        Assert.That(namespaceException.Message, Does.Contain("aliases"));
    }

    [Test]
    public void Static_import_directives_disabled_text_and_partial_types_are_rejected()
    {
        using Fixture staticImport = new();
        staticImport.Replace(
            Extractor.SystemProcessorPath,
            "using System.Diagnostics;",
            "using System.Diagnostics;\nusing static System.Math;");
        ExtractionException staticException = Assert.Throws<ExtractionException>(
            () => Extractor.Extract(staticImport.Root, staticImport.Output))!;
        Assert.That(staticException.Message, Does.Contain("static imports"));

        using Fixture disabled = new();
        disabled.Replace(
            Extractor.KernelPath,
            "internal static class SystemTransactionRoutingKernel",
            "#if false\ninternal static class HiddenRoutingKernel { }\n#endif\ninternal static class SystemTransactionRoutingKernel");
        ExtractionException disabledException = Assert.Throws<ExtractionException>(
            () => Extractor.Extract(disabled.Root, disabled.Output))!;
        Assert.That(disabledException.Message, Does.Contain("disabled source text"));

        using Fixture directive = new();
        directive.Replace(
            Extractor.OptionsPath,
            "using System;",
            "#nullable enable\nusing System;");
        ExtractionException directiveException = Assert.Throws<ExtractionException>(
            () => Extractor.Extract(directive.Root, directive.Output))!;
        Assert.That(directiveException.Message, Does.Contain("preprocessor"));

        using Fixture partial = new();
        partial.Replace(
            Extractor.KernelPath,
            "internal static class SystemTransactionRoutingKernel",
            "internal static partial class SystemTransactionRoutingKernel");
        ExtractionException partialException = Assert.Throws<ExtractionException>(
            () => Extractor.Extract(partial.Root, partial.Output))!;
        Assert.That(partialException.Message, Does.Contain("partial"));
    }

    [Test]
    public void Local_and_type_shadow_declarations_are_rejected()
    {
        using Fixture localShadow = new();
        localShadow.Replace(
            Extractor.TransactionProcessorPath,
            "if (Logger.IsTrace) Logger.Trace($\"Executing tx {tx.Hash}\");",
            "var SystemTransactionRoutingKernel = new object();\n            if (Logger.IsTrace) Logger.Trace($\"Executing tx {tx.Hash}\");");
        ExtractionException localException = Assert.Throws<ExtractionException>(
            () => Extractor.Extract(localShadow.Root, localShadow.Output))!;
        Assert.That(localException.Message, Does.Contain("shadowed"));

        using Fixture typeShadow = new();
        typeShadow.Replace(
            Extractor.MainnetDiPath,
            "public class BlockProcessingModule(IInitConfig initConfig, IBlocksConfig blocksConfig) : Module\n{",
            "public class BlockProcessingModule(IInitConfig initConfig, IBlocksConfig blocksConfig) : Module\n{\n    private sealed class EthereumTransactionProcessor { }");
        ExtractionException typeException = Assert.Throws<ExtractionException>(
            () => Extractor.Extract(typeShadow.Root, typeShadow.Output))!;
        Assert.That(typeException.Message, Does.Contain("shadowed"));
    }

    [Test]
    public void Same_namespace_competing_add_scoped_extension_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Append(
            Extractor.MainnetDiPath,
            """

            internal static class CompetingRegistration
            {
                internal static ContainerBuilder AddScoped<TService, TImplementation>(this ContainerBuilder builder) => builder;
            }
            """);

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => Extractor.Extract(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("competing AddScoped"));
    }

    [Test]
    public void External_same_namespace_system_classifier_is_rejected_by_actual_call_binding()
    {
        using Fixture fixture = new();
        fixture.Append(
            Extractor.TransactionExtensionsPath,
            """

            namespace Nethermind.Evm.TransactionProcessing
            {
                internal static class AlternateTransactionExtensions
                {
                    internal static bool IsSystem(this Nethermind.Core.Transaction transaction) => false;
                }
            }
            """);

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => Extractor.Extract(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("system classifier call"));
    }

    [Test]
    public void External_same_namespace_add_scoped_is_rejected_by_actual_registration_binding()
    {
        using Fixture fixture = new();
        fixture.Append(
            Extractor.TransactionExtensionsPath,
            """

            namespace Nethermind.Init.Modules
            {
                internal static class AlternateRegistrationExtensions
                {
                    internal static Autofac.ContainerBuilder AddScoped<TService, TImplementation>(
                        this Autofac.ContainerBuilder builder)
                        where TImplementation : TService
                        where TService : notnull => builder;
                }
            }
            """);

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => Extractor.Extract(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("registration"));
    }

    [TestCase("protected override void Load(ContainerBuilder builder)", "protected void Load(dynamic builder)")]
    [TestCase("protected override void Load(ContainerBuilder builder)", "protected void Load(ContainerBuilder builder)")]
    public void Dynamic_or_non_override_load_route_is_rejected(string original, string replacement)
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.MainnetDiPath, original, replacement);

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => Extractor.Extract(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("Autofac.Module.Load"));
    }

    [Test]
    public void Alternate_load_overload_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.MainnetDiPath,
            "    private static ITransactionProcessorAdapter CreateExecuteAdapter",
            "    private void Load(dynamic builder) { }\n\n    private static ITransactionProcessorAdapter CreateExecuteAdapter");

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => Extractor.Extract(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("Load must be declared exactly once"));
    }

    [TestCase(0, TestName = "Rejects_qualified_alternate_EVM_counter_target")]
    [TestCase(1, TestName = "Rejects_qualified_alternate_simple_transfer_counter_target")]
    public void Qualified_alternate_counter_target_is_rejected(int occurrence)
    {
        using Fixture fixture = new();
        fixture.ReplaceOccurrence(
            Extractor.TransactionProcessorPath,
            "UpdateHeaderGasUsedAndPayFees(tx, header, spec, tracer, opts, in substate",
            "AlternateCounterTarget.UpdateHeaderGasUsedAndPayFees(tx, header, spec, tracer, opts, in substate",
            occurrence);
        fixture.Append(
            Extractor.TransactionProcessorPath,
            """

            namespace Nethermind.Evm.TransactionProcessing
            {
                internal static class AlternateCounterTarget
                {
                    internal static void UpdateHeaderGasUsedAndPayFees(
                        Nethermind.Core.Transaction tx,
                        Nethermind.Core.BlockHeader header,
                        Nethermind.Core.Specs.IReleaseSpec spec,
                        Nethermind.Evm.Tracing.ITxTracer tracer,
                        ExecutionOptions opts,
                        in Nethermind.Evm.TransactionSubstate substate,
                        in GasConsumed spentGas,
                        Nethermind.Int256.UInt256 premiumPerGas,
                        in Nethermind.Int256.UInt256 effectiveGasPrice,
                        Nethermind.Int256.UInt256 blobBaseFee,
                        int statusCode) { }
                }
            }
            """);

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => Extractor.Extract(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("counter forwarding"));
    }

    [Test]
    public void ExecuteCore_route_must_be_a_direct_reachable_branch()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.TransactionProcessorPath,
            "            if (SystemTransactionRoutingKernel.UseSystemProcessor(tx.IsSystem(), opts))\n" +
            "            {\n" +
            "                return GetOrCreateSystemTransactionProcessor().Execute(tx, tracer, opts);\n" +
            "            }",
            "            if (tx.IsSystem())\n" +
            "            {\n" +
            "                if (SystemTransactionRoutingKernel.UseSystemProcessor(tx.IsSystem(), opts))\n" +
            "                {\n" +
            "                    return GetOrCreateSystemTransactionProcessor().Execute(tx, tracer, opts);\n" +
            "                }\n" +
            "            }");

        AssertRejected(fixture, "kernel-controlled system route");
    }

    [Test]
    public void ExecuteCore_route_must_not_return_before_system_forwarding()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.TransactionProcessorPath,
            "            if (SystemTransactionRoutingKernel.UseSystemProcessor(tx.IsSystem(), opts))\n" +
            "            {\n" +
            "                return GetOrCreateSystemTransactionProcessor().Execute(tx, tracer, opts);\n" +
            "            }",
            "            if (SystemTransactionRoutingKernel.UseSystemProcessor(tx.IsSystem(), opts))\n" +
            "            {\n" +
            "                if (tx.IsSystem()) return TransactionResult.Ok;\n" +
            "                return GetOrCreateSystemTransactionProcessor().Execute(tx, tracer, opts);\n" +
            "            }");

        AssertRejected(fixture, "direct, else-free branch with one forwarding return");
    }

    [Test]
    public void System_execute_must_retain_its_complete_control_flow()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.SystemProcessorPath,
            "        OnBeforeSystemTransaction();",
            "        if (tx is null) return TransactionResult.Ok;\n" +
            "        OnBeforeSystemTransaction();");

        AssertRejected(fixture, "complete reachable override body");
    }

    [Test]
    public void Execute_entry_option_forwarding_must_be_a_live_direct_return()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.TransactionProcessorPath,
            "            return Execute(tx, tracer, opts, header, spec, in intrinsicGas);",
            "            if (tx is null)\n" +
            "            {\n" +
            "                return Execute(tx, tracer, opts, header, spec, in intrinsicGas);\n" +
            "            }\n" +
            "            return ValidateStatic(tx, header, spec, opts, in intrinsicGas);");

        AssertRejected(fixture, "Execute entry option forwarding must be a reachable direct return");
    }

    [Test]
    public void Evm_counter_forwarding_must_be_a_live_direct_call()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.TransactionProcessorPath,
            "            UpdateHeaderGasUsedAndPayFees(tx, header, spec, tracer, opts, in substate, in spentGas, premiumPerGas, in opcodeGasPrice, blobBaseFee, statusCode);",
            "            if (tx is null)\n" +
            "            {\n" +
            "                UpdateHeaderGasUsedAndPayFees(tx, header, spec, tracer, opts, in substate, in spentGas, premiumPerGas, in opcodeGasPrice, blobBaseFee, statusCode);\n" +
            "            }");

        AssertRejected(fixture, "EVM counter forwarding must be a reachable direct body statement");
    }

    [Test]
    public void Evm_counter_forwarding_must_postdominate_each_dispatch_path()
    {
        using Fixture fixture = new();
        fixture.ReplaceOccurrence(
            Extractor.TransactionProcessorPath,
            "            UpdateHeaderGasUsedAndPayFees(tx, header, spec, tracer, opts, in substate, in spentGas, premiumPerGas, in opcodeGasPrice, blobBaseFee, statusCode);",
            "            if (tx is null) return TransactionResult.Ok;\n\n" +
            "            UpdateHeaderGasUsedAndPayFees(tx, header, spec, tracer, opts, in substate, in spentGas, premiumPerGas, in opcodeGasPrice, blobBaseFee, statusCode);",
            occurrence: 0);

        AssertRejected(fixture, "does not postdominate its source operation");
    }

    [Test]
    public void Counter_forwarding_must_not_reassign_options_before_the_call()
    {
        using Fixture fixture = new();
        fixture.ReplaceOccurrence(
            Extractor.TransactionProcessorPath,
            "            UpdateHeaderGasUsedAndPayFees(tx, header, spec, tracer, opts, in substate, in spentGas, premiumPerGas, in opcodeGasPrice, blobBaseFee, statusCode);",
            "            opts = ExecutionOptions.None;\n" +
            "            UpdateHeaderGasUsedAndPayFees(tx, header, spec, tracer, opts, in substate, in spentGas, premiumPerGas, in opcodeGasPrice, blobBaseFee, statusCode);",
            occurrence: 0);

        AssertRejected(fixture, "changes the admitted 'opts' parameter before the call");
    }

    [TestCase("Rewrite(ref opts);", TestName = "Counter_forwarding_must_not_pass_options_through_a_ref_write")]
    [TestCase("Clear(out opts);", TestName = "Counter_forwarding_must_not_pass_options_through_an_out_write")]
    public void Counter_forwarding_must_not_pass_options_through_a_ref_or_out_write(string mutation)
    {
        using Fixture fixture = new();
        fixture.ReplaceOccurrence(
            Extractor.TransactionProcessorPath,
            "            UpdateHeaderGasUsedAndPayFees(tx, header, spec, tracer, opts, in substate, in spentGas, premiumPerGas, in opcodeGasPrice, blobBaseFee, statusCode);",
            $"            RoutingParameterMutation.{mutation}\n" +
            "            UpdateHeaderGasUsedAndPayFees(tx, header, spec, tracer, opts, in substate, in spentGas, premiumPerGas, in opcodeGasPrice, blobBaseFee, statusCode);",
            occurrence: 0);
        fixture.Append(
            Extractor.TransactionProcessorPath,
            "\n\nnamespace Nethermind.Evm.TransactionProcessing\n" +
            "{\n" +
            "    internal static class RoutingParameterMutation\n" +
            "    {\n" +
            "        internal static void Rewrite(ref ExecutionOptions value) => value = ExecutionOptions.None;\n" +
            "        internal static void Clear(out ExecutionOptions value) => value = ExecutionOptions.None;\n" +
            "    }\n" +
            "}\n");

        AssertRejected(fixture, "changes the admitted 'opts' parameter before the call");
    }

    [TestCase(0, "simple-transfer PayValue dispatch", TestName = "Simple_transfer_PayValue_must_forward_all_arguments")]
    [TestCase(1, "EVM PayValue dispatch", TestName = "EVM_PayValue_must_forward_all_arguments")]
    public void PayValue_calls_must_forward_the_caller_transaction_spec_and_options(
        int occurrence,
        string description)
    {
        using Fixture fixture = new();
        fixture.ReplaceOccurrence(
            Extractor.TransactionProcessorPath,
            "PayValue(tx, spec, opts);",
            "PayValue(tx, spec, ExecutionOptions.None);",
            occurrence);

        AssertRejected(fixture, $"{description} argument 2 must be the unchanged 'opts' parameter");
    }

    [TestCase("Rewrite(ref tx);", "tx", TestName = "PayValue_must_not_pass_transaction_through_a_ref_write")]
    [TestCase("Clear(out tx);", "tx", TestName = "PayValue_must_not_pass_transaction_through_an_out_write")]
    [TestCase("Rewrite(ref spec);", "spec", TestName = "PayValue_must_not_pass_specification_through_a_ref_write")]
    [TestCase("Clear(out spec);", "spec", TestName = "PayValue_must_not_pass_specification_through_an_out_write")]
    public void PayValue_must_not_pass_transaction_or_specification_through_a_ref_or_out_write(
        string mutation,
        string parameter)
    {
        using Fixture fixture = new();
        fixture.ReplaceOccurrence(
            Extractor.TransactionProcessorPath,
            "            PayValue(tx, spec, opts);",
            $"            RoutingParameterMutation.{mutation}\n" +
            "            PayValue(tx, spec, opts);",
            occurrence: 0);
        fixture.Append(
            Extractor.TransactionProcessorPath,
            "\n\nnamespace Nethermind.Evm.TransactionProcessing\n" +
            "{\n" +
            "    internal static class RoutingParameterMutation\n" +
            "    {\n" +
            "        internal static void Rewrite(ref Nethermind.Core.Transaction value) => value = new();\n" +
            "        internal static void Clear(out Nethermind.Core.Transaction value) => value = new();\n" +
            "        internal static void Rewrite(ref Nethermind.Core.Specs.IReleaseSpec value) => value = null!;\n" +
            "        internal static void Clear(out Nethermind.Core.Specs.IReleaseSpec value) => value = null!;\n" +
            "    }\n" +
            "}\n");

        AssertRejected(fixture, $"changes the admitted '{parameter}' parameter before the call");
    }

    [TestCase("RoutingParameterMutation.Mutate(tx);", "tx", TestName = "PayValue_must_not_pass_transaction_by_value_to_an_unmodeled_call")]
    [TestCase("RoutingParameterMutation.Mutate(spec);", "spec", TestName = "PayValue_must_not_pass_specification_by_value_to_an_unmodeled_call")]
    [TestCase(
        "Transaction txAlias = tx;\n            RoutingParameterMutation.Mutate(txAlias);",
        "tx",
        TestName = "PayValue_must_not_pass_transaction_alias_by_value_to_an_unmodeled_call")]
    [TestCase(
        "IReleaseSpec specAlias = spec;\n            RoutingParameterMutation.Mutate(specAlias);",
        "spec",
        TestName = "PayValue_must_not_pass_specification_alias_by_value_to_an_unmodeled_call")]
    [TestCase("RoutingParameterMutation.Mutate((tx));", "tx", TestName = "PayValue_must_not_hide_transaction_in_parentheses")]
    [TestCase("RoutingParameterMutation.MutateObject(tx);", "tx", TestName = "PayValue_must_not_implicitly_convert_transaction_to_object")]
    [TestCase("RoutingParameterMutation.Mutate((object)tx);", "tx", TestName = "PayValue_must_not_hide_transaction_in_an_explicit_conversion")]
    [TestCase("RoutingParameterMutation.Mutate(true ? tx : new Transaction());", "tx", TestName = "PayValue_must_not_hide_transaction_in_a_conditional")]
    [TestCase("RoutingParameterMutation.Mutate(tx ?? new Transaction());", "tx", TestName = "PayValue_must_not_hide_transaction_in_a_coalesce")]
    [TestCase("RoutingParameterMutation.Mutate(tx as object);", "tx", TestName = "PayValue_must_not_hide_transaction_in_an_as_conversion")]
    [TestCase("RoutingParameterMutation.MutateSpecificationObject((object)spec);", "spec", TestName = "PayValue_must_not_hide_specification_in_an_explicit_conversion")]
    public void PayValue_must_not_pass_reference_arguments_by_value_to_an_unmodeled_call(
        string mutation,
        string parameter)
    {
        using Fixture fixture = new();
        fixture.ReplaceOccurrence(
            Extractor.TransactionProcessorPath,
            "            PayValue(tx, spec, opts);",
            $"            {mutation}\n            PayValue(tx, spec, opts);",
            occurrence: 0);
        fixture.Append(
            Extractor.TransactionProcessorPath,
            "\n\nnamespace Nethermind.Evm.TransactionProcessing\n" +
            "{\n" +
            "    internal static class RoutingParameterMutation\n" +
            "    {\n" +
            "        internal static void Mutate(Nethermind.Core.Transaction value) => value.SenderAddress = null;\n" +
            "        internal static void Mutate(Nethermind.Core.Specs.IReleaseSpec value) => ((MutableReleaseSpec)value).WasMutated = true;\n" +
            "        internal static void Mutate(object value) => ((Nethermind.Core.Transaction)value).SenderAddress = null;\n" +
            "        internal static void MutateObject(object value) => ((Nethermind.Core.Transaction)value).SenderAddress = null;\n" +
            "        internal static void MutateSpecificationObject(object value) => ((MutableReleaseSpec)value).WasMutated = true;\n" +
            "\n" +
            "        private sealed class MutableReleaseSpec : Nethermind.Core.Specs.IReleaseSpec\n" +
            "        {\n" +
            "            public bool WasMutated { get; set; }\n" +
            "        }\n" +
            "    }\n" +
            "}\n");

        AssertRejected(fixture, $"passes the admitted '{parameter}' reference by value to the unmodeled call");
    }

    [TestCase("RoutingBackEdgeMutation.Mutate(tx);", "tx", TestName = "EVM_PayValue_must_reject_a_later_transaction_handoff_that_loops_back")]
    [TestCase("RoutingBackEdgeMutation.Mutate(spec);", "spec", TestName = "EVM_PayValue_must_reject_a_later_specification_handoff_that_loops_back")]
    [TestCase(
        "Transaction txAlias = tx;\n            RoutingBackEdgeMutation.Mutate(txAlias);",
        "tx",
        TestName = "EVM_PayValue_must_reject_a_later_alias_handoff_that_loops_back")]
    [TestCase(
        "ref readonly Transaction txAlias = ref tx;\n            RoutingBackEdgeMutation.Mutate(txAlias);",
        "tx",
        TestName = "EVM_PayValue_must_reject_a_later_ref_readonly_alias_handoff_that_loops_back")]
    [TestCase(
        "bool selectAlias = true;\n            Transaction txAlias = selectAlias ? tx : tx;\n            RoutingBackEdgeMutation.Mutate(txAlias);",
        "tx",
        TestName = "EVM_PayValue_must_reject_a_later_conditional_alias_handoff_that_loops_back")]
    [TestCase("RoutingBackEdgeMutation.Mutate((object)tx);", "tx", TestName = "EVM_PayValue_must_reject_a_later_conversion_handoff_that_loops_back")]
    [TestCase("RoutingBackEdgeMutation.Mutate(tx!);", "tx", TestName = "EVM_PayValue_must_reject_a_later_null_suppressed_handoff_that_loops_back")]
    public void EVM_PayValue_must_reject_later_by_value_handoffs_that_can_loop_back(
        string mutation,
        string parameter)
    {
        using Fixture fixture = new();
        fixture.ReplaceOccurrence(
            Extractor.TransactionProcessorPath,
            "            PayValue(tx, spec, opts);",
            "            Retry:\n" +
            "            PayValue(tx, spec, opts);\n" +
            $"            {mutation}\n" +
            "            goto Retry;",
            occurrence: 0);
        fixture.Append(
            Extractor.TransactionProcessorPath,
            "\n\nnamespace Nethermind.Evm.TransactionProcessing\n" +
            "{\n" +
            "    internal static class RoutingBackEdgeMutation\n" +
            "    {\n" +
            "        internal static void Mutate(Transaction value) => value.SenderAddress = null;\n" +
            "        internal static void Mutate(IReleaseSpec value) { }\n" +
            "        internal static void Mutate(object value) => ((Transaction)value).SenderAddress = null;\n" +
            "        internal static void MutateExtension(this Transaction value) => value.SenderAddress = null;\n" +
            "    }\n" +
            "}\n");

        AssertRejected(fixture, $"passes the admitted '{parameter}' reference by value to the unmodeled call");
    }

    [Test]
    public void EVM_PayValue_allows_a_later_straight_line_handoff()
    {
        using Fixture fixture = new();
        fixture.ReplaceOccurrence(
            Extractor.TransactionProcessorPath,
            "            PayValue(tx, spec, opts);",
            "            PayValue(tx, spec, opts);\n" +
            "            RoutingBackEdgeMutation.Mutate(tx);",
            occurrence: 0);
        fixture.Append(
            Extractor.TransactionProcessorPath,
            "\n\nnamespace Nethermind.Evm.TransactionProcessing\n" +
            "{\n" +
            "    internal static class RoutingBackEdgeMutation\n" +
            "    {\n" +
            "        internal static void Mutate(Transaction value) => value.SenderAddress = null;\n" +
            "    }\n" +
            "}\n");

        Assert.DoesNotThrow(() => Extractor.Extract(fixture.Root, fixture.Output));
    }

    [TestCase("tx.ToString();", "passes the admitted 'tx' reference as the receiver to the unmodeled call", TestName = "EVM_PayValue_must_reject_a_later_instance_receiver_that_loops_back")]
    [TestCase("tx.MutateExtension();", "passes the admitted 'tx' reference as the receiver to the unmodeled call", TestName = "EVM_PayValue_must_reject_a_later_extension_receiver_that_loops_back")]
    [TestCase("Func<string> methodGroup = tx.ToString;", "captures the admitted 'tx' reference as the method group", TestName = "EVM_PayValue_must_reject_a_later_method_group_capture_that_loops_back")]
    public void EVM_PayValue_must_reject_later_receiver_escapes_that_can_loop_back(
        string mutation,
        string expectedMessage)
    {
        using Fixture fixture = new();
        fixture.ReplaceOccurrence(
            Extractor.TransactionProcessorPath,
            "            PayValue(tx, spec, opts);",
            "            Retry:\n" +
            "            PayValue(tx, spec, opts);\n" +
            $"            {mutation}\n" +
            "            goto Retry;",
            occurrence: 0);
        fixture.Append(
            Extractor.TransactionProcessorPath,
            "\n\nnamespace Nethermind.Evm.TransactionProcessing\n" +
            "{\n" +
            "    internal static class RoutingReceiverBackEdgeMutation\n" +
            "    {\n" +
            "        internal static void MutateExtension(this Transaction value) => value.SenderAddress = null;\n" +
            "    }\n" +
            "}\n");

        AssertRejected(fixture, expectedMessage);
    }

    [TestCase("opts = ExecutionOptions.None;", "changes the admitted 'opts' parameter before the call", TestName = "Counter_forwarding_must_reject_a_later_option_write_that_loops_back")]
    [TestCase("ref ExecutionOptions optsAlias = ref opts;\n            optsAlias = ExecutionOptions.None;", "changes the admitted 'opts' parameter before the call", TestName = "Counter_forwarding_must_reject_a_later_ref_alias_write_that_loops_back")]
    [TestCase("ref readonly ExecutionOptions optsAlias = ref opts;\n            RoutingCounterBackEdgeMutation.Observe(in optsAlias);", "contains an unanalyzed in-reference escape", TestName = "Counter_forwarding_must_reject_a_later_ref_readonly_alias_in_escape_that_loops_back")]
    [TestCase("RoutingCounterBackEdgeMutation.Rewrite(ref opts);", "changes the admitted 'opts' parameter before the call", TestName = "Counter_forwarding_must_reject_a_later_ref_write_that_loops_back")]
    [TestCase("RoutingCounterBackEdgeMutation.Clear(out opts);", "changes the admitted 'opts' parameter before the call", TestName = "Counter_forwarding_must_reject_a_later_out_write_that_loops_back")]
    [TestCase("RoutingCounterBackEdgeMutation.Observe(in opts);", "contains an unanalyzed in-reference escape", TestName = "Counter_forwarding_must_reject_a_later_in_reference_escape_that_loops_back")]
    public void Counter_forwarding_must_reject_later_parameter_operations_that_can_loop_back(
        string mutation,
        string expectedMessage)
    {
        using Fixture fixture = new();
        fixture.ReplaceOccurrence(
            Extractor.TransactionProcessorPath,
            "            UpdateHeaderGasUsedAndPayFees(tx, header, spec, tracer, opts, in substate, in spentGas, premiumPerGas, in opcodeGasPrice, blobBaseFee, statusCode);",
            "            Retry:\n" +
            "            UpdateHeaderGasUsedAndPayFees(tx, header, spec, tracer, opts, in substate, in spentGas, premiumPerGas, in opcodeGasPrice, blobBaseFee, statusCode);\n" +
            $"            {mutation}\n" +
            "            goto Retry;",
            occurrence: 0);
        fixture.Append(
            Extractor.TransactionProcessorPath,
            "\n\nnamespace Nethermind.Evm.TransactionProcessing\n" +
            "{\n" +
            "    internal static class RoutingCounterBackEdgeMutation\n" +
            "    {\n" +
            "        internal static void Rewrite(ref ExecutionOptions value) => value = ExecutionOptions.None;\n" +
            "        internal static void Clear(out ExecutionOptions value) => value = ExecutionOptions.None;\n" +
            "        internal static void Observe(in ExecutionOptions value) { }\n" +
            "    }\n" +
            "}\n");

        AssertRejected(fixture, expectedMessage);
    }

    [TestCase(
        "            if (SystemTransactionRoutingKernel.UseSystemProcessor(tx.IsSystem(), opts))",
        "            RoutingParameterMutation.Mutate((object)tx);\n" +
        "            if (SystemTransactionRoutingKernel.UseSystemProcessor(tx.IsSystem(), opts))",
        "complete reachable dispatch body",
        TestName = "Route_kernel_must_reject_a_hidden_transaction_by_value_mutation")]
    [TestCase(
        "            TransactionResult result = Execute(tx, tracer, opts);",
        "            RoutingParameterMutation.Mutate(tx);\n" +
        "            TransactionResult result = Execute(tx, tracer, opts);",
        "complete reachable dispatch body",
        TestName = "Normal_route_must_reject_a_transaction_by_value_mutation")]
    [TestCase(
        "                return GetOrCreateSystemTransactionProcessor().Execute(tx, tracer, opts);",
        "                RoutingParameterMutation.Mutate(tx);\n" +
        "                return GetOrCreateSystemTransactionProcessor().Execute(tx, tracer, opts);",
        "direct, else-free branch with one forwarding return",
        TestName = "System_route_must_reject_a_transaction_by_value_mutation")]
    public void Route_shape_must_reject_transaction_by_value_mutations(
        string original,
        string replacement,
        string expectedMessage)
    {
        using Fixture fixture = new();
        fixture.ReplaceOccurrence(Extractor.TransactionProcessorPath, original, replacement, occurrence: 0);
        fixture.Append(
            Extractor.TransactionProcessorPath,
            "\n\nnamespace Nethermind.Evm.TransactionProcessing\n" +
            "{\n" +
            "    internal static class RoutingParameterMutation\n" +
            "    {\n" +
            "        internal static void Mutate(Nethermind.Core.Transaction value) => value.SenderAddress = null;\n" +
            "        internal static void Mutate(object value) => ((Nethermind.Core.Transaction)value).SenderAddress = null;\n" +
            "    }\n" +
            "}\n");

        AssertRejected(fixture, expectedMessage);
    }

    [TestCase("tx = new Transaction();", "tx", TestName = "EVM_PayValue_must_not_reassign_transaction_before_the_call")]
    [TestCase("spec = GetSpec(header);", "spec", TestName = "EVM_PayValue_must_not_reassign_specification_before_the_call")]
    [TestCase("opts = ExecutionOptions.None;", "opts", TestName = "EVM_PayValue_must_not_reassign_options_before_the_call")]
    public void PayValue_must_not_change_admitted_parameters_before_the_call(string assignment, string parameter)
    {
        using Fixture fixture = new();
        fixture.ReplaceOccurrence(
            Extractor.TransactionProcessorPath,
            "            PayValue(tx, spec, opts);",
            $"            {assignment}\n            PayValue(tx, spec, opts);",
            occurrence: 0);

        AssertRejected(fixture, $"changes the admitted '{parameter}' parameter before the call");
    }

    [Test]
    public void PayValue_must_not_change_transaction_through_an_alias_before_the_call()
    {
        using Fixture fixture = new();
        fixture.ReplaceOccurrence(
            Extractor.TransactionProcessorPath,
            "            PayValue(tx, spec, opts);",
            "            Transaction txAlias = tx;\n" +
            "            txAlias.SenderAddress = null;\n" +
            "            PayValue(tx, spec, opts);",
            occurrence: 0);

        AssertRejected(fixture, "changes the admitted 'tx' parameter before the call");
    }

    [Test]
    public void Evm_PayValue_must_be_a_live_direct_call()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.TransactionProcessorPath,
            "            PayValue(tx, spec, opts);",
            "            if (tx is null)\n" +
            "            {\n" +
            "                PayValue(tx, spec, opts);\n" +
            "            }");

        AssertRejected(fixture, "EVM PayValue dispatch must be a reachable direct body statement");
    }

    [Test]
    [TestCase(
        "statusCode + (int)(((BlockHeader)(object)header).GasUsed = ((BlockHeader)(object)header).GasUsed + spentGas.EffectiveBlockGas)",
        TestName = "Counter_claim_excludes_virtual_PayFees_header_mutation")]
    [TestCase(
        "statusCode + (int)(header.GasUsed = header.GasUsed + spentGas.EffectiveBlockGas)",
        TestName = "Counter_claim_excludes_virtual_PayFees_direct_header_mutation")]
    public void Counter_claim_does_not_analyze_virtual_PayFees_effects(string statusExpression)
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.TransactionProcessorPath,
            "            PayFees(tx, header, spec, tracer, in substate, spentGas.SpentGas, premiumPerGas, in effectiveGasPrice, blobBaseFee, statusCode);",
            $"            PayFees(tx, header, spec, tracer, in substate, spentGas.SpentGas, premiumPerGas, in effectiveGasPrice, blobBaseFee, {statusExpression});");

        ExtractionResult result = Extractor.Extract(fixture.Root, fixture.Output);
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(result.ManifestPath));
        string[] bindings = manifest.RootElement.GetProperty("semanticBindings")
            .EnumerateArray()
            .Select(static binding => binding.GetString()!)
            .ToArray();
        Assert.That(bindings, Does.Contain(
            "UpdateHeaderGasUsedAndPayFees gate decision only -> ParticipatesInNormalBlockCounters; virtual PayFees effects are outside the claim"));
        Assert.That(bindings.Any(static binding => binding.Contains("counter write", StringComparison.OrdinalIgnoreCase)), Is.False);
    }

    [Test]
    public void Counter_claim_excludes_virtual_PayFees_body_mutation()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.TransactionProcessorPath,
            "            UInt256 fees = premiumPerGas * spentGas;",
            "            UInt256 fees = premiumPerGas * spentGas;\n" +
            "            header.GasUsed += 1;");

        ExtractionResult result = Extractor.Extract(fixture.Root, fixture.Output);
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(result.ManifestPath));
        string[] bindings = manifest.RootElement.GetProperty("semanticBindings")
            .EnumerateArray()
            .Select(static binding => binding.GetString()!)
            .ToArray();
        Assert.That(bindings, Does.Contain(
            "UpdateHeaderGasUsedAndPayFees gate decision only -> ParticipatesInNormalBlockCounters; virtual PayFees effects are outside the claim"));
    }

    [Test]
    public void ExecuteCore_route_must_not_hide_an_early_exit_before_forwarding()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.TransactionProcessorPath,
            "            if (SystemTransactionRoutingKernel.UseSystemProcessor(tx.IsSystem(), opts))\n" +
            "            {\n" +
            "                return GetOrCreateSystemTransactionProcessor().Execute(tx, tracer, opts);\n" +
            "            }",
            "            if (SystemTransactionRoutingKernel.UseSystemProcessor(tx.IsSystem(), opts))\n" +
            "            {\n" +
            "                if (tx.IsSystem())\n" +
            "                {\n" +
            "                    return TransactionResult.Ok;\n" +
            "                }\n" +
            "                return GetOrCreateSystemTransactionProcessor().Execute(tx, tracer, opts);\n" +
            "            }");

        AssertRejected(fixture, "direct, else-free branch with one forwarding return");
    }

    [Test]
    public void ExecuteCore_route_must_not_reassign_arguments_before_forwarding()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.TransactionProcessorPath,
            "            if (SystemTransactionRoutingKernel.UseSystemProcessor(tx.IsSystem(), opts))\n" +
            "            {\n" +
            "                return GetOrCreateSystemTransactionProcessor().Execute(tx, tracer, opts);\n" +
            "            }",
            "            if (SystemTransactionRoutingKernel.UseSystemProcessor(tx.IsSystem(), opts))\n" +
            "            {\n" +
            "                opts = ExecutionOptions.None;\n" +
            "                return GetOrCreateSystemTransactionProcessor().Execute(tx, tracer, opts);\n" +
            "            }");

        AssertRejected(fixture, "direct, else-free branch with one forwarding return");
    }

    [Test]
    public void System_execute_must_not_hide_an_early_exit_before_inherited_forwarding()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.SystemProcessorPath,
            "        return base.Execute(tx, tracer, SystemTransactionRoutingKernel.GetSystemExecutionOptions(opts, _payOriginalValue));",
            "        if (tx is null) return TransactionResult.Ok;\n" +
            "        return base.Execute(tx, tracer, SystemTransactionRoutingKernel.GetSystemExecutionOptions(opts, _payOriginalValue));");

        AssertRejected(fixture, "complete reachable override body");
    }

    [Test]
    public void System_execute_must_not_reassign_options_before_inherited_forwarding()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.SystemProcessorPath,
            "        return base.Execute(tx, tracer, SystemTransactionRoutingKernel.GetSystemExecutionOptions(opts, _payOriginalValue));",
            "        opts = ExecutionOptions.None;\n" +
            "        return base.Execute(tx, tracer, SystemTransactionRoutingKernel.GetSystemExecutionOptions(opts, _payOriginalValue));");

        AssertRejected(fixture, "complete reachable override body");
    }

    [TestCase("opts = ExecutionOptions.None;", TestName = "PayValue_late_local_function_must_not_reassign_options")]
    [TestCase("tx.SenderAddress = null;", TestName = "PayValue_late_local_function_must_not_mutate_transaction")]
    [TestCase("spec = null;", TestName = "PayValue_late_local_function_must_not_reassign_specification")]
    public void PayValue_must_reject_a_parameter_write_hidden_in_a_late_local_function(string mutation)
    {
        using Fixture fixture = new();
        fixture.ReplaceOccurrence(
            Extractor.TransactionProcessorPath,
            "            if (hasValueTransfer) PayValue(tx, spec, opts);",
            "            if (hasValueTransfer) Mutate();\n" +
            "            PayValue(tx, spec, opts);\n" +
            $"            void Mutate() {{ {mutation} }}",
            occurrence: 0);

        AssertRejected(fixture, "hides a write-capable local function");
    }

    [Test]
    public void PayValue_must_reject_an_alias_created_by_assignment_before_the_call()
    {
        using Fixture fixture = new();
        fixture.ReplaceOccurrence(
            Extractor.TransactionProcessorPath,
            "            if (hasValueTransfer) PayValue(tx, spec, opts);",
            "            Transaction txAlias;\n" +
            "            txAlias = tx;\n" +
            "            txAlias.SenderAddress = null;\n" +
            "            if (hasValueTransfer) PayValue(tx, spec, opts);",
            occurrence: 0);

        AssertRejected(fixture, "changes the admitted 'tx' parameter before the call");
    }

    [Test]
    public void PayValue_must_reject_an_alias_write_even_after_the_alias_is_reassigned()
    {
        using Fixture fixture = new();
        fixture.ReplaceOccurrence(
            Extractor.TransactionProcessorPath,
            "            if (hasValueTransfer) PayValue(tx, spec, opts);",
            "            Transaction txAlias = tx;\n" +
            "            txAlias = new Transaction();\n" +
            "            txAlias.SenderAddress = null;\n" +
            "            if (hasValueTransfer) PayValue(tx, spec, opts);",
            occurrence: 0);

        AssertRejected(fixture, "changes the admitted 'tx' parameter before the call");
    }

    [Test]
    public void PayValue_must_not_escape_an_admitted_parameter_through_a_field()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.TransactionProcessorPath,
            "        private ulong _blockCumulativeStateGas;",
            "        private ulong _blockCumulativeStateGas;\n" +
            "        private Transaction _routingAlias = null!;");
        fixture.ReplaceOccurrence(
            Extractor.TransactionProcessorPath,
            "            if (hasValueTransfer) PayValue(tx, spec, opts);",
            "            _routingAlias = tx;\n" +
            "            if (hasValueTransfer) PayValue(tx, spec, opts);",
            occurrence: 0);

        AssertRejected(fixture, "escape through a non-local alias");
    }

    [Test]
    public void PayValue_must_reject_a_ref_write_hidden_in_a_late_local_function()
    {
        using Fixture fixture = new();
        fixture.ReplaceOccurrence(
            Extractor.TransactionProcessorPath,
            "            if (hasValueTransfer) PayValue(tx, spec, opts);",
            "            if (hasValueTransfer) Mutate();\n" +
            "            PayValue(tx, spec, opts);\n" +
            "            void Mutate() { RoutingParameterMutation.Rewrite(ref opts); }",
            occurrence: 0);
        fixture.Append(
            Extractor.TransactionProcessorPath,
            "\n\nnamespace Nethermind.Evm.TransactionProcessing\n" +
            "{\n" +
            "    internal static class RoutingParameterMutation\n" +
            "    {\n" +
            "        internal static void Rewrite(ref ExecutionOptions value) => value = ExecutionOptions.None;\n" +
            "    }\n" +
            "}\n");

        AssertRejected(fixture, "hides a write-capable local function");
    }

    [Test]
    public void PayValue_must_reject_a_write_through_a_ref_alias_before_the_call()
    {
        using Fixture fixture = new();
        fixture.ReplaceOccurrence(
            Extractor.TransactionProcessorPath,
            "            if (hasValueTransfer) PayValue(tx, spec, opts);",
            "            ref ExecutionOptions optsAlias = ref opts;\n" +
            "            optsAlias = ExecutionOptions.None;\n" +
            "            if (hasValueTransfer) PayValue(tx, spec, opts);",
            occurrence: 0);

        AssertRejected(fixture, "changes the admitted 'opts' parameter before the call");
    }

    [Test]
    public void Mainnet_transaction_processor_registration_must_be_live_and_direct()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.MainnetDiPath,
            "    protected override void Load(ContainerBuilder builder)\n" +
            "    {\n" +
            "        builder",
            "    protected override void Load(ContainerBuilder builder)\n" +
            "    {\n" +
            "        if (false)\n" +
            "        {\n" +
            "            builder.AddScoped<ITransactionProcessor, EthereumTransactionProcessor>();\n" +
            "        }\n" +
            "\n" +
            "        builder");
        fixture.ReplaceOccurrence(
            Extractor.MainnetDiPath,
            ".AddScoped<ITransactionProcessor, EthereumTransactionProcessor>()",
            ".AddScoped<IBlockValidator, BlockValidator>()",
            occurrence: 1);

        AssertRejected(fixture, "must be a live direct body statement");
    }

    [Test]
    public void Mainnet_registration_must_not_follow_a_preceding_exit()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.MainnetDiPath,
            "    protected override void Load(ContainerBuilder builder)\n" +
            "    {\n" +
            "        builder",
            "    protected override void Load(ContainerBuilder builder)\n" +
            "    {\n" +
            "        return;\n\n" +
            "        builder");

        AssertRejected(fixture, "control-flow exit before the admitted ITransactionProcessor registration");
    }

    [Test]
    public void Mainnet_registration_must_not_follow_a_preceding_throw()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.MainnetDiPath,
            "        builder",
            "        if (blocksConfig.BuildBlocksOnMainState) throw new InvalidOperationException();\n\n        builder");

        AssertRejected(fixture, "control-flow exit before the admitted ITransactionProcessor registration");
    }

    [Test]
    public void Mainnet_registration_must_not_follow_a_conditional_preceding_exit()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.MainnetDiPath,
            "    protected override void Load(ContainerBuilder builder)\n" +
            "    {\n" +
            "        builder",
            "    protected override void Load(ContainerBuilder builder)\n" +
            "    {\n" +
            "        if (blocksConfig.BuildBlocksOnMainState)\n" +
            "        {\n" +
            "            return;\n" +
            "        }\n\n" +
            "        builder");

        AssertRejected(fixture, "control-flow exit before the admitted ITransactionProcessor registration");
    }

    [Test]
    public void Mainnet_registration_must_not_have_a_later_override()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.MainnetDiPath,
            "            .AddScoped<IGenesisLoader, GenesisLoader>()\n" +
            "            ;\n\n" +
            "        builder.AddSingleton<IMainStateBlockProducerEnvFactory, GlobalWorldStateBlockProducerEnvFactory>();",
            "            .AddScoped<IGenesisLoader, GenesisLoader>()\n" +
            "            ;\n\n" +
            "        builder.AddSingleton<ITransactionProcessor, EthereumTransactionProcessor>();\n\n" +
            "        builder.AddSingleton<IMainStateBlockProducerEnvFactory, GlobalWorldStateBlockProducerEnvFactory>();");

        AssertRejected(fixture, "exactly one ITransactionProcessor registration with no later override");
    }

    [Test]
    public void Mainnet_registration_must_not_have_a_later_bind_override()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.MainnetDiPath,
            "        builder.AddSingleton<IMainStateBlockProducerEnvFactory, GlobalWorldStateBlockProducerEnvFactory>();",
            "        builder.Bind<ITransactionProcessor, EthereumTransactionProcessor>();\n\n" +
            "        builder.AddSingleton<IMainStateBlockProducerEnvFactory, GlobalWorldStateBlockProducerEnvFactory>();");

        AssertRejected(fixture, "exactly one ITransactionProcessor registration with no later override");
    }

    [Test]
    public void Mainnet_load_must_not_defer_a_builder_registration_through_a_closure()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.MainnetDiPath,
            "        builder.AddSingleton<IMainStateBlockProducerEnvFactory, GlobalWorldStateBlockProducerEnvFactory>();",
            "        Action registerLater = () => builder.AddSingleton<IBlockValidator, BlockValidator>();\n\n" +
            "        builder.AddSingleton<IMainStateBlockProducerEnvFactory, GlobalWorldStateBlockProducerEnvFactory>();");

        AssertRejected(fixture, "capture builder in a deferred function");
    }

    [Test]
    public void Mainnet_load_must_not_escape_builder_through_an_alias()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.MainnetDiPath,
            "        builder.AddSingleton<IMainStateBlockProducerEnvFactory, GlobalWorldStateBlockProducerEnvFactory>();",
            "        ContainerBuilder registrationBuilder = builder;\n" +
            "        registrationBuilder.AddSingleton<IBlockValidator, BlockValidator>();\n\n" +
            "        builder.AddSingleton<IMainStateBlockProducerEnvFactory, GlobalWorldStateBlockProducerEnvFactory>();");

        AssertRejected(fixture, "every builder use in a direct receiver chain");
    }

    [Test]
    public void Mainnet_load_must_not_hide_an_unknown_builder_operation_after_registration()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.MainnetDiPath,
            "        builder.AddSingleton<IMainStateBlockProducerEnvFactory, GlobalWorldStateBlockProducerEnvFactory>();",
            "        builder.ConfigureRouting();\n\n" +
            "        builder.AddSingleton<IMainStateBlockProducerEnvFactory, GlobalWorldStateBlockProducerEnvFactory>();");
        fixture.Append(
            Extractor.MainnetDiPath,
            "\n\ninternal static class RoutingBuilderMutation\n" +
            "{\n" +
            "    internal static ContainerBuilder ConfigureRouting(this ContainerBuilder builder) => builder;\n" +
            "}\n");

        AssertRejected(fixture, "builder operation 'ConfigureRouting' is not an admitted source operation");
    }

    [Test]
    public void Mainnet_registration_receiver_must_use_only_admitted_builder_operations()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.MainnetDiPath,
            "        builder\n            // Validators",
            "        builder.PrepareRouting()\n            // Validators");
        fixture.Append(
            Extractor.MainnetDiPath,
            "\n\ninternal static class RoutingBuilderMutation\n" +
            "{\n" +
            "    internal static ContainerBuilder PrepareRouting(this ContainerBuilder builder) => builder;\n" +
            "}\n");

        AssertRejected(fixture, "DI receiver chain contains a non-admitted operation");
    }

    private static ExtractionException AssertRejected(Fixture fixture, string expectedMessage)
    {
        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => Extractor.Extract(fixture.Root, fixture.Output))!;
        Assert.That(exception.Message, Does.Contain(expectedMessage));
        return exception;
    }

    private static void AssertSerializedIrMutationRejected(
        byte[] sourceDerived,
        Action<JsonObject> mutation,
        string expectedMessage)
    {
        JsonObject candidate = ParseSerializedIr(sourceDerived);
        mutation(candidate);
        AssertSerializedIrRejected(sourceDerived, SerializeSerializedIr(candidate), expectedMessage);
    }

    private static void AssertSerializedIrRejected(byte[] sourceDerived, byte[] candidate, string expectedMessage)
    {
        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => Extractor.ValidateSerializedIr(sourceDerived, candidate))!;
        Assert.That(exception.Message, Does.Contain(expectedMessage));
    }

    private static void AssertSerializedManifestRejected(byte[] sourceDerived, byte[] candidate, string expectedMessage)
    {
        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => Extractor.ValidateSerializedManifest(sourceDerived, candidate))!;
        Assert.That(exception.Message, Does.Contain(expectedMessage));
    }

    private static byte[] ReadCheckedIr() => Encoding.UTF8.GetBytes(ReadProductionFile(
        "tools/Evm/Lean/SystemTransactionRoutingExtractor/Generated/SystemTransactionRoutingKernel.ir.json"));

    private static byte[] ReadCheckedManifest() => Encoding.UTF8.GetBytes(ReadProductionFile(
        "tools/Evm/Lean/SystemTransactionRoutingExtractor/Generated/SystemTransactionRoutingKernel.source-manifest.json"));

    private static JsonObject ParseSerializedIr(byte[] bytes) => JsonNode.Parse(Encoding.UTF8.GetString(bytes))!.AsObject();

    private static byte[] SerializeSerializedIr(JsonObject ir) => Encoding.UTF8.GetBytes(ir.ToJsonString(new JsonSerializerOptions
    {
        WriteIndented = true,
    }));

    private static void Rename(JsonObject value, string from, string to)
    {
        value[to] = value[from]!.DeepClone();
        value.Remove(from);
    }

    private static void MutateScalar(JsonObject value, string property)
    {
        JsonValue scalar = value[property]!.AsValue();
        if (scalar.TryGetValue<bool>(out bool boolean)) value[property] = !boolean;
        else if (scalar.TryGetValue<int>(out int integer)) value[property] = integer + 1;
        else value[property] = scalar.GetValue<string>() + "#";
    }

    private static string ReadProductionFile(string relativePath)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not locate a pinned system routing source.", relativePath);
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly string[] SourcePaths =
        [
            Extractor.KernelPath,
            Extractor.OptionsPath,
            Extractor.TransactionProcessorPath,
            Extractor.SystemProcessorPath,
            Extractor.TransactionExtensionsPath,
            Extractor.MainnetDiPath,
            Extractor.ContainerRegistrationPath,
        ];

        public Fixture()
        {
            Root = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "system-transaction-routing-extractor-fixtures",
                Guid.NewGuid().ToString("N"));
            Output = Path.Combine(Root, "generated");
            foreach (string relativePath in SourcePaths)
            {
                Write(relativePath, ReadProductionFile(relativePath));
            }
        }

        public string Root { get; }

        public string Output { get; }

        public void Replace(string relativePath, string original, string replacement)
        {
            string source = Read(relativePath);
            Assert.That(source, Does.Contain(original));
            Write(relativePath, source.Replace(original, replacement, StringComparison.Ordinal));
        }

        public void ReplaceOccurrence(string relativePath, string original, string replacement, int occurrence)
        {
            string source = Read(relativePath);
            int start = -1;
            for (int index = 0; index <= occurrence; index++)
            {
                start = source.IndexOf(original, start + 1, StringComparison.Ordinal);
                Assert.That(start, Is.GreaterThanOrEqualTo(0));
            }
            Write(relativePath, source.Remove(start, original.Length).Insert(start, replacement));
        }

        public void Append(string relativePath, string suffix) =>
            Write(relativePath, Read(relativePath) + suffix);

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private string Read(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath));

        private void Write(string relativePath, string source)
        {
            string path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }
}
