# PingGrapher

Pings a bunch of hosts at the same time and draws the latency as a live graph. I wrote it because I was tired of running five `ping -t` windows side by side to figure out whether the problem was the Wi-Fi, the router, the ISP or the server.

Out of the box it pings your gateway, your DNS server and 8.8.8.8. Anything else you type into the box (IPs or hostnames, comma separated) gets added on top.

## Download

Grab `PingGrapher.exe` from the [latest release](https://github.com/mttgrb/PingGrapher/releases/latest). It's a single exe, no installer.

## Usage

Start it, hit Start. Hover over the graph to see the exact values at a point in time. Timeouts show up as red crosses at the top of the graph.

The panel at the bottom shows last/avg/min/max/jitter for what's currently on screen (last hour by default) and packet loss since you pressed Start.

Download saves the graph as an image, or all the raw data as CSV if you pick that file type.

### Command line

```
PingGrapher.exe [options] [hosts...]
```

If you pass any hosts it starts pinging right away.

```
-i <ms>       interval between rounds, default 1000
-t <ms>       timeout, default 5000
-w <sec>      how much history the graph shows, default 3600
-log <file>   append every ping to a CSV file
-nogoogle     don't add 8.8.8.8
```

For example, to leave it running overnight and check the log in the morning:

```
PingGrapher.exe -t 2000 -log C:\Temp\ping.csv myserver.local
```

A new round only starts once the previous one is done, same as `ping.exe`. So if a host is down, you get one sample per timeout instead of one per second. Lower `-t` if you care about that.

### CSV files

The export has one row per round and one column per host. Empty cell = timeout. The log file (`-log`) has one row per ping with the status, which is easier to filter.

Both are comma separated. If you have Excel set to a European locale it'll open everything in one column; use Data > From Text/CSV instead.

## Building

Needs .NET Framework 4.8 (already on any recent Windows) and the .NET SDK to build.

```
dotnet build -c Release
```

The exe ends up in `bin\Release\net48\`.

## License

MIT, see [LICENSE](LICENSE).
