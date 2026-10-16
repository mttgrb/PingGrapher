using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

namespace PingGrapher
{
    public partial class MainForm : Form
    {
        // Settings (overridable from the command line, see UsageText)
        private int _intervalMs = 1000;
        private int _timeoutMs = 5000;
        private int _windowSeconds = 3600; // Visible history: 1 hour
        private string _logPath;
        private bool _includeGoogleDns = true;

        private const int PingStaggerMs = 50;           // Delay between pings of the same round
        private const int DnsTimeoutMs = 5000;
        private static readonly TimeSpan ReResolveInterval = TimeSpan.FromMinutes(5);
        private const int MaxRoundsKept = 1000000;      // Raw history kept for CSV export (~11 days at 1s)
        private const int StatsMargin = 10;
        private const int MinStatsHeight = 140;
        private const double TimeoutMarkerRatio = 0.95; // Timeout markers sit at 95% of the Y axis

        private const string UsageText =
            "Usage: PingGrapher.exe [options] [host1 host2 ...]\r\n\r\n" +
            "Hosts can be IP addresses or hostnames (space or comma separated).\r\n" +
            "If any host is given, pinging starts automatically.\r\n\r\n" +
            "Options:\r\n" +
            "  -i <ms>       Ping interval (100-60000, default 1000)\r\n" +
            "  -t <ms>       Ping timeout (100-30000, default 5000)\r\n" +
            "  -w <seconds>  Visible history window (10-86400, default 3600)\r\n" +
            "  -log <file>   Append every ping result to a CSV file\r\n" +
            "  -nogoogle     Don't add Google DNS (8.8.8.8) automatically\r\n" +
            "  -h, -?        Show this help";

        private readonly List<PingTarget> _targets = new List<PingTarget>();
        private readonly List<RoundRecord> _rounds = new List<RoundRecord>();
        private Timer _timer;
        private int _currentSample = 0;
        private int _runId = 0;            // Changes on every Start/Stop so results of an old run are discarded
        private bool _pingInProgress = false;
        private Label _statsLabel;
        private DateTime _startTime;       // Track when pinging started
        private StreamWriter _logWriter;
        private string _logError;

        // Command line arguments
        private readonly string[] _commandLineArgs;

        // Cursor tracking fields
        private bool _isMouseOverChart = false;
        private double _cursorXValue = double.NaN;
        private List<CursorDataPoint> _cursorDataPoints = new List<CursorDataPoint>();
        private readonly Font _tooltipFont = new Font("Arial", 8);

        [DllImport("iphlpapi.dll")]
        private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

        public MainForm() : this(null)
        {
        }

        public MainForm(string[] args)
        {
            _commandLineArgs = args;
            InitializeComponent();
            LoadIcon();
            InitializeChart();
            InitializeStatsLabel();

            // Process command line arguments if provided
            ProcessCommandLineArguments();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // Invalidate any in-flight ping round so it doesn't touch disposed controls
            _runId++;
            _timer?.Stop();
            _timer?.Dispose();
            _timer = null;
            CloseLog();
            _tooltipFont.Dispose();
            base.OnFormClosed(e);
        }

        private void LoadIcon()
        {
            try
            {
                // First try to load from the Resources folder relative to the executable
                var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "icon.ico");
                if (File.Exists(iconPath))
                {
                    this.Icon = new Icon(iconPath);
                    return;
                }

                // Try embedded resource
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream("icon.ico"))
                {
                    if (stream != null)
                    {
                        this.Icon = new Icon(stream);
                        return;
                    }
                }

