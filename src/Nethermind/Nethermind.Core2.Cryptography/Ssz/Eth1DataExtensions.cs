// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Cortex.SimpleSerialize;
using Nethermind.Core2.Containers;

namespace Nethermind.Core2.Cryptography.Ssz
{
    public static class Eth1DataExtensions
    {
        public static SszContainer ToSszContainer(this Eth1Data item)
        {
            return new SszContainer(GetValues(item));
        }

        private static IEnumerable<SszElement> GetValues(Eth1Data item)
        {
            yield return item.DepositRoot.ToSszBasicVector();
            yield return item.DepositCount.ToSszBasicElement();
            yield return item.BlockHash.ToSszBasicVector();
        }
    }
}
