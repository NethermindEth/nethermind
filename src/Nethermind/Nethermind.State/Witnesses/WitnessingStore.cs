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

        public byte[]? this[ReadOnlySpan<byte> key]
        {
            get => Get(key);
            set => Set(key, value);
        }

        public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            if (key.Length != 32)
            {
                throw new NotSupportedException($"{nameof(WitnessingStore)} requires 32 bytes long keys.");
            }

            Touch(key);
            return _wrapped.Get(key, flags);
        }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            _wrapped.Set(key, value, flags);
        }

        public void DeleteByRange(Span<byte> startKey, Span<byte> endKey)
        {
            _wrapped.DeleteByRange(startKey, endKey);
        }

        public IBatch StartBatch()
        {
            return _wrapped.StartBatch();
        }

        public void Touch(ReadOnlySpan<byte> key)
        {
            _witnessCollector.Add(new Keccak(key));
        }
    }
}
