// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc;

internal class SignTransactionManager(
    Lazy<ISigner> signer,
    Lazy<ITxPool> txPool,
    IBlockTree blockTree,
    ISnapshotManager snapshotManager,
    ISpecProvider specProvider,
    ITimestamper timestamper,
    ILogManager logManager) : ISignTransactionManager, IStartable, IDisposable
{
    // Lazy: ISigner and ITxPool are registered during InitializeBlockchain, after this class is instantiated.
    private readonly Lazy<ISigner> _signer = signer;
    private readonly Lazy<ITxPool> _txPool = txPool;
    private readonly IBlockTree _blockTree = blockTree;
    private readonly ISnapshotManager _snapshotManager = snapshotManager;
    private readonly ISpecProvider _specProvider = specProvider;
    private readonly ITimestamper _timestamper = timestamper;
    private readonly ILogger _logger = logManager.GetClassLogger<SignTransactionManager>();
    private readonly AssociativeKeyCache<ValueHash256> _alreadySigned = new(128);

    public void Start() => _blockTree.BlockAddedToMain += OnBlockAddedToMain;

    public Task SubmitTransactionSign(XdcBlockHeader header, IXdcReleaseSpec spec)
    {
        ulong nonce = _txPool.Value.GetLatestPendingNonce(_signer.Value.Address);
        Transaction transaction = CreateTxSign((UInt256)header.Number, header.Hash ?? header.CalculateHash().ToHash256(), nonce, spec.BlockSignerContract, _signer.Value.Address);

        if (!_signer.Value.TrySign(transaction))
        {
            if (_logger.IsWarn) _logger.Warn($"XDC signer {_signer.Value.Address} could not sign block-sign tx for header {header.Number} — skipping submission.");
            return Task.CompletedTask;
        }

        transaction.Hash = transaction.CalculateHash();

        AcceptTxResult added = _txPool.Value.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast);
        if (!added)
        {
            _logger.Warn($"Failed to add signed transaction to the pool: {added} {header.ToString(BlockHeader.Format.FullHashAndNumber)}");
        }
        return Task.CompletedTask;
    }

    private void OnBlockAddedToMain(object? sender, BlockReplacementEventArgs e)
    {
        if (e.Block.Header is not XdcBlockHeader xdcHeader)
            return;

        if (e.Block.Hash is null || !_blockTree.WasProcessed(e.Block.Number, e.Block.Hash) || _blockTree.IsSyncing().isSyncing)
            return;

        if (_alreadySigned.Contains(xdcHeader.Hash))
            return;

        ulong round = xdcHeader.ExtraConsensusData!.BlockRound;
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(xdcHeader, round);
        if (spec is null)
            return;

        // Sign only recent head blocks; older ones are replayed during catch-up.
        long window = spec.MergeSignRange * spec.MinePeriod * XdcConstants.MaxSignableBlockPeriods;
        if ((long)xdcHeader.Timestamp + window < _timestamper.UnixTime.SecondsLong)
            return;

        if (xdcHeader.Number % spec.MergeSignRange != 0)
            return;

        Snapshot snapshot = _snapshotManager.GetSnapshotByBlockNumber(xdcHeader.Number, spec);
        if (snapshot is null)
            return;

        if (IsMasternode(snapshot, _signer.Value.Address))
        {
            _alreadySigned.Set(xdcHeader.Hash);
            _ = SubmitTransactionSign(xdcHeader, spec)
                .ContinueWith(t => _logger.Error("Failed to submit sign transaction", t.Exception),
                TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private static bool IsMasternode(Snapshot snapshot, Address signerAddress) =>
        snapshot.NextEpochCandidates.AsSpan().IndexOf(signerAddress) != -1;

    internal static Transaction CreateTxSign(UInt256 number, Hash256 hash, ulong nonce, Address blockSignersAddress, Address sender)
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

    public void Dispose() => _blockTree.BlockAddedToMain -= OnBlockAddedToMain;
}
