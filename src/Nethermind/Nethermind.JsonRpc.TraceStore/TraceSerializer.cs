//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.TraceStore;

public static class TraceSerializer
{
    private static readonly JsonSerializerOptions _jsonSerializerOptions = new() { Converters = { new KeccakUtf8Converter() }, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public static TCollection? Deserialize<TCollection>(Span<byte> serialized) where TCollection : IEnumerable<ParityLikeTxTrace> => JsonSerializer.Deserialize<TCollection>(serialized, _jsonSerializerOptions);
    public static byte[] Serialize(IReadOnlyCollection<ParityLikeTxTrace> traces) => JsonSerializer.SerializeToUtf8Bytes(traces, _jsonSerializerOptions);
}
