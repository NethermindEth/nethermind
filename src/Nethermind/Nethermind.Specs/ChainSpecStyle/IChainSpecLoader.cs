// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;

namespace Nethermind.Specs.ChainSpecStyle
{
    public interface IChainSpecLoader
    {
        ChainSpec Load(byte[] data);
        ChainSpec Load(string jsonData);
        ChainSpec Load(Stream streamData);
    }
}
