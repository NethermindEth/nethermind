//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System;
using Nethermind.Blockchain;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Synchronization.FastSync
{
    public partial class StateSyncFeed
    {
        private class DetailedProgress
        {
            private long _lastDataSize;
            // private long _lastHandledNodesCount;
            internal long LastDbReads;
            internal decimal AverageTimeInHandler;
            // ReSharper disable once NotAccessedField.Local
            internal long LastRequestedNodesCount;
            // ReSharper disable once NotAccessedField.Local
            internal long LastSavedNodesCount;
            internal long ConsumedNodesCount;
            internal long SavedStorageCount;
            internal long SavedStateCount;
            internal long SavedNodesCount;
            internal long SavedAccounts;
            internal long SavedCode;
            internal long RequestedNodesCount;
            internal long HandledNodesCount;
            internal long SecondsInSync;
            internal long DbChecks;
            internal long CheckWasCached;
            internal long CheckWasInDependencies;
            internal long StateWasThere;
            internal long StateWasNotThere;
            internal long EmptishCount;
            internal long InvalidFormatCount;
            internal long OkCount;
            internal long BadQualityCount;
            internal long NotAssignedCount;
            internal long DataSize;

            private long TotalRequestsCount => EmptishCount + InvalidFormatCount + BadQualityCount + OkCount + NotAssignedCount;
            public long ProcessedRequestsCount => EmptishCount + BadQualityCount + OkCount;

            internal (DateTime small, DateTime full) LastReportTime = (DateTime.MinValue, DateTime.MinValue);

            private Known.SizeInfo? _chainSizeInfo;

            public DetailedProgress(ulong chainId, byte[] serializedInitialState)
            {
                if (Known.ChainSize.ContainsKey(chainId))
                {
                    _chainSizeInfo = Known.ChainSize[chainId];
                }

                LoadFromSerialized(serializedInitialState);
            }

            internal void DisplayProgressReport(int pendingRequestsCount, BranchProgress branchProgress, ILogger logger)
            {
                TimeSpan sinceLastReport = DateTime.UtcNow - LastReportTime.small;
                if (sinceLastReport > TimeSpan.FromSeconds(1))
                {
                    // decimal savedNodesPerSecond = 1000m * (SavedNodesCount - LastSavedNodesCount) / (decimal) sinceLastReport.TotalMilliseconds;
                    decimal savedKBytesPerSecond = 1000m * ((DataSize - _lastDataSize) / 1000m) / (decimal) sinceLastReport.TotalMilliseconds;
                    // decimal requestedNodesPerSecond = 1000m * (RequestedNodesCount - LastRequestedNodesCount) / (decimal) sinceLastReport.TotalMilliseconds;
                    // decimal handledNodesPerSecond = 1000m * (HandledNodesCount - _lastHandledNodesCount) / (decimal) sinceLastReport.TotalMilliseconds;
                    _lastDataSize = DataSize;
                    // LastSavedNodesCount = SavedNodesCount;
                    // LastRequestedNodesCount = RequestedNodesCount;
                    // _lastHandledNodesCount = HandledNodesCount;
                    // if (_logger.IsInfo) _logger.Info($"Time {TimeSpan.FromSeconds(_secondsInSync):dd\\.hh\\:mm\\:ss} | {(decimal) _dataSize / 1000 / 1000,6:F2}MB | kBps: {savedKBytesPerSecond,5:F0} | P: {_pendingRequests.Count} | acc {_savedAccounts} | queues {StreamsDescription} | db {_averageTimeInHandler:f2}ms");

                    Metrics.StateSynced = DataSize;
                    string dataSizeInfo = $"{(decimal) DataSize / 1000 / 1000,6:F2}MB";
                    if (_chainSizeInfo != null)
                    {
                        decimal percentage = Math.Min(1, (decimal) DataSize / _chainSizeInfo.Value.Current);
                        dataSizeInfo = string.Concat(
                            $"~{percentage:P2} | ", dataSizeInfo,
                            $" / ~{(decimal) _chainSizeInfo.Value.Current / 1000 / 1000,6:F2}MB");
                    }

                    if (logger.IsInfo) logger.Info(
                        $"State Sync {TimeSpan.FromSeconds(SecondsInSync):dd\\.hh\\:mm\\:ss} | {dataSizeInfo} | branches: {branchProgress.Progress:P2} | kB/s: {savedKBytesPerSecond,5:F0} | accounts {SavedAccounts} | nodes {SavedNodesCount} | diagnostics: {pendingRequestsCount}.{AverageTimeInHandler:f2}ms");
                    if (logger.IsDebug && DateTime.UtcNow - LastReportTime.full > TimeSpan.FromSeconds(10))
                    {
                        long allChecks = CheckWasInDependencies + CheckWasCached + StateWasThere + StateWasNotThere;
                        if (logger.IsDebug) logger.Debug($"OK {(decimal) OkCount / TotalRequestsCount:p2} | Emptish: {(decimal) EmptishCount / TotalRequestsCount:p2} | BadQuality: {(decimal) BadQualityCount / TotalRequestsCount:p2} | InvalidFormat: {(decimal) InvalidFormatCount / TotalRequestsCount:p2} | NotAssigned {(decimal) NotAssignedCount / TotalRequestsCount:p2}");
                        if (RequestedNodesCount > 0)
                        {
                            decimal consumedRatio = (decimal) ConsumedNodesCount / RequestedNodesCount;
                            decimal saveRatio = (decimal) SavedNodesCount / RequestedNodesCount;
                            decimal dbCheckRatio = (decimal) DbChecks / RequestedNodesCount;
                            if (logger.IsDebug) logger.Debug(
                                $"Consumed {consumedRatio:p2} | Saved {saveRatio:p2} | DB Reads : {dbCheckRatio:p2} | DB checks {StateWasThere}/{StateWasNotThere + StateWasThere} | Cached {(decimal) CheckWasCached / allChecks:P2} + {(decimal) CheckWasInDependencies / allChecks:P2}");
                        }

                        LastReportTime.full = DateTime.UtcNow;
                    }

                    LastReportTime.small = DateTime.UtcNow;
                }
            }

            private void LoadFromSerialized(byte[] serializedData)
            {
                if (serializedData != null)
                {
                    RlpStream rlpStream = new(serializedData);
                    rlpStream.ReadSequenceLength();
                    ConsumedNodesCount = rlpStream.DecodeLong();
                    SavedStorageCount = rlpStream.DecodeLong();
                    SavedStateCount = rlpStream.DecodeLong();
                    SavedNodesCount = rlpStream.DecodeLong();
                    SavedAccounts = rlpStream.DecodeLong();
                    SavedCode = rlpStream.DecodeLong();
                    RequestedNodesCount = rlpStream.DecodeLong();
                    DbChecks = rlpStream.DecodeLong();
                    StateWasThere = rlpStream.DecodeLong();
                    StateWasNotThere = rlpStream.DecodeLong();
                    DataSize = rlpStream.DecodeLong();

                    if (rlpStream.Position != rlpStream.Length)
                    {
                        SecondsInSync = rlpStream.DecodeLong();
                    }
                }
            }

            public byte[] Serialize()
            {
                Rlp rlp = Rlp.Encode(
                    Rlp.Encode(ConsumedNodesCount),
                    Rlp.Encode(SavedStorageCount),
                    Rlp.Encode(SavedStateCount),
                    Rlp.Encode(SavedNodesCount),
                    Rlp.Encode(SavedAccounts),
                    Rlp.Encode(SavedCode),
                    Rlp.Encode(RequestedNodesCount),
                    Rlp.Encode(DbChecks),
                    Rlp.Encode(StateWasThere),
                    Rlp.Encode(StateWasNotThere),
                    Rlp.Encode(DataSize),
                    Rlp.Encode(SecondsInSync));

                return rlp.Bytes;
            }
        }
    }
}
