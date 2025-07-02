// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
namespace Nethermind.Evm.Tracing.GethStyle.Custom.Native.Prestate;

public class NativePrestateTracerDiffMode
{
    public Dictionary<AddressAsKey, NativePrestateTracerAccount> pre { get; init; }
    public Dictionary<AddressAsKey, NativePrestateTracerAccount> post { get; init; }
}
