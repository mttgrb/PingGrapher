# PingGrapher

A small Windows tool that pings several hosts at once and graphs their latency in real time.

By default it pings your **gateway**, your **primary DNS server** and **Google DNS (8.8.8.8)**, plus any IPs or hostnames you enter (comma separated).

## Features

- Live latency graph per host, with timeouts shown as red ✕ markers and gaps in the line
- Statistics per host: last, average, min, max and jitter over the visible window, plus packet loss over the whole session
- Hover over the graph to see the exact time and values at that point
- Export the graph as PNG/JPEG/BMP, or the raw data as CSV
- Optional continuous logging of every ping to a CSV file
- Hostnames are re-resolved every 5 minutes

## Requirements

Windows with .NET Framework 4.8.

## Command line

```
PingGrapher.exe [options] [host1 host2 ...]
```

If any host is given, pinging starts automatically.

| Option          | Description                                         |
|-----------------|-----------------------------------------------------|
| `-i <ms>`       | Ping interval (100-60000, default 1000)             |
| `-t <ms>`       | Ping timeout (100-30000, default 5000)              |
| `-w <seconds>`  | Visible history window (10-86400, default 3600)     |
| `-log <file>`   | Append every ping result to a CSV file              |
| `-nogoogle`     | Don't add Google DNS (8.8.8.8) automatically        |
| `-h`, `-?`      | Show help                                           |

Examples:

```
PingGrapher.exe google.com 1.1.1.1
PingGrapher.exe -i 500 -t 2000 -log C:\Temp\ping.csv myserver.local
```

If a round is still waiting for a timed-out host, the next round starts once it finishes, so during outages fewer samples are taken (like `ping.exe`).

## CSV formats

- **Export (Download → CSV):** one row per round, one column per host; an empty cell means timeout/error.
- **Log (`-log`):** one row per ping: `Timestamp,Target,Host,IP,LatencyMs,Status`.

Both use `,` as separator. In Excel with a European locale, use *Data → From Text/CSV* to import.

## Building

```
dotnet build -c Release
```
