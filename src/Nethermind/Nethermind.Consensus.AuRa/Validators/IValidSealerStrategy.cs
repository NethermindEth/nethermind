// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Consensus.AuRa.Validators
{
    public interface IValidSealerStrategy
    {
        /// <summary>
        /// Checks if <see cref="address"/> is a valid sealer for step <see cref="step"/> for <see cref="validators"/> collection.
        /// </summary>
        /// <param name="validators">Validators at given step.</param>
        /// <param name="address">Address to be checked if its a sealer at this step.</param>
        /// <param name="step">Step to be checked.</param>
        /// <returns>'true' if <see cref="address"/> should seal a block at <see cref="step"/> for supplied <see cref="validators"/> collection. Otherwise 'false'.</returns>
        bool IsValidSealer(IList<Address> validators, Address address, long step);
    }
}
