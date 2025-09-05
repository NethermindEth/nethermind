// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core.Test;

public static class Eip2935TestConstants
{
    public static readonly byte[] Code = Bytes.FromHexString("0x3373fffffffffffffffffffffffffffffffffffffffe14604657602036036042575f35600143038111604257611fff81430311604257611fff9006545f5260205ff35b5f5ffd5b5f35611fff60014303065500");

    public static readonly byte[] InitCode = Bytes.FromHexString("0x60538060095f395ff33373fffffffffffffffffffffffffffffffffffffffe14604657602036036042575f35600143038111604257611fff81430311604257611fff9006545f5260205ff35b5f5ffd5b5f35611fff60014303065500");
    public static readonly ValueHash256 CodeHash = ValueKeccak.Compute(Code);

    public static readonly UInt256 Nonce = 1;
}
