// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Stats.Model;
using Nethermind.TxPool;
using System;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62;

public class FloodController(SyncPeerProtocolHandlerBase protocolHandler, ITimestamper timestamper, ILogger logger)
{
    private DateTime _checkpoint = DateTime.UtcNow;
    private readonly SyncPeerProtocolHandlerBase _protocolHandler = protocolHandler ?? throw new ArgumentNullException(nameof(protocolHandler));
    private readonly ITimestamper _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
    private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(60);
    private long _notAcceptedSinceLastCheck;
    private readonly Random _random = new();

    internal bool IsDowngraded { get; private set; }

    public void Report(FloodLevel floodLevel)
    {
        TryReset();

        switch (floodLevel)
        {
            case FloodLevel.Fatal:
                if (logger.IsDebug) logger.Debug($"Disconnecting {_protocolHandler} due to invalid tx received");
                _protocolHandler.Disconnect(DisconnectReason.Other, $"invalid request or response");
                return;
            case FloodLevel.Warning:
                _notAcceptedSinceLastCheck++;
                if (!IsDowngraded && _notAcceptedSinceLastCheck / _checkInterval.TotalSeconds > 10)
                {
                    if (logger.IsDebug) logger.Debug($"Downgrading {_protocolHandler} due to flooding");
                    IsDowngraded = true;
                }
                else if (_notAcceptedSinceLastCheck / _checkInterval.TotalSeconds > 100)
                {
                    if (logger.IsDebug) logger.Debug($"Disconnecting {_protocolHandler} due to flooding");
                    _protocolHandler.Disconnect(
                        DisconnectReason.TxFlooding,
                        $"tx flooding {_notAcceptedSinceLastCheck}/{_checkInterval.TotalSeconds > 100}");
                }
                return;
        }
    }

    private void TryReset()
    {
        DateTime now = _timestamper.UtcNow;
        if (now >= _checkpoint + _checkInterval)
        {
            _checkpoint = now;
            _notAcceptedSinceLastCheck = 0;
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

public enum FloodLevel
{
    Correct,
    Warning,
    Fatal
}

internal static class FloodControllerExtensions
{
    public static void Report(this FloodController floodController, AcceptTxResult acceptTxResult) =>
        floodController.Report(
            acceptTxResult == AcceptTxResult.Accepted ? FloodLevel.Correct :
            acceptTxResult == AcceptTxResult.Invalid ? FloodLevel.Fatal :
            FloodLevel.Warning);
}
