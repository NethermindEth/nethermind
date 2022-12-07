// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo
{
    public class UInt256Serializer : SerializerBase<UInt256>
    {
        public override UInt256 Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
            => UInt256.Parse(context.Reader.ReadString());

        public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, UInt256 value)
            => context.Writer.WriteString(value.ToString());
    }
}
