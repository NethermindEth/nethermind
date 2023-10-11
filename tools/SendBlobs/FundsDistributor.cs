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
    private readonly INodeManager _nodeManager;
    private readonly ulong _chainId;
    private readonly string? _keyFilePath;
    private readonly ILogManager _logManager;

    public FundsDistributor(INodeManager nodeManager, ulong chainId, string? keyFilePath, ILogManager logManager)
    {
        _nodeManager = nodeManager ?? throw new ArgumentNullException(nameof(nodeManager));
        _chainId = chainId;
        _keyFilePath = keyFilePath;
        _logManager = logManager;
    }

    /// <summary>
    /// Distribute the available funds from an address to a number of newly generated private keys.
    /// </summary>
    /// <param name="distributeFrom"></param>
    /// <param name="keysToMake"></param>
    /// <param name="maxFee"></param>
    /// <param name="maxPriorityFee"></param>
    /// <returns><see cref="IEnumerable{string}"/> containing all the executed tx hashes.</returns>
    /// <exception cref="AccountException"></exception>
    public async Task<IEnumerable<string>> DitributeFunds(Signer distributeFrom, uint keysToMake, UInt256 maxFee, UInt256 maxPriorityFee)
    {
        string? balanceString = await _nodeManager.Post("eth_getBalance", distributeFrom.Address, "latest");
        if (balanceString is null)
            throw new AccountException($"Unable to get balance for {distributeFrom.Address}");
        string? nonceString = await _nodeManager.Post<string>("eth_getTransactionCount", distributeFrom.Address, "latest");
        if (nonceString is null)
            throw new AccountException($"Unable to get nonce for {distributeFrom.Address}");

        string? gasPriceRes = await _nodeManager.Post<string>("eth_gasPrice") ?? "1";
        UInt256 gasPrice = HexConvert.ToUInt256(gasPriceRes);

        string? maxPriorityFeePerGasRes;
        UInt256 maxPriorityFeePerGas = maxPriorityFee;
        if (maxPriorityFee == 0)
        {
            maxPriorityFeePerGasRes = await _nodeManager.Post<string>("eth_maxPriorityFeePerGas") ?? "1";
            maxPriorityFeePerGas = HexConvert.ToUInt256(maxPriorityFeePerGasRes);
        }

        UInt256 balance = new UInt256(Bytes.FromHexString(balanceString));

        if (balance == 0)
            throw new AccountException($"Balance on provided signer {distributeFrom.Address} is 0.");

        ulong nonce = HexConvert.ToUInt64(nonceString);

        UInt256 approxGasFee = (gasPrice + maxPriorityFeePerGas) * GasCostOf.Transaction;

        //Leave 10% of the balance as buffer in case of gas spikes
        UInt256 balanceMinusBuffer = (balance * 900) / 1000;
        UInt256 totalFee = approxGasFee * keysToMake;

        if (balanceMinusBuffer <= totalFee)
            throw new AccountException($"Not enough balance on {distributeFrom.Address} to distribute to {keysToMake} addresses");

        UInt256 balanceToDistribute = balanceMinusBuffer - totalFee;

        UInt256 perKeyToSend = balanceToDistribute / keysToMake;

        using PrivateKeyGenerator generator = new();
        IEnumerable<PrivateKey> privateKeys = Enumerable.Range(1, (int)keysToMake).Select(i => generator.Generate());

        List<string> txHash = new List<string>();

        TxDecoder txDecoder = new();
        StreamWriter? keyWriter = null;

        if (!string.IsNullOrWhiteSpace(_keyFilePath))
        {
            if (File.Exists(_keyFilePath))
                File.Delete(_keyFilePath);
            keyWriter = File.AppendText(_keyFilePath);
        }

        using (keyWriter)
        {
            foreach (PrivateKey key in privateKeys)
            {
                if (maxFee == 0)
                {
                    gasPriceRes = await _nodeManager.Post<string>("eth_gasPrice") ?? "1";
                    gasPrice = HexConvert.ToUInt256(gasPriceRes);

                    maxPriorityFeePerGasRes = await _nodeManager.Post<string>("eth_maxPriorityFeePerGas") ?? "1";
                    maxPriorityFeePerGas = HexConvert.ToUInt256(maxPriorityFeePerGasRes);
                }

                Transaction tx = CreateTx(_chainId,
                                          key.Address,
                                          maxFee != 0 ? maxFee : gasPrice + maxPriorityFeePerGas,
                                          nonce,
                                          maxPriorityFeePerGas,
                                          perKeyToSend);

                await distributeFrom.Sign(tx);

                string txRlp = Hex.ToHexString(txDecoder
                    .Encode(tx, RlpBehaviors.SkipTypedWrapping | RlpBehaviors.InMempoolForm).Bytes);

                string? result = await _nodeManager.Post<string>("eth_sendRawTransaction", "0x" + txRlp);
                if (result != null)
                    txHash.Add(result);

                if (keyWriter != null)
                    keyWriter.WriteLine(key.ToString());

                nonce++;
            }
        }

        return txHash;
    }

    /// <summary>
    /// Send all available funds from a list of private keys contained in <paramref name="keyFilePath"/> to <paramref name="beneficiary"/>.
    /// </summary>
    /// <param name="beneficiary"></param>
    /// <param name="maxFee"></param>
    /// <param name="maxPriorityFee"></param>
    /// <returns><see cref="IEnumerable{string}"/> containing all the executed tx hashes.</returns>
    public async Task<IEnumerable<string>> ReclaimFunds(Address beneficiary, UInt256 maxFee, UInt256 maxPriorityFee)
    {
        IEnumerable<Signer> privateSigners =
            File.ReadAllLines(_keyFilePath)
            .Select(k => new Signer(_chainId, new PrivateKey(k), _logManager));

        ILogger log = _logManager.GetClassLogger();
        List<string> txHashes = new List<string>();
        TxDecoder txDecoder = new();

        foreach (var signer in privateSigners)
        {
            string? balanceString = await _nodeManager.Post("eth_getBalance", signer.Address, "latest");
            if (balanceString is null)
                continue;
            string? nonceString = await _nodeManager.Post<string>("eth_getTransactionCount", signer.Address, "latest");
            if (nonceString is null)
                continue;

            UInt256 balance = new UInt256(Bytes.FromHexString(balanceString));

            ulong nonce = HexConvert.ToUInt64(nonceString);

            string? gasPriceRes = await _nodeManager.Post<string>("eth_gasPrice") ?? "1";
            UInt256 gasPrice = HexConvert.ToUInt256(gasPriceRes);

            UInt256 maxPriorityFeePerGas = maxPriorityFee;
            if (maxPriorityFee == 0)
            {
                string? maxPriorityFeePerGasRes = await _nodeManager.Post<string>("eth_maxPriorityFeePerGas") ?? "1";
                maxPriorityFeePerGas = HexConvert.ToUInt256(maxPriorityFeePerGasRes);
            }

            UInt256 approxGasFee = (gasPrice + maxPriorityFeePerGas) * GasCostOf.Transaction;

            if (balance < approxGasFee)
            {
                log.Info($"Not enough funds on {signer.Address} to pay for gas");
                continue;
            }

            UInt256 toSend = balance - approxGasFee;

            Transaction tx = CreateTx(_chainId,
                                      beneficiary,
                                      maxFee != 0 ? maxFee : gasPrice + maxPriorityFeePerGas,
                                      nonce,
                                      maxPriorityFeePerGas,
                                      toSend);
            await signer.Sign(tx);

            string txRlp = Hex.ToHexString(txDecoder
                .Encode(tx, RlpBehaviors.SkipTypedWrapping | RlpBehaviors.InMempoolForm).Bytes);

            string? result = await _nodeManager.Post<string>("eth_sendRawTransaction", "0x" + txRlp);
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
