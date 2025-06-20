// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.Evm.CodeAnalysis.IL;
using Microsoft.Diagnostics.Runtime;
using Nethermind.Evm.Config;
using Microsoft.Diagnostics.Tracing.Parsers;
using Nethermind.Evm.Tracing.GethStyle;
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Collections.Generic;
using Nethermind.Blockchain;
using static Nethermind.Evm.VirtualMachine;
using System.Reflection;
using Nethermind.Core.Test.Builders;
using System.Linq;
using Microsoft.Extensions.Options;
using BenchmarkDotNet.Running;
using Nethermind.Specs.Forks;
using Nethermind.Abi;
using Nethermind.Evm.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Crypto;
using Nethermind.Evm.Test;
using Nethermind.Evm.TransactionProcessing;
using CommandLine;
using Google.Protobuf.WellKnownTypes;
using Iced.Intel;
using static Microsoft.FSharp.Core.ByRefKinds;
using Nethermind.Consensus.Processing;
using Grpc.Core;
using Nethermind.Core.Collections;
using Nethermind.State.Tracing;
using Nethermind.Core.Eip2930;
using Nethermind.Trie;
using Nethermind.Evm.Test.ILEVM;
using System.Diagnostics.CodeAnalysis;

#nullable enable

namespace Nethermind.Evm.Benchmark
{

    public class Weth : IlEvmBenchmarkTransactions
    {

