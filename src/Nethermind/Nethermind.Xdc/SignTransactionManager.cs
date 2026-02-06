// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Threading.Tasks;

namespace Nethermind.Xdc;

internal class SignTransactionManager : ISignTransactionManager
{
    private readonly ISigner _signer;
    private readonly ITxPool _txPool;
    private readonly IEpochSwitchManager _epochSwitchManager;
    private readonly ISpecProvider _specProvider;
    private readonly ILogger _logger;

    public SignTransactionManager(ISigner signer, ITxPool txPool, IEpochSwitchManager epochSwitchManager, ISpecProvider specProvider, ILogger logger)
    {
        _signer = signer;
        _txPool = txPool;
        _epochSwitchManager = epochSwitchManager;
        _specProvider = specProvider;
        _logger = logger;
        _txPool.TxPoolHeadChanged += OnNewHead;
    }

    private void OnNewHead(object sender, Block block)
    {
        if (block.Header is XdcBlockHeader header)
        {
            Task.Run(async () =>
            {
                try
                {
                    if (!IsMasternode(_epochSwitchManager.GetEpochSwitchInfo(header), _signer.Address))
                        return;
                    IXdcReleaseSpec spec = _specProvider.GetXdcSpec(header.Number, header.ExtraConsensusData.BlockRound);
                    if (header.Number % spec.MergeSignRange == 0)
                        return;
                    await SubmitTransactionSign(header, spec);
                }
                catch (Exception ex)
                {
                    _logger.Error("Error while submitting sign transaction on new head block.", ex);
                }
            });
        }
    }

    public async Task SubmitTransactionSign(XdcBlockHeader header, IXdcReleaseSpec spec)
    {
        UInt256 nonce = _txPool.GetLatestPendingNonce(_signer.Address);
        Transaction transaction = CreateTxSign((UInt256)header.Number, header.Hash ?? header.CalculateHash().ToHash256(), nonce, spec.BlockSignerContract, _signer.Address);

        await _signer.Sign(transaction);

        bool added = _txPool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast);
        if (!added)
        {
            _logger.Warn("Failed to add signed transaction to the pool.");
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
    private static bool IsMasternode(EpochSwitchInfo epochInfo, Address address) =>
        epochInfo.Masternodes.AsSpan().IndexOf(address) != -1;
}
