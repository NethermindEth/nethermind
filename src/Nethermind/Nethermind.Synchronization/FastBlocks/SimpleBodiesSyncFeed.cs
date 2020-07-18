//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.State.Proofs;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using Nethermind.Synchronization.SyncLimits;

namespace Nethermind.Synchronization.FastBlocks
{
    public class SimpleBodiesSyncFeed : ActivatedSyncFeed<SimpleBodiesSyncBatch>
    {
        private readonly int _bodiesRequestSize = GethSyncLimits.MaxBodyFetch;

        private readonly ILogger _logger;
        private readonly IBlockTree _blockTree;
        private readonly ISyncConfig _syncConfig;
        private readonly ISyncReport _syncReport;
        private readonly ISyncPeerPool _syncPeerPool;

        private readonly long _pivotNumber;

        private enum Status : byte
        {
            Unknown = 0,
            Sent = 1,
            Inserted = 2,
        }

        // save this to DB
        private Status[] _simpleStats;

        public SimpleBodiesSyncFeed(ISyncModeSelector syncModeSelector, IBlockTree blockTree, ISyncPeerPool syncPeerPool, ISyncConfig syncConfig, ISyncReport syncReport, ILogManager logManager)
            : base(syncModeSelector, logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _syncReport = syncReport ?? throw new ArgumentNullException(nameof(syncReport));

            if (!_syncConfig.UseGethLimitsInFastBlocks)
            {
                _bodiesRequestSize = NethermindSyncLimits.MaxBodyFetch;
            }

            if (!_syncConfig.FastBlocks)
            {
                throw new InvalidOperationException("Entered fast blocks mode without fast blocks enabled in configuration.");
            }

            _pivotNumber = _syncConfig.PivotNumberParsed;
            _lowestInsertWithoutGaps = _pivotNumber;
            _simpleStats = new Status[(int) _pivotNumber + 1];
        }

        protected override SyncMode ActivationSyncModes { get; } = SyncMode.FastBodies & ~SyncMode.FastBlocks;

        private bool ShouldFinish => !_syncConfig.DownloadBodiesInFastSync || (_blockTree.LowestInsertedBody?.Number ?? 0) == 1;

        public override bool IsMultiFeed => true;

        public override AllocationContexts Contexts => AllocationContexts.Bodies;

        private bool ShouldBuildANewBatch()
        {
            bool shouldDownloadBodies = _syncConfig.DownloadBodiesInFastSync;
            bool allBodiesDownloaded = (_blockTree.LowestInsertedBody?.Number ?? 0) == 1;
            // bool requestedGenesis = _lowestRequestedBodyHash == _blockTree.Genesis.Hash;
            bool requestedGenesis = false;

            bool noBatchesLeft = !shouldDownloadBodies
                                 || allBodiesDownloaded
                                 || requestedGenesis;

            if (noBatchesLeft)
            {
                if (ShouldFinish)
                {
                    Finish();
                    PostFinishCleanUp();
                }

                return false;
            }

            return true;
        }

        private void PostFinishCleanUp()
        {
            _syncReport.FastBlocksBodies.Update(_pivotNumber);
            _syncReport.FastBlocksBodies.MarkEnd();
        }

        private long _lowestInsertWithoutGaps;

        public override Task<SimpleBodiesSyncBatch> PrepareRequest()
        {
            if (!ShouldBuildANewBatch())
            {
                return Task.FromResult((SimpleBodiesSyncBatch) null);
            }

            SimpleBodiesSyncBatch simple = new SimpleBodiesSyncBatch();
            simple.Infos = new BlockInfo[_bodiesRequestSize];

            int collected = 0;
            long currentNumber = _lowestInsertWithoutGaps;
            while (collected < _bodiesRequestSize)
            {
                switch (_simpleStats[currentNumber])
                {
                    case Status.Unknown:
                        simple.Infos[collected] = _blockTree.FindBlockInfo(currentNumber);
                        _simpleStats[currentNumber] = Status.Sent;
                        collected++;
                        break;
                    case Status.Inserted:
                        _lowestInsertWithoutGaps--;
                        break;
                    case Status.Sent:
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                currentNumber--;
            }

            return Task.FromResult(simple);
        }

        public override SyncResponseHandlingResult HandleResponse(SimpleBodiesSyncBatch batch)
        {
            batch.MarkHandlingStart();
            try
            {
                if (batch.IsResponseEmpty)
                {
                    if (_logger.IsTrace) _logger.Trace($"{batch} - came back EMPTY");
                    return batch.ResponseSourcePeer == null
                        ? SyncResponseHandlingResult.NotAssigned
                        : SyncResponseHandlingResult.NoProgress;
                }
                else
                {
                    int added = InsertBodies(batch);
                    return added == 0 ? SyncResponseHandlingResult.NoProgress : SyncResponseHandlingResult.OK;    
                }
            }
            finally
            {
                batch.MarkHandlingEnd();
            }
        }

        private bool TryPrepareBlock(BlockInfo blockInfo, BlockBody blockBody, out Block block)
        {
            BlockHeader header = _blockTree.FindHeader(blockInfo.BlockHash);
            bool txRootIsValid = new TxTrie(blockBody.Transactions).RootHash != header.TxRoot;
            bool ommersHashIsValid = OmmersHash.Calculate(blockBody.Ommers) != header.OmmersHash;
            if (txRootIsValid && ommersHashIsValid)
            {
                block = null;
            }
            else
            {
                block = new Block(header, blockBody);
            }

            return block != null;
        }

        private int InsertBodies(SimpleBodiesSyncBatch batch)
        {
            bool hasBreachedProtocol = false;
            int validResponsesCount = 0;
            
            for (int i = 0; i < batch.Infos.Length; i++)
            {
                BlockInfo blockInfo = batch.Infos[i];
                BlockBody body = batch.Response.Length <= i
                    ? null
                    : batch.Response[i];
                if (body != null)
                {
                    Block block = null;
                    bool isValid = !hasBreachedProtocol && TryPrepareBlock(blockInfo, body, out block);
                    if (isValid)
                    {
                        validResponsesCount++;
                        _blockTree.Insert(block);
                        _simpleStats[block.Number] = Status.Inserted;
                    }
                    else
                    {
                        hasBreachedProtocol = true;
                        if (_logger.IsWarn) _logger.Warn($"{batch} - reporting INVALID - tx or ommers");
                        _syncPeerPool.ReportBreachOfProtocol(batch.ResponseSourcePeer, "invalid tx or ommers root");
                        _simpleStats[blockInfo.BlockNumber] = Status.Unknown;
                    }
                }
                else
                {
                    _simpleStats[blockInfo.BlockNumber] = Status.Unknown;
                }
            }

            _syncReport.FastBlocksBodies.Update(_pivotNumber - _lowestInsertWithoutGaps);
            return validResponsesCount;
        }
    }
}