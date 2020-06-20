//  Copyright (c) 2018 Demerzel Solutions Limited
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

using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core.Specs
{
    /// <summary>
    /// https://github.com/ethereum/EIPs
    /// </summary>
    public interface IReleaseSpec
    {
        long MaximumExtraDataSize { get; }
        long MaxCodeSize { get; }
        long MinGasLimit { get; }
        long GasLimitBoundDivisor { get; }
        UInt256 BlockReward { get; }
        long DifficultyBombDelay { get; }
        long DifficultyBoundDivisor { get; }
        int MaximumUncleCount { get; }
        
        /// <summary>
        /// ---
        /// In chainspec - Ethash.Duration
        /// </summary>
        bool IsTimeAdjustmentPostOlympic { get; }

        /// <summary>
        /// Homestead contract creation via transaction cost set to 21000 + 32000 (previously 21000)
        /// Failing init does not create an empty code contract
        /// Difficulty adjustment changed
        /// Transaction signature uniqueness (s-value has to be less or equal than than secp256k1n/2)
        /// </summary>
        bool IsEip2Enabled { get; }

        /// <summary>
        /// Homestead DELEGATECALL instruction added
        /// </summary>
        bool IsEip7Enabled { get; }

        /// <summary>
        /// Byzantium Change difficulty adjustment to target mean block time including uncles
        /// </summary>
        bool IsEip100Enabled { get; }

        /// <summary>
        /// Byzantium REVERT instruction in the Ethereum Virtual Machine
        /// ---
        /// in chainspec Ethash.Eip100bTransition
        /// </summary>
        bool IsEip140Enabled { get; }

        /// <summary>
        /// Tangerine Whistle Gas cost of IO operations increased
        /// </summary>
        bool IsEip150Enabled { get; }

        /// <summary>
        /// Spurious Dragon Chain ID in signatures (replay attack protection)
        /// </summary>
        bool IsEip155Enabled { get; }

        /// <summary>
        /// Spurious Dragon State clearing
        /// </summary>
        bool IsEip158Enabled { get; }

        /// <summary>
        /// Spurious Dragon EXP cost increase
        /// </summary>
        bool IsEip160Enabled { get; }

        /// <summary>
        /// Spurious Dragon Code size limit
        /// ---
        /// in chainspec MaxCodeSizeTransition
        /// </summary>
        bool IsEip170Enabled { get; }

        /// <summary>
        /// Byzantium Precompiled contracts for addition and scalar multiplication on the elliptic curve alt_bn128
        /// ---
        /// in chainspec in builtin accounts
        /// </summary>
        bool IsEip196Enabled { get; }

        /// <summary>
        /// Byzantium Precompiled contracts for optimal ate pairing check on the elliptic curve alt_bn128
        /// ---
        /// in chainspec in builtin accounts
        /// </summary>
        bool IsEip197Enabled { get; }

        /// <summary>
        /// Byzantium Precompiled contract for bigint modular exponentiation
        /// ---
        /// in chainspec in builtin accounts
        /// </summary>
        bool IsEip198Enabled { get; }

        /// <summary>
        /// Byzantium New opcodes: RETURNDATASIZE and RETURNDATACOPY
        /// </summary>
        bool IsEip211Enabled { get; }

        /// <summary>
        /// Byzantium New opcode STATICCALL
        /// </summary>
        bool IsEip214Enabled { get; }

        /// <summary>
        /// Byzantium Difficulty Bomb Delay and Block Reward Reduction
        /// ---
        /// in chainspec as DifficultyBombDelays
        /// </summary>
        bool IsEip649Enabled { get; }

        /// <summary>
        /// Byzantium Embedding transaction return data in receipts
        /// </summary>
        bool IsEip658Enabled { get; }

        /// <summary>
        /// Constantinople SHL, SHR, SAR instructions
        /// </summary>
        bool IsEip145Enabled { get; }

        /// <summary>
        /// Constantinople Skinny CREATE2
        /// </summary>
        bool IsEip1014Enabled { get; }

        /// <summary>
        /// Constantinople EXTCODEHASH instructions
        /// </summary>
        bool IsEip1052Enabled { get; }

        /// <summary>
        /// Constantinople Net gas metering for SSTORE operations
        /// </summary>
        bool IsEip1283Enabled { get; }

        /// <summary>
        /// Constantinople Difficulty Bomb Delay and Block Reward Adjustment
        /// ---
        /// in chainspec as DifficultyBombDelays and BlockReward
        /// </summary>
        bool IsEip1234Enabled { get; }

        /// <summary>
        /// Istanbul ChainID opcode
        /// </summary>
        bool IsEip1344Enabled { get; }
        
        /// <summary>
        /// Istanbul transaction data gas cost reduction
        /// </summary>
        bool IsEip2028Enabled { get; }

        /// <summary>
        /// Istanbul Blake2b precompile
        /// </summary>
        bool IsEip152Enabled { get; }
        
        /// <summary>
        /// Istanbul alt_bn128 gas cost reduction
        /// </summary>
        bool IsEip1108Enabled { get; }
        
        /// <summary>
        /// Istanbul state opcodes gas cost increase
        /// </summary>
        bool IsEip1884Enabled { get; }
        
        /// <summary>
        /// Istanbul net-metered SSTORE
        /// </summary>
        bool IsEip2200Enabled { get; }
        
        /// <summary>
        /// Berlin subroutines -> https://github.com/ethereum/EIPs/issues/2315
        /// </summary>
        bool IsEip2315Enabled { get; }
        
        /// <summary>
        /// Should EIP158 be ignored for this account.
        /// </summary>
        /// <remarks>THis is needed for SystemUser account compatibility with Parity.</remarks>
        /// <param name="address"></param>
        /// <returns></returns>
        bool IsEip158IgnoredAccount(Address address);		
    }
}