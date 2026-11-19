// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Crypto;

/// <summary>Looks up public keys whose recovery inputs have already been verified.</summary>
internal sealed class VerifiedRecoveries
{
    private readonly byte[] _messages;
    private readonly byte[] _signatures;
    private readonly byte[] _recoveryIds;
    private readonly byte[] _keys;
    private Dictionary<RecoveryKey, int>? _indices;
    private int _next;

    internal VerifiedRecoveries(byte[] messages, byte[] signatures, byte[] recoveryIds, byte[] keys)
    {
        _messages = messages;
        _signatures = signatures;
        _recoveryIds = recoveryIds;
        _keys = keys;
    }

    internal bool TryGetKey(ReadOnlySpan<byte> signature, byte recoveryId, ReadOnlySpan<byte> message, Span<byte> publicKey)
    {
        if (message.Length != 32 || signature.Length != 64) return false;

        // ECRECOVER hints come in call order; EIP-7702 authorization hints follow all of them, so those fall back to the search.
        int index = _next;
        if (index >= _recoveryIds.Length || !Matches(index, signature, recoveryId, message))
        {
            if (_recoveryIds.Length > 64)
            {
                _indices ??= BuildIndex();
                if (!_indices.TryGetValue(Key(signature, recoveryId, message), out index)) return false;
            }
            else
            {
                for (index = 0; index < _recoveryIds.Length; index++)
                    if (Matches(index, signature, recoveryId, message)) break;

                if (index == _recoveryIds.Length) return false;
            }
        }

        _keys.AsSpan(index * 64, 64).CopyTo(publicKey);
        _next = index + 1;
        return true;
    }

    private bool Matches(int index, ReadOnlySpan<byte> signature, byte recoveryId, ReadOnlySpan<byte> message) =>
        _recoveryIds[index] == recoveryId
        && message.SequenceEqual(_messages.AsSpan(index * 32, 32))
        && signature.SequenceEqual(_signatures.AsSpan(index * 64, 64));

    private Dictionary<RecoveryKey, int> BuildIndex()
    {
        Dictionary<RecoveryKey, int> indices = [with(_recoveryIds.Length)];
        for (int i = 0; i < _recoveryIds.Length; i++)
            indices.TryAdd(Key(_signatures.AsSpan(i * 64, 64), _recoveryIds[i], _messages.AsSpan(i * 32, 32)), i);
        return indices;
    }

    private static RecoveryKey Key(ReadOnlySpan<byte> signature, byte recoveryId, ReadOnlySpan<byte> message) =>
        new(new ValueHash256(message), new ValueHash256(signature[..32]), new ValueHash256(signature[32..]), recoveryId);

    private readonly record struct RecoveryKey(ValueHash256 Message, ValueHash256 R, ValueHash256 S, byte RecoveryId);
}
