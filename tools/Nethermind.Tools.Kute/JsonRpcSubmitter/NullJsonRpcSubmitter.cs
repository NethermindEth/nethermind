// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.JsonRpcSubmitter;

class NullJsonRpcSubmitter : IJsonRpcSubmitter
{
    public Task Submit(string content) => Task.CompletedTask;
}
