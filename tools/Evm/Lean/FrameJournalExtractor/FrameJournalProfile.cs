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

namespace Nethermind.Evm.Lean.FrameJournalExtractor;

internal static class FrameJournalProfile
{
    internal const string ExtractorVersion = "1.0.0";
    internal const string IrFileName = "FrameJournalKernel.ir.json";
    internal const string ManifestFileName = "FrameJournalKernel.source-manifest.json";
    internal const string DefaultLeanRelativePath = "Generated/FrameJournalKernel.lean";

    internal const string VirtualMachinePath = "src/Nethermind/Nethermind.Evm/VirtualMachine.cs";
    internal const string CallResultPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.CallResult.cs";
    internal const string VmStatePath = "src/Nethermind/Nethermind.Evm/VmState.cs";
    internal const string CallPath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.cs";
    internal const string CreatePath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Create.cs";
    internal const string ControlFlowPath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.ControlFlow.cs";
    internal const string WorldStatePath = "src/Nethermind/Nethermind.State/WorldState.cs";
    internal const string AccessTrackerPath = "src/Nethermind/Nethermind.Evm/StackAccessTracker.cs";
    internal const string SnapshotPath = "src/Nethermind/Nethermind.Evm/State/Snapshot.cs";
    internal const string VirtualMachineDispatchPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs";
    internal const string VirtualMachineExecutionHandlersPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.ExecutionHandlers.cs";
    internal const string BuildTargetsPath = "src/Nethermind/Directory.Build.targets";
    internal const string SpecFlagsPath = "src/Nethermind/Nethermind.Evm/SpecFlags.std.cs";
    internal const string ExecutionTypePath = "src/Nethermind/Nethermind.Evm/ExecutionType.cs";
    internal const string AmsterdamPath = "src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs";
    internal const string MainnetSpecProviderPath = "src/Nethermind/Nethermind.Specs/MainnetSpecProvider.cs";
    internal const string BlockProcessingModulePath = "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs";
    internal const string TransactionProcessorPath = "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs";
    internal const string AccountPath = "src/Nethermind/Nethermind.Core/Account.cs";
    internal const string StateProviderPath = "src/Nethermind/Nethermind.State/StateProvider.cs";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly string[] SourcePaths =
    [
        VirtualMachinePath,
        CallResultPath,
        VmStatePath,
        CallPath,
        CreatePath,
        ControlFlowPath,
        WorldStatePath,
        AccessTrackerPath,
        SnapshotPath,
        VirtualMachineDispatchPath,
        VirtualMachineExecutionHandlersPath,
        BuildTargetsPath,
        SpecFlagsPath,
        ExecutionTypePath,
        AmsterdamPath,
        MainnetSpecProviderPath,
        BlockProcessingModulePath,
        TransactionProcessorPath,
        AccountPath,
        StateProviderPath,
    ];

    internal static IReadOnlyList<string> SourceRelativePaths => SourcePaths;

    internal static IReadOnlyList<string> DependencyRelativePaths =>
        [.. DependencyPins.SelectMany(pin => new[] { pin.IdentityPath, pin.ProofPath })
            .Concat(ImportedKernelPins.Select(pin => pin.ArtifactPath)).Distinct(StringComparer.Ordinal)];

    private const string ExpectedFingerprints = """
        src/Nethermind/Nethermind.Evm/VirtualMachine.cs 45edba3691e09185e749485785990662ddea1af849bc6e4564f92b68a5137a6b
        src/Nethermind/Nethermind.Evm/VirtualMachine.CallResult.cs d21bebbb4caea938c42d369083d922f6e18d01eb797b1c534fb1541c242ca073
        src/Nethermind/Nethermind.Evm/VmState.cs 7b7b6ddb753118b426d14976a3fe8e79f808320d6a09ea9cace9df19197f9f23
        src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.cs cfc87832eb304f8f8dccfb27b6b89605f6c74e88522f02b13c775834d14948a5
        src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Create.cs 23927529d13488932ec47b57972ab7084566a28deae83805d69a1fe977576719
        src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.ControlFlow.cs 282ba75f60c1ddd03fd51460b0e3273c0113430d02faee3abff991fe5f920c6c
        src/Nethermind/Nethermind.State/WorldState.cs 2dec74bcc5748a1850d1bed1e8fe0221aed8c904bf2ba0c0827e76e603a71f18
        src/Nethermind/Nethermind.Evm/StackAccessTracker.cs ed5cdfdfe2ebf4d8c9f651572927efe7bdb5c75107db63a40577c5ec3ec2882f
        src/Nethermind/Nethermind.Evm/State/Snapshot.cs 47625d60a8315bbc3afd26eca85780b0ab894d7401d913cbec00ed89b51f46c8
        src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs 4f36bb20057caec9c85bcd3621372563f4d47ba36a9cbf01be267af4e379bca1
        src/Nethermind/Nethermind.Evm/VirtualMachine.ExecutionHandlers.cs 62714bd459a38cc13e7b7bd2d5cfc7bc2ffdcb50743eeb565fdc2ee2966e286a
        src/Nethermind/Directory.Build.targets 0598cebaef1df41102a18a3f9ace810bed1e4e64b8471d055724b4a394dccec6
        src/Nethermind/Nethermind.Evm/SpecFlags.std.cs 653891cfe2289e874dcdd98c9401b3b319637d5241fd9a4d6cd7ef1c146ee5a8
        src/Nethermind/Nethermind.Evm/ExecutionType.cs b6689c7c928182f6d115ca57cc8ac45bb9312359a026704f2eec77b921028013
        src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs 4dfbf9079e9dce30423327ae4433f32e49388d403a8fedfcf253f94f79450c6a
        src/Nethermind/Nethermind.Specs/MainnetSpecProvider.cs ef89706a6ec327c5975a1ccb0e23930e774ef82a24b43a45053f7b021ddcb3b8
        src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs fce65ce5bb523c56fc8aa6ee0e4d092c940a5a90b174c820ffe262eeb5241c60
        src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs 0374c6f35a9a23361f37b41162ac281ca83b09846ab4295d5a592db6315c99cc
        src/Nethermind/Nethermind.Core/Account.cs 3a94d972388a4c5d46d744bf0733b80fb6575a148bf32123d64a0a51abddd749
        src/Nethermind/Nethermind.State/StateProvider.cs 93eab6702f4e3ef6c52afb0f595ff2b0c17cdeabbce1de3f0507f2aa0e6f6858
        """;

