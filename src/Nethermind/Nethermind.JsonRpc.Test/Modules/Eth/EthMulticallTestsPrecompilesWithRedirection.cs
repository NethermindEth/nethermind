// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.Precompiles;
using Nethermind.Facade.Proxy.Models;
using Nethermind.Facade.Proxy.Models.MultiCall;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public class EthMulticallTestsPrecompilesWithRedirection
{
    public static byte[] HexStringToByteArray(string hex)
    {
        if (hex.StartsWith("0x"))
        {
            hex = hex.Substring(2);
        }

        int NumberChars = hex.Length;
        byte[] bytes = new byte[NumberChars / 2];
        for (int i = 0; i < NumberChars; i += 2)
        {
            bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
        }
        return bytes;
    }

    [Test]
    public async Task Test_eth_multicall_create()
    {
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();

        Transaction systemTransactionForModifiedVm = new()
        {
            SenderAddress = new Address("0xc000000000000000000000000000000000000000"),
            Data = HexStringToByteArray("0xee82ac5e0000000000000000000000000000000000000000000000000000000000000001"),
            To = new Address("0xc200000000000000000000000000000000000000"),
            GasLimit = 3_500_000,
            GasPrice = 20.GWei(),

        };

        TransactionForRpc transactionForRpc = new(systemTransactionForModifiedVm) { Nonce = null };

        MultiCallPayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls = new BlockStateCall<TransactionForRpc>[]
            {
                new()
                {
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        {
                            new Address("0xc200000000000000000000000000000000000000"),
                            new AccountOverride
                            {
                                Code = HexStringToByteArray("0x6080604052348015600f57600080fd5b506004361060285760003560e01c8063ee82ac5e14602d575b600080fd5b60436004803603810190603f91906098565b6057565b604051604e919060d7565b60405180910390f35b600081409050919050565b600080fd5b6000819050919050565b6078816067565b8114608257600080fd5b50565b6000813590506092816071565b92915050565b60006020828403121560ab5760aa6062565b5b600060b7848285016085565b91505092915050565b6000819050919050565b60d18160c0565b82525050565b600060208201905060ea600083018460ca565b9291505056fea2646970667358221220a4d7face162688805e99e86526524ac3dadfb01cc29366d0d68b70dadcf01afe64736f6c63430008120033")
                            }
                        },
                    },
                    Calls = new[]
                    {
                        transactionForRpc,
                    }
                }
            },
            TraceTransfers = false,
            Validation = false
        };

        //will mock our GetCachedCodeInfo function - it shall be called 3 times if redirect is working, 2 times if not
        MultiCallTxExecutor executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig());

        ResultWrapper<IReadOnlyList<SimulateBlockResult>> result = executor.Execute(payload, BlockParameter.Latest);

        //Check results
        byte[]? returnData = result.Data[0].Calls.First().ReturnData;
    }


    /// <summary>
    ///     This test verifies that a temporary forked blockchain can redirect precompiles
    /// </summary>
    [Test]
    public async Task Test_eth_multicall_ecr_moved()
    {
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
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
        byte[] zeroByte = { 0 };
        byte[] code = Prepare.EvmCode
            .JUMPDEST()
            .PushData(zeroByte)
            .Op(Instruction.DUP1)
            .PushData(Bytes.FromHexString("0x0666")) //  666
            .Op(Instruction.SWAP1)
            .Op(Instruction.POP)
            .Op(Instruction.CALLDATASIZE)
            .PushData(zeroByte)
            .Op(Instruction.DUP1)
            .Op(Instruction.CALLDATACOPY)
            .PushData(zeroByte)
            .Op(Instruction.DUP1)
            .Op(Instruction.CALLDATASIZE)
            .PushData(zeroByte)
            .Op(Instruction.DUP5)
            .Op(Instruction.GAS)
            .Op(Instruction.DELEGATECALL)
            .Op(Instruction.RETURNDATASIZE)
            .PushData(zeroByte)
            .Op(Instruction.DUP1)
            .Op(Instruction.RETURNDATACOPY)
            .Op(Instruction.RETURNDATASIZE)
            .PushData(zeroByte)
            .Op(Instruction.RETURN)
            .Done;

        byte[] transactionData = EthRpcMulticallTestsBase.GetTxData(chain, TestItem.PrivateKeyA);

        var headHash = chain.BlockFinder.Head!.Hash!;
        Address? contractAddress = await EthRpcMulticallTestsBase.DeployEcRecoverContract(chain, TestItem.PrivateKeyB,
            EthMulticallTestsSimplePrecompiles.EcRecoverCallerContractBytecode);

        var tst = EthRpcMulticallTestsBase.MainChainTransaction(transactionData, contractAddress, chain, TestItem.AddressB);

        chain.BlockTree.UpdateMainChain(new List<Block> { chain.BlockFinder.Head! }, true, true);
        chain.BlockTree.UpdateHeadBlock(chain.BlockFinder.Head!.Hash!);

        var headHashAfterPost = chain.BlockFinder.Head!.Hash!;
        Assert.That(headHash != headHashAfterPost);
        chain.State.StateRoot = chain.BlockFinder.Head!.StateRoot!;

        Transaction systemTransactionForModifiedVm = new()
        {
            Data = transactionData,
            To = contractAddress,
            SenderAddress = TestItem.AddressA,
            GasLimit = 3_500_000,
            GasPrice = 20.GWei()
        };

        TransactionForRpc transactionForRpc = new(systemTransactionForModifiedVm) { Nonce = null };

        MultiCallPayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls = new BlockStateCall<TransactionForRpc>[]
            {
                new()
                {
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        {
                            EcRecoverPrecompile.Address,
                            new AccountOverride
                            {
                                Code = code,
                                //MovePrecompileToAddress = new Address("0x0000000000000000000000000000000000000666"),
                            }
                        },
                    },
                    Calls = new[]
                    {
                        transactionForRpc,
                    }
                }
            },
            TraceTransfers = false,
            Validation = false
        };

        //will mock our GetCachedCodeInfo function - it shall be called 3 times if redirect is working, 2 times if not
        MultiCallTxExecutor executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig());

        Debug.Assert(contractAddress != null, nameof(contractAddress) + " != null");
        Assert.IsTrue(chain.State.AccountExists(contractAddress));
        ResultWrapper<IReadOnlyList<SimulateBlockResult>> result = executor.Execute(payload, BlockParameter.Latest);

        //Check results
        byte[]? returnData = result.Data[0].Calls.First().ReturnData;
        byte[] addressBytes = returnData!.SliceWithZeroPaddingEmptyOnError(12, 20);
        Address resultingAddress = new(addressBytes);
        Assert.That(resultingAddress, Is.EqualTo(TestItem.AddressA));
    }
}
