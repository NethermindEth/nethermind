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
using Nethermind.Evm.Test.ILEVM;
using Nethermind.Crypto;
using Nethermind.Evm.Test;
using Nethermind.Evm.TransactionProcessing;
using CommandLine;
using Google.Protobuf.WellKnownTypes;
using Iced.Intel;
using static Microsoft.FSharp.Core.ByRefKinds;
using Nethermind.Consensus.Processing;
using Grpc.Core;

namespace Nethermind.Evm.Benchmark
{
    public interface ILocalSetup
    {
        void Setup();
        void Run();
        void Reset();
    }
    public struct LocalSetup<TIsOptimizing> : ILocalSetup
        where TIsOptimizing : struct, VirtualMachine.IIsOptimizing
    {

        internal delegate CallResult ExecuteCode<TTracingInstructions, TTracingRefunds, TTracingStorage>(EvmState vmState, scoped ref EvmStack<TTracingInstructions> stack, long gasAvailable, IReleaseSpec spec)
            where TTracingInstructions : struct, VirtualMachine.IIsTracing
            where TTracingRefunds : struct, VirtualMachine.IIsTracing
            where TTracingStorage : struct, VirtualMachine.IIsTracing;
        public string Name { get; init; }

        private readonly ICodeInfoRepository codeInfoRepository;
        private IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec((ForkActivation)MainnetSpecProvider.IstanbulBlockNumber);
        private ITxTracer _txTracer = NullTxTracer.Instance;
        private ExecutionEnvironment _environment;
        private VirtualMachine<VirtualMachine.NotTracing, TIsOptimizing> _virtualMachine;
        private BlockHeader _header = new BlockHeader(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One, MainnetSpecProvider.IstanbulBlockNumber, Int64.MaxValue, 1UL, Bytes.Empty);
        private IBlockhashProvider _blockhashProvider = new TestBlockhashProvider(MainnetSpecProvider.Instance);
        private EvmState _evmState;
        private WorldState _stateProvider;
        private ILogger _logger;
        private byte[] bytecode;
        private VMConfig vmConfig;
        private CodeInfo driverCodeInfo;
        public LocalSetup(string name, byte[] _bytecode)
        {
            Name = name;

            vmConfig = new VMConfig();

            vmConfig.IsILEvmEnabled = typeof(TIsOptimizing) != typeof(VirtualMachine.NotOptimizing);
            vmConfig.IlEvmEnabledMode = typeof(TIsOptimizing) == typeof(VirtualMachine.IsPrecompiling)
                ? ILMode.DYNAMIC_AOT_MODE : ILMode.NO_ILVM;

            vmConfig.IlEvmAnalysisThreshold = 1;
            vmConfig.IsIlEvmAggressiveModeEnabled = true;
            TrieStore trieStore = new(new MemDb(), new OneLoggerLogManager(NullLogger.Instance));
            IKeyValueStore codeDb = new MemDb();
            _stateProvider = new WorldState(trieStore, codeDb, new OneLoggerLogManager(NullLogger.Instance));
            _stateProvider.CreateAccount(Address.Zero, 1000.Ether());
            _stateProvider.Commit(_spec);

            bytecode = _bytecode;

            codeInfoRepository = new TestCodeInfoRepository();

            ILogManager logmanager = NullLogManager.Instance;

            _logger = logmanager.GetClassLogger();

            _virtualMachine = new VirtualMachine<VirtualMachine.NotTracing, TIsOptimizing>(_blockhashProvider, codeInfoRepository, MainnetSpecProvider.Instance, vmConfig, _logger);

            var (address, codeHash) = InsertCode(bytecode);

            var driver =
                Prepare.EvmCode
                .COMMENT("BEGIN")
                .Call(address, 1000000)
                .POP()
                .COMMENT("END")
                .STOP()
                .Done;

            var driverCodeinfo = new CodeInfo(driver, codeHash);
            var targetCodeInfo = codeInfoRepository.GetCachedCodeInfo(_stateProvider, address, Prague.Instance, out _);

            if (vmConfig.IsILEvmEnabled)
            {
                IlAnalyzer.Analyse(driverCodeinfo, vmConfig.IlEvmEnabledMode, vmConfig, NullLogger.Instance);
                IlAnalyzer.Analyse(targetCodeInfo, vmConfig.IlEvmEnabledMode, vmConfig, NullLogger.Instance);
            }

            driverCodeInfo = driverCodeinfo;
        }
        private (Address, ValueHash256) InsertCode(byte[] bytecode, Address target = null)
        {
            var hashcode = Keccak.Compute(bytecode);
            var address = target ?? new Address(hashcode);

            var spec = Prague.Instance;
            _stateProvider.CreateAccount(address, 1_000_000_000);
            _stateProvider.InsertCode(address, bytecode, spec);
            return (address, hashcode);
        }

        public void Setup()
        {
            _environment = new ExecutionEnvironment
            (
                executingAccount: Address.Zero,
                codeSource: Address.Zero,
                caller: Address.Zero,
                codeInfo: driverCodeInfo,
                value: 0,
                transferValue: 0,
                txExecutionContext: new TxExecutionContext(new BlockExecutionContext(_header, _spec), Address.Zero, 0, null, codeInfoRepository),
                inputData: default
            );

            _evmState = EvmState.RentTopLevel(long.MaxValue, ExecutionType.TRANSACTION, _stateProvider.TakeSnapshot(), _environment, new StackAccessTracker());
        }

        public void Run()
        {
            _virtualMachine.Run<VirtualMachine.NotTracing>(_evmState, _stateProvider, _txTracer);
        }

        public void Reset()
        {
            //_stateProvider.Reset();
            _evmState.Dispose();
        }

        public override string ToString()
        {
            return Name;
        }

        public ITxTracer TxTracer => _txTracer;
    }

