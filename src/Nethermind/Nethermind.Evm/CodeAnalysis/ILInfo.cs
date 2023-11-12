// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Sigil.NonGeneric;

namespace Nethermind.Evm.CodeAnalysis;
internal struct ILInfo
{
    public ushort[] Pcs;
    public Emit[] IlMethod;

    Emit HasOverride(ushort pc)
    {
        return IlMethod[pc];
    }
}
