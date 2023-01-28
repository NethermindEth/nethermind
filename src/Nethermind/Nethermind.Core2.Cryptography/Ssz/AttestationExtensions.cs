// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Cortex.SimpleSerialize;
using Nethermind.Core2.Containers;

namespace Nethermind.Core2.Cryptography.Ssz
{
    public static class AttestationExtensions
    {
        public static SszContainer ToSszContainer(this Attestation item, ulong maximumValidatorsPerCommittee)
        {
            return new SszContainer(GetValues(item, maximumValidatorsPerCommittee));
        }

        public static SszList ToSszList(this IEnumerable<Attestation> list, ulong limit, ulong maximumValidatorsPerCommittee)
        {
            return new SszList(list.Select(x => ToSszContainer(x, maximumValidatorsPerCommittee)), limit);
        }

        private static IEnumerable<SszElement> GetValues(Attestation item, ulong maximumValidatorsPerCommittee)
        {
            yield return item.AggregationBits.ToSszBitlist(maximumValidatorsPerCommittee);
            yield return item.Data.ToSszContainer();
            yield return item.Signature.ToSszBasicVector();
        }
    }
}
