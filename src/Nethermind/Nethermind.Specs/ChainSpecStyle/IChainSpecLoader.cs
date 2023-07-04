// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.ChainSpecStyle
{
    public interface IChainSpecLoader
    {
        ChainSpec Load(byte[] data);
        ChainSpec Load(string jsonData);
    }
}
