// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


namespace Nethermind.Crypto.Blake2;

public enum Blake2CompressMethod
{
    Avx2,
    Sse41,
    Scalar,
    Optimal
}
