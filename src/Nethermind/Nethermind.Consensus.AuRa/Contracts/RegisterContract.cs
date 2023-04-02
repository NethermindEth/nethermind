// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public interface IRegisterContract
    {
        bool TryGetAddress(BlockHeader header, string key, out Address address);
        Address GetAddress(BlockHeader header, string key);
    }

    /// <summary>
    /// Contract for registry of values (dictionary) on chain
    /// </summary>
    public class RegisterContract : Contract, IRegisterContract
    {
        private static Address MissingAddress = Address.Zero;
        private static readonly object[] MissingGetAddressResult = { MissingAddress };

        /// <summary>
        /// Category of domain name service addresses
        /// </summary>
        private const string DnsAddressRecord = "A";
        private IConstantContract Constant { get; }

        public RegisterContract(
            IAbiEncoder abiEncoder,
            Address contractAddress,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource)
            : base(abiEncoder, contractAddress ?? throw new ArgumentNullException(nameof(contractAddress)))
        {
            Constant = GetConstant(readOnlyTxProcessorSource);
        }

        public bool TryGetAddress(BlockHeader header, string key, out Address address)
        {
            try
            {
                address = GetAddress(header, key);
                return !ReferenceEquals(address, MissingAddress);
            }
            catch (AbiException)
            {
                address = MissingAddress;
                return false;
            }
        }

        public Address GetAddress(BlockHeader header, string key) =>
            // 2 arguments: name and key (category)
            Constant.Call<Address>(
                new CallInfo(header, nameof(GetAddress), Address.Zero, Keccak.Compute(key), DnsAddressRecord) { MissingContractResult = MissingGetAddressResult });
    }
}
