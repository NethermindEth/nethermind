public class NethermindApi
{
    private void RegisterNetwork()
    {
        // ... existing registrations ...

        // Register etha protocol factory
        services.AddSingleton<EthaProtocolFactory>();
    }
}
