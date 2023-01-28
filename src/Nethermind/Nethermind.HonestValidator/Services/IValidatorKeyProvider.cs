// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.HonestValidator.Services
{
    public interface IValidatorKeyProvider
    {
        IList<BlsPublicKey> GetPublicKeys();
        BlsSignature SignRoot(BlsPublicKey blsPublicKey, Root root);
    }
}
