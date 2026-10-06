// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Buffers;
using Nethermind.Network;

namespace Nethermind.Xdc.Test;

/// <summary>
/// Canned-response <see cref="IMessageSerializationService"/> for handler tests.
/// </summary>
/// <remarks>
/// NSubstitute cannot mock methods with span parameters, so tests that stub deserialization
/// use this hand-written fake instead. Consumed length covers the whole input, mirroring the
/// previous test setup where the mocked buffer reader index advanced past all readable bytes.
/// </remarks>
internal sealed class StubSerializationService : IMessageSerializationService
{
    private readonly Dictionary<Type, MessageBase> _responses = [];

    public void Respond<T>(T message) where T : MessageBase => _responses[typeof(T)] = message;

    public PooledBuffer ZeroSerialize<T>(T message) where T : MessageBase =>
        throw new NotImplementedException();

    public T Deserialize<T>(ReadOnlySpan<byte> data) where T : MessageBase =>
        Deserialize<T>(data, out _);

    public T Deserialize<T>(ReadOnlySpan<byte> data, out int consumed) where T : MessageBase
    {
        consumed = data.Length;
        return (T)_responses[typeof(T)];
    }
}
