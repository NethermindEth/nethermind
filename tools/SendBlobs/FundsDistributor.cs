// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace SendBlobs;

internal class FundsDistributor
{
    private static readonly TxDecoder TxDecoderInstance = TxDecoder.Instance;

    private readonly IJsonRpcClient _rpcClient;
    private readonly ulong _chainId;
    private readonly string? _keyFilePath;
    private readonly ILogManager _logManager;

    public FundsDistributor(IJsonRpcClient rpcClient, ulong chainId, string? keyFilePath, ILogManager logManager)
    {
        _rpcClient = rpcClient ?? throw new ArgumentNullException(nameof(rpcClient));
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
    public async Task<IEnumerable<string>> DistributeFunds(Signer distributeFrom, uint keysToMake, UInt256 maxFee, UInt256 maxPriorityFee)
    {
        if (keysToMake == 0)
            throw new ArgumentException("keysToMake must be greater than zero.", nameof(keysToMake));

        string balanceString = await _rpcClient.GetBalanceAsync(distributeFrom.Address);
        ulong nonce = await _rpcClient.GetTransactionCountAsync(distributeFrom.Address);
        UInt256 gasPrice = await _rpcClient.GetGasPriceAsync();

        UInt256 maxPriorityFeePerGas = maxPriorityFee;
        if (maxPriorityFee == 0)
        {
            maxPriorityFeePerGas = await _rpcClient.GetMaxPriorityFeePerGasAsync();
        }

        UInt256 balance = new UInt256(Bytes.FromHexString(balanceString));

        if (balance == 0)
            throw new AccountException($"Balance on provided signer {distributeFrom.Address} is 0.");

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

        List<string> txHash = [];

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
                    gasPrice = await _rpcClient.GetGasPriceAsync();
                    maxPriorityFeePerGas = await _rpcClient.GetMaxPriorityFeePerGasAsync();
                }

                Transaction tx = CreateTx(_chainId,
                                          key.Address,
                                          maxFee != 0 ? maxFee : gasPrice + maxPriorityFeePerGas,
                                          nonce,
                                          maxPriorityFeePerGas,
                                          perKeyToSend);

                string? result = await SignAndSendAsync(distributeFrom, tx);
                if (result is not null)
                    txHash.Add(result);

                if (keyWriter is not null)
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
        IEnumerable<Signer> privateSigners = _keyFilePath is null
            ? []
            : File.ReadAllLines(_keyFilePath).Select(k => new Signer(_chainId, new PrivateKey(k), _logManager));

        ILogger log = _logManager.GetClassLogger();
        List<string> txHashes = [];
        foreach (var signer in privateSigners)
        {
            string balanceString = await _rpcClient.GetBalanceAsync(signer.Address);
            ulong nonce = await _rpcClient.GetTransactionCountAsync(signer.Address);

            UInt256 balance = new UInt256(Bytes.FromHexString(balanceString));

            ulong nonceValue = nonce;

            UInt256 gasPrice = await _rpcClient.GetGasPriceAsync();

            UInt256 maxPriorityFeePerGas = maxPriorityFee;
            if (maxPriorityFee == 0)
            {
                maxPriorityFeePerGas = await _rpcClient.GetMaxPriorityFeePerGasAsync();
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
                                      nonceValue,
                                      maxPriorityFeePerGas,
                                      toSend);
            string? result = await SignAndSendAsync(signer, tx);

            if (result is not null)
            {
                txHashes.Add(result);
            }
        }
        return txHashes;
    }

    private async Task<string?> SignAndSendAsync(Signer signer, Transaction tx)
    {
        await signer.Sign(tx);

        string txRlp = Convert.ToHexStringLower(TxDecoderInstance
            .Encode(tx, RlpBehaviors.SkipTypedWrapping | RlpBehaviors.InMempoolForm).Bytes);

        return await _rpcClient.SendRawTransactionAsync($"0x{txRlp}");
    }

    private static Transaction CreateTx(ulong chainId, Address beneficiary, UInt256 maxFee, ulong nonce, UInt256 maxPriorityFeePerGas, UInt256 toSend) => new()
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
