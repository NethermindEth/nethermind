// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts.Json;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
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
using Org.BouncyCastle.Asn1.X509;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public class EthRpcSimulateTestsBase
{
    public static Task<TestRpcBlockchain> CreateChain(IReleaseSpec? releaseSpec = null)
    {
        TestRpcBlockchain testMevRpcBlockchain = new();
        TestSpecProvider testSpecProvider = releaseSpec is not null
            ? new TestSpecProvider(releaseSpec)
            : new TestSpecProvider(London.Instance);
        return TestRpcBlockchain.ForTest(testMevRpcBlockchain).Build(testSpecProvider, null);
    }

    private static string GetEcRecoverContractJsonAbi(string name = "recover")
    {
        return $@"
[
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
        // Step 1: Hash the message
        Hash256 messageHash = Keccak.Compute("Hello, world!");
        // Step 2: Sign the hash
        Signature signature = chain.EthereumEcdsa.Sign(account, messageHash);

        //Check real address
        return GenerateTransactionDataForEcRecover(messageHash, signature, name);
    }

    public static async Task<Address> DeployEcRecoverContract(TestRpcBlockchain chain, PrivateKey privateKey, string contractBytecode)
    {
        byte[] bytecode = Bytes.FromHexString(contractBytecode);
        Transaction tx = new()
        {
            Value = UInt256.Zero,
            Nonce = 0,
            Data = bytecode,
            GasLimit = 3_000_000,
            SenderAddress = privateKey.Address,
            To = null,
            GasPrice = 20.GWei()
        };

        TxPoolSender txSender = new(chain.TxPool,
            new TxSealer(new Signer(chain.SpecProvider.ChainId, privateKey, LimboLogs.Instance), chain.Timestamper),
            chain.NonceManager,
            chain.EthereumEcdsa);

        (Hash256 hash, AcceptTxResult? code) = await txSender.SendTransaction(tx, TxHandlingOptions.ManagedNonce | TxHandlingOptions.PersistentBroadcast);

        code?.Should().Be(AcceptTxResult.Accepted);
        Transaction[] txs = chain.TxPool.GetPendingTransactions();
        await chain.AddBlock(true, txs);

        TxReceipt? createContractTxReceipt = null;
        while (createContractTxReceipt is null)
        {
            await Task.Delay(100);
            createContractTxReceipt = chain.Bridge.GetReceipt(hash);
        }

        createContractTxReceipt.ContractAddress.Should().NotBeNull($"Contract transaction {tx.Hash!} was not deployed.");
        return createContractTxReceipt.ContractAddress!;
    }

    protected static byte[] GenerateTransactionDataForEcRecover(Hash256 keccak, Signature signature, string name = "recover")
    {
        AbiDefinition call = new AbiDefinitionParser().Parse(GetEcRecoverContractJsonAbi(name));
        AbiEncodingInfo functionInfo = call.GetFunction(name).GetCallInfo();
        return AbiEncoder.Instance.Encode(functionInfo.EncodingStyle, functionInfo.Signature, keccak, signature.V, signature.R, signature.S);
    }

    private static Address? ParseEcRecoverAddress(byte[] data, string name = "recover")
    {
        AbiDefinition call = new AbiDefinitionParser().Parse(GetEcRecoverContractJsonAbi(name));
        AbiEncodingInfo functionInfo = call.GetFunction(name).GetReturnInfo();
        return AbiEncoder.Instance.Decode(functionInfo.EncodingStyle, functionInfo.Signature, data).FirstOrDefault() as Address;
    }

    public static Address? EcRecoverCall(TestRpcBlockchain testRpcBlockchain, Address senderAddress, byte[] bytes, Address? toAddress = null)
    {
        SystemTransaction transaction = new() { Data = bytes, To = toAddress, SenderAddress = senderAddress };
        transaction.Hash = transaction.CalculateHash();
        TransactionForRpc transactionForRpc = new(transaction);
        ResultWrapper<string> mainChainResult = testRpcBlockchain.EthRpcModule.eth_call(transactionForRpc, BlockParameter.Pending);
        return ParseEcRecoverAddress(Bytes.FromHexString(mainChainResult.Data));
    }
}