    [MemoryDiagnoser]
    public class EvmBenchmarks()
    {
        static byte[] fibbBytecode(byte[] argBytes) => Prepare.EvmCode
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
                        .STOP()
                        .JUMPDEST()
                        .Done;
        public static IEnumerable<ILocalSetup> GetBenchmarkSamples()
        {
            ILMode mode = (ILMode)Int32.Parse(Environment.GetEnvironmentVariable("NETH.BENCHMARK.BYTECODE.MODE") ?? string.Empty);

            UInt256[] f_args = [1, 23, 101, 1023, 2047, 4999];
            UInt256[] p_args = [1, 23, 1023, 8000009, 16000057];

            foreach (var arg in f_args)
            {
                byte[] bytes = new byte[32];
                arg.ToBigEndian(bytes);
                var argBytes = bytes.WithoutLeadingZeros().ToArray();
                var bytecode = fibbBytecode(argBytes);

                string benchName = $"Fib With args {new UInt256(argBytes)}";

                switch (mode)
                {
                    case ILMode.NO_ILVM:
                        yield return new LocalSetup<NotOptimizing>("ILEVM::1::std::" + benchName, bytecode);
                        break;
                    case ILMode.DYNAMIC_AOT_MODE:
                        yield return new LocalSetup<IsPrecompiling>("ILEVM::2::aot::" + benchName, bytecode);
                        break;
                }
            }


            foreach (var arg in p_args)
            {
                byte[] bytes = new byte[32];
                arg.ToBigEndian(bytes);
                var argBytes = bytes.WithoutLeadingZeros().ToArray();

                string benchName = $"Prim With args {new UInt256(argBytes)}";
                var bytecode = isPrimeBytecode(argBytes);

                switch (mode)
                {
                    case ILMode.NO_ILVM:
                        yield return new LocalSetup<NotOptimizing>("ILEVM::1::std::" + benchName, bytecode);
                        break;
                    case ILMode.DYNAMIC_AOT_MODE:
                        yield return new LocalSetup<IsPrecompiling>("ILEVM::2::aot::" + benchName, bytecode);
                        break;
                }
            }
        }

        [ParamsSource(nameof(GetBenchmarkSamples))]
        public ILocalSetup BenchmarkSetup;

        [IterationSetup]
        public void Setup()
        {
            BenchmarkSetup.Setup();
        }

        [Benchmark]
        public void ExecuteCode()
        {
            BenchmarkSetup.Run();
        }

