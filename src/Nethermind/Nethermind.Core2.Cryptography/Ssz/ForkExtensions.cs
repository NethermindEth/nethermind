// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Cortex.SimpleSerialize;
using Nethermind.Core2.Containers;

namespace Nethermind.Core2.Cryptography.Ssz
{
    public static class ForkExtensions
    {
        public static SszContainer ToSszContainer(this Fork item)
        {
            return new SszContainer(GetValues(item));
        }

        private static IEnumerable<SszElement> GetValues(Fork item)
        {
            yield return item.PreviousVersion.ToSszBasicVector();
            yield return item.CurrentVersion.ToSszBasicVector();
            // Epoch of latest fork
            yield return item.Epoch.ToSszBasicElement();
        }
    }
}
