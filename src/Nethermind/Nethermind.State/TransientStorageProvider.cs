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

using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.State
{
    /// <summary>
    /// EIP-1153 provides a transient store for contracts that doesn't persist
    /// storage across calls. Reverts will rollback any transient state changes.
    /// </summary>
    public class TransientStorageProvider : PartialStorageProviderBase
    {
        public TransientStorageProvider(ILogManager? logManager)
            : base(logManager) { }

        /// <summary>
        /// Get the storage value at the specified storage cell
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <returns>Value at cell</returns>
        protected override byte[] GetCurrentValue(StorageCell storageCell) =>
            TryGetCachedValue(storageCell, out byte[]? bytes) ? bytes! : _zeroValue;
    }
}
