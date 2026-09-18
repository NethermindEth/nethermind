// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.EvmTransactionPreparationExtractor.Test;

[TestFixture]
public sealed class ExtractorTests
{
    private const string AdmissionClosurePath = "tools/Evm/Lean/EvmTransactionPreparationExtractor/Admission/ProductionClosure.txt";
    private const string OrdinaryGeneratedPath = "tools/Evm/Lean/OrdinaryPostNonceDispatchExtractor/Generated/OrdinaryPostNonceDispatch.lean";
    private const string OrdinaryReferencePath = "tools/Evm/Lean/OrdinaryPostNonceDispatchExtractor/Reference/OrdinaryPostNonceDispatchReference.lean";
    private const string OrdinaryManifestPath = "tools/Evm/Lean/OrdinaryPostNonceDispatchExtractor/Generated/OrdinaryPostNonceDispatch.source-manifest.json";
    private const string ZeroGasFields = "{ value := 0, stateReservoir := 0, stateGasUsed := 0, stateGasSpill := 0, stateGasSpillRefunded := 0 }";
    private static string RepositoryRoot => FindRepositoryRoot();

    [Test]
    public void OrdinarySemanticImportDriftOrMissingFailsClosed(
        [Values(OrdinaryGeneratedPath, OrdinaryReferencePath)] string importPath,
        [Values] bool missing)
    {
        string root = CreateTempDirectory();
        string output = Path.Combine(root, "output");
        try
        {
            CopyAdmittedInputs(root);
            string target = Path.Combine(root, importPath);
            if (missing)
            {
                File.Delete(target);
            }
            else
            {
                string text = File.ReadAllText(target);
                Assert.That(text.Split(ZeroGasFields, StringSplitOptions.None), Has.Length.EqualTo(2));
                File.WriteAllText(target, text.Replace(ZeroGasFields, ZeroGasFields.Replace("value := 0", "value := 1", StringComparison.Ordinal), StringComparison.Ordinal));
            }

            ExtractionException? error = Assert.Throws<ExtractionException>(() => Extractor.Extract(root, output, Path.Combine(output, "EvmTransactionPreparation.lean")));
            Assert.That(error!.Message, Does.StartWith(missing
                ? $"Missing admitted dependency: {importPath}."
                : $"Dependency byte drift for {importPath}:"));
            Assert.That(Directory.Exists(output), Is.False);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Test]
    public void OrdinaryManifestMustIdentifyTheImportedLean([Values("path", "sha256", "missing")] string mutation)
    {
        JsonObject manifest = JsonNode.Parse(File.ReadAllBytes(Path.Combine(RepositoryRoot, OrdinaryManifestPath)))!.AsObject();
        JsonObject lean = manifest["lean"]!.AsObject();
        ArtifactIdentity actual = new(OrdinaryGeneratedPath, lean["sha256"]!.GetValue<string>());
        if (mutation == "missing") manifest.Remove("lean");
        else lean[mutation] = mutation == "path" ? "Generated/Different.lean" : new string('0', 64);
        ExtractionException? error = Assert.Throws<ExtractionException>(() =>
            Extractor.ValidateOrdinaryPostNonceManifest(Encoding.UTF8.GetBytes(manifest.ToJsonString()), actual));
        Assert.That(error!.Message, Is.EqualTo("The ordinary post-nonce manifest does not identify the exact imported generated Lean file."));
    }

    [Test]
    public void ProductionSignedArithmeticResidualCannotBeRemoved()
    {
        IrDocument document = Extractor.LoadIrForTest(Path.Combine(RepositoryRoot,
            "tools/Evm/Lean/EvmTransactionPreparationExtractor/Generated/EvmTransactionPreparation.ir.json"));
        ExtractionException? error = Assert.Throws<ExtractionException>(() =>
            Extractor.ValidateIrForTest(document with { Exclusions = [] }));
        Assert.That(error!.Message, Is.EqualTo("The production signed-state-gas arithmetic residual must remain explicit."));
    }

    [Test]
    public void SourceAdmissionIsDeterministic()
    {
        string first = CreateTempDirectory();
        string second = CreateTempDirectory();
        try
        {
            ExtractionResult left = Extractor.BuildForTest(RepositoryRoot, first, new Dictionary<string, byte[]>());
            ExtractionResult right = Extractor.BuildForTest(RepositoryRoot, second, new Dictionary<string, byte[]>());
            Assert.That(File.ReadAllBytes(left.IrPath), Is.EqualTo(File.ReadAllBytes(right.IrPath)));
            Assert.That(File.ReadAllBytes(left.ManifestPath), Is.EqualTo(File.ReadAllBytes(right.ManifestPath)));
            Assert.That(File.ReadAllBytes(left.LeanPath), Is.EqualTo(File.ReadAllBytes(right.LeanPath)));
        }
        finally
        {
            DeleteTempDirectory(first);
            DeleteTempDirectory(second);
        }
    }

    [TestCase("prePreparationGas = gasAvailable", "prePreparationGas = gasAvailable + TGasPolicy.FromULong(1)")]
    [TestCase("accessTracker.WarmUp(delegationAddress);", "accessTracker.WarmUp(recipient);")]
    public void SemanticSourceMutationFailsClosed(string original, string mutated)
    {
        string sourcePath = Path.Combine(RepositoryRoot, Extractor.TransactionProcessorPath.Replace('/', Path.DirectorySeparatorChar));
        byte[] source = File.ReadAllBytes(sourcePath);
        string text = Encoding.UTF8.GetString(source);
        Assert.That(text.IndexOf(original, StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
        byte[] changed = Encoding.UTF8.GetBytes(text.Replace(original, mutated, StringComparison.Ordinal));
        string output = CreateTempDirectory();
        try
        {
            Assert.Throws<ExtractionException>(() => Extractor.BuildForTest(RepositoryRoot, output,
                new Dictionary<string, byte[]> { [Extractor.TransactionProcessorPath] = changed }));
            Assert.That(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories), Is.Empty);
        }
        finally
        {
            DeleteTempDirectory(output);
        }
    }

    [Test]
    public void LegacyDelegatedTargetWarmUpOutsidePresenceTrueBranchFailsClosed()
    {
        string sourcePath = Path.Combine(RepositoryRoot, Extractor.TransactionProcessorPath.Replace('/', Path.DirectorySeparatorChar));
        string text = Encoding.UTF8.GetString(File.ReadAllBytes(sourcePath));
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string original =
            $"                if (delegationAddress is not null){newline}" +
            $"                {{{newline}" +
            "                    if (spec.IsEip8037Enabled)";
        string mutated =
            $"                if (delegationAddress is not null){newline}" +
            $"                {{{newline}" +
            $"                }}{newline}" +
            $"                else{newline}" +
            $"                {{{newline}" +
            "                    if (spec.IsEip8037Enabled)";
        Assert.That(text.IndexOf(original, StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
        byte[] changed = Encoding.UTF8.GetBytes(text.Replace(original, mutated, StringComparison.Ordinal));
        string output = CreateTempDirectory();
        try
        {
            ExtractionException? error = Assert.Throws<ExtractionException>(() => Extractor.BuildForTest(
                RepositoryRoot,
                output,
                new Dictionary<string, byte[]> { [Extractor.TransactionProcessorPath] = changed }));
            Assert.That(error!.Message, Is.EqualTo(
                "Delegated-target WarmUp must retain the exact legacy fork branch and target-presence guard."));
            Assert.That(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories), Is.Empty);
        }
        finally
        {
            DeleteTempDirectory(output);
        }
    }

    [Test]
    public void AddedPreparationBranchFailsClosed()
    {
        string sourcePath = Path.Combine(RepositoryRoot, Extractor.TransactionProcessorPath.Replace('/', Path.DirectorySeparatorChar));
        string text = Encoding.UTF8.GetString(File.ReadAllBytes(sourcePath));
        string marker = "bool topFrameOutOfGas = false;";
        string insertion = marker + Environment.NewLine + "            if (topFrameOutOfGas) { topFrameOutOfGas = false; }";
        Assert.That(text.IndexOf(marker, StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
        byte[] changed = Encoding.UTF8.GetBytes(text.Replace(marker, insertion, StringComparison.Ordinal));
        string output = CreateTempDirectory();
        try
        {
            Assert.Throws<ExtractionException>(() => Extractor.BuildForTest(RepositoryRoot, output,
                new Dictionary<string, byte[]> { [Extractor.TransactionProcessorPath] = changed }));
            Assert.That(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories), Is.Empty);
        }
        finally
        {
            DeleteTempDirectory(output);
        }
    }

    [Test]
    public void VirtualMachineBoundarySignatureMutationFailsClosed()
    {
        string sourcePath = Path.Combine(RepositoryRoot, Extractor.VirtualMachinePath.Replace('/', Path.DirectorySeparatorChar));
        string text = Encoding.UTF8.GetString(File.ReadAllBytes(sourcePath));
        string original = "        ITxTracer txTracer)";
        string mutated = "        ITxTracer alteredTracer)";
        Assert.That(text.IndexOf(original, StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
        byte[] changed = Encoding.UTF8.GetBytes(text.Replace(original, mutated, StringComparison.Ordinal));
        string output = CreateTempDirectory();
        try
        {
            Assert.Throws<ExtractionException>(() => Extractor.BuildForTest(RepositoryRoot, output,
                new Dictionary<string, byte[]> { [Extractor.VirtualMachinePath] = changed }));
            Assert.That(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories), Is.Empty);
        }
        finally
        {
            DeleteTempDirectory(output);
        }
    }

    [Test]
    public void AmsterdamForkActivationMutationFailsClosed()
    {
        string sourcePath = Path.Combine(RepositoryRoot, Extractor.AmsterdamForkPath.Replace('/', Path.DirectorySeparatorChar));
        string text = Encoding.UTF8.GetString(File.ReadAllBytes(sourcePath));
        string original = "spec.IsEip8037Enabled = true;";
        string mutated = "spec.IsEip8037Enabled = false;";
        Assert.That(text.IndexOf(original, StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
        byte[] changed = Encoding.UTF8.GetBytes(text.Replace(original, mutated, StringComparison.Ordinal));
        string output = CreateTempDirectory();
        try
        {
            Assert.Throws<ExtractionException>(() => Extractor.BuildForTest(RepositoryRoot, output,
                new Dictionary<string, byte[]> { [Extractor.AmsterdamForkPath] = changed }));
            Assert.That(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories), Is.Empty);
        }
        finally
        {
            DeleteTempDirectory(output);
        }
    }

    [Test]
    public void SourceEntryRefundInitializationMutationFailsClosed()
    {
        string sourcePath = Path.Combine(RepositoryRoot, Extractor.TransactionProcessorPath.Replace('/', Path.DirectorySeparatorChar));
        string text = Encoding.UTF8.GetString(File.ReadAllBytes(sourcePath));
        string original = "long delegationRefunds = 0;";
        string mutated = "long delegationRefunds = 1;";
        Assert.That(text.IndexOf(original, StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
        byte[] changed = Encoding.UTF8.GetBytes(text.Replace(original, mutated, StringComparison.Ordinal));
        string output = CreateTempDirectory();
        try
        {
            Assert.Throws<ExtractionException>(() => Extractor.BuildForTest(RepositoryRoot, output,
                new Dictionary<string, byte[]> { [Extractor.TransactionProcessorPath] = changed }));
            Assert.That(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories), Is.Empty);
        }
        finally
        {
            DeleteTempDirectory(output);
        }
    }

    [Test]
    public void SourceEntryTracingInitializationMutationFailsClosed()
    {
        string sourcePath = Path.Combine(RepositoryRoot, Extractor.TransactionProcessorPath.Replace('/', Path.DirectorySeparatorChar));
        string text = Encoding.UTF8.GetString(File.ReadAllBytes(sourcePath));
        string original = "using StackAccessTracker accessTracker = new(tracer.IsTracingAccess);";
        string mutated = "using StackAccessTracker accessTracker = new(false);";
        Assert.That(text.IndexOf(original, StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
        byte[] changed = Encoding.UTF8.GetBytes(text.Replace(original, mutated, StringComparison.Ordinal));
        string output = CreateTempDirectory();
        try
        {
            Assert.Throws<ExtractionException>(() => Extractor.BuildForTest(RepositoryRoot, output,
                new Dictionary<string, byte[]> { [Extractor.TransactionProcessorPath] = changed }));
            Assert.That(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories), Is.Empty);
        }
        finally
        {
            DeleteTempDirectory(output);
        }
    }

    [Test]
    public void SourceEntryStrayPrewarmedTargetMutationFailsClosed()
    {
        string sourcePath = Path.Combine(RepositoryRoot, Extractor.TransactionProcessorPath.Replace('/', Path.DirectorySeparatorChar));
        string text = Encoding.UTF8.GetString(File.ReadAllBytes(sourcePath));
        string original = "using StackAccessTracker accessTracker = new(tracer.IsTracingAccess);";
        string mutated = original + Environment.NewLine + "            accessTracker.WarmUp(tx.To!);";
        Assert.That(text.IndexOf(original, StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
        byte[] changed = Encoding.UTF8.GetBytes(text.Replace(original, mutated, StringComparison.Ordinal));
        string output = CreateTempDirectory();
        try
        {
            Assert.Throws<ExtractionException>(() => Extractor.BuildForTest(RepositoryRoot, output,
                new Dictionary<string, byte[]> { [Extractor.TransactionProcessorPath] = changed }));
            Assert.That(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories), Is.Empty);
        }
        finally
        {
            DeleteTempDirectory(output);
        }
    }

    [Test]
    public void SnapshotEmptyPositionMutationFailsClosed()
    {
        string sourcePath = Path.Combine(RepositoryRoot, Extractor.SnapshotPath.Replace('/', Path.DirectorySeparatorChar));
        string text = Encoding.UTF8.GetString(File.ReadAllBytes(sourcePath));
        string original = "public const int EmptyPosition = -1;";
        string mutated = "public const int EmptyPosition = 0;";
        Assert.That(text.IndexOf(original, StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
        byte[] changed = Encoding.UTF8.GetBytes(text.Replace(original, mutated, StringComparison.Ordinal));
        string output = CreateTempDirectory();
        try
        {
            Assert.Throws<ExtractionException>(() => Extractor.BuildForTest(RepositoryRoot, output,
                new Dictionary<string, byte[]> { [Extractor.SnapshotPath] = changed }));
            Assert.That(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories), Is.Empty);
        }
        finally
        {
            DeleteTempDirectory(output);
        }
    }

    [Test]
    public void SnapshotPresenceGuardMutationFailsClosed()
    {
        string sourcePath = Path.Combine(RepositoryRoot, Extractor.TransactionProcessorPath.Replace('/', Path.DirectorySeparatorChar));
        string text = Encoding.UTF8.GetString(File.ReadAllBytes(sourcePath));
        string original = "if (spec.IsEip7702Enabled && tx.HasAuthorizationList)";
        string mutated = "if (spec.IsEip7702Enabled || tx.HasAuthorizationList)";
        Assert.That(text.IndexOf(original, StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
        byte[] changed = Encoding.UTF8.GetBytes(text.Replace(original, mutated, StringComparison.Ordinal));
        string output = CreateTempDirectory();
        try
        {
            Assert.Throws<ExtractionException>(() => Extractor.BuildForTest(RepositoryRoot, output,
                new Dictionary<string, byte[]> { [Extractor.TransactionProcessorPath] = changed }));
            Assert.That(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories), Is.Empty);
        }
        finally
        {
            DeleteTempDirectory(output);
        }
    }

    [Test]
    public void SnapshotEip8037GuardMutationFailsClosed()
    {
        string sourcePath = Path.Combine(RepositoryRoot, Extractor.TransactionProcessorPath.Replace('/', Path.DirectorySeparatorChar));
        string text = Encoding.UTF8.GetString(File.ReadAllBytes(sourcePath));
        string original = "if (spec.IsEip8037Enabled)";
        int index = text.IndexOf(original, StringComparison.Ordinal);
        Assert.That(index, Is.GreaterThanOrEqualTo(0));
        string replacement = "if (!spec.IsEip8037Enabled)";
        string changedText = text[..index] + replacement + text[(index + original.Length)..];
        byte[] changed = Encoding.UTF8.GetBytes(changedText);
        string output = CreateTempDirectory();
        try
        {
            Assert.Throws<ExtractionException>(() => Extractor.BuildForTest(RepositoryRoot, output,
                new Dictionary<string, byte[]> { [Extractor.TransactionProcessorPath] = changed }));
            Assert.That(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories), Is.Empty);
        }
        finally
        {
            DeleteTempDirectory(output);
        }
    }

    [Test]
    public void AuthorizationTransactionTypeMutationFailsClosed()
    {
        string sourcePath = Path.Combine(RepositoryRoot, Extractor.TransactionPath.Replace('/', Path.DirectorySeparatorChar));
        string text = Encoding.UTF8.GetString(File.ReadAllBytes(sourcePath));
        string original = "Type == TxType.SetCode";
        string mutated = "Type != TxType.SetCode";
        Assert.That(text.IndexOf(original, StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
        byte[] changed = Encoding.UTF8.GetBytes(text.Replace(original, mutated, StringComparison.Ordinal));
        string output = CreateTempDirectory();
        try
        {
            Assert.Throws<ExtractionException>(() => Extractor.BuildForTest(RepositoryRoot, output,
                new Dictionary<string, byte[]> { [Extractor.TransactionPath] = changed }));
            Assert.That(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories), Is.Empty);
        }
        finally
        {
            DeleteTempDirectory(output);
        }
    }

    [Test]
    public void AuthorizationListStructureMutationFailsClosed()
    {
        string sourcePath = Path.Combine(RepositoryRoot, Extractor.TransactionPath.Replace('/', Path.DirectorySeparatorChar));
        string text = Encoding.UTF8.GetString(File.ReadAllBytes(sourcePath));
        (string Original, string Mutated)[] mutations =
        [
            ("AuthorizationList is not null", "AuthorizationList is null"),
            ("AuthorizationList.Length > 0", "AuthorizationList.Length >= 0"),
        ];
        foreach ((string original, string mutated) in mutations)
        {
            Assert.That(text.IndexOf(original, StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
            byte[] changed = Encoding.UTF8.GetBytes(text.Replace(original, mutated, StringComparison.Ordinal));
            string output = CreateTempDirectory();
            try
            {
                Assert.Throws<ExtractionException>(() => Extractor.BuildForTest(RepositoryRoot, output,
                    new Dictionary<string, byte[]> { [Extractor.TransactionPath] = changed }));
                Assert.That(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories), Is.Empty);
            }
            finally
            {
                DeleteTempDirectory(output);
            }
        }
    }

    [Test]
    public void RecipientDerivationBodyMutationFailsClosed()
    {
        string sourcePath = Path.Combine(RepositoryRoot, Extractor.EvmTransactionExtensionsPath.Replace('/', Path.DirectorySeparatorChar));
        string text = Encoding.UTF8.GetString(File.ReadAllBytes(sourcePath));
        string original = "nonce > 0 ? nonce - 1 : nonce";
        string mutated = "nonce > 0 ? nonce : nonce";
        Assert.That(text.IndexOf(original, StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
        byte[] changed = Encoding.UTF8.GetBytes(text.Replace(original, mutated, StringComparison.Ordinal));
        string output = CreateTempDirectory();
        try
        {
            Assert.Throws<ExtractionException>(() => Extractor.BuildForTest(RepositoryRoot, output,
                new Dictionary<string, byte[]> { [Extractor.EvmTransactionExtensionsPath] = changed }));
            Assert.That(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories), Is.Empty);
        }
        finally
        {
            DeleteTempDirectory(output);
        }
    }

    [Test]
    public void StaticAuthorizationFailureGuardMutationFailsClosed()
    {
        string sourcePath = Path.Combine(RepositoryRoot, Extractor.TransactionProcessorPath.Replace('/', Path.DirectorySeparatorChar));
        string text = Encoding.UTF8.GetString(File.ReadAllBytes(sourcePath));
        (string Original, string Mutated)[] mutations =
        [
            ("if (!noCreation)", "if (noCreation)"),
            ("if (!authList)", "if (authList)"),
        ];
        foreach ((string original, string mutated) in mutations)
        {
            Assert.That(text.IndexOf(original, StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
            byte[] changed = Encoding.UTF8.GetBytes(text.Replace(original, mutated, StringComparison.Ordinal));
            string output = CreateTempDirectory();
            try
            {
                Assert.Throws<ExtractionException>(() => Extractor.BuildForTest(RepositoryRoot, output,
                    new Dictionary<string, byte[]> { [Extractor.TransactionProcessorPath] = changed }));
                Assert.That(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories), Is.Empty);
            }
            finally
            {
                DeleteTempDirectory(output);
            }
        }
    }

    [Test]
    public void OuterExecuteEvmCallTrackerSubstitutionFailsClosed()
    {
        string sourcePath = Path.Combine(RepositoryRoot, Extractor.TransactionProcessorPath.Replace('/', Path.DirectorySeparatorChar));
        string text = Encoding.UTF8.GetString(File.ReadAllBytes(sourcePath));
        string allocation = "using StackAccessTracker accessTracker = new(tracer.IsTracingAccess);";
        string originalArgument = "postIntrinsicStateReservoir, accessTracker, gasAvailable";
        string mutatedArgument = "postIntrinsicStateReservoir, alternateTracker, gasAvailable";
        Assert.That(text.IndexOf(allocation, StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
        Assert.That(text.IndexOf(originalArgument, StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
        string withAlternate = text.Replace(allocation,
            allocation + Environment.NewLine + "            StackAccessTracker alternateTracker = accessTracker;",
            StringComparison.Ordinal);
        byte[] changed = Encoding.UTF8.GetBytes(withAlternate.Replace(originalArgument, mutatedArgument, StringComparison.Ordinal));
        string output = CreateTempDirectory();
        try
        {
            Assert.Throws<ExtractionException>(() => Extractor.BuildForTest(RepositoryRoot, output,
                new Dictionary<string, byte[]> { [Extractor.TransactionProcessorPath] = changed }));
            Assert.That(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories), Is.Empty);
        }
        finally
        {
            DeleteTempDirectory(output);
        }
    }

    [Test]
    public void RentTopLevelAccessParameterSubstitutionFailsClosed()
    {
        string sourcePath = Path.Combine(RepositoryRoot, Extractor.TransactionProcessorPath.Replace('/', Path.DirectorySeparatorChar));
        string text = Encoding.UTF8.GetString(File.ReadAllBytes(sourcePath));
        string rentMarker = "using (VmState<TGasPolicy> state = VmState<TGasPolicy>.RentTopLevel";
        string originalArgument = "in accessedItems, in snapshot";
        string mutatedArgument = "in alternateTracker, in snapshot";
        Assert.That(text.IndexOf(rentMarker, StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
        Assert.That(text.IndexOf(originalArgument, StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
        string withAlternate = text.Replace(rentMarker,
            "StackAccessTracker alternateTracker = accessedItems;" + Environment.NewLine + "            " + rentMarker,
            StringComparison.Ordinal);
        byte[] changed = Encoding.UTF8.GetBytes(withAlternate.Replace(originalArgument, mutatedArgument, StringComparison.Ordinal));
        string output = CreateTempDirectory();
        try
        {
            Assert.Throws<ExtractionException>(() => Extractor.BuildForTest(RepositoryRoot, output,
                new Dictionary<string, byte[]> { [Extractor.TransactionProcessorPath] = changed }));
            Assert.That(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories), Is.Empty);
        }
        finally
        {
            DeleteTempDirectory(output);
        }
    }

    [Test]
    public void HandoffAccessBindingTamperFailsClosed()
    {
        string output = CreateTempDirectory();
        try
        {
            ExtractionResult result = Extractor.BuildForTest(RepositoryRoot, output, new Dictionary<string, byte[]>());
            IrDocument document = Extractor.LoadIrForTest(result.IrPath);
            SourceBinding access = document.Semantics.Handoff.AccessBindings[0];
            HandoffIdentity handoff = document.Semantics.Handoff with
            {
                AccessBindings = document.Semantics.Handoff.AccessBindings.Select(binding => binding == access
                    ? binding with { TargetSymbol = "Nethermind.Evm.FakeAccessTrackerTarget" }
                    : binding).ToArray(),
            };
            Assert.Throws<ExtractionException>(() => Extractor.ValidateIrForTest(document with
            {
                Semantics = document.Semantics with { Handoff = handoff },
            }));
        }
        finally
        {
            DeleteTempDirectory(output);
        }
    }

    [Test]
    public void AuthorizationNonceReadRejectionOrderMutationFailsClosed()
    {
        string sourcePath = Path.Combine(RepositoryRoot, Extractor.TransactionProcessorPath.Replace('/', Path.DirectorySeparatorChar));
        string text = Encoding.UTF8.GetString(File.ReadAllBytes(sourcePath));
        string original = "if (authNonce != authorizationTuple.Nonce)";
        string mutated = "if (authNonce == authorizationTuple.Nonce)";
        Assert.That(text.IndexOf(original, StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
        byte[] changed = Encoding.UTF8.GetBytes(text.Replace(original, mutated, StringComparison.Ordinal));
        string output = CreateTempDirectory();
        try
        {
            Assert.Throws<ExtractionException>(() => Extractor.BuildForTest(RepositoryRoot, output,
                new Dictionary<string, byte[]> { [Extractor.TransactionProcessorPath] = changed }));
            Assert.That(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories), Is.Empty);
        }
        finally
        {
            DeleteTempDirectory(output);
        }
    }

    [Test]
    public void LogicalAccountExistenceGuardMutationFailsClosed()
    {
        string sourcePath = Path.Combine(RepositoryRoot, Extractor.TransactionProcessorPath.Replace('/', Path.DirectorySeparatorChar));
        string text = Encoding.UTF8.GetString(File.ReadAllBytes(sourcePath));
        string original = "if (!logicalAccountExists && !TGasPolicy.TryConsumeStateGas(ref gasAvailable, TGasPolicy.GetNewAccountStateCost()))";
        string mutated = "if (!physicalAccountExists && !TGasPolicy.TryConsumeStateGas(ref gasAvailable, TGasPolicy.GetNewAccountStateCost()))";
        Assert.That(text.IndexOf(original, StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
        byte[] changed = Encoding.UTF8.GetBytes(text.Replace(original, mutated, StringComparison.Ordinal));
        string output = CreateTempDirectory();
        try
        {
            Assert.Throws<ExtractionException>(() => Extractor.BuildForTest(RepositoryRoot, output,
                new Dictionary<string, byte[]> { [Extractor.TransactionProcessorPath] = changed }));
            Assert.That(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories), Is.Empty);
        }
        finally
        {
            DeleteTempDirectory(output);
        }
    }

    [Test]
    public void LogicalPhysicalExistenceGuardPolarityMutationFailsClosed()
    {
        string sourcePath = Path.Combine(RepositoryRoot, Extractor.TransactionProcessorPath.Replace('/', Path.DirectorySeparatorChar));
        string text = Encoding.UTF8.GetString(File.ReadAllBytes(sourcePath));
        string original = "logicalAccountExists = !WorldState.IsDeadAccount(authority);";
        string mutated = "logicalAccountExists = WorldState.IsDeadAccount(authority);";
        Assert.That(text.IndexOf(original, StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
        byte[] changed = Encoding.UTF8.GetBytes(text.Replace(original, mutated, StringComparison.Ordinal));
        string output = CreateTempDirectory();
        try
        {
            Assert.Throws<ExtractionException>(() => Extractor.BuildForTest(RepositoryRoot, output,
                new Dictionary<string, byte[]> { [Extractor.TransactionProcessorPath] = changed }));
            Assert.That(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories), Is.Empty);
        }
        finally
        {
            DeleteTempDirectory(output);
        }
    }

    [Test]
    public void LogicalPhysicalExistenceGuardExtraConditionMutationFailsClosed()
    {
        string sourcePath = Path.Combine(RepositoryRoot, Extractor.TransactionProcessorPath.Replace('/', Path.DirectorySeparatorChar));
        string text = Encoding.UTF8.GetString(File.ReadAllBytes(sourcePath));
        string original = "physicalAccountExists = logicalAccountExists || WorldState.AccountExists(authority);";
        string mutated = "physicalAccountExists = logicalAccountExists || WorldState.AccountExists(authority) || true;";
        Assert.That(text.IndexOf(original, StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
        byte[] changed = Encoding.UTF8.GetBytes(text.Replace(original, mutated, StringComparison.Ordinal));
        string output = CreateTempDirectory();
        try
        {
            Assert.Throws<ExtractionException>(() => Extractor.BuildForTest(RepositoryRoot, output,
                new Dictionary<string, byte[]> { [Extractor.TransactionProcessorPath] = changed }));
            Assert.That(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories), Is.Empty);
        }
        finally
        {
            DeleteTempDirectory(output);
        }
    }

    [Test]
    public void AuthorizationListGuardPolarityMutationFailsClosed()
    {
        string sourcePath = Path.Combine(RepositoryRoot, Extractor.TransactionProcessorPath.Replace('/', Path.DirectorySeparatorChar));
        string text = Encoding.UTF8.GetString(File.ReadAllBytes(sourcePath));
        string original = "if (tx.SupportsAuthorizationList)";
        string mutated = "if (!tx.SupportsAuthorizationList)";
        Assert.That(text.IndexOf(original, StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
        byte[] changed = Encoding.UTF8.GetBytes(text.Replace(original, mutated, StringComparison.Ordinal));
        string output = CreateTempDirectory();
        try
        {
            Assert.Throws<ExtractionException>(() => Extractor.BuildForTest(RepositoryRoot, output,
                new Dictionary<string, byte[]> { [Extractor.TransactionProcessorPath] = changed }));
            Assert.That(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories), Is.Empty);
        }
        finally
        {
            DeleteTempDirectory(output);
        }
    }

    [Test]
    public void NullCodePredicateMutationFailsClosed()
    {
        string sourcePath = Path.Combine(RepositoryRoot, Extractor.TransactionProcessorPath.Replace('/', Path.DirectorySeparatorChar));
        string text = Encoding.UTF8.GetString(File.ReadAllBytes(sourcePath));
        string original = "if (env.CodeInfo is null)";
        string mutated = "if (env.CodeInfo is not null && env.CodeInfo.IsEmpty)";
        Assert.That(text.IndexOf(original, StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
        byte[] changed = Encoding.UTF8.GetBytes(text.Replace(original, mutated, StringComparison.Ordinal));
        string output = CreateTempDirectory();
        try
        {
            Assert.Throws<ExtractionException>(() => Extractor.BuildForTest(RepositoryRoot, output,
                new Dictionary<string, byte[]> { [Extractor.TransactionProcessorPath] = changed }));
            Assert.That(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories), Is.Empty);
        }
        finally
        {
            DeleteTempDirectory(output);
        }
    }

    [Test]
    public void CreateStoragePreparationMutationFailsClosed()
    {
        string sourcePath = Path.Combine(RepositoryRoot, Extractor.TransactionProcessorPath.Replace('/', Path.DirectorySeparatorChar));
        string text = Encoding.UTF8.GetString(File.ReadAllBytes(sourcePath));
        string original = "if (!includeStorageCollision)";
        string mutated = "if (includeStorageCollision)";
        Assert.That(text.IndexOf(original, StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
        byte[] changed = Encoding.UTF8.GetBytes(text.Replace(original, mutated, StringComparison.Ordinal));
        string output = CreateTempDirectory();
        try
        {
            Assert.Throws<ExtractionException>(() => Extractor.BuildForTest(RepositoryRoot, output,
                new Dictionary<string, byte[]> { [Extractor.TransactionProcessorPath] = changed }));
            Assert.That(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories), Is.Empty);
        }
        finally
        {
            DeleteTempDirectory(output);
        }
    }

    [Test]
    public void IrMutationChangesOrRejectsLiveSemantics()
    {
        string output = CreateTempDirectory();
        try
        {
            ExtractionResult result = Extractor.BuildForTest(RepositoryRoot, output, new Dictionary<string, byte[]>());
            IrDocument document = Extractor.LoadIrForTest(result.IrPath);
            OperationIdentity boundary = document.Semantics.Operations.Single(operation => operation.Formula == SemanticFormula.ExecuteTransactionBoundary);
            OperationIdentity mutated = boundary with { Formula = SemanticFormula.RentTopLevel };
            SemanticProfile semantics = document.Semantics with
            {
                Operations = document.Semantics.Operations.Select(operation => operation == boundary ? mutated : operation).ToArray(),
            };
            Assert.Throws<ExtractionException>(() => Extractor.ValidateIrForTest(document with { Semantics = semantics }));
        }
        finally
        {
            DeleteTempDirectory(output);
        }
    }

    [Test]
    public void GeneratedSourceHasNoProofPlaceholders()
    {
        string generated = Path.Combine(RepositoryRoot, "tools/Evm/Lean/EvmTransactionPreparationExtractor/Generated");
        Assert.That(Directory.Exists(generated), Is.True, "The generated artifact directory is required.");
        string[] required =
        [
            Path.Combine(generated, "EvmTransactionPreparation.ir.json"),
            Path.Combine(generated, "EvmTransactionPreparation.source-manifest.json"),
            Path.Combine(generated, "EvmTransactionPreparation.lean"),
        ];
        Assert.That(required.All(File.Exists), Is.True, "All three exact generated artifacts are required before placeholder scanning.");
        string[] placeholders = Directory.EnumerateFiles(generated, "*.lean", SearchOption.AllDirectories)
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, index, line)))
            .Where(item => Regex.IsMatch(item.line, @"\b(sorry|admit|axiom)\b", RegexOptions.CultureInvariant))
            .Select(item => $"{item.path}:{item.index + 1}")
            .ToArray();
        Assert.That(placeholders, Is.Empty);
    }

    [Test]
    public void SourceBoundOperationMutationChangesEmittedTransitionOrFailsClosed(
        [Values("PayValue", "WarmLegacyDelegatedTarget")] string formula)
    {
        string output = CreateTempDirectory();
        try
        {
            ExtractionResult result = Extractor.BuildForTest(RepositoryRoot, output, new Dictionary<string, byte[]>());
            IrDocument document = Extractor.LoadIrForTest(result.IrPath);
            OperationIdentity operation = document.Semantics.Operations.First(item => item.Formula.ToString() == formula);
            OperationIdentity mutatedOperation = operation with
            {
                Binding = operation.Binding with { CanonicalSyntaxSha256 = new string('a', 64) },
            };
            IrDocument mutated = document with
            {
                Semantics = document.Semantics with
                {
                    Operations = document.Semantics.Operations.Select(item => item == operation ? mutatedOperation : item).ToArray(),
                },
            };
            Assert.Throws<ExtractionException>(() => Extractor.ValidateIrForTest(mutated));
        }
        finally
        {
            DeleteTempDirectory(output);
        }
    }

    [Test]
    public void EmissionRequiresTheNormalizedPhaseGates()
    {
        string output = CreateTempDirectory();
        try
        {
            ExtractionResult result = Extractor.BuildForTest(RepositoryRoot, output, new Dictionary<string, byte[]>());
            IrDocument document = Extractor.LoadIrForTest(result.IrPath);
            byte[] emitted = Extractor.EmitLeanForTest(document, result.IrSha256);
            string text = Encoding.UTF8.GetString(emitted);
            Assert.That(text, Does.Contain("def admittedAuthorizationOperations : Bool"));
            Assert.That(text, Does.Contain("def admittedCallOperations : Bool"));
            Assert.That(text, Does.Contain("if !admittedAuthorizationOperations"));
            Assert.That(text, Does.Contain("if !admittedCallOperations"));
        }
        finally
        {
            DeleteTempDirectory(output);
        }
    }

    [Test]
    public void EmissionConsumesEveryNormalizedEvidenceDomain()
    {
        string output = CreateTempDirectory();
        try
        {
            ExtractionResult result = Extractor.BuildForTest(RepositoryRoot, output, new Dictionary<string, byte[]>());
            IrDocument document = Extractor.LoadIrForTest(result.IrPath);
            string text = Encoding.UTF8.GetString(Extractor.EmitLeanForTest(document, result.IrSha256));
            Assert.That(text, Does.Contain("def semanticEvidenceReady : Bool"));
            Assert.That(text, Does.Contain("def operationsExact : Bool := admittedOperations == expectedAdmittedOperations"));
            Assert.That(text, Does.Contain("def branchesExact : Bool := admittedBranches == expectedAdmittedBranches"));
            Assert.That(text, Does.Contain("def gasFieldsExact : Bool := admittedGasFields == expectedAdmittedGasFields"));
            Assert.That(text, Does.Contain("def snapshotsExact : Bool := admittedSnapshots == expectedAdmittedSnapshots"));
            Assert.That(text, Does.Contain("def environmentExact : Bool := admittedEnvironment == expectedAdmittedEnvironment"));
            Assert.That(text, Does.Contain("def handoffExact : Bool := admittedHandoff == expectedAdmittedHandoff"));
            Assert.That(text, Does.Contain("operation.effects.any"));
            Assert.That(text, Does.Contain("def operationReady (expected : OperationEvidence)"));
            Assert.That(text, Does.Contain("def branchReady (expected : BranchEvidence)"));
            Assert.That(text, Does.Contain("def controlFlowReady (expected : ControlFlowEvidence)"));
            Assert.That(text, Does.Contain("VirtualMachine.ExecuteTransaction"));
            Assert.That(text, Does.Contain("stateGasSpillRefunded"));
            Assert.That(text, Does.Contain("def sourceEntryFactsCoherent (input : Input) : Bool"));
            Assert.That(text, Does.Contain("def sourceAccessLineageAdmitted : Bool"));
            Assert.That(text, Does.Contain("!input.handoff.tx.isSetCode || !input.authorizations.isEmpty"));
            Assert.That(text, Does.Contain("def snapshotPresenceCoherent (input : Input) : Bool"));
            Assert.That(text, Does.Contain("def freshAccess (access : AccessObservation) : Bool"));
            Assert.That(text, Does.Contain("input.relations.delegatedTarget.target.isNone"));
            Assert.That(text, Does.Contain("caller := input.handoff.tx.sender"));
            Assert.That(text, Does.Contain("callDepth := 0, value := input.handoff.tx.value, input := messageInputData input"));
        }
        finally
        {
            DeleteTempDirectory(output);
        }
    }

    [Test]
    public void SemanticEvidenceMutationCannotReachTheTransition()
    {
        string output = CreateTempDirectory();
        try
        {
            ExtractionResult result = Extractor.BuildForTest(RepositoryRoot, output, new Dictionary<string, byte[]>());
            IrDocument document = Extractor.LoadIrForTest(result.IrPath);
            OperationIdentity operation = document.Semantics.Operations.First(item => item.Formula == SemanticFormula.PayValue);
            OperationIdentity mutatedOperation = operation with
            {
                Effects = [SemanticEffect.PreservesGas],
            };
            IrDocument mutated = document with
            {
                Semantics = document.Semantics with
                {
                    Operations = document.Semantics.Operations.Select(item => item == operation ? mutatedOperation : item).ToArray(),
                },
            };
            Assert.Throws<ExtractionException>(() => Extractor.EmitLeanForTest(mutated, result.IrSha256));
        }
        finally
        {
            DeleteTempDirectory(output);
        }
    }

    [Test]
    public void FieldCompleteMutationChangesPlanOrFailsClosed()
    {
        string output = CreateTempDirectory();
        try
        {
            ExtractionResult result = Extractor.BuildForTest(RepositoryRoot, output, new Dictionary<string, byte[]>());
            IrDocument document = Extractor.LoadIrForTest(result.IrPath);
            GasFieldIdentity field = document.Semantics.GasFields[1];
            GasFieldIdentity mutatedField = field with { Representation = "UInt64" };
            IrDocument mutated = document with
            {
                Semantics = document.Semantics with
                {
                    GasFields = document.Semantics.GasFields.Select(item => item == field ? mutatedField : item).ToArray(),
                },
            };
            Assert.Throws<ExtractionException>(() => Extractor.ValidateIrForTest(mutated));
        }
        finally
        {
            DeleteTempDirectory(output);
        }
    }

    private static void CopyAdmittedInputs(string root)
    {
        Copy(AdmissionClosurePath);
        foreach (string row in File.ReadLines(Path.Combine(RepositoryRoot, AdmissionClosurePath)))
        {
            string[] columns = row.Split('|');
            if (columns[0] == "source") Copy(columns[2]);
            else if (columns[0] == "dependency") Copy(columns[3]);
        }

        void Copy(string relative)
        {
            string target = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(RepositoryRoot, relative), target);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "evm-transaction-preparation-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("The test could not locate the repository root.");
    }

    private static void DeleteTempDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
