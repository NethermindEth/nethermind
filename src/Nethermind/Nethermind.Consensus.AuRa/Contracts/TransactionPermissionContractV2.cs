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
    public sealed class TransactionPermissionContractV2(
        Address contractAddress,
        IReadOnlyTxProcessorSource readOnlyTxProcessorSource) : TransactionPermissionContract(contractAddress ?? throw new ArgumentNullException(nameof(contractAddress)), readOnlyTxProcessorSource)
    {
        private static readonly UInt256 Two = 2;
        private static readonly ContractFunction<(Address, Address, UInt256), (uint, bool)> AllowedTxTypesFunction =
            new AbiSignature("allowedTxTypes").With(AbiType.Address, AbiType.Address, AbiType.UInt256).Returns(AbiType.UInt32, AbiType.Bool);

        protected override (ITransactionPermissionContract.TxPermissions, bool) CallAllowedTxTypes(BlockHeader parentHeader, Transaction tx, out bool toIsContract)
        {
            Address to = tx.To ?? Address.Zero;
            (uint permissions, bool shouldCache) = Constant.Call(parentHeader, AllowedTxTypesFunction, (tx.SenderAddress, to, tx.Value), to, out toIsContract);
            return ((ITransactionPermissionContract.TxPermissions)permissions, shouldCache);
        }

        public override UInt256 Version => Two;
    }
}
