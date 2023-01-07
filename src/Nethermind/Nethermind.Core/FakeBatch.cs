// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core
{
    public class FakeBatch : IBatch
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
            GC.SuppressFinalize(this);
        }

        public byte[]? this[byte[] key]
        {
            get => _storePretendingToSupportBatches[key];
            set => _storePretendingToSupportBatches[key] = value;
        }
    }
}
