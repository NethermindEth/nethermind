// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Diagnostics;
using Autofac;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Container;
using Nethermind.Core.Test.Db;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Block-level processing benchmark measuring <see cref="BranchProcessor.Process"/>
/// with mainnet-like block scenarios under <see cref="Osaka"/> rules.
///
/// Uses the full <see cref="BranchProcessor"/> pipeline — including the
/// <see cref="BlockCachePreWarmer"/> — to match the live client's block processing
/// path. Pre-warming triggers for blocks with 3+ transactions.
///
/// A single world state and branch processor are built once in <see cref="GlobalSetup"/>.
/// <see cref="BranchProcessor.Process"/> manages its own scope internally, so each
/// iteration opens a fresh scope at the genesis root and disposes it on exit —
/// exactly as the runtime does.
///
/// Each benchmark method loops <see cref="N"/> times with
/// <c>OperationsPerInvoke = N</c> so BDN divides the total time by N.
/// This keeps iteration time well above the 100 ms minimum, eliminating
/// MinIterationTime warnings and reducing coefficient of variation caused
/// by OS scheduling noise on sub-millisecond measurements.
///
/// Scenarios:
/// - EmptyBlock, SingleTransfer, Transfers_50, Transfers_200
/// - Eip1559_200, AccessList_50, ContractDeploy_10
/// - ContractCall_200, MixedBlock (100 legacy + 60 EIP-1559 + 30 AL + 10 calls)
/// </summary>
[Config(typeof(BlockProcessingConfig))]
[MemoryDiagnoser]
[JsonExporterAttribute.FullCompressed]
public class BlockProcessingBenchmark
{
    /// <summary>
    /// Repetitions per BDN invocation. Used for low per-op benchmarks
    /// (EmptyBlock ~21 us, SingleTransfer ~46 us, ContractDeploy_10 ~400 us)
    /// where higher rep counts amortize OS scheduling noise and prewarmer
    /// thread contention, keeping iteration time above BDN's 100 ms minimum.
    /// </summary>
    private const int N_LARGE = 5000;

    private const int N_SMALL = 200;

    private const int N_MEDIUM = 500;

    /// <summary>
    /// 20 data points per benchmark (2 launches x 10 iterations, 2 warmup each).
    /// GcForce ensures a GC collection between iterations to reduce allocation noise.
    /// </summary>
    private class BlockProcessingConfig : ManualConfig
    {
        public BlockProcessingConfig()
        {
            AddJob(Job.Default
                .WithToolchain(InProcessNoEmitToolchain.Instance)
                .WithInvocationCount(1)
                .WithUnrollFactor(1)
                .WithLaunchCount(2)
                .WithWarmupCount(2)
                .WithIterationCount(5)
                .WithGcForce(true));
            AddColumn(StatisticColumn.Min);
            AddColumn(StatisticColumn.Max);
            AddColumn(StatisticColumn.Median);
            AddColumn(StatisticColumn.P90);
            AddColumn(StatisticColumn.P95);
        }
    }

    /// <summary>The fork the whole scenario set runs under.</summary>
    /// <remarks>Amsterdam turns on EIP-7928 block access lists, which decorate every state read. That layer
    /// is what the ethpandaops bal-full suite exercises and what an Osaka-only benchmark cannot see.</remarks>
    [Params("Osaka", "Amsterdam")]
    public string Fork { get; set; } = "Osaka";

    private IReleaseSpec Spec => Fork switch
    {
        "Osaka" => Osaka.Instance,
        "Amsterdam" => Amsterdam.Instance,
        _ => throw new ArgumentOutOfRangeException(nameof(Fork), Fork, "Unmapped fork - BDN would silently label a wrong spec."),
    };

    private static readonly byte[] ContractCode = Prepare.EvmCode
        .PushData(0x01)
        .Op(Instruction.STOP)
        .Done;

    // Minimal bytecode (STOP) for system contract stubs
    private static readonly byte[] StopCode = [0x00];

    /// <summary>How many times the SLOAD scenario reads the same slot per call.</summary>
    private const int SloadsPerCall = 2000;

    /// <summary>Home of the SLOAD loop. Deliberately not <see cref="TestItem.AddressD"/>:
    /// <see cref="SampleAccessList"/> names it, and merely creating it would make the pre-warmer load
    /// its declared slots for the pre-existing access-list scenarios (the trigger is existence, not
    /// code or storage).</summary>
    private static readonly Address SloadCallerAddress = new("0x00000000000000000000000000000000000000ad");

