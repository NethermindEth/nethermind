// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Consensus.AuRa.Validators
{
    internal static class AuRaValidatorsCollectionExtensions
    {
        public static int MinSealersForFinalization(this IList<Address> validators, bool twoThirds = false)
        {
            return (twoThirds ? validators.Count * 2 / 3 : validators.Count / 2) + 1;
        }
    }
}
