// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Cortex.SimpleSerialize;
using Nethermind.Core2.Containers;

namespace Nethermind.Core2.Cryptography.Ssz
{
    public static class ProposerSlashingExtensions
    {
        public static SszContainer ToSszContainer(this ProposerSlashing item)
        {
            return new SszContainer(GetValues(item));
        }

        public static SszList ToSszList(this IEnumerable<ProposerSlashing> list, ulong limit)
        {
            return new SszList(list.Select(x => ToSszContainer(x)), limit);
        }

        private static IEnumerable<SszElement> GetValues(ProposerSlashing item)
        {
            yield return item.ProposerIndex.ToSszBasicElement();
            yield return item.SignedHeader1.ToSszContainer();
            yield return item.SignedHeader2.ToSszContainer();
        }
    }
}
