// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
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
using Nethermind.Facade.Proxy.Models.MultiCall;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Eth.Multicall;
using Nethermind.KeyStore;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using Nethermind.Wallet;
using NUnit.Framework;
using static Nethermind.JsonRpc.Modules.Eth.EthRpcModule;
using ResultType = Nethermind.Core.ResultType;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public class EthRpcMulticallTests
{
    public static Task<TestRpcBlockchain> CreateChain(IReleaseSpec? releaseSpec = null,
        UInt256? initialBaseFeePerGas = null)
    {
        TestRpcBlockchain testMevRpcBlockchain = new();
        TestSpecProvider testSpecProvider = releaseSpec is not null
            ? new TestSpecProvider(releaseSpec)
            : new TestSpecProvider(London.Instance);
        return TestRpcBlockchain.ForTest(testMevRpcBlockchain).Build(testSpecProvider);
    }


    public static string getEcRecoverContractJsonAbi(string name = "recover")
    {
        return $@"[
  {{
    ""payable"": false,
 	""inputs"": [
		{{
			""internalType"": ""bytes32"",
			""name"": ""hash"",
			""type"": ""bytes32""
		}},
		{{
			""internalType"": ""uint8"",
			""name"": ""v"",
			""type"": ""uint8""

        }},
		{{
			""internalType"": ""bytes32"",
			""name"": ""r"",
			""type"": ""bytes32""
		}},
		{{
			""internalType"": ""bytes32"",
			""name"": ""s"",
			""type"": ""bytes32""
		}}
	],
	""name"": ""{name}"",
	""outputs"": [
		{{
			""internalType"": ""address"",
			""name"": """",
			""type"": ""address""
		}}
	],
	""stateMutability"": ""pure"",
	""type"": ""function""
  }}
]";
    }

    public static byte[] GetTxData(TestRpcBlockchain chain, PrivateKey account, string name = "recover")
    {
        // Step 1: Take an account
        // Step 2: Hash the message
        Keccak messageHash = Keccak.Compute("Hello, world!");
        // Step 3: Sign the hash
        Signature signature = chain.EthereumEcdsa.Sign(account, messageHash);

        ulong v = signature.V;
        byte[] r = signature.R;
        byte[] s = signature.S;

        //Check real address

        byte[] transactionData = GenerateTransactionDataForEcRecover(messageHash, v, r, s, name);
        return transactionData;
    }

    public static async Task<Address?> DeployEcRecoverContract(TestRpcBlockchain chain1, PrivateKey fromPrivateKey,
        string ContractBytecode)
    {
        byte[] bytecode = Bytes.FromHexString(ContractBytecode);
        Transaction tx = new()
        {
            Value = UInt256.Zero,
            Nonce = 0,
            Data = bytecode,
            GasLimit = 3_000_000,
            SenderAddress = fromPrivateKey.Address,
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
        //chain1.EthereumEcdsa.Sign(TestItem.PrivateKeyB, tx, true);
        //tx.Hash = tx.CalculateHash();
        //wait chain1.AddBlock(true, tx);
        //TxReceipt? createContractTxReceipt2 = chain1.Bridge.GetReceipt(tx.Hash);
        //createContractTxReceipt2.ContractAddress
        //    .Should().NotBeNull($"Contract transaction {tx.Hash} was not deployed.");

        Address contractAddress1 = null;
        using (SecureStringWrapper pass = new("testB"))
        {
            wallet.Import(fromPrivateKey.KeyBytes, pass.SecureData);
            wallet.UnlockAccount(fromPrivateKey.Address, pass.SecureData, TimeSpan.MaxValue);
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

    public static byte[] GenerateTransactionDataForEcRecover(Keccak keccak, ulong @ulong, byte[] bytes1, byte[] bytes2,
        string name = "recover")
    {
        AbiDefinitionParser parser = new();
        AbiDefinition call = parser.Parse(getEcRecoverContractJsonAbi(name));
        AbiEncodingInfo functionInfo = call.GetFunction(name).GetCallInfo();
        byte[] transactionData1 = AbiEncoder.Instance.Encode(functionInfo.EncodingStyle,
            functionInfo.Signature,
            keccak, @ulong, bytes1, bytes2);
        return transactionData1;
    }

    public static Address? GetTransactionResultFromEcRecover(byte[] data, string name = "recover")
    {
        AbiDefinitionParser parser = new();
        AbiDefinition call = parser.Parse(getEcRecoverContractJsonAbi(name));
        AbiEncodingInfo functionInfo = call.GetFunction("recover").GetReturnInfo();
        Address? transactionData1 = AbiEncoder.Instance.Decode(functionInfo.EncodingStyle,
            functionInfo.Signature, data).FirstOrDefault() as Address;
        return transactionData1;
    }

    public static Address? MainChainTransaction(byte[] bytes, Address? toAddress, TestRpcBlockchain testRpcBlockchain,
        Address senderAddress)
    {
        SystemTransaction transaction = new() { Data = bytes, To = toAddress, SenderAddress = senderAddress };
        transaction.Hash = transaction.CalculateHash();
        TransactionForRpc transactionForRpc = new(transaction);
        ResultWrapper<string> mainChainResult =
            testRpcBlockchain.EthRpcModule.eth_call(transactionForRpc, BlockParameter.Pending);

        //byte[] mainChainResultBytes =
        //    Bytes.FromHexString(mainChainResult.Data).SliceWithZeroPaddingEmptyOnError(12, 20);
        Address? mainChainRpcAddress =
            GetTransactionResultFromEcRecover(Bytes.FromHexString(mainChainResult.Data)); //new(mainChainResultBytes);
        return mainChainRpcAddress;
    }

    /// <summary>
    ///     This test verifies that a temporary forked blockchain updates the user balance and block number
    ///     independently of the main chain, ensuring the main chain remains intact.
    /// </summary>
    [Test]
    public async Task Test_eth_multicall_account_data()
    {
        TestRpcBlockchain chain = await CreateChain();

        MultiCallBlockStateCallsModel requestBlockOne = new()
        {
            StateOverrides = new[] { new AccountOverride { Address = TestItem.AddressA, Balance = UInt256.One } }
        };


        long blockNumberBefore = chain.BlockFinder.Head.Number;
        ResultWrapper<UInt256?> userBalanceBefore =
            await chain.EthRpcModule.eth_getBalance(TestItem.AddressA, BlockParameter.Latest);
        userBalanceBefore.Result.ResultType.Should().Be(ResultType.Success);

        //Force persistancy of head block in main chain
        chain.BlockTree.UpdateMainChain(new[] { chain.BlockFinder.Head }, true, true);
        chain.BlockTree.UpdateHeadBlock(chain.BlockFinder.Head.Hash);

        TrieStore tt = chain.TrieStore;
        using (MultiCallBlockchainFork tmpChain = new(chain.DbProvider, chain.SpecProvider,
                   MultiCallTxExecutor.GetMaxGas(new JsonRpcConfig())))
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

            Block? _ = tmpChain.ForgeChainBlock(requestBlockOne);

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
