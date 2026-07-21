// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Stats.Model;
using Nethermind.TxPool;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62
{
    internal class TxFloodController
    {
        private const int MinimumPooledTransactionRequests = 4_096;
        private const int UnproductivePooledTransactionReturnPercent = 8;
        private const int UnproductivePooledTransactionWindowLimit = 2;

        private readonly Eth62ProtocolHandler _protocolHandler;
        private readonly ITimestamper _timestamper;
        private readonly ILogger _logger;
        private readonly Random _random;
        private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(60);
        private readonly Lock _accountingLock = new();
        private PooledTransactionSample _currentPooledTransactionSample = new(MinimumPooledTransactionRequests);
        private PooledTransactionSample _previousPooledTransactionSample = new(MinimumPooledTransactionRequests);
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

        public void Report(AcceptTxResult accepted)
        {
            lock (_accountingLock)
            {
                ResetIfElapsed();

                if (!accepted)
                {
                    if (accepted == AcceptTxResult.Invalid)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Disconnecting {_protocolHandler} due to invalid tx received");
                        _protocolHandler.Disconnect(DisconnectReason.InvalidTxReceived, $"invalid tx");
                        return;
                    }

                    _notAcceptedSinceLastCheck++;
                    if (!_isLegacyDowngraded && _notAcceptedSinceLastCheck / _checkInterval.TotalSeconds > 10)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Downgrading {_protocolHandler} due to tx flooding");
                        _isLegacyDowngraded = true;
                    }
                    else if (_notAcceptedSinceLastCheck / _checkInterval.TotalSeconds > 100)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Disconnecting {_protocolHandler} due to tx flooding");
                        _protocolHandler.Disconnect(
                            DisconnectReason.TxFlooding,
                            $"tx flooding {_notAcceptedSinceLastCheck}/{_checkInterval.TotalSeconds}");
                    }
                }
            }
        }

        public void ReportPooledTransactionRequests(ReadOnlySpan<Hash256> hashes)
        {
            lock (_accountingLock)
            {
                ResetIfElapsed();

                _currentPooledTransactionSample.ReportRequests(hashes, _random);
            }
        }

        public void ReportPooledTransactionsReturned(ReadOnlySpan<Transaction> transactions)
        {
            lock (_accountingLock)
            {
                ResetIfElapsed();

                for (int i = 0; i < transactions.Length; i++)
                {
                    Hash256? hash = transactions[i].Hash;
                    if (hash is null)
                    {
                        continue;
                    }

                    _currentPooledTransactionSample.ReportReturn(hash.ValueHash256);
                    _previousPooledTransactionSample.ReportReturn(hash.ValueHash256);
                }
            }
        }

        public void ClearPooledTransactionRequests()
        {
            lock (_accountingLock)
            {
                _currentPooledTransactionSample.Clear();
                _previousPooledTransactionSample.Clear();
            }
        }

        private void ResetIfElapsed()
        {
            DateTime now = _timestamper.UtcNow;
            if (now >= _checkpoint + _checkInterval)
            {
                int sampled = _previousPooledTransactionSample.Count;
                int matched = _previousPooledTransactionSample.Matched;
                bool evaluated = sampled >= MinimumPooledTransactionRequests;
                bool unproductive = evaluated && matched * 100 < sampled * UnproductivePooledTransactionReturnPercent;

                _checkpoint = now;
                _notAcceptedSinceLastCheck = 0;
                _isLegacyDowngraded = false;
                _previousPooledTransactionSample.Clear();
                (_currentPooledTransactionSample, _previousPooledTransactionSample) =
                    (_previousPooledTransactionSample, _currentPooledTransactionSample);

                if (!evaluated)
                {
                    return;
                }

                _isPooledTransactionDowngraded = unproductive;

                if (unproductive)
                {
                    _unproductivePooledTransactionWindows++;
                    if (_unproductivePooledTransactionWindows >= UnproductivePooledTransactionWindowLimit)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Disconnecting {_protocolHandler} due to unproductive pooled transaction announcements");
                        _protocolHandler.Disconnect(
                            DisconnectReason.TxFlooding,
                            $"pooled transaction flooding: returned {matched} of {sampled} sampled requested transactions");
                    }
                }
                else
                {
                    _unproductivePooledTransactionWindows = 0;
                }
            }
        }

        public bool IsAllowed()
        {
            bool isDowngraded;
            lock (_accountingLock)
            {
                ResetIfElapsed();
                isDowngraded = _isLegacyDowngraded || _isPooledTransactionDowngraded;
            }

            return !(IsEnabled && isDowngraded && 10 < _random.Next(0, 99));
        }

        public bool IsEnabled { get; set; } = true;

        private sealed class PooledTransactionSample(int capacity)
        {
            private readonly Dictionary<ValueHash256, int> _indexes = [];
            private ValueHash256[] _hashes = [];
            private bool[] _matched = [];
            private int _seen;

            public int Count { get; private set; }
            public int Matched { get; private set; }

            public void ReportRequests(ReadOnlySpan<Hash256> hashes, Random random)
            {
                for (int i = 0; i < hashes.Length; i++)
                {
                    Hash256 hash = hashes[i];
                    if (hash is null || _indexes.ContainsKey(hash.ValueHash256))
                    {
                        continue;
                    }

                    int index = Count;
                    _seen++;
                    if (Count == capacity)
                    {
                        index = random.Next(_seen);
                        if (index >= capacity)
                        {
                            continue;
                        }

                        _indexes.Remove(_hashes[index]);
                        if (_matched[index])
                        {
                            Matched--;
                        }
                    }
                    else
                    {
                        Count++;
                        EnsureCapacity(Count);
                    }

                    ValueHash256 valueHash = hash.ValueHash256;
                    _hashes[index] = valueHash;
                    _matched[index] = false;
                    _indexes.Add(valueHash, index);
                }
            }

            public void ReportReturn(ValueHash256 hash)
            {
                if (_indexes.TryGetValue(hash, out int index) && !_matched[index])
                {
                    _matched[index] = true;
                    Matched++;
                }
            }

            public void Clear()
            {
                _indexes.Clear();
                _seen = 0;
                Count = 0;
                Matched = 0;
            }

            private void EnsureCapacity(int count)
            {
                if (count <= _hashes.Length)
                {
                    return;
                }

                int newCapacity = Math.Min(capacity, Math.Max(16, _hashes.Length * 2));
                Array.Resize(ref _hashes, newCapacity);
                Array.Resize(ref _matched, newCapacity);
            }
        }
    }
}
