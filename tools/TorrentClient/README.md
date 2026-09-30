# Nethermind Torrent Client

The standalone .NET client downloads and SHA-1 verifies BitTorrent v1 payloads, then
shares verified pieces with other peers. Windows MAUI and Linux GTK desktop heads
also keep completed torrents available for seeding while they are running.

## CLI

```powershell
dotnet run --project tools/TorrentClient/src/Nethermind.Torrent.Cli -c Release -- "C:\Users\flcl\Downloads\slackware64-14.2-install-dvd.torrent" --output "D:\Downloads" --port 6881
```

The process remains online after the payload is complete or restored from disk.
Press Ctrl+C to stop and send a tracker `stopped` event. Use `--no-seed` to exit
after verification/download. `--upload-slots 8` controls simultaneous
upload peers; `--port 0` selects an available port, but a fixed forwarded TCP
port is more useful for external peers. If a configured port is unavailable,
the client logs the fallback port and advertises that actual port. The desktop
Peers tab also shows the active listening port. `--help` lists the remaining
options.

Only SHA-1-verified pieces are advertised and served. Seeds accept inbound TCP
peers, connect to reachable peers discovered through trackers or DHT, and serve
the original info dictionary to magnet peers through `ut_metadata`. Tracker and
mainline-DHT announcements publish the active listening port; DHT announcements
are refreshed while seeding. Private torrents never use public DHT discovery or announcements.
The client does not currently configure firewall or NAT port
forwarding, so outbound discovery alone does not guarantee internet peers can
connect to a private host. Keep the app running and allow/forward its TCP port
to seed outside the local network. Payload transfers currently use TCP; the
uTP packet primitives are not connected to the transfer path.
