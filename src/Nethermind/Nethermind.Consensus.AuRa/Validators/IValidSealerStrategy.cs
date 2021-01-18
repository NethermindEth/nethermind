//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
