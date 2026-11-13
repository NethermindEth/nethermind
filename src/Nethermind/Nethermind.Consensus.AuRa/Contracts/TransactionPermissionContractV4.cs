// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Consensus.AuRa.Contracts
{
    /// <summary>Version four of the contract. Created to adjust EIP1559 changes.</summary>
    public sealed class TransactionPermissionContractV4(
        Address contractAddress,
        IReadOnlyTxProcessorSource readOnlyTxProcessorSource,
        ISpecProvider specProvider) : TransactionPermissionContract(contractAddress, readOnlyTxProcessorSource)
    {
        private readonly ISpecProvider _specProvider = specProvider;
        private static readonly UInt256 Four = 4;
        internal static readonly ContractFunction<(Address, Address, UInt256, UInt256, UInt256, UInt256, byte[]), (uint, bool)> AllowedTxTypesFunction =
            new AbiSignature("allowedTxTypes")
                .With(AbiType.Address, AbiType.Address, AbiType.UInt256, AbiType.UInt256, AbiType.UInt256, AbiType.UInt256, AbiType.Bytes)
                .Returns(AbiType.UInt32, AbiType.Bool);

        protected override (ITransactionPermissionContract.TxPermissions, bool) CallAllowedTxTypes(BlockHeader parentHeader, Transaction tx, out bool toIsContract)
        {
            // _sender Transaction sender address.
            // _to Transaction recipient address. If creating a contract, the `_to` address is zero.
            // _value Transaction amount in wei.
            // _maxFeePerGas instead of the legacy _gasPrice
            // _maxInclusionFeePerGas instead of the legacy _gasPrice
            // _gasLimit
            // _data Transaction data.

            Address to = tx.To ?? Address.Zero;
            (uint permissions, bool shouldCache) = Constant.Call(parentHeader, AllowedTxTypesFunction,
                (tx.SenderAddress, to, tx.Value, tx.MaxFeePerGas, tx.MaxPriorityFeePerGas, (UInt256)tx.GasLimit, tx.Data.AsArray() ?? []), to, out toIsContract);
            return ((ITransactionPermissionContract.TxPermissions)permissions, shouldCache);
        }

        public override UInt256 Version => Four;
    }
}
