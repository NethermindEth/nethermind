// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.JsonRpc.Modules.Personal
{
    public class AccountForRpc
    {
        public Address Address { get; set; }
        public bool Unlocked { get; set; }
    }
}
