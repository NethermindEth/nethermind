// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public sealed class TransactionPermissionContractV3(
        Address contractAddress,
        IReadOnlyTxProcessorSource readOnlyTxProcessorSource,
        ISpecProvider specProvider) : TransactionPermissionContract(contractAddress ?? throw new ArgumentNullException(nameof(contractAddress)), readOnlyTxProcessorSource)
    {
        private readonly ISpecProvider _specProvider = specProvider;
        private static readonly UInt256 Three = 3;
        private static readonly ContractFunction<(Address, Address, UInt256, UInt256, byte[]), (uint, bool)> AllowedTxTypesFunction =
            new AbiSignature("allowedTxTypes").With(AbiType.Address, AbiType.Address, AbiType.UInt256, AbiType.UInt256, AbiType.Bytes).Returns(AbiType.UInt32, AbiType.Bool);

        protected override (ITransactionPermissionContract.TxPermissions, bool) CallAllowedTxTypes(BlockHeader parentHeader, Transaction tx, out bool toIsContract)
        {
            // _sender Transaction sender address.
            // _to Transaction recipient address. If creating a contract, the `_to` address is zero.
            // _value Transaction amount in wei.
            // _gasPrice Gas price in wei for the transaction.
            // _data Transaction data.

            ulong number = (parentHeader?.Number ?? 0) + 1;
            bool isEip1559Enabled = _specProvider.GetSpecFor1559(number).IsEip1559Enabled;
            UInt256 gasPrice = isEip1559Enabled && tx.Supports1559 ? tx.MaxFeePerGas : tx.GasPrice;

            Address to = tx.To ?? Address.Zero;
            (uint permissions, bool shouldCache) = Constant.Call(parentHeader, AllowedTxTypesFunction,
                (tx.SenderAddress, to, tx.Value, gasPrice, tx.Data.AsArray() ?? []), to, out toIsContract);
            return ((ITransactionPermissionContract.TxPermissions)permissions, shouldCache);
        }

        public override UInt256 Version => Three;
    }
}
