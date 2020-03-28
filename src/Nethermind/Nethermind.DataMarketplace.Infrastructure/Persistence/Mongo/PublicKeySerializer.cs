//  Copyright (c) 2018 Demerzel Solutions Limited
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

using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using Nethermind.Core.Crypto;
#pragma warning disable 8609

namespace Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo
{
    public class PublicKeySerializer : SerializerBase<PublicKey>
    {
        public override PublicKey? Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
        {
            string value = context.Reader.ReadString();
            return string.IsNullOrWhiteSpace(value) ? null : new PublicKey(value);
        }

        public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args,
            PublicKey? value) => context.Writer.WriteString(value?.ToString() ?? string.Empty);
    }
}