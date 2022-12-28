// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Cortex.SimpleSerialize;
using Nethermind.Core2.Containers;

namespace Nethermind.Core2.Cryptography.Ssz
{
    public static class DepositExtensions
    {
        public static SszContainer ToSszContainer(this Deposit item)
        {
            return new SszContainer(GetValues(item));
        }

        public static SszList ToSszList(this IEnumerable<Deposit> list, ulong limit)
        {
            return new SszList(list.Select(x => ToSszContainer(x)), limit);
        }

        private static IEnumerable<SszElement> GetValues(Deposit item)
        {
            // TODO: vector of byte arrays
            //yield return new SszVector(item.Proof.AsSpan());
            throw new InvalidOperationException();
            // yield return item.Data.ToSszContainer();
        }
    }
}
