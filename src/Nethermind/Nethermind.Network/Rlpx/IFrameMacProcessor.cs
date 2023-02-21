// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Network.Rlpx
{
    public interface IFrameMacProcessor : IDisposable
    {
        void AddMac(byte[] input, int offset, int length, bool isHeader);

        void UpdateEgressMac(byte[] input);

        void UpdateIngressMac(byte[] input, bool isHeader);

        bool CheckMac(byte[] mac, bool isHeader);

        void CalculateMac(byte[] output);

        void AddMac(byte[] input, int offset, int length, byte[] output, int outputOffset, bool isHeader);
        void CheckMac(byte[] input, int offset, int length, bool isHeader);
    }
}
