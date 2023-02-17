// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public interface ICertifierContract
    {
        bool Certified(BlockHeader parentHeader, Address sender);
    }

    public class CertifierContract : RegisterBasedContract, ICertifierContract
    {
        private static readonly object[] MissingCertifiedResult = { false };
        internal const string ServiceTransactionContractRegistryName = "service_transaction_checker";

        private IConstantContract Constant { get; }

        public CertifierContract(
            IAbiEncoder abiEncoder,
            IRegisterContract registerContract,
            IReadOnlyTxProcessorSource readOnlyTransactionProcessorSource)
            : base(abiEncoder, registerContract, ServiceTransactionContractRegistryName)
        {
            Constant = GetConstant(readOnlyTransactionProcessorSource);
        }

        public bool Certified(BlockHeader parentHeader, Address sender) =>
            Constant.Call<bool>(new CallInfo(parentHeader, nameof(Certified), Address.Zero, sender) { MissingContractResult = MissingCertifiedResult });
    }
}
