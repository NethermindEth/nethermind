// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Jint;
using Jint.Native;

namespace Nethermind.Cli
{
    public interface ICliEngine
    {
        Engine JintEngine { get; }
        JsValue Execute(string statement);
    }
}
