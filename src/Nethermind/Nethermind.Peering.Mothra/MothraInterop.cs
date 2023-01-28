// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System.Runtime.InteropServices;

namespace Nethermind.Peering.Mothra
{
    internal static class MothraInterop
    {
        // mothra.dll on Windows, libmothra.so on Linux, libmotha.dylib on OSX
        private const string DllName = "mothra";
        //public static extern unsafe void RegisterHandlers(IntPtr discoveredPeer, IntPtr receiveGossip, IntPtr receiveRpc);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate void DiscoveredPeer(byte* peerUtf8Ptr, int peerLength);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate void ReceiveGossip(byte* topicUtf8Ptr, int topicLength, byte* dataPtr, int dataLength);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate void ReceiveRpc(byte* methodUtf8Ptr, int methodLength, int requestResponseFlag,
            byte* peerUtf8Ptr, int peerLength, byte* dataPtr, int dataLength);

        [DllImport(DllName, EntryPoint = "libp2p_register_handlers", CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe void RegisterHandlers(DiscoveredPeer discoveredPeer, ReceiveGossip receiveGossip,
            ReceiveRpc receiveRpc);

        [DllImport(DllName, EntryPoint = "libp2p_send_gossip", CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe void SendGossip(byte* topicUtf8Ptr, int topicLength, byte* dataPtr, int dataLength);

        [DllImport(DllName, EntryPoint = "libp2p_send_rpc_request", CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe void SendRequest(byte* methodUtf8Ptr, int methodLength, byte* peerUtf8Ptr,
            int peerLength,
            byte* data, int dataLength);

        [DllImport(DllName, EntryPoint = "libp2p_send_rpc_response", CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe void SendResponse(byte* methodUtf8Ptr, int methodLength, byte* peerUtf8Ptr,
            int peerLength, byte* dataPtr, int dataLength);

        [DllImport(DllName, EntryPoint = "libp2p_start", CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe void Start([In][Out] string[] args, int length);
    }
}
