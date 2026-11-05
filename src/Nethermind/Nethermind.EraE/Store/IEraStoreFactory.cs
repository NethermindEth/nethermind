// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.EraE.Store;

public interface IEraStoreFactory
{
    IEraStore Create(string src, ISet<ValueHash256>? trustedAccumulators);
}
