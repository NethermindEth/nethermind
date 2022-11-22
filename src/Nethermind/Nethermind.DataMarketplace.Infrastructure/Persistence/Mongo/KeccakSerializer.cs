// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using Nethermind.Core.Crypto;
#pragma warning disable 8609

namespace Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo
{
    public class KeccakSerializer : SerializerBase<Keccak>
    {
        public override Keccak? Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
        {
            string value = context.Reader.ReadString();

            return string.IsNullOrWhiteSpace(value) ? null : new Keccak(value);
        }

        public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, Keccak? value)
            => context.Writer.WriteString(value?.ToString() ?? string.Empty);
    }
}
