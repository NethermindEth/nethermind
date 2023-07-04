// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Consensus.Ethash
{
    internal interface IEthashDataSet : IDisposable
    {
        uint Size { get; }
        uint[] CalcDataSetItem(uint i);
    }
}
