// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core
{
    public class FakeWriteBatch : IWriteBatch
    {
        private readonly IKeyValueStore _storePretendingToSupportBatches;

        private readonly Action? _onDispose;

        public FakeWriteBatch(IKeyValueStore storePretendingToSupportBatches)
            : this(storePretendingToSupportBatches, null)
        {
        }

        public FakeWriteBatch(IKeyValueStore storePretendingToSupportBatches, Action? onDispose)
        {
            _storePretendingToSupportBatches = storePretendingToSupportBatches;
            _onDispose = onDispose;
        }

        public void Dispose()
        {
            _onDispose?.Invoke();
        }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            _storePretendingToSupportBatches.Set(key, value, flags);
        }
    }
}
