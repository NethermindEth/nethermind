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
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62
{
    internal class TxFloodController
    {
        private DateTime _checkpoint = DateTime.UtcNow;
        private readonly Eth62ProtocolHandler _protocolHandler;
        private readonly ITimestamper _timestamper;
        private readonly ILogger _logger;
        private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(60);
        private long _notAcceptedSinceLastCheck;
        private readonly Random _random = new Random();
        
        internal bool IsDowngraded { get; private set; }

        public TxFloodController(Eth62ProtocolHandler protocolHandler, ITimestamper timestamper, ILogger logger)
        {
            _protocolHandler = protocolHandler ?? throw new ArgumentNullException(nameof(protocolHandler));
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void Report(bool accepted)
        {
            TryReset();
            
            if (!accepted)
            {
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
