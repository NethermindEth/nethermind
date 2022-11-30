// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Cortex.SimpleSerialize;
using Nethermind.Core2.Containers;

namespace Nethermind.Core2.Cryptography.Ssz
{
    public static class AttesterSlashingExtensions
    {
        public static SszContainer ToSszContainer(this AttesterSlashing item, ulong maximumValidatorsPerCommittee)
        {
            return new SszContainer(GetValues(item, maximumValidatorsPerCommittee));
        }

        public static SszList ToSszList(this IEnumerable<AttesterSlashing> list, ulong limit, ulong maximumValidatorsPerCommittee)
        {
            return new SszList(list.Select(x => ToSszContainer(x, maximumValidatorsPerCommittee)), limit);
        }

        private static IEnumerable<SszElement> GetValues(AttesterSlashing item, ulong maximumValidatorsPerCommittee)
        {
            yield return item.Attestation1.ToSszContainer(maximumValidatorsPerCommittee);
            yield return item.Attestation2.ToSszContainer(maximumValidatorsPerCommittee);
        }
    }
}
