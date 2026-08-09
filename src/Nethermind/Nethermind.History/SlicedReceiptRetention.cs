// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Db.LogIndex;

namespace Nethermind.History;

internal sealed class SlicedReceiptRetention(IFlatDbConfig flatDbConfig, ILogIndexStorage logIndexStorage)
{
    private readonly FrozenSet<Address> _addresses = ParseAddresses(flatDbConfig.HistorySliceAddresses);
    private readonly ILogIndexStorage _logIndexStorage = logIndexStorage;

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

        bool indexCoversBlock = _logIndexStorage.Enabled
            && _logIndexStorage.MinBlockNumber is { } min && block.Number >= (ulong)min
            && _logIndexStorage.MaxBlockNumber is { } max && block.Number <= (ulong)max;

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

            using IEnumerator<int> hits = _logIndexStorage.GetEnumerator(address, (int)block.Number, (int)block.Number);
            if (hits.MoveNext())
            {
                return true;
            }
        }

        return false;
    }

    private static FrozenSet<Address> ParseAddresses(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return FrozenSet<Address>.Empty;
        }

        HashSet<Address> addresses = [];
        foreach (Range tokenRange in raw.AsSpan().Split(','))
        {
            ReadOnlySpan<char> token = raw.AsSpan(tokenRange).Trim();
            if (token.IsEmpty)
            {
                continue;
            }

            int separatorIndex = token.IndexOf(':');
            ReadOnlySpan<char> addressToken = separatorIndex < 0 ? token : token[..separatorIndex];

            Address? address;
            try
            {
                if (!Address.TryParse(addressToken.ToString(), out address) || address is null)
                {
                    address = null;
                }
            }
            catch (Exception)
            {
                address = null;
            }

            if (address is null)
            {
                throw new InvalidConfigurationException(
                    $"Flat.HistorySliceAddresses entry '{token}' has address '{addressToken}', which is not a valid address.", -1);
            }

            addresses.Add(address);
        }

        return addresses.ToFrozenSet();
    }
}
