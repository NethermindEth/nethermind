// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core
{
    public interface IKeyValueStoreWithBatching : IKeyValueStore
    {
        IBatch StartBatch();
        void DeleteByRange(Span<byte> startKey, Span<byte> endKey);
    }
}
