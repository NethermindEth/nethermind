using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.Enr.Identity.V4;
using Lantern.Discv5.Rlp;
using Lantern.Discv5.WireProtocol;
using Lantern.Discv5.WireProtocol.Connection;
using Lantern.Discv5.WireProtocol.Session;
using Lantern.Discv5.WireProtocol.Table;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin.Secp256k1;
using Nethermind.Config;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
#pragma warning disable CS0219 // Variable is assigned but its value is never used

int localPort = 30304;
IPAddress localIPAddress = IPAddress.Any;

var trin = "enr:-JS4QP-LPN7KyeBC1x0IUDgua0-AdQyeAlr7mgbaG2ceyKqMIESTDaH1yvwuyQ6etcWUJOCBYR_6M_es0mOU3GGTcMCEZ4EQT2OKdCBjOTNlMTI5ZIJpZIJ2NIJpcISLsbU9iXNlY3AyNTZrMaEDT6KDCWWJCqGnQlJ-fRit89uGtsKlT582MsBHJ9IPzMWDdWRwgiMx";

byte[] k = Convert.FromHexString($"DDB9DB40CAAA9D145481D0C5B77F54BA61F33F59B6E0427616FCCB0326C{localPort.ToString().PadLeft(5, '0')}");

IdentityVerifierV4 identityVerifier = new();
var signer = new IdentitySignerV4(k);



SessionOptions _sessionOptions = new()
{
    Signer = signer,
    Verifier = identityVerifier,
    SessionKeys = new SessionKeys(k),
};


IServiceCollection services = new ServiceCollection()
   .AddSingleton<ILoggerFactory>(LoggerFactory.Create(builder => builder.AddConsole()))
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

EnrBuilder enrBuilder = new EnrBuilder()
    .WithIdentityScheme(_sessionOptions.Verifier, _sessionOptions.Signer)
    .WithEntry(EnrEntryKey.Id, new EntryId("v4"))
    .WithEntry("c", new UnrecognizedEntry("c", RlpDecoder.Decode(RlpEncoder.EncodeBytes("n"u8.ToArray()))[0]))
    .WithEntry(EnrEntryKey.Secp256K1, new EntrySecp256K1(_sessionOptions.Signer.PublicKey))
    .WithEntry(EnrEntryKey.Ip, new EntryIp(Shared.Ip!.MapToIPv4()))
    .WithEntry(EnrEntryKey.Udp, new EntryUdp(Shared.Port));

