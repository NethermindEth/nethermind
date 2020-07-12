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
using System.Threading;
using System.Timers;
using Nethermind.Logging;
using Nethermind.Stats.Model;
using Timer = System.Timers.Timer;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62
{
    internal class TxFloodController : IDisposable
    {
        private readonly Eth62ProtocolHandler _protocolHandler;
        private readonly ILogger _logger;
        private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(60);
        private readonly Timer _txFloodCheckTimer;
        private bool isDowngraded;
        private long _notAcceptedSinceLastCheck;
        private readonly Random _random = new Random();
        private int _disposed;

        public TxFloodController(Eth62ProtocolHandler protocolHandler, ILogger logger)
        {
            _protocolHandler = protocolHandler ?? throw new ArgumentNullException(nameof(protocolHandler));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _txFloodCheckTimer = new Timer(_checkInterval.TotalMilliseconds);
            _txFloodCheckTimer.Elapsed += CheckTxFlooding;
            _txFloodCheckTimer.Start();
        }

        private void CheckTxFlooding(object sender, ElapsedEventArgs e)
        {
            if (_protocolHandler.Session.IsClosing)
            {
                Dispose();
            }
            else
            {
                if (!isDowngraded && _notAcceptedSinceLastCheck / _checkInterval.TotalSeconds > 10)
                {
                    if (_logger.IsDebug) _logger.Debug($"Downgrading {_protocolHandler} due to tx flooding");
                    isDowngraded = true;
                }
                else
                {
                    if (_notAcceptedSinceLastCheck / _checkInterval.TotalSeconds > 100)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Disconnecting {_protocolHandler} due to tx flooding");
                        _protocolHandler.InitiateDisconnect(
                            DisconnectReason.UselessPeer,
                            $"tx flooding {_notAcceptedSinceLastCheck}/{_checkInterval.TotalSeconds > 100}");
                    }
                }   
                
                _notAcceptedSinceLastCheck = 0;
            }
        }

        public void ReportNotAccepted()
        {
            _notAcceptedSinceLastCheck++;
        }
        
        public bool IsAllowed()
        {
            if (IsEnabled && (isDowngraded || 10 < _random.Next(0, 99)))
            {
                // we only accept 10% of transactions from downgraded nodes
                return false;
            }

            return true;
        }
        
        public bool IsEnabled { get; set; }

        public void Dispose()
        {
            if (Interlocked.Increment(ref _disposed) == 1)
            {
                _txFloodCheckTimer.Elapsed -= CheckTxFlooding;
                _txFloodCheckTimer?.Dispose();
            }
        }
    }
}