// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Int256;

namespace Ethereum.Test.Base;

public class AccountState
{
    public byte[]? Code { get; set; }
    public UInt256 Balance { get; set; }
    public UInt256 Nonce { get; set; }
    public Dictionary<UInt256, byte[]>? Storage { get; set; }

    public bool IsEmptyAccount()
    {
        return Balance.IsZero && Nonce.IsZero && (Code == null || Code.Length == 0) && (Storage == null || Storage.Count == 0);
    }
}
