// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Consensus.AuRa.Validators
{
    public class ValidSealerStrategy : IValidSealerStrategy
    {
        public bool IsValidSealer(IList<Address> validators, Address address, long step, out Address expectedAddress) =>
            (expectedAddress = validators.GetItemRoundRobin(step)) == address;
    }
}
