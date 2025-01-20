// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Facade.Eth;
using Nethermind.Optimism.Rpc;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Optimism.CL;

public class PayloadAttributesDeriver : IPayloadAttributesDeriver
{
    public readonly Address SequencerFeeVault = new Address("0x4200000000000000000000000000000000000011");

    private readonly ulong _chainId;

    public PayloadAttributesDeriver(ulong chainId)
    {
        _chainId = chainId;
    }

    public OptimismPayloadAttributes[] DerivePayloadAttributes(BatchV1 batch, BeaconBlock l1BeaconOrigin, BlockForRpc l1Origin, SystemConfig config)
    {
        // TODO: finish
        OptimismPayloadAttributes[] payloadAttributes = new OptimismPayloadAttributes[batch.BlockCount];
        int currentTx = 0;
        for (int i = 0; i < (int)batch.BlockCount; i++)
        {
            int txCount = (int)batch.BlockTxCounts[i];
            byte[][] txs = new byte[txCount + 1][];
            txs[0] = TxDecoder.Instance.Encode(DeriveSystemTransaction(config)).Bytes;
            for (int j = 0; j < txCount; ++j)
            {
                Transaction tx = new()
                {
                    To = batch.Txs.Tos[currentTx + j],
                    Data = batch.Txs.Datas[currentTx + j],
                    GasLimit = (long)batch.Txs.Gases[currentTx + j],
                    ChainId = _chainId,
                    Nonce = batch.Txs.Nonces[currentTx + j],
                    Signature = batch.Txs.Signatures[currentTx + j],
                };
                txs[j + 1] = TxDecoder.Instance.Encode(tx).Bytes;
            }

            payloadAttributes[i] = new()
            {
                GasLimit = (long)config.GasLimit,
                NoTxPool = true,
                ParentBeaconBlockRoot = l1Origin.ParentBeaconBlockRoot,
                PrevRandao = l1BeaconOrigin.PrevRandao,
                SuggestedFeeRecipient = SequencerFeeVault,
                Timestamp = batch.RelTimestamp,
                Transactions = txs,
                Withdrawals = Array.Empty<Withdrawal>(),
            };

            currentTx += txCount;
        }

        return payloadAttributes;
    }

    private Transaction DeriveSystemTransaction(SystemConfig systemConfig)
    {
        throw new NotImplementedException();
    }
}
