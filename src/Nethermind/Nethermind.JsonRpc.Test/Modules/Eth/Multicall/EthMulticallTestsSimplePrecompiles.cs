// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Precompiles;
using Nethermind.Facade;
using Nethermind.Facade.Proxy.Models;
using Nethermind.Facade.Proxy.Models.MultiCall;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public class EthMulticallTestsSimplePrecompiles
{
    /* Compiled contract
* Call example for TestItem.AddressA
* recover 0xb6e16d27ac5ab427a7f68900ac5559ce272dc6c37c82b3e052246c82244c50e4 28 0x7b8b1991eb44757bc688016d27940df8fb971d7c87f77a6bc4e938e3202c4403 0x7e9267b0aeaa82fa765361918f2d8abd9cdd86e64aa6f2b81d3c4e0b69a7b055
* returns address: 0xb7705aE4c6F81B66cdB323C65f4E8133690fC099

pragma solidity ^0.8.7;

contract EcrecoverProxy {
    function recover(bytes32 hash, uint8 v, bytes32 r, bytes32 s) public pure returns (address) {
            return ecrecover(hash, v, r, s);
    }
}
*/
    //Taken from contract compiler output metadata
    public const string EcRecoverCallerContractBytecode =
        "608060405234801561001057600080fd5b5061028b806100206000396000f3fe608060405234801561001057600080fd5b506004361061002b5760003560e01c8063c2bf17b014610030575b600080fd5b61004a6004803603810190610045919061012f565b610060565b60405161005791906101d7565b60405180910390f35b6000600185858585604051600081526020016040526040516100859493929190610210565b6020604051602081039080840390855afa1580156100a7573d6000803e3d6000fd5b505050602060405103519050949350505050565b600080fd5b6000819050919050565b6100d3816100c0565b81146100de57600080fd5b50565b6000813590506100f0816100ca565b92915050565b600060ff82169050919050565b61010c816100f6565b811461011757600080fd5b50565b60008135905061012981610103565b92915050565b60008060008060808587031215610149576101486100bb565b5b6000610157878288016100e1565b94505060206101688782880161011a565b9350506040610179878288016100e1565b925050606061018a878288016100e1565b91505092959194509250565b600073ffffffffffffffffffffffffffffffffffffffff82169050919050565b60006101c182610196565b9050919050565b6101d1816101b6565b82525050565b60006020820190506101ec60008301846101c8565b92915050565b6101fb816100c0565b82525050565b61020a816100f6565b82525050565b600060808201905061022560008301876101f2565b6102326020830186610201565b61023f60408301856101f2565b61024c60608301846101f2565b9594505050505056fea26469706673582212204855668ab62273dde1249722b61c57ad057ef3d17384f21233e1b7bb309db7e464736f6c63430008120033";


    /// <summary>
    ///     This test verifies that a temporary forked blockchain can updates precompiles
    /// </summary>
    [Test]
    public async Task Test_eth_multicall_erc()
    {

        // Arrange
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();

        //Empose Opcode instead of EcRecoverPrecompile, it returns const TestItem.AddressE address
        byte[] code = Prepare.EvmCode
            .StoreDataInMemory(0, TestItem.AddressE
                .ToString(false, false)
                .PadLeft(64, '0'))
            .PushData(Bytes.FromHexString("0x20"))
            .PushData(Bytes.FromHexString("0x0"))
            .Op(Instruction.RETURN).Done;

        // Step 1: Take an account
        Address account = TestItem.AddressA;
        // Step 2: Hash the message
        Keccak messageHash = Keccak.Compute("Hello, world!");
        // Step 3: Sign the hash
        Signature signature = chain.EthereumEcdsa.Sign(TestItem.PrivateKeyA, messageHash);

        ulong v = signature.V;
        byte[] r = signature.R;
        byte[] s = signature.S;

        Address? contractAddress =
            await EthRpcMulticallTestsBase.DeployEcRecoverContract(chain, TestItem.PrivateKeyB,
                EcRecoverCallerContractBytecode);

        byte[] transactionData = EthRpcMulticallTestsBase.GenerateTransactionDataForEcRecover(messageHash, v, r, s);

        SystemTransaction systemTransactionForModifiedVM = new()
        {
            Data = transactionData,
            To = contractAddress,
            SenderAddress = TestItem.PublicKeyB.Address
        };

        systemTransactionForModifiedVM.Hash = systemTransactionForModifiedVM.CalculateHash();

        MultiCallPayload<Transaction> payload = new()
        {
            BlockStateCalls = new[]
            {
                new BlockStateCall<Transaction>()
                {
                    StateOverrides = new Dictionary<Address, AccountOverride>()
                    {
                        { EcRecoverPrecompile.Address, new AccountOverride { Code = code } }
                    },
                    Calls = new[] {  systemTransactionForModifiedVM  }
                }
            },
            TraceTransfers = true,
            Validation = false
        };

        // Act

        MultiCallOutput result = chain.Bridge.MultiCall(chain.BlockFinder.Head?.Header!, payload, CancellationToken.None);
        Log[]? logs = result.Items.First().Calls.First().Logs;
        byte[] addressBytes = result.Items.First().Calls.First().ReturnData!
            .SliceWithZeroPaddingEmptyOnError(12, 20);
        //Address resultingAddress = new(addressBytes);
        //Assert.That(resultingAddress, Is.EqualTo(TestItem.AddressE));

        //Check that initial VM is intact
        Address? mainChainRpcAddress =
            EthRpcMulticallTestsBase.MainChainTransaction(transactionData, contractAddress, chain, TestItem.AddressB);

        Assert.NotNull(mainChainRpcAddress);
        Assert.That(mainChainRpcAddress, Is.EqualTo(TestItem.AddressA));

    }
}