        public override byte[] bytecode => Bytes.FromHexString(
            "0x6060604052600436106100af576000357c0100000000000000000000000000000000000000000000000000000000900463ffffffff16806306fdde03146100b9578063095ea7b31461014757806318160ddd146101a157806323b872dd146101ca5780632e1a7d4d14610243578063313ce5671461026657806370a082311461029557806395d89b41146102e2578063a9059cbb14610370578063d0e30db0146103ca578063dd62ed3e146103d4575b6100b7610440565b005b34156100c457600080fd5b6100cc6104dd565b6040518080602001828103825283818151815260200191508051906020019080838360005b8381101561010c5780820151818401526020810190506100f1565b50505050905090810190601f1680156101395780820380516001836020036101000a031916815260200191505b509250505060405180910390f35b341561015257600080fd5b610187600480803573ffffffffffffffffffffffffffffffffffffffff1690602001909190803590602001909190505061057b565b604051808215151515815260200191505060405180910390f35b34156101ac57600080fd5b6101b461066d565b6040518082815260200191505060405180910390f35b34156101d557600080fd5b610229600480803573ffffffffffffffffffffffffffffffffffffffff1690602001909190803573ffffffffffffffffffffffffffffffffffffffff1690602001909190803590602001909190505061068c565b604051808215151515815260200191505060405180910390f35b341561024e57600080fd5b61026460048080359060200190919050506109d9565b005b341561027157600080fd5b610279610b05565b604051808260ff1660ff16815260200191505060405180910390f35b34156102a057600080fd5b6102cc600480803573ffffffffffffffffffffffffffffffffffffffff16906020019091905050610b18565b6040518082815260200191505060405180910390f35b34156102ed57600080fd5b6102f5610b30565b6040518080602001828103825283818151815260200191508051906020019080838360005b8381101561033557808201518184015260208101905061031a565b50505050905090810190601f1680156103625780820380516001836020036101000a031916815260200191505b509250505060405180910390f35b341561037b57600080fd5b6103b0600480803573ffffffffffffffffffffffffffffffffffffffff16906020019091908035906020019091905050610bce565b604051808215151515815260200191505060405180910390f35b6103d2610440565b005b34156103df57600080fd5b61042a600480803573ffffffffffffffffffffffffffffffffffffffff1690602001909190803573ffffffffffffffffffffffffffffffffffffffff16906020019091905050610be3565b6040518082815260200191505060405180910390f35b34600360003373ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff168152602001908152602001600020600082825401925050819055503373ffffffffffffffffffffffffffffffffffffffff167fe1fffcc4923d04b559f4d29a8bfc6cda04eb5b0d3c460751c2402c5c5cc9109c346040518082815260200191505060405180910390a2565b60008054600181600116156101000203166002900480601f0160208091040260200160405190810160405280929190818152602001828054600181600116156101000203166002900480156105735780601f1061054857610100808354040283529160200191610573565b820191906000526020600020905b81548152906001019060200180831161055657829003601f168201915b505050505081565b600081600460003373ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff16815260200190815260200160002060008573ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff168152602001908152602001600020819055508273ffffffffffffffffffffffffffffffffffffffff163373ffffffffffffffffffffffffffffffffffffffff167f8c5be1e5ebec7d5bd14f71427d1e84f3dd0314c0f7b2291e5b200ac8c7c3b925846040518082815260200191505060405180910390a36001905092915050565b60003073ffffffffffffffffffffffffffffffffffffffff1631905090565b600081600360008673ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff16815260200190815260200160002054101515156106dc57600080fd5b3373ffffffffffffffffffffffffffffffffffffffff168473ffffffffffffffffffffffffffffffffffffffff16141580156107b457507fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff600460008673ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff16815260200190815260200160002060003373ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff1681526020019081526020016000205414155b156108cf5781600460008673ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff16815260200190815260200160002060003373ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff168152602001908152602001600020541015151561084457600080fd5b81600460008673ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff16815260200190815260200160002060003373ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff168152602001908152602001600020600082825403925050819055505b81600360008673ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff1681526020019081526020016000206000828254039250508190555081600360008573ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff168152602001908152602001600020600082825401925050819055508273ffffffffffffffffffffffffffffffffffffffff168473ffffffffffffffffffffffffffffffffffffffff167fddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef846040518082815260200191505060405180910390a3600190509392505050565b80600360003373ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff1681526020019081526020016000205410151515610a2757600080fd5b80600360003373ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff168152602001908152602001600020600082825403925050819055503373ffffffffffffffffffffffffffffffffffffffff166108fc829081150290604051600060405180830381858888f193505050501515610ab457600080fd5b3373ffffffffffffffffffffffffffffffffffffffff167f7fcf532c15f0a6db0bd6d0e038bea71d30d808c7d98cb3bf7268a95bf5081b65826040518082815260200191505060405180910390a250565b600260009054906101000a900460ff1681565b60036020528060005260406000206000915090505481565b60018054600181600116156101000203166002900480601f016020809104026020016040519081016040528092919081815260200182805460018160011615610100020316600290048015610bc65780601f10610b9b57610100808354040283529160200191610bc6565b820191906000526020600020905b815481529060010190602001808311610ba957829003601f168201915b505050505081565b6000610bdb33848461068c565b905092915050565b60046020528160005260406000206020528060005260406000206000915091505054815600a165627a7a72305820deb4c2ccab3c2fdca32ab3f46728389c2fe2c165d5fafa07661e4e004f6c344a0029");
        public override UInt256 AddressAStorage => new(9055108917293287279, 16116806354556711235, 16080230447512486757, 16153194885772539840);
        public override UInt256 AddressBStorage => new(6336954612432966780, 13641044163492443802,
            12866168085374088197, 1518696171257252784);

        public override ulong Value { get; set; } = 10UL;

        public override Transaction[] TransactionSet => _transactions;
        private readonly Transaction[] _transactions;
        public Weth()
        {

            SeedCell(AddressABalanceCell, 1000);
            SeedCell(AddressBBalanceCell, 0);

            _transactions = new[]
            {
                BuildTransferTxFrom(AddressA, AddressAKey, AddressB, ContractAddress, 1000),
                BuildTransferTxFrom(AddressB, AddressBKey, AddressA, ContractAddress, 1000)
            };
        }

        private static readonly byte[] TransferSelector = Bytes.FromHexString("0xa9059cbb");

