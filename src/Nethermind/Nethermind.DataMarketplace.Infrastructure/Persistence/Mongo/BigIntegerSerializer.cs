// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo
{
    public class BigIntegerSerializer : SerializerBase<BigInteger>
    {
        public override BigInteger Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
            => BigInteger.Parse(context.Reader.ReadString());

        public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args,
            BigInteger value) => context.Writer.WriteString(value.ToString());
    }
}
