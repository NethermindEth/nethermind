// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts.Json;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public sealed class TransactionPermissionContractV2 : TransactionPermissionContract
    {
        private static readonly UInt256 Two = 2;

        public TransactionPermissionContractV2(
            IAbiEncoder abiEncoder,
            Address contractAddress,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource)
            : base(abiEncoder, contractAddress ?? throw new ArgumentNullException(nameof(contractAddress)), readOnlyTxProcessorSource)
        {
        }

        protected override object[] GetAllowedTxTypesParameters(Transaction tx, BlockHeader parentHeader) =>
            new object[] { tx.SenderAddress, tx.To ?? Address.Zero, tx.Value };

        public override UInt256 Version => Two;
    }
}
