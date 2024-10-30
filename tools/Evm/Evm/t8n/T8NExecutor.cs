// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Evm.t8n;

public static class T8NExecutor
{
    public static void Execute(T8NCommandArguments arguments)
    {
        var t8nTest = T8NInputProcessor.Process(arguments);

        Console.Write(t8nTest);
    }
}
