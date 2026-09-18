// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.WorldJournalExtractor;

internal static class WorldJournalProfile
{
    internal const string WorldStatePath = "src/Nethermind/Nethermind.State/WorldState.cs";
    internal const string WorldStateInterfacePath = "src/Nethermind/Nethermind.Evm/State/IWorldState.cs";
    internal const string WorldStateScopeInterfacePath = "src/Nethermind/Nethermind.Evm/State/IWorldStateScopeProvider.cs";
    internal const string LocalMetricsPath = "src/Nethermind/Nethermind.Evm/State/LocalMetrics.cs";
    internal const string StateProviderPath = "src/Nethermind/Nethermind.State/StateProvider.cs";
    internal const string PartialStoragePath = "src/Nethermind/Nethermind.State/PartialStorageProviderBase.cs";
    internal const string PersistentStoragePath = "src/Nethermind/Nethermind.State/PersistentStorageProvider.cs";
    internal const string TransientStoragePath = "src/Nethermind/Nethermind.State/TransientStorageProvider.cs";
    internal const string AccessTrackerPath = "src/Nethermind/Nethermind.Evm/StackAccessTracker.cs";
    internal const string SnapshotPath = "src/Nethermind/Nethermind.Evm/State/Snapshot.cs";
    internal const string JournalSetPath = "src/Nethermind/Nethermind.Core/Collections/JournalSet.cs";
    internal const string JournalCollectionPath = "src/Nethermind/Nethermind.Core/Collections/JournalCollection.cs";
    internal const string VmStatePath = "src/Nethermind/Nethermind.Evm/VmState.cs";
    internal const string VirtualMachinePath = "src/Nethermind/Nethermind.Evm/VirtualMachine.cs";
    internal const string MainnetDiPath = "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs";
    internal const string AccountPath = "src/Nethermind/Nethermind.Core/Account.cs";
    internal const string ResettablePath = "src/Nethermind/Nethermind.Core/Resettables/Resettable.cs";
    internal const string StorageTreePath = "src/Nethermind/Nethermind.State/StorageTree.cs";
    internal const string KeccakPath = "src/Nethermind/Nethermind.Core/Crypto/Keccak.cs";

    internal const string DefaultOutputRelativePath = "tools/Evm/Lean/WorldJournalExtractor/Generated";
    internal const string DefaultLeanRelativePath = "tools/Evm/Lean/WorldJournalExtractor/Generated/WorldJournalKernel.lean";
    internal const string IrFileName = "WorldJournalKernel.ir.json";
    internal const string ManifestFileName = "WorldJournalKernel.source-manifest.json";
    internal const string ExtractorVersion = "1.0.0";
    internal const string Kernel = "Nethermind standard-mainnet pre-commit extensional world journal";

