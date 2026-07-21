// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth.Simulate;

/// <summary>
/// Regression tests for EIP-7610 collision detection in eth_simulateV1.
/// EIP-7610: Revert CREATE/CREATE2 if the target address already has non-empty storage.
/// </summary>
[TestFixture]
public class EthSimulateEip7610Tests
{
    private const string FactoryBytecode =
        "0x601260376000397f0000000000000000000000000000000000000000000000000000000000000000" +
        "601260006000f5600052602060" +
        "00f3602a6000556001601160003960016000f300";

    private static readonly Address FactoryAddress = new("0xf1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1");
    private static readonly Address CallerAddress = new("0xca11e1ca11e1ca11e1ca11e1ca11e1ca11e1ca11");

    /// <summary>
    /// EIP-7610: eth_simulateV1 must detect storage collision from a prior simulated block.
    ///
    /// Block 1: factory deploys C via CREATE2; C's constructor sets storage[0]=42.
    /// Block 2: state override resets C's code + nonce to zero; factory called again with same salt.
    ///
    /// Expected: CREATE2 in block 2 sees C still has non-empty storage → returns 0x00 (collision).
    /// Bug: without the fix, block 2's CREATE2 succeeds and returns C's address instead of 0x00.
    /// </summary>
    [Test]
    public async Task Simulate_EIP7610_detects_storage_collision_from_prior_simulated_block()
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain(Osaka.Instance);

        SimulatePayload<TransactionForRpc> block1Only = new()
        {
            BlockStateCalls = [BuildBlock1Call()],
            Validation = false
        };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> step1 =
            chain.EthRpcModule.eth_simulateV1(block1Only, BlockParameter.Latest);

        Assert.That(step1.Result.ResultType, Is.EqualTo(Core.ResultType.Success), "Block 1 simulation should succeed");
        Assert.That(step1.Data, Is.Not.Null);

        byte[] block1ReturnData = step1.Data.First().Calls.First().ReturnData ?? [];
        Assert.That(block1ReturnData, Has.Length.EqualTo(32), "Factory should return a 32-byte address");

        Address contractC = new(block1ReturnData.Skip(12).Take(20).ToArray());
        Assert.That(contractC, Is.Not.EqualTo(Address.Zero), "Deployed contract C address must be non-zero");

        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                BuildBlock1Call(),
                BuildBlock2Call(contractC)
            ],
            Validation = false
        };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            chain.EthRpcModule.eth_simulateV1(payload, BlockParameter.Latest);

        Assert.That(result.Result.ResultType, Is.EqualTo(Core.ResultType.Success), "Two-block simulation must not error");
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data, Has.Count.EqualTo(2), "Should have results for both blocks");

        byte[] block2ReturnData = result.Data.Last().Calls.First().ReturnData ?? [];
        Assert.That(block2ReturnData, Has.Length.EqualTo(32), "Block 2 result should be 32 bytes");

        bool returnedZeroAddress = block2ReturnData.All(static b => b == 0);
        Address returnedAddress = new(block2ReturnData.Skip(12).Take(20).ToArray());

        Assert.That(returnedZeroAddress, Is.True,
            $"EIP-7610 storage collision not detected: block 2 CREATE2 returned {returnedAddress} " +
            $"instead of 0x00. Contract C at {contractC} still has storage[0]=42 " +
            "from block 1 and must block the redeployment.");
    }

    private static BlockStateCall<TransactionForRpc> BuildBlock1Call() =>
        new()
        {
            StateOverrides = new Dictionary<Address, AccountOverride>
            {
                [FactoryAddress] = new AccountOverride
                {
                    Code = Bytes.FromHexString(FactoryBytecode),
                    Balance = 1.Ether
                },
                [CallerAddress] = new AccountOverride
                {
                    Balance = 1.Ether
                }
            },
            Calls =
            [
                new LegacyTransactionForRpc
                {
                    From = CallerAddress,
                    To = FactoryAddress,
                    Gas = 1_000_000,
                    GasPrice = 0
                }
            ]
        };

    private static BlockStateCall<TransactionForRpc> BuildBlock2Call(Address contractC) =>
        new()
        {
            StateOverrides = new Dictionary<Address, AccountOverride>
            {
                // Clear C's code and nonce, leaving storage[0]=42 intact.
                [contractC] = new AccountOverride
                {
                    Code = [],
                    Nonce = 0
                }
            },
            Calls =
            [
                new LegacyTransactionForRpc
                {
                    From = CallerAddress,
                    To = FactoryAddress,
                    Gas = 1_000_000,
                    GasPrice = 0
                }
            ]
        };
}