    /// <summary>Recipient of every transfer and access-list tx. Seeded (non-zero, so not EIP-161-empty)
    /// because a real transfer overwhelmingly targets an existing account; otherwise the first tx of each
    /// block pays EIP-8037's ~183k NEW_ACCOUNT state charge and OOGs on Amsterdam. Deliberately outside
    /// <see cref="SampleAccessList"/>: an address that exists makes the pre-warmer load its declared
    /// slots, which would change the pre-existing AccessList_50 and MixedBlock numbers.</summary>
    private static readonly Address TransferTargetAddress = new("0x00000000000000000000000000000000000000ba");

    /// <summary>Reads one warm slot over and over, the shape of the sload_same_key benchmark.</summary>
    private static readonly byte[] SloadSameKeyCode = BuildSloadSameKeyCode();

    /// <summary>The same loop without the SLOAD, so subtracting isolates what the read itself costs.</summary>
    private static readonly byte[] PushPopOnlyCode = BuildPushPopOnlyCode();

    private static byte[] BuildPushPopOnlyCode()
    {
        Prepare code = Prepare.EvmCode;
        for (int i = 0; i < SloadsPerCall; i++)
        {
            code = code.PushData(0).Op(Instruction.POP);
        }
        return code.Op(Instruction.STOP).Done;
    }

    /// <summary>Same loop with TLOAD: no access-list check and no persistent storage provider —
    /// so the difference against the SLOAD loop isolates those two layers.</summary>
    private static readonly byte[] TloadSameKeyCode = BuildTloadSameKeyCode();

    private static byte[] BuildTloadSameKeyCode()
    {
        Prepare code = Prepare.EvmCode;
        for (int i = 0; i < SloadsPerCall; i++)
        {
            code = code.PushData(0).Op(Instruction.TLOAD).Op(Instruction.POP);
        }
        return code.Op(Instruction.STOP).Done;
    }

    /// <summary>Queries one external account's balance repeatedly, the ext_account_query_warm shape.</summary>
    private static readonly Address BalanceCallerAddress = new("0x00000000000000000000000000000000000000aa");

    private static readonly byte[] BalanceSameAddressCode = BuildBalanceSameAddressCode();

    private static byte[] BuildBalanceSameAddressCode()
    {
        Prepare code = Prepare.EvmCode;
        for (int i = 0; i < SloadsPerCall; i++)
        {
            code = code.PushData(TestItem.AddressB).Op(Instruction.BALANCE).Op(Instruction.POP);
        }
        return code.Op(Instruction.STOP).Done;
    }

    private static readonly Address ExtCodeSizeCallerAddress = new("0x00000000000000000000000000000000000000bb");

    /// <summary>EXTCODESIZE on one account repeatedly; POP follows so the peephole path cannot fire.</summary>
    private static readonly byte[] ExtCodeSizeSameAddressCode = BuildExtCodeSizeCode();

    private static byte[] BuildExtCodeSizeCode()
    {
        Prepare code = Prepare.EvmCode;
        for (int i = 0; i < SloadsPerCall; i++)
        {
            code = code.PushData(TestItem.AddressB).Op(Instruction.EXTCODESIZE).Op(Instruction.POP);
        }
        return code.Op(Instruction.STOP).Done;
    }

    private static readonly Address ExtCodeHashCallerAddress = new("0x00000000000000000000000000000000000000cc");

    /// <summary>EXTCODEHASH on one account with code, the ext_account_query_warm shape.</summary>
    private static readonly byte[] ExtCodeHashSameAddressCode = BuildExtCodeHashCode();

    private static byte[] BuildExtCodeHashCode()
    {
        Prepare code = Prepare.EvmCode;
        for (int i = 0; i < SloadsPerCall; i++)
        {
            code = code.PushData(TestItem.AddressB).Op(Instruction.EXTCODEHASH).Op(Instruction.POP);
        }
        return code.Op(Instruction.STOP).Done;
    }

    private static readonly Address CallCallerAddress = new("0x00000000000000000000000000000000000000dd");

    /// <summary>STATICCALL to one account repeatedly, the CALL half of ext_account_query_warm.</summary>
    /// <remarks>Fewer iterations than the single-opcode loops, since a call frame is far more code and
    /// gas per step. (Code size is no constraint here: seeding goes through <c>InsertCode</c>, which
    /// bypasses the deploy-time EIP-170 limit — the single-opcode loops already exceed it.)</remarks>
    private const int CallsPerCall = 400;

    private static readonly byte[] StaticCallSameAddressCode = BuildStaticCallCode();