string[] bootNodesStr = new string[] {

 //   // Teku team's bootnode
	//"enr:-KG4QMOEswP62yzDjSwWS4YEjtTZ5PO6r65CPqYBkgTTkrpaedQ8uEUo1uMALtJIvb2w_WWEVmg5yt1UAuK1ftxUU7QDhGV0aDKQu6TalgMAAAD__________4JpZIJ2NIJpcIQEnfA2iXNlY3AyNTZrMaEDfol8oLr6XJ7FsdAYE7lpJhKMls4G_v6qQOGKJUWGb_uDdGNwgiMog3VkcIIjKA", // # 4.157.240.54 | azure-us-east-virginia
	//"enr:-KG4QF4B5WrlFcRhUU6dZETwY5ZzAXnA0vGC__L1Kdw602nDZwXSTs5RFXFIFUnbQJmhNGVU6OIX7KVrCSTODsz1tK4DhGV0aDKQu6TalgMAAAD__________4JpZIJ2NIJpcIQExNYEiXNlY3AyNTZrMaECQmM9vp7KhaXhI-nqL_R0ovULLCFSFTa9CPPSdb1zPX6DdGNwgiMog3VkcIIjKA", // 4.196.214.4  | azure-au-east-sydney
	//// Prylab team's bootnodes
	//"enr:-Ku4QImhMc1z8yCiNJ1TyUxdcfNucje3BGwEHzodEZUan8PherEo4sF7pPHPSIB1NNuSg5fZy7qFsjmUKs2ea1Whi0EBh2F0dG5ldHOIAAAAAAAAAACEZXRoMpD1pf1CAAAAAP__________gmlkgnY0gmlwhBLf22SJc2VjcDI1NmsxoQOVphkDqal4QzPMksc5wnpuC3gvSC8AfbFOnZY_On34wIN1ZHCCIyg", // 18.223.219.100 | aws-us-east-2-ohio
	//"enr:-Ku4QP2xDnEtUXIjzJ_DhlCRN9SN99RYQPJL92TMlSv7U5C1YnYLjwOQHgZIUXw6c-BvRg2Yc2QsZxxoS_pPRVe0yK8Bh2F0dG5ldHOIAAAAAAAAAACEZXRoMpD1pf1CAAAAAP__________gmlkgnY0gmlwhBLf22SJc2VjcDI1NmsxoQMeFF5GrS7UZpAH2Ly84aLK-TyvH-dRo0JM1i8yygH50YN1ZHCCJxA", // 18.223.219.100 | aws-us-east-2-ohio
	//"enr:-Ku4QPp9z1W4tAO8Ber_NQierYaOStqhDqQdOPY3bB3jDgkjcbk6YrEnVYIiCBbTxuar3CzS528d2iE7TdJsrL-dEKoBh2F0dG5ldHOIAAAAAAAAAACEZXRoMpD1pf1CAAAAAP__________gmlkgnY0gmlwhBLf22SJc2VjcDI1NmsxoQMw5fqqkw2hHC4F5HZZDPsNmPdB1Gi8JPQK7pRc9XHh-oN1ZHCCKvg", // 18.223.219.100 | aws-us-east-2-ohio
	//// Lighthouse team's bootnodes
	//"enr:-Le4QPUXJS2BTORXxyx2Ia-9ae4YqA_JWX3ssj4E_J-3z1A-HmFGrU8BpvpqhNabayXeOZ2Nq_sbeDgtzMJpLLnXFgAChGV0aDKQtTA_KgEAAAAAIgEAAAAAAIJpZIJ2NIJpcISsaa0Zg2lwNpAkAIkHAAAAAPA8kv_-awoTiXNlY3AyNTZrMaEDHAD2JKYevx89W0CcFJFiskdcEzkH_Wdv9iW42qLK79ODdWRwgiMohHVkcDaCI4I", // 172.105.173.25 | linode-au-sydney
	//"enr:-Le4QLHZDSvkLfqgEo8IWGG96h6mxwe_PsggC20CL3neLBjfXLGAQFOPSltZ7oP6ol54OvaNqO02Rnvb8YmDR274uq8ChGV0aDKQtTA_KgEAAAAAIgEAAAAAAIJpZIJ2NIJpcISLosQxg2lwNpAqAX4AAAAAAPA8kv_-ax65iXNlY3AyNTZrMaEDBJj7_dLFACaxBfaI8KZTh_SSJUjhyAyfshimvSqo22WDdWRwgiMohHVkcDaCI4I", // 139.162.196.49 | linode-uk-london
	//"enr:-Le4QH6LQrusDbAHPjU_HcKOuMeXfdEB5NJyXgHWFadfHgiySqeDyusQMvfphdYWOzuSZO9Uq2AMRJR5O4ip7OvVma8BhGV0aDKQtTA_KgEAAAAAIgEAAAAAAIJpZIJ2NIJpcISLY9ncg2lwNpAkAh8AgQIBAAAAAAAAAAmXiXNlY3AyNTZrMaECDYCZTZEksF-kmgPholqgVt8IXr-8L7Nu7YrZ7HUpgxmDdWRwgiMohHVkcDaCI4I", // 139.99.217.220 | ovh-au-sydney
	//"enr:-Le4QIqLuWybHNONr933Lk0dcMmAB5WgvGKRyDihy1wHDIVlNuuztX62W51voT4I8qD34GcTEOTmag1bcdZ_8aaT4NUBhGV0aDKQtTA_KgEAAAAAIgEAAAAAAIJpZIJ2NIJpcISLY04ng2lwNpAkAh8AgAIBAAAAAAAAAA-fiXNlY3AyNTZrMaEDscnRV6n1m-D9ID5UsURk0jsoKNXt1TIrj8uKOGW6iluDdWRwgiMohHVkcDaCI4I", // 139.99.78.39 | ovh-singapore
	//// EF bootnodes
	//"enr:-Ku4QHqVeJ8PPICcWk1vSn_XcSkjOkNiTg6Fmii5j6vUQgvzMc9L1goFnLKgXqBJspJjIsB91LTOleFmyWWrFVATGngBh2F0dG5ldHOIAAAAAAAAAACEZXRoMpC1MD8qAAAAAP__________gmlkgnY0gmlwhAMRHkWJc2VjcDI1NmsxoQKLVXFOhp2uX6jeT0DvvDpPcU8FWMjQdR4wMuORMhpX24N1ZHCCIyg", // 3.17.30.69 | aws-us-east-2-ohio
	//"enr:-Ku4QG-2_Md3sZIAUebGYT6g0SMskIml77l6yR-M_JXc-UdNHCmHQeOiMLbylPejyJsdAPsTHJyjJB2sYGDLe0dn8uYBh2F0dG5ldHOIAAAAAAAAAACEZXRoMpC1MD8qAAAAAP__________gmlkgnY0gmlwhBLY-NyJc2VjcDI1NmsxoQORcM6e19T1T9gi7jxEZjk_sjVLGFscUNqAY9obgZaxbIN1ZHCCIyg", // 18.216.248.220 | aws-us-east-2-ohio
	//"enr:-Ku4QPn5eVhcoF1opaFEvg1b6JNFD2rqVkHQ8HApOKK61OIcIXD127bKWgAtbwI7pnxx6cDyk_nI88TrZKQaGMZj0q0Bh2F0dG5ldHOIAAAAAAAAAACEZXRoMpC1MD8qAAAAAP__________gmlkgnY0gmlwhDayLMaJc2VjcDI1NmsxoQK2sBOLGcUb4AwuYzFuAVCaNHA-dy24UuEKkeFNgCVCsIN1ZHCCIyg", // 54.178.44.198 | aws-ap-northeast-1-tokyo
	//"enr:-Ku4QEWzdnVtXc2Q0ZVigfCGggOVB2Vc1ZCPEc6j21NIFLODSJbvNaef1g4PxhPwl_3kax86YPheFUSLXPRs98vvYsoBh2F0dG5ldHOIAAAAAAAAAACEZXRoMpC1MD8qAAAAAP__________gmlkgnY0gmlwhDZBrP2Jc2VjcDI1NmsxoQM6jr8Rb1ktLEsVcKAPa08wCsKUmvoQ8khiOl_SLozf9IN1ZHCCIyg", // 54.65.172.253 | aws-ap-northeast-1-tokyo
	//// Nimbus team's bootnodes
	//"enr:-LK4QA8FfhaAjlb_BXsXxSfiysR7R52Nhi9JBt4F8SPssu8hdE1BXQQEtVDC3qStCW60LSO7hEsVHv5zm8_6Vnjhcn0Bh2F0dG5ldHOIAAAAAAAAAACEZXRoMpC1MD8qAAAAAP__________gmlkgnY0gmlwhAN4aBKJc2VjcDI1NmsxoQJerDhsJ-KxZ8sHySMOCmTO6sHM3iCFQ6VMvLTe948MyYN0Y3CCI4yDdWRwgiOM", // 3.120.104.18 | aws-eu-central-1-frankfurt
	//"enr:-LK4QKWrXTpV9T78hNG6s8AM6IO4XH9kFT91uZtFg1GcsJ6dKovDOr1jtAAFPnS2lvNltkOGA9k29BUN7lFh_sjuc9QBh2F0dG5ldHOIAAAAAAAAAACEZXRoMpC1MD8qAAAAAP__________gmlkgnY0gmlwhANAdd-Jc2VjcDI1NmsxoQLQa6ai7y9PMN5hpLe5HmiJSlYzMuzP7ZhwRiwHvqNXdoN0Y3CCI4yDdWRwgiOM", // 3.64.117.223 | aws-eu-central-1-frankfurt}
    "enr:-Iq4QEv35Upr43xU5t5i2PwfHZesgVt4ca8fK4fobpFPvJerTPoepsMoFOICrLzquFaHQIRjuBmmkLn5iDMyl8f4QzWGAZhBIySIgmlkgnY0gmlwhIuxtT2Jc2VjcDI1NmsxoQMNTvCnu2qQ1qBNkSZ_wmpIvByHD_0a9JQpnWjUOJRSlIN1ZHCCI1I"
}.Select(n => n.StartsWith("enr") ? n : GetEnr(new Enode(n)).ToString()).ToArray();


