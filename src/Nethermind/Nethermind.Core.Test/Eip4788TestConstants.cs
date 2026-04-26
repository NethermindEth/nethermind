// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core.Test;

public static class Eip4788TestConstants
{
    public static readonly byte[] Code = Bytes.FromHexString("0x3373fffffffffffffffffffffffffffffffffffffffe14604d57602036146024575f5ffd5b5f35801560495762001fff810690815414603c575f5ffd5b62001fff01545f5260205ff35b5f5ffd5b62001fff42064281555f359062001fff015500");

    public static readonly ValueHash256 CodeHash = ValueKeccak.Compute(Code);

    public static readonly UInt256 Nonce = 1;
}
