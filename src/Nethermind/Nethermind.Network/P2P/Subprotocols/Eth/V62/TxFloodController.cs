// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62
{
    internal class TxFloodController(Eth62ProtocolHandler protocolHandler, ITimestamper timestamper, ILogger logger)
    {
        private DateTime _checkpoint = DateTime.UtcNow;
        private readonly Eth62ProtocolHandler _protocolHandler = protocolHandler ?? throw new ArgumentNullException(nameof(protocolHandler));
        private readonly ITimestamper _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
        private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(60);
        private long _notAcceptedSinceLastCheck;
        private readonly Random _random = new();

        internal bool IsDowngraded { get; private set; }

        public void Report(bool accepted)
        {
            TryReset();

            if (!accepted)
            {
                _notAcceptedSinceLastCheck++;
                if (!IsDowngraded && _notAcceptedSinceLastCheck / _checkInterval.TotalSeconds > 10)
                {
                    if (logger.IsDebug) logger.Debug($"Downgrading {_protocolHandler} due to tx flooding");
                    IsDowngraded = true;
                }
                else if (_notAcceptedSinceLastCheck / _checkInterval.TotalSeconds > 100)
                {
                    if (logger.IsDebug) logger.Debug($"Disconnecting {_protocolHandler} due to tx flooding");
                    _protocolHandler.Disconnect(
                        DisconnectReason.TxFlooding,
                        $"tx flooding {_notAcceptedSinceLastCheck}/{_checkInterval.TotalSeconds > 100}");
                }
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
}
