// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;

namespace Nethermind.State;

public class KeyValueWithBatchingBackedCodeDb(IKeyValueStoreWithBatching codeDb) : IWorldStateScopeProvider.ICodeDb
{
    public byte[]? GetCode(in ValueHash256 codeHash)
    {
        return codeDb[codeHash.Bytes]?.ToArray();
    }

    public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite()
    {
        return new CodeSetter(codeDb.StartWriteBatch());
    }

    private class CodeSetter(IWriteBatch writeBatch) : IWorldStateScopeProvider.ICodeSetter
    {
        public void Set(in ValueHash256 codeHash, ReadOnlySpan<byte> code)
        {
            writeBatch.PutSpan(codeHash.Bytes, code);
        }

        public void Dispose()
        {
            writeBatch.Dispose();
        }
    }
}