    private static readonly MemberExpectation[] MemberExpectations =
    [
        new(VirtualMachinePath, "VirtualMachine", "ExecuteTransaction", 3,
            ["_shouldRestoreRipemdTouch = false", "PrepareNextCallFrame", "HandleException", "_stateStack.Pop", "CommitToParent", "HandleRevert", "HandleFailure"],
            "TransactionSubstate ExecuteTransaction`1(VmState<TGasPolicy>,IWorldState,ITxTracer)", 1),
        new(VirtualMachinePath, "VirtualMachine", "PrepareNextCallFrame", 2,
            ["_stateStack.Push(_currentState)", "_currentState = callResult.StateToExecute", "_previousCallResult = default"],
            "void PrepareNextCallFrame(in CallResult,ref nuint)"),
        new(VirtualMachinePath, "VirtualMachine", "HandleRevert", 3,
            ["_worldState.Restore(previousState.Snapshot)", "RestoreRipemdTouch", "ReturnDataBuffer = outputBytes", "_previousCallResult = (null, false)"],
            "void HandleRevert(VmState<TGasPolicy>,in CallResult,ref nuint)"),
        new(VirtualMachinePath, "VirtualMachine", "HandleFailure", 4,
            ["_worldState.Restore(_currentState.Snapshot)", "RestoreRipemdTouch", "_currentState.IsTopLevel", "PopAndRestoreParentState"],
            "TransactionSubstate HandleFailure`1(Exception,string?,scoped ref nuint,out bool)", 1),
        new(VirtualMachinePath, "VirtualMachine", "HandleException", 3,
            ["_worldState.Restore(_currentState.Snapshot)", "RestoreRipemdTouch", "_currentState.IsTopLevel", "PopAndRestoreParentState"],
            "TransactionSubstate HandleException(scoped in CallResult,scoped ref nuint,out bool)"),
        new(VirtualMachinePath, "VirtualMachineStatics", "RestoreRipemdTouch", 3,
            ["shouldRestore", "worldState.AccountExists(Ripemd160Address)", "worldState.AddToBalance(Ripemd160Address, UInt256.Zero, spec)"],
            "void RestoreRipemdTouch(IWorldState,IReleaseSpec,bool)"),
        new(VirtualMachinePath, "VirtualMachine", "RunPrecompile", 1,
            ["AddToBalanceAndCreateIfNotExists", "!wasCreated", "transferValue.IsZero", "Eip158.IsActive", "Ripemd160Address", "IsDeadAccount", "_shouldRestoreRipemdTouch = true"],
            "CallResult RunPrecompile`1(VmState<TGasPolicy>)", 1),
        new(VirtualMachinePath, "VirtualMachine", "RunPrecompile", 1,
            ["GetExecutionHandlers().RunPrecompile(this, state)"],
            "CallResult RunPrecompile(VmState<TGasPolicy>)"),
        new(VirtualMachinePath, "VirtualMachine", "PopAndRestoreParentState", 0,
            ["VmState<TGasPolicy> childState = _currentState", "_currentState = _stateStack.Pop", "childState.Dispose"],
            "void PopAndRestoreParentState()"),
        new(VirtualMachinePath, "VirtualMachine", "AddLog", 1,
            ["VmState.AccessTracker.Logs.Add", "DispatchFlags.ConstTracing && TxTracer.IsTracingLogs", "TxTracer.ReportLog"],
            "void AddLog(LogEntry)"),
        new(VirtualMachinePath, "VirtualMachine", "AddSelfDestructLog", 3,
            ["executingAccount == inheritor", "AddLog", "TransferLog.CreateBurn", "AddLog", "TransferLog.CreateTransfer"],
            "void AddSelfDestructLog`2(Address,Address,in UInt256)", 2),
        new(CallResultPath, "CallResult", "IsReturn", 0,
            ["StateToExecute is null"], "bool IsReturn", Kind: MemberSyntaxKind.Property),
        new(CallResultPath, "CallResult", "IsException", 0,
            ["ExceptionType != EvmExceptionType.None", "ExceptionType != EvmExceptionType.Revert"],
            "bool IsException", Kind: MemberSyntaxKind.Property),
        new(CallResultPath, "CallResult", "ShouldRevert", 0,
            [], "bool ShouldRevert", Kind: MemberSyntaxKind.Property),
        new(CallResultPath, "CallResult", "StateToExecute", 0,
            [], "VmState<TGasPolicy>? StateToExecute", Kind: MemberSyntaxKind.Property),
        new(VmStatePath, "VmState", "RentFrame", 12,
            ["Rent()", "state.Initialize", "return state"],
            "VmState<TGasPolicy> RentFrame(TGasPolicy,long,long,ExecutionType,bool,bool,ExecutionEnvironment,in StackAccessTracker,in Snapshot,bool,bool,bool)"),
        new(VmStatePath, "VmState", "Initialize", 12,
            ["_snapshot = snapshot", "_accessTracker = stateForAccessLists", "executionType.IsAnyCreate", "_accessTracker.WasCreated", "_accessTracker.TakeSnapshot"],
            "void Initialize(TGasPolicy,long,long,ExecutionType,bool,bool,bool,bool,bool,ExecutionEnvironment,in StackAccessTracker,in Snapshot)"),
        new(VmStatePath, "VmState", "Dispose", 0,
            ["if (_canRestore)", "_accessTracker.Restore", "_accessTracker = default"], "void Dispose()"),
        new(VmStatePath, "VmState", "CommitToParent", 1,
            ["parentState.Refund", "_canRestore = false"], "void CommitToParent(VmState<TGasPolicy>)"),
        new(CallPath, "EvmInstructions", "InstructionCall", 3,
            ["TryReserveChildGas", "env.CallDepth >= MaxCallDepth", "UpdateGasUp", "CreateFullCallFrame"],
            "EvmExceptionType InstructionCall`6(ref EvmStack,ref TGasPolicy,VirtualMachine<TGasPolicy>)", 6),
        new(CallPath, "EvmInstructions", "CreateFullCallFrame", 15,
            ["Snapshot snapshot = state.TakeSnapshot", "state.SubtractFromBalance", "VmState<TGasPolicy>.RentFrame"],
            "EvmExceptionType CreateFullCallFrame`3(VirtualMachine<TGasPolicy>,ref EvmStack,ref TGasPolicy,in UInt256,UInt256,UInt256,UInt256,CodeInfo,Address,Address,Address,ExecutionEnvironment,in UInt256,ulong,bool)", 3),
        new(CreatePath, "EvmInstructions", "InstructionCreate", 3,
            ["env.CallDepth >= MaxCallDepth", "state.GetBalance", "state.GetNonce", "state.IncrementNonce", "Snapshot snapshot = state.TakeSnapshot", "if (isCreateCollision)", "state.SubtractFromBalance", "VmState<TGasPolicy>.RentFrame"],
            "EvmExceptionType InstructionCreate`5(ref EvmStack,ref TGasPolicy,VirtualMachine<TGasPolicy>)", 5),
        new(CreatePath, "EvmInstructions", "CompleteCreateWithoutChild", 3,
            ["ReturnDataBuffer = default", "stack.PushZero", "return result"],
            "EvmExceptionType CompleteCreateWithoutChild`2(ref EvmStack,ref TGasPolicy,VirtualMachine<TGasPolicy>)", 2),
        new(ControlFlowPath, "EvmInstructions", "InstructionSelfDestruct", 3,
            ["ToBeDestroyed", "GetBalance", "CreateAccount", "AddSelfDestructLog", "SubtractFromBalance"],
            "EvmExceptionType InstructionSelfDestruct`4(ref EvmStack,ref TGasPolicy,VirtualMachine<TGasPolicy>)", 4),
        new(WorldStatePath, "WorldState", "TakeSnapshot", 1,
            ["_persistentStorageProvider.TakeSnapshot", "_transientStorageProvider.TakeSnapshot", "_stateProvider.TakeSnapshot"],
            "Snapshot TakeSnapshot(bool)"),
        new(WorldStatePath, "WorldState", "Restore", 1,
            ["_persistentStorageProvider.Restore", "_transientStorageProvider.Restore", "_stateProvider.Restore"],
            "void Restore(Snapshot)", ParameterType: "Snapshot"),
        new(WorldStatePath, "WorldState", "AccountExists", 1,
            ["_stateProvider.AccountExists"], "bool AccountExists(Address)"),
        new(WorldStatePath, "WorldState", "IsDeadAccount", 1,
            ["_stateProvider.IsDeadAccount"], "bool IsDeadAccount(Address)"),
        new(WorldStatePath, "WorldState", "AddToBalance", 3,
            ["AddToBalance(address, balanceChange, spec, out UInt256 oldBalance)"],
            "void AddToBalance(Address,in UInt256,IReleaseSpec)"),
        new(AccessTrackerPath, "StackAccessTracker", "TakeSnapshot", 0,
            ["AccessedAddresses.TakeSnapshot", "AccessedStorageCells.TakeSnapshot", "DestroyList.TakeSnapshot", "Logs.TakeSnapshot"],
            "void TakeSnapshot()"),
        new(AccessTrackerPath, "StackAccessTracker", "Restore", 0,
            ["if (!_isTracingAccess)", "AccessedAddresses.Restore", "AccessedStorageCells.Restore", "DestroyList.Restore", "Logs.Restore"],
            "void Restore()"),
        new(AccessTrackerPath, "StackAccessTracker", "ToBeDestroyed", 1,
            ["DestroyList.Add"], "void ToBeDestroyed(Address)"),
        new(SnapshotPath, "Snapshot", "Snapshot", 3,
            ["StorageSnapshot = storageSnapshot", "StateSnapshot = stateSnapshot", "BlockAccessListSnapshot = balSnapshot"],
            "Snapshot(in Storage,int,int)", Kind: MemberSyntaxKind.Constructor),
        new(VirtualMachineDispatchPath, "VirtualMachine", "PrepareOpcodes", 0,
            ["IReleaseSpec spec = Spec", "SpecFlags.Validate(spec)", "_executionHandlers = table.GetExecutionHandlers(spec)"],
            "void PrepareOpcodes`2()", 2),
        new(VirtualMachineDispatchPath, "OpcodeTable", "GetExecutionHandlers", 1,
            ["Volatile.Read(ref _executionHandlers)", "new ExecutionHandlers(spec)", "Interlocked.CompareExchange"],
            "ExecutionHandlers GetExecutionHandlers(IReleaseSpec)"),
        new(VirtualMachineExecutionHandlersPath, "VirtualMachine", "GetExecutionHandlers", 0,
            ["_executionHandlers!"], "ExecutionHandlers GetExecutionHandlers()"),
        new(VirtualMachineExecutionHandlersPath, "ExecutionHandlers", "RunPrecompile", 0,
            ["SpecFlags.Eip158(spec)", "&RunPrecompileCore<OnFlag>", "&RunPrecompileCore<OffFlag>"],
            "delegate*<VirtualMachine<TGasPolicy>, VmState<TGasPolicy>, CallResult> RunPrecompile",
            Kind: MemberSyntaxKind.Field),
        new(VirtualMachineExecutionHandlersPath, "VirtualMachine", "RunPrecompileCore", 2,
            ["vm.RunPrecompile<Eip158>(state)"],
            "CallResult RunPrecompileCore`1(VirtualMachine<TGasPolicy>,VmState<TGasPolicy>)", 1),
        new(SpecFlagsPath, "SpecFlags", "Eip158", 1,
            ["spec.ClearEmptyAccountWhenTouched"], "bool Eip158(IReleaseSpec)"),
        new(SpecFlagsPath, "SpecFlags", "Validate", 1,
            [], "void Validate(IReleaseSpec)"),
        new(ExecutionTypePath, "ExecutionTypeExtensions", "IsAnyCreate", 1,
            ["ExecutionType.CREATE", "ExecutionType.CREATE2"], "bool IsAnyCreate(this ExecutionType)"),
        new(AmsterdamPath, "Amsterdam", "Apply", 1,
            ["spec.Name = \"Amsterdam\"", "spec.IsEip7928Enabled = true", "spec.IsEip8037Enabled = true", "spec.IsEip8038Enabled = true"],
            "void Apply(NamedReleaseSpec)"),
        new(MainnetSpecProviderPath, "MainnetSpecProvider", "MainnetSpecProvider", 0,
            ["AmsterdamBlockTimestamp", "Amsterdam.Instance", "BogotaBlockTimestamp", "Bogota.Instance"],
            "MainnetSpecProvider()", Kind: MemberSyntaxKind.Constructor),
        new(BlockProcessingModulePath, "BlockProcessingModule", "Load", 1,
            ["AddScoped<ITransactionProcessor, EthereumTransactionProcessor>", "AddScoped<IWorldState, WorldState>", "AddScoped<IVirtualMachine, EthereumVirtualMachine>"],
            "void Load(ContainerBuilder)"),
        new(TransactionProcessorPath, "TransactionProcessorBase", "ExecuteEvmTransaction", 16,
            ["new(tracer.IsTracingAccess)", "ExecuteEvmCall<OffFlag>", "ExecuteEvmCall<OnFlag>"],
            "TransactionResult ExecuteEvmTransaction(Transaction,BlockHeader,IReleaseSpec,ITxTracer,ExecutionOptions,bool,bool,bool,in IntrinsicGas<TGasPolicy>,TGasPolicy,in UInt256,in UInt256,in UInt256,in UInt256,CodeInfo?,Address?)"),
        new(TransactionProcessorPath, "TransactionProcessorBase", "ExecuteEvmCall", 14,
            ["WorldState.TakeSnapshot", "VmState<TGasPolicy>.RentTopLevel", "VirtualMachine.ExecuteTransaction(state, WorldState, tracer)", "VirtualMachine.ExecuteTransaction<OnFlag>(state, WorldState, tracer)"],
            "int ExecuteEvmCall`1(Transaction,BlockHeader,IReleaseSpec,ITxTracer,ExecutionOptions,long,IntrinsicGas<TGasPolicy>,long,in StackAccessTracker,TGasPolicy,ExecutionEnvironment,bool,out TransactionSubstate,out GasConsumed)", 1),
        new(AccountPath, "Account", "IsEmpty", 0,
            ["_codeHash is null", "Balance.IsZero", "Nonce == 0"],
            "bool IsEmpty", Kind: MemberSyntaxKind.Property),
        new(StateProviderPath, "StateProvider", "AccountExists", 1,
            ["GetThroughCache(address)"], "bool AccountExists(Address)"),
        new(StateProviderPath, "StateProvider", "IsDeadAccount", 1,
            ["GetThroughCache(address)", "account?.IsEmpty ?? true"], "bool IsDeadAccount(Address)"),
        new(StateProviderPath, "StateProvider", "AddToBalance", 4,
            ["SetNewBalance(address, balanceChange, releaseSpec, false, out oldBalance)"],
            "void AddToBalance(Address,in UInt256,IReleaseSpec,out UInt256)"),
        new(StateProviderPath, "StateProvider", "SetNewBalance", 5,
            ["balanceChange.IsZero", "releaseSpec.IsEip158Enabled", "touched.IsEmpty", "PushTouch(address, touched, releaseSpec, true)"],
            "void SetNewBalance(Address,in UInt256,IReleaseSpec,bool,out UInt256)"),
        new(StateProviderPath, "StateProvider", "PushTouch", 4,
            ["address == releaseSpec.Eip158IgnoredAccount", "Push(address, account, ChangeType.Touch)"],
            "void PushTouch(Address,Account,IReleaseSpec,bool)"),
        new(StateProviderPath, "StateProvider", "Push", 3,
            ["changeType == ChangeType.Touch", "_changes[head].ChangeType == ChangeType.Touch", "_changes.Add"],
            "void Push(Address,Account?,ChangeType)"),
    ];

