// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using System;

namespace Nethermind.JsonRpc
{
    public interface IResultWrapper
    {
        public object Data { get; }
        public Func<object, byte[]>? GetBytes { get; }
        public Result Result { get; }
        public int ErrorCode { get; }
        public bool IsTemporary { get; }
    }
}
