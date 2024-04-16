// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Microsoft.IdentityModel.Tokens;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.State;

namespace Ethereum.Test.Base
{
    public class AccountState
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public byte[]? Code { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public UInt256 Balance { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public UInt256 Nonce { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<UInt256, byte[]> Storage { get; set; }

        public static AccountState GetFromAccount(Address address, WorldState stateProvider, Dictionary<Address, Dictionary<UInt256, byte[]>> storages)
        {
            var account = stateProvider.GetAccount(address);
            var code = stateProvider.GetCode(address);
            var accountState = new AccountState()
            {
                Nonce = account.Nonce,
                Balance = account.Balance,
                Code = code.Length == 0 ? null : code
            };

            if (storages.TryGetValue(address, out var storage))
            {
                accountState.Storage = storage;
            }

            return accountState;
        }

        public bool IsEmptyAccount()
        {
            return Balance.IsZero && Nonce.IsZero && Code.IsNullOrEmpty() && Storage.IsNullOrEmpty();
        }

    }
}
