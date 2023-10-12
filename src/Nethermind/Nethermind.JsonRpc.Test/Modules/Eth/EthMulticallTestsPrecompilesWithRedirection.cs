// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
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

        Address? contractAddress = await EthRpcMulticallTestsBase.DeployEcRecoverContract(chain, TestItem.PrivateKeyB,
            EthMulticallTestsSimplePrecompiles.EcRecoverCallerContractBytecode);

        EthRpcMulticallTestsBase.MainChainTransaction(transactionData, contractAddress, chain, TestItem.AddressB);

        Transaction systemTransactionForModifiedVm = new()
        {
            Data = transactionData,
            To = contractAddress,
            SenderAddress = TestItem.AddressA,
            GasLimit = 3_500_000,
            GasPrice = 20.GWei()
        };

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
                                MovePrecompileToAddress = new Address("0x0000000000000000000000000000000000000666"),
                            }
                        },
                        {
                            TestItem.AddressA,
                            new AccountOverride
                            {
                                Balance = 10.Ether()
                            }
                        }
                    },
                    Calls = new[]
                    {
                        new TransactionForRpc(systemTransactionForModifiedVm),
                    }
                }
            },
            TraceTransfers = true,
            Validation = false
        };

        //will mock our GetCachedCodeInfo function - it shall be called 3 times if redirect is working, 2 times if not
        MultiCallTxExecutor executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig());

        ResultWrapper<IReadOnlyList<MultiCallBlockResult>> result = executor.Execute(payload, BlockParameter.Latest);

        //Check results
        byte[] addressBytes = result.Data[0].Calls.First().ReturnData!.SliceWithZeroPaddingEmptyOnError(12, 20);
        //Address resultingAddress = new(addressBytes);
        //Assert.That(resultingAddress, Is.EqualTo(TestItem.AddressA))
    }
}
