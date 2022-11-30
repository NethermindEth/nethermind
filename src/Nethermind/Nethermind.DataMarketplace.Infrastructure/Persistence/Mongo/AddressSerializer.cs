// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using Nethermind.Core;
#pragma warning disable 8609

namespace Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo
{
    public class AddressSerializer : SerializerBase<Address>
    {
        public override Address? Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
        {
            string value = context.Reader.ReadString();

            return string.IsNullOrWhiteSpace(value) ? null : new Address(value);
        }

        public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, Address? value)
            => context.Writer.WriteString(value?.ToString());
    }
}
