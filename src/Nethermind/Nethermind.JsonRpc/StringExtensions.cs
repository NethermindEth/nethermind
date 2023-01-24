// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc;

public static class StringExtensions
{
    public static JsonTextReader ToJsonTextReader(this string json) => new(new StringReader(json));
}