    private static readonly DependencyPin[] DependencyPins =
    [
        new("WorldJournalExtractor", "productionRefinement",
            "tools/Evm/Lean/WorldJournalExtractor/Generated/WorldJournalKernel.source-manifest.json",
            "5c22d3c7649fc110fbb40cdaeacf49dfe4d7a87b529d27ea3f79a6d316bf1ffb",
            "WorldJournalExtractor.Refinement.WorldJournal",
            "tools/Evm/Lean/WorldJournalExtractor/Refinement/WorldJournal.lean",
            "f37d5ebd59a9f6353bf5200f6dd1952c1591cde70f408d23403fb9f0ecd91407",
            [new("WorldJournalExtractor.Refinement.WorldJournal.transition_refines", "54fcea6c3e4b4d89ef460b41b986abfef7ade6e16c2975a0631014c40ce03988"),
             new("WorldJournalExtractor.Refinement.WorldJournal.finite_trace_refines", "a954fddf09edb5dae3230e7fe285e3bdea6518c1a443f3a85718c4305542d6db")]),
        new("PersistentStorageOpcodeExtractor", "productionRefinement",
            "tools/Evm/Lean/PersistentStorageOpcodeExtractor/Generated/PersistentStorageOpcodeKernel.source-manifest.json",
            "4a62692982c77affa265763fc0dcffb62f3c828c9136e127862c1ad655470881",
            "PersistentStorageOpcodeExtractor.Refinement.PersistentStorageOpcode",
            "tools/Evm/Lean/PersistentStorageOpcodeExtractor/Refinement/PersistentStorageOpcode.lean",
            "4543590597947d7541bcd6b17a49823453f8309861ac57bcfdb3bd5e282f2d98",
            [new("PersistentStorageOpcodeExtractor.Refinement.PersistentStorageOpcode.sload_byte_refines", "68a6b42c5aa0f8dcca66636ddec33cff049750390c41786ed77f9109a04aeeab"),
             new("PersistentStorageOpcodeExtractor.Refinement.PersistentStorageOpcode.sstore_byte_refines", "b587237145ee1912f9d77141e327171cf8f21f8705a40734713ab95b800ef43a")]),
        new("TransientStorageOpcodeExtractor", "productionRefinement",
            "tools/Evm/Lean/TransientStorageOpcodeExtractor/Generated/TransientStorageOpcodeKernel.source-manifest.json",
            "c2a054aa59d22c5753c06a387ea392513502bad05081f6a687146a23cf58d6c9",
            "TransientStorageOpcodeExtractor.Refinement.TransientStorageOpcode",
            "tools/Evm/Lean/TransientStorageOpcodeExtractor/Refinement/TransientStorageOpcode.lean",
            "8dcf94eb16c0214d067e9f111d57b5153656d3198a34964a0730caa735c994a6",
            [new("TransientStorageOpcodeExtractor.Refinement.TransientStorageOpcode.tload_byte_refines", "904bf0ad238351edb1b0873fce5e4b301c2bdeb665a7bcf1a79fa8bf101cfada"),
             new("TransientStorageOpcodeExtractor.Refinement.TransientStorageOpcode.tstore_byte_refines", "e7f73630cad4ec7c9fdbb2cf61890edcbd49cbcd3ab46ebb6c1231c7dd454869")]),
        new("LogOpcodeExtractor", "productionRefinement",
            "tools/Evm/Lean/LogOpcodeExtractor/Generated/LogOpcodeKernel.source-manifest.json",
            "09741481354b0cb8967c434897885d46f72786b981f0a1cfe0f6754a2e4e74f0",
            "LogOpcodeExtractor.Refinement.LogOpcode",
            "tools/Evm/Lean/LogOpcodeExtractor/Refinement/LogOpcode.lean",
            "d90c5bd31ae4919178d2916e1f50d7b78c552ab5f85acef161e549231583bc6a",
            [new("LogOpcodeExtractor.Refinement.generated_execute_refines_reference", "f9cedf2b4e5cb173bfcc581988a451569f6f2a99b0a537326c7877ee869d603f")]),
        new("CallCreateFrame", "acceptedHandwrittenReference",
            "tools/Evm/Lean/Eip803x/Evm/CallCreateFrame.lean",
            "63606e8412bf5459477921d8dfea326078182d18530b7fafca6170ea148a9ee2",
            "Eip803x.Evm.CallCreateFrame",
            "tools/Evm/Lean/Eip803x/Evm/CallCreateFrame.lean",
            "63606e8412bf5459477921d8dfea326078182d18530b7fafca6170ea148a9ee2",
            [new("Eip803x.Evm.CallCreateFrame.call_failure_constructor_has_no_child", "00a75378c19eeb26996c6d70f9952a6adaff8f7d0d1721f3274b433eb9d46e44"),
             new("Eip803x.Evm.CallCreateFrame.create_failure_constructor_has_no_state_charge", "de9a001256b293d832af74905e5e4fde6dc9bc3ca19aef5f16f193c66291200f"),
             new("Eip803x.Evm.CallCreateFrame.revert_returns_exact_execution_and_restores_world", "071a6e5b1f83f3aec372d0cce0df8f3e587e5b1bf7026df43c109e3a6234ecb6"),
             new("Eip803x.Evm.CallCreateFrame.completion_preserves_enclosing_checkpoint", "262abc8b9b9a7a4415e9fb45d4da4b36d1bfd8be9109504840e9da986eb2616f")]),
        new("SelfDestruct", "acceptedHandwrittenReference",
            "tools/Evm/Lean/Eip803x/Evm/SelfDestruct.lean",
            "056d34839b368b1daf46079fc03c8842b7553ffa089a4c5a1a29b0d8be1e7c3e",
            "Eip803x.Evm.SelfDestruct",
            "tools/Evm/Lean/Eip803x/Evm/SelfDestruct.lean",
            "056d34839b368b1daf46079fc03c8842b7553ffa089a4c5a1a29b0d8be1e7c3e",
            [new("Eip803x.Evm.SelfDestruct.destroy_marked_exactly_for_same_transaction_creation", "07099a44b275eb1040b2abb72a6a22ab6d4c8cd530a2f16f6e7aa5627144d53f"),
             new("Eip803x.Evm.SelfDestruct.successful_other_target_moves_exact_balance", "7f3c3f92d68cf697f16acf1f8b40bee24439f4cf479217801d1d9791fcbfa4d6")]),
    ];

