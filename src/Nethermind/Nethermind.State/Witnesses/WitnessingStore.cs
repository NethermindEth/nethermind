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
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.State.Witnesses
{
    public static class KeyValueStoreWithBatchingExtensions
    {
        public static IKeyValueStoreWithBatching WitnessedBy(
            this IKeyValueStoreWithBatching @this,
            IWitnessCollector witnessCollector, IDb stateDb, IDb codeDb)
        {
            return witnessCollector == NullWitnessCollector.Instance
                ? @this
                : new WitnessingStore(@this, witnessCollector, stateDb, codeDb);
        }
    }

    public class WitnessingStore : IKeyValueStoreWithBatching
    {
        private readonly IKeyValueStoreWithBatching _wrapped;
        private readonly IWitnessCollector _witnessCollector;
        private readonly IDb _stateDb;
        private readonly IDb _codeDb;

        public WitnessingStore(IKeyValueStoreWithBatching? wrapped, IWitnessCollector? witnessCollector, IDb stateDb,
            IDb codeDb)
        {
            _wrapped = wrapped ?? throw new ArgumentNullException(nameof(wrapped));
            _witnessCollector = witnessCollector ?? throw new ArgumentNullException(nameof(witnessCollector));
            _stateDb = stateDb ?? throw new ArgumentNullException(nameof(stateDb));
            _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
        }

        public byte[]? this[byte[] key]
        {
            get
            {
                if (key.Length != 32)
                {
                    throw new NotSupportedException($"{nameof(WitnessingStore)} requires 32 bytes long keys.");
                }
                
                Touch(key);
                if (_wrapped[key] is not null)
                {
                    return _stateDb.KeyExists(_wrapped[key])
                        ? _stateDb[_wrapped[key]]
                        : (_codeDb.KeyExists(_wrapped[key]) ? _codeDb[_wrapped[key]] : null);
                }

                return null;
            }
            set => _wrapped[key] = value;
        }

        public IBatch StartBatch()
        {
            return _wrapped.StartBatch();
        }

        public void Touch(byte[] key)
        {
            _witnessCollector.Add(new Keccak(key));
        }
    }
}
