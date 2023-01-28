// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core2.Types;

namespace Nethermind.Core2.Configuration
{
    public class SignatureDomains
    {
        public DomainType BeaconAttester { get; set; }
        public DomainType BeaconProposer { get; set; }
        public DomainType Deposit { get; set; }
        public DomainType Randao { get; set; }
        public DomainType VoluntaryExit { get; set; }
    }
}
