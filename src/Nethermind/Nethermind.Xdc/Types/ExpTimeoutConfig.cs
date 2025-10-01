// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Types;

public class ExpTimeoutConfig(int @base, byte maxExponent)
{
    public int Base { get; set; } = @base;
    public byte MaxExponent { get; set; } = maxExponent;
}
