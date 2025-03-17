using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.Enr.Identity.V4;
using Lantern.Discv5.Enr;
using Lantern.Discv5.WireProtocol.Connection;
using Lantern.Discv5.WireProtocol.Session;
using Lantern.Discv5.WireProtocol.Table;
using Lantern.Discv5.WireProtocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Nethermind.Core;
using Nethermind.Network.Portal.History;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net;
using Nethermind.Crypto;
using Nethermind.Network.Discovery;
using Nethermind.Logging;
using Nethermind.Network.Portal;
using Nethermind.Network.Portal.LanternAdapter;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core.Crypto;
using Nethermind.Blockchain.Visitors;
using Nethermind.Core.Collections;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Network.Config;
using Nethermind.Network.Kademlia;
using DotNetty.Transport.Bootstrapping;
using Nethermind.Serialization.Json;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Facade.Eth;
using System.Buffers.Binary;
using System.Diagnostics;
#pragma warning disable CS0219 // Variable is assigned but its value is never used

int localPort = 30304;
IPAddress localIPAddress = IPAddress.Any;

EthereumJsonSerializer json = new();
var trin = "enr:-JS4QP-LPN7KyeBC1x0IUDgua0-AdQyeAlr7mgbaG2ceyKqMIESTDaH1yvwuyQ6etcWUJOCBYR_6M_es0mOU3GGTcMCEZ4EQT2OKdCBjOTNlMTI5ZIJpZIJ2NIJpcISLsbU9iXNlY3AyNTZrMaEDT6KDCWWJCqGnQlJ-fRit89uGtsKlT582MsBHJ9IPzMWDdWRwgiMx";

TaskCompletionSource tcs = new();

Nethermind.Logging.ILogger _logger = SimpleConsoleLogManager.Instance.GetClassLogger();

var _connections = new DiscoveryConnectionsPool(SimpleConsoleLogManager.Instance.GetClassLogger<DiscoveryConnectionsPool>(), new NetworkConfig() { LocalIp = "0.0.0.0" }, new DiscoveryConfig());

IdentityVerifierV4 identityVerifier = new();

PrivateKey k = new(Convert.FromHexString($"DDB9DB40CAAA9D145481D0C5B77F54BA61F33F59B6E0427616FCCB0326C{localPort.ToString().PadLeft(5, '0')}"));

SessionOptions _sessionOptions = new()
{
    Signer = new IdentitySignerV4(k.KeyBytes),
    Verifier = identityVerifier,
    SessionKeys = new SessionKeys(k.KeyBytes),
};
NettyDiscoveryV5Handler handler = new NettyDiscoveryV5Handler(SimpleConsoleLogManager.Instance)!;

IServiceCollection services = new ServiceCollection()
   .AddSingleton<ILoggerFactory, NullLoggerFactory>()
   .AddSingleton((IBlockTree)new EmptyBlockTree())
   .AddSingleton<ISyncConfig>(new SyncConfig())
   .AddSingleton((IReceiptStorage)new EmptyReceiptStorage())
   .AddSingleton((IReceiptFinder)new EmptyReceiptFinder())
   .AddSingleton<NettyDiscoveryV5Handler>(handler)
   .AddSingleton<IUdpConnection>(handler)
   .AddSingleton(SimpleConsoleLogManager.Instance)
   .AddSingleton(_sessionOptions.Verifier)
   .AddSingleton(_sessionOptions.Signer);

IEnrEntryRegistry registry = new EnrEntryRegistry();
registry.RegisterEntry("c", (b) => new RawEntry("c", b));
registry.RegisterEntry("quic", (b) => new RawEntry("quic", b));
registry.RegisterEntry("domaintype", (b) => new RawEntry("domaintype", b));
registry.RegisterEntry("subnets", (b) => new RawEntry("subnets", b));
registry.RegisterEntry("eth", (b) => new RawEntry("eth", b));
registry.RegisterEntry("v4", (b) => new RawEntry("v4", b));
registry.RegisterEntry("opstack", (b) => new RawEntry("opstack", b));

EnrFactory enrFactory = new(registry);
var s = $"{enrFactory.CreateFromString(trin, identityVerifier):ea}";

IPAddress heh = NetworkInterface.GetAllNetworkInterfaces()!
.Where(i => i.Name == "eth0" ||
(i.OperationalStatus == OperationalStatus.Up &&
             i.NetworkInterfaceType == NetworkInterfaceType.Ethernet &&
             i.GetIPProperties().GatewayAddresses.Any())
         ).First()
         .GetIPProperties()
         .UnicastAddresses
         .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address).First();



