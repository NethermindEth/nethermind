// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
