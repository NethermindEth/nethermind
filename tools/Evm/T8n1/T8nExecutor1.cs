// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Evm.T8n.JsonTypes;

namespace Evm.T8n;

public static class T8nExecutor
{
    public static void Execute(T8nCommandArguments arguments)
    {
        T8nTest t8nTest = T8nInputProcessor.ProcessInputAndConvertToT8nTest(arguments);
    }
}
