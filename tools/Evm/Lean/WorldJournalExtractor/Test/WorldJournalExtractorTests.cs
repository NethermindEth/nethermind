// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.WorldJournalExtractor.Test;

[TestFixture]
public class WorldJournalExtractorTests
{
    [Test]
    public void Extraction_is_deterministic_field_complete_and_theorem_free()
    {
        using Fixture fixture = new();
        string lean = Path.Combine(fixture.Output, "WorldJournalKernel.lean");
        ExtractionResult first = WorldJournalProfile.Extract(fixture.Root, fixture.Output, lean);
        byte[] firstIr = File.ReadAllBytes(first.IrPath);
        byte[] firstManifest = File.ReadAllBytes(first.ManifestPath);
        byte[] firstLean = File.ReadAllBytes(first.LeanPath);
        WorldJournalProfile.ValidateArtifacts(fixture.Root, firstManifest, firstIr, firstLean);
        ExtractionResult second = WorldJournalProfile.Extract(fixture.Root, fixture.Output, lean);
        using JsonDocument ir = JsonDocument.Parse(firstIr);
        using JsonDocument manifest = JsonDocument.Parse(firstManifest);
        string leanText = Encoding.UTF8.GetString(firstLean);
        string irHash = Convert.ToHexStringLower(SHA256.HashData(firstIr));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.SourceCount, Is.EqualTo(19));
            Assert.That(first.MemberCount, Is.EqualTo(87));
            Assert.That(ir.RootElement.GetProperty("operations").GetArrayLength(), Is.EqualTo(14));
            Assert.That(ir.RootElement.GetProperty("snapshot").GetProperty("restoredSurfaces").GetArrayLength(), Is.EqualTo(7));
            Assert.That(ir.RootElement.GetProperty("snapshot").GetProperty("transactionWideSurfaces").GetArrayLength(), Is.EqualTo(2));
            Assert.That(ir.RootElement.GetProperty("scope").GetProperty("exclusions").GetArrayLength(), Is.EqualTo(8));
            Assert.That(ir.RootElement.GetProperty("scope").GetProperty("unextractedPremises").GetArrayLength(), Is.EqualTo(7));
            Assert.That(manifest.RootElement.GetProperty("sources").GetArrayLength(), Is.EqualTo(19));
            Assert.That(manifest.RootElement.GetProperty("members").GetArrayLength(), Is.EqualTo(87));
            Assert.That(manifest.RootElement.GetProperty("semanticBindings").GetArrayLength(), Is.EqualTo(12));
            Assert.That(File.ReadAllBytes(second.IrPath), Is.EqualTo(firstIr));
            Assert.That(File.ReadAllBytes(second.ManifestPath), Is.EqualTo(firstManifest));
            Assert.That(File.ReadAllBytes(second.LeanPath), Is.EqualTo(firstLean));
            Assert.That(leanText, Does.Contain($"-- Semantic IR SHA-256: {irHash}"));
            Assert.That(leanText, Does.Contain("def transition"));
            Assert.That(leanText, Does.Contain("def executeTrace"));
            Assert.That(leanText, Does.Not.Match(@"\b(theorem|axiom|example|sorry|admit)\b"));
            Assert.That(leanText, Does.Contain("def createdAccountValue"));
            Assert.That(leanText, Does.Contain("storageRoot := emptyStorageRoot, codeHash := emptyCodeHash"));
            Assert.That(leanText, Does.Contain("kind := .justCache"));
            Assert.That(leanText, Does.Contain("def rewindAccountJournal"));
            Assert.That(leanText, Does.Contain("(removed.filter keepCacheEntry).reverse"));
        }
    }

    [Test]
    public void Ir_operation_order_and_all_fields_are_closed()
    {
        IrDocument document = WorldJournalProfile.BuildDocument();
        string[] expected =
        [
            "readAccount", "createAccount", "updateAccount", "deleteAccount",
            "readPersistent", "writePersistent", "readTransient", "writeTransient",
            "warmAccount", "warmCell", "appendLog", "addDestroy", "takeSnapshot", "restoreSnapshot",
        ];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(document.Operations.Select(operation => operation.Name), Is.EqualTo(expected));
            Assert.That(document.Operations.All(operation => operation.OrderedEffects.Length > 0), Is.True);
            Assert.That(document.Operations.All(operation => !string.IsNullOrWhiteSpace(operation.Signature)), Is.True);
            Assert.That(document.Operations.Count(operation => operation.Journaled), Is.EqualTo(10));
            Assert.That(
                document.Snapshot.RestoredSurfaces,
                Is.EqualTo(new[] { "persistent", "transient", "accounts", "warmAccounts", "warmCells", "destroySet", "logs" }));
            Assert.That(document.Snapshot.TransactionWideSurfaces, Is.EqualTo(new[] { "persistentOriginals", "createdThisTx" }));
            Assert.That(document.Scope.ExecutionMode, Does.Contain("isTracingAccess=false"));
            Assert.That(document.Scope.Exclusions, Does.Contain("ClearStorage"));
            Assert.That(document.Scope.Exclusions, Does.Contain("BlockAccessListBasedWorldState and BAL snapshots"));
            Assert.That(document.Scope.Exclusions, Does.Contain("WasCreated mutation and CREATE child initialization"));
            OperationDescriptor persistentWrite = document.Operations.Single(operation => operation.Name == "writePersistent");
            Assert.That(persistentWrite.SourcePath, Is.EqualTo(WorldJournalProfile.PersistentStoragePath));
            Assert.That(persistentWrite.ContainingType, Is.EqualTo("PersistentStorageProvider"));
            Assert.That(persistentWrite.Observation, Does.Contain("projected away"));
            Assert.That(persistentWrite.OrderedEffects, Is.EqualTo(new[]
            {
                "resolveActiveScope", "incrementMetrics", "registerContract", "baseSet",
                "pushUpdate", "hintSlot", "hintAccountOnce",
            }));
        }
    }

    [Test]
    public void Every_operation_name_has_a_mutation_sentinel()
    {
        using Fixture fixture = new();
        ExtractionResult result = WorldJournalProfile.Extract(fixture.Root, fixture.Output, Path.Combine(fixture.Output, "kernel.lean"));
        string source = File.ReadAllText(result.IrPath);

        foreach (OperationDescriptor operation in WorldJournalProfile.BuildDocument().Operations)
        {
            string before = $"\"name\": \"{operation.Name}\"";
            string after = $"\"name\": \"mutated-{operation.Name}\"";
            Assert.That(source, Does.Contain(before));
            Assert.That(
                () => WorldJournalProfile.ValidateSerializedIr(Encoding.UTF8.GetBytes(source.Replace(before, after, StringComparison.Ordinal))),
                Throws.InstanceOf<Exception>(),
                operation.Name);
        }
    }

    [Test]
    public void Serialized_ir_rejects_omission_null_case_duplicate_and_leaf_array_mutations()
    {
        using Fixture fixture = new();
        ExtractionResult result = WorldJournalProfile.Extract(
            fixture.Root,
            fixture.Output,
            Path.Combine(fixture.Output, "kernel.lean"));
        string source = File.ReadAllText(result.IrPath);
        string[] candidates =
        [
            source.Replace("  \"schemaVersion\": 1,\n", string.Empty, StringComparison.Ordinal),
            ReplaceJsonArray(source, "operations", "null"),
            source.Replace("\"schemaVersion\"", "\"SchemaVersion\"", StringComparison.Ordinal),
            source.Replace("  \"schemaVersion\": 1,", "  \"schemaVersion\": 1,\n  \"schemaVersion\": 1,", StringComparison.Ordinal),
            ReplaceJsonArray(source, "orderedEffects", "[]"),
            ReplaceJsonArray(source, "unextractedPremises", "null"),
        ];

        foreach (string candidate in candidates)
        {
            Assert.That(
                () => WorldJournalProfile.ValidateSerializedIr(Encoding.UTF8.GetBytes(candidate)),
                Throws.InstanceOf<Exception>());
        }
    }

    [Test]
    public void Canonical_json_rejects_bom_crlf_and_final_lf_mutations()
    {
        using Fixture fixture = new();
        ExtractionResult result = WorldJournalProfile.Extract(
            fixture.Root,
            fixture.Output,
            Path.Combine(fixture.Output, "kernel.lean"));
        byte[] ir = File.ReadAllBytes(result.IrPath);
        byte[] manifest = File.ReadAllBytes(result.ManifestPath);

        foreach (byte[] candidate in EncodingMutants(ir))
        {
            Assert.That(() => WorldJournalProfile.ValidateSerializedIr(candidate), Throws.InstanceOf<Exception>());
        }
        foreach (byte[] candidate in EncodingMutants(manifest))
        {
            Assert.That(() => WorldJournalProfile.ValidateSerializedManifest(candidate), Throws.InstanceOf<Exception>());
        }
    }

    [Test]
    public void Every_admitted_source_rejects_a_byte_mutation()
    {
        foreach (string relativePath in WorldJournalProfile.SourceRelativePaths)
        {
            using SourceFixture fixture = new();
            fixture.Append(relativePath, " ");
            Exception? exception = Assert.Throws<ExtractionException>(() =>
                WorldJournalProfile.Extract(
                    fixture.Root,
                    fixture.Output,
                    Path.Combine(fixture.Output, "kernel.lean")));
            Assert.That(exception!.Message, Does.Contain("Pinned source changed"), relativePath);
        }
    }

    [TestCase("\"name\": \"readAccount\"", "\"name\": \"readAccounx\"", TestName = "IR_rejects_operation_name_mutation")]
    [TestCase("\"surface\": \"persistentOriginals\"", "\"surface\": \"persistent\"", TestName = "IR_rejects_original_surface_mutation")]
    [TestCase("\"journaled\": false", "\"journaled\": true", TestName = "IR_rejects_journal_flag_mutation")]
    [TestCase("\"persistentOriginals\",", "\"persistent\",", TestName = "IR_rejects_transaction_surface_mutation")]
    [TestCase(
        "\"sourcePath\": \"src/Nethermind/Nethermind.State/PersistentStorageProvider.cs\"",
        "\"sourcePath\": \"src/Nethermind/Nethermind.State/PartialStorageProviderBase.cs\"",
        TestName = "IR_rejects_persistent_write_concrete_override_bypass")]
    [TestCase(
        "\"restoreOrder\": \"persistent, transient, accounts, warmAccounts, warmCells, destroySet, logs\"",
        "\"restoreOrder\": \"transient, persistent, accounts, warmAccounts, warmCells, destroySet, logs\"",
        TestName = "IR_rejects_restore_order_mutation")]
    public void Serialized_ir_rejects_semantic_mutation(string before, string after)
    {
        using Fixture fixture = new();
        ExtractionResult result = WorldJournalProfile.Extract(fixture.Root, fixture.Output, Path.Combine(fixture.Output, "kernel.lean"));
        string source = File.ReadAllText(result.IrPath);
        Assert.That(source, Does.Contain(before));
        byte[] mutated = Encoding.UTF8.GetBytes(source.Replace(before, after, StringComparison.Ordinal));

        Assert.That(() => WorldJournalProfile.ValidateSerializedIr(mutated), Throws.InstanceOf<Exception>());
    }

    [Test]
    public void Manifest_rejects_source_member_and_aggregate_mutations()
    {
        using Fixture fixture = new();
        ExtractionResult result = WorldJournalProfile.Extract(fixture.Root, fixture.Output, Path.Combine(fixture.Output, "kernel.lean"));
        string source = File.ReadAllText(result.ManifestPath);
        string[] candidates =
        [
            source.Replace(WorldJournalProfile.Kernel, "mutated kernel", StringComparison.Ordinal),
            ReplaceFirstHash(source),
            source.Replace("\"containingType\": \"StateProvider\"", "\"containingType\": \"WorldState\"", StringComparison.Ordinal),
            source.Replace("\"combinedSourceSha256\":", "\"combinedSourceSha25x\":", StringComparison.Ordinal),
            source.Replace("pre-commit only", "post-commit", StringComparison.Ordinal),
        ];

        foreach (string candidate in candidates)
        {
            Assert.That(() => WorldJournalProfile.ValidateSerializedManifest(Encoding.UTF8.GetBytes(candidate)), Throws.InstanceOf<Exception>());
        }

        Assert.That(
            () => WorldJournalProfile.ValidateSerializedManifest(
                Encoding.UTF8.GetBytes(source),
                Encoding.UTF8.GetBytes(source.Replace("\"compilerVersion\": \"", "\"compilerVersion\": \"mutated-", StringComparison.Ordinal))),
            Throws.InstanceOf<Exception>());
    }

    [Test]
    public void Manifest_rejects_omission_null_case_duplicate_and_leaf_array_mutations()
    {
        using Fixture fixture = new();
        ExtractionResult result = WorldJournalProfile.Extract(
            fixture.Root,
            fixture.Output,
            Path.Combine(fixture.Output, "kernel.lean"));
        string source = File.ReadAllText(result.ManifestPath);
        string[] candidates =
        [
            source.Replace("  \"schemaVersion\": 1,\n", string.Empty, StringComparison.Ordinal),
            ReplaceJsonArray(source, "sources", "null"),
            source.Replace("\"extractorVersion\"", "\"ExtractorVersion\"", StringComparison.Ordinal),
            source.Replace("  \"schemaVersion\": 1,", "  \"schemaVersion\": 1,\n  \"schemaVersion\": 1,", StringComparison.Ordinal),
            ReplaceJsonArray(source, "members", "[]"),
            ReplaceJsonArray(source, "semanticBindings", "null"),
        ];

        foreach (string candidate in candidates)
        {
            Assert.That(
                () => WorldJournalProfile.ValidateSerializedManifest(Encoding.UTF8.GetBytes(candidate)),
                Throws.InstanceOf<Exception>());
        }
    }

    [Test]
    public void Source_derived_validation_rejects_uppercase_and_alternate_self_consistent_digests()
    {
        using Fixture fixture = new();
        ExtractionResult result = WorldJournalProfile.Extract(
            fixture.Root,
            fixture.Output,
            Path.Combine(fixture.Output, "kernel.lean"));
        byte[] ir = File.ReadAllBytes(result.IrPath);
        byte[] lean = File.ReadAllBytes(result.LeanPath);
        string source = File.ReadAllText(result.ManifestPath);
        int firstHashStart = source.IndexOf("\"sha256\": \"", StringComparison.Ordinal) + "\"sha256\": \"".Length;
        string firstHash = source.Substring(firstHashStart, 64);
        string uppercase = ReplaceAt(source, firstHashStart, 64, firstHash.ToUpperInvariant());

        Assert.That(
            () => WorldJournalProfile.ValidateSerializedManifest(Encoding.UTF8.GetBytes(uppercase)),
            Throws.InstanceOf<Exception>());

        string alternate = ReplaceAt(source, firstHashStart, 64, new string('0', 64));
        string originalCombined = ReadStringProperty(source, "combinedSourceSha256");
        string alternateCombined = CalculateCombinedSourceHash(alternate);
        alternate = alternate.Replace(originalCombined, alternateCombined, StringComparison.Ordinal);
        byte[] alternateBytes = Encoding.UTF8.GetBytes(alternate);
        WorldJournalProfile.ValidateSerializedManifest(alternateBytes);
        Assert.That(
            () => WorldJournalProfile.ValidateArtifacts(fixture.Root, alternateBytes, ir, lean),
            Throws.InstanceOf<ExtractionException>());
    }

    [Test]
    public void Artifact_validation_rejects_proof_import_name_semantic_and_encoding_mutations()
    {
        using Fixture fixture = new();
        ExtractionResult result = WorldJournalProfile.Extract(
            fixture.Root,
            fixture.Output,
            Path.Combine(fixture.Output, "kernel.lean"));
        byte[] manifest = File.ReadAllBytes(result.ManifestPath);
        byte[] ir = File.ReadAllBytes(result.IrPath);
        string lean = File.ReadAllText(result.LeanPath);
        string[] textMutants =
        [
            lean + "theorem injected : True := by trivial\n",
            lean + "axiom injected : True\n",
            lean + "example : True := by trivial\n",
            lean + "def injected : True := by sorry\n",
            lean + "def injected : True := by admit\n",
            lean.Replace(
                "import WorldJournalExtractor.Specification.WorldProjection",
                "import WorldJournalExtractor.Specification.WorldJournal",
                StringComparison.Ordinal),
            lean.Replace("def transition", "def handwrittenTransition", StringComparison.Ordinal),
            lean.Replace("storageRoot := emptyStorageRoot", "storageRoot := 1", StringComparison.Ordinal),
            lean.Replace("(removed.filter keepCacheEntry).reverse", "removed.filter keepCacheEntry", StringComparison.Ordinal),
        ];

        foreach (string mutant in textMutants)
        {
            Assert.That(
                () => WorldJournalProfile.ValidateArtifacts(
                    fixture.Root,
                    manifest,
                    ir,
                    Encoding.UTF8.GetBytes(mutant)),
                Throws.InstanceOf<ExtractionException>());
        }
        foreach (byte[] mutant in EncodingMutants(Encoding.UTF8.GetBytes(lean)))
        {
            Assert.That(
                () => WorldJournalProfile.ValidateArtifacts(fixture.Root, manifest, ir, mutant),
                Throws.InstanceOf<ExtractionException>());
        }
    }

    [Test]
    public void Manifest_member_identities_are_complete_and_unique()
    {
        using Fixture fixture = new();
        ExtractionResult result = WorldJournalProfile.Extract(fixture.Root, fixture.Output, Path.Combine(fixture.Output, "kernel.lean"));
        SourceManifest manifest = WorldJournalProfile.DeserializeManifest(File.ReadAllBytes(result.ManifestPath));
        string[] memberIdentities = manifest.Members
            .Select(member => $"{member.SourcePath}:{member.ContainingType}:{member.Signature}")
            .Distinct()
            .ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(manifest.Members.All(member => member.CanonicalSha256.Length == 64), Is.True);
            Assert.That(memberIdentities, Has.Length.EqualTo(87));
            Assert.That(manifest.Sources.Select(source => source.Path), Is.EqualTo(WorldJournalProfile.SourceRelativePaths));
            Assert.That(manifest.Members.Any(member => member.Member == "PushJustCache"), Is.True);
            Assert.That(manifest.Members.Any(member => member.Member == "PushNew"), Is.True);
            Assert.That(manifest.Members.Any(member => member.Member == "PushDelete"), Is.True);
            Assert.That(manifest.Members.Any(member => member.Member == "Position"), Is.True);
            Assert.That(manifest.Members.Any(member => member.SourcePath == WorldJournalProfile.ResettablePath && member.Member == "EmptyPosition"), Is.True);
            Assert.That(manifest.Members.Any(member => member.SourcePath == WorldJournalProfile.StorageTreePath && member.Member == "ZeroBytes"), Is.True);
            Assert.That(manifest.Members.Count(member => member.SourcePath == WorldJournalProfile.KeccakPath), Is.EqualTo(3));
            Assert.That(manifest.Members.Any(member => member.SourcePath == WorldJournalProfile.PersistentStoragePath &&
                member.ContainingType == "PersistentStorageProvider" && member.Signature == "void Set(in StorageCell,byte[])"), Is.True);
            Assert.That(manifest.Members.Any(member => member.SourcePath == WorldJournalProfile.WorldStateScopeInterfacePath &&
                member.Member == "HintWarmSlot"), Is.True);
            Assert.That(manifest.Members.Any(member => member.SourcePath == WorldJournalProfile.LocalMetricsPath &&
                member.Member == "IncrementStorageWrites"), Is.True);
        }
    }

    [TestCase(
        "public override void Set(in StorageCell storageCell, byte[] newValue)",
        "public void Set(in StorageCell storageCell, byte[] newValue)",
        TestName = "Rejects_persistent_set_override_removal")]
    [TestCase(
        "base.Set(in storageCell, newValue);",
        "_ = newValue;",
        TestName = "Rejects_persistent_set_base_body_bypass")]
    [TestCase(
        "PerContractState state = GetOrCreateStorage(storageCell.Address);\n        base.Set(in storageCell, newValue);",
        "base.Set(in storageCell, newValue);\n        PerContractState state = GetOrCreateStorage(storageCell.Address);",
        TestName = "Rejects_persistent_set_reachability_order_mutation")]
    [TestCase(
        "public override void Set(in StorageCell storageCell, byte[] newValue)",
        "public override void Set(StorageCell storageCell, byte[] newValue)",
        TestName = "Rejects_persistent_set_overload_mutation")]
    public void Persistent_write_override_mutations_fail_closed(string before, string after)
    {
        using SourceFixture fixture = new();
        fixture.Replace(WorldJournalProfile.PersistentStoragePath, before, after);

        Exception? exception = Assert.Throws<ExtractionException>(() =>
            WorldJournalProfile.ValidateSourceShape(fixture.Root));
        Assert.That(exception!.Message, Does.Contain("PersistentStorageProvider.Set"));
    }

    [TestCase(
        WorldJournalProfile.WorldStatePath,
        "_persistentStorageProvider.Restore(snapshot.StorageSnapshot.PersistentStorageSnapshot);\r\n            _transientStorageProvider.Restore(snapshot.StorageSnapshot.TransientStorageSnapshot);",
        "_transientStorageProvider.Restore(snapshot.StorageSnapshot.TransientStorageSnapshot);\r\n            _persistentStorageProvider.Restore(snapshot.StorageSnapshot.PersistentStorageSnapshot);",
        TestName = "Rejects_world_restore_order_mutation")]
    [TestCase(
        WorldJournalProfile.AccessTrackerPath,
        "if (!_isTracingAccess)",
        "if (_isTracingAccess)",
        TestName = "Rejects_normal_access_restore_gate_mutation")]
    [TestCase(
        WorldJournalProfile.StateProviderPath,
        "ChangeType.JustCache",
        "ChangeType.Update",
        TestName = "Rejects_just_cache_restore_mutation")]
    [TestCase(
        WorldJournalProfile.PartialStoragePath,
        "bool firstWriteThisTx = !exists || head.CurrentIdx <= currentSnapshot;",
        "bool firstWriteThisTx = !exists;",
        TestName = "Rejects_transaction_original_mutation")]
    [TestCase(
        WorldJournalProfile.JournalCollectionPath,
        "public void Add(T item) => _list.Add(item);",
        "public void Add(T item) => _list.Insert(0, item);",
        TestName = "Rejects_log_order_mutation")]
    [TestCase(
        WorldJournalProfile.JournalSetPath,
        "if (_set.Add(item))",
        "if (true)",
        TestName = "Rejects_set_idempotence_mutation")]
    [TestCase(
        WorldJournalProfile.VmStatePath,
        "_accessTracker.WasCreated(env.ExecutingAccount);",
        "_accessTracker.WarmUp(env.ExecutingAccount);",
        TestName = "Rejects_created_this_transaction_mutation")]
    [TestCase(
        WorldJournalProfile.WorldStatePath,
        "_persistentStorageProvider.Set(storageCell, newValue);",
        "_transientStorageProvider.Set(storageCell, newValue);",
        TestName = "Rejects_world_to_persistent_set_reachability_mutation")]
    [TestCase(
        WorldJournalProfile.PartialStoragePath,
        "public virtual void Set(in StorageCell storageCell, byte[] newValue) => PushUpdate(in storageCell, newValue);",
        "public virtual void Set(in StorageCell storageCell, byte[] newValue) { }",
        TestName = "Rejects_base_set_to_push_update_reachability_mutation")]
    public void Source_semantic_mutations_fail_closed(string path, string before, string after)
    {
        using SourceFixture fixture = new();
        fixture.Replace(path, before, after);

        Assert.That(
            () => WorldJournalProfile.Extract(fixture.Root, fixture.Output, Path.Combine(fixture.Output, "kernel.lean")),
            Throws.InstanceOf<ExtractionException>());
    }

    [Test]
    public void Source_signature_mutation_fails_closed()
    {
        using SourceFixture fixture = new();
        fixture.Replace(
            WorldJournalProfile.WorldStatePath,
            "public void Restore(Snapshot snapshot)",
            "public void Restore(in Snapshot snapshot)");

        Exception? exception = Assert.Throws<ExtractionException>(() =>
            WorldJournalProfile.ValidateSourceShape(fixture.Root));
        Assert.That(exception!.Message, Does.Contain("Operation restoreSnapshot"));
    }

    [Test]
    public void Source_owner_mutation_fails_exact_member_admission()
    {
        using SourceFixture fixture = new();
        fixture.Replace(WorldJournalProfile.AccountPath, "public class Account :", "public class RenamedAccount :");

        Exception? exception = Assert.Throws<ExtractionException>(() =>
            WorldJournalProfile.ValidateSourceShape(fixture.Root));
        Assert.That(exception!.Message, Does.Contain("Account.Account"));
    }

    [Test]
    public void Source_overload_mutation_fails_exact_parameter_admission()
    {
        using SourceFixture fixture = new();
        fixture.Replace(
            WorldJournalProfile.AccessTrackerPath,
            "public readonly bool WarmUp(in StorageCell storageCell)",
            "public readonly bool WarmUp(in object storageCell)");

        Exception? exception = Assert.Throws<ExtractionException>(() =>
            WorldJournalProfile.ValidateSourceShape(fixture.Root));
        Assert.That(exception!.Message, Does.Contain("StackAccessTracker.WarmUp"));
    }

    [Test]
    public void Competing_member_declaration_fails_closed()
    {
        using SourceFixture fixture = new();
        const string original = """
            private void PushDelete(Address address)
                    => Push(address, null, ChangeType.Delete);
            """;
        const string competing = """
            private void PushDelete(Address address)
                    => Push(address, null, ChangeType.Delete);

            private void PushDelete(Address competingAddress)
                    => Push(competingAddress, null, ChangeType.Delete);
            """;
        fixture.Replace(WorldJournalProfile.StateProviderPath, original, competing);

        Exception? exception = Assert.Throws<ExtractionException>(() =>
            WorldJournalProfile.ValidateSourceShape(fixture.Root));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.Message, Does.Contain("StateProvider.PushDelete"));
            Assert.That(exception.Message, Does.Contain("found 2"));
        }
    }

    [Test]
    public void Missing_source_fails_closed()
    {
        using SourceFixture fixture = new();
        File.Delete(Path.Combine(fixture.Root, WorldJournalProfile.SnapshotPath));

        Assert.That(
            () => WorldJournalProfile.Extract(fixture.Root, fixture.Output, Path.Combine(fixture.Output, "kernel.lean")),
            Throws.InstanceOf<ExtractionException>());
    }

    private static string ReplaceFirstHash(string source)
    {
        int start = source.IndexOf("\"sha256\": \"", StringComparison.Ordinal) + "\"sha256\": \"".Length;
        return string.Concat(source.AsSpan(0, start), new string('0', 64), source.AsSpan(start + 64));
    }

    private static string ReplaceJsonArray(string source, string propertyName, string replacement)
    {
        int property = source.IndexOf($"\"{propertyName}\":", StringComparison.Ordinal);
        if (property < 0) throw new InvalidOperationException($"JSON property {propertyName} was not found.");
        int start = source.IndexOf('[', property);
        if (start < 0) throw new InvalidOperationException($"JSON array {propertyName} was not found.");
        int depth = 0;
        bool quoted = false;
        bool escaped = false;
        for (int index = start; index < source.Length; index++)
        {
            char character = source[index];
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') quoted = false;
                continue;
            }
            if (character == '"') quoted = true;
            else if (character == '[') depth++;
            else if (character == ']' && --depth == 0) return ReplaceAt(source, start, index - start + 1, replacement);
        }
        throw new InvalidOperationException($"JSON array {propertyName} was not terminated.");
    }

    private static byte[][] EncodingMutants(byte[] canonical)
    {
        byte[] bom = new byte[canonical.Length + 3];
        bom[0] = 0xef;
        bom[1] = 0xbb;
        bom[2] = 0xbf;
        canonical.CopyTo(bom, 3);
        string text = Encoding.UTF8.GetString(canonical);
        byte[] crlf = Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\r\n"));
        byte[] missingFinalLf = canonical[..^1];
        byte[] duplicateFinalLf = new byte[canonical.Length + 1];
        canonical.CopyTo(duplicateFinalLf, 0);
        duplicateFinalLf[^1] = (byte)'\n';
        return [bom, crlf, missingFinalLf, duplicateFinalLf];
    }

    private static string ReplaceAt(string source, int start, int length, string replacement) =>
        string.Concat(source.AsSpan(0, start), replacement, source.AsSpan(start + length));

    private static string ReadStringProperty(string json, string propertyName)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(propertyName).GetString()
            ?? throw new InvalidOperationException($"JSON property {propertyName} was null.");
    }

    private static string CalculateCombinedSourceHash(string manifest)
    {
        using JsonDocument document = JsonDocument.Parse(manifest);
        StringBuilder source = new();
        foreach (JsonElement item in document.RootElement.GetProperty("sources").EnumerateArray())
        {
            source.Append(item.GetProperty("path").GetString());
            source.Append('\0');
            source.Append(item.GetProperty("sha256").GetString());
            source.Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.ToString())));
    }

    private class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = FindRoot();
            Output = Path.Combine(Path.GetTempPath(), $"world-journal-extractor-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Output);
        }

        public string Root { get; protected set; }
        public string Output { get; }

        public void Dispose() => Directory.Delete(Output, recursive: true);

        private static string FindRoot()
        {
            DirectoryInfo? current = new(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, WorldJournalProfile.WorldStatePath))) return current.FullName;
                current = current.Parent;
            }
            throw new InvalidOperationException("Could not locate the repository root.");
        }
    }

    private sealed class SourceFixture : Fixture
    {
        public SourceFixture()
        {
            string sourceRoot = Root;
            Root = Path.Combine(Output, "repo");
            foreach (string relativePath in WorldJournalProfile.SourceRelativePaths)
            {
                string source = Path.Combine(sourceRoot, relativePath);
                string destination = Path.Combine(Root, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }
        }

        public void Replace(string path, string before, string after)
        {
            string fullPath = Path.Combine(Root, path);
            string source = File.ReadAllText(fullPath);
            string lineEnding = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            string normalizedSource = source.ReplaceLineEndings("\n");
            string normalizedBefore = before.ReplaceLineEndings("\n");
            string normalizedAfter = after.ReplaceLineEndings("\n");
            if (!normalizedSource.Contains(normalizedBefore, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Fixture token not found in {path}: {before}");
            }
            File.WriteAllText(
                fullPath,
                normalizedSource.Replace(normalizedBefore, normalizedAfter, StringComparison.Ordinal).ReplaceLineEndings(lineEnding));
        }

        public void Append(string path, string value) =>
            File.AppendAllText(Path.Combine(Root, path), value, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
