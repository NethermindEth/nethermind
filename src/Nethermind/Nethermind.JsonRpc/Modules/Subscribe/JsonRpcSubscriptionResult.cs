// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class JsonRpcSubscriptionResult
    {
        public string Subscription { get; set; } = null!;

        public object? Result { get; set; }
    }

    public class JsonRpcSubscriptionResult<T>
    {
        public string Subscription { get; set; } = null!;

        public T Result { get; set; } = default!;
    }
}
