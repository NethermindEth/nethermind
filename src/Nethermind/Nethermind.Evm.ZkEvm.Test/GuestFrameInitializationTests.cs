// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Reflection;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.ZkEvm.Test;

/// <summary>Enters a frame the way the dispatch loop does and records whether crediting its account reached the world state.</summary>
/// <remarks>
/// The guest drops the zero credit to an account whose code the frame runs, as that account is never empty; every other
/// entry - a credit, an account without code, a creation, or a fork whose access list records the credit - still goes through.
/// </remarks>
public class GuestFrameInitializationTests
{
    private static readonly BlockHeader Header = new(Hash256.Zero, Hash256.Zero, Address.Zero, UInt256.Zero, 1, 30_000_000, 1, []);
    private static readonly Address Callee = new("0x00000000000000000000000000000000000000c0");
    private static readonly Address Caller = new("0x00000000000000000000000000000000000000ca");

    [TestCase(ExecutionType.CALL, 0, true, false, ExpectedResult = false)]
    [TestCase(ExecutionType.STATICCALL, 0, true, false, ExpectedResult = false)]
    [TestCase(ExecutionType.TRANSACTION, 0, true, false, ExpectedResult = false)]
    [TestCase(ExecutionType.DELEGATECALL, 1, true, false, ExpectedResult = false)]
    [TestCase(ExecutionType.CALLCODE, 0, true, false, ExpectedResult = false)]
    [TestCase(ExecutionType.CALLCODE, 1, true, false, ExpectedResult = true)]
    [TestCase(ExecutionType.CALL, 1, true, false, ExpectedResult = true)]
    [TestCase(ExecutionType.CALL, 0, false, false, ExpectedResult = true)]
    [TestCase(ExecutionType.CREATE, 0, true, false, ExpectedResult = true)]
    [TestCase(ExecutionType.CALL, 0, true, true, ExpectedResult = true)]
    public bool Frame_entry_reaches_the_world_state(ExecutionType executionType, int value, bool hasCode, bool blockAccessList)
    {
        IReleaseSpec spec = blockAccessList ? Amsterdam.Instance : Osaka.Instance;
        FrameEnteringVirtualMachine vm = new(spec);
        CodeInfo codeInfo = hasCode ? new CodeInfo(new byte[] { (byte)Instruction.STOP }) : CodeInfo.Empty;
        using ExecutionEnvironment env = ExecutionEnvironment.Rent(codeInfo, Callee, Caller, Callee, 1, (UInt256)value, default);
        using VmState<EthereumGasPolicy> frame = VmState<EthereumGasPolicy>.RentFrame(
            EthereumGasPolicy.FromULong(100_000), 0, 0, executionType, isStatic: false, isCreateOnPreExistingAccount: false,
            env, new StackAccessTracker(), default);

        vm.EnterFrame(frame);

        return vm.WorldState.ReceivedCalls().Any();
    }

    private sealed class FrameEnteringVirtualMachine : VirtualMachine<EthereumGasPolicy>
    {
        public FrameEnteringVirtualMachine(IReleaseSpec spec)
            : base(Substitute.For<IBlockhashProvider>(), new SingleReleaseSpecProvider(spec, BlockchainIds.Mainnet, BlockchainIds.Mainnet), LimboLogs.Instance)
        {
            SetBlockExecutionContext(new BlockExecutionContext(Header, spec));
            _txTracer = NullTxTracer.Instance;
            _worldState = Substitute.For<IWorldState>();
            Type type = typeof(VirtualMachine<EthereumGasPolicy>);
            type.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                .Single(static method => method.Name == "PrepareOpcodes" && method.GetGenericArguments().Length == 1)
                .MakeGenericMethod(typeof(OffFlag))
                .Invoke(this, null);
            // The guest keeps the frame handlers of the first fork it prepares; build this fork's own.
            Type handlersType = type.GetNestedType("ExecutionHandlers", BindingFlags.NonPublic)!.MakeGenericType(typeof(EthereumGasPolicy));
            type.GetField("_executionHandlers", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(this, Activator.CreateInstance(handlersType, spec));
        }

        public void EnterFrame(VmState<EthereumGasPolicy> frame)
        {
            VmState = frame;
            ExecuteCall<OffFlag>(default, 0, UInt256.Zero);
        }
    }
}
