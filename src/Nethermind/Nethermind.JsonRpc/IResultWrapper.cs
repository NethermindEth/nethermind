// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.JsonRpc
{
    public interface IResultWrapper
    {
        public object Data { get; }
        public Result Result { get; }
        public int ErrorCode { get; }
        public bool IsTemporary { get; }
    }
}
