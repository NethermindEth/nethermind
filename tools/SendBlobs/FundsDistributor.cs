// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
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
    public async Task<IEnumerable<string>> DistributeFunds(Signer distributeFrom, uint keysToMake, UInt256 maxFee, UInt256 maxPriorityFee)
    {
        if (keysToMake == 0)
            throw new ArgumentException("keysToMake must be greater than zero.", nameof(keysToMake));

        string? balanceString = await _rpcClient.Post<string>("eth_getBalance", distributeFrom.Address, "latest");
        if (balanceString is null)
            throw new AccountException($"Unable to get balance for {distributeFrom.Address}");
        string? nonceString = await _rpcClient.Post<string>("eth_getTransactionCount", distributeFrom.Address, "latest");
        if (nonceString is null)
            throw new AccountException($"Unable to get nonce for {distributeFrom.Address}");

        UInt256 balance = new UInt256(Bytes.FromHexString(balanceString));

        if (balance == 0)
            throw new AccountException($"Balance on provided signer {distributeFrom.Address} is 0.");

        ulong nonce = HexConvert.ToUInt64(nonceString);

        (UInt256 feePerGasEstimate, UInt256 priorityFeeEstimate) = await ResolveFeeSettingsAsync(maxFee, maxPriorityFee);

        UInt256 approxGasFee = feePerGasEstimate * GasCostOf.Transaction;

        //Leave 10% of the balance as buffer in case of gas spikes
        UInt256 balanceMinusBuffer = (balance * 900) / 1000;
        UInt256 totalFee = approxGasFee * keysToMake;

        if (balanceMinusBuffer <= totalFee)
            throw new AccountException($"Not enough balance on {distributeFrom.Address} to distribute to {keysToMake} addresses");

        UInt256 balanceToDistribute = balanceMinusBuffer - totalFee;

        UInt256 perKeyToSend = balanceToDistribute / keysToMake;

        using PrivateKeyGenerator generator = new();
        List<string> txHashes = new();

        TxDecoder txDecoder = TxDecoder.Instance;
        StreamWriter? keyWriter = null;

        if (!string.IsNullOrWhiteSpace(_keyFilePath))
        {
            if (File.Exists(_keyFilePath))
                File.Delete(_keyFilePath);
            keyWriter = File.AppendText(_keyFilePath);
        }

        bool refreshFeesPerTransaction = maxFee == 0 || maxPriorityFee == 0;

        using (keyWriter)
        {
            for (uint i = 0; i < keysToMake; i++)
            {
                PrivateKey key = generator.Generate();

                (UInt256 txMaxFeePerGas, UInt256 txPriorityFeePerGas) = refreshFeesPerTransaction
                    ? await ResolveFeeSettingsAsync(maxFee, maxPriorityFee)
                    : (feePerGasEstimate, priorityFeeEstimate);

                Transaction tx = CreateTx(
                    _chainId,
                    key.Address,
                    txMaxFeePerGas,
                    nonce,
                    txPriorityFeePerGas,
                    perKeyToSend);

                await distributeFrom.Sign(tx);

                string txRlp = Convert.ToHexStringLower(txDecoder
                    .Encode(tx, RlpBehaviors.SkipTypedWrapping | RlpBehaviors.InMempoolForm).Bytes);

                string? result = await _rpcClient.Post<string>("eth_sendRawTransaction", $"0x{txRlp}");
                if (result is not null)
                    txHashes.Add(result);

                if (keyWriter is not null)
                    keyWriter.WriteLine(key.ToString());

                nonce++;
            }
        }

        return txHashes;
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
            : File.ReadAllLines(_keyFilePath)
                .Select(static k => k.Trim())
                .Where(static k => !string.IsNullOrWhiteSpace(k))
                .Select(k => new Signer(_chainId, new PrivateKey(k), _logManager));

        ILogger log = _logManager.GetClassLogger();
        List<string> txHashes = new List<string>();
        TxDecoder txDecoder = TxDecoder.Instance;

        foreach (var signer in privateSigners)
        {
            string? balanceString = await _rpcClient.Post<string>("eth_getBalance", signer.Address, "latest");
            if (balanceString is null)
                continue;
            string? nonceString = await _rpcClient.Post<string>("eth_getTransactionCount", signer.Address, "latest");
            if (nonceString is null)
                continue;

            UInt256 balance = new UInt256(Bytes.FromHexString(balanceString));

            ulong nonce = HexConvert.ToUInt64(nonceString);

            (UInt256 resolvedMaxFeePerGas, UInt256 resolvedPriorityFeePerGas) = await ResolveFeeSettingsAsync(maxFee, maxPriorityFee);

            UInt256 approxGasFee = resolvedMaxFeePerGas * GasCostOf.Transaction;

            if (balance < approxGasFee)
            {
                log.Info($"Not enough funds on {signer.Address} to pay for gas");
                continue;
            }

            UInt256 toSend = balance - approxGasFee;

            Transaction tx = CreateTx(
                _chainId,
                beneficiary,
                resolvedMaxFeePerGas,
                nonce,
                resolvedPriorityFeePerGas,
                toSend);
            await signer.Sign(tx);

            string txRlp = Convert.ToHexStringLower(txDecoder
                .Encode(tx, RlpBehaviors.SkipTypedWrapping | RlpBehaviors.InMempoolForm).Bytes);

            string? result = await _rpcClient.Post<string>("eth_sendRawTransaction", $"0x{txRlp}");
            if (result is not null)
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

    private async Task<(UInt256 maxFeePerGas, UInt256 maxPriorityFeePerGas)> ResolveFeeSettingsAsync(UInt256 maxFeeOverride, UInt256 maxPriorityFeeOverride)
    {
        UInt256 resolvedPriority = maxPriorityFeeOverride;
        if (resolvedPriority == 0)
        {
            string? rpcResult = await _rpcClient.Post<string>("eth_maxPriorityFeePerGas") ?? "0x1";
            resolvedPriority = HexConvert.ToUInt256(rpcResult);
        }

        UInt256 resolvedMaxFee = maxFeeOverride;
        if (resolvedMaxFee == 0)
        {
            string? rpcResult = await _rpcClient.Post<string>("eth_gasPrice") ?? "0x1";
            resolvedMaxFee = HexConvert.ToUInt256(rpcResult);
        }

        return (resolvedMaxFee, resolvedPriority);
    }
}