    private static readonly ImportedKernelPin[] ImportedKernelPins =
    [
        new("WorldJournalExtractor.Generated.WorldJournalKernel",
            "tools/Evm/Lean/WorldJournalExtractor/Generated/WorldJournalKernel.source-manifest.json",
            "5c22d3c7649fc110fbb40cdaeacf49dfe4d7a87b529d27ea3f79a6d316bf1ffb",
            "tools/Evm/Lean/WorldJournalExtractor/Generated/WorldJournalKernel.lean",
            "188c1a3d9a2f6c5cdb1a33b1446381b325e769910e8583470ccc5b5684e843a9"),
    ];

    private static readonly string[] SemanticBindings =
    [
        "child suspension pushes the current VmState before selecting StateToExecute",
        "CALL child entry snapshots world state before value-transfer mutation and snapshots the copied access tracker in VmState.Initialize",
        "CREATE preserves the parent nonce increment, snapshots world state, marks the destination created-this-transaction, then snapshots the access tracker",
        "successful child completion disables tracker restoration through CommitToParent before disposal",
        "explicit REVERT restores the world snapshot and child disposal restores access, log, and destroy journals",
        "returned exceptions and thrown EVM failures restore the world snapshot before the child is popped and disposed",
        "normal access tracking restores warm account and cell sets; destroy and ordered log journals always restore",
        "pre-frame failure is modeled relative to the post-pricing/post-warming parent baseline and creates no additional child snapshot or child mutation",
        "world operation bodies delegate to the accepted theorem-free WorldJournalExtractor transition",
        "the imported WorldJournal generated kernel is byte-bound both directly and through its independently pinned upstream manifest digest",
        "the standard build selects .std.cs and excludes .zkevm.cs when EnableZkEvm is not true",
        "Amsterdam is inherited from BPO2, enables EIP-7928/8037/8038, and is selected by the pinned mainnet fork schedule",
        "standard main processing registers concrete WorldState and constructs StackAccessTracker from tracer.IsTracingAccess; this package admits the false normal-access branch",
        "ExecutionType.IsAnyCreate is exactly CREATE or CREATE2 at the VmState created-this-transaction gate",
        "the standard execution-handler dispatch binds the active spec's EIP-158 flag to the generic RIPEMD precompile body used by execution",
        "the execution-wide RIPEMD-160 touch latch is set after the historical empty-account predicate and replayed after world restoration",
        "the production RIPEMD PushTouch change kind and consecutive-touch deduplication are projected extensionally to a same-account update",
        "SSTORE, TSTORE, LOG, and SELFDESTRUCT leaf effects enter composition only through the pinned dependency boundary",
    ];

    internal static ExtractionResult Extract(string repoRoot, string outputDirectory, string? leanOutputPath = null)
    {
        string root = Path.GetFullPath(repoRoot);
        SourceFile[] sources = ReadSources(root);
        ValidateFingerprints(sources);
        ValidateProductionRoots(sources);
        MemberIdentity[] members = AdmitMembers(sources);
        DependencyIdentity[] dependencies = AdmitDependencies(root);
        ImportedKernelIdentity[] importedKernels = AdmitImportedKernels(root);
        IrDocument document = BuildDocument();
        ValidateDocument(document, members, dependencies, importedKernels);

        byte[] irBytes = Serialize(document);
        string irHash = Sha256(irBytes);
        byte[] leanBytes = FrameJournalLeanEmitter.Emit(document, irHash);
        ValidateGeneratedLean(leanBytes);
        SourceManifest manifest = BuildManifest(sources, members, dependencies, importedKernels, irBytes, leanBytes);
        byte[] manifestBytes = Serialize(manifest);

        Directory.CreateDirectory(outputDirectory);
        string irPath = Path.Combine(outputDirectory, IrFileName);
        string manifestPath = Path.Combine(outputDirectory, ManifestFileName);
        string leanPath = leanOutputPath ?? Path.Combine(outputDirectory, "FrameJournalKernel.lean");
        Directory.CreateDirectory(Path.GetDirectoryName(leanPath)!);
        File.WriteAllBytes(irPath, irBytes);
        File.WriteAllBytes(manifestPath, manifestBytes);
        File.WriteAllBytes(leanPath, leanBytes);
        return new(irPath, manifestPath, leanPath, sources.Length, members.Length, dependencies.Length);
    }

    internal static void ValidateSourceShape(string repoRoot)
    {
        SourceFile[] sources = ReadSources(Path.GetFullPath(repoRoot));
        MemberIdentity[] members = AdmitMembers(sources);
        ValidateProductionRoots(sources);
        ValidateSourceChains(members);
    }

    internal static void ValidateDependencyTheoremShape(string repoRoot)
    {
        string root = Path.GetFullPath(repoRoot);
        foreach (DependencyPin pin in DependencyPins)
        {
            string proofSource = StrictUtf8(File.ReadAllBytes(FullPath(root, pin.ProofPath)), pin.ProofPath);
            foreach (TheoremIdentity theorem in pin.RequiredTheorems)
            {
                string signatureHash = Sha256(Encoding.UTF8.GetBytes(ExtractTheoremSignature(proofSource, theorem.FullyQualifiedName)));
                if (signatureHash != theorem.SignatureSha256)
                    throw new ExtractionException($"Theorem signature changed for {theorem.FullyQualifiedName}; found SHA-256 {signatureHash}.");
            }
        }
    }

