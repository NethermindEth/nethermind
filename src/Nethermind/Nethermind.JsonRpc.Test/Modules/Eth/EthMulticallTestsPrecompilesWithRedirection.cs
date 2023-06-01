// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Precompiles;
using Nethermind.Facade.Proxy.Models.MultiCall;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Eth.Multicall;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public class EthMulticallTestsPrecompilesWithRedirection
{
    /* Compiled contract
    pragma solidity ^0.8.7;

    // An interface wrapper for Ecrecover 
    interface IPrecompiledEcrecoverContract {
        function ecrecover(bytes32 hash, uint8 v, bytes32 r, bytes32 s) pure external returns (address)  ;
    }

        * contract that expects real ecrecover to be at 0x0000000000000000000000000000000000000666 and is to be used for ecrecover mocking
        * Call example for TestItem.AddressA, "Hello, world!" message
        * recover 0xb6e16d27ac5ab427a7f68900ac5559ce272dc6c37c82b3e052246c82244c50e4 28 0x7b8b1991eb44757bc688016d27940df8fb971d7c87f77a6bc4e938e3202c4403 0x7e9267b0aeaa82fa765361918f2d8abd9cdd86e64aa6f2b81d3c4e0b69a7b055
        * returns address mocked by hash ending 0xB6E16D check to: 0x0000000000000000000000000000000000011111
        * if hash does not end in 0xB6E16D returns narmal ecrecover values
        * recover 0xA6e16d27ac5ab427a7f68900ac5559ce272dc6c37c82b3e052246c82244c50e4 28 0x7b8b1991eb44757bc688016d27940df8fb971d7c87f77a6bc4e938e3202c4403 0x7e9267b0aeaa82fa765361918f2d8abd9cdd86e64aa6f2b81d3c4e0b69a7b055
        * returns address: shall be as in ecrecover:  0x6F3566EDa7CF07302FDa3654Cc65e447Afd2871C
        contract EcrecoverProxy
        {
            function ecrecover(bytes32 hash, uint8 v, bytes32 r, bytes32 s) public pure returns(address)
        {
            address redirectedToAddress = 0x0000000000000000000000000000000000000666;
            address predefinedResultAddress = 0x0000000000000000000000000000000000011111;

            // "hash/(2**232)": This operation shifts the hash value 232 bits to the right.
            // thus in 0xB6E16D27AC5AB427A7F68900AC5559CE272DC6C37C82B3E052246C82244C50E4 it would give 0xB6E16D
            // In other words, it's dividing the hash by 2**232 which has the effect of 
            // discarding the lowest 232 bits and keeping the highest 24 bits (since 256 - 232 = 24).
            // "uint24(...)": This casts the result of the division operation to a uint24.
            // This is necessary because the result of the division is still a uint256, 
            // but the highest 24 bits are the ones we're interested in, and the rest are zeros.
            uint24 end = uint24(uint256(hash) / (2 * *232));
            bool check = end == 0xB6E16D;
            if (check)
            {
                return predefinedResultAddress;
            }
            else
            {
                IPrecompiledEcrecoverContract myInterface = IPrecompiledEcrecoverContract(redirectedToAddress);
                return myInterface.ecrecover(hash, v, r, s);
            }

        }

    }
    */

    //A way to call original ecrecover add an a redirecton argument
    //Think in the future Allow to force a cost to code vs Dynamic code cost
    //Peak Totall gas (before refunds substraction)
    //Docker image

    //Taken from contract compiler output metadata
    private const string EcRecoverProxyFunctionContractBytecode =
        "608060405234801561001057600080fd5b506102b9806100206000396000f3fe608060405234801561001057600080fd5b506004361061002b5760003560e01c806396d107f614610030575b600080fd5b61004a60048036038101906100459190610156565b610060565b60405161005791906101fe565b60405180910390f35b6000806106669050600062011111905060007d0100000000000000000000000000000000000000000000000000000000008860001c61009f9190610252565b9050600062b6e16d8262ffffff1614905080156100c257829450505050506100da565b3660008037600080366000875af43d6000803e3d6000f35b949350505050565b600080fd5b6000819050919050565b6100fa816100e7565b811461010557600080fd5b50565b600081359050610117816100f1565b92915050565b600060ff82169050919050565b6101338161011d565b811461013e57600080fd5b50565b6000813590506101508161012a565b92915050565b600080600080608085870312156101705761016f6100e2565b5b600061017e87828801610108565b945050602061018f87828801610141565b93505060406101a087828801610108565b92505060606101b187828801610108565b91505092959194509250565b600073ffffffffffffffffffffffffffffffffffffffff82169050919050565b60006101e8826101bd565b9050919050565b6101f8816101dd565b82525050565b600060208201905061021360008301846101ef565b92915050565b6000819050919050565b7f4e487b7100000000000000000000000000000000000000000000000000000000600052601260045260246000fd5b600061025d82610219565b915061026883610219565b92508261027857610277610223565b5b82820490509291505056fea2646970667358221220a26a78048ff9fffb3a331accd3da703877d83336f68578b26df710a5408e204364736f6c63430008120033";


    /// <summary>
    ///     This test verifies that a temporary forked blockchain can updates precompiles
    /// </summary>
    [Test]
    public async Task Test_eth_multicall_ecr_moved()
    {
        TestRpcBlockchain chain = await EthRpcMulticallTests.CreateChain();

        //TODO: add IF-ELSE test clause. Sadly SC code was too hard to reimplement in opcodes and binary does not fit in asis
        //So currently we use:
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
        MultiCallBlockStateCallsModel requestMultiCall = new();
        requestMultiCall.StateOverrides =
            new[]
            {
                new AccountOverride
                {
                    Address = EcRecoverPrecompile.Instance.Address,
                    Code = code,
                    MoveToAddress = new Address("0x0000000000000000000000000000000000000666")
                }
            };

        Address realSenderAccount = TestItem.AddressA;
        byte[] transactionData = EthRpcMulticallTests.GetTxData(chain, realSenderAccount);

        Address? contractAddress = await EthRpcMulticallTests.DeployEcRecoverContract(chain, TestItem.PrivateKeyB,
            EthMulticallTestsSimplePrecompiles.EcRecoverCallerContractBytecode);

        Address? mainChainRpcAddress =
            EthRpcMulticallTests.MainChainTransaction(transactionData, contractAddress, chain, TestItem.AddressB);

        //Force persistancy of head block in main chain
        chain.BlockTree.UpdateMainChain(new[] { chain.BlockFinder.Head }, true, true);
        chain.BlockTree.UpdateHeadBlock(chain.BlockFinder.Head.Hash);

        //will mock our GetCachedCodeInfo function - it shall be called 3 times if redirect is working, 2 times if not
        using (MultiCallBlockchainFork tmpChain = new(chain.DbProvider, chain.SpecProvider))
        {
            bool processed = tmpChain.ForgeChainBlock((stateProvider, currentSpec, specProvider, virtualMachine) =>
            {
                EthRpcModule.MultiCallTxExecutor.ModifyAccounts(requestMultiCall, stateProvider, currentSpec,
                    specProvider, virtualMachine);
            });
            Assert.True(processed);

            //Generate and send transaction (shall be mocked)
            SystemTransaction systemTransactionForModifiedVM = new()
            {
                Data = transactionData,
                To = contractAddress,
                SenderAddress = TestItem.PublicKeyB.Address
            };
            systemTransactionForModifiedVM.Hash = systemTransactionForModifiedVM.CalculateHash();
            TransactionForRpc transactionForRpcOfModifiedVM = new(systemTransactionForModifiedVM);
            ResultWrapper<string> responseFromModifiedVM =
                tmpChain.EthRpcModule.eth_call(transactionForRpcOfModifiedVM, BlockParameter.Pending);
            responseFromModifiedVM.ErrorCode.Should().Be(ErrorCodes.None);


            //Check results
            byte[] addressBytes = Bytes.FromHexString(responseFromModifiedVM.Data)
                .SliceWithZeroPaddingEmptyOnError(12, 20);
            Address resultingAddress = new(addressBytes);

            //Address expectedMockResultAddress = new Address("0x0000000000000000000000000000000000011111");
            //We redirect to 666 so it will return correct data
            Assert.AreEqual(realSenderAccount, resultingAddress);
        }
    }
}
