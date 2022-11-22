// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Cortex.SimpleSerialize;
using Nethermind.Core2.Containers;

namespace Nethermind.Core2.Cryptography.Ssz
{
    public static class CheckpointExtensions
    {
        public static SszContainer ToSszContainer(this Checkpoint item)
        {
            return new SszContainer(GetValues(item));
        }

        private static IEnumerable<SszElement> GetValues(Checkpoint item)
        {
            yield return item.Epoch.ToSszBasicElement();
            yield return item.Root.ToSszBasicVector();
        }
    }
}
