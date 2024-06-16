// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.Precompiles;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Proxy.Models;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public class EthSimulateTestsPrecompilesWithRedirection
{
    [Test]
    public async Task Test_eth_simulate_create()
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();

        Transaction systemTransactionForModifiedVm = new()
        {
            SenderAddress = TestItem.AddressB,
            Data = Bytes.FromHexString("0xee82ac5e0000000000000000000000000000000000000000000000000000000000000001"),
            To = TestItem.AddressA,
            GasLimit = 3_500_000,
            GasPrice = 20.GWei()
        };

        TransactionForRpc transactionForRpc = new(systemTransactionForModifiedVm) { Nonce = null };

        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        {
                            TestItem.AddressA,
                            new AccountOverride
                            {
                                Code = Bytes.FromHexString("0x6080604052348015600f57600080fd5b506004361060285760003560e01c8063ee82ac5e14602d575b600080fd5b60436004803603810190603f91906098565b6057565b604051604e919060d7565b60405180910390f35b600081409050919050565b600080fd5b6000819050919050565b6078816067565b8114608257600080fd5b50565b6000813590506092816071565b92915050565b60006020828403121560ab5760aa6062565b5b600060b7848285016085565b91505092915050565b6000819050919050565b60d18160c0565b82525050565b600060208201905060ea600083018460ca565b9291505056fea2646970667358221220a4d7face162688805e99e86526524ac3dadfb01cc29366d0d68b70dadcf01afe64736f6c63430008120033")
                            }
                        },
                    },
                    Calls = [transactionForRpc]
                }
            ]
        };

        //will mock our GetCachedCodeInfo function - it shall be called 3 times if redirect is working, 2 times if not
        SimulateTxExecutor executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig(), new BlocksConfig().SecondsPerSlot);

        ResultWrapper<IReadOnlyList<SimulateBlockResult>> result = executor.Execute(payload, BlockParameter.Latest);

        //Check results
        byte[]? returnData = result.Data[0].Calls.First().ReturnData;
        Assert.IsNotNull(returnData);
    }


    /// <summary>
    ///     This test verifies that a temporary forked blockchain can redirect precompiles
    /// </summary>
    [Test]
    public async Task Test_eth_simulate_ecr_moved()
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();
        //The following opcodes code is based on the following contract compiled:
        /*
         function ecrecover(bytes32 hash, uint8 v, bytes32 r, bytes32 s) public  returns(address)
        {

            address redirectedToAddress = 0x0000000000000000000000000000000000000666;

            assembly {
                // Copy msg.data. We take full control of memory in this inline assembly
                // block because it will not return to Solidity code. We overwrite the
                // Solidity scratch pad at memory position 0.
                calldatacopy(0, 0, calldatasize())

                // Call the implementation.
                // out and outsize are 0 because we don't know the size yet.
                let result := delegatecall(gas(), redirectedToAddress, 0, calldatasize(), 0, 0)

                // Copy the returned data.
                returndatacopy(0, 0, returndatasize())

                return (0, returndatasize())
            }
        }
         */

        byte[] code = Prepare.EvmCode
            .JUMPDEST()
            .PushData(Bytes.ZeroByte)
            .Op(Instruction.DUP1)
            .PushData(TestItem.AddressB.Bytes)
            .Op(Instruction.SWAP1)
            .Op(Instruction.POP)
            .Op(Instruction.CALLDATASIZE)
            .PushData(Bytes.ZeroByte)
            .Op(Instruction.DUP1)
            .Op(Instruction.CALLDATACOPY)
            .PushData(Bytes.ZeroByte)
            .Op(Instruction.DUP1)
            .Op(Instruction.CALLDATASIZE)
            .PushData(Bytes.ZeroByte)
            .Op(Instruction.DUP5)
            .Op(Instruction.GAS)
            .Op(Instruction.DELEGATECALL)
            .Op(Instruction.RETURNDATASIZE)
            .PushData(Bytes.ZeroByte)
            .Op(Instruction.DUP1)
            .Op(Instruction.RETURNDATACOPY)
            .Op(Instruction.RETURNDATASIZE)
            .PushData(Bytes.ZeroByte)
            .Op(Instruction.RETURN)
            .Done;

        byte[] transactionData = EthRpcSimulateTestsBase.GetTxData(chain, TestItem.PrivateKeyA);

        Hash256 headHash = chain.BlockFinder.Head!.Hash!;
        Address contractAddress = await EthRpcSimulateTestsBase.DeployEcRecoverContract(chain, TestItem.PrivateKeyB, EthSimulateTestsSimplePrecompiles.EcRecoverCallerContractBytecode);

        EthRpcSimulateTestsBase.EcRecoverCall(chain, TestItem.AddressB, transactionData, contractAddress);

        chain.BlockTree.UpdateMainChain(new List<Block> { chain.BlockFinder.Head! }, true, true);
        chain.BlockTree.UpdateHeadBlock(chain.BlockFinder.Head!.Hash!);

        Assert.That(headHash != chain.BlockFinder.Head!.Hash!);
        chain.State.StateRoot = chain.BlockFinder.Head!.StateRoot!;

        TransactionForRpc transactionForRpc = new(new Transaction
        {
            Data = transactionData,
            To = contractAddress,
            SenderAddress = TestItem.AddressA,
            GasLimit = 3_500_000,
            GasPrice = 20.GWei()
        })
        {
            Nonce = null
        };

        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        {
                            EcRecoverPrecompile.Address,
                            new AccountOverride
                            {
                                Code = code,
                                MovePrecompileToAddress = TestItem.AddressB
                            }
                        }
                    },
                    Calls = [transactionForRpc]
                }
            ]
        };

        //will mock our GetCachedCodeInfo function - it shall be called 3 times if redirect is working, 2 times if not
        SimulateTxExecutor executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig(), new BlocksConfig().SecondsPerSlot);

        Debug.Assert(contractAddress is not null, nameof(contractAddress) + " is not null");
        Assert.IsTrue(chain.State.AccountExists(contractAddress));

        ResultWrapper<IReadOnlyList<SimulateBlockResult>> result = executor.Execute(payload, BlockParameter.Latest);

        //Check results
        using ArrayPoolList<byte> addressBytes = result.Data[0].Calls[0].ReturnData!.SliceWithZeroPaddingEmptyOnError(12, 20);
        Address resultingAddress = new(addressBytes.ToArray());
        Assert.That(resultingAddress, Is.EqualTo(TestItem.AddressA));
    }
}
