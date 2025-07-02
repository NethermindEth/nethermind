// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.JsonRpcMethodFilter;

interface IJsonRpcMethodFilter
{
    bool ShouldSubmit(string methodName);

    bool ShouldIgnore(string methodName) => !ShouldSubmit(methodName);
}