        private Transaction BuildTransferTxFrom(Address sender, PrivateKey senderKey, Address receiver, Address contract, UInt256 value)
        {
            byte[] data = TransferSelector
                .Concat(receiver.Bytes.PadLeft(32))
                .Concat(value.ToBigEndian().PadLeft(32))
                .ToArray();

            return BuildTransaction(data, value, sender, senderKey, contract);
        }

    }

    public class Fib : IlEvmBenchmarkCode
    {
        protected override byte[] GenerateBytecode(byte[] argBytes) => FibBytecode(argBytes);


        [Params(1UL, 23UL, 101UL, 1023UL, 2047UL, 4999UL)]
        public override ulong Value { get; set; } = 10UL;

        private static byte[] FibBytecode(byte[] argBytes) => Prepare.EvmCode
            .JUMPDEST()
            .PUSHx([0, 0])
            .POP()
            .PushData(argBytes)
            .COMMENT("1st/2nd fib number")
            .PushData(0)
            .PushData(1)
            .COMMENT("MAINLOOP:")
            .JUMPDEST()
            .DUPx(3)
            .ISZERO()
            .PushData(5 + 26 + argBytes.Length)
            .JUMPI()
            .COMMENT("fib step")
            .DUPx(2)
            .DUPx(2)
            .ADD()
            .SWAPx(2)
            .POP()
            .SWAPx(1)
            .COMMENT("decrement fib step counter")
            .SWAPx(2)
            .PushData(1)
            .SWAPx(1)
            .SUB()
            .SWAPx(2)
            .PushData(5 + 5 + argBytes.Length).COMMENT("goto MAINLOOP")
            .JUMP()
            .COMMENT("CLEANUP:")
            .JUMPDEST()
            .SWAPx(2)
            .POP()
            .POP()
            .COMMENT("done: requested fib number is the only element on the stack!")
            .STOP()
            .Done;

    }

    public class Prime : IlEvmBenchmarkCode
    {
        protected override byte[] GenerateBytecode(byte[] argBytes) => isPrimeBytecode(argBytes);

        [Params(1UL, 23UL, 1023UL, 8000009UL, 16000057UL)]
        public override ulong Value { get; set; } = 10UL;

        static byte[] isPrimeBytecode(byte[] argBytes) => Prepare.EvmCode
                        .JUMPDEST()
                        .PUSHx([0])
                        .POP()

                        .PUSHx(argBytes)
                        .COMMENT("Store variable(n) in Memory")
                        .MSTORE(0)
                        .COMMENT("Store Indexer(i) in Memory")
                        .PushData(2)
                        .MSTORE(32)
                        .COMMENT("We mark this place as a GOTO section")
                        .JUMPDEST()
                        .COMMENT("We check if i * i < n")
                        .MLOAD(32)
                        .DUPx(1)
                        .MUL()
                        .MLOAD(0)
                        .LT()
                        .PushData(4 + 47 + argBytes.Length)
                        .JUMPI()
                        .COMMENT("We check if n % i == 0")
                        .MLOAD(32)
                        .MLOAD(0)
                        .MOD()
                        .ISZERO()
                        .DUPx(1)
                        .COMMENT("if 0 we jump to the end")
                        .PushData(4 + 51 + argBytes.Length)
                        .JUMPI()
                        .POP()
                        .COMMENT("increment Indexer(i)")
                        .MLOAD(32)
                        .ADD(1)
                        .MSTORE(32)
                        .COMMENT("Loop back to top of conditional loop")
                        .PushData(4 + 9 + argBytes.Length)
                        .JUMP()
                        .COMMENT("return 0")
                        .JUMPDEST()
                        .PushData(0)
                        .SSTORE(0)
                        .STOP()
                        .JUMPDEST()
                        .SSTORE(0)
                        .STOP()
                        .Done;
    }


