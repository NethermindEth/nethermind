// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts.Json;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.Facade.Proxy.Models.MultiCall;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth.Multicall;
using Nethermind.KeyStore;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using Nethermind.Wallet;
using NUnit.Framework;
using ResultType = Nethermind.Core.ResultType;

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

    private static void allocateAccounts(MultiCallBlockStateCallsModel requestBlockOne, IWorldState _stateProvider,
        IReleaseSpec latestBlockSpec, ISpecProvider _specProvider, MultiCallVirtualMachine virtualMachine)
    {
        foreach (AccountOverride accountOverride in requestBlockOne.StateOverrides)
        {
            Address address = accountOverride.Address;
            Account? acc = _stateProvider.GetAccount(address);
            if (acc == null)
            {
                _stateProvider.CreateAccount(address, accountOverride.Balance, accountOverride.Nonce);
                acc = _stateProvider.GetAccount(address);
            }

            UInt256 t = acc.Balance;
            _stateProvider.SubtractFromBalance(address, 666, latestBlockSpec);

            _stateProvider.Commit(latestBlockSpec);
            // _storageProvider.Commit();


            if (acc != null)
                if (accountOverride.Code is not null)
                    virtualMachine.SetOverwrite(address, new CodeInfo(accountOverride.Code));


            if (accountOverride.State is not null)
            {
                accountOverride.State = new Dictionary<UInt256, byte[]>();
                foreach (KeyValuePair<UInt256, byte[]> storage in accountOverride.State)
                    _stateProvider.Set(new StorageCell(address, storage.Key),
                        storage.Value.WithoutLeadingZeros().ToArray());
            }

            if (accountOverride.StateDiff is not null)
            {
                foreach (KeyValuePair<UInt256, byte[]> storage in accountOverride.StateDiff)
                    _stateProvider.Set(new StorageCell(address, storage.Key),
                        storage.Value.WithoutLeadingZeros().ToArray());
            }
        }
    }

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
    private const string EcRecoverContractBytecode =
        "608060405234801561001057600080fd5b5061028b806100206000396000f3fe608060405234801561001057600080fd5b506004361061002b5760003560e01c8063c2bf17b014610030575b600080fd5b61004a6004803603810190610045919061012f565b610060565b60405161005791906101d7565b60405180910390f35b6000600185858585604051600081526020016040526040516100859493929190610210565b6020604051602081039080840390855afa1580156100a7573d6000803e3d6000fd5b505050602060405103519050949350505050565b600080fd5b6000819050919050565b6100d3816100c0565b81146100de57600080fd5b50565b6000813590506100f0816100ca565b92915050565b600060ff82169050919050565b61010c816100f6565b811461011757600080fd5b50565b60008135905061012981610103565b92915050565b60008060008060808587031215610149576101486100bb565b5b6000610157878288016100e1565b94505060206101688782880161011a565b9350506040610179878288016100e1565b925050606061018a878288016100e1565b91505092959194509250565b600073ffffffffffffffffffffffffffffffffffffffff82169050919050565b60006101c182610196565b9050919050565b6101d1816101b6565b82525050565b60006020820190506101ec60008301846101c8565b92915050565b6101fb816100c0565b82525050565b61020a816100f6565b82525050565b600060808201905061022560008301876101f2565b6102326020830186610201565b61023f60408301856101f2565b61024c60608301846101f2565b9594505050505056fea26469706673582212204855668ab62273dde1249722b61c57ad057ef3d17384f21233e1b7bb309db7e464736f6c63430008120033";

    //Taken from contract compiler output metadata
    private const string EcRecoverContractJsonAbi = @"[
  {
    ""payable"": false,
 	""inputs"": [
		{
			""internalType"": ""bytes32"",
			""name"": ""hash"",
			""type"": ""bytes32""
		},
		{
			""internalType"": ""uint8"",
			""name"": ""v"",
			""type"": ""uint8""
		},
		{
			""internalType"": ""bytes32"",
			""name"": ""r"",
			""type"": ""bytes32""
		},
		{
			""internalType"": ""bytes32"",
			""name"": ""s"",
			""type"": ""bytes32""
		}
	],
	""name"": ""recover"",
	""outputs"": [
		{
			""internalType"": ""address"",
			""name"": """",
			""type"": ""address""
		}
	],
	""stateMutability"": ""pure"",
	""type"": ""function""
  }
]";


    private async Task<Address?> DeployEcRecoverContract(TestRpcBlockchain chain1)
    {
        byte[] bytecode = Bytes.FromHexString(EcRecoverContractBytecode);
        Transaction tx = new()
        {
            Value = UInt256.Zero,
            Nonce = 0,
            Data = bytecode,
            GasLimit = 3_000_000,
            SenderAddress = TestItem.PublicKeyB.Address,
            To = null,
            GasPrice = 20.GWei()
        };
        // calculate contract address

        ILogManager logManager = SimpleConsoleLogManager.Instance;
        IKeyStoreConfig config = new KeyStoreConfig();
        config.KeyStoreDirectory = TempPath.GetTempDirectory().Path;
        ISymmetricEncrypter encrypter = new AesEncrypter(config, LimboLogs.Instance);

        IWallet? wallet = new DevKeyStoreWallet(
            new FileKeyStore(config,
                new EthereumJsonSerializer(), encrypter, new CryptoRandom(),
                LimboLogs.Instance, new PrivateKeyStoreIOSettingsProvider(config)),
            LimboLogs.Instance);

        ITxSigner txSigner = new WalletTxSigner(wallet, chain1.SpecProvider.ChainId);
        TxSealer txSealer = new(txSigner, chain1.Timestamper);
        TxPoolSender txSender = new(chain1.TxPool, txSealer, chain1.NonceManager, chain1.EthereumEcdsa);

        //Tested Alternative, often faster
        //chain.EthereumEcdsa.Sign(TestItem.PrivateKeyB, tx, true);
        //tx.Hash = tx.CalculateHash();
        //await chain.AddBlock(true, tx);
        //TxReceipt? createContractTxReceipt = chain.Bridge.GetReceipt(tx.Hash);
        //createContractTxReceipt.ContractAddress.Should().NotBeNull($"Contract transaction {tx.Hash} was not deployed.");

        Address contractAddress1 = null;
        using (SecureStringWrapper pass = new("testB"))
        {
            wallet.Import(TestItem.PrivateKeyB.KeyBytes, pass.SecureData);
            wallet.UnlockAccount(TestItem.PublicKeyB.Address, pass.SecureData, TimeSpan.MaxValue);
            (Keccak hash, AcceptTxResult? code) = await txSender.SendTransaction(tx,
                TxHandlingOptions.ManagedNonce | TxHandlingOptions.PersistentBroadcast);

            code.Value.Should().Be(AcceptTxResult.Accepted);
            Transaction[] txs = chain1.TxPool.GetPendingTransactions();

            await chain1.AddBlock(true, txs);


            TxReceipt createContractTxReceipt = null;
            while (createContractTxReceipt == null)
            {
                await Task.Delay(100); // wait... todo enforce!
                createContractTxReceipt = chain1.Bridge.GetReceipt(tx.Hash);
            }

            createContractTxReceipt.ContractAddress.Should()
                .NotBeNull($"Contract transaction {tx.Hash} was not deployed.");
            contractAddress1 = createContractTxReceipt.ContractAddress;
        }

        return contractAddress1;
    }

    /// <summary>
    ///     This test verifies that a temporary forked blockchain can updates precompiles
    /// </summary>
    [Test]
    public async Task Test_eth_multicall_erc()
    {
        byte[] GenerateTransactionData(Keccak keccak, ulong @ulong, byte[] bytes1, byte[] bytes2)
        {
            AbiDefinitionParser parser = new();
            AbiDefinition call = parser.Parse(EcRecoverContractJsonAbi);
            AbiEncodingInfo functionInfo = call.GetFunction("recover").GetCallInfo();
            byte[] transactionData1 = AbiEncoder.Instance.Encode(functionInfo.EncodingStyle,
                functionInfo.Signature,
                keccak, @ulong, bytes1, bytes2);
            return transactionData1;
        }

        void MainChainTransaction(byte[] bytes, Address? address, TestRpcBlockchain testRpcBlockchain)
        {
            SystemTransaction transaction = new()
            {
                Data = bytes, To = address, SenderAddress = TestItem.PublicKeyB.Address
            };
            transaction.Hash = transaction.CalculateHash();
            TransactionForRpc transactionForRpc = new(transaction);
            ResultWrapper<string> mainChainResult =
                testRpcBlockchain.EthRpcModule.eth_call(transactionForRpc, BlockParameter.Pending);

            byte[] mainChainResultBytes =
                Bytes.FromHexString(mainChainResult.Data).SliceWithZeroPaddingEmptyOnError(12, 20);
            Address mainChainRpcAddress = new(mainChainResultBytes);
            Assert.AreEqual(TestItem.AddressA, mainChainRpcAddress);
        }

        TestRpcBlockchain chain = await CreateChain();

        //Empose Opcode instead of EcRecoverPrecompile, it returns const TestItem.AddressE address
        byte[] code = Prepare.EvmCode
            .StoreDataInMemory(0, TestItem.AddressE.ToString(false, false).PadLeft(64, '0'))
            .PushData(Bytes.FromHexString("0x20"))
            .PushData(Bytes.FromHexString("0x0"))
            .Op(Instruction.RETURN).Done;
        MultiCallBlockStateCallsModel requestMultiCall = new();
        requestMultiCall.StateOverrides =
            new[] { new AccountOverride { Address = EcRecoverPrecompile.Instance.Address, Code = code } };

        // Step 1: Take an account
        Address account = TestItem.AddressA;
        // Step 2: Hash the message
        Keccak messageHash = Keccak.Compute("Hello, world!");
        // Step 3: Sign the hash
        Signature signature = chain.EthereumEcdsa.Sign(TestItem.PrivateKeyA, messageHash);

        ulong v = signature.V;
        byte[] r = signature.R;
        byte[] s = signature.S;

        Address? contractAddress = await DeployEcRecoverContract(chain);

        //Check real address
        Address recoveredAddress = chain.EthereumEcdsa.RecoverAddress(signature, messageHash);
        Assert.AreEqual(TestItem.AddressA, recoveredAddress);

        byte[] transactionData = GenerateTransactionData(messageHash, v, r, s);


        MainChainTransaction(transactionData, contractAddress, chain);

        //Force persistancy of head block in main chain
        chain.BlockTree.UpdateMainChain(new[] { chain.BlockFinder.Head }, true, true);
        chain.BlockTree.UpdateHeadBlock(chain.BlockFinder.Head.Hash);
        using (MultiCallBlockchainFork tmpChain = new(chain.DbProvider, chain.SpecProvider))
        {
            foreach (AccountOverride accountOverride in requestMultiCall.StateOverrides)
                if (accountOverride.Code != null)
                    tmpChain.VirtualMachine.SetOverwrite(accountOverride.Address, new CodeInfo(accountOverride.Code));

            //Generate and send transaction
            SystemTransaction systemTransactionForModifiedVM = new()
            {
                Data = transactionData, To = contractAddress, SenderAddress = TestItem.PublicKeyB.Address
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
            Assert.AreNotEqual(account, resultingAddress);
            Assert.AreEqual(TestItem.AddressE, resultingAddress);

            //Note: real address can still be accessed
            Address recoveredAddressOnMulticallChain = tmpChain.EthereumEcdsa.RecoverAddress(signature, messageHash);
            Assert.AreEqual(TestItem.AddressA, recoveredAddressOnMulticallChain);
        }

        //Check that initial VM is intact
        MainChainTransaction(transactionData, contractAddress, chain);
    }

    /// <summary>
    ///     This test verifies that a temporary forked blockchain updates the user balance and block number
    ///     independently of the main chain, ensuring the main chain remains intact.
    /// </summary>
    [Test]
    public async Task Test_eth_multicall()
    {
        TestRpcBlockchain chain = await CreateChain();

        MultiCallBlockStateCallsModel requestBlockOne = new();
        requestBlockOne.StateOverrides =
            new[] { new AccountOverride { Address = TestItem.AddressA, Balance = UInt256.One } };

        long blockNumberBefore = chain.BlockFinder.Head.Number;
        ResultWrapper<UInt256?> userBalanceBefore =
            await chain.EthRpcModule.eth_getBalance(TestItem.AddressA, BlockParameter.Latest);
        userBalanceBefore.Result.ResultType.Should().Be(ResultType.Success);

        //Force persistancy of head block in main chain
        chain.BlockTree.UpdateMainChain(new[] { chain.BlockFinder.Head }, true, true);
        chain.BlockTree.UpdateHeadBlock(chain.BlockFinder.Head.Hash);

        TrieStore tt = chain.TrieStore;
        using (MultiCallBlockchainFork tmpChain = new(chain.DbProvider, chain.SpecProvider))
        {
            //Check if tmpChain initialised
            Assert.AreEqual(chain.BlockTree.BestKnownNumber, tmpChain.BlockTree.BestKnownNumber);
            Assert.AreEqual(chain.BlockFinder.BestPersistedState, tmpChain.BlockFinder.BestPersistedState);
            Assert.AreEqual(chain.BlockFinder.Head.Number, tmpChain.BlockFinder.Head.Number);

            //Check if tmpChain RPC initialised
            ResultWrapper<UInt256?> userBalanceBefore_fromTmp =
                await tmpChain.EthRpcModule.eth_getBalance(TestItem.AddressA, BlockParameter.Latest);
            userBalanceBefore_fromTmp.Result.ResultType.Should().Be(ResultType.Success);

            //Check if tmpChain shows same values as main one
            UInt256 num_real = userBalanceBefore.Data.Value;
            UInt256 num_tmp = userBalanceBefore_fromTmp.Data.Value;
            Assert.AreEqual(userBalanceBefore_fromTmp.Data, userBalanceBefore.Data);

            bool processed = tmpChain.ForgeChainBlock((stateProvider, currentSpec, specProvider, virtualMachine) =>
            {
                allocateAccounts(requestBlockOne, stateProvider, currentSpec, specProvider, virtualMachine);
            });

            //Check block has been added to chain as main
            Assert.True(processed);

            //Check block has updated values in tmp chain
            ResultWrapper<UInt256?> userBalanceResult_fromTm =
                await tmpChain.EthRpcModule.eth_getBalance(TestItem.AddressA, BlockParameter.Latest);
            userBalanceResult_fromTm.Result.ResultType.Should().Be(ResultType.Success);
            UInt256 tval = tmpChain.StateProvider.GetBalance(TestItem.AddressA);

            Assert.AreNotEqual(userBalanceResult_fromTm.Data, userBalanceBefore.Data);

            //Check block has not updated values in the main chain
            ResultWrapper<UInt256?> userBalanceResult =
                await chain.EthRpcModule.eth_getBalance(TestItem.AddressA, BlockParameter.Latest);
            userBalanceResult.Result.ResultType.Should().Be(ResultType.Success);
            Assert.AreEqual(userBalanceResult.Data, userBalanceBefore.Data); //Main chain is intact
            Assert.AreNotEqual(userBalanceResult.Data, userBalanceResult_fromTm.Data); // Balance was changed
            Assert.AreNotEqual(chain.BlockFinder.Head.Number, tmpChain.LatestBlock.Number); // Block number changed
        }

        GC.Collect();
        GC.WaitForFullGCComplete();

        Assert.AreEqual(chain.BlockFinder.Head.Number,
            blockNumberBefore); // tmp chain is disposed, main chain block number is still the same
    }
}
