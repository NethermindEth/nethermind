// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;

namespace Nethermind.Core;

// Lazy, so the guest runs no class constructor for this type.
public static partial class Eip2935Constants
{
    public static Address BlockHashHistoryAddress => field ??= new("0x0000F90827F1C53a10cb7A02335B175320002935");

    public static byte[] Code => field ??= Bytes.FromHexString("3373fffffffffffffffffffffffffffffffffffffffe14604657602036036042575f35600143038111604257611fff81430311604257611fff9006545f5260205ff35b5f5ffd5b5f35611fff60014303065500");
}