    public abstract class IlEvmBenchmarkTransactions : IlEvmBenchmarkBase
    {
        protected virtual byte[] GenerateBytecode(byte[] argBytes) => Array.Empty<byte>();

        public virtual ulong Value { get; set; } = 1000UL;

        public void GlobalSetup<TIsOptimizing>() where TIsOptimizing : VirtualMachine.IIsOptimizing
        {
            Initialize();
            SetMode<TIsOptimizing>();
            InsertCode(bytecode, ContractAddress);
            BuildBlock();
            IterationStepTxs();
            SeedAccounts();
            if (Mode == STD) IlAnalyzer.StopPrecompilerBackgroundThread(vmConfigOptimizing!);
        }

        [GlobalSetup(Target = nameof(ExecuteCodeStd))]
        public void GlobalSetupStd() => GlobalSetup<NotOptimizing>();

        [GlobalSetup(Target = nameof(ExecuteCodeAot))]
        public void GlobalSetupAot() => GlobalSetup<IsPrecompiling>();

        [BenchmarkCategory("STD"), Benchmark(Baseline = true)]
        public void ExecuteCodeStd() => RunTxs();

        [BenchmarkCategory("AOT"), Benchmark(Baseline = false)]
        public void ExecuteCodeAot() => RunTxs();

        [IterationCleanup]
        public void Cleanup()
        {
            _evmState?.Dispose();
            _stateProvider!.Commit(_specProvider!.GenesisSpec);
            snapshot = _stateProvider!.TakeSnapshot();
        }
    }



    public abstract class IlEvmBenchmarkCode : IlEvmBenchmarkBase
    {
        protected virtual byte[] GenerateBytecode(byte[] argBytes) => Array.Empty<byte>();

        public virtual ulong Value { get; set; } = 1000UL;

        public void GlobalSetup<TIsOptimizing>() where TIsOptimizing : VirtualMachine.IIsOptimizing
        {
            Initialize();
            SetMode<TIsOptimizing>();
            byte[] bytes = new byte[32];
            new UInt256(Value).ToBigEndian(bytes);
            var argBytes = bytes.WithoutLeadingZeros().ToArray();
            SetCode(GenerateBytecode(argBytes));


        }

        [GlobalSetup(Target = nameof(ExecuteCodeStd))]
        public void GlobalSetupStd() => GlobalSetup<NotOptimizing>();

        [GlobalSetup(Target = nameof(ExecuteCodeAot))]
        public void GlobalSetupAot() => GlobalSetup<IsPrecompiling>();

        [IterationSetup]
        public void Setup() => IterationStep(); // rent evm state

        [BenchmarkCategory("STD"), Benchmark(Baseline = true)]
        public void ExecuteCodeStd() => Run();

        [BenchmarkCategory("AOT"), Benchmark(Baseline = false)]
        public void ExecuteCodeAot() => Run();

        [IterationCleanup]
        public void Cleanup() => _evmState?.Dispose();
    }




    public class IlEvmBenchmarkBase
    {
        public const string AOT = "AOT";
        public const string STD = "STD";
        public string Mode { get; set; } = "AOT";



        protected ISpecProvider _specProvider = MainnetSpecProvider.Instance;
        public virtual byte[] bytecode => [];

        protected Address ContractAddress => new Address(Keccak.Compute(bytecode));

        private ICodeInfoRepository? codeInfoRepository;
        protected IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec((ForkActivation)MainnetSpecProvider.PragueActivation);
        private ITxTracer _txTracer = NullTxTracer.Instance;
        private ExecutionEnvironment _environment;
        private VirtualMachine<VirtualMachine.NotTracing, IsPrecompiling>? _virtualMachineOptimizing;
        private VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.NotOptimizing>? _virtualMachineNotOptimizing;
        private BlockHeader _header = new BlockHeader(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One, MainnetSpecProvider.ParisBlockNumber, Int64.MaxValue, 1UL, Bytes.Empty);
        private IBlockhashProvider _blockhashProvider = new TestBlockhashProvider(MainnetSpecProvider.Instance);
        protected EvmState? _evmState;
        private ILogger _logger;
        private byte[]? targetCode;
        protected VMConfig? vmConfigOptimizing;
        protected VMConfig? vmConfigNotOptimizing;
        private CodeInfo? driverCodeInfo;
        private CodeInfo? targetCodeInfo;

