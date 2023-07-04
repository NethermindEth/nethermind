// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Crypto
{
    public interface IIesEngine
    {
        byte[] ProcessBlock(
            byte[] input,
            int inOff,
            int inLen,
            byte[] macData);
    }
}
