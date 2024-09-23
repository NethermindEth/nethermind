// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Lantern.Discv5.Enr;
using Lantern.Discv5.Rlp;
using Lantern.Discv5.WireProtocol.Packet.Handlers;
using Lantern.Discv5.WireProtocol.Session;
using Lantern.Discv5.WireProtocol.Table;
using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Network.Discovery.Portal.LanternAdapter;

public static class IServiceCollectionExtensions
{
    public static IServiceCollection ConfigureLanternPortalAdapter(this IServiceCollection services)
    {
        return services
            .AddSingleton<IPacketHandlerFactory, CustomPacketHandlerFactory>()
            .AddSingleton<IRoutingTable, TransientRoutingTable>()
            .AddSingleton<ISessionManager, SessionManagerNormalizer>()
            .AddSingleton<IRawTalkReqSender, LanternTalkReqSender>()
            .AddSingleton<IEnrProvider, LanternIEnrProvider>();
    }
    public class AllEnrEntryRegistry : IEnrEntryRegistry
    {
        private readonly IEnrEntryRegistry _baseRegistry = new EnrEntryRegistry();
        public void RegisterEntry(string key, Func<byte[], IEntry> entryCreator)
        {
            _baseRegistry.RegisterEntry(key, entryCreator);
        }

        public void UnregisterEntry(string key)
        {
            _baseRegistry.UnregisterEntry(key);
        }

        public IEntry? GetEnrEntry(string stringKey, byte[] value)
        {
            var entry = _baseRegistry.GetEnrEntry(stringKey, value);
            if (entry == null)
            {
                Console.Error.WriteLine($"Unknown enr key {stringKey}");
                return new RawByteIEnr(stringKey, value);
            }
            return entry;
        }

        public class RawByteIEnr(string key, byte[] bytes) : IEntry
        {
            public EnrEntryKey Key => key;
            public IEnumerable<byte> EncodeEntry()
            {
                return ByteArrayUtils.JoinByteArrays(RlpEncoder.EncodeString(key, Encoding.ASCII),
                    RlpEncoder.EncodeBytes(bytes));
            }
        }
    }

}
