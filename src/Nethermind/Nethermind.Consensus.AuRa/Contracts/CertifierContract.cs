//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
        private static readonly object[] MissingCertifiedResult = {false};
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
            Constant.Call<bool>(new CallInfo(parentHeader, nameof(Certified), Address.Zero, sender) {MissingContractResult = MissingCertifiedResult});
    }
}
