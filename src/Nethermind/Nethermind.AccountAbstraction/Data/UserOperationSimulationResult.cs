// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Collections.Immutable;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.AccountAbstraction.Data
{
    public struct UserOperationSimulationResult
    {
        public bool Success { get; set; }
        public UserOperationAccessList AccessList { get; set; }
        public IDictionary<Address, Keccak> AddressesToCodeHashes { get; set; }
        public string? Error { get; set; }

        public static UserOperationSimulationResult Failed(string? error)
        {
            return new()
            {
                Success = false,
                AccessList = UserOperationAccessList.Empty,
                AddressesToCodeHashes = ImmutableDictionary<Address, Keccak>.Empty,
                Error = error
            };
        }
    }
}