        [IterationCleanup]
        public void Cleanup()
        {
            BenchmarkSetup.Reset();
        }
    }


    [MemoryDiagnoser]
    public class CustomEvmBenchmarks()
    {
        public IEnumerable<ILocalSetup> GetBenchmarkSamples()
        {
            byte[] bytecode = Bytes.FromHexString(Environment.GetEnvironmentVariable("NETH.BENCHMARK.BYTECODE.CODE") ?? string.Empty);
            ILMode mode = (ILMode)Int32.Parse(Environment.GetEnvironmentVariable("NETH.BENCHMARK.BYTECODE.MODE") ?? string.Empty);
            string BenchmarkName = Environment.GetEnvironmentVariable("NETH.BENCHMARK.BYTECODE.NAME") ?? string.Empty;
            switch (mode)
            {
                case ILMode.NO_ILVM:
                    yield return new LocalSetup<NotOptimizing>("ILEVM::0::std::" + BenchmarkName, bytecode);
                    break;
                case ILMode.DYNAMIC_AOT_MODE:
                    yield return new LocalSetup<IsPrecompiling>("ILEVM::2::aot::" + BenchmarkName, bytecode);
                    break;
            }
        }

        [ParamsSource(nameof(GetBenchmarkSamples))]
        public ILocalSetup BenchmarkSetup;

        [IterationSetup]
        public void Setup()
        {
            BenchmarkSetup.Setup();
        }

        [Benchmark]
        public void ExecuteCode()
        {
            BenchmarkSetup.Run();
        }

        [IterationCleanup]
        public void Cleanup()
        {
            BenchmarkSetup.Reset();
        }
    }