    internal static void ValidateImportedKernelShape(string repoRoot) =>
        _ = AdmitImportedKernels(Path.GetFullPath(repoRoot));

    internal static IrDocument BuildDocument() => new(
        1,
        ExtractorVersion,
        "Nethermind standard-mainnet nested-frame pre-commit journal composition",
        new(
            "Amsterdam",
            "normal non-BAL execution with isTracingAccess=false",
            "nested child CALL/CREATE entry and success/revert/exception exit before provider commit",
            ["accounts", "persistent", "transient", "warmAccounts", "warmCells", "logs", "destroySet"],
            ["persistentOriginals", "createdThisTx", "ripemdTouchLatched"],
            ["gas and refund settlement", "code deposit and ClearStorage", "precompile bodies", "trie, state root, database, and provider commit", "BAL and tracing-access execution", "top-level transaction and block processing", "CLR, unsafe stack, pooling, and tracer correctness"],
            ["EnableZkEvm is not true, so the pinned standard-source item selection applies", "the executing release spec is the pinned Amsterdam instance selected by the pinned MainnetSpecProvider schedule", "the injected world state follows the pinned normal main-processing IWorldState-to-WorldState registration and tracer.IsTracingAccess is false", "all child body world operations satisfy the accepted WorldJournalExtractor adapter relation", "CALL/CREATE admission facts and child exit classification satisfy the pinned CallCreate reference boundary", "SSTORE, TSTORE, LOG, and SELFDESTRUCT effects are mapped to world operations by the pinned accepted leaf adapters", "frame-stack and world-snapshot stacks agree at package entry", "the child VmState is disposed exactly once on every nested exit", "latchRipemdTouch is emitted only after the pinned production RIPEMD empty-account predicate has set the execution-wide latch", "production PushTouch raw change-kind and duplicate-touch behavior is related extensionally to the abstract same-account update"]),
        Operations(),
        [
            "InstructionCall -> CreateFullCallFrame -> WorldState.TakeSnapshot -> VmState.RentFrame -> Initialize",
            "InstructionCreate -> IncrementNonce -> WorldState.TakeSnapshot -> VmState.RentFrame -> Initialize",
            "ExecuteTransaction success -> CommitToParent -> Dispose without StackAccessTracker.Restore",
            "ExecuteTransaction revert -> HandleRevert -> WorldState.Restore -> Dispose -> StackAccessTracker.Restore",
            "HandleException/HandleFailure -> WorldState.Restore -> PopAndRestoreParentState -> Dispose -> StackAccessTracker.Restore",
            "RunPrecompile RIPEMD predicate -> latch -> child rollback WorldState.Restore -> RestoreRipemdTouch -> zero-balance touch replay",
        ],
        [.. DependencyPins.SelectMany(pin => pin.RequiredTheorems).Select(theorem => theorem.FullyQualifiedName)],
        [.. ImportedKernelPins.Select(pin => new ImportedKernelDescriptor(pin.Name, pin.ManifestPath, pin.ArtifactPath))]);

    internal static void ValidateArtifacts(string repoRoot, byte[] manifestBytes, byte[] irBytes, byte[] leanBytes)
    {
        IrDocument document = Deserialize<IrDocument>(irBytes, "IR");
        if (!Serialize(document).AsSpan().SequenceEqual(irBytes))
            throw new ExtractionException("The frame-journal IR is not canonical JSON.");
        SourceManifest manifest = Deserialize<SourceManifest>(manifestBytes, "manifest");
        if (!Serialize(manifest).AsSpan().SequenceEqual(manifestBytes))
            throw new ExtractionException("The frame-journal manifest is not canonical JSON.");

        string root = Path.GetFullPath(repoRoot);
        SourceFile[] sources = ReadSources(root);
        ValidateFingerprints(sources);
        ValidateProductionRoots(sources);
        MemberIdentity[] members = AdmitMembers(sources);
        DependencyIdentity[] dependencies = AdmitDependencies(root);
        ImportedKernelIdentity[] importedKernels = AdmitImportedKernels(root);
        ValidateDocument(document, members, dependencies, importedKernels);
        byte[] expectedIr = Serialize(BuildDocument());
        if (!expectedIr.AsSpan().SequenceEqual(irBytes))
            throw new ExtractionException("The frame-journal IR differs from the source-derived document.");
        byte[] expectedLean = FrameJournalLeanEmitter.Emit(document, Sha256(irBytes));
        if (!expectedLean.AsSpan().SequenceEqual(leanBytes))
            throw new ExtractionException("The generated Lean differs from the source-derived transition.");
        SourceManifest expectedManifest = BuildManifest(sources, members, dependencies, importedKernels, irBytes, leanBytes);
        if (!Serialize(expectedManifest).AsSpan().SequenceEqual(manifestBytes))
            throw new ExtractionException("The manifest differs from independently recomputed source, dependency, and artifact identities.");
    }

    private static OperationDescriptor[] Operations() =>
    [
        new("applyWorld", "apply one accepted world-journal operation", "requires a non-snapshot WorldJournal operation", "dependencyTheorem", DependencyPins[0].IdentityPath, DependencyPins[0].ProofModule, "transition_refines", DependencyPins[0].RequiredTheorems[0].SignatureSha256, ["dependency transition"]),
        new("enterCall", "capture child world/access/log/destroy baseline", "begins after parent-owned pricing, access warming, and admission", "sourceMember", CallPath, "EvmInstructions", "CreateFullCallFrame", "EvmExceptionType CreateFullCallFrame`3(VirtualMachine<TGasPolicy>,ref EvmStack,ref TGasPolicy,in UInt256,UInt256,UInt256,UInt256,CodeInfo,Address,Address,Address,ExecutionEnvironment,in UInt256,ulong,bool)", ["takeSnapshot", "rentFrame"]),
        new("enterCreate", "mark destination created-this-transaction and capture all journaled surfaces", "parent nonce increment precedes the captured world baseline", "sourceMember", VmStatePath, "VmState", "Initialize", "void Initialize(TGasPolicy,long,long,ExecutionType,bool,bool,bool,bool,bool,ExecutionEnvironment,in StackAccessTracker,in Snapshot)", ["wasCreated", "takeSnapshot"]),
        new("preFrameCallFailure", "stutter at the child-composition boundary", "no child was rented; earlier target warming remains in the parent baseline", "sourceMember", CallPath, "EvmInstructions", "InstructionCall", "EvmExceptionType InstructionCall`6(ref EvmStack,ref TGasPolicy,VirtualMachine<TGasPolicy>)", ["no child"]),
        new("preFrameCreateFailure", "stutter at the child-composition boundary", "covers CompleteCreateWithoutChild and collision branches returning before child RentFrame; parent effects already applied belong to the input baseline", "sourceMember", CreatePath, "EvmInstructions", "InstructionCreate", "EvmExceptionType InstructionCreate`5(ref EvmStack,ref TGasPolicy,VirtualMachine<TGasPolicy>)", ["no child"]),
        new("latchRipemdTouch", "retain the historical RIPEMD-160 empty-account touch across later rollback", "emitted only after RunPrecompile has observed an existing dead RIPEMD account under EIP-158", "sourceMember", VirtualMachinePath, "VirtualMachine", "RunPrecompile", "CallResult RunPrecompile`1(VmState<TGasPolicy>)", ["empty-account predicate", "set execution-wide latch"]),
        new("exitSuccess", "discard top checkpoint while retaining child mutations", "CommitToParent disables access rollback before disposal", "sourceMember", VmStatePath, "VmState", "CommitToParent", "void CommitToParent(VmState<TGasPolicy>)", ["retain", "pop"]),
        new("exitRevert", "restore top checkpoint and pop child", "world restore followed by access/log/destroy restore", "sourceMember", VirtualMachinePath, "VirtualMachine", "HandleRevert", "void HandleRevert(VmState<TGasPolicy>,in CallResult,ref nuint)", ["restore", "pop"]),
        new("exitException", "restore top checkpoint and pop child", "returned and thrown exception paths share the same journal result", "sourceMember", VirtualMachinePath, "VirtualMachine", "HandleException", "TransactionSubstate HandleException(scoped in CallResult,scoped ref nuint,out bool)", ["restore", "pop"]),
    ];

