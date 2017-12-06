using System;

namespace Nevermind.Network
{
    public static class Discovery
    {
        public static string[] BootstrapNodes { get; } =
        {
            "a",
            "b"
        };

        /// <summary>
        /// Kademlia's 'k'
        /// </summary>
        public static int BucketSize { get; } = 16;
        
        /// <summary>
        /// Kademlia's 'alpha'
        /// </summary>
        public static int Concurrency { get; } = 3;
        
        /// <summary>
        /// Kademlia's 'b'
        /// </summary>
        public static int BitsPerHop { get; } = 8;
        
        public static TimeSpan EvictionCheckIntervalMiliseconds { get; } = TimeSpan.FromMilliseconds(75);
        
        public static TimeSpan IdleBucketRefreshInterval { get; } = TimeSpan.FromHours(1);
        
        public static TimeSpan PacketValidity { get; } = TimeSpan.FromSeconds(3);
        
        public static int DatagramSizeInBytes { get; } = 1280;
    }
}