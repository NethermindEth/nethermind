// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Core;
public class TxContractCode
{
    public TxContractCode(byte[]? contractCode, Signature sig) : this(contractCode, sig.V, sig.R, sig.S)
    {

    }
    public TxContractCode(byte[]? contractCode, ulong yParity, byte[] r, byte[] s)
    {
        ContractCode = contractCode;
        YParity = yParity;
        R = r;
        S = s;
    }
    public byte[]? ContractCode { get; }
    public ulong YParity { get; }
    public byte[] R { get; }
    public byte[] S { get; }
}
