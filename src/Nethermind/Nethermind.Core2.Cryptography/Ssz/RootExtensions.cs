// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Cortex.SimpleSerialize;
using Nethermind.Core2.Crypto;

namespace Nethermind.Core2.Cryptography.Ssz
{
    public static class RootExtensions
    {
        public static SszBasicVector ToSszBasicVector(this Root item)
        {
            return new SszBasicVector(item.AsSpan());
        }

        public static SszList ToSszList(this IEnumerable<Root> list, ulong limit)
        {
            return new SszList(list.Select(x => ToSszBasicVector(x)), limit);
        }

        public static SszVector ToSszVector(this IEnumerable<Root> vector)
        {
            return new SszVector(vector.Select(x => ToSszBasicVector(x)));
        }
    }
}
