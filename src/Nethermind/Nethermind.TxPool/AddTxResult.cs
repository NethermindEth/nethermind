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

namespace Nethermind.TxPool
{
    /// <summary>
    /// Describes potential outcomes of adding transaction to the TX pool.
    /// </summary>
    public enum AddTxResult
    {
        /// <summary>
        /// The transaction has been added successfully. This is the only 'success' outcome.
        /// </summary>
        Added,
        
        /// <summary>
        /// A transaction with the same hash has already been added to the pool in the past.
        /// </summary>
        AlreadyKnown,
        
        /// <summary>
        /// Covers scenarios where sender recovery fails.
        /// </summary>
        FailedToResolveSender,
        
        /// <summary>
        /// Fee paid by this transaction is not enough to be accepted in the mempool.
        /// </summary>
        FeeTooLow,
        
        /// <summary>
        /// Fee paid by this transaction is not enough to be accepted in the mempool.
        /// </summary>
        FeeTooLowToCompete,
        
        /// <summary>
        /// This transaction has been filtered out by the transaction pool filter.
        /// </summary>
        Filtered,
        
        /// <summary>
        /// Transaction gas limit exceeds the block gas limit.
        /// </summary>
        GasLimitExceeded,
        
        /// <summary>
        /// Sender account has not enough balance to execute this transaction.
        /// </summary>
        InsufficientFunds,
        
        /// <summary>
        /// Calculation of gas price * gas limit + value overflowed int256.
        /// </summary>
        Int256Overflow,
        
        /// <summary>
        /// Transaction format is invalid.
        /// </summary>
        Invalid,
        
        /// <summary>
        /// The nonce is too far in the future for this sender account.
        /// </summary>
        NonceTooFarInTheFuture,

        /// <summary>
        /// The EOA (externally owned account) that signed this transaction (sender) has already signed and executed a transaction with the same nonce.
        /// </summary>
        OldNonce,

        /// <summary>
        /// A transaction with same nonce has been signed locally already and is awaiting in the pool.
        /// (I would like to change this behaviour to allow local replacement)
        /// </summary>
        OwnNonceAlreadyUsed
    }
}