    private static void ValidateDocument(IrDocument document, MemberIdentity[] members,
        DependencyIdentity[] dependencies, ImportedKernelIdentity[] importedKernels)
    {
        if (!Serialize(document).AsSpan().SequenceEqual(Serialize(BuildDocument())))
            throw new ExtractionException("The frame-journal IR schema, order, or field set changed.");
        if (document.Operations.Length != 9 || document.Scope.RestoredSurfaces.Length != 7 ||
            document.Scope.TransactionWideSurfaces.Length != 3 || document.Scope.Exclusions.Length != 7 ||
            document.Scope.Premises.Length != 10 || document.SourceChains.Length != 6 || dependencies.Length != 6 ||
            document.DependencyTheorems.Length != 13 || importedKernels.Length != 1 ||
            document.ImportedKernels.Length != 1)
            throw new ExtractionException("The frame-journal IR is not field-complete.");
        foreach (OperationDescriptor operation in document.Operations)
        {
            bool admitted = operation.BindingKind switch
            {
                "sourceMember" => members.Any(member => member.SourcePath == operation.SourcePath &&
                    member.ContainingType == operation.ContainingType && member.Member == operation.Member &&
                    member.Signature == operation.Signature),
                "dependencyTheorem" => dependencies.Any(dependency => dependency.IdentityPath == operation.SourcePath &&
                    dependency.ProofModule == operation.ContainingType && dependency.RequiredTheorems.Any(theorem =>
                        theorem.FullyQualifiedName.EndsWith("." + operation.Member, StringComparison.Ordinal) &&
                        theorem.SignatureSha256 == operation.Signature)),
                _ => false,
            };
            if (!admitted)
                throw new ExtractionException($"Operation {operation.Name} lost its exact source or dependency binding.");
        }
        string[] admittedTheorems = [.. dependencies.SelectMany(dependency => dependency.RequiredTheorems)
            .Select(theorem => theorem.FullyQualifiedName)];
        if (!document.DependencyTheorems.SequenceEqual(admittedTheorems, StringComparer.Ordinal))
            throw new ExtractionException("The frame-journal dependency theorem identities changed.");
        if (!document.ImportedKernels.SequenceEqual(importedKernels.Select(kernel =>
                new ImportedKernelDescriptor(kernel.Name, kernel.ManifestPath, kernel.ArtifactPath))))
            throw new ExtractionException("The frame-journal imported kernel identity changed.");
        ValidateSourceChains(members);
    }

    private static void ValidateSourceChains(MemberIdentity[] members)
    {
        (string Path, string Type, string Member, string Signature)[] required =
        [
            (VirtualMachinePath, "VirtualMachine", "ExecuteTransaction", "TransactionSubstate ExecuteTransaction`1(VmState<TGasPolicy>,IWorldState,ITxTracer)"),
            (VirtualMachinePath, "VirtualMachine", "PrepareNextCallFrame", "void PrepareNextCallFrame(in CallResult,ref nuint)"),
            (VirtualMachinePath, "VirtualMachine", "HandleFailure", "TransactionSubstate HandleFailure`1(Exception,string?,scoped ref nuint,out bool)"),
            (VirtualMachinePath, "VirtualMachine", "PopAndRestoreParentState", "void PopAndRestoreParentState()"),
            (VmStatePath, "VmState", "Dispose", "void Dispose()"),
            (WorldStatePath, "WorldState", "TakeSnapshot", "Snapshot TakeSnapshot(bool)"),
            (WorldStatePath, "WorldState", "Restore", "void Restore(Snapshot)"),
            (WorldStatePath, "WorldState", "AccountExists", "bool AccountExists(Address)"),
            (WorldStatePath, "WorldState", "IsDeadAccount", "bool IsDeadAccount(Address)"),
            (WorldStatePath, "WorldState", "AddToBalance", "void AddToBalance(Address,in UInt256,IReleaseSpec)"),
            (AccessTrackerPath, "StackAccessTracker", "TakeSnapshot", "void TakeSnapshot()"),
            (AccessTrackerPath, "StackAccessTracker", "Restore", "void Restore()"),
            (VirtualMachinePath, "VirtualMachineStatics", "RestoreRipemdTouch", "void RestoreRipemdTouch(IWorldState,IReleaseSpec,bool)"),
            (VirtualMachinePath, "VirtualMachine", "RunPrecompile", "CallResult RunPrecompile`1(VmState<TGasPolicy>)"),
            (VirtualMachinePath, "VirtualMachine", "RunPrecompile", "CallResult RunPrecompile(VmState<TGasPolicy>)"),
            (VirtualMachineDispatchPath, "VirtualMachine", "PrepareOpcodes", "void PrepareOpcodes`2()"),
            (VirtualMachineDispatchPath, "OpcodeTable", "GetExecutionHandlers", "ExecutionHandlers GetExecutionHandlers(IReleaseSpec)"),
            (VirtualMachineExecutionHandlersPath, "VirtualMachine", "GetExecutionHandlers", "ExecutionHandlers GetExecutionHandlers()"),
            (VirtualMachineExecutionHandlersPath, "ExecutionHandlers", "RunPrecompile", "delegate*<VirtualMachine<TGasPolicy>, VmState<TGasPolicy>, CallResult> RunPrecompile"),
            (VirtualMachineExecutionHandlersPath, "VirtualMachine", "RunPrecompileCore", "CallResult RunPrecompileCore`1(VirtualMachine<TGasPolicy>,VmState<TGasPolicy>)"),
            (SpecFlagsPath, "SpecFlags", "Eip158", "bool Eip158(IReleaseSpec)"),
            (ExecutionTypePath, "ExecutionTypeExtensions", "IsAnyCreate", "bool IsAnyCreate(this ExecutionType)"),
            (AmsterdamPath, "Amsterdam", "Apply", "void Apply(NamedReleaseSpec)"),
            (MainnetSpecProviderPath, "MainnetSpecProvider", "MainnetSpecProvider", "MainnetSpecProvider()"),
            (BlockProcessingModulePath, "BlockProcessingModule", "Load", "void Load(ContainerBuilder)"),
            (TransactionProcessorPath, "TransactionProcessorBase", "ExecuteEvmTransaction", "TransactionResult ExecuteEvmTransaction(Transaction,BlockHeader,IReleaseSpec,ITxTracer,ExecutionOptions,bool,bool,bool,in IntrinsicGas<TGasPolicy>,TGasPolicy,in UInt256,in UInt256,in UInt256,in UInt256,CodeInfo?,Address?)"),
            (TransactionProcessorPath, "TransactionProcessorBase", "ExecuteEvmCall", "int ExecuteEvmCall`1(Transaction,BlockHeader,IReleaseSpec,ITxTracer,ExecutionOptions,long,IntrinsicGas<TGasPolicy>,long,in StackAccessTracker,TGasPolicy,ExecutionEnvironment,bool,out TransactionSubstate,out GasConsumed)"),
            (AccountPath, "Account", "IsEmpty", "bool IsEmpty"),
            (StateProviderPath, "StateProvider", "AccountExists", "bool AccountExists(Address)"),
            (StateProviderPath, "StateProvider", "IsDeadAccount", "bool IsDeadAccount(Address)"),
            (StateProviderPath, "StateProvider", "AddToBalance", "void AddToBalance(Address,in UInt256,IReleaseSpec,out UInt256)"),
            (StateProviderPath, "StateProvider", "SetNewBalance", "void SetNewBalance(Address,in UInt256,IReleaseSpec,bool,out UInt256)"),
            (StateProviderPath, "StateProvider", "PushTouch", "void PushTouch(Address,Account,IReleaseSpec,bool)"),
            (StateProviderPath, "StateProvider", "Push", "void Push(Address,Account?,ChangeType)"),
        ];
        foreach ((string path, string type, string member, string signature) in required)
        {
            if (members.Count(identity => identity.SourcePath == path && identity.ContainingType == type &&
                    identity.Member == member && identity.Signature == signature) != 1)
                throw new ExtractionException($"Nested-frame source chain lost exact member {type}.{signature}.");
        }
    }