Bootstrap bootstrap = new();
bootstrap.Group(new MultithreadEventLoopGroup(1));
bootstrap.Channel<SocketDatagramChannel2>();

bootstrap.Handler(new ActionChannelInitializer<IDatagramChannel>((channel) =>
{
    handler.InitializeChannel(channel);
    channel.Pipeline.AddLast(handler);
    tcs.SetResult();
}));

await _connections.BindAsync(bootstrap, localPort);

await tcs.Task;

_logger.Warn($"Discv5 IP address: {Shared.Ip!.MapToIPv4()}:{Shared.Port}");

EnrBuilder enrBuilder = new EnrBuilder()
    .WithIdentityScheme(_sessionOptions.Verifier, _sessionOptions.Signer)
    .WithEntry(EnrEntryKey.Id, new EntryId("v4"))
    .WithEntry("c", new RawEntry("c", "n"u8.ToArray()))
    .WithEntry(EnrEntryKey.Secp256K1, new EntrySecp256K1(_sessionOptions.Signer.PublicKey))
    .WithEntry(EnrEntryKey.Ip, new EntryIp(Shared.Ip!.MapToIPv4()))
    .WithEntry(EnrEntryKey.Udp, new EntryUdp(Shared.Port));

IDiscv5ProtocolBuilder discv5Builder = new Discv5ProtocolBuilder(services)
    .WithConnectionOptions(new ConnectionOptions
    {
        UdpPort = localPort
    })
    .WithSessionOptions(_sessionOptions)
    .WithTableOptions(new TableOptions([]))
    .WithEnrBuilder(enrBuilder)
    .WithEnrEntryRegistry(registry)
    .WithLoggerFactory(new NethermindLoggerFactory(SimpleConsoleLogManager.Instance, true))
    .WithServices(s =>
    {
        NettyDiscoveryV5Handler.Register(s);
        s
        .ConfigurePortalNetworkCommonServices()
        .ConfigureLanternPortalAdapter();
    });

//var _discv5Protocol = NetworkHelper.HandlePortTakenError(discv5Builder.Build, port);
IDiscv5Protocol proto = discv5Builder.Build();

IServiceProvider _serviceProvider = discv5Builder.GetServiceProvider();



_logger.Warn($"My enr: {proto.SelfEnr}");

