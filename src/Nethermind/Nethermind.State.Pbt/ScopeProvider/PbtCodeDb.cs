// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.State;

namespace Nethermind.State.Pbt.ScopeProvider;

/// <summary>Retains whole code in the flat branch while preserving the shared code database contract.</summary>
public sealed class PbtCodeDb(IWorldStateScopeProvider.ICodeDb inner, PbtSnapshotBundle bundle) : IWorldStateScopeProvider.ICodeDb
{
    public byte[]? GetCode(in ValueHash256 codeHash) => bundle.GetCode(codeHash)?.Code.ToArray() ?? inner.GetCode(codeHash);

    public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite() => new CapturingCodeSetter(inner.BeginCodeWrite(), bundle);

    public bool ContainsCode(in ValueHash256 codeHash) => bundle.GetCode(codeHash) is not null || inner.ContainsCode(codeHash);

    public void MarkCodePersisted(in ValueHash256 codeHash) => inner.MarkCodePersisted(codeHash);

    private sealed class CapturingCodeSetter(IWorldStateScopeProvider.ICodeSetter inner, PbtSnapshotBundle bundle) : IWorldStateScopeProvider.ICodeSetter
    {
        public void Set(in ValueHash256 codeHash, ReadOnlySpan<byte> code)
        {
            bundle.SetCode(codeHash, new CodeInfo(code.ToArray()) { CodeHash = codeHash });
            inner.Set(codeHash, code);
        }

        public void Dispose() => inner.Dispose();
    }
}
