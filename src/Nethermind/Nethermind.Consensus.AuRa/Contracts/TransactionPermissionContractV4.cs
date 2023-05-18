// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.AuRa.Contracts
{
    /// <summary>Version four of the contract. Created to adjust EIP1559 changes.</summary>
    public sealed class TransactionPermissionContractV4 : TransactionPermissionContract
    {
        private readonly ISpecProvider _specProvider;
        private static readonly UInt256 Four = 4;

        public TransactionPermissionContractV4(
            IAbiEncoder abiEncoder,
            Address contractAddress,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource,
            ISpecProvider specProvider)
            : base(abiEncoder, contractAddress, readOnlyTxProcessorSource)
        {
            _specProvider = specProvider;
        }


        protected override object[] GetAllowedTxTypesParameters(Transaction tx, BlockHeader parentHeader)
        {
            // _sender Transaction sender address.
            // _to Transaction recipient address. If creating a contract, the `_to` address is zero.
            // _value Transaction amount in wei.
            // _maxFeePerGas instead of the legacy _gasPrice
            // _maxInclusionFeePerGas instead of the legacy _gasPrice
            // _gasLimit
            // _data Transaction data.

            return new object[]
            {
                tx.SenderAddress, tx.To ?? Address.Zero, tx.Value, tx.MaxFeePerGas, tx.MaxPriorityFeePerGas, tx.GasLimit, tx.Data.FasterToArray() ?? Array.Empty<byte>()
            };
        }

        public override UInt256 Version => Four;
    }
}
