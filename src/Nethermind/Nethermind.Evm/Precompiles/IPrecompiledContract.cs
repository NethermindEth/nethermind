/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles
{
    public interface IPrecompiledContract
    {
        /// <summary>
        /// Address at which the precompiled contract is deployed. Not that this, at the moment is a property of the precompiled contract definition.
        /// It may be considered to allow given <see cref="IPrecompiledContract"/> to be deployed at any address.
        /// </summary>
        /// <param name="releaseSpec">The network release spec that needs to be used in case of the change of the cost calculation algorithm between the versions.</param>
        /// <returns>Base gas cost of a single call to this precompiled contract on top of the standard CALL gas cost.</returns>
        Address Address { get; }

        /// <summary>
        /// This gas cost will be deducted independently of the input data size.
        /// </summary>
        /// <param name="releaseSpec">Network release spec in case of the change of the cost calculation algorithm between the versions.</param>
        /// <returns>Static/data-independent part of the gas cost of a single call to this precompiled contract on top of the standard CALL gas cost.</returns>
        long BaseGasCost(IReleaseSpec releaseSpec);

        /// <summary>
        /// The part of the gas cost that is dependent on the type and size of the input data.
        /// </summary>
        /// <param name="inputData">Input data that needs to be inspected for the dynamic gas cost calculation.</param>
        /// <param name="releaseSpec">The network release spec that needs to be used in case of the change of the cost calculation algorithm between the versions.</param>
        /// <returns>Dynamic/data-dependent part of the gas cost of a single call to this precompile on top of the standard CALL gas cost and the base cost of this precompiled contract.</returns>
        long DataGasCost(byte[] inputData, IReleaseSpec releaseSpec);

        /// <summary>
        /// Executes the code of the precompiled contract.
        /// </summary>
        /// <param name="inputData">Input data to be used by the precompiled contract algorithm.</param>
        /// <returns>A tuple with the result bytes and success code. Etheruem return '1' for success and '0' for failure.</returns>
        (byte[], bool) Run(byte[] inputData);
    }
}