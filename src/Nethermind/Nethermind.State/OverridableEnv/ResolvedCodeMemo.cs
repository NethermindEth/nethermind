// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.State.OverridableEnv;

/// <summary>
/// The code each address resolved to in the current env scope, which <see cref="MemoizingCodeInfoRepository"/>
/// answers repeated lookups from.
/// </summary>
/// <remarks>
/// The env that registers it clears it when a scope closes and, through
/// <see cref="ResolvedCodeClearingTransactionProcessor"/>, after every transaction that keeps its changes.
/// </remarks>
public sealed class ResolvedCodeMemo
{
    // The gain comes from a few hot contracts; the cap and the trim on clear keep a pooled env from holding grown tables.
    private const int MaxRemembered = 4096;
    private const int RetainedCapacity = 256;

    private readonly Dictionary<AddressAsKey, CodeInfo> _resolved = [];
    private readonly HashSet<AddressAsKey> _codeWritten = [];

    // The last address found, which answers a repeat without the dictionary probe: a call's frames often ask for the same
    // code in a row, as a quoter calling one pool manager thousands of times does.
    private Address? _lastAddress;
    private CodeInfo? _lastCode;

    /// <summary>Gets the code remembered for <paramref name="address"/> in this scope.</summary>
    public bool TryGet(Address address, [NotNullWhen(true)] out CodeInfo? codeInfo)
    {
        if (_lastCode is not null && (ReferenceEquals(address, _lastAddress) || address.Equals(_lastAddress)))
        {
            codeInfo = _lastCode;
            return true;
        }

        if (!_resolved.TryGetValue(address, out codeInfo)) return false;
        _lastAddress = address;
        _lastCode = codeInfo;
        return true;
    }

    /// <summary>Remembers <paramref name="codeInfo"/> unless the code at <paramref name="address"/> changed in this scope.</summary>
    public void Remember(Address address, CodeInfo codeInfo)
    {
        if (_resolved.Count >= MaxRemembered || _codeWritten.Contains(address)) return;
        _resolved[address] = codeInfo;
        if (address.Equals(_lastAddress)) _lastCode = codeInfo;
    }

    /// <summary>Drops <paramref name="address"/>, whose code is changing, and keeps it out until <see cref="Clear"/>.</summary>
    public void Forget(Address address)
    {
        _codeWritten.Add(address);
        _resolved.Remove(address);
        if (address.Equals(_lastAddress)) (_lastAddress, _lastCode) = (null, null);
    }

    /// <summary>Forgets everything, including which addresses had their code changed, before the next scope.</summary>
    public void Clear()
    {
        (_lastAddress, _lastCode) = (null, null);
        bool grown = _resolved.Count > RetainedCapacity;
        _resolved.Clear();
        if (grown) _resolved.TrimExcess(RetainedCapacity);
        _codeWritten.ClearAndTrim();
    }
}
