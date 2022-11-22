// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Cortex.SimpleSerialize;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;

namespace Nethermind.Core2.Cryptography.Ssz
{
    public static class AttestationDataExtensions
    {
        public static Root HashTreeRoot(this AttestationData item)
        {
            var tree = new SszTree(item.ToSszContainer());
            return new Root(tree.HashTreeRoot());
        }

        public static SszContainer ToSszContainer(this AttestationData item)
        {
            return new SszContainer(GetValues(item));
        }

        private static IEnumerable<SszElement> GetValues(AttestationData item)
        {
            yield return item.Slot.ToSszBasicElement();
            yield return item.Index.ToSszBasicElement();
            // LMD GHOST vote
            yield return item.BeaconBlockRoot.ToSszBasicVector();
            // FFG vote
            yield return item.Source.ToSszContainer();
            yield return item.Target.ToSszContainer();
        }
    }
}
