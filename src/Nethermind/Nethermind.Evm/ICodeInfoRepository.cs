// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.State;

namespace Nethermind.Evm;

public interface ICodeInfoRepository
{
    CodeInfo GetCachedCodeInfo(IWorldState worldState, Address codeSource, IReleaseSpec vmSpec);
    CodeInfo GetOrAdd(ValueHash256 codeHash, ReadOnlySpan<byte> initCode);
    void InsertCode(IWorldState state, ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec);
}
