// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Rlpx
{
    public interface IFrameCipher
    {
        void Encrypt(byte[] input, int offset, int length, byte[] output, int outputOffset);
        void Decrypt(byte[] input, int offset, int length, byte[] output, int outputOffset);
    }
}
