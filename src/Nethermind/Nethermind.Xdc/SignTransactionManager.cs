// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.TxPool;
using Nethermind.Xdc.Spec;
using Nethermind.Logging;
using System.Threading.Tasks;

namespace Nethermind.Xdc;

internal class SignTransactionManager(ISigner signer, ITxPool txPool, ILogger logger) : ISignTransactionManager
{
    public async Task SubmitTransactionSign(XdcBlockHeader header, IXdcReleaseSpec spec)
    {
        UInt256 nonce = txPool.GetLatestPendingNonce(signer.Address);
        Transaction transaction = CreateTxSign((UInt256)header.Number, header.Hash ?? header.CalculateHash().ToHash256(), nonce, spec.BlockSignerContract, signer.Address);

        await signer.Sign(transaction);

        bool added = txPool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast);
        if (!added)
        {
            logger.Info("Failed to add signed transaction to the pool.");
        }
    }

    internal static Transaction CreateTxSign(UInt256 number, Hash256 hash, UInt256 nonce, Address blockSignersAddress, Address sender)
    {
        byte[] inputData = [.. XdcConstants.SignMethod, .. number.PaddedBytes(32), .. hash.Bytes.PadLeft(32)];

        var transaction = new Transaction();
        transaction.Nonce = nonce;
        transaction.To = blockSignersAddress;
        transaction.Value = 0;
        transaction.GasLimit = 200_000;
        transaction.GasPrice = 0;
        transaction.Data = inputData;
        transaction.SenderAddress = sender;

        transaction.Type = TxType.Legacy;

        transaction.Hash = transaction.CalculateHash();

        return transaction;
    }
}
