// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Evm.t8n.JsonTypes;

namespace Evm.t8n;

public static class T8NExecutor
{
    public static void Execute(T8NCommandArguments arguments)
    {
        T8NTest t8nTest = T8NInputProcessor.ProcessInputAndConvertToT8NTest(arguments);
    }
}
