// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Test.CodeAnalysis;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Measures what skipping a recognized template's opcodes is worth per call.
/// </summary>
/// <remarks>
/// Each shape is benchmarked twice against bytecode that behaves identically and costs identical gas,
/// differing only in whether the template matcher accepts it, so the two arms run the same work and the
/// delta is the fast path alone — no runtime switch and no tracing asymmetry. The function bodies are
/// deliberately trivial, which makes the reported delta the per-call overhead removed rather than a
/// share of any real contract's runtime.
/// </remarks>
[MemoryDiagnoser]
public class CodeTemplateBenchmarks
{
    /// <summary>Which template shape the executed bytecode uses.</summary>
    public enum Shape
    {
        MinimalProxy,
        LinearDispatcher,
        BinarySearchDispatcher,
    }

    private static readonly Address Implementation = Address.FromNumber(0xabcd);

    private static readonly uint[] Selectors =
        [0x18160ddd, 0x23b872dd, 0x313ce567, 0x70a08231, 0x95d89b41, 0xa9059cbb, 0xdd62ed3e, 0xf2fde38b, 0x06fdde03];

    /// <summary>The selector driven through the dispatcher, chosen to sit deep in the routing structure.</summary>
    private static readonly byte[] Input = [0xf2, 0xfd, 0xe3, 0x8b];

    private readonly IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec(MainnetSpecProvider.CancunActivation);
    private readonly BlockHeader _header = new(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One,
        MainnetSpecProvider.ParisBlockNumber + 2, long.MaxValue, MainnetSpecProvider.CancunBlockTimestamp, Bytes.Empty);

    private IVirtualMachine _virtualMachine = null!;
    private IWorldState _stateProvider = null!;
    private IDisposable _stateScope = null!;
    private CodeInfo _codeInfo = null!;

    [Params(Shape.MinimalProxy, Shape.LinearDispatcher, Shape.BinarySearchDispatcher)]
    public Shape Template { get; set; }

    [Params(true, false)]
    public bool Recognized { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _stateProvider = TestWorldStateFactory.CreateForTest();
        _stateScope = _stateProvider.BeginScope(IWorldState.PreGenesis);
        _stateProvider.CreateAccount(Address.Zero, 1000.Ether);
        _stateProvider.CreateAccount(Implementation, UInt256.Zero);
        _stateProvider.InsertCode(Implementation, Prepare.EvmCode.Op(Instruction.STOP).Done, _spec);
        _stateProvider.Commit(_spec);

        EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
        _virtualMachine = new EthereumVirtualMachine(new TestBlockhashProvider(), MainnetSpecProvider.Instance,
            new OneLoggerLogManager(NullLogger.Instance));
        _virtualMachine.SetBlockExecutionContext(new BlockExecutionContext(_header, _spec));
        _virtualMachine.SetTxExecutionContext(new TxExecutionContext(Address.Zero, codeInfoRepository, null, 0));

        _codeInfo = new CodeInfo(BuildCode());

        bool matched = !ReferenceEquals(_codeInfo.Template, CodeTemplate.None);
        if (matched != Recognized)
        {
            throw new InvalidOperationException(
                $"{Template} bytecode was {(matched ? "" : "not ")}recognized, but the arm expects Recognized={Recognized}.");
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _stateScope.Dispose();

    [Benchmark]
    public void ExecuteCall()
    {
        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            executingAccount: Address.Zero,
            codeSource: Address.Zero,
            caller: Address.Zero,
            codeInfo: _codeInfo,
            callDepth: 0,
            value: 0,
            inputData: Input);

        using VmState<EthereumGasPolicy> state = VmState<EthereumGasPolicy>.RentTopLevel(
            EthereumGasPolicy.FromULong(1_000_000UL), ExecutionType.TRANSACTION, environment,
            new StackAccessTracker(), _stateProvider.TakeSnapshot());

        _virtualMachine.ExecuteTransaction<OffFlag>(state, _stateProvider, NullTxTracer.Instance);
        _stateProvider.Reset();
    }

    private byte[] BuildCode() => Template switch
    {
        Shape.MinimalProxy => Recognized
            ? TemplateCode.MinimalProxy(Implementation)
            : TemplateCode.UnrecognizedMinimalProxy(Implementation),
        Shape.LinearDispatcher =>
            TemplateCode.SelectorDispatch(Selectors, withCallValueGuard: true, DispatchShape.Linear, Recognized).Code,
        Shape.BinarySearchDispatcher =>
            TemplateCode.SelectorDispatch(Selectors, withCallValueGuard: true, DispatchShape.BinarySearch, Recognized).Code,
        _ => throw new ArgumentOutOfRangeException(nameof(Template), Template, "Unknown template shape."),
    };
}
