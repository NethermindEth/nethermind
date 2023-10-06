// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.TxPool;

public interface INonceManager
{
    NonceLocker ReserveNonce(Address address, out UInt256 reservedNonce);
    NonceLocker TxWithNonceReceived(Address address, UInt256 nonce);
}