Enr GetEnr(Enode node) => new EnrBuilder()
    .WithIdentityScheme(identityVerifier, signer)
    .WithEntry(EnrEntryKey.Id, new EntryId("v4"))
    .WithEntry(EnrEntryKey.Secp256K1, new EntrySecp256K1(Context.Instance.CreatePubKey(node.PublicKey.PrefixedBytes).ToBytes(false)))
    .WithEntry(EnrEntryKey.Ip, new EntryIp(node.HostIp))
    .WithEntry(EnrEntryKey.Tcp, new EntryTcp(node.Port))
    .WithEntry(EnrEntryKey.Udp, new EntryUdp(node.DiscoveryPort))
    .Build();

IDiscv5ProtocolBuilder discv5Builder = new Discv5ProtocolBuilder(services)
    .WithConnectionOptions(new ConnectionOptions
    {
        UdpPort = localPort,
    })
    .WithSessionOptions(_sessionOptions)
    .WithTableOptions(new TableOptions(bootNodesStr))
    .WithEnrBuilder(enrBuilder)
    .WithEnrEntryRegistry(registry);

//var _discv5Protocol = NetworkHelper.HandlePortTakenError(discv5Builder.Build, port);
IDiscv5Protocol proto = discv5Builder.Build();

IServiceProvider _serviceProvider = discv5Builder.GetServiceProvider();


await proto.InitAsync();

//IEnr[] historyNetworkBootnodes = bootNodesStr.Select((str) => enrFactory.CreateFromString(str, identityVerifier)).ToArray();

var r = new Random();
byte[] randNodeId = new byte[32];
r.NextBytes(randNodeId);

Console.WriteLine("Discoverying");


await proto.SendPingAsync(enrFactory.CreateFromString(bootNodesStr[0], identityVerifier));

Console.ReadLine();
static class Shared
{
    public static IPAddress? Ip { get; set; } = IPAddress.Parse("178.172.225.183");
    public static int Port { get; set; } = 30304;
}


public class RawEntry(string key, byte[] value) : IEntry
{
    public string Key { get; } = key;
    public byte[] Value { get; } = value;

    EnrEntryKey IEntry.Key => new(Key);

    public IEnumerable<byte> EncodeEntry()
    {
        return ByteArrayUtils.JoinByteArrays(RlpEncoder.EncodeString(Key, Encoding.ASCII),
            RlpEncoder.EncodeBytes(Value));
    }
}
