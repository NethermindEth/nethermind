// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Cortex.SimpleSerialize;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Cryptography.Ssz
{
    public static class Bytes32Extensions
    {
        public static SszBasicVector ToSszBasicVector(this Bytes32 item)
        {
            return new SszBasicVector(item.AsSpan());
        }

        public static SszVector ToSszVector(this IEnumerable<Bytes32> vector)
        {
            return new SszVector(vector.Select(x => ToSszBasicVector(x)));
        }
    }
}
