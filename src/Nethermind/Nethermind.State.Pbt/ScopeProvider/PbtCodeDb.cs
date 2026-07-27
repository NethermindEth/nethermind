// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Evm.State;

namespace Nethermind.State.Pbt.ScopeProvider;

/// <summary>Captures newly written code so the owning scope can chunk it into the tree at commit time.</summary>
public sealed class PbtCodeDb(IWorldStateScopeProvider.ICodeDb inner, Dictionary<ValueHash256, byte[]> pendingCode) : IWorldStateScopeProvider.ICodeDb
{
    public byte[]? GetCode(in ValueHash256 codeHash) =>
        pendingCode.TryGetValue(codeHash, out byte[]? code) ? code : inner.GetCode(codeHash);

    public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite() => new CapturingCodeSetter(inner.BeginCodeWrite(), pendingCode);

    public bool ContainsCode(in ValueHash256 codeHash) => inner.ContainsCode(codeHash);

    public void MarkCodePersisted(in ValueHash256 codeHash) => inner.MarkCodePersisted(codeHash);

    private sealed class CapturingCodeSetter(IWorldStateScopeProvider.ICodeSetter inner, Dictionary<ValueHash256, byte[]> pendingCode) : IWorldStateScopeProvider.ICodeSetter
    {
        public void Set(in ValueHash256 codeHash, ReadOnlySpan<byte> code)
        {
            pendingCode[codeHash] = code.ToArray();
            inner.Set(codeHash, code);
        }

        public void Dispose() => inner.Dispose();
    }
}