        protected virtual StorageCell AddressABalanceCell => new(ContractAddress, AddressAStorage);
        protected UInt256 AddressABalanceCellValue => new(1000UL);

        public static readonly PrivateKey AddressAKey = TestItem.PrivateKeyA;
        public static Address AddressA = AddressAKey.Address;
        public virtual UInt256 AddressAStorage => new(0UL);

        // Represents some other address
        public static readonly PrivateKey AddressBKey = TestItem.PrivateKeyD;
        public static readonly Address AddressB = AddressBKey.Address;
        public virtual UInt256 AddressBStorage => new(0UL);
        protected StorageCell AddressBBalanceCell => new(ContractAddress, AddressBStorage);
        protected virtual UInt256 AddressBBalanceCellValue => new(0UL);

        private EthereumEcdsa? _ethereumEcdsa;
        // private VMConfig vmConfig;

        private Block? _targetBlock;
        public virtual Transaction[] TransactionSet { get; set; } = [];

        protected ForkActivation Activation => (BlockNumber, Timestamp);
        protected long BlockNumber { get; } = MainnetSpecProvider.ParisBlockNumber;
        protected ulong Timestamp => MainnetSpecProvider.PragueBlockTimestamp;
        protected long gasLimit => 10_000_000L;

        protected Snapshot? snapshot;

        protected IWorldState? _stateProvider;

        protected virtual UInt256 GetStorageValue => 1000;
        protected virtual UInt256 GetStorageOriginalValue => 0;

        private ExecutionEnvironment[]? _prebuiltEnvs;
        private readonly StackAccessTracker _tracker = new();

        public IlEvmBenchmarkBase()
        {
            Initialize();
        }


        public void SetMode(string mode)
        {
            Mode = mode;
        }

        public void Initialize()
        {

            vmConfigOptimizing = new VMConfig();
            vmConfigNotOptimizing = new VMConfig();

            vmConfigOptimizing.IsILEvmEnabled = true;
            vmConfigOptimizing.IlEvmEnabledMode = ILMode.DYNAMIC_AOT_MODE;

            vmConfigOptimizing.IlEvmAnalysisThreshold = 1;
            vmConfigOptimizing.IsIlEvmAggressiveModeEnabled = true;
            TrieStore trieStore = new(new MemDb(), new OneLoggerLogManager(NullLogger.Instance));
            IKeyValueStore codeDb = new MemDb();

            _stateProvider = new MockWorldState(GetStorageValue, GetStorageOriginalValue, new WorldState(trieStore, codeDb, new OneLoggerLogManager(NullLogger.Instance)));
            //_stateProvider = new WorldState(trieStore, codeDb, new OneLoggerLogManager(NullLogger.Instance));

            codeInfoRepository = new TestCodeInfoRepository();


            _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId);

            ILogManager logmanager = NullLogManager.Instance;

            _logger = logmanager.GetClassLogger();

            _virtualMachineOptimizing = new VirtualMachine<VirtualMachine.NotTracing, IsPrecompiling>(_blockhashProvider, codeInfoRepository, MainnetSpecProvider.Instance, vmConfigOptimizing, _logger);
            _virtualMachineNotOptimizing = new VirtualMachine<VirtualMachine.NotTracing, NotOptimizing>(_blockhashProvider, codeInfoRepository, MainnetSpecProvider.Instance, vmConfigNotOptimizing, _logger);



        }


        protected VMConfig vmConfig
        {
            get => Mode == "AOT" ? vmConfigOptimizing! : vmConfigNotOptimizing!;
            set
            {
                if (Mode == "AOT")
                    vmConfigOptimizing = value;
                else
                    vmConfigNotOptimizing = value;
            }
        }


