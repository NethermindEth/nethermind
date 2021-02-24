//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Evm.Benchmark
{
    //[MemoryDiagnoser]
    //public class EvmBenchmarks
    //{
    //    public static byte[] ByteCode { get; set; }

    //    private IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec(MainnetSpecProvider.IstanbulBlockNumber);
    //    private ITxTracer _txTracer = NullTxTracer.Instance;
    //    private ExecutionEnvironment _environment;
    //    private IVirtualMachine _virtualMachine;
    //    private BlockHeader _header = new BlockHeader(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One, MainnetSpecProvider.IstanbulBlockNumber, Int64.MaxValue, UInt256.One, Bytes.Empty);
    //    private IBlockhashProvider _blockhashProvider = new TestBlockhashProvider();
    //    private EvmState _evmState;
    //    private StateProvider _stateProvider;
    //    private StorageProvider _storageProvider;

    //    [GlobalSetup]
    //    public void GlobalSetup()
    //    {
    //        ByteCode = Bytes.FromHexString(Environment.GetEnvironmentVariable("NETH.BENCHMARK.BYTECODE"));
    //        Console.WriteLine($"Running benchmark for bytecode {ByteCode?.ToHexString()}");
    //        IDb codeDb = new StateDb();
    //        ISnapshotableDb stateDb = new StateDb();

    //        _stateProvider = new StateProvider(stateDb, codeDb, LimboLogs.Instance);
    //        _stateProvider.CreateAccount(Address.Zero, 1000.Ether());
    //        _stateProvider.Commit(_spec);

    //        _storageProvider = new StorageProvider(stateDb, _stateProvider, LimboLogs.Instance);
    //        _virtualMachine = new VirtualMachine(_stateProvider, _storageProvider, _blockhashProvider, MainnetSpecProvider.Instance, LimboLogs.Instance);
            
    //        _environment = new ExecutionEnvironment();
    //        _environment.ExecutingAccount = Address.Zero;
    //        _environment.CodeSource = Address.Zero;
    //        _environment.Originator = Address.Zero;
    //        _environment.Sender = Address.Zero;
    //        _environment.CodeInfo = new CodeInfo(ByteCode);
    //        _environment.GasPrice = 0;
    //        _environment.Value = 0;
    //        _environment.TransferValue = 0;
    //        _environment.CurrentBlock = _header;
            
    //        _evmState = new EvmState(long.MaxValue, _environment, ExecutionType.Transaction, true, false);
    //    }

    //    [Benchmark]
    //    public void ExecuteCode()
    //    {
    //        _virtualMachine.Run(_evmState, _txTracer);
    //        _stateProvider.Reset();
    //        _storageProvider.Reset();
    //    }
    //}
}
