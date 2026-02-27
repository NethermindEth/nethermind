// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Stats.Model;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62;

internal class TxFloodController(Eth62ProtocolHandler protocolHandler, ITimestamper timestamper, ILogger logger)
{
    // Late with one block
    private const int DegradeRatePerMinute = 250;
    // Late with 5 blocks (and has all included txs in their pool)
    private const int DisconnectRatePerMinute = 750;

    private DateTime _checkpoint = DateTime.UtcNow;
    private readonly Eth62ProtocolHandler _protocolHandler = protocolHandler ?? throw new ArgumentNullException(nameof(protocolHandler));
    private readonly ITimestamper _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
    private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(60);
    private long _notAcceptedSinceLastCheck;
    private readonly Random _random = new();

    internal bool IsDowngraded { get; private set; }

    public void Report(in AcceptTxResult accepted)
    {
        TryReset();

        if (!accepted)
        {
            if (accepted.Id == TxResultCode.Syncing)
            {
                // Do not count rejections while we are syncing
                return;
            }

            if (accepted is
                {
                    Id:
                    TxResultCode.Invalid or
                    TxResultCode.GasLimitExceeded or
                    TxResultCode.MaxTxSizeExceeded or
                    TxResultCode.Int256Overflow or
                    TxResultCode.SenderIsContract or
                    TxResultCode.FailedToResolveSender or
                    TxResultCode.NotSupportedTxType
                })
            {
                if (logger.IsDebug) logger.Debug($"Disconnecting {_protocolHandler} due to invalid tx received");
                _protocolHandler.Disconnect(DisconnectReason.InvalidTx, accepted.Id.ToString());
                return;
            }

            TimeSpan timeDiff = _timestamper.UtcNow - _checkpoint;
            double rejectRatePerMinute = _notAcceptedSinceLastCheck / _checkInterval.TotalSeconds;
            _notAcceptedSinceLastCheck++;
            if (!IsDowngraded && rejectRatePerMinute > DegradeRatePerMinute)
            {
                if (logger.IsDebug) logger.Debug($"Downgrading {_protocolHandler} due to tx flooding");
                IsDowngraded = true;
            }
            else if (rejectRatePerMinute > DisconnectRatePerMinute)
            {
                if (logger.IsDebug) logger.Debug($"Disconnecting {_protocolHandler} due to tx flooding");
                _protocolHandler.Disconnect(
                    DisconnectReason.TxFlooding,
                    $"Tx flooding: Rejected {_notAcceptedSinceLastCheck} in {timeDiff.TotalSeconds:f3}sec");
            }
        }
    }

    private void TryReset()
    {
        DateTime now = _timestamper.UtcNow;
        if (now >= _checkpoint + _checkInterval)
        {
            _notAcceptedSinceLastCheck = 0;
            _checkpoint = now;
            IsDowngraded = false;
        }
    }

    public bool IsAllowed()
    {
        TryReset();
        return !(IsEnabled && IsDowngraded && 10 < _random.Next(0, 99));
    }

    public bool IsEnabled { get; set; } = true;
}
