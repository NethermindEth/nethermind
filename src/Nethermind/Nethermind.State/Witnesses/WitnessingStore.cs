// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Witnesses
{
    public static class KeyValueStoreWithBatchingExtensions
    {
        public static IKeyValueStoreWithBatching WitnessedBy(
            this IKeyValueStoreWithBatching @this,
            IWitnessCollector witnessCollector)
        {
            return witnessCollector == NullWitnessCollector.Instance
                ? @this
                : new WitnessingStore(@this, witnessCollector);
        }
    }

    public class WitnessingStore : IKeyValueStoreWithBatching
    {
        private readonly IKeyValueStoreWithBatching _wrapped;
        private readonly IWitnessCollector _witnessCollector;

        public WitnessingStore(IKeyValueStoreWithBatching? wrapped, IWitnessCollector? witnessCollector)
        {
            _wrapped = wrapped ?? throw new ArgumentNullException(nameof(wrapped));
            _witnessCollector = witnessCollector ?? throw new ArgumentNullException(nameof(witnessCollector));
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
                return _wrapped[key];
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