        public void SetCode(byte[] _targetCode, byte[]? _driverCode = null)
        {
            targetCode = _targetCode;
            var (address, codeHash) = InsertCode(targetCode);

            var driver =
                Prepare.EvmCode
                .COMMENT("BEGIN")
                .Call(address, 1000000)
                .POP()
                .COMMENT("END")
                .STOP()
                .Done;
            var hashcode = Keccak.Compute(driver);
            driverCodeInfo = new CodeInfo(driver, hashcode);
            targetCodeInfo = codeInfoRepository!.GetCachedCodeInfo(_stateProvider!, address, Prague.Instance, out _);
            if (Mode == AOT)
            {

                IlAnalyzer.StartPrecompilerBackgroundThread(vmConfigOptimizing ?? new VMConfig(), NullLogger.Instance);
                IlAnalyzer.Analyse(driverCodeInfo, vmConfigOptimizing!.IlEvmEnabledMode, vmConfigOptimizing!, NullLogger.Instance);
                IlAnalyzer.Analyse(targetCodeInfo, vmConfigOptimizing!.IlEvmEnabledMode, vmConfigOptimizing!, NullLogger.Instance);
            }

        }


        public void SetMode<TIsOptimizing>() where TIsOptimizing : VirtualMachine.IIsOptimizing
        {
            if (typeof(TIsOptimizing) != typeof(VirtualMachine.NotOptimizing))
            {
                Mode = AOT; // to avoid using the check above everywhere
            }
            else
            {
                Mode = STD;
            }
        }

        public void IterationStep()
        {
            _environment = new ExecutionEnvironment
            (
                executingAccount: Address.Zero,
                codeSource: Address.Zero,
                caller: Address.Zero,
                codeInfo: driverCodeInfo!,
                value: 0,
                transferValue: 0,
                txExecutionContext: new TxExecutionContext(new BlockExecutionContext(_header, _spec), Address.Zero, 0, [], codeInfoRepository!),
                inputData: default
            );

            _evmState = EvmState.RentTopLevel(long.MaxValue, ExecutionType.TRANSACTION, _stateProvider!.TakeSnapshot(), _environment, new StackAccessTracker());
        }

        protected (Address, ValueHash256) InsertCode(byte[] bytecode, Address? target = null)
        {
            var hashcode = Keccak.Compute(bytecode);
            var address = target ?? new Address(hashcode);

            var spec = Prague.Instance;
            SeedAccount(address);
            _stateProvider!.InsertCode(address, bytecode, spec);
            var codeInfo = codeInfoRepository!.GetCachedCodeInfo(_stateProvider!, address, Prague.Instance, out _);

            targetCodeInfo = codeInfo;
            if (Mode == AOT)
            {
                IlAnalyzer.StartPrecompilerBackgroundThread(vmConfigOptimizing ?? new VMConfig(), NullLogger.Instance);
                IlAnalyzer.Analyse(codeInfo, vmConfigOptimizing!.IlEvmEnabledMode, vmConfigOptimizing!, NullLogger.Instance);

            }

            return (address, hashcode);

        }



        public void IterationStepTxs()
        {
            var txs = _targetBlock!.Transactions;
            var txCount = txs.Length;
            _prebuiltEnvs = new ExecutionEnvironment[txCount];
            var codeInfo = targetCodeInfo ??= codeInfoRepository!.GetCachedCodeInfo(_stateProvider!, ContractAddress, Prague.Instance, out _);
            var blockCtx = new BlockExecutionContext(_targetBlock!.Header, _spec);

            for (int i = 0; i < txCount; i++)
            {
                ref readonly var tx = ref txs[i];

                var ctx = new TxExecutionContext(
                    blockCtx,
                    tx.SenderAddress!,
                    0,
                    Array.Empty<byte[]>(),
                    codeInfoRepository!
                );

                _prebuiltEnvs[i] = new ExecutionEnvironment(
                    executingAccount: ContractAddress,
                    codeSource: ContractAddress,
                    caller: tx.SenderAddress!,
                    codeInfo: codeInfo,
                    value: tx.Value,
                    transferValue: tx.Value,
                    txExecutionContext: in ctx,
                    inputData: tx.Data ?? default
                );
            }
            if (snapshot is null)
            {
                snapshot = _stateProvider!.TakeSnapshot();
            }
            else
            {

                // need to have changes before restoring the snapshot
                _stateProvider!.Restore(snapshot.Value);
            }


        }


