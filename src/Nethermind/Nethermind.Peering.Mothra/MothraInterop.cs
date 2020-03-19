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
        public static extern unsafe void Start([In] [Out] string[] args, int length);
    }
}