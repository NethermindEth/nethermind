// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus;
public interface IHeaderSigner : ISigner
{
    bool CanSignHeader { get; }
    Signature Sign(BlockHeader header);
}
