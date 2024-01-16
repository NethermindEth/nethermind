// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;

namespace Nethermind.Crypto.PairingCurves;
public interface IBaseField
{
    BigInteger GetOrder();
    // size to encode Fp in bytes
    int GetSize();
}
