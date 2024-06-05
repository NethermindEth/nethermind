// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Core;
public class TxContractCode
{
    public TxContractCode(byte[]? contractCode, UInt256 yParity, UInt256 r, UInt256 s)
    {
        ContractCode = contractCode;
        YParity = yParity;
        R = r;
        S = s;
    }
    public byte[]? ContractCode { get; }
    public UInt256 YParity { get; }
    public UInt256 R { get; }
    public UInt256 S { get; }
}
