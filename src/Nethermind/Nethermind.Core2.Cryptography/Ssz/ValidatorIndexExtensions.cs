// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Cortex.SimpleSerialize;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Cryptography.Ssz
{
    public static class ValidatorIndexExtensions
    {
        public static SszElement ToSszBasicElement(this ValidatorIndex item)
        {
            return new SszBasicElement((ulong)item);
        }

        public static SszBasicList ToSszBasicList(this IEnumerable<ValidatorIndex> list, ulong limit)
        {
            return new SszBasicList(list.Cast<ulong>().ToArray(), limit);
        }
    }
}
