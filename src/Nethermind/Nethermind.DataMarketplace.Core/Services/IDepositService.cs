// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Core.Services
{
    public interface IDepositService
    {
        ulong GasLimit { get; }
        Task<Keccak?> MakeDepositAsync(Address onBehalfOf, Deposit deposit, UInt256 gasPrice);
        Task<uint> VerifyDepositAsync(Address onBehalfOf, Keccak depositId);
        Task<uint> VerifyDepositAsync(Address onBehalfOf, Keccak depositId, long blockNumber);
        Task<UInt256> ReadDepositBalanceAsync(Address onBehalfOf, Keccak depositId);
        Task ValidateContractAddressAsync(Address contractAddress);
    }
}
