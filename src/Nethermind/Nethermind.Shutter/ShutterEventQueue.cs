// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Collections;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Shutter.Contracts;

namespace Nethermind.Shutter;

public class ShutterEventQueue(int encryptedGasLimit, ILogManager logManager)
{
    private const int MaxQueueSize = 10000;
    private ulong? _eon;
    private ulong? _txIndex;
    private ulong _nextEonTxIndex = 0;
    private Queue<ISequencerContract.TransactionSubmitted> _events = [];
    private Queue<ISequencerContract.TransactionSubmitted> _nextEonEvents = [];
    private readonly ILogger _logger = logManager.GetClassLogger();

    public int Count { get => _events.Count; }

    public void EnqueueEvents(IEnumerable<ISequencerContract.TransactionSubmitted> events, ulong eon)
        => events.ForEach(e => EnqueueEvent(e, eon));

    public void EnqueueEvent(ISequencerContract.TransactionSubmitted e, ulong eon)
    {
        if (_eon is null || eon > _eon)
        {
            SetEon(eon);
        }

        if (e.Eon == _eon)
        {
            if (_logger.IsDebug && _txIndex is not null && e.TxIndex != _txIndex)
            {
                _logger.Warn($"Loading unexpected Shutter event with index {e.TxIndex} in eon {_eon}, expected {_txIndex}.");
            }

            _txIndex = e.TxIndex + 1;

            if (_events.Count < MaxQueueSize)
            {
                _events.Enqueue(e);
            }
            else
            {
                if (_logger.IsError) _logger.Error($"Shutter queue for eon {_eon} is full, cannot load events.");
                return;
            }
        }
        else if (e.Eon == _eon + 1)
        {
            if (_logger.IsDebug && e.TxIndex != _nextEonTxIndex)
            {
                _logger.Warn($"Loading unexpected Shutter event with index {e.TxIndex} in eon {_eon + 1}, expected {_nextEonTxIndex}.");
            }

            _nextEonTxIndex = e.TxIndex + 1;

            if (_nextEonEvents.Count < MaxQueueSize)
            {
                _nextEonEvents.Enqueue(e);
            }
            else
            {
                if (_logger.IsError) _logger.Error($"Shutter queue for eon {_eon + 1} is full, cannot load events.");
                return;
            }
        }
        else if (_logger.IsDebug)
        {
            _logger.Warn($"Ignoring Shutter event with future eon {e.Eon}.");
        }
    }

    public IEnumerable<ISequencerContract.TransactionSubmitted> DequeueToGasLimit(ulong eon, ulong txPointer)
    {
        UInt256 totalGas = 0;

        if (_eon is null || eon > _eon)
        {
            SetEon(eon);
        }

        if (eon != _eon)
        {
            if (_logger.IsError) _logger.Error($"Cannot load Shutter transactions for eon {eon}, expected {_eon}.");
            yield break;
        }

        while (_events.TryPeek(out ISequencerContract.TransactionSubmitted e))
        {
            if (e.TxIndex < txPointer)
            {
                // skip and delete outdated events
                _events.Dequeue();
                continue;
            }

            if (totalGas + e.GasLimit > encryptedGasLimit)
            {
                if (_logger.IsDebug) _logger.Debug("Shutter gas limit reached.");
                yield break;
            }

            _events.Dequeue();
            totalGas += e.GasLimit;
            yield return e;
        }

        Metrics.ShutterEncryptedGasUsed = (ulong)totalGas;
    }

    private void SetEon(ulong eon)
    {
        if (eon == _eon + 1)
        {
            _txIndex = _nextEonTxIndex;
            _events = _nextEonEvents;
            _nextEonTxIndex = 0;
            _nextEonEvents = [];
        }

        _eon = eon;
    }
}
