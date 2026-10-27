// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Experimental.Abi;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public interface ICertifierContract
    {
        bool Certified(BlockHeader parentHeader, Address sender);
    }

    public class CertifierContract : RegisterBasedContract, ICertifierContract
    {
        private static readonly ContractFunction<Address, bool> CertifiedFunction = new AbiSignature("certified").With(AbiType.Address).Returns(AbiType.Bool);
        internal const string ServiceTransactionContractRegistryName = "service_transaction_checker";

        private IConstantContract Constant { get; }

        public CertifierContract(
            IRegisterContract registerContract,
            IReadOnlyTxProcessorSource readOnlyTransactionProcessorSource)
            : base(registerContract, ServiceTransactionContractRegistryName) => Constant = GetConstant(readOnlyTransactionProcessorSource);

        public bool Certified(BlockHeader parentHeader, Address sender) =>
            Constant.Call(parentHeader, CertifiedFunction, Address.Zero, sender, missingContractResult: false);
    }
}