await proto.InitAsync();
string[] bootNodesStr =
[
    ////Trin bootstrap nodes
    ////trin,
    //"enr:-Jy4QIs2pCyiKna9YWnAF0zgf7bT0GzlAGoF8MEKFJOExmtofBIqzm71zDvmzRiiLkxaEJcs_Amr7XIhLI74k1rtlXICY5Z0IDAuMS4xLWFscGhhLjEtMTEwZjUwgmlkgnY0gmlwhKEjVaWJc2VjcDI1NmsxoQLSC_nhF1iRwsCw0n3J4jRjqoaRxtKgsEe5a-Dz7y0JloN1ZHCCIyg",
    //"enr:-Jy4QKSLYMpku9F0Ebk84zhIhwTkmn80UnYvE4Z4sOcLukASIcofrGdXVLAUPVHh8oPCfnEOZm1W1gcAxB9kV2FJywkCY5Z0IDAuMS4xLWFscGhhLjEtMTEwZjUwgmlkgnY0gmlwhJO2oc6Jc2VjcDI1NmsxoQLMSGVlxXL62N3sPtaV-n_TbZFCEM5AR7RDyIwOadbQK4N1ZHCCIyg",
    //"enr:-Jy4QH4_H4cW--ejWDl_W7ngXw2m31MM2GT8_1ZgECnfWxMzZTiZKvHDgkmwUS_l2aqHHU54Q7hcFSPz6VGzkUjOqkcCY5Z0IDAuMS4xLWFscGhhLjEtMTEwZjUwgmlkgnY0gmlwhJ31OTWJc2VjcDI1NmsxoQPC0eRkjRajDiETr_DRa5N5VJRm-ttCWDoO1QAMMCg5pIN1ZHCCIyg",

    ////Fluffy bootstrap nodes
    //"enr:-Ia4QLBxlH0Y8hGPQ1IRF5EStZbZvCPHQ2OjaJkuFMz0NRoZIuO2dLP0L-W_8ZmgnVx5SwvxYCXmX7zrHYv0FeHFFR0TY2aCaWSCdjSCaXCEwiErIIlzZWNwMjU2azGhAnnTykipGqyOy-ZRB9ga9pQVPF-wQs-yj_rYUoOqXEjbg3VkcIIjjA",
    //"enr:-Ia4QM4amOkJf5z84Lv5Fl0RgWeSSDUekwnOPRn6XA1eMWgrHwWmn_gJGtOeuVfuX7ywGuPMRwb0odqQ9N_w_2Qc53gTY2aCaWSCdjSCaXCEwiErIYlzZWNwMjU2azGhAzaQEdPmz9SHiCw2I5yVAO8sriQ-mhC5yB7ea1u4u5QZg3VkcIIjjA",
    //"enr:-Ia4QKVuHjNafkYuvhU7yCvSarNIVXquzJ8QOp5YbWJRIJw_EDVOIMNJ_fInfYoAvlRCHEx9LUQpYpqJa04pUDU21uoTY2aCaWSCdjSCaXCEwiErQIlzZWNwMjU2azGhA47eAW5oIDJAqxxqI0sL0d8ttXMV0h6sRIWU4ZwS4pYfg3VkcIIjjA",
    //"enr:-Ia4QIU9U3zrP2DM7sfpgLJbbYpg12sWeXNeYcpKN49-6fhRCng0IUoVRI2E51mN-2eKJ4tbTimxNLaAnbA7r7fxVjcTY2aCaWSCdjSCaXCEwiErQYlzZWNwMjU2azGhAxOroJ3HceYvdD2yK1q9w8c9tgrISJso8q_JXI6U0Xwng3VkcIIjjA",

    ////Ultralight bootstrap nodes
    //"enr:-IS4QFV_wTNknw7qiCGAbHf6LxB-xPQCktyrCEZX-b-7PikMOIKkBg-frHRBkfwhI3XaYo_T-HxBYmOOQGNwThkBBHYDgmlkgnY0gmlwhKRc9_OJc2VjcDI1NmsxoQKHPt5CQ0D66ueTtSUqwGjfhscU_LiwS28QvJ0GgJFd-YN1ZHCCE4k",
    ////"enr:-IS4QDpUz2hQBNt0DECFm8Zy58Hi59PF_7sw780X3qA0vzJEB2IEd5RtVdPUYZUbeg4f0LMradgwpyIhYUeSxz2Tfa8DgmlkgnY0gmlwhKRc9_OJc2VjcDI1NmsxoQJd4NAVKOXfbdxyjSOUJzmA4rjtg43EDeEJu1f8YRhb_4N1ZHCCE4o",
    //"enr:-IS4QGG6moBhLW1oXz84NaKEHaRcim64qzFn1hAG80yQyVGNLoKqzJe887kEjthr7rJCNlt6vdVMKMNoUC9OCeNK-EMDgmlkgnY0gmlwhKRc9-KJc2VjcDI1NmsxoQLJhXByb3LmxHQaqgLDtIGUmpANXaBbFw3ybZWzGqb9-IN1ZHCCE4k",
    //"enr:-IS4QA5hpJikeDFf1DD1_Le6_ylgrLGpdwn3SRaneGu9hY2HUI7peHep0f28UUMzbC0PvlWjN8zSfnqMG07WVcCyBhADgmlkgnY0gmlwhKRc9-KJc2VjcDI1NmsxoQJMpHmGj1xSP1O-Mffk_jYIHVcg6tY5_CjmWVg1gJEsPIN1ZHCCE4o"

    //"enr:-Ii4QFAIi3_aJfuHapjnVCfDh4BgHqND2PVTBncv1iPELQj-egf71SVrLVWpZtNcwVSOmtu3wHwoIwjT2zOCUi2ykR-CGSdjZoJpZIJ2NIJpcITCIStZiXNlY3AyNTZrMaEDMOi5OBRftLAQpfK2gN5L0CPAeEnd9FTLQBRkYR191bWDdWRwgiOM",
    //"enr:-Ii4QLyPY1f3EpQjww8I31V2knEsYTllk74hGXsK5-tCuViPDLvCzFuozVJpM_t0oc9RXXTOJ0YmD1ON5Lqe1KcajSGCEodjZoJpZIJ2NIJpcITCIStNiXNlY3AyNTZrMaECDNlp0U6kzM_snZBXWnv-JXk1IwzWJP5e8lbfb6RjxOyDdWRwgiOM",
    //"enr:-Ii4QHoeGc7ytlq4V2gq77U9Uyqn2FhVx8abdFDskl-7n0LXbnD4iaADqGYMCDjKszALBFxOACMC9pdnl6kDBr_GjlmCFB9jZoJpZIJ2NIJpcITCISsliXNlY3AyNTZrMaECNiZslK14i4CFMYBHGxPkH_Z3VWsdf3Zg0K5t2QunMR-DdWRwgiOM",
    "enr:-Ii4QADcN5hwh2ezIyWq2iCt4kkRocrahpiAsWhsq_RqffYpKDHa22w267lOyMActkcVhQq2ZP1ok09zWwHxPhFsbLWCFcdjZoJpZIJ2NIJpcITCISsuiXNlY3AyNTZrMaECc52FNY8CKrp0Ie4LFvh1BQJc6wkI03WU3GQSzQP14TWDdWRwgiOM"
];
IEnr[] historyNetworkBootnodes = bootNodesStr.Select((str) => enrFactory.CreateFromString(str, identityVerifier)).ToArray();

