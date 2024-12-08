// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public sealed class TransactionPermissionContractV1 : TransactionPermissionContract
    {
        public TransactionPermissionContractV1(
            IAbiEncoder abiEncoder,
            Address contractAddress,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource)
            : base(abiEncoder, contractAddress ?? throw new ArgumentNullException(nameof(contractAddress)), readOnlyTxProcessorSource)
        {
        }

        protected override object[] GetAllowedTxTypesParameters(Transaction tx, BlockHeader parentHeader) => new object[] { tx.SenderAddress };

        protected override (ITransactionPermissionContract.TxPermissions, bool) CallAllowedTxTypes(PermissionConstantContract.PermissionCallInfo callInfo) =>
            (Constant.Call<ITransactionPermissionContract.TxPermissions>(callInfo), true);

        public override UInt256 Version => UInt256.One;
    }
}
