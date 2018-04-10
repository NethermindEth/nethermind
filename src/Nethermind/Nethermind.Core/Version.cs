namespace Nethermind.Core
{
    public static class ClientVersion
    {
        // public static readonly string Description = $"Nethermind/v0.0.1-alpha/{RuntimeInformation.OSArchitecture}-{RuntimeInformation.OSDescription.Trim().Replace(" ", "_")}/{RuntimeInformation.FrameworkDescription.Trim().Replace(".NET ", "").Replace(" ", "")}";
        // public static readonly string Description = $"Nethermind/v0.0.1-alpha/x86_64-Win10/netcore2.0.0";
         public static readonly string Description = $"Parity/v1.9.2-beta-0feb0bb-20180201/x86_64-linux-gnu/rustc1.23.0"; // TODO: since it seems that some clients depend on specific format of this we would pretend to be parity while testing
//        public static readonly string Description = $"Nethermind/alpha";
    }
}