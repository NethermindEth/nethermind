// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
namespace Nethermind.Evm.Tracing.GethStyle.Custom.Native;

public struct NativeTracerContext
{
    public Address? From;
    public Address? To;
}