    private static byte[] BuildStaticCallCode()
    {
        Prepare code = Prepare.EvmCode;
        for (int i = 0; i < CallsPerCall; i++)
        {
            // STATICCALL takes gas, address, argsOffset, argsLength, retOffset, retLength.
            code = code
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(TestItem.AddressB)
                .PushData(1000)
                .Op(Instruction.STATICCALL)
                .Op(Instruction.POP);
        }
        return code.Op(Instruction.STOP).Done;
    }

    private static readonly Address EoaCallCallerAddress = new("0x00000000000000000000000000000000000000ee");

    /// <summary>An account with no code, so a call to it takes the empty-account fast path.</summary>
    private static readonly Address EoaTargetAddress = new("0x00000000000000000000000000000000000000ef");

    /// <summary>STATICCALL to a codeless account: identical opcode work, but no call frame is built.</summary>
    /// <remarks>Subtracting this from <see cref="StaticCall_SameAddress"/> isolates what a frame costs.</remarks>
    private static readonly byte[] StaticCallEoaCode = BuildStaticCallEoaCode();

    private static byte[] BuildStaticCallEoaCode()
    {
        Prepare code = Prepare.EvmCode;
        for (int i = 0; i < CallsPerCall; i++)
        {
            code = code
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(EoaTargetAddress)
                .PushData(1000)
                .Op(Instruction.STATICCALL)
                .Op(Instruction.POP);
        }
        return code.Op(Instruction.STOP).Done;
    }

    private static readonly Address PrecompileCallCallerAddress = new("0x00000000000000000000000000000000000000fa");

    /// <summary>STATICCALL to the identity precompile with no input.</summary>
    /// <remarks>STATICCALL to a precompile takes the inline path, which runs the callee without building a
    /// frame or suspending to the outer dispatch loop. Against <see cref="StaticCall_SameAddress"/> that
    /// isolates what the frame round trip costs.</remarks>
    private static readonly byte[] StaticCallPrecompileCode = BuildStaticCallPrecompileCode();

    private static byte[] BuildStaticCallPrecompileCode()
    {
        Address identity = new("0x0000000000000000000000000000000000000004");
        Prepare code = Prepare.EvmCode;
        for (int i = 0; i < CallsPerCall; i++)
        {
            code = code
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(identity)
                .PushData(1000)
                .Op(Instruction.STATICCALL)
                .Op(Instruction.POP);
        }
        return code.Op(Instruction.STOP).Done;
    }

    private static readonly Address SstoreCallerAddress = new("0x00000000000000000000000000000000000000ab");

    /// <summary>Writes alternating values to one slot, so every write after the first is a dirty
    /// transition — the shape is fork-independent even where the write's price is not.</summary>
    private static readonly byte[] SstoreDirtyCode = BuildSstoreDirtyCode();

    private static byte[] BuildSstoreDirtyCode()
    {
        Prepare code = Prepare.EvmCode;
        for (int i = 0; i < SloadsPerCall; i++)
        {
            code = code.PushData((i & 1) == 0 ? 7 : 9).PushData(0).Op(Instruction.SSTORE);
        }

        // Restore the seeded value so every transaction starts from the same EIP-2200 original.
        // Without this, the slot ends at 9 and the next transaction's alternating writes swing
        // around their own original, turning every other write into a fresh clean SSTORE — which
        // multiplies the gas ~12x and quietly turned transactions 2..10 into out-of-gas burns.
        return code.PushData(5).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done;
    }

    private static readonly Address Create2CallerAddress = new("0x00000000000000000000000000000000000000ac");

    /// <summary>How many CREATE2s the create scenario performs per call.</summary>
    /// <remarks>One transaction of 50: distinct salts keep the addresses collision-free within the block
    /// (a second tx would recompute the same addresses and every create would collide), and one tx is
    /// below the pre-warmer's 3-transaction trigger, so this scenario deliberately runs unwarmed. The
    /// 25M gas limit is sized for Amsterdam, where EIP-8037 state gas makes an empty-initcode CREATE2
    /// ~183k gas against Osaka's ~32k — a smaller budget OOGs silently under NoValidation.</remarks>
    private const int CreatesPerCall = 50;

    /// <summary>CREATE2 of an empty contract followed by an immediate query of the created address,
    /// the create2_immediate_access shape.</summary>
    private static readonly byte[] Create2ImmediateAccessCode = BuildCreate2ImmediateAccessCode();

