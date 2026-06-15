// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm;

public class EvmConfig : IEvmConfig
{
    public bool StreamInterpreter { get; set; }
    public int StreamInterpreterThreshold { get; set; } = 16;
}
