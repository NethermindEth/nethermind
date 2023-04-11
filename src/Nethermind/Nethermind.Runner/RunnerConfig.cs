// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Runner;

public class RunnerConfig: IRunnerConfig
{
    public long? MaxHeapMb { get; set; } = null;
}