        public void RunTxs()
        {
            var txCount = _prebuiltEnvs!.Length;

            for (int i = 0; i < txCount; i++)
            {
                ref readonly var env = ref _prebuiltEnvs[i];

                using var state = EvmState.RentTopLevel(
                    long.MaxValue,
                    ExecutionType.TRANSACTION,
                    snapshot!.Value,
                    env,
                    _tracker
                );

                Run(state);
            }

        }

        public void Run(EvmState? evmState = null)
        {
            var _state = evmState ?? _evmState;

            if (Mode == AOT)
            {
                _virtualMachineOptimizing!.Run<VirtualMachine.NotTracing>(_state!, _stateProvider!, _txTracer);
            }
            else
                _virtualMachineNotOptimizing!.Run<VirtualMachine.NotTracing>(_state!, _stateProvider!, _txTracer);

        }

        protected void SeedCell(StorageCell cell, UInt256 value)
        {
            _stateProvider!.Set(cell, value.ToBigEndian().WithoutLeadingZeros().ToArray());
        }

        protected void BuildBlock()
        {

            Transaction[] txList =
                Enumerable.Repeat(TransactionSet, 100).SelectMany(x => x).ToArray();

            var senderRecipientAndMiner = SenderRecipientAndMiner.Default;
            _targetBlock = Build.A.Block.WithNumber(Activation.BlockNumber)
                .WithTimestamp(Activation.Timestamp ?? 0)
                .WithTransactions(txList)
                .WithGasLimit(gasLimit)
                .WithBeneficiary(senderRecipientAndMiner.Miner)
                .WithBlobGasUsed(0)
                .WithExcessBlobGas(0)
                .WithParentBeaconBlockRoot(TestItem.KeccakG)
                .WithExcessBlobGas(0)
                .TestObject;
        }



        protected virtual void SeedAccount(Address address, UInt256? value = null)
        {
            var _value = value ?? 10000.Ether();
            if (!_stateProvider!.AccountExists(address))
                _stateProvider!.CreateAccount(address, _value);
            else
                _stateProvider!.AddToBalance(address, _value, _specProvider.GenesisSpec);

            _stateProvider!.Commit(_specProvider.GenesisSpec);
        }

        protected virtual void SeedAccounts()
        {
            SeedAccount(AddressA);
            SeedAccount(AddressB);
            SeedAccount(ContractAddress);
            SeedAccount(Address.Zero);
        }

        protected Transaction BuildTransaction(byte[] data, UInt256 value, Address sender, PrivateKey senderKey, Address to)
        {

            // checking if account exists - because creating new accounts overwrites already existing accounts,
            // thus overwriting storage roots - essentially clearing the storage slots
            // earlier it used to work - because the cache mapping address:storageTree was never cleared on account of
            // _stateProvider.CommitTrees() not being called. But now the WorldState.CommitTrees which also calls _stateProvider.CommitTrees, clearing the cache.

            Transaction transaction = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithGasPrice(1)
                .WithNonce(_stateProvider!.GetNonce(sender))
                .WithData(data)
                .WithValue(0)
                .To(to)
                .SignedAndResolved(_ethereumEcdsa!, senderKey)
                .TestObject;

            return transaction;
        }


    }

}