    private static void ValidateProductionRoots(SourceFile[] sources)
    {
        ValidateOrderedText(sources, BuildTargetsPath,
            ["<ItemGroup Condition=\"'$(EnableZkEvm)' == 'true'\">", "<Compile Remove=\"**/*.std.cs\" />",
             "<ItemGroup Condition=\"'$(EnableZkEvm)' != 'true'\">", "<Compile Remove=\"**/*.zkevm.cs\" />"]);
        ValidateOrderedText(sources, SpecFlagsPath,
            ["internal static partial class SpecFlags", "public static bool Eip158(IReleaseSpec spec) => spec.ClearEmptyAccountWhenTouched;",
             "public static void Validate(IReleaseSpec spec) { }"]);
        ValidateOrderedText(sources, VirtualMachineDispatchPath,
            ["SpecFlags.Validate(spec);", "_executionHandlers = table.GetExecutionHandlers(spec);",
             "_opcodeHandlers = table.GetHandlers<TTracingInst, TCancelable>(spec);"]);
        ValidateOrderedText(sources, VirtualMachineExecutionHandlersPath,
            ["private ExecutionHandlers GetExecutionHandlers()", "SpecFlags.Eip158(spec) ? &RunPrecompileCore<OnFlag> : &RunPrecompileCore<OffFlag>;",
             "private static CallResult RunPrecompileCore<Eip158>", "vm.RunPrecompile<Eip158>(state);"]);
        ValidateOrderedText(sources, ExecutionTypePath,
            ["public static bool IsAnyCreate", "ExecutionType.CREATE or ExecutionType.CREATE2"]);
        ValidateOrderedText(sources, AmsterdamPath,
            ["NamedReleaseSpec<Amsterdam>(BPO2.Instance)", "spec.Name = \"Amsterdam\";",
             "spec.IsEip7928Enabled = true;", "spec.IsEip8037Enabled = true;", "spec.IsEip8038Enabled = true;"]);
        ValidateOrderedText(sources, MainnetSpecProviderPath,
            ["public class MainnetSpecProvider", "[BPO2BlockTimestamp] = BPO2.Instance,",
             "[AmsterdamBlockTimestamp] = Amsterdam.Instance,", "[BogotaBlockTimestamp] = Bogota.Instance,",
             "public override ulong NetworkId => Core.BlockchainIds.Mainnet;"]);
        ValidateOrderedText(sources, BlockProcessingModulePath,
            [".AddScoped<ITransactionProcessor, EthereumTransactionProcessor>()",
             ".AddScoped<IWorldState, WorldState>()", ".AddScoped<IVirtualMachine, EthereumVirtualMachine>()"]);
        ValidateOrderedText(sources, TransactionProcessorPath,
            ["using StackAccessTracker accessTracker = new(tracer.IsTracingAccess);",
             "VirtualMachine.ExecuteTransaction(state, WorldState, tracer)"]);
        ValidateOrderedText(sources, AccountPath,
            ["public bool HasStorage => _storageRoot is not null;",
             "public bool IsEmpty => _codeHash is null && Balance.IsZero && Nonce == 0;"]);
        ValidateOrderedText(sources, StateProviderPath,
            ["public bool AccountExists(Address address)", "public bool IsDeadAccount(Address address)",
             "private void SetNewBalance", "if (releaseSpec.IsEip158Enabled && !isSubtracting)",
             "if (touched.IsEmpty)", "PushTouch(address, touched, releaseSpec, true)",
             "private void PushTouch", "Push(address, account, ChangeType.Touch)"]);
    }

    private static void ValidateOrderedText(SourceFile[] sources, string path, string[] tokens)
    {
        string text = sources.Single(source => source.RelativePath == path).Text;
        int cursor = 0;
        foreach (string token in tokens)
        {
            int position = text.IndexOf(token, cursor, StringComparison.Ordinal);
            if (position < 0)
                throw new ExtractionException($"Production root {path} lost ordered token '{token}'.");
            cursor = position + token.Length;
        }
    }

    private static SourceManifest BuildManifest(SourceFile[] sources, MemberIdentity[] members,
        DependencyIdentity[] dependencies, ImportedKernelIdentity[] importedKernels,
        byte[] irBytes, byte[] leanBytes) => new(
        1,
        ExtractorVersion,
        typeof(CSharpSyntaxTree).Assembly.GetName().Version?.ToString() ?? "unknown",
        LanguageVersion.CSharp14.ToDisplayString(),
        "Nethermind standard-mainnet nested-frame pre-commit journal composition",
        [.. sources.Select(source => new SourceIdentity(source.RelativePath, source.Sha256))],
        members,
        dependencies,
        importedKernels,
        new(IrFileName, Sha256(irBytes)),
        new(DefaultLeanRelativePath, Sha256(leanBytes)),
        CombinedHash(sources.Select(source => $"{source.RelativePath}\0{source.Sha256}")),
        CombinedHash(members.Select(member => $"{member.SourcePath}\0{member.Signature}\0{member.CanonicalSha256}")),
        CombinedHash(dependencies.Select(dependency => $"{dependency.Name}\0{dependency.IdentitySha256}\0{dependency.ProofSha256}\0{string.Join(';', dependency.RequiredTheorems.Select(theorem => theorem.SignatureSha256))}")),
        CombinedHash(importedKernels.Select(kernel => $"{kernel.Name}\0{kernel.ManifestSha256}\0{kernel.ArtifactPath}\0{kernel.ArtifactSha256}")),
        SemanticBindings);

    private static SourceFile[] ReadSources(string root) =>
        [.. SourcePaths.Select(path => ReadSource(root, path))];

