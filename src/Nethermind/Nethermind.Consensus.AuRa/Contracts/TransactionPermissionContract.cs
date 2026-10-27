// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public interface ITransactionPermissionContract : IVersionedContract
    {
        /// <summary>
        /// Returns the contract version number needed for node's engine.
        /// </summary>
        UInt256 Version { get; }

        /// <summary>
        /// Defines the allowed transaction types which may be initiated by the specified sender with
        /// the specified gas price and data. Used by node's engine each time a transaction is about to be
        /// included into a block.
        /// </summary>
        /// <param name="parentHeader"></param>
        /// <param name="tx"></param>
        /// <returns><see cref="TxPermissions"/>Set of allowed transactions types and <see cref="bool"/> If `true` is returned, the same permissions will be applied from the same sender without calling this contract again.</returns>
        (TxPermissions Permissions, bool ShouldCache, bool ContractExists) AllowedTxTypes(BlockHeader parentHeader, Transaction tx);

        [Flags]
        public enum TxPermissions : uint
        {
            /// <summary>
            /// No permissions
            /// </summary>
            None = 0x0,

            /// <summary>
            /// 0x01 - basic transaction (e.g. ether transferring to user wallet)
            /// </summary>
            Basic = 0b00000001,

            /// <summary>
            /// 0x02 - contract call
            /// </summary>
            Call = 0b00000010,

            /// <summary>
            /// 0x04 - contract creation
            /// </summary>
            Create = 0b00000100,

            /// <summary>
            /// 0x08 - private transaction
            /// </summary>
            Private = 0b00001000,

            All = 0xffffffff,
        }
    }

    public abstract class TransactionPermissionContract : Contract, ITransactionPermissionContract
    {
        private static readonly ContractFunction<ValueTuple, UInt256> ContractVersionFunction = new AbiSignature("contractVersion").With().Returns(AbiType.UInt256);

        public virtual UInt256 ContractVersion(BlockHeader blockHeader) => Constant.Call(blockHeader, ContractVersionFunction, Address.Zero, default);

        /// <summary>
        /// Returns the contract version number needed for node's engine.
        /// </summary>
        public abstract UInt256 Version { get; }

        /// <summary>
        /// Defines the allowed transaction types which may be initiated by the specified sender with
        /// the specified gas price and data. Used by node's engine each time a transaction is about to be
        /// included into a block.
        /// </summary>
        /// <param name="parentHeader"></param>
        /// <param name="tx"></param>
        /// <returns><see cref="ITransactionPermissionContract.TxPermissions"/>Set of allowed transactions types and <see cref="bool"/> If `true` is returned, the same permissions will be applied from the same sender without calling this contract again.</returns>
        public (ITransactionPermissionContract.TxPermissions Permissions, bool ShouldCache, bool ContractExists) AllowedTxTypes(BlockHeader parentHeader, Transaction tx)
        {
            (ITransactionPermissionContract.TxPermissions permissions, bool shouldCache) = CallAllowedTxTypes(parentHeader, tx, out bool toIsContract);
            return (permissions, shouldCache, toIsContract);
        }

        /// <summary>Calls the version-specific <c>allowedTxTypes</c> function with the fields of <paramref name="tx"/>.</summary>
        /// <param name="toIsContract">Whether the recipient of <paramref name="tx"/> is a contract, in the state the call ran on.</param>
        protected abstract (ITransactionPermissionContract.TxPermissions, bool) CallAllowedTxTypes(BlockHeader parentHeader, Transaction tx, out bool toIsContract);

        protected PermissionConstantContract Constant { get; }

        protected TransactionPermissionContract(
            Address contractAddress,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource)
            : base(contractAddress) => Constant = new PermissionConstantContract(this, readOnlyTxProcessorSource);

        protected class PermissionConstantContract(Contract contract, IReadOnlyTxProcessorSource readOnlyTxProcessorSource) : ConstantContract(contract, readOnlyTxProcessorSource)
        {
            /// <summary>Calls <paramref name="function"/>, also checking in the same state whether <paramref name="to"/> is a contract.</summary>
            public TResult Call<TArgs, TResult>(BlockHeader parentHeader, ContractFunction<TArgs, TResult> function, TArgs arguments, Address to, out bool toIsContract)
            {
                lock (_readOnlyTxProcessorSource)
                {
                    using IReadOnlyTxProcessingScope scope = _readOnlyTxProcessorSource.Build(parentHeader);
                    toIsContract = scope.WorldState.IsContract(to);
                    return Call(scope, parentHeader, function, Address.Zero, arguments);
                }
            }
        }
    }
}
