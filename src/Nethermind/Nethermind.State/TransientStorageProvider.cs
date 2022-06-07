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

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Resettables;
using Nethermind.Logging;

namespace Nethermind.State
{
    /// <summary>
    /// EIP-1153 provides a transient store for contracts that doesn't persist
    /// storage across calls. Reverts will rollback any transient state changes.
    /// </summary>
    public class TransientStorageProvider : PartialStorageProviderBase, IPartialStorageProvider
    {
        public TransientStorageProvider(ILogManager? logManager)
            : base(logManager) { }

        private Keccak RecalculateRootHash(Address address)
        {
            throw new NotImplementedException();
        }
        public override void CommitTrees(long blockNumber)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Nothing to commit to permanent storage
        /// Reset the caches and return
        /// </summary>
        /// <param name="tracer"></param>
        /// <exception cref="InvalidOperationException"></exception>
        public override void Commit(IStorageTracer tracer)
        {
            if (_currentPosition == -1)
            {
                if (_logger.IsTrace) _logger.Trace("No storage changes to commit");
                return;
            }

            if (_logger.IsTrace) _logger.Trace("Committing transient storage changes");

            Resettable<Change>.Reset(ref _changes, ref _capacity, ref _currentPosition, StartCapacity);
            _committedThisRound.Reset();
            _intraBlockCache.Reset();
            _originalValues.Reset();
            _transactionChangesSnapshots.Clear();
        }

        protected override byte[] GetCurrentValue(StorageCell storageCell)
        {
            if (_intraBlockCache.TryGetValue(storageCell, out StackList<int> stack))
            {
                int lastChangeIndex = stack.Peek();
                return _changes[lastChangeIndex]!.Value;
            }

            return _zeroValue;
        }

        public override void ClearStorage(Address address)
        {
            /* we are setting cached values to zero so we do not use previously set values
               when the contract is revived with CREATE2 inside the same block */
            foreach (var cellByAddress in _intraBlockCache)
            {
                if (cellByAddress.Key.Address == address)
                {
                    Set(cellByAddress.Key, _zeroValue);
                }
            }
        }
    }
}