    public class WrapedEthBenchmarksSetup<TIsOptimizing>
        where TIsOptimizing : struct, VirtualMachine.IIsOptimizing
    {
        string Name { get; set; } = "weth";

        private static readonly byte[] bytecode = Bytes.FromHexString(
            "0x6060604052600436106100af576000357c0100000000000000000000000000000000000000000000000000000000900463ffffffff16806306fdde03146100b9578063095ea7b31461014757806318160ddd146101a157806323b872dd146101ca5780632e1a7d4d14610243578063313ce5671461026657806370a082311461029557806395d89b41146102e2578063a9059cbb14610370578063d0e30db0146103ca578063dd62ed3e146103d4575b6100b7610440565b005b34156100c457600080fd5b6100cc6104dd565b6040518080602001828103825283818151815260200191508051906020019080838360005b8381101561010c5780820151818401526020810190506100f1565b50505050905090810190601f1680156101395780820380516001836020036101000a031916815260200191505b509250505060405180910390f35b341561015257600080fd5b610187600480803573ffffffffffffffffffffffffffffffffffffffff1690602001909190803590602001909190505061057b565b604051808215151515815260200191505060405180910390f35b34156101ac57600080fd5b6101b461066d565b6040518082815260200191505060405180910390f35b34156101d557600080fd5b610229600480803573ffffffffffffffffffffffffffffffffffffffff1690602001909190803573ffffffffffffffffffffffffffffffffffffffff1690602001909190803590602001909190505061068c565b604051808215151515815260200191505060405180910390f35b341561024e57600080fd5b61026460048080359060200190919050506109d9565b005b341561027157600080fd5b610279610b05565b604051808260ff1660ff16815260200191505060405180910390f35b34156102a057600080fd5b6102cc600480803573ffffffffffffffffffffffffffffffffffffffff16906020019091905050610b18565b6040518082815260200191505060405180910390f35b34156102ed57600080fd5b6102f5610b30565b6040518080602001828103825283818151815260200191508051906020019080838360005b8381101561033557808201518184015260208101905061031a565b50505050905090810190601f1680156103625780820380516001836020036101000a031916815260200191505b509250505060405180910390f35b341561037b57600080fd5b6103b0600480803573ffffffffffffffffffffffffffffffffffffffff16906020019091908035906020019091905050610bce565b604051808215151515815260200191505060405180910390f35b6103d2610440565b005b34156103df57600080fd5b61042a600480803573ffffffffffffffffffffffffffffffffffffffff1690602001909190803573ffffffffffffffffffffffffffffffffffffffff16906020019091905050610be3565b6040518082815260200191505060405180910390f35b34600360003373ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff168152602001908152602001600020600082825401925050819055503373ffffffffffffffffffffffffffffffffffffffff167fe1fffcc4923d04b559f4d29a8bfc6cda04eb5b0d3c460751c2402c5c5cc9109c346040518082815260200191505060405180910390a2565b60008054600181600116156101000203166002900480601f0160208091040260200160405190810160405280929190818152602001828054600181600116156101000203166002900480156105735780601f1061054857610100808354040283529160200191610573565b820191906000526020600020905b81548152906001019060200180831161055657829003601f168201915b505050505081565b600081600460003373ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff16815260200190815260200160002060008573ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff168152602001908152602001600020819055508273ffffffffffffffffffffffffffffffffffffffff163373ffffffffffffffffffffffffffffffffffffffff167f8c5be1e5ebec7d5bd14f71427d1e84f3dd0314c0f7b2291e5b200ac8c7c3b925846040518082815260200191505060405180910390a36001905092915050565b60003073ffffffffffffffffffffffffffffffffffffffff1631905090565b600081600360008673ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff16815260200190815260200160002054101515156106dc57600080fd5b3373ffffffffffffffffffffffffffffffffffffffff168473ffffffffffffffffffffffffffffffffffffffff16141580156107b457507fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff600460008673ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff16815260200190815260200160002060003373ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff1681526020019081526020016000205414155b156108cf5781600460008673ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff16815260200190815260200160002060003373ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff168152602001908152602001600020541015151561084457600080fd5b81600460008673ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff16815260200190815260200160002060003373ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff168152602001908152602001600020600082825403925050819055505b81600360008673ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff1681526020019081526020016000206000828254039250508190555081600360008573ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff168152602001908152602001600020600082825401925050819055508273ffffffffffffffffffffffffffffffffffffffff168473ffffffffffffffffffffffffffffffffffffffff167fddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef846040518082815260200191505060405180910390a3600190509392505050565b80600360003373ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff1681526020019081526020016000205410151515610a2757600080fd5b80600360003373ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff168152602001908152602001600020600082825403925050819055503373ffffffffffffffffffffffffffffffffffffffff166108fc829081150290604051600060405180830381858888f193505050501515610ab457600080fd5b3373ffffffffffffffffffffffffffffffffffffffff167f7fcf532c15f0a6db0bd6d0e038bea71d30d808c7d98cb3bf7268a95bf5081b65826040518082815260200191505060405180910390a250565b600260009054906101000a900460ff1681565b60036020528060005260406000206000915090505481565b60018054600181600116156101000203166002900480601f016020809104026020016040519081016040528092919081815260200182805460018160011615610100020316600290048015610bc65780601f10610b9b57610100808354040283529160200191610bc6565b820191906000526020600020905b815481529060010190602001808311610ba957829003601f168201915b505050505081565b6000610bdb33848461068c565b905092915050565b60046020528160005260406000206020528060005260406000206000915091505054815600a165627a7a72305820deb4c2ccab3c2fdca32ab3f46728389c2fe2c165d5fafa07661e4e004f6c344a0029");

        private static Address ContractAddress => new Address(Keccak.Compute(bytecode));

        private static readonly byte[] DepositSelector = Bytes.FromHexString("0xd0e30db0");
        private static readonly byte[] TransferSelector = Bytes.FromHexString("0xa9059cbb");
        private static readonly PrivateKey AddressAKey = TestItem.PrivateKeyA;
        private static readonly Address AddressA = AddressAKey.Address;
        private static readonly UInt256 AddressABalanceStorage = new(9055108917293287279, 16116806354556711235, 16080230447512486757, 16153194885772539840);
        private static readonly StorageCell AddressABalanceCell = new(ContractAddress, AddressABalanceStorage);

        // Represents some other address
        private static readonly PrivateKey AddressBKey = TestItem.PrivateKeyD;
        private static readonly Address AddressB = AddressBKey.Address;
        private static readonly UInt256 AddressBStorage = new(6336954612432966780, 13641044163492443802,
            12866168085374088197, 1518696171257252784);
        private static readonly StorageCell AddressBBalanceCell = new(ContractAddress, AddressBStorage);


        private ICodeInfoRepository codeInfoRepository;
        private IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec((ForkActivation)MainnetSpecProvider.IstanbulBlockNumber);
        private ITxTracer _txTracer = NullTxTracer.Instance;
        private VirtualMachine<VirtualMachine.NotTracing, TIsOptimizing> _virtualMachine;
        private ITransactionProcessor _transactionProcessor;
        private ISpecProvider _specProvider = MainnetSpecProvider.Instance;
        private IBlockhashProvider _blockhashProvider = new TestBlockhashProvider(MainnetSpecProvider.Instance);
        private WorldState _stateProvider;
        private ILogger _logger;
        private EthereumEcdsa _ethereumEcdsa;
        private VMConfig vmConfig;

        private Block _targetBlock;

        protected ForkActivation Activation => (BlockNumber, Timestamp);
        protected long BlockNumber { get; } = MainnetSpecProvider.ByzantiumBlockNumber;
        protected ulong Timestamp => 0UL;
        protected long gasLimit => 10_000_000L;

        private Snapshot snapshot;

        public void GlobalSetup()
        {

            string isIlvm = typeof(TIsOptimizing) == typeof(VirtualMachine.IsPrecompiling) ? "-ilvm" : "-no-ilvm";
            Name = "weth" + isIlvm;

            vmConfig = new VMConfig();

            vmConfig.IsILEvmEnabled = typeof(TIsOptimizing) != typeof(VirtualMachine.NotOptimizing);
            vmConfig.IlEvmEnabledMode = typeof(TIsOptimizing) == typeof(VirtualMachine.IsPrecompiling)
                ? ILMode.DYNAMIC_AOT_MODE : ILMode.NO_ILVM;

            vmConfig.IlEvmAnalysisThreshold = 1;
            vmConfig.IsIlEvmAggressiveModeEnabled = true;
            TrieStore trieStore = new(new MemDb(), new OneLoggerLogManager(NullLogger.Instance));
            IKeyValueStore codeDb = new MemDb();
            _stateProvider = new WorldState(trieStore, codeDb, new OneLoggerLogManager(NullLogger.Instance));
            _stateProvider.CreateAccount(Address.Zero, 1000.Ether());
            _stateProvider.Commit(_spec);

            codeInfoRepository = new TestCodeInfoRepository();

            ILogManager logmanager = NullLogManager.Instance;

            _logger = logmanager.GetClassLogger();

            _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId);

            _virtualMachine = new VirtualMachine<VirtualMachine.NotTracing, TIsOptimizing>(_blockhashProvider, codeInfoRepository, MainnetSpecProvider.Instance, vmConfig, _logger);

            _transactionProcessor = new TransactionProcessor(
                _specProvider,
                _stateProvider,
                _virtualMachine,
                codeInfoRepository,
                logmanager);

            InsertCode(bytecode, ContractAddress);

            if (vmConfig.IsILEvmEnabled)
            {
                var targetCodeInfo = codeInfoRepository.GetCachedCodeInfo(_stateProvider, ContractAddress, Prague.Instance, out _);
                IlAnalyzer.Analyse(targetCodeInfo, vmConfig.IlEvmEnabledMode, vmConfig, NullLogger.Instance);
            }

            _targetBlock = BuildBlock();

            snapshot = _stateProvider.TakeSnapshot();
        }

