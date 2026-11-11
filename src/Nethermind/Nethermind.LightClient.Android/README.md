# Nethermind LC for Android

An experimental Android ARM64 application that follows Ethereum mainnet and
tracks an account's **native ETH balance** and selected **USDT, DAI and USDC**
balances. On first launch, enter a public Ethereum address and choose the tokens;
all three tokens are selected by default. ETH is always included, even if every
token is deselected. No wallet connection or private key is needed.

The phone connects directly to beacon libp2p and execution SNAP peers. It uses the
existing light-client sync committee, signature, Merkle branch and account-proof
verification code. The UI shows optimistic and finalized heads, peer counts, the
balance's proof block/state root and request duration. An unavailable proof never
becomes a zero balance. The last verified balance stays labelled as refreshing
while a newer result is pending; its proof block is in the details popup. Token balances use local `balanceOf(address)`
execution in Nethermind's EVM over authenticated state fetched from peers,
including proxy-contract code and storage. No token storage layout is assumed.

The predefined mainnet tokens are [USDT](https://tether.to/en/supported-protocols/)
(`0xdAC17F958D2ee523a2206206994597C13D831ec7`),
[DAI](https://chainlog.skyeco.com/api/mainnet/active.json)
(`0x6B175474E89094C44Da98b954EedeAC495271d0F`), and
[USDC](https://developers.circle.com/stablecoins/usdc-contract-addresses)
(`0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48`).

The initial checkpoint comes from `https://mainnet.checkpoint.sigp.io`; trusting
this HTTPS provider is an explicit bootstrap assumption. Its selection and the
verified consensus journal persist in private app storage. No upstream JSON-RPC
is used. As on the CLI, optimistic heads can reorganize. The app does not hold
wallet keys or send transactions.

## Build and install

Requirements: .NET 10 SDK with the `android` workload, Android SDK, JDK 21,
Python 3, and the native build tools listed in
[the Android crypto build instructions](../../../tools/LightClient/android/README.md).
The app requires Android 8/API 26 or newer and an ARM64 device.

From the repository root:

```powershell
dotnet workload install android
dotnet build src/Nethermind/Nethermind.LightClient.Android/Nethermind.LightClient.Android.csproj `
  -t:InstallAndroidDependencies -p:AndroidSdkDirectory=C:/Temp/android-sdk `
  -p:JavaSdkDirectory=C:/Temp/android-jdk -p:AcceptAndroidSdkLicenses=true -p:SaveDiskSpace=true
```

Build the three native libraries and the Android BLS binding using the linked
instructions. Their default output is `src/Nethermind/artifacts/android-native`.
Then build the dedicated solution; the desktop solution does not require an
Android workload:

```powershell
dotnet build src/Nethermind/LightClient.Android.slnx -c release `
  -p:AndroidSdkDirectory=C:/Temp/android-sdk -p:JavaSdkDirectory=C:/Temp/android-jdk `
  -p:SaveDiskSpace=true -warnaserror

C:/Temp/android-sdk/platform-tools/adb.exe devices -l
C:/Temp/android-sdk/platform-tools/adb.exe install --no-incremental --no-streaming -r `
  src/Nethermind/artifacts/bin/Nethermind.LightClient.Android/release_android-arm64/io.nethermind.lightclient-Signed.apk
C:/Temp/android-sdk/platform-tools/adb.exe shell monkey -p io.nethermind.lightclient 1
```

The locally signed APK is for development/device testing. Distribution requires
an appropriate release signing key. Choose **Save and start** on the first-run
setup screen. The account and token selection persist across restarts; later
launches resume mainnet sync automatically. The hamburger menu offers **Stop client**
while running and **Start mainnet** once stopped. **Reset settings** in that menu stops the
client, clears the account/token selection and returns to setup, while retaining
the verified chain journal and checkpoint trust anchor.

A green **Synced** status means ETH and every selected token match a fresh
authenticated head. Balances are rounded to two decimal places without a ticker
suffix; their verified integer values are preserved. Compact, borderless rows
show each asset icon and ticker beside its amount. The amounts share one
font size and fixed decimal/fraction columns. Decimal points align vertically,
and fractional digits start directly after the dot even with proportional fonts. Verification
status shares the text baseline of the Balances heading; stale amounts are muted and failed refreshes are amber.
The tracked address shows its first six and last six hexadecimal characters.
The hamburger icon shares the right edge of the verification label and amounts;
the mainnet/verification subtitle fits on one line. Tap a row to see the
full address and its proof block, block hash, state root, exact units and request duration. Proof
details refresh while the popup is open.

**Client stats** in the hamburger menu combines live peers, latest/finalized
heads, bootstrap provider and trust assumptions with RAM, CPU and traffic usage.
The top-right back chevron returns to balances while verification keeps running.
Both screens request portrait orientation; Android 16 can override orientation
restrictions on displays at least 600 dp wide. Usage is sampled every five seconds
only while Client stats is open; networking details refresh every second.
RAM is proportional resident memory (PSS, excluding swap), CPU uses 100% for one
fully occupied core, and traffic shows receive/send rates plus app totals since
device boot across network interfaces. The counters use Android's
[per-UID traffic statistics](https://developer.android.com/reference/android/net/TrafficStats)
and include checkpoint and discovery traffic as well as P2P messages.

Mobile memory defaults use a 4 MiB native Keccak cache, 4 MiB retention budgets
for each of the seven execution payload caches and the EVM state cache, and
smaller per-thread network buffer caches. These are cache limits, not a process
RAM limit; entries are verified and can be fetched again after eviction. Network
message-size limits and peer targets are unchanged. EVM calls start with small
account maps that grow as needed, and discovery burst filters allocate on demand
without changing their limits. The Android runtime uses
[`System.GC.ConserveMemory=5`](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector#conserve-memory);
this trades more frequent collection for a smaller managed heap. Networking
identity creation persists the owner-only node key directly, without creating
an unused wallet keystore or allocating its scrypt scratch memory. Existing
execution identities are reused. The APK remains untrimmed because the current reflection-based
dependency graph does not pass strict linker validation.

Allow notifications to keep its foreground-service status visible. Inspect its
own logs with:

```powershell
C:/Temp/android-sdk/platform-tools/adb.exe logcat -s NethermindLC
```

For a VM that cannot see the physical USB phone, use Android **Developer options →
Wireless debugging**, then `adb pair IP:PAIRING_PORT` and
`adb connect IP:CONNECTION_PORT`. The two ports differ. Both machines must be able
to reach each other; do not expose ADB to the public internet.

## Current scope and limits

- This Android host tracks one chosen account's ETH and selected mainnet USDT,
  DAI and USDC balances. It does not expose a
  local HTTP/WS RPC server, wallet signing, general `eth_call`, or hosted storage.
- Beacon transport uses TCP/Noise with yamux/mplex; QUIC is disabled on Android.
- The app uses .NET 10's experimental Android CoreCLR support. This is a device
  prototype, not a production mobile release.
- Android's foreground data-sync time limits and power management still apply.
  The service stops on a platform timeout and can be restarted from the app.
- Beacon identity is fresh each run, with a matching fresh ENR sequence. Consensus
  provenance/journal, execution identity and bounded discovery records persist.
- A journal that is too old to resume fails closed. Clearing app storage and
  restarting explicitly selects a new checkpoint from the displayed provider.
- The mobile build links the original verifier/protocol sources and uses a
  reduced execution networking composition. It excludes desktop web entrypoints
  and native libraries unused by balance tracking. The BLS binding differs only
  in Android native-library loading; native source revisions are pinned.

The portable test project runs the same captured mainnet/Hoodi signature,
consensus SSZ and SNAP proof tests against the mobile assemblies. Passing these
desktop tests or building an APK does not establish that Android networking or
native cryptography works on a phone; that requires device validation.

Device validation on November 11, 2026 used a OnePlus 15 (CPH2749), Android 16,
ARM64. The earlier ETH-only release APK followed mainnet and verified successive
balance proofs for the displayed account. Its first proof arrived about 30 seconds after
launch; subsequent reads initially took 64–124 ms. Stop removed the foreground
service, and Start resumed verification from the persisted checkpoint/journal.
The subsequent account/token build preserved settings across an APK update and
verified ETH, USDT, DAI and USDC at a fresh authenticated head on the same phone.
This is a short functional test, not a battery, thermal, network-handover or
long-running mobile stability result.
