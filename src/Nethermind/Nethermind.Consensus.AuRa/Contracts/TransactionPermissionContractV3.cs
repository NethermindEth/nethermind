// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts.Json;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public sealed class TransactionPermissionContractV3 : TransactionPermissionContract
    {
        private readonly ISpecProvider _specProvider;
        private static readonly UInt256 Three = 3;

        public TransactionPermissionContractV3(
            IAbiEncoder abiEncoder,
            Address contractAddress,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource,
            ISpecProvider specProvider)
            : base(abiEncoder, contractAddress ?? throw new ArgumentNullException(nameof(contractAddress)), readOnlyTxProcessorSource)
        {
            _specProvider = specProvider;
        }


        protected override object[] GetAllowedTxTypesParameters(Transaction tx, BlockHeader parentHeader)
        {
            // _sender Transaction sender address.
            // _to Transaction recipient address. If creating a contract, the `_to` address is zero.
            // _value Transaction amount in wei.
            // _gasPrice Gas price in wei for the transaction.
            // _data Transaction data.

            long number = (parentHeader?.Number ?? 0) + 1;
            bool isEip1559Enabled = _specProvider.GetSpecFor1559(number).IsEip1559Enabled;
            UInt256 gasPrice = isEip1559Enabled && tx.Supports1559 ? tx.MaxFeePerGas : tx.GasPrice;

            return new object[]
            {
                tx.SenderAddress, tx.To ?? Address.Zero, tx.Value, gasPrice, tx.Data.FasterToArray() ?? Array.Empty<byte>()
            };
        }

        public override UInt256 Version => Three;
    }
}
