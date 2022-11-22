// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public interface IRlpNdmDecoder<T> : IRlpStreamDecoder<T>, IRlpObjectDecoder<T>
    {
    }
}
