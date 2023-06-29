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
using Nethermind.Core.Test.IO;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.KeyStore;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public class EthRpcMulticallTestsBase
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

}
