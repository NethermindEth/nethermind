// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Cortex.SimpleSerialize;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Cryptography.Ssz
{
    public static class ShardExtensions
    {
        public static SszElement ToSszBasicElement(this Shard item)
        {
            return new SszBasicElement((ulong)item);
        }
    }
}
