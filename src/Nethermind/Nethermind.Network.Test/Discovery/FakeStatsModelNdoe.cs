using Nethermind.Core.Crypto;
using Nethermind.Stats.Model;
using System;
using System.Net;
using System.Reflection;

public class FakeStatsModelNode : INode
{
    private readonly static int _port = 1;
    private readonly static string _host = "192.168.1.27";
    
    public FakeStatsModelNode(Keccak idHash)
    {
        IdHash = idHash;
 
    }
    private PublicKey _id;

    public PublicKey Id
    {
        get => _id;
        set
        {
            if (_id != null)
            {
                throw new InvalidOperationException($"ID already set for the node {Id}");
            }

            _id = value;
            IdHash = Keccak.Compute(_id.PrefixedBytes);
        }
    }

    public Keccak IdHash { get; private set; }
    public string Host { get; private set; }
    public int Port { get; set; }
    public IPEndPoint Address { get; private set; }
    public bool AddedToDiscovery { get; set; }
    public bool IsBootnode { get; set; }
    public bool IsTrusted { get; set; }

    public bool IsStatic { get; set; }

    public string ClientId { get; set; }

    public void InitializeAddress(IPEndPoint address)
    {
        Host = address.Address.ToString();
        Port = address.Port;
        Address = address;
    }

    public void InitializeAddress(string host, int port)
    {
        Host = host;
        Port = port;
        Address = new IPEndPoint(IPAddress.Parse(host), port);
    }

    public override bool Equals(object obj)
    {
        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj is Node item)
        {
            return IdHash.Equals(item.IdHash);
        }

        return false;
    }

    public override int GetHashCode()
    {
        // ReSharper disable once NonReadonlyMemberInGetHashCode
        return IdHash.GetHashCode();
    }

    public override string ToString()
    {
        return $"enode://{Id.ToString(false)}@{Host}:{Port}|{Id.Address}";
    }

    public string ToString(string format)
    {
        return ToString(format, null);
    }

    public string ToString(string format, IFormatProvider formatProvider)
    {
        string ipv4Host = Host.Replace("::ffff:", string.Empty);
        switch (format)
        {
            default:
                return $"enode://{Id.ToString(false)}@{ipv4Host}:{Port}";
            case "s":
                return $"{ipv4Host}:{Port}";
            case "c":
                return $"{ClientId}|{ipv4Host}:{Port}";
            case "f":
                return $"enode://{Id.ToString(false)}@{ipv4Host}:{Port}|{ClientId}";
        }
    }

    public static bool operator ==(FakeStatsModelNode a, FakeStatsModelNode b)
    {
        if (ReferenceEquals(a, null))
        {
            return ReferenceEquals(b, null);
        }

        if (ReferenceEquals(b, null))
        {
            return false;
        }

        return a.Id.Equals(b.Id);
    }

    public static bool operator !=(FakeStatsModelNode a, FakeStatsModelNode b)
    {
        return !(a == b);
    }
}
