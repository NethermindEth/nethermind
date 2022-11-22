// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Cortex.SimpleSerialize;
using Nethermind.Core2.Containers;

namespace Nethermind.Core2.Cryptography.Ssz
{
    public static class IndexedAttestationExtensions
    {
        public static SszContainer ToSszContainer(this IndexedAttestation item, ulong maximumValidatorsPerCommittee)
        {
            return new SszContainer(GetValues(item, maximumValidatorsPerCommittee));
        }

        private static IEnumerable<SszElement> GetValues(IndexedAttestation item, ulong maximumValidatorsPerCommittee)
        {
            yield return item.AttestingIndices.ToSszBasicList(maximumValidatorsPerCommittee);
            yield return item.Data.ToSszContainer();
            yield return item.Signature.ToSszBasicVector();
        }
    }
}
