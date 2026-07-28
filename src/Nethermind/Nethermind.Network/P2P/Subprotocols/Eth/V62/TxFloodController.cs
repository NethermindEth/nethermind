// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Stats.Model;
using Nethermind.TxPool;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62
{
    internal class TxFloodController
    {
        private const int MinimumPooledTransactionRequests = 4_096;
        private const int PooledTransactionRequestSampleCapacity = 128;
        private const int UnproductivePooledTransactionReturnPercent = 8;
        private const int PooledTransactionReturnConfidenceStandardDeviations = 3;
        private const int UnproductivePooledTransactionWindowLimit = 2;
        private const int MaximumPooledTransactionsPerResponse = 256;
        private const int RetainedCreditedFingerprintCapacity = 4_096;

        private readonly Eth62ProtocolHandler _protocolHandler;
        private readonly ITimestamper _timestamper;
        private readonly ILogger _logger;
        private readonly Random _random;
        private readonly byte[] _pooledTransactionFingerprintKey;
        private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(60);
        private readonly Lock _accountingLock = new();
        private PooledTransactionSample _currentPooledTransactionSample = new(PooledTransactionRequestSampleCapacity);
        private PooledTransactionSample _previousPooledTransactionSample = new(PooledTransactionRequestSampleCapacity);
        private DateTime _checkpoint;
        private long _notAcceptedSinceLastCheck;
        private int _unproductivePooledTransactionWindows;
        private bool _isLegacyDowngraded;
        private bool _isPooledTransactionDowngraded;

        public TxFloodController(Eth62ProtocolHandler protocolHandler, ITimestamper timestamper, ILogger logger, Random? random = null)
        {
            _protocolHandler = protocolHandler ?? throw new ArgumentNullException(nameof(protocolHandler));
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            _logger = logger;
            _random = random ?? Random.Shared;
            _pooledTransactionFingerprintKey = RandomNumberGenerator.GetBytes(32);
            _checkpoint = _timestamper.UtcNow;
        }

        internal bool IsDowngraded
        {
            get
            {
                lock (_accountingLock)
                {
                    return _isLegacyDowngraded || _isPooledTransactionDowngraded;
                }
            }
        }

        internal long RequestedPooledTransactionHashes
        {
            get
            {
                lock (_accountingLock)
                {
                    return _currentPooledTransactionSample.RequestedHashes
                        + _previousPooledTransactionSample.RequestedHashes;
                }
            }
        }

        public void Report(AcceptTxResult accepted)
        {
            DisconnectRequest? disconnectRequest;
            lock (_accountingLock)
            {
                disconnectRequest = ResetIfElapsed();

                if (!accepted)
                {
                    if (accepted == AcceptTxResult.Invalid)
                    {
                        disconnectRequest ??= new(
                            DisconnectReason.InvalidTxReceived,
                            "invalid tx",
                            $"Disconnecting {_protocolHandler} due to invalid tx received");
                    }
                    else
                    {
                        _notAcceptedSinceLastCheck++;
                        if (!_isLegacyDowngraded && _notAcceptedSinceLastCheck / _checkInterval.TotalSeconds > 10)
                        {
                            if (_logger.IsDebug) _logger.Debug($"Downgrading {_protocolHandler} due to tx flooding");
                            _isLegacyDowngraded = true;
                        }
                        else if (_notAcceptedSinceLastCheck / _checkInterval.TotalSeconds > 100)
                        {
                            disconnectRequest ??= new(
                                DisconnectReason.TxFlooding,
                                $"tx flooding {_notAcceptedSinceLastCheck}/{_checkInterval.TotalSeconds}",
                                $"Disconnecting {_protocolHandler} due to tx flooding");
                        }
                    }
                }
            }

            DisconnectIfRequested(disconnectRequest);
        }

        public void ReportPooledTransactionRequest(ReadOnlySpan<Hash256> hashes)
        {
            DisconnectRequest? disconnectRequest;
            lock (_accountingLock)
            {
                disconnectRequest = ResetIfElapsed();

                _currentPooledTransactionSample.ReportRequest(
                    hashes,
                    _random,
                    _pooledTransactionFingerprintKey);
            }

            DisconnectIfRequested(disconnectRequest);
        }

        public void ReportPooledTransactionsReturned(ReadOnlySpan<Transaction> transactions)
        {
            int transactionCount = Math.Min(
                transactions.Length,
                MaximumPooledTransactionsPerResponse);
            using ArrayPoolListRef<ulong> fingerprints = new(transactionCount);
            for (int i = 0; i < transactionCount; i++)
            {
                Hash256? hash = transactions[i].Hash;
                if (hash is not null)
                {
                    fingerprints.Add(GetPooledTransactionFingerprint(
                        hash.ValueHash256,
                        _pooledTransactionFingerprintKey));
                }
            }

            fingerprints.AsSpan().Sort();

            DisconnectRequest? disconnectRequest;
            lock (_accountingLock)
            {
                disconnectRequest = ResetIfElapsed();

                Span<ulong> unconsumed = fingerprints.AsSpan();
                int unconsumedCount = _previousPooledTransactionSample.RemoveCredited(unconsumed);
                if (unconsumedCount > 0
                    && !_previousPooledTransactionSample.TryReportResponse(
                        unconsumed[..unconsumedCount]))
                {
                    unconsumedCount = _currentPooledTransactionSample.RemoveCredited(
                        unconsumed[..unconsumedCount]);
                    if (unconsumedCount > 0)
                    {
                        _currentPooledTransactionSample.TryReportResponse(
                            unconsumed[..unconsumedCount]);
                    }
                }
            }

            DisconnectIfRequested(disconnectRequest);
        }

        public void ClearPooledTransactionRequests()
        {
            lock (_accountingLock)
            {
                _currentPooledTransactionSample.Clear();
                _previousPooledTransactionSample.Clear();
            }
        }

        private DisconnectRequest? ResetIfElapsed()
        {
            DateTime now = _timestamper.UtcNow;
            if (now >= _checkpoint + _checkInterval)
            {
                int sampled = _previousPooledTransactionSample.Count;
                int useful = _previousPooledTransactionSample.Useful;
                bool evaluated =
                    _previousPooledTransactionSample.RequestedHashes >= MinimumPooledTransactionRequests
                    && sampled == PooledTransactionRequestSampleCapacity;
                double expectedUseful = sampled * UnproductivePooledTransactionReturnPercent / 100.0;
                double standardDeviation = Math.Sqrt(
                    sampled
                    * UnproductivePooledTransactionReturnPercent
                    * (100 - UnproductivePooledTransactionReturnPercent))
                    / 100.0;
                bool unproductive = evaluated
                    && useful < expectedUseful
                        - PooledTransactionReturnConfidenceStandardDeviations * standardDeviation;

                _checkpoint = now;
                _notAcceptedSinceLastCheck = 0;
                _isLegacyDowngraded = false;
                _previousPooledTransactionSample.Clear();
                (_currentPooledTransactionSample, _previousPooledTransactionSample) =
                    (_previousPooledTransactionSample, _currentPooledTransactionSample);

                if (!evaluated)
                {
                    return null;
                }

                _isPooledTransactionDowngraded = unproductive;

                if (unproductive)
                {
                    _unproductivePooledTransactionWindows++;
                    if (_unproductivePooledTransactionWindows >= UnproductivePooledTransactionWindowLimit)
                    {
                        return new(
                            DisconnectReason.TxFlooding,
                            $"pooled transaction flooding: returned requested transactions in {useful} of {sampled} sampled request messages",
                            $"Disconnecting {_protocolHandler} due to unproductive pooled transaction announcements");
                    }
                }
                else
                {
                    _unproductivePooledTransactionWindows = 0;
                }
            }

            return null;
        }

        public bool IsAllowed()
        {
            bool isDowngraded;
            DisconnectRequest? disconnectRequest;
            lock (_accountingLock)
            {
                disconnectRequest = ResetIfElapsed();
                isDowngraded = _isLegacyDowngraded || _isPooledTransactionDowngraded;
            }

            DisconnectIfRequested(disconnectRequest);
            return !(IsEnabled && isDowngraded && 10 < _random.Next(0, 99));
        }

        public bool IsEnabled { get; set; } = true;

        private void DisconnectIfRequested(DisconnectRequest? request)
        {
            if (request is not { } disconnect)
            {
                return;
            }

            if (_logger.IsDebug) _logger.Debug(disconnect.DebugMessage);
            _protocolHandler.Disconnect(disconnect.Reason, disconnect.Details);
        }

        private readonly record struct DisconnectRequest(
            DisconnectReason Reason,
            string Details,
            string DebugMessage);

        private sealed class PooledTransactionSample(int capacity)
        {
            private SampledPooledTransactionRequest?[]? _requests;
            private Dictionary<ulong, int>? _creditedFingerprints;
            private bool _releaseCreditedFingerprintsOnClear;
            private long _seenRequests;
            private long _requestSequence;

            public int Count { get; private set; }
            public int Useful { get; private set; }
            public long RequestedHashes { get; private set; }

            public void ReportRequest(
                ReadOnlySpan<Hash256> hashes,
                Random random,
                byte[] fingerprintKey)
            {
                RequestedHashes += hashes.Length;
                _seenRequests++;
                _requestSequence++;

                int index;
                if (Count < capacity)
                {
                    index = Count++;
                    _requests ??= new SampledPooledTransactionRequest?[capacity];
                }
                else
                {
                    long sampledIndex = random.NextInt64(_seenRequests);
                    if (sampledIndex >= capacity)
                    {
                        return;
                    }

                    index = (int)sampledIndex;
                    if (_requests![index]!.IsUseful)
                    {
                        _requests[index]!.RemoveCreditsFrom(_creditedFingerprints!);
                        Useful--;
                    }
                }

                SampledPooledTransactionRequest request =
                    _requests![index] ??= new SampledPooledTransactionRequest();
                request.Set(hashes, _requestSequence, fingerprintKey);
            }

            public bool TryReportResponse(ReadOnlySpan<ulong> fingerprints)
            {
                SampledPooledTransactionRequest? oldest = null;
                for (int i = 0; i < Count; i++)
                {
                    SampledPooledTransactionRequest request = _requests![i]!;
                    if (!request.IsAnswered
                        && request.CountMatches(fingerprints) > 0
                        && (oldest is null || request.Sequence < oldest.Sequence))
                    {
                        oldest = request;
                    }
                }

                if (oldest is null)
                {
                    return false;
                }

                oldest.MarkUseful(fingerprints);
                _creditedFingerprints ??= [];
                oldest.AddCreditsTo(_creditedFingerprints);
                _releaseCreditedFingerprintsOnClear |=
                    _creditedFingerprints.Count > RetainedCreditedFingerprintCapacity;
                Useful++;
                return true;
            }

            public int RemoveCredited(Span<ulong> fingerprints)
            {
                if (_creditedFingerprints is null)
                {
                    return fingerprints.Length;
                }

                int unconsumedCount = 0;
                for (int i = 0; i < fingerprints.Length; i++)
                {
                    ulong fingerprint = fingerprints[i];
                    if (!_creditedFingerprints.ContainsKey(fingerprint))
                    {
                        fingerprints[unconsumedCount++] = fingerprint;
                    }
                }

                return unconsumedCount;
            }

            public void Clear()
            {
                if (_releaseCreditedFingerprintsOnClear)
                {
                    _creditedFingerprints = null;
                    _releaseCreditedFingerprintsOnClear = false;
                }
                else
                {
                    _creditedFingerprints?.Clear();
                }

                _seenRequests = 0;
                _requestSequence = 0;
                Count = 0;
                Useful = 0;
                RequestedHashes = 0;
            }
        }

        private sealed class SampledPooledTransactionRequest
        {
            private ulong[] _fingerprints = [];
            private bool[] _returned = [];
            private int _count;

            public long Sequence { get; private set; }
            public bool IsAnswered { get; private set; }
            public bool IsUseful { get; private set; }

            public void Set(
                ReadOnlySpan<Hash256> hashes,
                long sequence,
                byte[] fingerprintKey)
            {
                if (_fingerprints.Length < hashes.Length)
                {
                    _fingerprints = new ulong[hashes.Length];
                    _returned = new bool[hashes.Length];
                }
                else
                {
                    Array.Clear(_returned, 0, _count);
                }

                _count = hashes.Length;
                for (int i = 0; i < hashes.Length; i++)
                {
                    _fingerprints[i] = GetPooledTransactionFingerprint(
                        hashes[i].ValueHash256,
                        fingerprintKey);
                }

                _fingerprints.AsSpan(0, _count).Sort();
                Sequence = sequence;
                IsAnswered = false;
                IsUseful = false;
            }

            public int CountMatches(ReadOnlySpan<ulong> fingerprints)
            {
                ReadOnlySpan<ulong> requested = _fingerprints.AsSpan(0, _count);
                int requestIndex = 0;
                int responseIndex = 0;
                int matches = 0;
                while (requestIndex < requested.Length && responseIndex < fingerprints.Length)
                {
                    ulong requestedFingerprint = requested[requestIndex];
                    ulong returnedFingerprint = fingerprints[responseIndex];
                    if (requestedFingerprint == returnedFingerprint)
                    {
                        matches++;
                        requestIndex++;
                        responseIndex++;
                    }
                    else if (requestedFingerprint < returnedFingerprint)
                    {
                        requestIndex++;
                    }
                    else
                    {
                        responseIndex++;
                    }
                }

                return matches;
            }

            public void AddCreditsTo(Dictionary<ulong, int> creditedFingerprints)
            {
                for (int i = 0; i < _count; i++)
                {
                    if (_returned[i])
                    {
                        ulong fingerprint = _fingerprints[i];
                        creditedFingerprints.TryGetValue(fingerprint, out int count);
                        creditedFingerprints[fingerprint] = count + 1;
                    }
                }
            }

            public void RemoveCreditsFrom(Dictionary<ulong, int> creditedFingerprints)
            {
                for (int i = 0; i < _count; i++)
                {
                    if (!_returned[i])
                    {
                        continue;
                    }

                    ulong fingerprint = _fingerprints[i];
                    int count = creditedFingerprints[fingerprint];
                    if (count == 1)
                    {
                        creditedFingerprints.Remove(fingerprint);
                    }
                    else
                    {
                        creditedFingerprints[fingerprint] = count - 1;
                    }
                }
            }

            public void MarkUseful(ReadOnlySpan<ulong> fingerprints)
            {
                int requestIndex = 0;
                int responseIndex = 0;
                while (requestIndex < _count && responseIndex < fingerprints.Length)
                {
                    ulong requestedFingerprint = _fingerprints[requestIndex];
                    ulong returnedFingerprint = fingerprints[responseIndex];
                    if (requestedFingerprint == returnedFingerprint)
                    {
                        _returned[requestIndex] = true;
                        requestIndex++;
                        responseIndex++;
                    }
                    else if (requestedFingerprint < returnedFingerprint)
                    {
                        requestIndex++;
                    }
                    else
                    {
                        responseIndex++;
                    }
                }

                IsAnswered = true;
                IsUseful = true;
            }
        }

        private static ulong GetPooledTransactionFingerprint(
            in ValueHash256 hash,
            byte[] fingerprintKey)
        {
            Span<byte> digest = stackalloc byte[32];
            HMACSHA256.HashData(fingerprintKey, hash.Bytes, digest);
            return BinaryPrimitives.ReadUInt64LittleEndian(digest);
        }
    }
}
