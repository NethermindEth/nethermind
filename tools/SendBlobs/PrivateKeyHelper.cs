// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Logging.Abstractions;
using Nethermind.Cli;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Facade.Proxy.Models;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Org.BouncyCastle.Utilities.Encoders;

namespace SendBlobs;
internal class PrivateKeyHelper
{
    public static IEnumerable<PrivateKey> ReadKeysFromFile(string path)
    {
        return File.ReadAllLines(path).Select(l => new PrivateKey(l));
    }

    public async static Task<IEnumerable<PrivateKey>> DitributeFunds(INodeManager nodeManager, ulong chainId, Signer distributeFrom, uint keysToMake, string? keyFilePath = null)
    {
        string? balanceString = await nodeManager.Post("eth_getBalance", distributeFrom.Address, "latest");
        if (balanceString is null)
            throw new Exception($"Unable to get balance for {distributeFrom.Address}");
        string? nonceString = await nodeManager.Post<string>("eth_getTransactionCount", distributeFrom.Address, "latest");
        if (nonceString is null)
            throw new Exception($"Unable to get nonce for {distributeFrom.Address}");

        string? gasPriceRes = await nodeManager.Post<string>("eth_gasPrice") ?? "1";
        UInt256 gasPrice = (UInt256)Convert.ToUInt64(gasPriceRes, gasPriceRes.StartsWith("0x") ? 16 : 10);

        string? maxPriorityFeePerGasRes = await nodeManager.Post<string>("eth_maxPriorityFeePerGas") ?? "1";
        UInt256 maxPriorityFeePerGas = (UInt256)Convert.ToUInt64(maxPriorityFeePerGasRes, maxPriorityFeePerGasRes.StartsWith("0x") ? 16 : 10);
        UInt256 balance = new UInt256(Bytes.FromHexString(balanceString));

        if (balance == 0)
            throw new Exception($"Balance on provided signer {distributeFrom.Address} is 0.");

        ulong nonce = Convert.ToUInt64(nonceString, nonceString.StartsWith("0x") ? 16 : 10);

        //Distribute 97% of the balance
        UInt256 temp;
        UInt256.Multiply(balance, 970, out temp);
        UInt256 balanceToDistribute;
        UInt256.Divide(temp, 1000, out balanceToDistribute);

        //Add an extra buffer for gas fees
        temp = balanceToDistribute;
        UInt256 gasBuffer = new UInt256(keysToMake) * 1_000_000_000;
        UInt256.Subtract(temp, gasBuffer, out balanceToDistribute);

        //Check for underflow
        if (balanceToDistribute > balance)
            throw new Exception($"Not enough balance on {distributeFrom.Address} to distribute to {keysToMake} addresses");

        UInt256 perKeyToSend;
        UInt256.Divide(balanceToDistribute, new UInt256(keysToMake), out perKeyToSend);

        using PrivateKeyGenerator generator = new();
        IEnumerable<PrivateKey> privateKeys = Enumerable.Range(1, (int)keysToMake).Select(i => generator.Generate());

        TxDecoder txDecoder = new();

        foreach (PrivateKey key in privateKeys)
        {
            gasPriceRes = await nodeManager.Post<string>("eth_gasPrice") ?? "1";
            gasPrice = (UInt256)Convert.ToUInt64(gasPriceRes, gasPriceRes.StartsWith("0x") ? 16 : 10);

            maxPriorityFeePerGasRes = await nodeManager.Post<string>("eth_maxPriorityFeePerGas") ?? "1";
            maxPriorityFeePerGas = (UInt256)Convert.ToUInt64(maxPriorityFeePerGasRes, maxPriorityFeePerGasRes.StartsWith("0x") ? 16 : 10);

            Transaction tx = new()
            {
                Type = TxType.EIP1559,
                ChainId = chainId,
                Nonce = nonce,
                GasLimit = GasCostOf.Transaction,
                GasPrice = maxPriorityFeePerGas,
                DecodedMaxFeePerGas = gasPrice + maxPriorityFeePerGas,
                Value = perKeyToSend,
                To = key.Address,
            };
            await distributeFrom.Sign(tx);

            string txRlp = Hex.ToHexString(txDecoder
                .Encode(tx, RlpBehaviors.SkipTypedWrapping | RlpBehaviors.InMempoolForm).Bytes);

            string? result = await nodeManager.Post<string>("eth_sendRawTransaction", "0x" + txRlp);

            nonce++;
        }
        if (!string.IsNullOrWhiteSpace(keyFilePath))
            File.WriteAllLines(keyFilePath, privateKeys.Select(k => k.ToString()), Encoding.ASCII);

        IEnumerable<PrivateKey> privateSigners =
        File.ReadAllLines(keyFilePath, Encoding.ASCII)
            .Select(k => new PrivateKey(k));

        foreach (PrivateKey privateSigner in privateSigners)
        {
            if (!privateKeys.Any(k => k.Equals(privateSigner)))
            {

            }
        }    


        return privateKeys;
    }
    public async static Task ReclaimFunds(INodeManager nodeManager, ulong chainId, Address beneficiary, string keyFilePath, ILogManager logManager)
    {
        IEnumerable<Signer> privateSigners =
            File.ReadAllLines(keyFilePath)
            .Select(k=> new Signer(chainId, new PrivateKey(k), logManager));

        foreach (var signer in privateSigners)
        {
            string? balanceString = await nodeManager.Post("eth_getBalance", signer.Address, "latest");
            if (balanceString is null)
                continue;
            string? nonceString = await nodeManager.Post<string>("eth_getTransactionCount", signer.Address, "latest");
            if (nonceString is null)
                continue;

            UInt256 balance = new UInt256(Bytes.FromHexString(balanceString));

            ulong nonce = Convert.ToUInt64(nonceString, nonceString.StartsWith("0x") ? 16 : 10);

            string? gasPriceRes = await nodeManager.Post<string>("eth_gasPrice") ?? "1";
            UInt256 gasPrice = (UInt256)Convert.ToUInt64(gasPriceRes, gasPriceRes.StartsWith("0x") ? 16 : 10);

            string? maxPriorityFeePerGasRes = await nodeManager.Post<string>("eth_maxPriorityFeePerGas") ?? "1";
            UInt256 maxPriorityFeePerGas = (UInt256)Convert.ToUInt64(maxPriorityFeePerGasRes, maxPriorityFeePerGasRes.StartsWith("0x") ? 16 : 10);

            //10 gwei extra buffer
            UInt256 approxGasFee = (gasPrice + maxPriorityFeePerGas + 10_000_000_000) * GasCostOf.Transaction;

            if (balance < approxGasFee)
            {
                Console.WriteLine($"Not enough funds on {signer.Address} to pay for gas");
                continue;
            }

            UInt256 toSend = balance - approxGasFee;

            TxDecoder txDecoder = new();
            Transaction tx = new()
            {
                Type = TxType.EIP1559,
                ChainId = chainId,
                Nonce = nonce,
                GasLimit = GasCostOf.Transaction,
                GasPrice = maxPriorityFeePerGas,
                DecodedMaxFeePerGas = gasPrice + maxPriorityFeePerGas,
                Value = toSend,
                To = beneficiary,
            };
            await signer.Sign(tx);

            string txRlp = Hex.ToHexString(txDecoder
                .Encode(tx, RlpBehaviors.SkipTypedWrapping | RlpBehaviors.InMempoolForm).Bytes);

            string? result = await nodeManager.Post<string>("eth_sendRawTransaction", "0x" + txRlp);
        }


    }
}