    private static string CompilerVersion =>
        typeof(CSharpCompilation).Assembly.GetName().Version?.ToString() ?? "unknown";

    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default
        .WithLanguageVersion(LanguageVersion.CSharp14)
        .WithDocumentationMode(DocumentationMode.Parse)
        .WithKind(SourceCodeKind.Regular);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
    };

    private static readonly string[] SourcePaths =
    [
        WorldStatePath,
        WorldStateInterfacePath,
        WorldStateScopeInterfacePath,
        LocalMetricsPath,
        StateProviderPath,
        PartialStoragePath,
        PersistentStoragePath,
        TransientStoragePath,
        AccessTrackerPath,
        SnapshotPath,
        JournalSetPath,
        JournalCollectionPath,
        VmStatePath,
        VirtualMachinePath,
        MainnetDiPath,
        AccountPath,
        ResettablePath,
        StorageTreePath,
        KeccakPath,
    ];

    internal static IReadOnlyList<string> SourceRelativePaths => SourcePaths;

    internal static void ValidateSourceShape(string repoRoot)
    {
        string root = Path.GetFullPath(repoRoot);
        SourceFile[] sources = SourcePaths.Select(path => Read(root, path)).ToArray();
        ValidateScope(sources);
        MemberIdentity[] members = MemberExpectations
            .Select(expectation => AdmitMember(sources, expectation))
            .ToArray();
        ValidateOperationBindings(BuildDocument(), members);
    }

    private const string ExpectedFingerprintText = """
        src/Nethermind/Nethermind.State/WorldState.cs 2dec74bcc5748a1850d1bed1e8fe0221aed8c904bf2ba0c0827e76e603a71f18
        src/Nethermind/Nethermind.Evm/State/IWorldState.cs 0680ebc7168472645d93df58d2b3240508c177361815c85cceb09cf960f189c7
        src/Nethermind/Nethermind.Evm/State/IWorldStateScopeProvider.cs 8c68c7561effd7e00f11985d36464d7ec97e8cd4e20b68ea48a66ccfd3702c86
        src/Nethermind/Nethermind.Evm/State/LocalMetrics.cs c646617c6bfeb451d38c9a74c42d3e45da684f731068d96931e9b286d73a3f3c
        src/Nethermind/Nethermind.State/StateProvider.cs 93eab6702f4e3ef6c52afb0f595ff2b0c17cdeabbce1de3f0507f2aa0e6f6858
        src/Nethermind/Nethermind.State/PartialStorageProviderBase.cs 0ade22ba2312b9ac665ca03afa2547f6f884232f20d3a58867b2c00402daa085
        src/Nethermind/Nethermind.State/PersistentStorageProvider.cs fd04e010e7088473b3a007f2cb8d688cf1d389f42866c60af8baa8860202683a
        src/Nethermind/Nethermind.State/TransientStorageProvider.cs d9731e1bae54aeb296c7a3a22baae18c35a043167623149888b1cfa5f098ba4d
        src/Nethermind/Nethermind.Evm/StackAccessTracker.cs ed5cdfdfe2ebf4d8c9f651572927efe7bdb5c75107db63a40577c5ec3ec2882f
        src/Nethermind/Nethermind.Evm/State/Snapshot.cs 47625d60a8315bbc3afd26eca85780b0ab894d7401d913cbec00ed89b51f46c8
        src/Nethermind/Nethermind.Core/Collections/JournalSet.cs 1ec1cff208a23cbde6f4352663061a3c3ecd517ebf1c96e789ab6ce97a150397
        src/Nethermind/Nethermind.Core/Collections/JournalCollection.cs 5c1395b025da5a03749a5b64da804e53c6ff0a7aec4c02d30fd4b1c0e059c720
        src/Nethermind/Nethermind.Evm/VmState.cs 7b7b6ddb753118b426d14976a3fe8e79f808320d6a09ea9cace9df19197f9f23
        src/Nethermind/Nethermind.Evm/VirtualMachine.cs 45edba3691e09185e749485785990662ddea1af849bc6e4564f92b68a5137a6b
        src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs fce65ce5bb523c56fc8aa6ee0e4d092c940a5a90b174c820ffe262eeb5241c60
        src/Nethermind/Nethermind.Core/Account.cs 3a94d972388a4c5d46d744bf0733b80fb6575a148bf32123d64a0a51abddd749
        src/Nethermind/Nethermind.Core/Resettables/Resettable.cs 6217e574a6018462d7df5d337304c1dd75014a1d6f27646da98bbd46a38251d2
        src/Nethermind/Nethermind.State/StorageTree.cs bf54dcfe7216eb73dc8c40fde911b1376e0868e9cff43dced0fe603ba29e179b
        src/Nethermind/Nethermind.Core/Crypto/Keccak.cs 86617536dfc7d4c040f047ed1477fc639a38f0f42f2857c354c24d585568782e
        """;

    private static readonly MemberExpectation[] MemberExpectations =
    [
        new(WorldStatePath, "WorldState", "GetAccount", 1, ["_stateProvider.GetAccount"]),
        new(WorldStatePath, "WorldState", "TryGetAccount", 2, ["_stateProvider.GetAccount", "WithChangedStorageRoot", "!account.IsTotallyEmpty"]),
        new(WorldStatePath, "WorldState", "HasEmptyAccountLeaf", 1, ["_stateProvider.GetThroughCache", "IsEmpty"]),
        new(WorldStatePath, "WorldState", "GetOriginal", 1, ["_persistentStorageProvider.GetOriginal"]),
        new(WorldStatePath, "WorldState", "Get", 1, ["_persistentStorageProvider.Get"]),
        new(WorldStatePath, "WorldState", "Set", 2, ["DebugGuardInScope", "_persistentStorageProvider.Set"],
            ExactSignature: "void Set(in StorageCell,byte[])", RequiredModifiers: ["public"]),
        new(WorldStatePath, "WorldState", "GetTransientState", 1, ["_transientStorageProvider.Get"]),
        new(WorldStatePath, "WorldState", "SetTransientState", 2, ["_transientStorageProvider.Set"]),
        new(WorldStatePath, "WorldState", "CreateAccount", 3, ["_stateProvider.CreateAccount"]),
        new(WorldStatePath, "WorldState", "DeleteAccount", 1, ["_stateProvider.DeleteAccount"]),
        new(StateProviderPath, "StateProvider", "GetAccount", 1, ["GetThroughCache", "Account.TotallyEmpty"]),
        new(StateProviderPath, "StateProvider", "GetThroughCache", 1, ["IsFrontCacheHit", "_intraTxCache.TryGetValue", "GetAndAddToCache"]),
        new(StateProviderPath, "StateProvider", "IsFrontCacheHit", 1, ["_cachedEpoch == _epoch", "_cachedAddress.Equals"]),
        new(StateProviderPath, "StateProvider", "GetAndAddToCache", 1, ["GetState", "PushJustCache"]),
        new(StateProviderPath, "StateProvider", "GetState", 1, ["GetOrAddBlockChange", "Tree.Get", "return accountChanges.After"]),
        new(StateProviderPath, "StateProvider", "GetOrAddBlockChange", 2, ["CollectionsMarshal.GetValueRefOrAddDefault"]),
        new(StateProviderPath, "StateProvider", "PushJustCache", 2, ["ChangeType.JustCache"]),
        new(StateProviderPath, "StateProvider", "CreateAccount", 3,
            ["balance.IsZero", "nonce == 0", "Account.TotallyEmpty", "new Account(nonce, balance)", "PushNew"]),
        new(StateProviderPath, "StateProvider", "PushNew", 2, ["ChangeType.New"]),
        new(StateProviderPath, "StateProvider", "PushUpdate", 2, ["Push(address, account, ChangeType.Update)"]),
        new(StateProviderPath, "StateProvider", "DeleteAccount", 1, ["PushDelete"]),
        new(StateProviderPath, "StateProvider", "PushDelete", 1, ["Push(address, null, ChangeType.Delete)"]),
        new(StateProviderPath, "StateProvider", "Push", 3, ["GetValueRefOrAddDefault", "head = _changes.Count", "_changes.Add"]),
        new(StateProviderPath, "StateProvider", "TakeSnapshot", 0, ["_changes.Count - 1"]),
        new(StateProviderPath, "StateProvider", "Restore", 1, ["RestoreCodeInserts", "CollectionsMarshal.AsSpan", "ChangeType.JustCache", "CollectionsMarshal.SetCount", "_changes.Add"]),
        new(StateProviderPath, "StateProvider", "InvalidateFrontCache", 0, ["_epoch++"]),
        new(PartialStoragePath, "PartialStorageProviderBase", "Get", 1, ["GetCurrentValue"]),
        new(PartialStoragePath, "PartialStorageProviderBase", "Set", 2, ["PushUpdate"],
            ExactSignature: "void Set(in StorageCell,byte[])", RequiredModifiers: ["public", "virtual"]),
        new(PartialStoragePath, "PartialStorageProviderBase", "TryGetCachedValue", 2, ["_intraBlockCache.TryGetValue", "head.Value", "bytes = null"]),
        new(PartialStoragePath, "PartialStorageProviderBase", "TakeSnapshot", 1, ["_changes.Count - 1", "_transactionChangesSnapshots.Push"]),
        new(PartialStoragePath, "PartialStorageProviderBase", "Restore", 1, ["CollectionsMarshal.AsSpan", "CollectionsMarshal.SetCount"]),
        new(PartialStoragePath, "PartialStorageProviderBase", "PushUpdate", 2, ["GetValueRefOrAddDefault", "firstWriteThisTx", "_changes.Add"]),
        new(PersistentStoragePath, "PersistentStorageProvider", "CurrentScope", 0,
            ["_currentScope", "throw new InvalidOperationException"], Kind: MemberSyntaxKind.Property,
            ExactSignature: "IWorldStateScopeProvider.IScope CurrentScope", RequiredModifiers: ["private"]),
        new(PersistentStoragePath, "PersistentStorageProvider", "Set", 2,
            ["CurrentScope", "IncrementStorageWrites", "GetOrCreateStorage", "base.Set", "HintWarmSlot", "TakeAccountWarmHint", "HintWarmAccount"],
            ExactSignature: "void Set(in StorageCell,byte[])", RequiredModifiers: ["public", "override"]),
        new(PersistentStoragePath, "PersistentStorageProvider", "GetCurrentValue", 1, ["TryGetCachedValue", "LoadFromTree"]),
        new(PersistentStoragePath, "PersistentStorageProvider", "LoadFromTree", 1, ["GetOrCreateStorage", "LoadFromTree"]),
        new(PersistentStoragePath, "PersistentStorageProvider", "GetOrCreateStorage", 1, ["_lastStorageAddress", "GetValueRefOrAddDefault", "PerContractState.Rent"]),
        new(PersistentStoragePath, "PersistentStorageProvider", "CaptureOriginalValue", 2, ["GetValueRefOrAddDefault", "if (!exists)", "slot = value"]),
        new(PersistentStoragePath, "PerContractState", "LoadFromTree", 1, ["BlockChange.GetValueRefOrAddDefault", "CaptureOriginalValue", "WithCapturedRound", "return valueChange.After"]),
        new(PersistentStoragePath, "PerContractState", "LoadFromTreeStorage", 1, ["EnsureStorageTree", "_backend.Get"]),
        new(PersistentStoragePath, "PersistentStorageProvider", "GetOriginal", 1, ["_originalValues.TryGetValue", "_transactionChangesSnapshots.TryPeek", "head.OriginalIdx"]),
        new(PersistentStoragePath, "PersistentStorageProvider", "GetStorageRoot", 1, ["GetOrCreateStorage", "StorageRoot"]),
        new(PersistentStoragePath, "PerContractState", "StorageRoot", 0, ["EnsureStorageTree", "_backend.RootHash"], Kind: MemberSyntaxKind.Property),
        new(PersistentStoragePath, "PerContractState", "TakeAccountWarmHint", 0,
            ["if (_accountHinted)", "_accountHinted = true", "return true"], ExactSignature: "bool TakeAccountWarmHint()"),
        new(TransientStoragePath, "TransientStorageProvider", "GetCurrentValue", 1, ["TryGetCachedValue", "StorageTree.ZeroBytes"]),
        new(WorldStateScopeInterfacePath, "IScope", "HintWarmAccount", 1, [], "ValueAddress",
            ExactSignature: "void HintWarmAccount(in ValueAddress)", RequireEmptyBody: true),
        new(WorldStateScopeInterfacePath, "IScope", "HintWarmSlot", 2, [], "ValueAddress",
            ExactSignature: "void HintWarmSlot(in ValueAddress,in UInt256)", RequireEmptyBody: true),
        new(LocalMetricsPath, "LocalMetrics", "IncrementStorageWrites", 0,
            ["ExecutionMetricsFlag.IsActive", "StorageWrites++"], ExactSignature: "void IncrementStorageWrites()"),
        new(AccessTrackerPath, "StackAccessTracker", "WarmUp", 1, ["AccessedAddresses.Add"], "Address"),
        new(AccessTrackerPath, "StackAccessTracker", "WarmUp", 1, ["AccessedStorageCells.Add"], "StorageCell"),
        new(AccessTrackerPath, "StackAccessTracker", "ToBeDestroyed", 1, ["DestroyList.Add"]),
        new(AccessTrackerPath, "StackAccessTracker", "WasCreated", 1, ["CreateList.Add"]),
        new(AccessTrackerPath, "StackAccessTracker", "TakeSnapshot", 0, ["AccessedAddresses.TakeSnapshot", "AccessedStorageCells.TakeSnapshot", "DestroyList.TakeSnapshot", "Logs.TakeSnapshot"]),
        new(AccessTrackerPath, "StackAccessTracker", "Restore", 0, ["if (!_isTracingAccess)", "AccessedAddresses.Restore", "AccessedStorageCells.Restore", "DestroyList.Restore", "Logs.Restore"]),
        new(JournalSetPath, "JournalSet", "TakeSnapshot", 0, ["Position"]),
        new(JournalSetPath, "JournalSet", "Position", 0, ["Count - 1"], Kind: MemberSyntaxKind.Property),
        new(JournalSetPath, "JournalSet", "Restore", 1, ["CollectionsMarshal.AsSpan", "_set.Remove", "CollectionsMarshal.SetCount"]),
        new(JournalSetPath, "JournalSet", "Add", 1, ["_set.Add", "_items.Add"]),
        new(JournalSetPath, "JournalSet", "Count", 0, ["_set.Count"], Kind: MemberSyntaxKind.Property),
        new(JournalCollectionPath, "JournalCollection", "TakeSnapshot", 0, ["Count - 1"]),
        new(JournalCollectionPath, "JournalCollection", "Restore", 1, ["CollectionsMarshal.SetCount"]),
        new(JournalCollectionPath, "JournalCollection", "Add", 1, ["_list.Add"]),
        new(JournalCollectionPath, "JournalCollection", "Count", 0, ["_list.Count"], Kind: MemberSyntaxKind.Property),
        new(WorldStatePath, "WorldState", "TakeSnapshot", 1, ["_persistentStorageProvider.TakeSnapshot", "_transientStorageProvider.TakeSnapshot", "_stateProvider.TakeSnapshot"]),
        new(WorldStatePath, "WorldState", "Restore", 1, ["_persistentStorageProvider.Restore", "_transientStorageProvider.Restore", "_stateProvider.Restore"], "Snapshot"),
        new(VirtualMachinePath, "VirtualMachine", "AddLog", 1, ["AccessTracker.Logs.Add"]),
        new(VmStatePath, "VmState", "Initialize", 12, ["WasCreated", "TakeSnapshot"]),
        new(SnapshotPath, "Snapshot", "EmptyPosition", 0, ["-1"], Kind: MemberSyntaxKind.Field),
        new(ResettablePath, "Resettable", "EmptyPosition", 0, ["-1"], Kind: MemberSyntaxKind.Field),
        new(StorageTreePath, "StorageTree", "ZeroBytes", 0, ["[0]"], Kind: MemberSyntaxKind.Field),
        new(AccountPath, "Account", "Account", 0, ["_codeHash = null", "_storageRoot = null", "Nonce = default", "Balance = default"], Kind: MemberSyntaxKind.Constructor, ExactSignature: "Account()"),
        new(AccountPath, "Account", "Account", 2, ["_codeHash = null", "_storageRoot = null", "Nonce = nonce", "Balance = balance"], Kind: MemberSyntaxKind.Constructor, ExactSignature: "Account(in ulong,in UInt256)"),
        new(AccountPath, "Account", "Account", 2, ["_codeHash = account._codeHash", "_storageRoot = storageRoot", "Nonce = account.Nonce", "Balance = account.Balance"], Kind: MemberSyntaxKind.Constructor, ExactSignature: "Account(Account,Hash256?)"),
        new(AccountPath, "Account", "TotallyEmpty", 0, ["new()"], Kind: MemberSyntaxKind.Field),
        new(AccountPath, "Account", "StorageRoot", 0, ["_storageRoot", "Keccak.EmptyTreeHash"], Kind: MemberSyntaxKind.Property),
        new(AccountPath, "Account", "CodeHash", 0, ["_codeHash", "Keccak.OfAnEmptyString"], Kind: MemberSyntaxKind.Property),
        new(AccountPath, "Account", "IsTotallyEmpty", 0, ["_storageRoot is null", "IsEmpty"], Kind: MemberSyntaxKind.Property),
        new(AccountPath, "Account", "IsEmpty", 0, ["_codeHash is null", "Balance.IsZero", "Nonce == 0"], Kind: MemberSyntaxKind.Property),
        new(AccountPath, "Account", "WithChangedStorageRoot", 1, ["new(this, newStorageRoot)"]),
        new(AccountPath, "Account", "ToStruct", 0, ["Nonce", "Balance", "StorageRoot", "CodeHash"]),
        new(AccountPath, "AccountStruct", "AccountStruct", 4, ["_balance = balance", "_nonce = nonce", "CodeHash = codeHash", "_storageRoot = storageRoot"], Kind: MemberSyntaxKind.Constructor),
        new(AccountPath, "AccountStruct", "IsTotallyEmpty", 0, ["IsEmpty", "IsStorageEmpty"], Kind: MemberSyntaxKind.Property),
        new(AccountPath, "AccountStruct", "IsEmpty", 0, ["Balance.IsZero", "Nonce == 0", "Keccak.OfAnEmptyString"], Kind: MemberSyntaxKind.Property),
        new(AccountPath, "AccountStruct", "IsStorageEmpty", 0, ["Keccak.EmptyTreeHash"], Kind: MemberSyntaxKind.Property),
        new(KeccakPath, "ValueKeccak", "InternalCompute", 1, ["KeccakHash.ComputeHashBytesToSpan"]),
        new(KeccakPath, "Keccak", "OfAnEmptyString", 0, ["ValueKeccak.InternalCompute([])"], Kind: MemberSyntaxKind.Field),
        new(KeccakPath, "Keccak", "EmptyTreeHash", 0, ["ValueKeccak.InternalCompute([128])"], Kind: MemberSyntaxKind.Field),
    ];

    private static readonly string[] SemanticBindings =
    [
        "IWorldState exposes the admitted persistent, transient, account-create/delete, and snapshot boundary.",
        "WorldState.TryGetAccount plus HasEmptyAccountLeaf distinguishes absent, physical-empty, and storage-only account cases; StateProvider.GetThroughCache supplies the exact option.",
        "BlockProcessingModule closes IWorldState to WorldState and IVirtualMachine to EthereumVirtualMachine for standard mainnet execution.",
        "WorldState.TakeSnapshot orders persistent, transient, then account positions; WorldState.Restore applies the same three surfaces in that order.",
        "StackAccessTracker.Restore restores warm account/cell sets only when isTracingAccess is false, then always restores destroy and ordered log journals.",
        "StateProvider backing reads append raw JustCache entries; Restore retains and re-appends predecessor-free entries, so snapshot positions use the exact raw count while the semantic account projection stutters.",
        "Persistent first reads/writes retain transaction-start originals outside frame restoration; transient values use a distinct journal.",
        "StackAccessTracker.CreateList is a plain HashSet populated by excluded CREATE-frame initialization before its frame snapshot; the projection admits it as transaction-wide input and only claims restore preservation.",
        "JournalSet is idempotent and suffix-restored; JournalCollection is ordered and suffix-restored.",
        "Pinned Account and Keccak members close fresh-account construction to the canonical empty storage root and empty code hash represented by the Lean constants.",
        "Persistent writes resolve an active scope, increment metrics, register the contract, journal through the base Set and PushUpdate path, then issue slot and one-shot account warm hints; scope, metrics, and advisory hints are explicitly projected away.",
        "The generated kernel is pre-commit only and does not claim trie, state-root, database, code-repository, BAL, or tracing-access refinement.",
    ];

    internal static ExtractionResult Extract(string repoRoot, string outputDirectory, string? leanOutputPath = null)
    {
        string root = Path.GetFullPath(repoRoot);
        SourceFile[] sources = SourcePaths.Select(path => Read(root, path)).ToArray();
        ValidateScope(sources);

        MemberIdentity[] members = MemberExpectations
            .Select(expectation => AdmitMember(sources, expectation))
            .ToArray();
        ValidateFingerprints(sources);
        IrDocument document = BuildDocument();
        ValidateDocument(document);
        ValidateOperationBindings(document, members);

        byte[] irBytes = Serialize(document);
        string irHash = Sha256(irBytes);
        byte[] leanBytes = LeanEmitter.Emit(document, irHash);
        string output = Path.GetFullPath(outputDirectory);
        string leanPath = Path.GetFullPath(leanOutputPath ?? Path.Combine(root, DefaultLeanRelativePath));
        EnsureWithin(leanOutputPath is null ? root : output, leanPath);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(Path.GetDirectoryName(leanPath)!);
        string irPath = Path.Combine(output, IrFileName);
        string manifestPath = Path.Combine(output, ManifestFileName);
        Write(irPath, irBytes);
        Write(leanPath, leanBytes);

        SourceManifest manifest = BuildManifest(sources, members, irBytes, leanBytes);
        byte[] manifestBytes = Serialize(manifest);
        ValidateArtifacts(root, manifestBytes, irBytes, leanBytes);
        Write(manifestPath, manifestBytes);
        return new(irPath, manifestPath, leanPath, sources.Length, members.Length);
    }

    private static SourceManifest BuildManifest(
        SourceFile[] sources,
        MemberIdentity[] members,
        byte[] irBytes,
        byte[] leanBytes)
    {
        SourceIdentity[] sourceIdentities = sources
            .Select(source => new SourceIdentity(source.RelativePath, source.Sha256))
            .ToArray();
        return new(
            1,
            ExtractorVersion,
            CompilerVersion,
            LanguageVersion.CSharp14.ToDisplayString(),
            Kernel,
            sourceIdentities,
            members,
            new(IrFileName, Sha256(irBytes)),
            new(Normalize(DefaultLeanRelativePath), Sha256(leanBytes)),
            CombinedHash(sourceIdentities.Select(identity => $"{identity.Path}\0{identity.Sha256}")),
            CombinedHash(members.Select(member => $"{member.SourcePath}\0{member.Signature}\0{member.CanonicalSha256}")),
            SemanticBindings);
    }

    internal static IrDocument BuildDocument() => new(
        1,
        ExtractorVersion,
        Kernel,
        new(
            "BlockProcessingModule IWorldState -> WorldState",
            "normal execution with StackAccessTracker.isTracingAccess=false",
            "one active journal stack; nested snapshots are LIFO positions, not extracted VM recursion",
            "before WorldState.Commit / CommitTree / RecalculateStateRoot",
            [
                "ClearStorage", "code insertion and deposit", "MarkStorageDestroyed", "account reaping",
                "provider commit and tree/root computation", "TracedAccessWorldState",
                "BlockAccessListBasedWorldState and BAL snapshots",
                "WasCreated mutation and CREATE child initialization",
            ],
            [
                "Dictionary and HashSet implement extensional lookup and uniqueness for immutable keys.",
                "List and CollectionsMarshal suffix truncation preserves the admitted prefix and ordering.",
                "byte arrays admitted as storage values or log payloads are not mutated after admission.",
                "Address, UInt256, Hash256, and nonce values satisfy their production fixed-width domains.",
                "Initial account and persistent maps agree with the backing tree and storage backend at trace entry.",
                "Pinned Keccak empty-input computations equal the explicit Lean empty-root and empty-code constants.",
                "Persistent writes execute inside an active scope; metrics and advisory slot/account warm hints do not mutate the admitted journal projection.",
            ]),
        new(
            ["persistent", "transient", "accounts", "warmAccounts", "warmCells", "destroySet", "logs"],
            ["persistentOriginals", "createdThisTx"],
            "production stores Count - 1 with -1 as the empty position; restoring truncates each surface independently",
            "Lean uses the successor encoding: Nat position = production position + 1 = retained journal length",
            "persistent, transient, accounts, warmAccounts, warmCells, destroySet, logs"),
        Operations());

    internal static void ValidateSerializedIr(byte[] bytes)
    {
        RejectDuplicateJsonProperties(bytes, "IR");
        IrDocument document = JsonSerializer.Deserialize<IrDocument>(bytes, JsonOptions)
            ?? throw new ExtractionException("The serialized world-journal IR was empty.");
        ValidateDocument(document);
        if (!Serialize(document).AsSpan().SequenceEqual(bytes))
        {
            throw new ExtractionException("The serialized world-journal IR is not canonical.");
        }
    }

    internal static SourceManifest DeserializeManifest(byte[] bytes)
    {
        RejectDuplicateJsonProperties(bytes, "manifest");
        SourceManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SourceManifest>(bytes, JsonOptions)
                ?? throw new ExtractionException("The serialized world-journal manifest was empty.");
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"The serialized world-journal manifest is invalid: {exception.Message}");
        }

        if (manifest.SchemaVersion != 1 || manifest.ExtractorVersion != ExtractorVersion ||
            manifest.CompilerVersion != CompilerVersion || manifest.Kernel != Kernel ||
            manifest.LanguageVersion != LanguageVersion.CSharp14.ToDisplayString())
        {
            throw new ExtractionException("The world-journal manifest header changed.");
        }
        RequireText(manifest.ExtractorVersion, "extractor version");
        RequireText(manifest.CompilerVersion, "compiler version");
        RequireText(manifest.LanguageVersion, "language version");
        RequireText(manifest.Kernel, "kernel");

        if (manifest.Sources is null || manifest.Members is null || manifest.Ir is null || manifest.Lean is null ||
            manifest.SemanticBindings is null || manifest.Sources.Any(source => source is null) ||
            manifest.Members.Any(member => member is null) || manifest.SemanticBindings.Any(binding => binding is null))
        {
            throw new ExtractionException("The world-journal manifest contains null collections or entries.");
        }
        if (!manifest.Sources.Select(source => source.Path).SequenceEqual(SourcePaths, StringComparer.Ordinal) ||
            manifest.Members.Length != MemberExpectations.Length)
        {
            throw new ExtractionException("The world-journal manifest source or member closure changed.");
        }
        foreach (SourceIdentity source in manifest.Sources) RequireSha(source.Sha256, $"source hash for {source.Path}");
        foreach (MemberIdentity member in manifest.Members)
        {
            if (string.IsNullOrWhiteSpace(member.SourcePath) || string.IsNullOrWhiteSpace(member.ContainingType) ||
                string.IsNullOrWhiteSpace(member.Member) || string.IsNullOrWhiteSpace(member.Signature))
            {
                throw new ExtractionException("The world-journal manifest contains a blank member field.");
            }
            RequireSha(member.CanonicalSha256, $"member hash for {member.Signature}");
        }
        for (int index = 0; index < MemberExpectations.Length; index++)
        {
            MemberExpectation expected = MemberExpectations[index];
            MemberIdentity actual = manifest.Members[index];
            if (actual.SourcePath != expected.Path || actual.ContainingType != expected.Type || actual.Member != expected.Member)
            {
                throw new ExtractionException($"The world-journal member admission at index {index} changed.");
            }
        }
        RequireUnique(manifest.Sources.Select(source => source.Path), "source path");
        RequireUnique(manifest.Members.Select(member => $"{member.SourcePath}:{member.ContainingType}:{member.Signature}"), "member admission");
        RequireSha(manifest.CombinedSourceSha256, "combined source hash");
        RequireSha(manifest.CombinedMemberSha256, "combined member hash");
        RequireSha(manifest.Ir.Sha256, "IR hash");
        RequireSha(manifest.Lean.Sha256, "Lean hash");
        if (manifest.Ir.Path != IrFileName || manifest.Lean.Path != Normalize(DefaultLeanRelativePath) ||
            manifest.CombinedSourceSha256 != CombinedHash(manifest.Sources.Select(identity => $"{identity.Path}\0{identity.Sha256}")) ||
            manifest.CombinedMemberSha256 != CombinedHash(manifest.Members.Select(member => $"{member.SourcePath}\0{member.Signature}\0{member.CanonicalSha256}")))
        {
            throw new ExtractionException("The world-journal manifest artifact paths or aggregate hashes changed.");
        }
        if (!manifest.SemanticBindings.SequenceEqual(SemanticBindings, StringComparer.Ordinal))
        {
            throw new ExtractionException("The world-journal semantic bindings changed.");
        }

        return manifest;
    }

    internal static void ValidateSerializedManifest(byte[] bytes)
    {
        SourceManifest manifest = DeserializeManifest(bytes);
        if (!Serialize(manifest).AsSpan().SequenceEqual(bytes))
        {
            throw new ExtractionException("The serialized world-journal manifest is not canonical.");
        }
    }

    internal static void ValidateSerializedManifest(byte[] sourceDerived, byte[] candidate)
    {
        ValidateSerializedManifest(sourceDerived);
        ValidateSerializedManifest(candidate);
        if (!sourceDerived.AsSpan().SequenceEqual(candidate))
        {
            throw new ExtractionException("The candidate world-journal manifest differs from current source-derived output.");
        }
    }

    internal static void ValidateArtifacts(
        string repoRoot,
        byte[] manifestBytes,
        byte[] irBytes,
        byte[] leanBytes)
    {
        ValidateSerializedIr(irBytes);
        ValidateSerializedManifest(manifestBytes);
        string root = Path.GetFullPath(repoRoot);
        SourceFile[] sources = SourcePaths.Select(path => Read(root, path)).ToArray();
        ValidateScope(sources);
        ValidateFingerprints(sources);
        MemberIdentity[] members = MemberExpectations
            .Select(expectation => AdmitMember(sources, expectation))
            .ToArray();
        IrDocument document = BuildDocument();
        ValidateOperationBindings(document, members);
        byte[] expectedLean = LeanEmitter.Emit(document, Sha256(irBytes));
        ValidateGeneratedLean(leanBytes, expectedLean);
        SourceManifest expectedManifest = BuildManifest(sources, members, irBytes, leanBytes);
        byte[] expectedManifestBytes = Serialize(expectedManifest);
        if (!manifestBytes.AsSpan().SequenceEqual(expectedManifestBytes))
        {
            throw new ExtractionException("The world-journal manifest is not the exact independently recomputed source and artifact identity.");
        }
    }

    private static OperationDescriptor[] Operations() =>
    [
        new("readAccount", "accounts", "physical Option account; a backing hit journals raw JustCache while the semantic projection stutters", true, StateProviderPath, "StateProvider", "GetThroughCache", "Account? GetThroughCache(Address)", ["frontCache", "journalHead", "backingState", "pushJustCache"]),
        new("createAccount", "accounts", "none", true, StateProviderPath, "StateProvider", "CreateAccount", "void CreateAccount(Address,in UInt256,in ulong)", ["constructPhysicalAccount", "pushNew"]),
        new("updateAccount", "accounts", "none", true, StateProviderPath, "StateProvider", "PushUpdate", "void PushUpdate(Address,Account)", ["pushUpdate"]),
        new("deleteAccount", "accounts", "none", true, StateProviderPath, "StateProvider", "DeleteAccount", "void DeleteAccount(Address)", ["pushDelete"]),
        new("readPersistent", "persistentOriginals", "current bytes and transaction original", false, PartialStoragePath, "PartialStorageProviderBase", "Get", "ReadOnlySpan<byte> Get(in StorageCell)", ["cacheOrTreeRead", "captureOriginalOnce"]),
        new("writePersistent", "persistent", "none; active-scope, metrics, and advisory warm-hint effects are projected away; standard EVM traces read before write", true, PersistentStoragePath, "PersistentStorageProvider", "Set", "void Set(in StorageCell,byte[])", ["resolveActiveScope", "incrementMetrics", "registerContract", "baseSet", "pushUpdate", "hintSlot", "hintAccountOnce"]),
        new("readTransient", "transient", "current bytes; missing is zero", false, TransientStoragePath, "TransientStorageProvider", "GetCurrentValue", "ReadOnlySpan<byte> GetCurrentValue(in StorageCell)", ["cacheRead", "zeroDefault"]),
        new("writeTransient", "transient", "none", true, PartialStoragePath, "PartialStorageProviderBase", "Set", "void Set(in StorageCell,byte[])", ["pushUpdate", "replaceHead"]),
        new("warmAccount", "warmAccounts", "true only on first insertion", true, AccessTrackerPath, "StackAccessTracker", "WarmUp", "bool WarmUp(Address)", ["setAdd", "journalAppendIfNew"]),
        new("warmCell", "warmCells", "true only on first insertion", true, AccessTrackerPath, "StackAccessTracker", "WarmUp", "bool WarmUp(in StorageCell)", ["setAdd", "journalAppendIfNew"]),
        new("appendLog", "logs", "none", true, VirtualMachinePath, "VirtualMachine", "AddLog", "void AddLog(LogEntry)", ["orderedAppend"]),
        new("addDestroy", "destroySet", "none", true, AccessTrackerPath, "StackAccessTracker", "ToBeDestroyed", "void ToBeDestroyed(Address)", ["setAdd", "journalAppendIfNew"]),
        new("takeSnapshot", "snapshotStack", "seven independent positions", false, WorldStatePath, "WorldState", "TakeSnapshot", "Snapshot TakeSnapshot(bool)", ["persistent", "transient", "accounts", "trackerPositions"]),
        new("restoreSnapshot", "snapshotStack", "failure when no snapshot exists", false, WorldStatePath, "WorldState", "Restore", "void Restore(Snapshot)", ["persistent", "transient", "accounts", "warmAccounts", "warmCells", "destroySet", "logs"]),
    ];

    private static void ValidateDocument(IrDocument document)
    {
        IrDocument expected = BuildDocument();
        byte[] actual = Serialize(document);
        byte[] expectedBytes = Serialize(expected);
        if (!actual.AsSpan().SequenceEqual(expectedBytes))
        {
            throw new ExtractionException("The world-journal IR schema, scope, operation order, or operation fields changed.");
        }

        RequireUnique(document.Operations.Select(operation => operation.Name), "operation");
        if (document.Operations.Length != 14 || document.Snapshot.RestoredSurfaces.Length != 7 ||
            document.Snapshot.TransactionWideSurfaces.Length != 2 || document.Scope.Exclusions.Length != 8 ||
            document.Scope.UnextractedPremises.Length != 7)
        {
            throw new ExtractionException("The world-journal IR is not field-complete.");
        }
    }

    private static void ValidateOperationBindings(IrDocument document, MemberIdentity[] members)
    {
        foreach (OperationDescriptor operation in document.Operations)
        {
            int matches = members.Count(member =>
                member.SourcePath == operation.SourcePath &&
                member.ContainingType == operation.ContainingType &&
                member.Member == operation.Member &&
                member.Signature == operation.Signature);
            if (matches != 1)
            {
                throw new ExtractionException($"Operation {operation.Name} is not bound to one exact admitted source member.");
            }
        }

        ValidatePersistentWriteReachability(document, members);
    }

    private static void ValidatePersistentWriteReachability(IrDocument document, MemberIdentity[] members)
    {
        OperationDescriptor write = document.Operations.Single(operation => operation.Name == "writePersistent");
        if (write.SourcePath != PersistentStoragePath || write.ContainingType != "PersistentStorageProvider" ||
            write.Member != "Set" || write.Signature != "void Set(in StorageCell,byte[])")
        {
            throw new ExtractionException("writePersistent is not rooted at the concrete persistent-storage override.");
        }

        (string Path, string Type, string Member, string Signature)[] chain =
        [
            (WorldStatePath, "WorldState", "Set", "void Set(in StorageCell,byte[])"),
            (PersistentStoragePath, "PersistentStorageProvider", "Set", "void Set(in StorageCell,byte[])"),
            (PartialStoragePath, "PartialStorageProviderBase", "Set", "void Set(in StorageCell,byte[])"),
            (PartialStoragePath, "PartialStorageProviderBase", "PushUpdate", "void PushUpdate(in StorageCell,byte[])"),
        ];
        foreach ((string path, string type, string member, string signature) in chain)
        {
            int matches = members.Count(identity => identity.SourcePath == path &&
                identity.ContainingType == type && identity.Member == member && identity.Signature == signature);
            if (matches != 1)
            {
                throw new ExtractionException($"Persistent write reachability lost exact member {type}.{signature}.");
            }
        }
    }

    private static MemberIdentity AdmitMember(SourceFile[] sources, MemberExpectation expectation)
    {
        SourceFile source = sources.Single(candidate => candidate.RelativePath == expectation.Path);
        IEnumerable<MemberDeclarationSyntax> candidates = source.Root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(type => type.Identifier.ValueText == expectation.Type)
            .SelectMany(type => type.Members)
            .Where(member => MatchesMember(member, expectation));
        if (expectation.ParameterType is not null)
        {
            candidates = candidates.Where(member => Parameters(member)
                .Any(parameter => parameter.Type?.ToString() == expectation.ParameterType));
        }
        if (expectation.ExactSignature is not null)
        {
            candidates = candidates.Where(member =>
                CanonicalSignature(member, expectation.Member) == expectation.ExactSignature);
        }
        if (expectation.RequiredModifiers is not null)
        {
            candidates = candidates.Where(member => expectation.RequiredModifiers.All(required =>
                Modifiers(member).Any(modifier => modifier.ValueText == required)));
        }
        if (expectation.RequireEmptyBody)
        {
            candidates = candidates.Where(member => member is MethodDeclarationSyntax method &&
                method.Body is { Statements.Count: 0 } && method.ExpressionBody is null);
        }

        MemberDeclarationSyntax[] matches = candidates.ToArray();
        if (matches.Length != 1)
        {
            throw new ExtractionException($"Expected one exact {expectation.Kind} member {expectation.Type}.{expectation.Member}/{expectation.ParameterCount} in {expectation.Path}, found {matches.Length}.");
        }

        MemberDeclarationSyntax member = matches[0];
        string canonical = member.WithoutTrivia().NormalizeWhitespace().ToFullString();
        int cursor = 0;
        foreach (string effect in expectation.OrderedEffects)
        {
            int position = canonical.IndexOf(effect, cursor, StringComparison.Ordinal);
            if (position < 0)
            {
                throw new ExtractionException($"Member {expectation.Type}.{expectation.Member} lost ordered effect '{effect}'.");
            }
            cursor = position + effect.Length;
        }

        string signature = CanonicalSignature(member, expectation.Member);
        return new(expectation.Path, expectation.Type, expectation.Member, signature, Sha256(Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool MatchesMember(MemberDeclarationSyntax member, MemberExpectation expectation) =>
        (expectation.Kind, member) switch
        {
            (MemberSyntaxKind.Method, MethodDeclarationSyntax method) =>
                method.Identifier.ValueText == expectation.Member &&
                method.ExplicitInterfaceSpecifier is null &&
                method.ParameterList.Parameters.Count == expectation.ParameterCount,
            (MemberSyntaxKind.Constructor, ConstructorDeclarationSyntax constructor) =>
                constructor.Identifier.ValueText == expectation.Member &&
                constructor.ParameterList.Parameters.Count == expectation.ParameterCount,
            (MemberSyntaxKind.Property, PropertyDeclarationSyntax property) =>
                property.Identifier.ValueText == expectation.Member &&
                property.ExplicitInterfaceSpecifier is null,
            (MemberSyntaxKind.Field, FieldDeclarationSyntax field) =>
                field.Declaration.Variables.Any(variable => variable.Identifier.ValueText == expectation.Member),
            _ => false,
        };

    private static IEnumerable<ParameterSyntax> Parameters(MemberDeclarationSyntax member) =>
        member switch
        {
            MethodDeclarationSyntax method => method.ParameterList.Parameters,
            ConstructorDeclarationSyntax constructor => constructor.ParameterList.Parameters,
            _ => [],
        };

    private static SyntaxTokenList Modifiers(MemberDeclarationSyntax member) =>
        member switch
        {
            MethodDeclarationSyntax method => method.Modifiers,
            ConstructorDeclarationSyntax constructor => constructor.Modifiers,
            PropertyDeclarationSyntax property => property.Modifiers,
            FieldDeclarationSyntax field => field.Modifiers,
            _ => default,
        };

    private static string CanonicalSignature(MemberDeclarationSyntax member, string memberName) =>
        member switch
        {
            MethodDeclarationSyntax method =>
                $"{method.ReturnType} {method.Identifier.ValueText}({CanonicalParameters(method.ParameterList.Parameters)})",
            ConstructorDeclarationSyntax constructor =>
                $"{constructor.Identifier.ValueText}({CanonicalParameters(constructor.ParameterList.Parameters)})",
            PropertyDeclarationSyntax property => $"{property.Type} {property.Identifier.ValueText}",
            FieldDeclarationSyntax field => $"{field.Declaration.Type} {memberName}",
            _ => throw new ExtractionException($"Unsupported admitted member syntax for {memberName}."),
        };

    private static string CanonicalParameters(SeparatedSyntaxList<ParameterSyntax> parameters) =>
        string.Join(",", parameters.Select(parameter =>
        {
            string modifiers = string.Join(" ", parameter.Modifiers.Select(modifier => modifier.ValueText));
            return modifiers.Length == 0 ? parameter.Type!.ToString() : $"{modifiers} {parameter.Type}";
        }));

    private static void ValidateScope(SourceFile[] sources)
    {
        string mainnet = Get(sources, MainnetDiPath).Text;
        Require(mainnet, ".AddScoped<IWorldState, WorldState>()", "standard mainnet IWorldState binding");
        Require(mainnet, ".AddScoped<IVirtualMachine, EthereumVirtualMachine>()", "standard mainnet EVM binding");

        string worldState = Get(sources, WorldStatePath).Text;
        Require(worldState, "_persistentStorageProvider.TakeSnapshot", "persistent snapshot");
        Require(worldState, "_transientStorageProvider.TakeSnapshot", "transient snapshot");
        Require(worldState, "new Snapshot(storageSnapshot, stateSnapshot, -1)", "non-BAL snapshot position");

        string worldStateInterface = Get(sources, WorldStateInterfacePath).Text;
        Require(worldStateInterface, "ReadOnlySpan<byte> Get(in StorageCell storageCell);", "persistent read interface");
        Require(worldStateInterface, "void Set(in StorageCell storageCell, byte[] newValue);", "persistent write interface");
        Require(worldStateInterface, "ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell);", "transient read interface");
        Require(worldStateInterface, "void SetTransientState(in StorageCell storageCell, byte[] newValue);", "transient write interface");
        Require(worldStateInterface, "Snapshot TakeSnapshot(bool newTransactionStart = false);", "snapshot interface");
        Require(worldStateInterface, "void DeleteAccount(Address address);", "delete-account interface");
        Require(worldStateInterface, "void CreateAccount(Address address, in UInt256 balance, in ulong nonce = default);", "create-account interface");
        Require(worldStateInterface, "bool HasEmptyAccountLeaf(Address address)", "physical empty-account interface");

        string tracker = Get(sources, AccessTrackerPath).Text;
        Require(tracker, "if (!_isTracingAccess)", "normal access restore gate");
        Require(tracker, "public readonly HashSet<AddressAsKey> CreateList", "transaction-wide create set");
        string vmState = Get(sources, VmStatePath).Text;
        RequireOrdered(vmState, ["_accessTracker.WasCreated", "_accessTracker.TakeSnapshot"], "created-before-snapshot order");

        string account = Get(sources, AccountPath).Text;
        Require(account, "IsTotallyEmpty", "physical-empty discriminator");
        Require(account, "StorageRoot", "account storage root");
        Require(account, "CodeHash", "account code hash");

        Require(Get(sources, SnapshotPath).Text, "public const int EmptyPosition = -1", "world snapshot empty position");
        Require(Get(sources, ResettablePath).Text, "public const int EmptyPosition = -1", "storage empty position");
        Require(Get(sources, StorageTreePath).Text, "public static readonly byte[] ZeroBytes = [0]", "storage zero bytes");
        string keccak = Get(sources, KeccakPath).Text;
        Require(keccak, "0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470", "empty-code digest");
        Require(keccak, "0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421", "empty-storage-root digest");
        Require(keccak, "OfAnEmptyString = new(ValueKeccak.InternalCompute([]))", "empty-code construction");
        Require(keccak, "EmptyTreeHash = new(ValueKeccak.InternalCompute([128]))", "empty-storage-root construction");
    }

    private static void ValidateFingerprints(SourceFile[] sources)
    {
        Dictionary<string, string> expected = ExpectedFingerprintText
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(' ', 2, StringSplitOptions.TrimEntries))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        if (expected.Count != SourcePaths.Length)
        {
            throw new ExtractionException("The pinned source fingerprint table is incomplete.");
        }

        foreach (SourceFile source in sources)
        {
            if (!expected.TryGetValue(source.RelativePath, out string? hash) || source.Sha256 != hash)
            {
                throw new ExtractionException($"Pinned source changed: {source.RelativePath}. Expected {hash ?? "<missing>"}, got {source.Sha256}.");
            }
        }
    }

    private static SourceFile Read(string root, string relativePath)
    {
        string path = Path.GetFullPath(Path.Combine(root, relativePath));
        EnsureWithin(root, path);
        if (!File.Exists(path))
        {
            throw new ExtractionException($"Required source does not exist: {relativePath}.");
        }
        byte[] bytes = File.ReadAllBytes(path);
        string text = Encoding.UTF8.GetString(bytes);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(text, ParseOptions, path);
        Diagnostic[] errors = tree.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0)
        {
            throw new ExtractionException($"Source {relativePath} does not parse as C# 14: {errors[0]}.");
        }
        return new(relativePath, text, tree.GetCompilationUnitRoot(), Sha256(bytes));
    }

    private static SourceFile Get(SourceFile[] sources, string path) =>
        sources.Single(source => source.RelativePath == path);

    private static void Require(string text, string token, string label)
    {
        if (!text.Contains(token, StringComparison.Ordinal))
        {
            throw new ExtractionException($"Missing {label}: '{token}'.");
        }
    }

    private static void RequireOrdered(string text, string[] tokens, string label)
    {
        int cursor = 0;
        foreach (string token in tokens)
        {
            int position = text.IndexOf(token, cursor, StringComparison.Ordinal);
            if (position < 0) throw new ExtractionException($"Missing {label} token '{token}'.");
            cursor = position + token.Length;
        }
    }

    private static void RequireUnique(IEnumerable<string> values, string label)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || !seen.Add(value))
            {
                throw new ExtractionException($"The {label} list contains a blank or duplicate entry.");
            }
        }
    }

    private static void RejectDuplicateJsonProperties(byte[] bytes, string label)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        Visit(document.RootElement);
        return;

        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                HashSet<string> names = new(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw new ExtractionException($"The serialized world-journal {label} contains duplicate property '{property.Name}'.");
                    }
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray()) Visit(item);
            }
        }
    }

    private static void ValidateGeneratedLean(byte[] candidate, byte[] expected)
    {
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(candidate);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ExtractionException($"The generated Lean artifact is not strict UTF-8: {exception.Message}");
        }

        if (candidate.Length >= 3 && candidate[0] == 0xef && candidate[1] == 0xbb && candidate[2] == 0xbf ||
            text.Contains('\r') ||
            !text.EndsWith('\n') || text.EndsWith("\n\n", StringComparison.Ordinal))
        {
            throw new ExtractionException("The generated Lean artifact must be BOM-free LF-only UTF-8 with one final LF.");
        }
        if (Regex.IsMatch(text, @"\b(theorem|axiom|example|sorry|admit)\b", RegexOptions.CultureInvariant))
        {
            throw new ExtractionException("The generated Lean artifact contains a forbidden proof declaration or placeholder.");
        }
        if (!candidate.AsSpan().SequenceEqual(expected))
        {
            throw new ExtractionException("The generated Lean artifact differs from exact semantic IR emission.");
        }
    }

    private static void RequireText(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ExtractionException($"The {label} is blank.");
        }
    }

    private static void RequireSha(string value, string label)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 64 ||
            value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ExtractionException($"The {label} is not lowercase SHA-256.");
        }
    }

    private static byte[] Serialize<T>(T value) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions).ReplaceLineEndings("\n") + "\n");

    private static string CombinedHash(IEnumerable<string> values) =>
        Sha256(Encoding.UTF8.GetBytes(string.Join('\n', values) + "\n"));

    internal static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static void Write(string path, byte[] bytes)
    {
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
        {
            File.WriteAllBytes(path, bytes);
        }
    }

    private static void EnsureWithin(string root, string path)
    {
        string canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ExtractionException($"Path escapes the admitted root: {path}.");
        }
    }

    private sealed record SourceFile(string RelativePath, string Text, CompilationUnitSyntax Root, string Sha256);

    private enum MemberSyntaxKind
    {
        Method,
        Constructor,
        Property,
        Field,
    }

    private sealed record MemberExpectation(
        string Path,
        string Type,
        string Member,
        int ParameterCount,
        string[] OrderedEffects,
        string? ParameterType = null,
        MemberSyntaxKind Kind = MemberSyntaxKind.Method,
        string? ExactSignature = null,
        string[]? RequiredModifiers = null,
        bool RequireEmptyBody = false);
}