IServiceProvider historyNetworkServiceProvider = _serviceProvider.CreateHistoryNetworkServiceProviderWithRpc(historyNetworkBootnodes);
IPortalHistoryNetwork historyNetwork = historyNetworkServiceProvider.GetRequiredService<IPortalHistoryNetwork>();

//var p = await proto.SendPingAsync(enrFactory.CreateFromString("enr:-Jy4QIs2pCyiKna9YWnAF0zgf7bT0GzlAGoF8MEKFJOExmtofBIqzm71zDvmzRiiLkxaEJcs_Amr7XIhLI74k1rtlXICY5Z0IDAuMS4xLWFscGhhLjEtMTEwZjUwgmlkgnY0gmlwhKEjVaWJc2VjcDI1NmsxoQLSC_nhF1iRwsCw0n3J4jRjqoaRxtKgsEe5a-Dz7y0JloN1ZHCCIyg", identityVerifier));
//PongMessage? p = await proto.SendPingAsync(enrFactory.CreateFromString("enr:-JS4QGc-JGSrDwji6mkl9Er86OUndfArTYVNFX3e_uJXeXvoWYQOFOONzY_cTRr3UjXlvjWqdMjMhCk6ceeIsDVo8o6EZ21lWWOKdCBjOTNlMTI5ZIJpZIJ2NIJpcISLsbU9iXNlY3AyNTZrMaEDUVl8sas6LoPjnr604i6eeUon2nYkueluBh0JAFrDKf-DdWRwgiMx", identityVerifier));

IKademlia<IEnr>? kad = historyNetworkServiceProvider.GetService<IKademlia<IEnr>>();
IPortalHistoryNetwork net = historyNetworkServiceProvider.GetService<IPortalHistoryNetwork>()!;


_logger.Warn($"Ready");

int h = 0, b = 0, r = 0;

Console.WriteLine("Started: {0}", DateTime.Now);
var sw = Stopwatch.StartNew();
await Task.Delay(3000);
ulong N = 1;
ulong startAt = 20537393;
ulong i = 0;

for (i = startAt - N; i < startAt; i++)
{
    try
    {
        Console.WriteLine("Querying: {0}", i);

        BlockHeader? head = await net.LookupBlockHeader(i, default);
        Console.WriteLine("Header: {0}", json.Serialize(head));

        if (head is null)
        {
            Console.WriteLine("Retry: head is null {0}", i);
            i--;
            h++;
            if (h > 0)
            {
                Console.WriteLine("Retry: head is null {0}", i);

                break;
            }
            continue;
        }

        BlockBody? bodyByHash = head is not null ? await net.LookupBlockBody(head.Hash, default) : null;
        Console.WriteLine("Body: {0}", bodyByHash is not null ? json.Serialize(new BlockForRpc(new Block(head!, bodyByHash), true, null)) : null);

        if (bodyByHash is null)
        {
            Console.WriteLine("Retry: body is null {0}", i);
            i--;
            b++;
            continue;
        }


        TxReceipt[]? receiptsByHash = head is not null ? await net.LookupReceipts(head.Hash, default) : null;
        Console.WriteLine("Receitps: {0}", receiptsByHash?.Length);

        if (receiptsByHash is null)
        {
            Console.WriteLine("Retry: receipts are null {0}", i);
            i--;
            r++;
            continue;
        }
        //BlockHeader? headByHash = head is not null ? await net.LookupBlockHeader(head.Hash, default) : null;
        //BlockBody? bodyByHash = head is not null ? await net.LookupBlockBody(head.Hash, default) : null;
        //TxReceipt[]? receiptByHash = head is not null ? await net.LookupReceipts(head.Hash, default) : null;
    }
    catch
    {

    }
}