        private Block BuildBlock()
        {
            _stateProvider.Set(AddressABalanceCell, 1000.Ether().ToBigEndian().WithoutLeadingZeros().ToArray());
            _stateProvider.Set(AddressBBalanceCell, 1000.Ether().ToBigEndian().WithoutLeadingZeros().ToArray());

            Transaction[] txList = [
                BuildTransferTxFrom(AddressA, AddressB, 1000),
                BuildTransferTxFrom(AddressB, AddressA, 1000),
                BuildTransferTxFrom(AddressA, AddressB, 1000),
                BuildTransferTxFrom(AddressB, AddressA, 1000),
                BuildTransferTxFrom(AddressA, AddressB, 1000),
                BuildTransferTxFrom(AddressB, AddressA, 1000),
                BuildTransferTxFrom(AddressA, AddressB, 1000),
                BuildTransferTxFrom(AddressB, AddressA, 1000)
            ];



            var senderRecipientAndMiner = SenderRecipientAndMiner.Default;
            return Build.A.Block.WithNumber(Activation.BlockNumber)
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

        private Transaction BuildTransferTxFrom(Address Sender, Address Receiver, UInt256 Value)
        {

            byte[] data = TransferSelector
                .Concat(Receiver.Bytes.PadLeft(32))
                .Concat(Value.ToBigEndian().PadLeft(32))
                .ToArray();

            return BuildTransaction(data, Value);
        }

        private Transaction BuildTransaction(byte[] data, UInt256 value)
        {

            // checking if account exists - because creating new accounts overwrites already existing accounts,
            // thus overwriting storage roots - essentially clearing the storage slots
            // earlier it used to work - because the cache mapping address:storageTree was never cleared on account of
            // _stateProvider.CommitTrees() not being called. But now the WorldState.CommitTrees which also calls _stateProvider.CommitTrees, clearing the cache.
            if (!_stateProvider.AccountExists(AddressA))
                _stateProvider.CreateAccount(AddressA, 10000.Ether());
            else
                _stateProvider.AddToBalance(AddressA, 10000.Ether(), _specProvider.GenesisSpec);

            if (!_stateProvider.AccountExists(AddressB))
                _stateProvider.CreateAccount(AddressB, 10000.Ether());
            else
                _stateProvider.AddToBalance(AddressB, 10000.Ether(), _specProvider.GenesisSpec);


            if (!_stateProvider.AccountExists(ContractAddress))
                _stateProvider.CreateAccount(ContractAddress, 10000.Ether());
            else
                _stateProvider.AddToBalance(ContractAddress, 10000.Ether(), _specProvider.GenesisSpec);

            _stateProvider.Commit(_specProvider.GenesisSpec);

            Transaction transaction = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithGasPrice(1)
                .WithNonce(_stateProvider.GetNonce(AddressA))
                .WithData(data)
                .WithValue(0)
                .To(ContractAddress)
                .SignedAndResolved(_ethereumEcdsa, AddressAKey)
                .TestObject;

            return transaction;
        }

        private (Address, ValueHash256) InsertCode(byte[] bytecode, Address target = null)
        {
            var hashcode = Keccak.Compute(bytecode);
            var address = target ?? new Address(hashcode);

            var spec = Prague.Instance;
            _stateProvider.CreateAccount(address, (ulong)gasLimit);
            _stateProvider.InsertCode(address, bytecode, spec);
            return (address, hashcode);
        }

        public void IterationSetup()
        {
            _stateProvider.Restore(snapshot);
        }

        public void ExecuteCode()
        {
            for(int i = 0; i < _targetBlock.Transactions.Length; i++) {
                _transactionProcessor.Warmup(_targetBlock.Transactions[i], new BlockExecutionContext(_targetBlock.Header, _spec), NullTxTracer.Instance);
            }
        }
    }

    [MemoryDiagnoser]
    public class WrapedEthBenchmarks
    {
        private WrapedEthBenchmarksSetup<NotOptimizing> Setup_noIlvm = new WrapedEthBenchmarksSetup<NotOptimizing>();
        private WrapedEthBenchmarksSetup<IsPrecompiling> Setup_ilvm = new WrapedEthBenchmarksSetup<IsPrecompiling>();

        [GlobalSetup]
        public void GlobalSetup()
        {
            Setup_noIlvm.GlobalSetup();
            Setup_ilvm.GlobalSetup();
        }

        [IterationSetup]
        public void IterationSetup()
        {
            Setup_noIlvm.IterationSetup();
            Setup_ilvm.IterationSetup();
        }


        [Benchmark(OperationsPerInvoke = 8)]
        public void ExecuteCode_Ilvm()
        {
            Setup_ilvm.ExecuteCode();
        }

        [Benchmark(Baseline = true, OperationsPerInvoke = 8)]
        public void ExecuteCode_No_Ilvm()
        {
            Setup_noIlvm.ExecuteCode();
        }
    }
}
