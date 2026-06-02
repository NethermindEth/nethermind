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

public class FundsDistributor(IJsonRpcClient rpcClient, ulong chainId, string? keyFilePath, ILogManager logManager)
{
    private static readonly TxDecoder TxDecoderInstance = TxDecoder.Instance;

    private readonly IJsonRpcClient _rpcClient = rpcClient ?? throw new ArgumentNullException(nameof(rpcClient));
    private readonly ulong _chainId = chainId;
    private readonly string? _keyFilePath = keyFilePath;
    private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));

    public async Task<IEnumerable<string>> DistributeFunds(Signer distributeFrom, uint keysToMake, UInt256 maxFee, UInt256 maxPriorityFee)
    {
        if (keysToMake == 0)
            throw new ArgumentException("keysToMake must be greater than zero.", nameof(keysToMake));

        using PendingKeyFile? pending = string.IsNullOrWhiteSpace(_keyFilePath)
            ? null
            : PendingKeyFile.Open(_keyFilePath);

        DistributionPlan plan = await BuildDistributionPlan(distributeFrom.Address, keysToMake, maxFee, maxPriorityFee);
        return await ExecuteDistribution(distributeFrom, plan, keysToMake, maxFee, pending);
    }

    private async Task<DistributionPlan> BuildDistributionPlan(Address from, uint keysToMake, UInt256 maxFee, UInt256 maxPriorityFee)
    {
        string balanceString = await _rpcClient.GetBalanceAsync(from);
        ulong nonce = await _rpcClient.GetTransactionCountAsync(from);
        UInt256 gasPrice = await _rpcClient.GetGasPriceAsync();

        UInt256 maxPriorityFeePerGas = maxPriorityFee == 0
            ? await _rpcClient.GetMaxPriorityFeePerGasAsync()
            : maxPriorityFee;

        UInt256 balance = new(Bytes.FromHexString(balanceString));
        if (balance == 0)
            throw new AccountException($"Balance on provided signer {from} is 0.");

        UInt256 maxFeePerGas = maxFee != 0 ? maxFee : gasPrice;
        UInt256 approxGasFee = maxFeePerGas * GasCostOf.Transaction;

        // Leave 10% of the balance as buffer in case of gas spikes.
        UInt256 balanceMinusBuffer = (balance * 900) / 1000;
        UInt256 totalFee = approxGasFee * keysToMake;

        if (balanceMinusBuffer <= totalFee)
            throw new AccountException($"Not enough balance on {from} to distribute to {keysToMake} addresses");

        UInt256 balanceToDistribute = balanceMinusBuffer - totalFee;
        UInt256 perKeyAmount = balanceToDistribute / keysToMake;
        return new DistributionPlan(nonce, maxPriorityFeePerGas, perKeyAmount);
    }

    private async Task<IEnumerable<string>> ExecuteDistribution(Signer distributeFrom, DistributionPlan plan, uint keysToMake, UInt256 maxFee, PendingKeyFile? pending)
    {
        List<string> txHashes = new((int)keysToMake);

        using PrivateKeyGenerator generator = new();

        ulong nonce = plan.Nonce;
        UInt256 priorityFee = plan.MaxPriorityFee;
        for (uint i = 0; i < keysToMake; i++)
        {
            PrivateKey key = generator.Generate();
            pending?.AppendDurable(key);

            UInt256 currentMaxFee = maxFee;
            if (maxFee == 0)
            {
                currentMaxFee = await _rpcClient.GetGasPriceAsync();
                priorityFee = await _rpcClient.GetMaxPriorityFeePerGasAsync();
            }

            Transaction tx = CreateTx(_chainId, key.Address, currentMaxFee, nonce, priorityFee, plan.PerKeyAmount);
            txHashes.Add(await SignAndSendAsync(distributeFrom, tx));
            nonce++;
        }

        pending?.CommitAtomic();
        return txHashes;
    }

    public async Task<IEnumerable<string>> ReclaimFunds(Address beneficiary, UInt256 maxFee, UInt256 maxPriorityFee)
    {
        IEnumerable<Signer> privateSigners = _keyFilePath is null
            ? []
            : File.ReadAllLines(_keyFilePath).Select(k => new Signer(_chainId, new PrivateKey(k), _logManager));

        ILogger log = _logManager.GetClassLogger<FundsDistributor>();
        List<string> txHashes = [];
        foreach (Signer signer in privateSigners)
        {
            string balanceString = await _rpcClient.GetBalanceAsync(signer.Address);
            ulong nonce = await _rpcClient.GetTransactionCountAsync(signer.Address);

            UInt256 balance = new(Bytes.FromHexString(balanceString));

            ulong nonceValue = nonce;

            UInt256 gasPrice = await _rpcClient.GetGasPriceAsync();

            UInt256 maxPriorityFeePerGas = maxPriorityFee;
            if (maxPriorityFee == 0)
            {
                maxPriorityFeePerGas = await _rpcClient.GetMaxPriorityFeePerGasAsync();
            }

            UInt256 maxFeePerGas = maxFee != 0 ? maxFee : gasPrice;
            UInt256 approxGasFee = maxFeePerGas * GasCostOf.Transaction;

            if (balance < approxGasFee)
            {
                log.Info($"Not enough funds on {signer.Address} to pay for gas");
                continue;
            }

            UInt256 toSend = balance - approxGasFee;

            Transaction tx = CreateTx(_chainId,
                                      beneficiary,
                                      maxFee != 0 ? maxFee : gasPrice,
                                      nonceValue,
                                      maxPriorityFeePerGas,
                                      toSend);
            txHashes.Add(await SignAndSendAsync(signer, tx));
        }
        return txHashes;
    }

    private async Task<string> SignAndSendAsync(Signer signer, Transaction tx)
    {
        if (!signer.TrySign(tx))
            throw new InvalidOperationException($"Signer {signer.Address} could not sign transaction.");

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

    private readonly record struct DistributionPlan(ulong Nonce, UInt256 MaxPriorityFee, UInt256 PerKeyAmount);
}
