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

using System.Collections.Generic;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core.Specs.ChainSpecStyle
{
    /// <summary>
    ///     "stepDuration": 5,
    ///     "blockReward": "0xDE0B6B3A7640000",
    ///     "maximumUncleCountTransition": 0,
    ///     "maximumUncleCount": 0,
    ///     "validators": {
    /// "multi": {
    ///     "0": {
    ///         "safeContract": "0x8bf38d4764929064f2d4d3a56520a76ab3df415b"
    ///     },
    ///     "362296": {
    ///         "safeContract": "0xf5cE3f5D0366D6ec551C74CCb1F67e91c56F2e34"
    ///     },
    ///     "509355": {
    ///         "safeContract": "0x03048F666359CFD3C74a1A5b9a97848BF71d5038"
    ///     },
    ///     "4622420": {
    ///         "safeContract": "0x4c6a159659CCcb033F4b2e2Be0C16ACC62b89DDB"
    ///     }
    /// }
    /// },
    /// "blockRewardContractAddress": "0x3145197AD50D7083D0222DE4fCCf67d9BD05C30D",
    /// "blockRewardContractTransition": 4639000
    /// </summary>
    public class AuRaParameters
    {
        public int StepDuration { get; set; }

        public UInt256 BlockReward { get; set; }

        public long MaximumUncleCountTransition { get; set; }
        
        public int MaximumUncleCount { get; set; }
        
        public Address BlockRewardContractAddress { get; set; }
        
        public long BlockRewardContractTransition { get; set; }
        
        public Validator Validators { get; set; }
        
        public enum ValidatorType
        {
            List,
            Contract,
            ReportingContract,
            Multi
        }

        public class Validator
        {
            public ValidatorType ValidatorType { get; set; }
        
            /// <summary>
            /// Dictionary of Validators per their starting block.
            /// </summary>
            /// <remarks>
            /// Only Valid for <seealso cref="ValidatorType"/> of type <see cref="AuRaParameters.ValidatorType.Multi"/>.
            /// 
            /// This has to sorted in order of starting blocks.
            /// </remarks>
            public IDictionary<long, Validator> Validators { get; set; }

            /// <summary>
            /// Addresses for validator.
            /// </summary>
            /// <remarks>
            /// For <seealso cref="ValidatorType"/> of type <see cref="AuRaParameters.ValidatorType.Contract"/> and <see cref="AuRaParameters.ValidatorType.ReportingContract"/> we expect only one address.
            /// For <seealso cref="ValidatorType"/> of type <see cref="AuRaParameters.ValidatorType.Multi"/> will be empty.
            /// </remarks>
            public Address[] Addresses { get; set; }
        }
    }
}