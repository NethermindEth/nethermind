// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.OverridableEnv;

public interface IOverridableEnvFactory
{
    IOverridableEnv Create();
}
