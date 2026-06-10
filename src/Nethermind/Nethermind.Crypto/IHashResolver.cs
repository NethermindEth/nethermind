// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Crypto;

public interface IHashResolver
{
    /// <summary>
    /// Compute the header hash, honouring <paramref name="behaviors"/>. AuRa sealing passes
    /// <see cref="RlpBehaviors.ForSealing"/> so the seal section is excluded from the digest.
    /// </summary>
    ValueHash256 CalculateHash(RlpBehaviors behaviors = RlpBehaviors.None);
}