                // Try alternative embedded resource names
                using (var stream = assembly.GetManifestResourceStream("PingGrapher.Resources.icon.ico"))
                {
                    if (stream != null)
                    {
                        this.Icon = new Icon(stream);
                    }
                }
            }
            catch (Exception ex)
            {
                // Log error for debugging
                System.Diagnostics.Debug.WriteLine($"Failed to load icon: {ex.Message}");
            }
        }

        private void InitializeChart()
        {
            chart.Series.Clear();
            var area = chart.ChartAreas[0];
            area.AxisX.Title = "Seconds";
            area.AxisX.IntervalAutoMode = IntervalAutoMode.VariableCount; // Let the chart pick a readable label spacing
            area.AxisX.LabelStyle.Format = "0";
            area.AxisY.Title = "Latency (ms)";
            area.AxisY.Minimum = 0;
            area.AxisY.Maximum = 30;

            // Add legend at top, horizontal with wrapping
            chart.Legends.Clear();
            var legend = new Legend("MainLegend")
            {
                Docking = Docking.Top,
                Alignment = StringAlignment.Center,
                BackColor = Color.Transparent,
                Font = new Font("Microsoft Sans Serif", 8F),
                LegendStyle = LegendStyle.Table, // Use table instead of row for better wrapping
                TableStyle = LegendTableStyle.Wide, // Allow wide layout
                MaximumAutoSize = 50 // Limit legend height to 50% of chart
            };
            chart.Legends.Add(legend);

            // Enable cursor tracking functionality
            chart.MouseMove += Chart_MouseMove;
            chart.MouseLeave += Chart_MouseLeave;
            chart.PostPaint += Chart_PostPaint;
        }

        private void InitializeStatsLabel()
        {
            // Position statistics panel below the chart, full width with consistent margins
            _statsLabel = new Label
            {
                Location = new Point(chart.Left, chart.Bottom + StatsMargin),
                Size = new Size(chart.Width, MinStatsHeight),
                Font = new Font("Consolas", 9F), // Use monospace font for better alignment
                BackColor = Color.FromArgb(240, 255, 255, 255), // Semi-transparent white
                BorderStyle = BorderStyle.FixedSingle,
                Text = "Click Start to begin ping monitoring...",
                TextAlign = ContentAlignment.TopLeft,
                Padding = new Padding(10, 10, 10, 10), // Generous padding for readability
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Parent = this
            };
            this.Controls.Add(_statsLabel);
            _statsLabel.BringToFront();
        }

        // Grow the stats panel (and shrink the chart) so every target row is visible
        private void LayoutStatsPanel(int lineCount)
        {
            if (WindowState == FormWindowState.Minimized || ClientSize.Height <= chart.Top) return;

            int lineHeight = TextRenderer.MeasureText("Xg", _statsLabel.Font).Height;
            int desired = lineCount * lineHeight + _statsLabel.Padding.Vertical + 4;
            int maxHeight = Math.Max(MinStatsHeight, (ClientSize.Height - chart.Top) / 2);
            int height = Math.Max(MinStatsHeight, Math.Min(desired, maxHeight));
            if (height == _statsLabel.Height) return;

            int bottomMargin = ClientSize.Height - _statsLabel.Bottom;
            _statsLabel.SetBounds(_statsLabel.Left, ClientSize.Height - bottomMargin - height, _statsLabel.Width, height);
            chart.Height = _statsLabel.Top - StatsMargin - chart.Top;
        }

        #region Target detection

        private List<PingTarget> DetectTargets()
        {
            var targets = new List<PingTarget>();
            var seenHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // Use the interface Windows actually routes internet traffic through (skips VPN/virtual adapters)
                var primary = FindPrimaryInterface();

                string gateway = FindIPv4Address(primary, props => props.GatewayAddresses.Select(g => g.Address));
                if (gateway != null)
                {
                    targets.Add(new PingTarget("Gateway", gateway, Color.Red));
                    seenHosts.Add(gateway);
                }

                string primaryDns = FindIPv4Address(primary, props => props.DnsAddresses);
                if (primaryDns != null)
                {
                    targets.Add(new PingTarget("Primary DNS", primaryDns, Color.Blue));
                    seenHosts.Add(primaryDns);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error detecting gateway/DNS: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            // Add Google DNS as a reliable external reference
            if (_includeGoogleDns)
            {
                targets.Add(new PingTarget("Google DNS", "8.8.8.8", Color.Orange));
                seenHosts.Add("8.8.8.8");
            }

            // Add custom hosts if specified (comma/semicolon separated), skipping duplicates
            Color[] customColors = { Color.Green, Color.Purple, Color.Brown, Color.Pink, Color.Teal, Color.Navy };
            int customColorIndex = 0;
            var customHosts = txtCustomIP.Text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var customHost in customHosts)
            {
                var host = customHost.Trim();
                if (host.Length == 0 || !seenHosts.Add(host)) continue;

                targets.Add(new PingTarget(host, host, customColors[customColorIndex % customColors.Length]));
                customColorIndex++;
            }

            return targets;
        }

        private static NetworkInterface FindPrimaryInterface()
        {
            try
            {
                uint destination = BitConverter.ToUInt32(IPAddress.Parse("8.8.8.8").GetAddressBytes(), 0);
                if (GetBestInterface(destination, out uint index) != 0) return null;

                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        var ipv4 = ni.GetIPProperties().GetIPv4Properties();
                        if (ipv4 != null && ipv4.Index == index) return ni;
                    }
                    catch (NetworkInformationException)
                    {
                        // Interface has no IPv4 - ignore
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"FindPrimaryInterface failed: {ex.Message}");
            }
            return null;
        }

        // First usable IPv4 address from the preferred interface, falling back to any active interface
        private static string FindIPv4Address(NetworkInterface preferred, Func<IPInterfaceProperties, IEnumerable<IPAddress>> selector)
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback);
            if (preferred != null)
                interfaces = new[] { preferred }.Concat(interfaces);

            foreach (var ni in interfaces)
            {
                IEnumerable<IPAddress> addresses;
                try
                {
                    addresses = selector(ni.GetIPProperties());
                }
                catch
                {
                    continue;
                }

                var address = addresses?.FirstOrDefault(a => a != null
                    && a.AddressFamily == AddressFamily.InterNetwork
                    && !a.Equals(IPAddress.Any));
                if (address != null) return address.ToString();
            }
            return null;
        }

        // Resolve a hostname off the UI thread; returns null on failure or timeout
        private static async Task<string> ResolveAsync(string host)
        {
            try
            {
                var lookup = Dns.GetHostAddressesAsync(host);
                if (await Task.WhenAny(lookup, Task.Delay(DnsTimeoutMs)) != lookup)
                {
                    // Observe a late failure so it doesn't surface as an unobserved task exception
                    var ignored = lookup.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    return null;
                }

                var addresses = await lookup;
                var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                              ?? addresses.FirstOrDefault();
                return address?.ToString();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DNS resolution failed for {host}: {ex.Message}");
                return null;
            }
        }

        private void RefreshStaleResolutions()
        {
            foreach (var target in _targets)
            {
                if (target.IsHostname && !target.Resolving && DateTime.Now - target.LastResolved > ReResolveInterval)
                {
                    var ignored = RefreshResolutionAsync(target, _runId);
                }
            }
        }

        private async Task RefreshResolutionAsync(PingTarget target, int runId)
        {
            target.Resolving = true;
            try
            {
                var ip = await ResolveAsync(target.Host);
                if (runId != _runId || IsDisposed || ip == null) return;

                if (ip != target.IP || target.ResolveFailed)
                {
                    target.IP = ip;
                    target.ResolveFailed = false;
                    target.Series.LegendText = target.LegendText;
                }
            }
            finally
            {
                target.Resolving = false;
                target.LastResolved = DateTime.Now;
            }
        }

        #endregion

        #region Start / Stop

        private async Task StartPingingAsync()
        {
            StopPinging();
            int runId = _runId;

            btnStart.Enabled = false;
            btnStop.Enabled = true;
            _statsLabel.Text = "Resolving targets...";

            var targets = DetectTargets();

            // Resolve hostnames in parallel without blocking the UI
            await Task.WhenAll(targets.Where(t => t.IsHostname).Select(async t =>
            {
                var ip = await ResolveAsync(t.Host);
                t.IP = ip ?? t.Host; // If resolution fails, let Ping try the name itself
                t.ResolveFailed = ip == null;
                t.LastResolved = DateTime.Now;
            }));

            // Stop was pressed (or the form closed) while resolving
            if (runId != _runId || IsDisposed) return;

            if (targets.Count == 0)
            {
                StopPinging();
                _statsLabel.Text = "No targets to ping. Enter an IP or hostname and click Start.";
                return;
            }

            // Clear existing targets and chart
            _targets.Clear();
            _targets.AddRange(targets);
            _rounds.Clear();
            BuildChartSeries();

            _currentSample = 0;
            _startTime = DateTime.Now; // Record start time for accurate timestamps
            _logError = null;
            OpenLog();

            // Reset chart axes - let it auto-scale initially
            var area = chart.ChartAreas[0];
            area.AxisX.Minimum = double.NaN;
            area.AxisX.Maximum = double.NaN;
            area.AxisY.Maximum = 30;

            if (_timer == null)
            {
                _timer = new Timer();
                _timer.Tick += TimerTick;
            }
            _timer.Interval = _intervalMs;
            _timer.Start();

            UpdateStatistics();
        }

        private void StopPinging()
        {
            _runId++; // Discard results of pings still in flight
            _pingInProgress = false;
            _timer?.Stop();
            CloseLog();
            btnStart.Enabled = true;
            btnStop.Enabled = false;
        }

        private void BuildChartSeries()
        {
            chart.Series.Clear();
            _cursorDataPoints.Clear();

            // Series names are internal keys; the legend shows LegendText, so duplicate labels can't collide
            for (int i = 0; i < _targets.Count; i++)
            {
                var target = _targets[i];
                target.Series = new Series("target" + i)
                {
                    ChartType = SeriesChartType.Line, // Line (unlike FastLine) supports gaps for timeouts
                    BorderWidth = 2,
                    Color = target.Color,
                    LegendText = target.LegendText
                };
                target.Series.EmptyPointStyle.Color = Color.Transparent;
                chart.Series.Add(target.Series);
            }

            // Timeout markers are drawn on top of all lines
            for (int i = 0; i < _targets.Count; i++)
            {
                var target = _targets[i];
                target.TimeoutSeries = new Series("timeouts" + i)
                {
                    ChartType = SeriesChartType.Point,
                    MarkerStyle = MarkerStyle.Cross,
                    MarkerSize = 8,
                    MarkerColor = Color.Red,
                    Color = Color.Red,
                    IsVisibleInLegend = false
                };
                chart.Series.Add(target.TimeoutSeries);
            }
        }

        private async void btnStart_Click(object sender, EventArgs e)
        {
            await StartPingingAsync();
        }

        private void btnStop_Click(object sender, EventArgs e)
        {
            StopPinging();
        }

        #endregion

        #region Ping loop

        private async void TimerTick(object sender, EventArgs e)
        {
            // A previous round is still waiting for replies (e.g. a host is timing out) - skip this tick
            // instead of starting overlapping rounds
            if (_pingInProgress) return;
            _pingInProgress = true;
            int runId = _runId;

            try
            {
                var targets = _targets.ToArray();
                var timestamp = DateTime.Now;
                double x = (timestamp - _startTime).TotalSeconds;
                int timeoutMs = _timeoutMs;

                // Ping all targets in parallel, staggered slightly to avoid bursts
                var tasks = new Task<PingResult>[targets.Length];
                for (int i = 0; i < targets.Length; i++)
                {
                    int delay = PingStaggerMs * i;
                    string address = targets[i].IP; // Read on the UI thread
                    tasks[i] = Task.Run(async () =>
                    {
                        if (delay > 0) await Task.Delay(delay);
                        return await PingTarget.PingAsync(address, timeoutMs);
                    });
                }

                var results = await Task.WhenAll(tasks);

                // Stop/Start was pressed or the form closed while we were waiting
                if (runId != _runId || IsDisposed) return;

                // Everything below runs on the UI thread
                _currentSample++;
                for (int i = 0; i < targets.Length; i++)
                {
                    targets[i].Record(results[i]);
                    AddPoint(targets[i], x, results[i]);
                }

                _rounds.Add(new RoundRecord(timestamp, x, results.Select(r => r.Success ? r.RoundtripTime : -1).ToArray()));
                if (_rounds.Count > MaxRoundsKept)
                    _rounds.RemoveRange(0, _rounds.Count - MaxRoundsKept + 10000);

                TrimToWindow(x);
                AdjustYAxis();
                UpdateStatistics();
                WriteLog(timestamp, targets, results);
                RefreshStaleResolutions();
            }
            catch (Exception ex)
            {
                // Continue with next iteration even if this one fails
                System.Diagnostics.Debug.WriteLine($"TimerTick error: {ex.Message}");
            }
            finally
            {
                if (runId == _runId) _pingInProgress = false;
            }
        }

        private void AddPoint(PingTarget target, double x, PingResult result)
        {
            int index = target.Series.Points.AddXY(x, result.Success ? result.RoundtripTime : 0);
            if (result.Success) return;

            // Failed ping: leave a gap in the line and add a red cross marker near the top of the chart
            target.Series.Points[index].IsEmpty = true;
            int markerIndex = target.TimeoutSeries.Points.AddXY(x, chart.ChartAreas[0].AxisY.Maximum * TimeoutMarkerRatio);
            target.TimeoutSeries.Points[markerIndex].ToolTip = $"{target.Name}: {result.Status}";
        }

        // Drop points older than the window and scroll the X axis once the window is full
        private void TrimToWindow(double latestX)
        {
            double cutoff = latestX - _windowSeconds;
            foreach (var target in _targets)
            {
                RemovePointsBefore(target.Series, cutoff);
                RemovePointsBefore(target.TimeoutSeries, cutoff);
            }

            var axisX = chart.ChartAreas[0].AxisX;
            if (cutoff > 0)
            {
                axisX.Minimum = Math.Floor(cutoff);
                axisX.Maximum = Math.Ceiling(latestX);
            }
            else
            {
                axisX.Minimum = double.NaN;
                axisX.Maximum = double.NaN;
            }
        }

        private static void RemovePointsBefore(Series series, double cutoff)
        {
            while (series.Points.Count > 0 && series.Points[0].XValue < cutoff)
            {
                series.Points.RemoveAt(0);
            }
        }

        private void AdjustYAxis()
        {
            double max = double.NaN;
            foreach (var target in _targets)
            {
                foreach (var point in target.Series.Points)
                {
                    // Only consider real measurements, not timeout gaps
                    if (!point.IsEmpty && !(point.YValues[0] <= max))
                    {
                        max = point.YValues[0];
                    }
                }
            }

            if (double.IsNaN(max)) return;

            var axisY = chart.ChartAreas[0].AxisY;
            double newMax = Math.Max(30, NiceCeiling(max)); // Minimum 30ms
            if (newMax == axisY.Maximum) return;
            axisY.Maximum = newMax;

            // Keep timeout markers at the top of the rescaled axis
            double markerY = newMax * TimeoutMarkerRatio;
            foreach (var target in _targets)
            {
                foreach (var point in target.TimeoutSeries.Points)
                {
                    point.SetValueY(markerY);
                }
            }
        }

        private static double NiceCeiling(double value)
        {
            double step = value <= 100 ? 10 : value <= 500 ? 50 : value <= 2000 ? 100 : 500;
            return Math.Ceiling(value / step) * step;
        }

        #endregion

        #region Statistics

        private void UpdateStatistics()
        {
            if (!_targets.Any())
            {
                _statsLabel.Text = "Click Start to begin ping monitoring...";
                return;
            }

            var stats = new StringBuilder();
            double elapsed = _rounds.Count > 0 ? _rounds[_rounds.Count - 1].Elapsed : 0;

            stats.AppendLine($"Ping Statistics - {_currentSample} rounds in {FormatDuration(elapsed)}   (latency: visible window, loss: whole session)");
            if (_logError != null)
                stats.AppendLine($"Logging stopped: {_logError}");
            stats.AppendLine(new string('─', 84));
            stats.AppendLine($"{"Target",-20}{"Last",9}{"Avg",9}{"Min",9}{"Max",9}{"Jitter",9}   Loss");

            foreach (var target in _targets)
            {
                var name = target.Name.Length > 18 ? target.Name.Substring(0, 15) + "..." : target.Name;
                string last = target.LastResult == null ? "-"
                    : target.LastResult.Success ? $"{target.LastResult.RoundtripTime}ms" : "timeout";
                var window = WindowStats.From(target.Series);
                string loss = $"{target.PacketLossPercentage,5:F1}% ({target.PacketsLost}/{target.PacketsSent})";

                if (window.Count > 0)
                {
                    stats.AppendLine($"{name,-20}{last,9}{Ms(window.Avg),9}{Ms(window.Min),9}{Ms(window.Max),9}{Ms(window.Jitter),9}   {loss}");
                }
                else
                {
                    stats.AppendLine($"{name,-20}{last,9}{"   No response yet",-48}{loss}");
                }
            }

            _statsLabel.Text = stats.ToString().TrimEnd();
            LayoutStatsPanel(_statsLabel.Text.Split('\n').Length);
        }

        private static string Ms(double value) => $"{value:F0}ms";

        private static string FormatDuration(double seconds)
        {
            var span = TimeSpan.FromSeconds(seconds);
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours}h {span.Minutes:D2}m {span.Seconds:D2}s"
                : $"{span.Minutes}m {span.Seconds:D2}s";
        }

        #endregion

        #region Command line

        private void ProcessCommandLineArguments()
        {
            if (_commandLineArgs == null || _commandLineArgs.Length == 0)
            {
                return; // No command line arguments, use default behavior
            }

            var customTargets = new List<string>();
            var errors = new List<string>();
            bool showHelp = false;

            for (int i = 0; i < _commandLineArgs.Length; i++)
            {
                // Remove quotes if present and trim whitespace
                var arg = _commandLineArgs[i].Trim('"', '\'').Trim();
                if (arg.Length == 0) continue;

                if (!arg.StartsWith("-") && !arg.StartsWith("/"))
                {
                    customTargets.Add(arg);
                    continue;
                }

                string option = arg.TrimStart('-', '/').ToLowerInvariant();
                string value = i + 1 < _commandLineArgs.Length ? _commandLineArgs[i + 1].Trim('"', '\'').Trim() : null;

                switch (option)
                {
                    case "i":
                    case "interval":
                        if (TryParseRange(value, 100, 60000, out int interval)) { _intervalMs = interval; i++; }
                        else errors.Add($"{arg} needs a value between 100 and 60000 (ms)");
                        break;
                    case "t":
                    case "timeout":
                        if (TryParseRange(value, 100, 30000, out int timeout)) { _timeoutMs = timeout; i++; }
                        else errors.Add($"{arg} needs a value between 100 and 30000 (ms)");
                        break;
                    case "w":
                    case "window":
                        if (TryParseRange(value, 10, 86400, out int window)) { _windowSeconds = window; i++; }
                        else errors.Add($"{arg} needs a value between 10 and 86400 (seconds)");
                        break;
                    case "log":
                        if (!string.IsNullOrEmpty(value) && !value.StartsWith("-"))
                        {
                            try
                            {
                                _logPath = Path.GetFullPath(value);
                            }
                            catch (Exception ex)
                            {
                                errors.Add($"Invalid log path '{value}': {ex.Message}");
                            }
                            i++;
                        }
                        else errors.Add($"{arg} needs a file path");
                        break;
                    case "nogoogle":
                        _includeGoogleDns = false;
                        break;
                    case "h":
                    case "?":
                    case "help":
                        showHelp = true;
                        break;
                    default:
                        errors.Add($"Unknown option {arg}");
                        break;
                }
            }

            if (errors.Count > 0)
            {
                // Show error message but don't crash the application
                MessageBox.Show(string.Join("\r\n", errors) + "\r\n\r\n" + UsageText,
                    "Command Line Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else if (showHelp)
            {
                MessageBox.Show(UsageText, "PingGrapher Help", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            if (customTargets.Count > 0)
            {
                // Set the custom IP textbox with the command line arguments
                txtCustomIP.Text = string.Join(", ", customTargets);

                // Automatically start pinging after a short delay to ensure UI is ready
                var startTimer = new Timer
                {
                    Interval = 500, // 500ms delay
                    Enabled = true
                };
                startTimer.Tick += async (sender, e) =>
                {
                    startTimer.Stop();
                    startTimer.Dispose();

                    if (btnStart.Enabled)
                    {
                        await StartPingingAsync();
                    }
                };
            }
        }

        private static bool TryParseRange(string text, int min, int max, out int value)
        {
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                   && value >= min && value <= max;
        }

        #endregion

        #region Logging and export

        private void OpenLog()
        {
            CloseLog();
            if (string.IsNullOrEmpty(_logPath)) return;

            try
            {
                bool writeHeader = !File.Exists(_logPath) || new FileInfo(_logPath).Length == 0;
                _logWriter = new StreamWriter(_logPath, true, new UTF8Encoding(false)) { AutoFlush = true };
                if (writeHeader)
                    _logWriter.WriteLine("Timestamp,Target,Host,IP,LatencyMs,Status");
            }
            catch (Exception ex)
            {
                _logWriter = null;
                _logError = ex.Message;
            }
        }

        private void CloseLog()
        {
            try
            {
                _logWriter?.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Closing log failed: {ex.Message}");
            }
            _logWriter = null;
        }

        private void WriteLog(DateTime timestamp, PingTarget[] targets, PingResult[] results)
        {
            if (_logWriter == null) return;

            try
            {
                string time = timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                for (int i = 0; i < targets.Length; i++)
                {
                    _logWriter.WriteLine(string.Join(",",
                        time,
                        CsvEscape(targets[i].Name),
                        CsvEscape(targets[i].Host),
                        CsvEscape(results[i].Address),
                        results[i].Success ? results[i].RoundtripTime.ToString(CultureInfo.InvariantCulture) : "",
                        results[i].Status));
                }
            }
            catch (Exception ex)
            {
                // Stop logging rather than failing every tick; the error is shown in the stats panel
                _logError = ex.Message;
                CloseLog();
            }
        }

        private static string CsvEscape(string value)
        {
            if (value == null) return "";
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;
        }

        private void btnDownload_Click(object sender, EventArgs e)
        {
            try
            {
                using (var saveDialog = new SaveFileDialog())
                {
                    saveDialog.Filter = "PNG Images (*.png)|*.png|JPEG Images (*.jpg)|*.jpg|Bitmap Images (*.bmp)|*.bmp|CSV Data (*.csv)|*.csv";
                    saveDialog.DefaultExt = "png";
                    saveDialog.FileName = $"PingGraph_{DateTime.Now:yyyyMMdd_HHmmss}";

                    if (saveDialog.ShowDialog() != DialogResult.OK) return;

                    switch (saveDialog.FilterIndex)
                    {
                        case 2:
                            ExportImage(saveDialog.FileName, System.Drawing.Imaging.ImageFormat.Jpeg);
                            break;
                        case 3:
                            ExportImage(saveDialog.FileName, System.Drawing.Imaging.ImageFormat.Bmp);
                            break;
                        case 4:
                            if (_rounds.Count == 0)
                            {
                                MessageBox.Show("There is no ping data to export yet.", "Export",
                                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                                return;
                            }
                            ExportCsv(saveDialog.FileName);
                            break;
                        default:
                            ExportImage(saveDialog.FileName, System.Drawing.Imaging.ImageFormat.Png);
                            break;
                    }

                    MessageBox.Show($"Exported successfully to {saveDialog.FileName}", "Export Complete",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error exporting: {ex.Message}", "Export Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ExportImage(string path, System.Drawing.Imaging.ImageFormat format)
        {
            // Create a bitmap of the chart and statistics area
            int exportHeight = chart.Height + StatsMargin + _statsLabel.Height;
            using (var bitmap = new Bitmap(chart.Width, exportHeight))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    // Fill background with white
                    graphics.FillRectangle(Brushes.White, 0, 0, bitmap.Width, bitmap.Height);

                    // Draw the chart
                    chart.DrawToBitmap(bitmap, new Rectangle(0, 0, chart.Width, chart.Height));

                    // Draw the statistics label
                    if (_statsLabel.Visible && !string.IsNullOrEmpty(_statsLabel.Text))
                    {
                        var statsRect = new Rectangle(0, chart.Height + StatsMargin, chart.Width - 1, _statsLabel.Height - 1);
                        using (var backgroundBrush = new SolidBrush(_statsLabel.BackColor))
                        using (var textBrush = new SolidBrush(_statsLabel.ForeColor))
                        using (var format2 = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near })
                        {
                            graphics.FillRectangle(backgroundBrush, statsRect);
                            graphics.DrawRectangle(Pens.Black, statsRect);
                            var textRect = new Rectangle(statsRect.X + _statsLabel.Padding.Left, statsRect.Y + _statsLabel.Padding.Top,
                                statsRect.Width - _statsLabel.Padding.Horizontal, statsRect.Height - _statsLabel.Padding.Vertical);
                            graphics.DrawString(_statsLabel.Text, _statsLabel.Font, textBrush, textRect, format2);
                        }
                    }
                }

                bitmap.Save(path, format);
            }
        }

        // One row per ping round, one column per target; an empty cell means timeout/error
        private void ExportCsv(string path)
        {
            var culture = CultureInfo.InvariantCulture;
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                writer.WriteLine("Timestamp,ElapsedSeconds," + string.Join(",", _targets.Select(t => CsvEscape(t.Series.LegendText))));
                foreach (var round in _rounds)
                {
                    writer.WriteLine(round.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", culture) + "," +
                                     round.Elapsed.ToString("0.000", culture) + "," +
                                     string.Join(",", round.Results.Select(r => r >= 0 ? r.ToString(culture) : "")));
                }
            }
        }

        #endregion

        #region Cursor tracking

        private void Chart_MouseMove(object sender, MouseEventArgs e)
        {
            if (_targets.Count == 0 || _targets[0].Series == null) return;

            try
            {
                var xValue = chart.ChartAreas[0].AxisX.PixelPositionToValue(e.X);
                var points = new List<CursorDataPoint>();
                double snappedX = double.NaN;

                // Find data points at the cursor position for each target
                foreach (var target in _targets)
                {
                    var closestPoint = FindClosestDataPoint(target.Series, xValue);
                    if (closestPoint == null) continue;

                    if (double.IsNaN(snappedX)) snappedX = closestPoint.XValue;
                    points.Add(new CursorDataPoint
                    {
                        SeriesName = target.Series.LegendText,
                        XValue = closestPoint.XValue,
                        YValue = closestPoint.YValues[0],
                        Color = target.Color,
                        Timestamp = _startTime.AddSeconds(closestPoint.XValue),
                        IsTimeout = closestPoint.IsEmpty
                    });
                }

                // Only repaint when the cursor snaps to a different sample
                bool changed = !_isMouseOverChart || snappedX != _cursorXValue || points.Count != _cursorDataPoints.Count;
                _isMouseOverChart = true;
                _cursorXValue = snappedX;
                _cursorDataPoints = points;

                if (changed) chart.Invalidate();
            }
            catch
            {
                // Ignore errors during mouse tracking
            }
        }

        private void Chart_MouseLeave(object sender, EventArgs e)
        {
            _isMouseOverChart = false;
            _cursorDataPoints.Clear();
            chart.Invalidate(); // Remove cursor tracking display
        }

        private void Chart_PostPaint(object sender, ChartPaintEventArgs e)
        {
            if (!_isMouseOverChart || _cursorDataPoints.Count == 0 || double.IsNaN(_cursorXValue)) return;

            try
            {
                var area = chart.ChartAreas[0];
                var graphics = e.ChartGraphics.Graphics;
                float xPixel = (float)area.AxisX.ValueToPixelPosition(_cursorXValue);
                float top = (float)area.AxisY.ValueToPixelPosition(area.AxisY.Maximum);
                float bottom = (float)area.AxisY.ValueToPixelPosition(area.AxisY.Minimum);

                // Draw vertical line at cursor position
                using (var pen = new Pen(Color.Gray, 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
                {
                    graphics.DrawLine(pen, xPixel, top, xPixel, bottom);
                }

                // Draw dots on each line at cursor position
                using (var borderPen = new Pen(Color.White, 1))
                {
                    foreach (var dataPoint in _cursorDataPoints)
                    {
                        double y = dataPoint.IsTimeout ? area.AxisY.Maximum * TimeoutMarkerRatio : dataPoint.YValue;
                        float yPixel = (float)area.AxisY.ValueToPixelPosition(y);
                        float dotSize = dataPoint.IsTimeout ? 8 : 6;

                        using (var brush = new SolidBrush(dataPoint.Color))
                        {
                            graphics.FillEllipse(brush, xPixel - dotSize / 2, yPixel - dotSize / 2, dotSize, dotSize);
                        }
                        // Draw white border around dot for visibility
                        graphics.DrawEllipse(borderPen, xPixel - dotSize / 2, yPixel - dotSize / 2, dotSize, dotSize);
                    }
                }

                // Draw tooltip with timestamp and values
                DrawCursorTooltip(graphics, xPixel, _cursorDataPoints);
            }
            catch
            {
                // Ignore errors during custom painting
            }
        }

        // Points are sorted by X (elapsed seconds), so a binary search is enough
        private static DataPoint FindClosestDataPoint(Series series, double xValue)
        {
            var points = series.Points;
            if (points.Count == 0) return null;

            int lo = 0, hi = points.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (points[mid].XValue < xValue) lo = mid + 1;
                else hi = mid;
            }

            if (lo > 0 && Math.Abs(points[lo - 1].XValue - xValue) <= Math.Abs(points[lo].XValue - xValue))
                lo--;
            return points[lo];
        }

        private void DrawCursorTooltip(Graphics graphics, double xPixel, List<CursorDataPoint> dataPoints)
        {
            if (dataPoints.Count == 0) return;

            var tooltipText = new StringBuilder();
            tooltipText.AppendLine($"Time: {dataPoints[0].Timestamp:HH:mm:ss}");
            tooltipText.AppendLine($"Elapsed: {FormatDuration(dataPoints[0].XValue)}");
            tooltipText.AppendLine();

            foreach (var point in dataPoints)
            {
                var displayName = point.SeriesName.Length > 20 ?
                    point.SeriesName.Substring(0, 20) + "..." : point.SeriesName;

                if (point.IsTimeout)
                {
                    tooltipText.AppendLine($"{displayName}: Timeout");
                }
                else
                {
                    tooltipText.AppendLine($"{displayName}: {point.YValue:F0}ms");
                }
            }

            var text = tooltipText.ToString().TrimEnd();
            using (var brush = new SolidBrush(Color.Black))
            using (var backgroundBrush = new SolidBrush(Color.FromArgb(240, 255, 255, 200)))
            using (var borderPen = new Pen(Color.Gray, 1))
            {
                var textSize = graphics.MeasureString(text, _tooltipFont);
                var padding = 6;
                var tooltipWidth = textSize.Width + padding * 2;
                var tooltipHeight = textSize.Height + padding * 2;

                // Position tooltip to the right or left of cursor based on available space
                var tooltipX = xPixel + 10;
                if (tooltipX + tooltipWidth > chart.Width - 20)
                {
                    tooltipX = xPixel - tooltipWidth - 10;
                }

                var tooltipY = 20;
                var tooltipRect = new RectangleF((float)tooltipX, tooltipY, tooltipWidth, tooltipHeight);

                graphics.FillRectangle(backgroundBrush, tooltipRect);
                graphics.DrawRectangle(borderPen, Rectangle.Round(tooltipRect));
                graphics.DrawString(text, _tooltipFont, brush, (float)tooltipX + padding, tooltipY + padding);
            }
        }

        #endregion
    }

    public class PingTarget
    {
        public string Name { get; }
        public string Host { get; }   // As entered or detected (IP or hostname)
        public string IP { get; set; } // Address actually pinged
        public Color Color { get; }
        public bool IsHostname { get; }
        public bool ResolveFailed { get; set; }
        public bool Resolving { get; set; }
        public DateTime LastResolved { get; set; }

        public Series Series { get; set; }
        public Series TimeoutSeries { get; set; }

        // Session statistics - only touched on the UI thread
        public int PacketsSent { get; private set; }
        public int PacketsLost { get; private set; }
        public PingResult LastResult { get; private set; }
        public double PacketLossPercentage => PacketsSent > 0 ? (PacketsLost / (double)PacketsSent) * 100 : 0;

        // Use a buffer like CLI ping (32 bytes, pattern abcdefghijklmnopqrstuvw...)
        private static readonly byte[] Buffer = Enumerable.Range(0, 32).Select(i => (byte)(97 + (i % 23))).ToArray();

        public PingTarget(string name, string host, Color color)
        {
            Name = name;
            Host = host;
            IP = host;
            Color = color;
            IsHostname = !IPAddress.TryParse(host, out _);
        }

        public string LegendText
        {
            get
            {
                if (IsHostname)
                    return ResolveFailed ? $"{Host} (unresolved)" : $"{Host} ({IP})";
                return Name == IP ? IP : $"{Name} ({IP})";
            }
        }

        public void Record(PingResult result)
        {
            PacketsSent++;
            if (!result.Success) PacketsLost++;
            LastResult = result;
        }

        // Thread-safe: doesn't touch any shared state
        public static async Task<PingResult> PingAsync(string address, int timeoutMs)
        {
            // Create a new Ping instance for each ping to avoid concurrency issues
            using (var ping = new Ping())
            {
                try
                {
                    // TTL 128 (Windows default); fragmentation allowed, like CLI ping
                    var options = new PingOptions
                    {
                        Ttl = 128,
                        DontFragment = false
                    };

                    var reply = await ping.SendPingAsync(address, timeoutMs, Buffer, options);
                    if (reply.Status != IPStatus.Success)
                    {
                        // Log the specific failure reason for debugging
                        System.Diagnostics.Debug.WriteLine($"Ping failed to {address}: {reply.Status}");
                    }
                    return new PingResult(address, reply.Status == IPStatus.Success, reply.RoundtripTime, reply.Status.ToString());
                }
                catch (Exception ex)
                {
                    // Log the exception for debugging
                    System.Diagnostics.Debug.WriteLine($"Ping exception to {address}: {ex.Message}");
                    return new PingResult(address, false, 0, "Error");
                }
            }
        }
    }

    public class PingResult
    {
        public string Address { get; }
        public bool Success { get; }
        public long RoundtripTime { get; }
        public string Status { get; }

        public PingResult(string address, bool success, long roundtripTime, string status)
        {
            Address = address;
            Success = success;
            RoundtripTime = roundtripTime;
            Status = status;
        }
    }

    // One ping round, kept for CSV export (-1 = timeout/error)
    public class RoundRecord
    {
        public DateTime Timestamp { get; }
        public double Elapsed { get; }
        public long[] Results { get; }

        public RoundRecord(DateTime timestamp, double elapsed, long[] results)
        {
            Timestamp = timestamp;
            Elapsed = elapsed;
            Results = results;
        }
    }

    // Latency statistics over the points currently visible in a series
    public struct WindowStats
    {
        public int Count;
        public double Avg;
        public double Min;
        public double Max;
        public double Jitter; // Mean absolute difference between consecutive replies

        public static WindowStats From(Series series)
        {
            var stats = new WindowStats { Min = double.MaxValue };
            if (series == null) return stats;

            double sum = 0, jitterSum = 0, previous = double.NaN;
            int jitterCount = 0;
            foreach (var point in series.Points)
            {
                if (point.IsEmpty) continue;

                double value = point.YValues[0];
                stats.Count++;
                sum += value;
                if (value < stats.Min) stats.Min = value;
                if (value > stats.Max) stats.Max = value;
                if (!double.IsNaN(previous))
                {
                    jitterSum += Math.Abs(value - previous);
                    jitterCount++;
                }
                previous = value;
            }

            if (stats.Count > 0) stats.Avg = sum / stats.Count;
            if (jitterCount > 0) stats.Jitter = jitterSum / jitterCount;
            return stats;
        }
    }

    // Helper class for cursor tracking data
    public class CursorDataPoint
    {
        public string SeriesName { get; set; }
        public double XValue { get; set; }
        public double YValue { get; set; }
        public Color Color { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsTimeout { get; set; }
    }
}
