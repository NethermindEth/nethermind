// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.Precompiles;
using Nethermind.Facade.Proxy.Models;
using Nethermind.Facade.Proxy.Models.MultiCall;
using Nethermind.JsonRpc.Modules.Eth;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public class EthMulticallTestsPrecompilesWithRedirection
{
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
        byte[] code = Prepare.EvmCode
            .JUMPDEST()
            .PushData(new byte[] { 0 })
            .Op(Instruction.DUP1)
            .PushData(Bytes.FromHexString("0x0666")) //  666
            .Op(Instruction.SWAP1)
            .Op(Instruction.POP)
            .Op(Instruction.CALLDATASIZE)
            .PushData(new byte[] { 0 })
            .Op(Instruction.DUP1)
            .Op(Instruction.CALLDATACOPY)
            .PushData(new byte[] { 0 })
            .Op(Instruction.DUP1)
            .Op(Instruction.CALLDATASIZE)
            .PushData(new byte[] { 0 })
            .Op(Instruction.DUP5)
            .Op(Instruction.GAS)
            .Op(Instruction.DELEGATECALL)
            .Op(Instruction.RETURNDATASIZE)
            .PushData(new byte[] { 0 })
            .Op(Instruction.DUP1)
            .Op(Instruction.RETURNDATACOPY)
            .Op(Instruction.RETURNDATASIZE)
            .PushData(new byte[] { 0 })
            .Op(Instruction.RETURN)
            .Done;

        Address realSenderAccount = TestItem.AddressA;
        byte[] transactionData = EthRpcMulticallTestsBase.GetTxData(chain, TestItem.PrivateKeyA);

        Address? contractAddress = await EthRpcMulticallTestsBase.DeployEcRecoverContract(chain, TestItem.PrivateKeyB,
            EthMulticallTestsSimplePrecompiles.EcRecoverCallerContractBytecode);

        Address? mainChainRpcAddress =
            EthRpcMulticallTestsBase.MainChainTransaction(transactionData, contractAddress, chain, TestItem.AddressB);

        Transaction systemTransactionForModifiedVM = new()
        {
            Data = transactionData,
            To = contractAddress,
            SenderAddress = TestItem.PublicKeyB.Address,
            GasLimit = 50_000
        };


        chain.BlockTree.UpdateMainChain(new[] { chain.BlockFinder.Head }, true, true);
        chain.BlockTree.UpdateHeadBlock(chain.BlockFinder.Head.Hash);

        var header = chain.BlockFinder.Head.Header;
        var spec = chain.SpecProvider.GetSpec(header);
        systemTransactionForModifiedVM.GasPrice = header.BaseFeePerGas >= 1 ? header.BaseFeePerGas : 1;
        systemTransactionForModifiedVM.GasLimit = (long)systemTransactionForModifiedVM.CalculateTransactionPotentialCost(spec.IsEip1559Enabled, header.BaseFeePerGas);

        MultiCallPayload payload = new()
        {
            BlockStateCalls = new BlockStateCalls[] { new()
        {
            StateOverrides = new[]
            {
                new AccountOverride
                {
                    Address = EcRecoverPrecompile.Address,
                    Code = code,
                    MoveToAddress = new Address("0x0000000000000000000000000000000000000666")
                }
            },
            Calls = new[]
            {
                CallTransactionModel.FromTransaction(systemTransactionForModifiedVM),
            }
        }},
            TraceTransfers = true
        };

        //Force persistancy of head block in main chain
        chain.BlockTree.UpdateMainChain(new[] { chain.BlockFinder.Head }, true, true);
        chain.BlockTree.UpdateHeadBlock(chain.BlockFinder.Head.Hash);

        //will mock our GetCachedCodeInfo function - it shall be called 3 times if redirect is working, 2 times if not
        MultiCallTxExecutor executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig());

        ResultWrapper<MultiCallBlockResult[]> result =
            executor.Execute(payload, BlockParameter.Latest);

        //Check results
        byte[] addressBytes = result.Data[0].Calls[0].Return
               .SliceWithZeroPaddingEmptyOnError(12, 20);
        Address resultingAddress = new(addressBytes);
        Assert.AreEqual(realSenderAccount, resultingAddress);

    }
}
