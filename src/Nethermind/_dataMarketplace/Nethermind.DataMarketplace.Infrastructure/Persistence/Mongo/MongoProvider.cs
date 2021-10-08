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

using System.Numerics;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo
{
    public class MongoProvider : IMongoProvider
    {
        private static bool _initialized;
        private static IMongoClient? _client;
        private readonly INdmMongoConfig _config;

        static MongoProvider()
        {
            RegisterConventions();
        }
        
        public MongoProvider(INdmMongoConfig config, ILogManager logManager)
        {
            _config = config;
            ILogger logger = logManager.GetClassLogger();
            if (_initialized)
            {
                return;
            }
            
            MongoUrl connectionUrl = new MongoUrl(config.ConnectionString);
            MongoClientSettings clientSettings = MongoClientSettings.FromUrl(connectionUrl);
            if (_config.LogQueries)
            {
                clientSettings.ClusterConfigurator = cb =>
                {
                    cb.Subscribe<CommandStartedEvent>(e =>
                    {
                        if (logger.IsInfo) logger.Info($"MongoDB command started '{e.CommandName}': {e.Command.ToJson()}");
                    });
                    cb.Subscribe<CommandSucceededEvent>(e =>
                    {
                        if (logger.IsInfo) logger.Info($"MongoDB command succeeded '{e.CommandName}': {e.Reply.ToJson()}");
                    });
                    cb.Subscribe<CommandFailedEvent>(e =>
                    {
                        if (logger.IsError) logger.Error($"MongoDB command failed '{e.CommandName}': {e.Failure}");
                    });
                };
            }

            _client = new MongoClient(clientSettings);
            _initialized = true;
        }

        public IMongoDatabase? GetDatabase() => _client?.GetDatabase(_config.Database);

        private static void RegisterConventions()
        {
            BsonSerializer.RegisterSerializer(typeof(decimal), new DecimalSerializer(BsonType.Decimal128));
            BsonSerializer.RegisterSerializer(typeof(Address), new AddressSerializer());
            BsonSerializer.RegisterSerializer(typeof(BigInteger), new BigIntegerSerializer());
            BsonSerializer.RegisterSerializer(typeof(Keccak), new KeccakSerializer());
            BsonSerializer.RegisterSerializer(typeof(PublicKey), new PublicKeySerializer());
            BsonSerializer.RegisterSerializer(typeof(Signature), new SignatureSerializer());
            BsonSerializer.RegisterSerializer(typeof(UInt256), new UInt256Serializer());
            ConventionRegistry.Register("Conventions", new MongoDbConventions(), _ => true);
        }
    }
}