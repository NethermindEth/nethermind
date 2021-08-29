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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Source;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.TxPool.Collections;

namespace Nethermind.AccountAbstraction.Broadcaster
{
    /// <summary>
    /// This class responsibility is to notify other peers about interesting user-operations.
    /// </summary>
    internal class UserOperationBroadcaster : IDisposable
    {
        /// <summary>
        /// Connected peers that can be notified about user operations.
        /// </summary>
        private readonly ConcurrentDictionary<PublicKey, IUserOperationPoolPeer> _peers = new();
        
        /// <summary>
        /// UserOperations published locally (initiated by this node users) or reorganised.
        /// </summary>
        private readonly SortedPool<UserOperation, UserOperation, Address> _userOperation;

        private readonly ILogger _logger;
        
        public UserOperationBroadcaster(IComparer<UserOperation> comparer,
            ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _userOperation = new UserOperationSortedPool(MemoryAllowance.MemPoolSize, comparer, logManager);
        }
        
        private void Notify(IUserOperationPoolPeer peer, UserOperation uop)
        {
            try
            {
                if (peer.SendNewUserOperation(uop))
                {
                    if (_logger.IsTrace) _logger.Trace($"Notified {peer} about a useroperation: {uop.Hash}");
                }
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"Failed to notify {peer} about a useroperation: {uop.Hash}", e);
            }
        }
        
        private void NotifySelectedPeers(UserOperation userOperation)
        {
            Task.Run(() =>
            {
                foreach ((_, IUserOperationPoolPeer peer) in _peers)
                {
                    Notify(peer, userOperation);
                }
            });
        }
        
        public void BroadcastOnce(UserOperation uop)
        {
            NotifySelectedPeers(uop);
        }

        public void Dispose()
        {
        }
    }
}
