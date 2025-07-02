// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Tracing.GethStyle.Custom.Native.Call;

public class NativeCallTracerConfig
{
    public bool OnlyTopCall { get; init; }

    public bool WithLog { get; init; }
}
