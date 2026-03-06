// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.TxPool;
using Nethermind.Xdc.Spec;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using System.Threading.Tasks;
using Nethermind.Xdc.Types;
using System;
using Nethermind.Blockchain;
using Lantern.Discv5.WireProtocol.Session;
using Nethermind.Core.Caching;

namespace Nethermind.Xdc;

internal class SignTransactionManager : ISignTransactionManager
{
    private readonly ISigner _signer;
    private readonly ITxPool _txPool;
    private readonly ILogger _logger;
    private readonly IBlockTree _blockTree;
    private readonly IEpochSwitchManager _epochSwitchManager;
    private readonly ISpecProvider _specProvider;
    private ClockKeyCache<ValueHash256> _alreadySigned = new (128);

    public SignTransactionManager(ISigner signer, ITxPool txPool, ILogger logger, IBlockTree blockTree, IEpochSwitchManager epochSwitchManager, ISpecProvider specProvider)
    {
        _signer = signer;
        _txPool = txPool;
        _logger = logger;
        _blockTree = blockTree;
        _epochSwitchManager = epochSwitchManager;
        _specProvider = specProvider;
        _blockTree.BlockAddedToMain += OnBlockAddedToMain;
    }

    public async Task SubmitTransactionSign(XdcBlockHeader header, IXdcReleaseSpec spec)
    {
        UInt256 nonce = _txPool.GetLatestPendingNonce(_signer.Address);
        Transaction transaction = CreateTxSign((UInt256)header.Number, header.Hash ?? header.CalculateHash().ToHash256(), nonce, spec.BlockSignerContract, _signer.Address);

        await _signer.Sign(transaction);

        AcceptTxResult added = _txPool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast);
        if (!added)
        {
            _logger.Warn($"Failed to add signed transaction to the pool: {added} {header.ToString(BlockHeader.Format.FullHashAndNumber)}");
        }
    }

    private void OnBlockAddedToMain(object? sender, BlockReplacementEventArgs e)
    {
        if (e.Block.Header is not XdcBlockHeader xdcHeader)
            return;

        if (e.Block.Hash is null || !_blockTree.WasProcessed(e.Block.Number, e.Block.Hash) || _blockTree.IsSyncing().isSyncing)
            return;

        if (_alreadySigned.Contains(xdcHeader.Hash))
            return;
        
        EpochSwitchInfo? epochInfo = _epochSwitchManager.GetEpochSwitchInfo(xdcHeader);
        if (epochInfo?.Masternodes == null || epochInfo.Masternodes.Length == 0)
            return;

        ulong round = xdcHeader.ExtraConsensusData.BlockRound;
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(xdcHeader, round);
        if (spec == null)
            return;

        if (IsMasternode(epochInfo, _signer.Address)
            && (xdcHeader.Number % spec.MergeSignRange == 0))
        {
            _alreadySigned.Set(xdcHeader.Hash);
            _ = SubmitTransactionSign(xdcHeader, spec);
        }
    }

    private static bool IsMasternode(EpochSwitchInfo epochInfo, Address node) =>
        epochInfo.Masternodes.AsSpan().IndexOf(node) != -1;

    internal static Transaction CreateTxSign(UInt256 number, Hash256 hash, UInt256 nonce, Address blockSignersAddress, Address sender)
    {
        byte[] inputData = [.. XdcConstants.SignMethod, .. number.PaddedBytes(32), .. hash.Bytes.PadLeft(32)];

        Transaction transaction = new();
        transaction.Nonce = nonce;
        transaction.To = blockSignersAddress;
        transaction.Value = 0;
        transaction.GasLimit = 200_000;
        transaction.GasPrice = 0;
        transaction.Data = inputData;
        transaction.SenderAddress = sender;

        transaction.Type = TxType.Legacy;

        return transaction;
    }
}
