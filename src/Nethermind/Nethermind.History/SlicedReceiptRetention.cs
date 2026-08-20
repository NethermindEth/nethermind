// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Db.LogIndex;

namespace Nethermind.History;

internal sealed class SlicedReceiptRetention(IFlatDbConfig flatDbConfig, ILogIndexStorage logIndexStorage)
{
    private readonly FrozenSet<Address> _addresses = ParseAddresses(flatDbConfig.HistorySliceAddresses);

    public bool ShouldRetainReceipts(Block block)
    {
        if (_addresses.Count == 0)
        {
            return false;
        }

        Bloom? bloom = block.Header.Bloom;
        if (bloom is null)
        {
            return false;
        }

        bool indexCoversBlock = logIndexStorage.Enabled
            && logIndexStorage.MinBlockNumber is { } min && block.Number >= (ulong)min
            && logIndexStorage.MaxBlockNumber is { } max && block.Number <= (ulong)max;

        foreach (Address address in _addresses)
        {
            if (!bloom.Matches(address))
            {
                continue;
            }

            if (!indexCoversBlock)
            {
                return true;
            }

            using IEnumerator<int> hits = logIndexStorage.GetEnumerator(address, (int)block.Number, (int)block.Number);
            if (hits.MoveNext())
            {
                return true;
            }
        }

        return false;
    }

    private static FrozenSet<Address> ParseAddresses(string? raw) =>
        SliceScopeConfig.Parse(raw).Select(static entry => entry.Address).ToFrozenSet();
}
