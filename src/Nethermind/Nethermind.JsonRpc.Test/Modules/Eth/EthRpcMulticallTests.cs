// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Crypto;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Proxy.Models.MultiCall;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth.Multicall;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;
using Nethermind.Blockchain;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public class EthRpcMulticallTests
{
    private static Task<TestRpcBlockchain> CreateChain(IReleaseSpec? releaseSpec = null,
        UInt256? initialBaseFeePerGas = null)
    {
        TestRpcBlockchain testMevRpcBlockchain = new();
        TestSpecProvider testSpecProvider = releaseSpec is not null
            ? new TestSpecProvider(releaseSpec)
            : new TestSpecProvider(London.Instance);
        return TestRpcBlockchain.ForTest(testMevRpcBlockchain).Build(testSpecProvider);
    }

    private static void allocateAccounts(MultiCallBlockStateCallsModel requestBlockOne, IStateProvider _stateProvider,
        IReleaseSpec latestBlockSpec, ISpecProvider _specProvider, IStorageProvider _storageProvider)
    {
        foreach (var accountOverride in requestBlockOne.StateOverrides)
        {
            var address = accountOverride.Address;
            var acc = _stateProvider.GetAccount(address);
            if (acc == null)
            {
                _stateProvider.CreateAccount(address, accountOverride.Balance, accountOverride.Nonce);
                acc = _stateProvider.GetAccount(address);
            }

            var t = acc.Balance;
            _stateProvider.SubtractFromBalance(address, 666, latestBlockSpec);

            if (acc != null)
            {
                if (accountOverride.Code is not null)
                {
                    Keccak codeHash = _stateProvider.UpdateCode(accountOverride.Code);
                    _stateProvider.UpdateCodeHash(address, codeHash,
                        latestBlockSpec, true);
                }
            }


            if (accountOverride.State is not null)
            {
                foreach (KeyValuePair<UInt256, byte[]> storage in accountOverride.State)
                {
                    _storageProvider.Set(new StorageCell(address, storage.Key),
                        storage.Value.WithoutLeadingZeros().ToArray());
                }
            }

            if (accountOverride.StateDiff is not null)
            {
                foreach (KeyValuePair<UInt256, byte[]> storage in accountOverride.StateDiff)
                {
                    _storageProvider.Set(new StorageCell(address, storage.Key),
                        storage.Value.WithoutLeadingZeros().ToArray());
                }
            }
        }
    }

    /// <summary>
    /// This test verifies that a temporary forked blockchain updates the user balance and block number
    /// independently of the main chain, ensuring the main chain remains intact.
    /// </summary>
    [Test]
    public async Task Test_eth_multicall()
    {
        var chain = await CreateChain();

        var requestBlockOne = new MultiCallBlockStateCallsModel();
        requestBlockOne.StateOverrides = new[] {
            new AccountOverride()
            {
                Address = TestItem.AddressA,
                Balance = UInt256.One
            }
        };

        var blockNumberBefore = chain.BlockFinder.Head.Number;
        var userBalanceBefore = await chain.EthRpcModule.eth_getBalance(TestItem.AddressA, BlockParameter.Latest);
        userBalanceBefore.Result.ResultType.Should().Be(Core.ResultType.Success);

        //Force persistancy of head block in main chain
        chain.BlockTree.UpdateHeadBlock(chain.BlockFinder.Head.Hash);

        var tt = chain.TrieStore;

        using (var tmpChain =new MultiCallBlockchainFork(chain.DbProvider, chain.SpecProvider))
        {
            //Check if tmpChain initialised
            Assert.AreEqual(chain.BlockTree.BestKnownNumber, tmpChain.BlockTree.BestKnownNumber);
            Assert.AreEqual(chain.BlockFinder.BestPersistedState, tmpChain.BlockFinder.BestPersistedState);
            Assert.AreEqual(chain.BlockFinder.Head.Number, tmpChain.BlockFinder.Head.Number);

            //Check if tmpChain RPC initialised
            var userBalanceBefore_fromTmp =
                await tmpChain.EthRpcModule.eth_getBalance(TestItem.AddressA, BlockParameter.Latest);
            userBalanceBefore_fromTmp.Result.ResultType.Should().Be(Core.ResultType.Success);

            //Check if tmpChain shows same values as main one
            var num_real = userBalanceBefore.Data.Value;
            var num_tmp = userBalanceBefore_fromTmp.Data.Value;
            Assert.AreEqual(userBalanceBefore_fromTmp.Data, userBalanceBefore.Data);

            var processed = tmpChain.ForgeChainBlock((stateProvider, currentSpec, specProvider, storageProvider) =>
            {
                allocateAccounts(requestBlockOne, stateProvider, currentSpec, specProvider,
                    storageProvider);
            });

            //Check block has been added to chain as main
            Assert.True(processed);

            //Check block has updated values in tmp chain
            var userBalanceResult_fromTm =
                await tmpChain.EthRpcModule.eth_getBalance(TestItem.AddressA, BlockParameter.Latest);
            userBalanceResult_fromTm.Result.ResultType.Should().Be(Core.ResultType.Success);
            Assert.AreNotEqual(userBalanceResult_fromTm.Data, userBalanceBefore.Data);

            //Check block has not updated values in the main chain
            var userBalanceResult = await chain.EthRpcModule.eth_getBalance(TestItem.AddressA, BlockParameter.Latest);
            userBalanceResult.Result.ResultType.Should().Be(Core.ResultType.Success);
            Assert.AreEqual(userBalanceResult.Data, userBalanceBefore.Data); //Main chain is intact
            Assert.AreNotEqual(userBalanceResult.Data, userBalanceResult_fromTm.Data); // Balance was changed
            Assert.AreNotEqual(chain.BlockFinder.Head.Number, tmpChain.LatestBlock.Number); // Block number changed
        }

        GC.Collect();
        GC.WaitForFullGCComplete();

        Assert.Equals(chain.BlockFinder.Head.Number,
            blockNumberBefore); // tmp chain is disposed, main chain block number is still the same
    }
}