    private static byte[] BuildCreate2ImmediateAccessCode()
    {
        Prepare code = Prepare.EvmCode;
        for (int i = 0; i < CreatesPerCall; i++)
        {
            // CREATE2 takes value, offset, length, salt; empty initcode deploys an empty contract.
            code = code
                .PushData(i)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.CREATE2)
                .Op(Instruction.EXTCODESIZE)
                .Op(Instruction.POP);
        }
        return code.Op(Instruction.STOP).Done;
    }

    private static byte[] BuildSloadSameKeyCode()
    {
        Prepare code = Prepare.EvmCode;
        for (int i = 0; i < SloadsPerCall; i++)
        {
            code = code.PushData(0).Op(Instruction.SLOAD).Op(Instruction.POP);
        }
        return code.Op(Instruction.STOP).Done;
    }

    private static readonly AccessList SampleAccessList = new AccessList.Builder()
        .AddAddress(TestItem.AddressC)
        .AddStorage(UInt256.Zero)
        .AddStorage(UInt256.One)
        .AddStorage(new UInt256(2))
        .AddAddress(TestItem.AddressD)
        .AddStorage(new UInt256(10))
        .AddStorage(new UInt256(11))
        .AddStorage(new UInt256(12))
        .Build();

    private readonly PrivateKey _senderKey = TestItem.PrivateKeyA;
    private Address _sender = null!;

    // DI container — built once, shared across all iterations
    private IContainer _container = null!;

    // Single processing scope, branch processor and parent header — shared across all iterations
    private ILifetimeScope _processingScope = null!;
    private IBranchProcessor _branchProcessor = null!;
    private BlockHeader _parentHeader = null!;

    // Pre-built blocks (immutable after GlobalSetup)
    private Block _emptyBlock = null!;
    private Block _singleTransferBlock = null!;
    private Block _transfers50Block = null!;
    private Block _transfers200Block = null!;
    private Block _eip1559_200Block = null!;
    private Block _accessList50Block = null!;
    private Block _contractDeploy10Block = null!;
    private Block _contractCall200Block = null!;
    private Block _sloadSameKeyBlock = null!;
    private Block _sloadSameKeyNoPrewarmBlock = null!;
    private Block _pushPopOnlyBlock = null!;
    private Block _tloadSameKeyBlock = null!;
    private Block _balanceSameAddressBlock = null!;
    private Block _extCodeSizeBlock = null!;
    private Block _extCodeHashBlock = null!;
    private Block _staticCallBlock = null!;
    private Block _staticCallEoaBlock = null!;
    private Block _staticCallPrecompileBlock = null!;
    private Block _sstoreDirtyBlock = null!;
    private Block _create2Block = null!;
    private Block _mixedBlock = null!;

    private BlockHeader _header = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // Pin to a single core to reduce OS scheduler jitter
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            Process.GetCurrentProcess().ProcessorAffinity = new IntPtr(1);
        }

        _sender = _senderKey.Address;

        _header = Build.A.BlockHeader
            .WithNumber(1)
            .WithGasLimit(30_000_000)
            .WithBaseFee(1.GWei)
            .WithTimestamp(1)
            .TestObject;

        _emptyBlock = BuildBlock();
        _singleTransferBlock = BuildBlock(BuildLegacyTransfers(1, 0));
        _transfers50Block = BuildBlock(BuildLegacyTransfers(50, 0));
        _transfers200Block = BuildBlock(BuildLegacyTransfers(200, 0));
        _eip1559_200Block = BuildBlock(BuildEip1559Transfers(200, 0));
        _accessList50Block = BuildBlock(BuildAccessListTxs(50, 0));
        _contractDeploy10Block = BuildBlock(BuildContractDeploys(10, 0));
        _contractCall200Block = BuildBlock(BuildContractCalls(200, 0));
        _sloadSameKeyBlock = BuildBlock(BuildSloadCalls(10, 0));
        _sloadSameKeyNoPrewarmBlock = BuildBlock(BuildSloadCalls(2, 0));
        _pushPopOnlyBlock = BuildBlock(BuildCallsTo(TestItem.AddressE, 10, 0));
        _tloadSameKeyBlock = BuildBlock(BuildCallsTo(TestItem.AddressF, 10, 0));
        _balanceSameAddressBlock = BuildBlock(BuildCallsTo(BalanceCallerAddress, 10, 0));
        _extCodeSizeBlock = BuildBlock(BuildCallsTo(ExtCodeSizeCallerAddress, 10, 0));
        _extCodeHashBlock = BuildBlock(BuildCallsTo(ExtCodeHashCallerAddress, 10, 0));
        _staticCallBlock = BuildBlock(BuildCallsTo(CallCallerAddress, 10, 0));
        _staticCallEoaBlock = BuildBlock(BuildCallsTo(EoaCallCallerAddress, 10, 0));
        _staticCallPrecompileBlock = BuildBlock(BuildCallsTo(PrecompileCallCallerAddress, 10, 0));
        _sstoreDirtyBlock = BuildBlock(BuildCallsTo(SstoreCallerAddress, 10, 0));
        _create2Block = BuildBlock(BuildCallsTo(Create2CallerAddress, 1, 0, gasLimit: 25_000_000));

        // MixedBlock: 100 legacy + 60 EIP-1559 + 30 access-list + 10 contract calls
        Transaction[] mixedTxs = new Transaction[200];
        ulong nonce = 0;
        BuildLegacyTransfers(100, nonce).CopyTo(mixedTxs, 0);
        nonce += 100;
        BuildEip1559Transfers(60, nonce).CopyTo(mixedTxs, 100);
        nonce += 60;
        BuildAccessListTxs(30, nonce).CopyTo(mixedTxs, 160);
        nonce += 30;
        BuildContractCalls(10, nonce).CopyTo(mixedTxs, 190);
        _mixedBlock = BuildBlock(mixedTxs);

        // Build DI container using standard modules instead of hand-wiring.
        // TestNethermindModule wires PseudoNethermindModule + TestEnvironmentModule
        // with TestSpecProvider(Spec) and in-memory databases.
        // Includes PrewarmerModule (via NethermindModule) for block cache pre-warming.
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Spec))
            .Build();

        // Single world state — BranchProcessor.Process() manages scope internally,
        // matching the live client's block processing path.
        IDbProvider dbProvider = TestMemDbProvider.Init();
        IWorldStateManager wsm = TestWorldStateFactory.CreateWorldStateManagerForTest(dbProvider, LimboLogs.Instance);
        IWorldStateScopeProvider scopeProvider = wsm.GlobalWorldState;

        IBlockValidationModule[] validationModules = _container.Resolve<IBlockValidationModule[]>();
        IMainProcessingModule[] mainProcessingModules = _container.Resolve<IMainProcessingModule[]>();
        _processingScope = _container.BeginLifetimeScope(b =>
        {
            b.RegisterInstance(scopeProvider).As<IWorldStateScopeProvider>().ExternallyOwned();
            b.RegisterInstance(wsm).As<IWorldStateManager>().ExternallyOwned();
            b.AddModule(validationModules);
            b.AddModule(mainProcessingModules);
        });

        IWorldState stateProvider = _processingScope.Resolve<IWorldState>();

        using (stateProvider.BeginScope(IWorldState.PreGenesis))
        {
            stateProvider.CreateAccount(_sender, 1_000_000.Ether);

            stateProvider.CreateAccount(TestItem.AddressB, UInt256.Zero);
            stateProvider.InsertCode(TestItem.AddressB, ContractCode, Spec);

            stateProvider.CreateAccount(TransferTargetAddress, UInt256.One);

            stateProvider.CreateAccount(SloadCallerAddress, UInt256.Zero);
            stateProvider.InsertCode(SloadCallerAddress, SloadSameKeyCode, Spec);
            stateProvider.Set(new StorageCell(SloadCallerAddress, UInt256.Zero), [0x07]);

            stateProvider.CreateAccount(TestItem.AddressE, UInt256.Zero);
            stateProvider.InsertCode(TestItem.AddressE, PushPopOnlyCode, Spec);

            stateProvider.CreateAccount(TestItem.AddressF, UInt256.Zero);
            stateProvider.InsertCode(TestItem.AddressF, TloadSameKeyCode, Spec);

            stateProvider.CreateAccount(BalanceCallerAddress, UInt256.Zero);
            stateProvider.InsertCode(BalanceCallerAddress, BalanceSameAddressCode, Spec);

            stateProvider.CreateAccount(SstoreCallerAddress, UInt256.Zero);
            stateProvider.InsertCode(SstoreCallerAddress, SstoreDirtyCode, Spec);
            stateProvider.Set(new StorageCell(SstoreCallerAddress, UInt256.Zero), [0x05]);

            stateProvider.CreateAccount(Create2CallerAddress, UInt256.Zero);
            stateProvider.InsertCode(Create2CallerAddress, Create2ImmediateAccessCode, Spec);

            stateProvider.CreateAccount(PrecompileCallCallerAddress, UInt256.Zero);
            stateProvider.InsertCode(PrecompileCallCallerAddress, StaticCallPrecompileCode, Spec);

            stateProvider.CreateAccount(EoaTargetAddress, UInt256.One);
            stateProvider.CreateAccount(EoaCallCallerAddress, UInt256.Zero);
            stateProvider.InsertCode(EoaCallCallerAddress, StaticCallEoaCode, Spec);

            stateProvider.CreateAccount(CallCallerAddress, UInt256.Zero);
            stateProvider.InsertCode(CallCallerAddress, StaticCallSameAddressCode, Spec);

            stateProvider.CreateAccount(ExtCodeHashCallerAddress, UInt256.Zero);
            stateProvider.InsertCode(ExtCodeHashCallerAddress, ExtCodeHashSameAddressCode, Spec);

            stateProvider.CreateAccount(ExtCodeSizeCallerAddress, UInt256.Zero);
            stateProvider.InsertCode(ExtCodeSizeCallerAddress, ExtCodeSizeSameAddressCode, Spec);

            stateProvider.CreateAccount(Eip7002Constants.WithdrawalRequestPredeployAddress, UInt256.Zero);
            stateProvider.InsertCode(Eip7002Constants.WithdrawalRequestPredeployAddress, StopCode, Spec);
            stateProvider.CreateAccount(Eip7251Constants.ConsolidationRequestPredeployAddress, UInt256.Zero);
            stateProvider.InsertCode(Eip7251Constants.ConsolidationRequestPredeployAddress, StopCode, Spec);

            // Amsterdam reads two more system contracts at the end of every block.
            stateProvider.CreateAccount(Eip8282Constants.BuilderDepositRequestPredeployAddress, UInt256.Zero);
            stateProvider.InsertCode(Eip8282Constants.BuilderDepositRequestPredeployAddress, StopCode, Spec);
            stateProvider.CreateAccount(Eip8282Constants.BuilderExitRequestPredeployAddress, UInt256.Zero);
            stateProvider.InsertCode(Eip8282Constants.BuilderExitRequestPredeployAddress, StopCode, Spec);

            stateProvider.Commit(Spec);
            stateProvider.CommitTree(0);

            _parentHeader = Build.A.BlockHeader
                .WithNumber(0)
                .WithStateRoot(stateProvider.StateRoot)
                .WithGasLimit(30_000_000)
                .TestObject;
        }

        _branchProcessor = _processingScope.Resolve<IBranchProcessor>();

        VerifyScenariosExecute();
    }

    /// <summary>Refuses to benchmark a failed execution.</summary>
    /// <remarks>Everything runs under <see cref="ProcessingOptions.NoValidation"/>, where a failed
    /// transaction still produces a number — silently, as fork repricing has already demonstrated on
    /// several scenarios. Receipt status is exact, so it also catches reverts and partial failures in a
    /// heterogeneous block, which an aggregate gas heuristic cannot. (A CREATE2 collision would not fail
    /// the transaction — the outer frame survives — so the create scenario excludes it by construction:
    /// distinct salts, one transaction, the same parent root every run.)</remarks>
    private void VerifyScenariosExecute()
    {
        foreach (Block block in (Block[])[
            _singleTransferBlock, _transfers50Block, _transfers200Block, _eip1559_200Block,
            _accessList50Block, _contractDeploy10Block, _contractCall200Block,
            _sloadSameKeyBlock, _sloadSameKeyNoPrewarmBlock, _pushPopOnlyBlock, _tloadSameKeyBlock,
            _balanceSameAddressBlock, _extCodeSizeBlock, _extCodeHashBlock,
            _staticCallBlock, _staticCallEoaBlock, _staticCallPrecompileBlock,
            _sstoreDirtyBlock, _create2Block, _mixedBlock])
        {
            BlockReceiptsTracer tracer = new();
            _branchProcessor.Process(_parentHeader, [block], ProcessingOptions.NoValidation, tracer);

            foreach (TxReceipt receipt in tracer.TxReceipts)
            {
                if (receipt.StatusCode != StatusCode.Success)
                {
                    throw new InvalidOperationException(
                        $"Scenario transaction to {receipt.Recipient?.ToString() ?? "create"} failed under {Fork} ({receipt.Error}) - the benchmark number would be meaningless.");
                }
            }
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _processingScope.Dispose();
        _container.Dispose();
    }

    // ── Benchmarks ────────────────────────────────────────────────────────

    [Benchmark(OperationsPerInvoke = N_LARGE)]
    public Block[] EmptyBlock()
    {
        Block[] result = null!;
        for (int i = 0; i < N_LARGE; i++)
            result = _branchProcessor.Process(_parentHeader, [_emptyBlock],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N_LARGE)]
    public Block[] SingleTransfer()
    {
        Block[] result = null!;
        for (int i = 0; i < N_LARGE; i++)
            result = _branchProcessor.Process(_parentHeader, [_singleTransferBlock],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N_MEDIUM)]
    public Block[] Transfers_50()
    {
        Block[] result = null!;
        for (int i = 0; i < N_MEDIUM; i++)
            result = _branchProcessor.Process(_parentHeader, [_transfers50Block],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = N_SMALL)]
    public Block[] Transfers_200()
    {
        Block[] result = null!;
        for (int i = 0; i < N_SMALL; i++)
            result = _branchProcessor.Process(_parentHeader, [_transfers200Block],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N_SMALL)]
    public Block[] Eip1559_200()
    {
        Block[] result = null!;
        for (int i = 0; i < N_SMALL; i++)
            result = _branchProcessor.Process(_parentHeader, [_eip1559_200Block],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N_MEDIUM)]
    public Block[] AccessList_50()
    {
        Block[] result = null!;
        for (int i = 0; i < N_MEDIUM; i++)
            result = _branchProcessor.Process(_parentHeader, [_accessList50Block],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N_LARGE)]
    public Block[] ContractDeploy_10()
    {
        Block[] result = null!;
        for (int i = 0; i < N_LARGE; i++)
            result = _branchProcessor.Process(_parentHeader, [_contractDeploy10Block],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    /// <summary>20,000 warm reads of one slot, so the per-SLOAD cost is the whole measurement.</summary>
    [Benchmark(OperationsPerInvoke = N_SMALL)]
    public Block[] Sload_SameKey()
    {
        Block[] result = null!;
        for (int i = 0; i < N_SMALL; i++)
            result = _branchProcessor.Process(_parentHeader, [_sloadSameKeyBlock],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    /// <summary>The same reads in two transactions, which is under the pre-warmer's 3-transaction trigger.</summary>
    /// <remarks>Both variants are normalised by their own read count, and the fixed per-block cost is
    /// amortised over fewer reads here, which biases against this one.</remarks>
    [Benchmark(OperationsPerInvoke = N_SMALL)]
    public Block[] Sload_SameKey_NoPrewarm()
    {
        Block[] result = null!;
        for (int i = 0; i < N_SMALL; i++)
            result = _branchProcessor.Process(_parentHeader, [_sloadSameKeyNoPrewarmBlock],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    /// <summary>The control for <see cref="Sload_SameKey"/>: identical loop, no SLOAD.</summary>
    [Benchmark(OperationsPerInvoke = N_SMALL)]
    public Block[] PushPop_Only()
    {
        Block[] result = null!;
        for (int i = 0; i < N_SMALL; i++)
            result = _branchProcessor.Process(_parentHeader, [_pushPopOnlyBlock],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N_SMALL)]
    public Block[] Tload_SameKey()
    {
        Block[] result = null!;
        for (int i = 0; i < N_SMALL; i++)
            result = _branchProcessor.Process(_parentHeader, [_tloadSameKeyBlock],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N_SMALL)]
    public Block[] Balance_SameAddress()
    {
        Block[] result = null!;
        for (int i = 0; i < N_SMALL; i++)
            result = _branchProcessor.Process(_parentHeader, [_balanceSameAddressBlock],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N_SMALL)]
    public Block[] ExtCodeSize_SameAddress()
    {
        Block[] result = null!;
        for (int i = 0; i < N_SMALL; i++)
            result = _branchProcessor.Process(_parentHeader, [_extCodeSizeBlock],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N_SMALL)]
    public Block[] ExtCodeHash_SameAddress()
    {
        Block[] result = null!;
        for (int i = 0; i < N_SMALL; i++)
            result = _branchProcessor.Process(_parentHeader, [_extCodeHashBlock],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N_SMALL)]
    public Block[] StaticCall_SameAddress()
    {
        Block[] result = null!;
        for (int i = 0; i < N_SMALL; i++)
            result = _branchProcessor.Process(_parentHeader, [_staticCallBlock],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N_SMALL)]
    public Block[] StaticCall_ToEoa()
    {
        Block[] result = null!;
        for (int i = 0; i < N_SMALL; i++)
            result = _branchProcessor.Process(_parentHeader, [_staticCallEoaBlock],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N_SMALL)]
    public Block[] StaticCall_ToPrecompile()
    {
        Block[] result = null!;
        for (int i = 0; i < N_SMALL; i++)
            result = _branchProcessor.Process(_parentHeader, [_staticCallPrecompileBlock],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N_SMALL)]
    public Block[] Sstore_DirtyTransitions()
    {
        Block[] result = null!;
        for (int i = 0; i < N_SMALL; i++)
            result = _branchProcessor.Process(_parentHeader, [_sstoreDirtyBlock],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N_SMALL)]
    public Block[] Create2_ImmediateAccess()
    {
        Block[] result = null!;
        for (int i = 0; i < N_SMALL; i++)
            result = _branchProcessor.Process(_parentHeader, [_create2Block],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N_SMALL)]
    public Block[] ContractCall_200()
    {
        Block[] result = null!;
        for (int i = 0; i < N_SMALL; i++)
            result = _branchProcessor.Process(_parentHeader, [_contractCall200Block],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N_SMALL)]
    public Block[] MixedBlock()
    {
        Block[] result = null!;
        for (int i = 0; i < N_SMALL; i++)
            result = _branchProcessor.Process(_parentHeader, [_mixedBlock],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    // ── Block builder ─────────────────────────────────────────────────────

    private Block BuildBlock(params Transaction[] transactions)
        => Build.A.Block
            .WithHeader(_header)
            .WithTransactions(transactions)
            .TestObject;

    // ── Transaction builders ──────────────────────────────────────────────

    private Transaction[] BuildLegacyTransfers(int count, ulong startNonce)
    {
        Transaction[] txs = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            txs[i] = Build.A.Transaction
                .WithNonce(startNonce + (ulong)i)
                .WithTo(TransferTargetAddress)
                .WithValue(1.Wei)
                .WithGasLimit(21_000)
                .WithGasPrice(2.GWei)
                .SignedAndResolved(_senderKey)
                .TestObject;
        }
        return txs;
    }

    private Transaction[] BuildEip1559Transfers(int count, ulong startNonce)
    {
        Transaction[] txs = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            txs[i] = Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithNonce(startNonce + (ulong)i)
                .WithTo(TransferTargetAddress)
                .WithValue(1.Wei)
                .WithGasLimit(21_000)
                .WithMaxFeePerGas(2.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .SignedAndResolved(_senderKey)
                .TestObject;
        }
        return txs;
    }

    private Transaction[] BuildAccessListTxs(int count, ulong startNonce)
    {
        Transaction[] txs = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            txs[i] = Build.A.Transaction
                .WithType(TxType.AccessList)
                .WithNonce(startNonce + (ulong)i)
                .WithTo(TransferTargetAddress)
                .WithValue(1.Wei)
                .WithGasLimit(100_000) // Amsterdam prices the access-list intrinsic above the classic 50k
                .WithGasPrice(2.GWei)
                .WithAccessList(SampleAccessList)
                .SignedAndResolved(_senderKey)
                .TestObject;
        }
        return txs;
    }

    private Transaction[] BuildContractDeploys(int count, ulong startNonce)
    {
        Transaction[] txs = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            txs[i] = Build.A.Transaction
                .WithNonce(startNonce + (ulong)i)
                .WithTo(null)
                .WithData(ContractCode)
                .WithGasLimit(600_000) // Amsterdam state gas makes a deploy several times Osaka's price; the setup guard verifies it executes
                .WithGasPrice(2.GWei)
                .SignedAndResolved(_senderKey)
                .TestObject;
        }
        return txs;
    }

    private Transaction[] BuildSloadCalls(int count, ulong startNonce) => BuildCallsTo(SloadCallerAddress, count, startNonce);

    private Transaction[] BuildCallsTo(Address to, int count, ulong startNonce, ulong gasLimit = 500_000)
    {
        Transaction[] txs = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            txs[i] = Build.A.Transaction
                .WithNonce(startNonce + (ulong)i)
                .WithTo(to)
                .WithGasLimit(gasLimit)
                .WithGasPrice(2.GWei)
                .SignedAndResolved(_senderKey)
                .TestObject;
        }
        return txs;
    }

    private Transaction[] BuildContractCalls(int count, ulong startNonce)
    {
        Transaction[] txs = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            txs[i] = Build.A.Transaction
                .WithNonce(startNonce + (ulong)i)
                .WithTo(TestItem.AddressB)
                .WithGasLimit(50_000)
                .WithGasPrice(2.GWei)
                .SignedAndResolved(_senderKey)
                .TestObject;
        }
        return txs;
    }
}
