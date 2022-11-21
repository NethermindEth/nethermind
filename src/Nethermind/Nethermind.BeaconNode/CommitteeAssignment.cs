// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode
{
    public class CommitteeAssignment
    {
        public static CommitteeAssignment None =
            new CommitteeAssignment(new ValidatorIndex[0], CommitteeIndex.Zero, Slot.Zero);

        private readonly List<ValidatorIndex> _committee;

        public CommitteeAssignment(IEnumerable<ValidatorIndex> committee, CommitteeIndex committeeIndex, Slot slot)
        {
            _committee = new List<ValidatorIndex>(committee);
            CommitteeIndex = committeeIndex;
            Slot = slot;
        }

        public IReadOnlyList<ValidatorIndex> Committee => _committee;

        public CommitteeIndex CommitteeIndex { get; }
        public Slot Slot { get; }
    }
}
