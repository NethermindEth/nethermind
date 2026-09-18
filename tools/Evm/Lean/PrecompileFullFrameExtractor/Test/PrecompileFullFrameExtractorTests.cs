// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using NJsonSchema;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.PrecompileFullFrameExtractor.Test;

[TestFixture, NonParallelizable]
public sealed class PrecompileFullFrameExtractorTests
{
    private const string Vm = "src/Nethermind/Nethermind.Evm/VirtualMachine.cs";
    private string _root = null!;
    private string _scratch = null!;
    private Artifacts _artifacts = null!;

    [OneTimeSetUp]
    public void SetUp()
    {
        _root = FindRoot();
        _scratch = Directory.CreateTempSubdirectory("precompile-full-frame-tests-").FullName;
        HashSet<string> paths = new(StringComparer.Ordinal) { Profile.Package + "Admission.json" };
        using JsonDocument pins = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(_root, Profile.Package + "Admission.json")));
        foreach (JsonElement file in pins.RootElement.GetProperty("files").EnumerateArray()) paths.Add(file.GetProperty("path").GetString()!);
        using JsonDocument stageA = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(_root,
            "tools/Evm/Lean/PrecompileFrameExtractor/Generated/precompile-frame-stage-a.source-manifest.json")));
        foreach (string collection in new[] { "sources", "dependencies" })
            foreach (JsonElement file in stageA.RootElement.GetProperty(collection).EnumerateArray()) paths.Add(file.GetProperty("path").GetString()!);
        foreach (string path in paths)
        {
            string target = Path.Combine(_scratch, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(_root, path), target);
        }
        _artifacts = Profile.Build(_scratch);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        if (_scratch is null) return;
        string path = Path.GetFullPath(_scratch);
        if (!path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith("precompile-full-frame-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing cleanup outside the owned test directory.");
        Directory.Delete(path, recursive: true);
    }

    [Test]
    public void Current_source_and_artifacts_are_exact()
    {
        Profile.Validate(Path.Combine(_root, Profile.Package, "Generated"), _artifacts);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_artifacts.Ir.Route, Has.Length.EqualTo(5));
            Assert.That(_artifacts.Ir.Sources, Has.Length.EqualTo(10));
            Assert.That(_artifacts.Ir.Members, Has.Length.EqualTo(48));
            Assert.That(_artifacts.Ir.Dependencies, Has.Length.EqualTo(53));
            Assert.That(_artifacts.Ir.Oracles, Has.Length.EqualTo(18));
            Assert.That(_artifacts.Ir.Branches, Has.Length.EqualTo(10));
            Assert.That(_artifacts.Ir.Assumptions, Has.Length.EqualTo(7));
            Assert.That(_artifacts.Ir.Exclusions, Has.Length.EqualTo(9));
            Assert.That(_artifacts.Ir.ActionAddress, Is.EqualTo("VmState.To = Env.CodeSource ?? Env.ExecutingAccount"));
            Assert.That(_artifacts.Ir.BalanceAddress, Is.EqualTo("Env.ExecutingAccount"));
            Assert.That(_artifacts.Manifest.Sources, Has.Length.EqualTo(10));
            Assert.That(_artifacts.Manifest.Members, Has.Length.EqualTo(48));
            Assert.That(_artifacts.Manifest.Dependencies, Has.Length.EqualTo(53));
            Assert.That(_artifacts.Manifest.Oracles, Has.Length.EqualTo(18));
        }

        string types = File.ReadAllText(Path.Combine(_root, Profile.Package, "Specification/Types.lean"));
        string refinement = File.ReadAllText(Path.Combine(_root, Profile.Package, "Refinement/PrecompileFullFrame.lean"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(types, Does.Contain("def Entry.Admitted : Entry → Prop"));
            Assert.That(refinement, Does.Contain("theorem generated_execute_is_raw_admitted"));
            Assert.That(refinement, Does.Contain("(admitted : entry.Admitted)"));
            Assert.That(refinement, Does.Contain("(frontParents : FrontPreservesParents front)"));
            Assert.That(refinement, Does.Not.Contain("(domain : ∀ raw"));
        }
    }

    [Test]
    public void Every_pinned_file_rejects_insertion([ValueSource(nameof(PinnedPaths))] string relative) =>
        Mutate(relative, static text => text + "\n ", () => Assert.Throws<ExtractionException>(() => Profile.Build(_scratch)));

    [TestCaseSource(nameof(SemanticMutations))]
    public void Semantic_mutations_fail_closed(string path, string before, string after) =>
        Mutate(path, source =>
        {
            Assert.That(source, Does.Contain(before), "Mutation must reach the intended current production branch.");
            return source.Replace(before, after, StringComparison.Ordinal);
        }, () => Assert.Throws<ExtractionException>(() => Profile.Build(_scratch)));

    [Test]
    public void Invalid_admission_does_not_repin_sources() =>
        Mutate(Profile.Package + "Admission.json", static text => text.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal) + " ",
            () => Assert.Throws<ExtractionException>(() => Profile.Build(_scratch)));

    [Test]
    public void Noncanonical_or_escaping_paths_fail([Values("../VirtualMachine.cs", "/tmp/example", "src\\example.cs", "SRC/Nethermind")] string path) =>
        Assert.Throws<ExtractionException>(() => Profile.Resolve(_scratch, path));

    [Test]
    public void Deterministic_emissions_are_byte_identical()
    {
        Artifacts again = Profile.Build(_scratch);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(again.IrBytes, Is.EqualTo(_artifacts.IrBytes));
            Assert.That(again.ManifestBytes, Is.EqualTo(_artifacts.ManifestBytes));
            Assert.That(again.LeanBytes, Is.EqualTo(_artifacts.LeanBytes));
        }
    }

    [Test]
    public void Artifact_tampering_fails([Values(Profile.IrName, Profile.ManifestName, Profile.LeanName)] string name)
    {
        string directory = Path.Combine(_scratch, "artifacts");
        Profile.Write(directory, _artifacts);
        File.AppendAllText(Path.Combine(directory, name), " ");
        Assert.Throws<ExtractionException>(() => Profile.Validate(directory, _artifacts));
    }

    [Test]
    public async Task Artifacts_obey_strict_schemas([Values(false, true)] bool manifest)
    {
        string path = Path.Combine(_root, Profile.Package, "Schema", manifest ? "source-manifest.schema.json" : "ir.schema.json");
        JsonSchema schema = await JsonSchema.FromFileAsync(path);
        string json = Encoding.UTF8.GetString(manifest ? _artifacts.ManifestBytes : _artifacts.IrBytes);
        Assert.That(schema.Validate(json), Is.Empty);
        string unknown = json.Insert(1, "\"unexpected\":true,");
        Assert.That(schema.Validate(unknown), Is.Not.Empty);
    }

    [Test]
    public void Failure_and_success_effect_order_is_exact()
    {
        Branch nested = _artifacts.Ir.Branches.Single(static branch => branch.Kind == BranchKind.NestedSuccess);
        Branch soft = _artifacts.Ir.Branches.Single(static branch => branch.Kind == BranchKind.ManagedNestedFailure);
        Branch[] pricing = _artifacts.Ir.Branches.Where(static branch => branch.Kind is BranchKind.PricingOverflow or BranchKind.PricingOutOfGas).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(nested.NestedSettlement, Is.EqualTo(new[] { Effect.IncorporateAdvancedRefund, Effect.RefundChildGas,
                Effect.HandleRegularReturn, Effect.CommitToParent, Effect.RepayStateGasSpill, Effect.ParentResume }));
            Assert.That(soft.RawControlRoute, Is.EqualTo("nestedPrecompileSoftFailure"));
            Assert.That(soft.Prefix, Does.Not.Contain(Effect.ClearExecutionGas));
            Assert.That(soft.NestedSettlement.Count(static effect => effect == Effect.ClearExecutionGas), Is.EqualTo(1));
            Assert.That(pricing, Has.All.Matches<Branch>(static branch => !branch.InstallsGas && !branch.RunsOracle));
            Assert.That(_artifacts.Ir.Branches, Has.None.Matches<Branch>(static branch => branch.RawControlRoute.Contains("handleException", StringComparison.Ordinal)));
        }
    }

    private void Mutate(string relative, Func<string, string> transform, Action assertion)
    {
        string path = Path.Combine(_scratch, relative);
        byte[] original = File.ReadAllBytes(path);
        try
        {
            string changed = transform(Encoding.UTF8.GetString(original));
            Assert.That(changed, Is.Not.EqualTo(Encoding.UTF8.GetString(original)));
            File.WriteAllText(path, changed, new UTF8Encoding(false));
            assertion();
        }
        finally { File.WriteAllBytes(path, original); }
    }

    private static IEnumerable<string> PinnedPaths()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(FindRoot(), Profile.Package + "Admission.json")));
        foreach (JsonElement file in document.RootElement.GetProperty("files").EnumerateArray()) yield return file.GetProperty("path").GetString()!;
    }

    private static IEnumerable<TestCaseData> SemanticMutations()
    {
        (string Before, string After)[] vm =
        [
            ("currentState.To,", "currentState.Env.ExecutingAccount,"),
            ("AddToBalanceAndCreateIfNotExists(state.Env.ExecutingAccount,", "AddToBalanceAndCreateIfNotExists(state.To,"),
            ("if (!wasCreated &&", "if (wasCreated &&"),
            ("transferValue.IsZero &&", "!transferValue.IsZero &&"),
            ("state.Env.ExecutingAccount.Equals(Ripemd160Address)", "state.To.Equals(Ripemd160Address)"),
            ("_shouldRestoreRipemdTouch = true;", "_shouldRestoreRipemdTouch = false;"),
            ("if (!TGasPolicy.TryConsumePrecompileGas(ref gas,", "if (TGasPolicy.TryConsumePrecompileGas(ref gas,"),
            ("state.Gas = gas;", "state.Gas = default;"),
            ("return ExecutePrecompileCall(state, precompile, callData, spec);", "state.Gas = default; return ExecutePrecompileCall(state, precompile, callData, spec);"),
            ("exceptionType: !success ? EvmExceptionType.PrecompileFailure : EvmExceptionType.None", "exceptionType: EvmExceptionType.None"),
            ("failure = PrecompileOutOfGasException;", "failure = VirtualMachineStatics.PrecompileExecutionFailureException;"),
            ("if (currentState.IsPrecompile && currentState.IsTopLevel)", "if (currentState.IsPrecompile && !currentState.IsTopLevel)"),
            ("TGasPolicy.ClearExecutionGas(ref currentState.Gas);", "TGasPolicy.ClearExecutionGas(ref currentState.Gas); TGasPolicy.ClearExecutionGas(ref currentState.Gas);"),
            ("Environment.Exit(ExitCodes.MissingPrecompile);", "return default;"),
            ("exception is DllNotFoundException or { InnerException: DllNotFoundException }", "exception is OverflowException"),
            ("catch (Exception exception)\n", "catch (OverflowException exception)\n"),
            ("if (!callResult.ShouldRevert)", "if (callResult.ShouldRevert)"),
            ("IncorporateChildStateGasRefunds(previousState);", "RemoveAdvancedStateGasRefund(previousState, ref previousState.Gas);"),
            ("TGasPolicy.Refund(ref _currentState.Gas, in previousState.Gas);", "TGasPolicy.RestoreChildStateGasOnHalt(ref _currentState.Gas, in previousState.Gas);"),
            ("previousState.CommitToParent(_currentState);", "TGasPolicy.RepayStateGasSpill(ref _currentState.Gas); previousState.CommitToParent(_currentState);"),
            ("TraceTransactionActionEnd(_currentState, callResult);", "PrepareTopLevelSubstate(in callResult); TraceTransactionActionEnd(_currentState, callResult);"),
            ("ex is EvmException or OverflowException", "ex is EvmException"),
            ("txTracer.ReportOperationRemainingGas(0);", "txTracer.ReportOperationRemainingGas(1);"),
            ("RestoreChildStateGasOnHalt(ref _currentState.Gas, in childState.Gas)", "RestoreChildStateGas(ref _currentState.Gas, in childState.Gas)"),
            ("_previousCallResult = (null, callResult.PrecompileSuccess != false);", "_previousCallResult = (null, true);"),
        ];
        int index = 0;
        foreach ((string before, string after) in vm)
            yield return new TestCaseData(Vm, before, after).SetName($"Semantic_mutation_{++index:D2}");
        yield return new TestCaseData("src/Nethermind/Nethermind.Evm/VmState.cs", "Env.CodeSource ?? Env.ExecutingAccount", "Env.ExecutingAccount").SetName("Action_address_fallback_mutation");
        yield return new TestCaseData("src/Nethermind/Nethermind.Evm/GasPolicy/PrecompileGasPricingKernel.cs", "baseGasCost > ulong.MaxValue - dataGasCost", "baseGasCost >= ulong.MaxValue - dataGasCost").SetName("Pricing_overflow_boundary_mutation");
        yield return new TestCaseData("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "gas.Value = 0", "gas.Value = 1").SetName("Execution_gas_clear_mutation");
    }

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, Profile.Package, "PrecompileFullFrameExtractor.csproj"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Cannot locate the Stage C repository root.");
    }
}
