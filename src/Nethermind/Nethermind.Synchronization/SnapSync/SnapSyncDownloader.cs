// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.State.Snap;
using Nethermind.Stats;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.SnapSync
{
    public class SnapSyncDownloader(ILogManager? logManager, INodeStatsManager? nodeStatsManager = null) : ISyncDownloader<SnapSyncBatch>
    {
        private readonly ILogger Logger = logManager.GetClassLogger<SnapSyncDownloader>();

        // TEMP(snap-code-throughput): measurement only, remove after the sync.
        private static readonly TimeSpan TempLogInterval = TimeSpan.FromMinutes(1);
        private readonly Lock _tempLock = new();
        private readonly TempCodeStats _tempTotal = new();
        private TempCodeStats _tempWindow = new();
        private long _tempTotalStart;
        private int _tempCodesInFlight;

        private sealed class TempCodeStats
        {
            public long Start = Stopwatch.GetTimestamp();
            public long Responses;
            public long Requested;
            public long Served;
            public long Bytes;
            public long LatencyTicks;
            public long LimitSum;
            public int LimitMin = int.MaxValue;
            public int LimitMax;
            public long NearByteLimit;
            public long Empty;
            public long InFlightSum;
            public int InFlightMax;
            // Fill = served / requested: 0%, (0,25], (25,50], (50,75], (75,100].
            public readonly long[] FillBuckets = new long[5];

            public void Add(int requested, int served, long bytes, long latencyTicks, int limit, int inFlight)
            {
                Responses++;
                Requested += requested;
                Served += served;
                Bytes += bytes;
                LatencyTicks += latencyTicks;
                if (limit > 0)
                {
                    LimitSum += limit;
                    LimitMin = Math.Min(LimitMin, limit);
                    LimitMax = Math.Max(LimitMax, limit);
                    if (bytes >= limit * 0.9) NearByteLimit++;
                }
                if (served == 0) Empty++;
                InFlightSum += inFlight;
                InFlightMax = Math.Max(InFlightMax, inFlight);

                double fill = requested == 0 ? 0 : (double)served / requested;
                FillBuckets[fill <= 0 ? 0 : fill <= 0.25 ? 1 : fill <= 0.5 ? 2 : fill <= 0.75 ? 3 : 4]++;
            }

            public string Format(long now)
            {
                double seconds = Math.Max(Stopwatch.GetElapsedTime(Start, now).TotalSeconds, 0.001);
                long responses = Math.Max(Responses, 1);
                string Bucket(int i) => $"{(double)FillBuckets[i] / responses:P0}";
                return $"{Stopwatch.GetElapsedTime(Start, now).TotalMinutes:N1} min, responses: {Responses:N0}, codes/s: {Served / seconds:N0}, KB/s: {Bytes / 1024.0 / seconds:N0} | " +
                       $"served/requested: {Served:N0}/{Requested:N0} ({(double)Served / Math.Max(Requested, 1):P0}), fill 0%/<=25/<=50/<=75/<=100: {Bucket(0)}/{Bucket(1)}/{Bucket(2)}/{Bucket(3)}/{Bucket(4)}, empty: {Empty:N0} | " +
                       $"avg KB/response: {Bytes / 1024.0 / responses:N0}, near byte limit: {(double)NearByteLimit / responses:P0}, byte limit avg/min/max KB: {LimitSum / 1024.0 / responses:N0}/{(LimitMin == int.MaxValue ? 0 : LimitMin / 1024):N0}/{LimitMax / 1024:N0} | " +
                       $"avg latency: {Stopwatch.GetElapsedTime(0, LatencyTicks / responses).TotalSeconds:N2} s, code requests in flight avg/max: {(double)InFlightSum / responses:N1}/{InFlightMax}";
            }
        }

        private void TempRecordCodes(PeerInfo peerInfo, int requested, IByteArrayList? codes, int limit, int inFlight, long startTimestamp)
        {
            long now = Stopwatch.GetTimestamp();
            int served = codes?.Count ?? 0;
            long bytes = 0;
            for (int i = 0; i < served; i++)
            {
                bytes += codes![i].Length;
            }

            string? windowText = null;
            string? totalText = null;
            lock (_tempLock)
            {
                if (_tempTotalStart == 0)
                {
                    _tempTotalStart = startTimestamp;
                    _tempTotal.Start = startTimestamp;
                    _tempWindow.Start = startTimestamp;
                }

                _tempTotal.Add(requested, served, bytes, now - startTimestamp, limit, inFlight);
                _tempWindow.Add(requested, served, bytes, now - startTimestamp, limit, inFlight);

                if (Stopwatch.GetElapsedTime(_tempWindow.Start, now) >= TempLogInterval)
                {
                    windowText = _tempWindow.Format(now);
                    totalText = _tempTotal.Format(now);
                    _tempWindow = new TempCodeStats { Start = now };
                }
            }

            if (windowText is not null && Logger.IsInfo)
            {
                Logger.Info($"[SNAP-CODE-THROUGHPUT] last {windowText}");
                Logger.Info($"[SNAP-CODE-THROUGHPUT] total {totalText}");
            }
        }

        private int TempCurrentLimit(PeerInfo peerInfo) =>
            nodeStatsManager?.GetOrAdd(peerInfo.SyncPeer.Node).GetCurrentRequestLimit(RequestType.SnapRanges) ?? -1;

        public async Task Dispatch(PeerInfo peerInfo, SnapSyncBatch batch, CancellationToken cancellationToken)
        {
            ISyncPeer peer = peerInfo.SyncPeer;

            if (peer.TryGetSatelliteProtocol<ISnapSyncPeer>(Protocol.Snap, out ISnapSyncPeer handler))
            {
                try
                {
                    if (batch.AccountRangeRequest is not null)
                    {
                        batch.AccountRangeResponse = await handler.GetAccountRange(batch.AccountRangeRequest, cancellationToken);
                    }
                    else if (batch.StorageRangeRequest is not null)
                    {
                        batch.StorageRangeResponse = await handler.GetStorageRange(batch.StorageRangeRequest, cancellationToken);
                    }
                    else if (batch.CodesRequest is not null)
                    {
                        // TEMP(snap-code-throughput)
                        long tempStart = Stopwatch.GetTimestamp();
                        int tempLimit = TempCurrentLimit(peerInfo);
                        int tempInFlight = Interlocked.Increment(ref _tempCodesInFlight);
                        try
                        {
                            batch.CodesResponse = await handler.GetByteCodes(batch.CodesRequest, cancellationToken);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _tempCodesInFlight);
                            TempRecordCodes(peerInfo, batch.CodesRequest.Count, batch.CodesResponse, tempLimit, tempInFlight, tempStart);
                        }
                    }
                    else if (batch.AccountsToRefreshRequest is { Paths.Count: > 0 })
                    {
                        // Refresh a single account via GetAccountRange so its storage root is verified against
                        // the state root. Use limit = path + 1 to avoid start == limit, which some peers treat as
                        // an empty range. (IncrementPath is a no-op only for the unreachable MaxValue path.)
                        AccountsToRefreshRequest request = batch.AccountsToRefreshRequest;
                        AccountWithStorageStartingHash account = request.Paths[0];
                        PathWithAccount pathAndAccount = account.PathAndAccount
                            ?? throw new InvalidOperationException("An account refresh request requires an account path.");
                        ValueHash256 path = pathAndAccount.Path;
                        AccountRange range = new(request.RootHash, path, path.IncrementPath());
                        batch.AccountsToRefreshResponse = await handler.GetAccountRange(range, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    if (Logger.IsDebug) Logger.Debug($"Snap sync request cancelled. Request: {batch}");
                }
                catch (Exception e)
                {
                    Logger.DebugError($"Error after dispatching the snap sync request. Request: {batch}", e);
                }
            }
        }
    }
}
