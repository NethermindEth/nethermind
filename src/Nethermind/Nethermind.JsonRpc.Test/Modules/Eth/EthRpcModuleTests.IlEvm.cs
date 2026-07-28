// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Specs.Forks;
using NUnit.Framework;
using IlEvmRuntime = Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    /// <summary>
    /// THE engagement guarantee: an eth_call through the full production RPC stack
    /// (EthRpcModule → BlockchainBridge → overridable env → VirtualMachine) must execute
    /// IL-EVM compiled segments — under PRODUCTION profitability economics — and reuse the
    /// artifact across calls. This is the test that fails if any link in the chain
    /// (tiering, spec identity, CodeInfo identity, dispatch preconditions) silently breaks,
    /// which previously cost a full node deploy-benchmark cycle to discover.
    /// </summary>
    [Test]
    [NonParallelizable]
    public async Task Eth_call_through_real_rpc_stack_executes_ilevm_compiled_segments()
    {
        bool enabledBackup = IlEvmRuntime.Enabled;
        int thresholdBackup = IlEvmRuntime.CompileThreshold;
        bool syncBackup = IlEvmRuntime.SynchronousCompilation;
        IlEvmRuntime.Enabled = true;
        IlEvmRuntime.CompileThreshold = 1;
        IlEvmRuntime.SynchronousCompilation = true;
        try
        {
            byte[] loopCode =
            [
                (byte)Instruction.PUSH1, 0,     // acc
                (byte)Instruction.PUSH1, 50,    // i
                (byte)Instruction.JUMPDEST,     // pc 4: loop
                (byte)Instruction.DUP1,
                (byte)Instruction.ISZERO,
                (byte)Instruction.PUSH1, 21,
                (byte)Instruction.JUMPI,
                (byte)Instruction.DUP1,
                (byte)Instruction.SWAP2,
                (byte)Instruction.ADD,
                (byte)Instruction.SWAP1,
                (byte)Instruction.PUSH1, 1,
                (byte)Instruction.SWAP1,
                (byte)Instruction.SUB,
                (byte)Instruction.PUSH1, 4,
                (byte)Instruction.JUMP,
                (byte)Instruction.JUMPDEST,     // pc 21: end
                (byte)Instruction.POP,
                (byte)Instruction.PUSH1, 0,
                (byte)Instruction.MSTORE,
                (byte)Instruction.PUSH1, 32,
                (byte)Instruction.PUSH1, 0,
                (byte)Instruction.RETURN,
            ];

            using Context ctx = await Context.Create(configurer: builder => builder
                .WithGenesisPostProcessor((block, state) =>
                {
                    state.InsertCode(TestItem.AddressC, loopCode, London.Instance);
                }));

            TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
                $"{{\"to\": \"{TestItem.AddressC}\"}}");

            long invocationsBefore = IlEvmRuntime.SegmentInvocations;
            long compiledBefore = IlEvmRuntime.ContractsCompiled;

            // Sum 50..1 = 1275 = 0x4FB.
            const string expected = "{\"jsonrpc\":\"2.0\",\"result\":\"0x00000000000000000000000000000000000000000000000000000000000004fb\",\"id\":67}";
            string first = await ctx.Test.TestEthRpc("eth_call", transaction, "latest");
            string second = await ctx.Test.TestEthRpc("eth_call", transaction, "latest");

            Assert.That(first, Is.EqualTo(expected), "the compiled loop must produce the interpreter's exact result");
            Assert.That(second, Is.EqualTo(expected), "repeat calls must agree");
            Assert.That(IlEvmRuntime.SegmentInvocations - invocationsBefore, Is.GreaterThan(0),
                "ENGAGEMENT FAILURE: eth_call through the real RPC stack executed zero compiled segments — " +
                "check spec identity (IlEvmSpecMismatches), CodeInfo identity (ContractsCompiled per call), and dispatch preconditions");
            Assert.That(IlEvmRuntime.ContractsCompiled - compiledBefore, Is.EqualTo(1),
                "the artifact must be compiled once and REUSED across calls — more than one compile means the " +
                "RPC path sees a different CodeInfo instance per call and tiering can never engage on a real node");
        }
        finally
        {
            IlEvmRuntime.Enabled = enabledBackup;
            IlEvmRuntime.CompileThreshold = thresholdBackup;
            IlEvmRuntime.SynchronousCompilation = syncBackup;
        }
    }
}
