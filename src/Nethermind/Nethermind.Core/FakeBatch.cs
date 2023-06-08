// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

using Nethermind.Core.Crypto;

namespace Nethermind.Core
{
    public class FakeBatch : IBatch, ISpanKeyBatch
    {
        private readonly IKeyValueStore _storePretendingToSupportBatches;

        private readonly Action? _onDispose;

        public FakeBatch(IKeyValueStore storePretendingToSupportBatches)
            : this(storePretendingToSupportBatches, null)
        {
        }

        public FakeBatch(IKeyValueStore storePretendingToSupportBatches, Action? onDispose)
        {
            _storePretendingToSupportBatches = storePretendingToSupportBatches;
            _onDispose = onDispose;
        }

        public void Dispose()
        {
            _onDispose?.Invoke();
        }

        public byte[]? Get(in ValueKeccak key, ReadFlags flags = ReadFlags.None)
        {
            return _storePretendingToSupportBatches.Get(key.Bytes, flags);
        }

        public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            return _storePretendingToSupportBatches.Get(key, flags);
        }

        public void Set(in ValueKeccak key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            _storePretendingToSupportBatches.Set(key.Bytes, value, flags);
        }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            _storePretendingToSupportBatches.Set(key, value, flags);
        }
    }
}
