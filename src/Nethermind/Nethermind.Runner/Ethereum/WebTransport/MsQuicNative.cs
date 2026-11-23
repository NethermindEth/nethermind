// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Nethermind.Runner.Ethereum.WebTransport;

// The application uses MsQuic's public v2 API because System.Net.Quic does not expose
// per-connection datagram settings. All ABI declarations below follow msquic.h v2.5.
internal sealed unsafe class MsQuicNative : IDisposable
{
    private const uint LibraryVersion = 0x01000004;
    private const uint ListenerAddress = 0x04000000;
    private const uint StreamId = 0x08000000;
    private readonly nint _library;
    private readonly delegate* unmanaged[Cdecl]<Functions*, void> _close;
    private readonly Functions* _api;

    internal static int Refused => OperatingSystem.IsWindows() ? unchecked((int)0x800704C9) : 111;
    internal static int Pending => OperatingSystem.IsWindows() ? 0x000703E5 : -2;
    internal static int Failed => OperatingSystem.IsWindows() ? unchecked((int)0x80004005) : 5;

    internal MsQuicNative()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Browser WebTransport requires MsQuic on Windows or Linux.");

        string name = OperatingSystem.IsWindows()
            ? Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "msquic.dll")
            : "libmsquic.so.2";
        if (!NativeLibrary.TryLoad(name, out _library))
            throw new PlatformNotSupportedException("Browser WebTransport requires the MsQuic 2.5 or later runtime.");

        Functions* api = null;
        try
        {
            delegate* unmanaged[Cdecl]<uint, Functions**, int> open = (delegate* unmanaged[Cdecl]<uint, Functions**, int>)NativeLibrary.GetExport(_library, "MsQuicOpenVersion");
            _close = (delegate* unmanaged[Cdecl]<Functions*, void>)NativeLibrary.GetExport(_library, "MsQuicClose");
            Check(open(2, &api), "opening MsQuic");

            uint* version = stackalloc uint[4];
            uint versionSize = 4 * sizeof(uint);
            Check(api->GetParam(0, LibraryVersion, &versionSize, version), "reading the MsQuic version");
            if (versionSize != 4 * sizeof(uint) || version[0] != 2 || version[1] < 5)
                throw new PlatformNotSupportedException("Browser WebTransport requires MsQuic 2.5 or later within major version 2.");
            _api = api;
        }
        catch
        {
            if (api is not null) _close(api);
            NativeLibrary.Free(_library);
            throw;
        }
    }

    public void Dispose()
    {
        _close(_api);
        NativeLibrary.Free(_library);
    }

    internal nint OpenRegistration()
    {
        ReadOnlySpan<byte> name = "nethermind-webtransport\0"u8;
        fixed (byte* pName = name)
        {
            RegistrationConfig config = new() { Name = pName };
            nint handle;
            Check(_api->RegistrationOpen(&config, &handle), "opening the QUIC registration");
            return handle;
        }
    }

    internal void CloseRegistration(nint registration) => _api->RegistrationClose(registration);
    internal void CloseConfiguration(nint configuration) => _api->ConfigurationClose(configuration);

    internal nint OpenConfiguration(nint registration, X509Certificate2 certificate)
    {
        fixed (byte* pAlpn = "h3"u8)
        {
            Buffer alpn = new() { Length = 2, Data = pAlpn };
            Settings settings = new()
            {
                // StreamMultiReceiveEnabled (bit 42) is explicitly disabled for the single pending receive per stream.
                IsSet = (1UL << 1) | (1UL << 2) | (1UL << 6) | (1UL << 8) | (1UL << 18) |
                        (1UL << 19) | (1UL << 27) | (1UL << 42),
                HandshakeIdleTimeout = 10_000,
                IdleTimeout = 20_000,
                StreamReceiveWindow = 512 * 1024,
                ConnectionReceiveWindow = 1024 * 1024,
                PeerBidirectionalStreams = 4,
                PeerUnidirectionalStreams = 4,
                Flags = 1 << 3, // DatagramReceiveEnabled
            };
            nint configuration;
            Check(_api->ConfigurationOpen(registration, &alpn, 1, &settings, (uint)sizeof(Settings), 0, &configuration), "opening the QUIC configuration");
            try
            {
                LoadCertificate(configuration, certificate);
                return configuration;
            }
            catch
            {
                CloseConfiguration(configuration);
                throw;
            }
        }
    }

    private void LoadCertificate(nint configuration, X509Certificate2 certificate)
    {
        if (OperatingSystem.IsWindows())
        {
            const int NoCredentials = unchecked((int)0x8009030e);
            int status = LoadCertificateContext(configuration, certificate);
            if (status != NoCredentials)
            {
                Check(status, "loading the QUIC certificate");
                return;
            }
        }

        byte[] pkcs12 = certificate.Export(X509ContentType.Pkcs12);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Schannel cannot use the ephemeral Windows private key created by CreateSelfSigned.
                using X509Certificate2 imported = X509CertificateLoader.LoadPkcs12(pkcs12, null, X509KeyStorageFlags.UserKeySet);
                Check(LoadCertificateContext(configuration, imported), "loading the QUIC certificate");
                return;
            }

            fixed (byte* data = pkcs12)
            {
                Pkcs12 blob = new() { Data = data, Length = (uint)pkcs12.Length };
                Credential credential = new() { Type = 6, Certificate = (nint)(&blob) };
                Check(_api->ConfigurationLoadCredential(configuration, &credential), "loading the QUIC certificate");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }
    }

    private int LoadCertificateContext(nint configuration, X509Certificate2 certificate)
    {
        Credential credential = new() { Type = 3, Certificate = certificate.Handle };
        int status = _api->ConfigurationLoadCredential(configuration, &credential);
        GC.KeepAlive(certificate);
        return status;
    }

    internal nint OpenListener(nint registration, nint callback, nint context)
    {
        nint listener;
        Check(_api->ListenerOpen(registration, callback, context, &listener), "opening the QUIC listener");
        return listener;
    }

    internal IPEndPoint StartListener(nint listener, IPEndPoint endpoint)
    {
        SocketAddress address = SocketAddress.FromEndpoint(endpoint);
        fixed (byte* h3 = "h3"u8)
        {
            Buffer alpn = new() { Length = 2, Data = h3 };
            Check(_api->ListenerStart(listener, &alpn, 1, &address), "binding the QUIC listener");
        }

        uint length = (uint)sizeof(SocketAddress);
        Check(_api->GetParam(listener, ListenerAddress, &length, &address), "reading the bound QUIC address");
        return address.ToEndpoint();
    }

    internal void CloseListener(nint listener) => _api->ListenerClose(listener);
    internal void SetCallback(nint handle, nint callback, nint context) => _api->SetCallbackHandler(handle, callback, context);
    internal int ConfigureConnection(nint connection, nint configuration) => _api->ConnectionSetConfiguration(connection, configuration);
    internal void ShutdownConnection(nint connection) => _api->ConnectionShutdown(connection, 0, 0x100);
    internal void CloseConnection(nint connection) => _api->ConnectionClose(connection);

    internal nint OpenStream(nint connection, bool unidirectional, nint callback, nint context)
    {
        nint handle;
        Check(_api->StreamOpen(connection, unidirectional ? 1u : 0u, callback, context, &handle), "opening a QUIC stream");
        return handle;
    }

    // Leave the stream unannounced until data is ready.
    internal void StartStream(nint stream) => Check(_api->StreamStart(stream, 0), "starting a QUIC stream");
    internal void CloseStream(nint stream) => _api->StreamClose(stream);
    internal int ShutdownStream(nint stream, bool graceful) => _api->StreamShutdown(stream, graceful ? 1u : 14u, graceful ? 0UL : 0x10cUL);
    internal void CompleteReceive(nint stream, int length) => _api->StreamReceiveComplete(stream, (ulong)length);
    internal int Send(nint stream, Buffer* buffer, nint context) => _api->StreamSend(stream, buffer, 1, 0, context);

    internal long GetStreamId(nint stream)
    {
        long id;
        uint length = sizeof(long);
        Check(_api->GetParam(stream, StreamId, &length, &id), "reading a QUIC stream ID");
        return id;
    }

    internal static bool IsFailure(int status) => OperatingSystem.IsWindows() ? status < 0 : status > 0;

    internal static void Check(int status, string operation)
    {
        if (IsFailure(status)) throw Error(status, operation);
    }

    internal static IOException Error(int status, string operation) => new($"MsQuic reported 0x{status:x8} while {operation}.");

    [StructLayout(LayoutKind.Explicit, Size = 28)]
    internal struct SocketAddress
    {
        [FieldOffset(0)] private ushort _family;
        [FieldOffset(24)] private uint _scopeId;

        internal static SocketAddress FromEndpoint(IPEndPoint endpoint)
        {
            bool v6 = endpoint.AddressFamily == AddressFamily.InterNetworkV6;
            SocketAddress address = new()
            {
                _family = endpoint.Address.Equals(IPAddress.IPv6Any) ? (ushort)0 :
                    v6 ? (OperatingSystem.IsWindows() ? (ushort)23 : (ushort)10) : (ushort)2,
                _scopeId = v6 ? (uint)endpoint.Address.ScopeId : 0,
            };
            byte* bytes = (byte*)&address;
            BinaryPrimitives.WriteUInt16BigEndian(new Span<byte>(bytes + 2, 2), (ushort)endpoint.Port);
            _ = endpoint.Address.TryWriteBytes(new Span<byte>(bytes + (v6 ? 8 : 4), v6 ? 16 : 4), out _);
            return address;
        }

        internal IPEndPoint ToEndpoint()
        {
            bool v6 = _family != 2;
            fixed (SocketAddress* data = &this)
            {
                byte* bytes = (byte*)data;
                IPAddress ip = v6 ? new IPAddress(new ReadOnlySpan<byte>(bytes + 8, 16), _scopeId)
                    : new IPAddress(new ReadOnlySpan<byte>(bytes + 4, 4));
                return new IPEndPoint(ip, BinaryPrimitives.ReadUInt16BigEndian(new ReadOnlySpan<byte>(bytes + 2, 2)));
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Buffer
    {
        internal uint Length;
        internal byte* Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RegistrationConfig
    {
        internal byte* Name;
        internal uint Profile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Pkcs12
    {
        internal byte* Data;
        internal uint Length;
        internal byte* Password;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        internal uint Type;
        internal uint Flags;
        internal nint Certificate;
        internal nint Principal;
        internal nint Reserved;
        internal nint AsyncHandler;
        internal uint AllowedCipherSuites;
        internal nint CaCertificateFile;
    }

    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct Settings
    {
        [FieldOffset(0)] internal ulong IsSet;
        [FieldOffset(16)] internal ulong HandshakeIdleTimeout;
        [FieldOffset(24)] internal ulong IdleTimeout;
        [FieldOffset(48)] internal uint StreamReceiveWindow;
        [FieldOffset(56)] internal uint ConnectionReceiveWindow;
        [FieldOffset(94)] internal ushort PeerBidirectionalStreams;
        [FieldOffset(96)] internal ushort PeerUnidirectionalStreams;
        [FieldOffset(106)] internal byte Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Functions
    {
        private nint _setContext, _getContext;
        internal delegate* unmanaged[Cdecl]<nint, nint, nint, void> SetCallbackHandler;
        private nint _setParam;
        internal delegate* unmanaged[Cdecl]<nint, uint, uint*, void*, int> GetParam;
        internal delegate* unmanaged[Cdecl]<RegistrationConfig*, nint*, int> RegistrationOpen;
        internal delegate* unmanaged[Cdecl]<nint, void> RegistrationClose;
        private nint _registrationShutdown;
        internal delegate* unmanaged[Cdecl]<nint, Buffer*, uint, Settings*, uint, nint, nint*, int> ConfigurationOpen;
        internal delegate* unmanaged[Cdecl]<nint, void> ConfigurationClose;
        internal delegate* unmanaged[Cdecl]<nint, Credential*, int> ConfigurationLoadCredential;
        internal delegate* unmanaged[Cdecl]<nint, nint, nint, nint*, int> ListenerOpen;
        internal delegate* unmanaged[Cdecl]<nint, void> ListenerClose;
        internal delegate* unmanaged[Cdecl]<nint, Buffer*, uint, SocketAddress*, int> ListenerStart;
        private nint _listenerStop, _connectionOpen;
        internal delegate* unmanaged[Cdecl]<nint, void> ConnectionClose;
        internal delegate* unmanaged[Cdecl]<nint, uint, ulong, void> ConnectionShutdown;
        private nint _connectionStart;
        internal delegate* unmanaged[Cdecl]<nint, nint, int> ConnectionSetConfiguration;
        private nint _connectionSendResumptionTicket;
        internal delegate* unmanaged[Cdecl]<nint, uint, nint, nint, nint*, int> StreamOpen;
        internal delegate* unmanaged[Cdecl]<nint, void> StreamClose;
        internal delegate* unmanaged[Cdecl]<nint, uint, int> StreamStart;
        internal delegate* unmanaged[Cdecl]<nint, uint, ulong, int> StreamShutdown;
        internal delegate* unmanaged[Cdecl]<nint, Buffer*, uint, uint, nint, int> StreamSend;
        internal delegate* unmanaged[Cdecl]<nint, ulong, void> StreamReceiveComplete;
    }
}
