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

namespace Nethermind.Core.Potocol
{
    /// <summary>
    /// https://github.com/ethereum/EIPs
    /// </summary>
    public interface IEthereumRelease
    {
        bool IsTimeAdjustmentPostOlympic { get; }
        bool AreJumpDestinationsUsed { get; }

        /// <summary>
        /// Contract creation via transaction cost set to 21000 + 32000 (previously 21000)
        /// Failing init does not create an empty code contract
        /// Difficulty adjustment changed
        /// Transaction signature uniqueness (s-value has to be less or equal than than secp256k1n/2)
        /// </summary>
        bool IsEip2Enabled { get; }

        /// <summary>
        /// DELEGATECALL instruction added
        /// </summary>
        bool IsEip7Enabled { get; }

        /// <summary>
        /// Change difficulty adjustment to target mean block time including uncles
        /// </summary>
        bool IsEip100Enabled { get; }

        /// <summary>
        /// REVERT instruction in the Ethereum Virtual Machine
        /// </summary>
        bool IsEip140Enabled { get; }

        /// <summary>
        /// Gas cost of IO operations increased
        /// </summary>
        bool IsEip150Enabled { get; }

        /// <summary>
        /// Chain ID in signatures (replay attack protection)
        /// </summary>
        bool IsEip155Enabled { get; }

        /// <summary>
        /// State clearing
        /// </summary>
        bool IsEip158Enabled { get; }

        /// <summary>
        /// EXP cost increase
        /// </summary>
        bool IsEip160Enabled { get; }

        /// <summary>
        /// Code size limit
        /// </summary>
        bool IsEip170Enabled { get; }

        /// <summary>
        /// Precompiled contracts for addition and scalar multiplication on the elliptic curve alt_bn128
        /// </summary>
        bool IsEip196Enabled { get; }

        /// <summary>
        /// Precompiled contracts for optimal ate pairing check on the elliptic curve alt_bn128
        /// </summary>
        bool IsEip197Enabled { get; }

        /// <summary>
        /// Precompiled contract for bigint modular exponentiation
        /// </summary>
        bool IsEip198Enabled { get; }

        /// <summary>
        /// New opcodes: RETURNDATASIZE and RETURNDATACOPY
        /// </summary>
        bool IsEip211Enabled { get; }

        /// <summary>
        /// New opcode STATICCALL
        /// </summary>
        bool IsEip214Enabled { get; }

        /// <summary>
        /// Difficulty Bomb Delay and Block Reward Reduction
        /// Block reward reduced to 3ETH
        /// </summary>
        bool IsEip649Enabled { get; }

        /// <summary>
        /// Embedding transaction return data in receipts
        /// </summary>
        bool IsEip658Enabled { get; }
    }
}