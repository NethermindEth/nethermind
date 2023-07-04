// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Config
{
    public interface IConfigSource
    {
        (bool IsSet, object Value) GetValue(Type type, string category, string name);

        (bool IsSet, string Value) GetRawValue(string category, string name);

        IEnumerable<(string Category, string Name)> GetConfigKeys();
    }
}
