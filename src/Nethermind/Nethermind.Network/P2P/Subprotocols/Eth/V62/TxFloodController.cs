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
// 

using System;
using Nethermind.Logging;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62
{
    internal class TxFloodController
    {
        private DateTime _checkpoint = DateTime.UtcNow;
        private readonly Eth62ProtocolHandler _protocolHandler;
        private readonly ILogger _logger;
        private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(60);
        private long _notAcceptedSinceLastCheck;
        private readonly Random _random = new Random();
        
        internal bool IsDowngraded { get; private set; }

        public TxFloodController(Eth62ProtocolHandler protocolHandler, ILogger logger)
        {
            _protocolHandler = protocolHandler ?? throw new ArgumentNullException(nameof(protocolHandler));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void ReportNotAccepted()
        {
            DateTime now = DateTime.UtcNow;
            if (now >= _checkpoint + _checkInterval)
            {
                _checkpoint = now;
                _notAcceptedSinceLastCheck = 0;
            }
            
            _notAcceptedSinceLastCheck++;
            if (!IsDowngraded && _notAcceptedSinceLastCheck / _checkInterval.TotalSeconds > 10)
            {
                if (_logger.IsDebug) _logger.Debug($"Downgrading {_protocolHandler} due to tx flooding");
                IsDowngraded = true;
            }
            else if (_notAcceptedSinceLastCheck / _checkInterval.TotalSeconds > 100)
            {
                if (_logger.IsDebug) _logger.Debug($"Disconnecting {_protocolHandler} due to tx flooding");
                    _protocolHandler.Disconnect(
                        DisconnectReason.UselessPeer,
                        $"tx flooding {_notAcceptedSinceLastCheck}/{_checkInterval.TotalSeconds > 100}");
            }
        }
        
        public bool IsAllowed()
        {
            if (IsEnabled && (IsDowngraded || 10 < _random.Next(0, 99)))
            {
                // we only accept 10% of transactions from downgraded nodes
                return false;
            }

            return true;
        }

        public bool IsEnabled { get; set; } = true;
    }
}