i++;

Console.WriteLine("Ended: {0} {1} {2} {3} {4} {5} {6}", DateTime.Now, h, b, r, sw.Elapsed, i, i - startAt);


#region stub
class EmptyBlockTree : IBlockTree
{
    public ulong NetworkId => 1;

    public ulong ChainId => 1;

    public BlockHeader? Genesis => null;

    public BlockHeader? BestSuggestedHeader => null;

    public Block? BestSuggestedBody => null;

    public BlockHeader? BestSuggestedBeaconHeader => null;

    public BlockHeader? LowestInsertedHeader { get; set; }
    public BlockHeader? LowestInsertedBeaconHeader { get; set; }

    public long BestKnownNumber => 0;

    public long BestKnownBeaconNumber => 0;

    public bool CanAcceptNewBlocks => false;

    public Hash256 HeadHash => Hash256.Zero;

    public Hash256 GenesisHash => Hash256.Zero;

    public Hash256? PendingHash => Hash256.Zero;

    public Hash256? FinalizedHash => Hash256.Zero;

    public Hash256? SafeHash => Hash256.Zero;

    public Block? Head => null;

    public long? BestPersistedState { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    public event EventHandler<BlockEventArgs> NewBestSuggestedBlock = (s, e) => { };
    public event EventHandler<BlockEventArgs> NewSuggestedBlock = (s, e) => { };
    public event EventHandler<BlockReplacementEventArgs> BlockAddedToMain = (s, e) => { };
    public event EventHandler<BlockEventArgs> NewHeadBlock = (s, e) => { };
    public event EventHandler<OnUpdateMainChainArgs> OnUpdateMainChain = (s, e) => { };

    public Task Accept(IBlockTreeVisitor blockTreeVisitor, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public int DeleteChainSlice(in long startNumber, long? endNumber = null, bool force = false)
    {
        return default!;
    }

    public void DeleteInvalidBlock(Block invalidBlock)
    {

    }

    public BlockHeader FindBestSuggestedHeader()
    {
        return default!;
    }

    public Block? FindBlock(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null)
    {
        return default!;
    }

    public Block? FindBlock(long blockNumber, BlockTreeLookupOptions options)
    {
        return default!;
    }

    public Hash256? FindBlockHash(long blockNumber)
    {
        return default!;
    }

    public BlockInfo FindCanonicalBlockInfo(long blockNumber)
    {
        return default!;
    }

    public Hash256 FindHash(long blockNumber)
    {
        return default!;
    }

    public BlockHeader? FindHeader(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null)
    {
        return default!;
    }

    public BlockHeader? FindHeader(long blockNumber, BlockTreeLookupOptions options)
    {
        return default!;
    }

    public IOwnedReadOnlyList<BlockHeader> FindHeaders(Hash256 hash, int numberOfBlocks, int skip, bool reverse)
    {
        return default!;
    }

    public ChainLevelInfo? FindLevel(long number)
    {
        return default!;
    }

    public void ForkChoiceUpdated(Hash256? finalizedBlockHash, Hash256? safeBlockBlockHash)
    {

    }

    public (BlockInfo? Info, ChainLevelInfo? Level) GetInfo(long number, Hash256 blockHash)
    {
        return default!;
    }

    public bool HasBlock(long blockNumber, Hash256 blockHash)
    {
        return default!;
    }

    public AddBlockResult Insert(BlockHeader header, BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.None)
    {
        return default!;
    }

    public AddBlockResult Insert(Block block, BlockTreeInsertBlockOptions insertBlockOptions = BlockTreeInsertBlockOptions.None, BlockTreeInsertHeaderOptions insertHeaderOptions = BlockTreeInsertHeaderOptions.None, WriteFlags bodiesWriteFlags = WriteFlags.None)
    {
        return default!;
    }

    public bool IsBetterThanHead(BlockHeader? header)
    {
        return default!;
    }

    public bool IsKnownBeaconBlock(long number, Hash256 blockHash)
    {
        return default!;
    }

    public bool IsKnownBlock(long number, Hash256 blockHash)
    {
        return default!;
    }

    public bool IsMainChain(BlockHeader blockHeader)
    {
        return default!;
    }

    public bool IsMainChain(Hash256 blockHash, bool throwOnMissingHash = true)
    {
        return default!;
    }

    public void MarkChainAsProcessed(IReadOnlyList<Block> blocks)
    {

    }

    public void RecalculateTreeLevels()
    {
    }

    public AddBlockResult SuggestBlock(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess)
    {
        return default!;
    }

    public ValueTask<AddBlockResult> SuggestBlockAsync(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess)
    {
        return default!;
    }

    public AddBlockResult SuggestHeader(BlockHeader header)
    {
        return default!;
    }

    public void UpdateBeaconMainChain(BlockInfo[]? blockInfos, long clearBeaconMainChainStartPoint)
    {

    }

    public void UpdateHeadBlock(Hash256 blockHash)
    {

    }

    public void UpdateMainChain(IReadOnlyList<Block> blocks, bool wereProcessed, bool forceHeadBlock = false)
    {

    }

    public bool WasProcessed(long number, Hash256 blockHash)
    {
        return default!;
    }
}

internal class EmptyReceiptFinder : IReceiptFinder
{
    public bool CanGetReceiptsByHash(long blockNumber)
    {
        return default!;
    }

    public Hash256? FindBlockHash(Hash256 txHash)
    {

        return default!;
    }

    public TxReceipt[] Get(Block block, bool recover = true, bool recoverSender = true)
    {

        return default!;
    }

    public TxReceipt[] Get(Hash256 blockHash, bool recover = true)
    {

        return default!;
    }

    public bool TryGetReceiptsIterator(long blockNumber, Hash256 blockHash, out ReceiptsIterator iterator)
    {
        iterator = default;
        return default!;
    }
}

internal class EmptyReceiptStorage : IReceiptStorage
{
    public long MigratedBlockNumber { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    public event EventHandler<BlockReplacementEventArgs> ReceiptsInserted = (s, e) => { };

    public bool CanGetReceiptsByHash(long blockNumber)
    {
        return default;
    }

    public void EnsureCanonical(Block block)
    {

    }

    public Hash256? FindBlockHash(Hash256 txHash)
    {
        return default;
    }

    public TxReceipt[] Get(Block block, bool recover = true, bool recoverSender = true)
    {
        return default!;
    }

    public TxReceipt[] Get(Hash256 blockHash, bool recover = true)
    {
        return default!;
    }

    public bool HasBlock(long blockNumber, Hash256 hash)
    {
        return default;
    }

    public void Insert(Block block, TxReceipt[]? txReceipts, bool ensureCanonical, WriteFlags writeFlags = WriteFlags.None)
    {

    }

    public bool TryGetReceiptsIterator(long blockNumber, Hash256 blockHash, out ReceiptsIterator iterator)
    {
        iterator = default;
        return default;
    }
}

#endregion
class SocketDatagramChannel2 : SocketDatagramChannel
{
    public SocketDatagramChannel2()
            : this(new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
    {
    }

    public SocketDatagramChannel2(AddressFamily addressFamily)
        : this(new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
    {
    }

    public SocketDatagramChannel2(Socket socket)
        : base(socket)
    {

    }

    protected override void DoBind(EndPoint localAddress)
    {
        try
        {
            this.Socket.Bind(localAddress);
            this.CacheLocalAddress();

            EndPoint handMadeStunAddr = IPEndPoint.Parse("139.177.181.61:9003");
            // EndPoint handMadeStunAddr = IPEndPoint.Parse("127.0.0.1:9003");
            //Thread.Sleep(100);
            Socket.SendTo(new[] { (byte)0x42 }, handMadeStunAddr);
            byte[] buf = new byte[1280];
            Thread.Sleep(130);

            Socket.ReceiveFrom(buf, ref handMadeStunAddr);
            Shared.Ip = new IPAddress(buf[0..4]);
            Shared.Port = BinaryPrimitives.ReadUInt16BigEndian(buf[4..6]);
            //Shared.Ip = IPAddress.Parse("178.172.225.183");
            //Shared.Port = 30304;
            this.SetState(StateFlags.Active);
        }
        catch
        {
            throw;
        }
    }

    protected override bool DoConnect(EndPoint remoteAddress, EndPoint localAddress)
    {
        return base.DoConnect(remoteAddress, localAddress);
    }

    protected override int DoReadMessages(List<object> buf)
    {
        return base.DoReadMessages(buf);
    }

    protected override void DoWrite(ChannelOutboundBuffer input)
    {
        try
        {
            base.DoWrite(input);
        }
        catch
        {
            throw;
        }
    }
}

static class Shared
{
    public static IPAddress? Ip { get; set; } = IPAddress.Parse("178.172.225.183");
    public static int Port { get; set; } = 30304;
}
