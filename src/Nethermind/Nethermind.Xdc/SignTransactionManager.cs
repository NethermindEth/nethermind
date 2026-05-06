// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Threading.Tasks;

namespace Nethermind.Xdc;

internal class SignTransactionManager : ISignTransactionManager, IDisposable
{
    private readonly ISigner _signer;
    private readonly ITxPool _txPool;
    private readonly ILogger _logger;
    private readonly IBlockTree _blockTree;
    private readonly ISnapshotManager _snapshotManager;
    private readonly ISpecProvider _specProvider;
    private AssociativeKeyCache<ValueHash256> _alreadySigned = new (128);
    private bool disposedValue;

    public SignTransactionManager(ISigner signer, ITxPool txPool, IBlockTree blockTree, ISnapshotManager snapshotManager, ISpecProvider specProvider, ILogger logger)
    {
        _signer = signer;
        _txPool = txPool;
        _logger = logger;
        _blockTree = blockTree;
        _snapshotManager = snapshotManager;
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
        
        ulong round = xdcHeader.ExtraConsensusData.BlockRound;
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(xdcHeader, round);
        if (spec == null)
            return;

        if (xdcHeader.Number % spec.MergeSignRange == 0)
            return;

        Snapshot snapshot = _snapshotManager.GetSnapshotByBlockNumber(xdcHeader.Number, spec);
        if (snapshot is null)
            return;

        if (IsMasternode(snapshot, _signer.Address))
        {
            _alreadySigned.Set(xdcHeader.Hash);
            _ = SubmitTransactionSign(xdcHeader, spec);
        }
    }

    private static bool IsMasternode(Snapshot snapshot, Address signerAddress) =>
        snapshot.NextEpochCandidates.AsSpan().IndexOf(signerAddress) != -1;

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

    public void Dispose()
    {
        _blockTree.BlockAddedToMain -= OnBlockAddedToMain;
        GC.SuppressFinalize(this);
    }
}
