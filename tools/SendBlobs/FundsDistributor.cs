// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Nethermind.Cli;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Org.BouncyCastle.Utilities.Encoders;

namespace SendBlobs;
internal class FundsDistributor
{
    /// <summary>
    /// Distribute the available funds from an address to a number of newly generated private keys.
    /// </summary>
    /// <param name="nodeManager"></param>
    /// <param name="chainId"></param>
    /// <param name="distributeFrom"></param>
    /// <param name="keysToMake"></param>
    /// <param name="keyFilePath">Path to a file </param>
    /// <returns><see cref="IEnumerable{string}"/> containing all the executed tx hashes.</returns>
    /// <exception cref="Exception"></exception>
    public async static Task<IEnumerable<string>> DitributeFunds(INodeManager nodeManager, ulong chainId, Signer distributeFrom, uint keysToMake, string keyFilePath, UInt256 maxFee, UInt256 maxPriorityFee)
    {
        string? balanceString = await nodeManager.Post("eth_getBalance", distributeFrom.Address, "latest");
        if (balanceString is null)
            throw new Exception($"Unable to get balance for {distributeFrom.Address}");
        string? nonceString = await nodeManager.Post<string>("eth_getTransactionCount", distributeFrom.Address, "latest");
        if (nonceString is null)
            throw new Exception($"Unable to get nonce for {distributeFrom.Address}");

        string? gasPriceRes = await nodeManager.Post<string>("eth_gasPrice") ?? "1";
        UInt256 gasPrice = (UInt256)Convert.ToUInt64(gasPriceRes, gasPriceRes.StartsWith("0x") ? 16 : 10);

        string? maxPriorityFeePerGasRes;
        UInt256 maxPriorityFeePerGas = maxPriorityFee;
        if (maxPriorityFee == 0)
        {
            maxPriorityFeePerGasRes = await nodeManager.Post<string>("eth_maxPriorityFeePerGas") ?? "1";
            maxPriorityFeePerGas = (UInt256)Convert.ToUInt64(maxPriorityFeePerGasRes, maxPriorityFeePerGasRes.StartsWith("0x") ? 16 : 10);
        }

        UInt256 balance = new UInt256(Bytes.FromHexString(balanceString));

        if (balance == 0)
            throw new Exception($"Balance on provided signer {distributeFrom.Address} is 0.");

        ulong nonce = Convert.ToUInt64(nonceString, nonceString.StartsWith("0x") ? 16 : 10);

        UInt256 approxGasFee = (gasPrice + maxPriorityFeePerGas) * GasCostOf.Transaction;

        //Leave 10% of the balance as buffer in case of gas spikes
        UInt256 balanceMinusBuffer = (balance * 900) / 1000;
        UInt256 totalFee = approxGasFee * keysToMake;

        if (balanceMinusBuffer <= totalFee)
            throw new Exception($"Not enough balance on {distributeFrom.Address} to distribute to {keysToMake} addresses");

        UInt256 balanceToDistribute = balanceMinusBuffer - totalFee;

        UInt256 perKeyToSend = balanceToDistribute / keysToMake;

        using PrivateKeyGenerator generator = new();
        IEnumerable<PrivateKey> privateKeys = Enumerable.Range(1, (int)keysToMake).Select(i => generator.Generate()).ToArray();

        List<string> txHash = new List<string>();

        TxDecoder txDecoder = new();

        foreach (PrivateKey key in privateKeys)
        {
            if (maxFee == 0)
            {
                gasPriceRes = await nodeManager.Post<string>("eth_gasPrice") ?? "1";
                gasPrice = (UInt256)Convert.ToUInt64(gasPriceRes, gasPriceRes.StartsWith("0x") ? 16 : 10);

                maxPriorityFeePerGasRes = await nodeManager.Post<string>("eth_maxPriorityFeePerGas") ?? "1";
                maxPriorityFeePerGas = (UInt256)Convert.ToUInt64(maxPriorityFeePerGasRes, maxPriorityFeePerGasRes.StartsWith("0x") ? 16 : 10);
            }

            Transaction tx = CreateTx(chainId,
                                      key.Address,
                                      maxFee != 0 ? maxFee : gasPrice + maxPriorityFeePerGas,
                                      nonce,
                                      maxPriorityFeePerGas,
                                      perKeyToSend);

            await distributeFrom.Sign(tx);

            string txRlp = Hex.ToHexString(txDecoder
                .Encode(tx, RlpBehaviors.SkipTypedWrapping | RlpBehaviors.InMempoolForm).Bytes);

            string? result = await nodeManager.Post<string>("eth_sendRawTransaction", "0x" + txRlp);
            if (result != null)
                txHash.Add(result);
            nonce++;
        }
        if (!string.IsNullOrWhiteSpace(keyFilePath))
            File.WriteAllLines(keyFilePath, privateKeys.Select(k => k.ToString()), Encoding.ASCII);

        return txHash;
    }
    /// <summary>
    /// Send all available funds from a list of private keys contained in <paramref name="keyFilePath"/> to <paramref name="beneficiary"/>.
    /// </summary>
    /// <param name="nodeManager"></param>
    /// <param name="chainId"></param>
    /// <param name="beneficiary"></param>
    /// <param name="keyFilePath"></param>
    /// <param name="logManager"></param>
    /// <returns><see cref="IEnumerable{string}"/> containing all the executed tx hashes.</returns>
    public async static Task<IEnumerable<string>> ReclaimFunds(INodeManager nodeManager, ulong chainId, Address beneficiary, string keyFilePath, ILogManager logManager, UInt256 maxFee, UInt256 maxPriorityFee)
    {
        IEnumerable<Signer> privateSigners =
            File.ReadAllLines(keyFilePath, Encoding.ASCII)
            .Select(k => new Signer(chainId, new PrivateKey(k), logManager));

        ILogger log = logManager.GetClassLogger();
        List<string> txHashes = new List<string>();
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

            UInt256 maxPriorityFeePerGas = maxPriorityFee;
            if (maxPriorityFee == 0)
            {
                string? maxPriorityFeePerGasRes = await nodeManager.Post<string>("eth_maxPriorityFeePerGas") ?? "1";
                maxPriorityFeePerGas = (UInt256)Convert.ToUInt64(maxPriorityFeePerGasRes, maxPriorityFeePerGasRes.StartsWith("0x") ? 16 : 10);
            }

            UInt256 approxGasFee = (gasPrice + maxPriorityFeePerGas) * GasCostOf.Transaction;

            if (balance < approxGasFee)
            {
                log.Info($"Not enough funds on {signer.Address} to pay for gas");
                continue;
            }

            UInt256 toSend = balance - approxGasFee;

            TxDecoder txDecoder = new();
            Transaction tx = CreateTx(chainId,
                                      beneficiary,
                                      maxFee != 0 ? maxFee : gasPrice + maxPriorityFeePerGas,
                                      nonce,
                                      maxPriorityFeePerGas,
                                      toSend);
            await signer.Sign(tx);

            string txRlp = Hex.ToHexString(txDecoder
                .Encode(tx, RlpBehaviors.SkipTypedWrapping | RlpBehaviors.InMempoolForm).Bytes);

            string? result = await nodeManager.Post<string>("eth_sendRawTransaction", "0x" + txRlp);
            if (result != null)
                txHashes.Add(result);
        }
        return txHashes;
    }

    private static Transaction CreateTx(ulong chainId, Address beneficiary, UInt256 maxFee, ulong nonce, UInt256 maxPriorityFeePerGas, UInt256 toSend)
    {
        return new()
        {
            Type = TxType.EIP1559,
            ChainId = chainId,
            Nonce = nonce,
            GasLimit = GasCostOf.Transaction,
            GasPrice = maxPriorityFeePerGas,
            DecodedMaxFeePerGas = maxFee,
            Value = toSend,
            To = beneficiary,
        };
    }
}
