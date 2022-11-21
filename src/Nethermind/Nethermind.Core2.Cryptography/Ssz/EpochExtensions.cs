// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Cortex.SimpleSerialize;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Cryptography.Ssz
{
    public static class EpochExtensions
    {
        public static Root HashTreeRoot(this Epoch item)
        {
            var tree = new SszTree(item.ToSszBasicElement());
            return new Root(tree.HashTreeRoot());
        }

        public static SszElement ToSszBasicElement(this Epoch item)
        {
            return new SszBasicElement((ulong)item);
        }
    }
}