    private static SourceFile ReadSource(string root, string relativePath)
    {
        string path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new ExtractionException($"Missing or escaped source path {relativePath}.");
        byte[] bytes = File.ReadAllBytes(path);
        string text = StrictUtf8(bytes, relativePath);
        CompilationUnitSyntax? compilationRoot = null;
        if (relativePath.EndsWith(".cs", StringComparison.Ordinal))
        {
            SyntaxTree tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.CSharp14), path);
            Diagnostic[] errors = [.. tree.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)];
            if (errors.Length != 0)
                throw new ExtractionException($"Source {relativePath} does not parse as C# 14: {errors[0]}.");
            compilationRoot = (CompilationUnitSyntax)tree.GetRoot();
        }
        return new(relativePath, bytes, text, compilationRoot, Sha256(bytes));
    }

    private static void ValidateFingerprints(SourceFile[] sources)
    {
        Dictionary<string, string> expected = ExpectedFingerprints.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(' ', 2, StringSplitOptions.TrimEntries))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        if (expected.Count != SourcePaths.Length)
            throw new ExtractionException("The frame-journal source fingerprint inventory changed.");
        foreach (SourceFile source in sources)
            if (!expected.TryGetValue(source.RelativePath, out string? hash) || source.Sha256 != hash)
                throw new ExtractionException($"Source identity changed for {source.RelativePath}; found SHA-256 {source.Sha256}.");
    }

    private static MemberIdentity[] AdmitMembers(SourceFile[] sources) =>
        [.. MemberExpectations.Select(expectation => AdmitMember(sources, expectation))];

    private static MemberIdentity AdmitMember(SourceFile[] sources, MemberExpectation expectation)
    {
        SourceFile source = sources.Single(candidate => candidate.RelativePath == expectation.Path);
        CompilationUnitSyntax root = source.Root ??
            throw new ExtractionException($"Member-bearing input {expectation.Path} is not a C# source.");
        IEnumerable<MemberDeclarationSyntax> candidates = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(type => type.Identifier.ValueText == expectation.Type)
            .SelectMany(type => type.Members)
            .Where(member => Matches(member, expectation));
        if (expectation.ParameterType is not null)
            candidates = candidates.Where(member => Parameters(member).Any(parameter => parameter.Type?.ToString() == expectation.ParameterType));
        MemberDeclarationSyntax[] matches = [.. candidates.Where(member => Signature(member, expectation.Member) == expectation.ExactSignature)];
        if (matches.Length != 1)
            throw new ExtractionException($"Expected one exact member {expectation.Type}.{expectation.Member}/{expectation.ParameterCount} in {expectation.Path}, found {matches.Length}.");
        MemberDeclarationSyntax selected = matches[0];
        string canonical = selected.WithoutTrivia().NormalizeWhitespace().ToFullString();
        int cursor = 0;
        foreach (string effect in expectation.OrderedEffects)
        {
            int position = canonical.IndexOf(effect, cursor, StringComparison.Ordinal);
            if (position < 0)
                throw new ExtractionException($"Member {expectation.Type}.{expectation.Member} lost ordered effect '{effect}'.");
            cursor = position + effect.Length;
        }
        return new(expectation.Path, expectation.Type, expectation.Member,
            Signature(selected, expectation.Member), Sha256(Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool Matches(MemberDeclarationSyntax member, MemberExpectation expectation) =>
        (expectation.Kind, member) switch
        {
            (MemberSyntaxKind.Method, MethodDeclarationSyntax method) =>
                method.Identifier.ValueText == expectation.Member && method.ParameterList.Parameters.Count == expectation.ParameterCount &&
                (method.TypeParameterList?.Parameters.Count ?? 0) == expectation.TypeParameterCount,
            (MemberSyntaxKind.Constructor, ConstructorDeclarationSyntax constructor) =>
                constructor.Identifier.ValueText == expectation.Member && constructor.ParameterList.Parameters.Count == expectation.ParameterCount,
            (MemberSyntaxKind.Property, PropertyDeclarationSyntax property) => property.Identifier.ValueText == expectation.Member,
            (MemberSyntaxKind.Field, FieldDeclarationSyntax field) =>
                field.Declaration.Variables.Count == 1 &&
                field.Declaration.Variables[0].Identifier.ValueText == expectation.Member,
            _ => false,
        };

    private static IEnumerable<ParameterSyntax> Parameters(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax method => method.ParameterList.Parameters,
        ConstructorDeclarationSyntax constructor => constructor.ParameterList.Parameters,
        _ => [],
    };

    private static string Signature(MemberDeclarationSyntax member, string memberName) => member switch
    {
        MethodDeclarationSyntax method => $"{method.ReturnType} {method.Identifier.ValueText}`{method.TypeParameterList?.Parameters.Count ?? 0}({CanonicalParameters(method.ParameterList.Parameters)})".Replace("`0", "", StringComparison.Ordinal),
        ConstructorDeclarationSyntax constructor => $"{constructor.Identifier.ValueText}({CanonicalParameters(constructor.ParameterList.Parameters)})",
        PropertyDeclarationSyntax property => $"{property.Type} {property.Identifier.ValueText}",
        FieldDeclarationSyntax field => $"{field.Declaration.Type} {memberName}",
        _ => throw new ExtractionException($"Unsupported member syntax for {memberName}."),
    };

    private static string CanonicalParameters(SeparatedSyntaxList<ParameterSyntax> parameters) =>
        string.Join(",", parameters.Select(parameter =>
        {
            string modifiers = string.Join(" ", parameter.Modifiers.Select(modifier => modifier.ValueText));
            return modifiers.Length == 0 ? parameter.Type!.ToString() : $"{modifiers} {parameter.Type}";
        }));

    private static DependencyIdentity[] AdmitDependencies(string root) =>
        [.. DependencyPins.Select(pin =>
        {
            string identityHash = HashFile(root, pin.IdentityPath);
            string proofHash = HashFile(root, pin.ProofPath);
            if (identityHash != pin.IdentitySha256 || proofHash != pin.ProofSha256)
                throw new ExtractionException($"Dependency identity changed for {pin.Name}.");
            string proofSource = StrictUtf8(File.ReadAllBytes(FullPath(root, pin.ProofPath)), pin.ProofPath);
            foreach (TheoremIdentity theorem in pin.RequiredTheorems)
            {
                string signatureHash = Sha256(Encoding.UTF8.GetBytes(ExtractTheoremSignature(proofSource, theorem.FullyQualifiedName)));
                if (signatureHash != theorem.SignatureSha256)
                    throw new ExtractionException($"Theorem signature changed for {theorem.FullyQualifiedName}; found SHA-256 {signatureHash}.");
            }
            return new DependencyIdentity(pin.Name, pin.AdmissionState, pin.IdentityPath, identityHash,
                pin.ProofModule, pin.ProofPath, proofHash, pin.RequiredTheorems);
        })];

    private static ImportedKernelIdentity[] AdmitImportedKernels(string root) =>
        [.. ImportedKernelPins.Select(pin => AdmitImportedKernel(root, pin))];

    private static ImportedKernelIdentity AdmitImportedKernel(string root, ImportedKernelPin pin)
    {
        byte[] manifestBytes = File.ReadAllBytes(FullPath(root, pin.ManifestPath));
        string manifestHash = Sha256(manifestBytes);
        if (manifestHash != pin.ManifestSha256)
            throw new ExtractionException($"Imported kernel manifest identity changed for {pin.Name}.");

        using JsonDocument manifest = JsonDocument.Parse(StrictUtf8(manifestBytes, pin.ManifestPath),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        JsonElement lean = manifest.RootElement.GetProperty("lean");
        string manifestArtifactPath = lean.GetProperty("path").GetString() ??
            throw new ExtractionException($"Imported kernel path is null for {pin.Name}.");
        string manifestArtifactHash = lean.GetProperty("sha256").GetString() ??
            throw new ExtractionException($"Imported kernel digest is null for {pin.Name}.");
        if (manifestArtifactPath != pin.ArtifactPath ||
            !Regex.IsMatch(manifestArtifactHash, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant) ||
            manifestArtifactHash != pin.ArtifactSha256)
            throw new ExtractionException($"Imported kernel manifest binding changed for {pin.Name}.");

        string artifactHash = HashFile(root, pin.ArtifactPath);
        if (artifactHash != manifestArtifactHash)
            throw new ExtractionException($"Imported kernel bytes disagree with the upstream manifest for {pin.Name}.");
        return new(pin.Name, pin.ManifestPath, manifestHash, pin.ArtifactPath, artifactHash);
    }

    private static string ExtractTheoremSignature(string source, string fullyQualifiedName)
    {
        string name = fullyQualifiedName[(fullyQualifiedName.LastIndexOf('.') + 1)..];
        MatchCollection matches = Regex.Matches(source, $@"(?ms)^[\t ]*theorem[\t ]+{Regex.Escape(name)}\b.*?(?=:=\s*by\b)");
        if (matches.Count != 1)
            throw new ExtractionException($"Expected exactly one theorem declaration for {fullyQualifiedName}.");
        return Regex.Replace(matches[0].Value, @"\s+", " ").Trim();
    }

    private static void ValidateGeneratedLean(byte[] bytes)
    {
        string source = StrictUtf8(bytes, DefaultLeanRelativePath);
        if (Regex.IsMatch(source, @"(?m)^\s*(theorem|lemma|axiom|example|admit|sorry)\b") ||
            Regex.IsMatch(source, @"(?m)^\s*import\s+FrameJournalExtractor\.Specification\.FrameJournal\s*$") ||
            Regex.IsMatch(source, @"(?m)^\s*import\s+WorldJournalExtractor\.Specification\.WorldJournal\s*$") ||
            !source.Contains("WorldJournalExtractor.Generated.WorldJournalKernel", StringComparison.Ordinal))
            throw new ExtractionException("Generated Lean is not theorem-free and independent from handwritten transitions.");
    }

    private static T Deserialize<T>(byte[] bytes, string name) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(StrictUtf8(bytes, name), JsonOptions) ??
                throw new ExtractionException($"The frame-journal {name} is null.");
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Invalid frame-journal {name}: {exception.Message}");
        }
    }

    private static byte[] Serialize<T>(T value) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions).ReplaceLineEndings("\n") + "\n");

    private static string HashFile(string root, string relativePath) => Sha256(File.ReadAllBytes(FullPath(root, relativePath)));

    private static string FullPath(string root, string relativePath)
    {
        string path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new ExtractionException($"Missing or escaped dependency path {relativePath}.");
        return path;
    }

    private static string StrictUtf8(byte[] bytes, string name)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new ExtractionException($"Input {name} is not valid UTF-8.");
        }
    }

    private static string CombinedHash(IEnumerable<string> values) =>
        Sha256(Encoding.UTF8.GetBytes(string.Join("\n", values)));

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private enum MemberSyntaxKind { Method, Constructor, Property, Field }

    private sealed record MemberExpectation(
        string Path,
        string Type,
        string Member,
        int ParameterCount,
        string[] OrderedEffects,
        string ExactSignature,
        int TypeParameterCount = 0,
        string? ParameterType = null,
        MemberSyntaxKind Kind = MemberSyntaxKind.Method);

    private sealed record DependencyPin(
        string Name,
        string AdmissionState,
        string IdentityPath,
        string IdentitySha256,
        string ProofModule,
        string ProofPath,
        string ProofSha256,
        TheoremIdentity[] RequiredTheorems);

    private sealed record ImportedKernelPin(
        string Name,
        string ManifestPath,
        string ManifestSha256,
        string ArtifactPath,
        string ArtifactSha256);

    private sealed record SourceFile(
        string RelativePath,
        byte[] Bytes,
        string Text,
        CompilationUnitSyntax? Root,
        string Sha256);
}
