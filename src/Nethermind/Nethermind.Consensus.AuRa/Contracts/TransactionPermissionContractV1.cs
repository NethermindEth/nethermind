// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Experimental.Abi;
using Nethermind.Int256;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public sealed class TransactionPermissionContractV1(
        Address contractAddress,
        IReadOnlyTxProcessorSource readOnlyTxProcessorSource) : TransactionPermissionContract(contractAddress ?? throw new ArgumentNullException(nameof(contractAddress)), readOnlyTxProcessorSource)
    {
        private static readonly ContractFunction<Address, uint> AllowedTxTypesFunction =
            new AbiSignature("allowedTxTypes").With(AbiType.Address).Returns(AbiType.UInt32);

        protected override (ITransactionPermissionContract.TxPermissions, bool) CallAllowedTxTypes(BlockHeader parentHeader, Transaction tx, out bool toIsContract) =>
            ((ITransactionPermissionContract.TxPermissions)Constant.Call(parentHeader, AllowedTxTypesFunction, tx.SenderAddress, tx.To ?? Address.Zero, out toIsContract), true);

        public override UInt256 Version => UInt256.One;
    }
}
