// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
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

namespace Nethermind.Evm.Test;

public class CallFrameEntryCreditTests
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
    [TestCase(ExecutionType.CALL, 0, true, true, ExpectedResult = false)]
    [TestCase(ExecutionType.DELEGATECALL, 1, true, true, ExpectedResult = false)]
    [TestCase(ExecutionType.CALL, 1, true, true, ExpectedResult = true)]
    [TestCase(ExecutionType.CALL, 0, false, true, ExpectedResult = true)]
    [TestCase(ExecutionType.CREATE, 0, true, true, ExpectedResult = true)]
    public bool Frame_entry_credits_the_executing_account(ExecutionType executionType, int value, bool hasCode, bool blockAccessList) =>
        EnterFrame(executionType, value, hasCode, blockAccessList).ReceivedCalls().Any(static call => call.GetMethodInfo().Name
            is nameof(IWorldState.AccountExists) or nameof(IWorldState.AddToBalance) or nameof(IWorldState.AddToBalanceAndCreateIfNotExists));

    /// <remarks>
    /// Under EIP-7928 a frame whose zero credit is skipped still leaves the account's entry in the block access list,
    /// which is all the credit left there.
    /// </remarks>
    [Test]
    public void Frame_entry_without_a_credit_records_the_executing_account_under_EIP_7928(
        [Values(ExecutionType.CALL, ExecutionType.STATICCALL, ExecutionType.DELEGATECALL)] ExecutionType executionType,
        [Values] bool blockAccessList)
    {
        IWorldState worldState = EnterFrame(executionType, 0, hasCode: true, blockAccessList);

        worldState.Received(blockAccessList ? 1 : 0).AddAccountRead(Callee);
    }

    private static IWorldState EnterFrame(ExecutionType executionType, int value, bool hasCode, bool blockAccessList)
    {
        IReleaseSpec spec = blockAccessList ? Amsterdam.Instance : Osaka.Instance;
        FrameEnteringVirtualMachine vm = new(spec);
        CodeInfo codeInfo = hasCode ? new CodeInfo(new byte[] { (byte)Instruction.STOP }) : CodeInfo.Empty;
        using ExecutionEnvironment env = ExecutionEnvironment.Rent(codeInfo, Callee, Caller, Callee, 1, (UInt256)value, default);
        using VmState<EthereumGasPolicy> frame = VmState<EthereumGasPolicy>.RentFrame(
            EthereumGasPolicy.FromULong(100_000), 0, 0, executionType, isStatic: false, isCreateOnPreExistingAccount: false,
            env, new StackAccessTracker(), default);

        vm.EnterFrame(frame);
        return vm.WorldState;
    }

    private sealed class FrameEnteringVirtualMachine : VirtualMachine<EthereumGasPolicy>
    {
        public FrameEnteringVirtualMachine(IReleaseSpec spec)
            : base(Substitute.For<IBlockhashProvider>(), new SingleReleaseSpecProvider(spec, BlockchainIds.Mainnet, BlockchainIds.Mainnet), LimboLogs.Instance)
        {
            SetBlockExecutionContext(new BlockExecutionContext(Header, spec));
            _txTracer = NullTxTracer.Instance;
            _worldState = Substitute.For<IWorldState>();
            PrepareFrameHandlersForTests();
        }

        public void EnterFrame(VmState<EthereumGasPolicy> frame)
        {
            VmState = frame;
            ExecuteCall<OffFlag>(default, 0, UInt256.Zero);
        }
    }
}
