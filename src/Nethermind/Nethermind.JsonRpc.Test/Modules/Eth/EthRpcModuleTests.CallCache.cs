// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Facade;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    /// <summary>
    /// The eth_call result cache guarantee, through the full production RPC stack: a repeated
    /// identical call against the same head must be served from the cache (hit counted, no
    /// re-execution) with a byte-identical response, and a different calldata against the same
    /// head must miss. Runs the same loop bytecode as the IL-EVM guarantee so the cached result
    /// is a real computed value, not a trivial empty output.
    /// </summary>
    [Test]
    [NonParallelizable]
    public async Task Eth_call_repeated_on_same_head_is_served_from_result_cache()
    {
        bool enabledBackup = EthCallResultCache.Enabled;
        EthCallResultCache.Enabled = true;
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
            TransactionForRpc differentTransaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
                $"{{\"to\": \"{TestItem.AddressC}\", \"input\": \"0x01\"}}");

            long hitsBefore = EthCallResultCache.Hits;
            long missesBefore = EthCallResultCache.Misses;

            // Sum 50..1 = 1275 = 0x4FB.
            const string expected = "{\"jsonrpc\":\"2.0\",\"result\":\"0x00000000000000000000000000000000000000000000000000000000000004fb\",\"id\":67}";
            string first = await ctx.Test.TestEthRpc("eth_call", transaction, "latest");
            string second = await ctx.Test.TestEthRpc("eth_call", transaction, "latest");

            Assert.That(first, Is.EqualTo(expected), "the first call computes the result");
            Assert.That(second, Is.EqualTo(expected), "the cached result must be byte-identical");
            Assert.That(EthCallResultCache.Hits - hitsBefore, Is.EqualTo(1),
                $"the second identical call against the same head must be a cache hit " +
                $"(misses delta: {EthCallResultCache.Misses - missesBefore} — 0 means the cache path was never reached, " +
                "2 means the two calls computed different keys or Set failed)");

            long hitsBeforeDifferent = EthCallResultCache.Hits;
            string different = await ctx.Test.TestEthRpc("eth_call", differentTransaction, "latest");
            Assert.That(different, Is.EqualTo(expected), "calldata is ignored by this contract, result matches");
            Assert.That(EthCallResultCache.Hits - hitsBeforeDifferent, Is.EqualTo(0),
                "different calldata against the same head must MISS — it is a different key");
        }
        finally
        {
            EthCallResultCache.Enabled = enabledBackup;
        }
    }
}
