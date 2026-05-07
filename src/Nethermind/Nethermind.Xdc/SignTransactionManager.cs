// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using Microsoft.Extensions.Logging;
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
using System;
using System.Threading.Tasks;

namespace Nethermind.Xdc;

internal class SignTransactionManager(
    ISigner signer,
    ITxPool txPool,
    IBlockTree blockTree,
    ISnapshotManager snapshotManager,
    ISpecProvider specProvider,
    ILogManager logManager) : ISignTransactionManager, IStartable, IDisposable
{
    private readonly AssociativeKeyCache<ValueHash256> _alreadySigned = new(128);
    private Logging.ILogger _logger = logManager.GetClassLogger<SignTransactionManager>();
    public void Start() => blockTree.BlockAddedToMain += OnBlockAddedToMain;

    public async Task SubmitTransactionSign(XdcBlockHeader header, IXdcReleaseSpec spec)
    {
        UInt256 nonce = txPool.GetLatestPendingNonce(signer.Address);
        Transaction transaction = CreateTxSign((UInt256)header.Number, header.Hash ?? header.CalculateHash().ToHash256(), nonce, spec.BlockSignerContract, signer.Address);

        await signer.Sign(transaction);

        transaction.Hash = transaction.CalculateHash();

        AcceptTxResult added = txPool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast);
        if (!added)
        {
            _logger.Warn($"Failed to add signed transaction to the pool: {added} {header.ToString(BlockHeader.Format.FullHashAndNumber)}");
        }
    }

    private void OnBlockAddedToMain(object? sender, BlockReplacementEventArgs e)
    {
        if (e.Block.Header is not XdcBlockHeader xdcHeader)
            return;

        if (e.Block.Hash is null || !blockTree.WasProcessed(e.Block.Number, e.Block.Hash) || blockTree.IsSyncing().isSyncing)
            return;

        if (_alreadySigned.Contains(xdcHeader.Hash))
            return;

        ulong round = xdcHeader.ExtraConsensusData.BlockRound;
        IXdcReleaseSpec spec = specProvider.GetXdcSpec(xdcHeader, round);
        if (spec is null)
            return;

        if (xdcHeader.Number % spec.MergeSignRange != 0)
            return;

        Snapshot snapshot = snapshotManager.GetSnapshotByBlockNumber(xdcHeader.Number, spec);
        if (snapshot is null)
            return;

        if (IsMasternode(snapshot, signer.Address))
        {
            _alreadySigned.Set(xdcHeader.Hash);
            _ = SubmitTransactionSign(xdcHeader, spec)
                .ContinueWith(t => _logger.Error("Failed to submit sign transaction", t.Exception),
                TaskContinuationOptions.OnlyOnFaulted);
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
        blockTree.BlockAddedToMain -= OnBlockAddedToMain;
        GC.SuppressFinalize(this);
    }
}
