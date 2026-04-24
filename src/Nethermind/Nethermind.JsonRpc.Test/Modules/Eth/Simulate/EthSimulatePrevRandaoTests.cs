// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;
using ResultType = Nethermind.Facade.Proxy.Models.Simulate.ResultType;

namespace Nethermind.JsonRpc.Test.Modules.Eth.Simulate;

[TestFixture]
public class EthSimulatePrevRandaoTests
{
    // Minimal bytecode: PREVRANDAO PUSH1 0x00 MSTORE PUSH1 0x20 PUSH1 0x00 RETURN
    // Reads prevRandao from the EVM context and returns it as a 32-byte value.
    private static readonly byte[] PrevRandaoBytecode = [0x44, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xF3];

    private static Task<TestRpcBlockchain> CreatePostMergeChain()
    {
        TestRpcBlockchain chain = new();
        // MergeBlockNumber = 0 ensures simulated blocks have IsPostMerge = true,
        // so PREVRANDAO reads header.MixHash rather than header.Difficulty.
        TestSpecProvider specProvider = new(Cancun.Instance);
        specProvider.UpdateMergeTransitionInfo(0);
        return TestRpcBlockchain.ForTest(chain).Build(specProvider);
    }

    [TestCase("0xc300000000000000000000000000000000000000000000000000000000000001",
        TestName = "with_override_returns_overridden_value")]
    [TestCase(null,
        TestName = "without_override_returns_zero")]
    public async Task prevrandao_opcode_returns_expected_value(string? overrideHex)
    {
        TestRpcBlockchain chain = await CreatePostMergeChain();
        Hash256? overrideHash = overrideHex is not null ? new Hash256(overrideHex) : null;
        Hash256 expected = overrideHash ?? Hash256.Zero;
        Address contractAddress = TestItem.AddressC;

        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    BlockOverrides = overrideHash is not null ? new BlockOverride { PrevRandao = overrideHash } : null,
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { contractAddress, new AccountOverride { Code = PrevRandaoBytecode } },
                        { TestItem.AddressA, new AccountOverride { Balance = 1.Ether } }
                    },
                    Calls =
                    [
                        new LegacyTransactionForRpc
                        {
                            From = TestItem.AddressA,
                            To = contractAddress,
                            Gas = 100_000
                        }
                    ]
                }
            ]
        };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        result.Result.ResultType.Should().Be(Core.ResultType.Success);
        SimulateCallResult callResult = result.Data.First().Calls.First();
        callResult.Status.Should().Be((ulong)ResultType.Success);
        callResult.ReturnData.Should().NotBeNull().And.HaveCount(32);
        new Hash256(callResult.ReturnData!).Should().Be(expected);
    }
}
