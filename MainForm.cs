using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

namespace PingGrapher
{
    public partial class MainForm : Form
    {
        private readonly List<PingTarget> _targets = new List<PingTarget>();
        private Timer _timer;
        private const int MaxSamples = 3600; // Increased to 1 hour (3600 seconds)
        private int _currentSample = 0;
        private Label _statsLabel;
        private DateTime _startTime; // Track when pinging started
        
        // Command line arguments
        private readonly string[] _commandLineArgs;
        
        // Cursor tracking fields
        private bool _isMouseOverChart = false;
        private double _mouseXValue = 0;
        private List<CursorDataPoint> _cursorDataPoints = new List<CursorDataPoint>();

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

        private void LoadIcon()
        {
            try
            {
                // First try to load from the Resources folder relative to the executable
                var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "icon.ico");
                if (System.IO.File.Exists(iconPath))
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
            chart.ChartAreas[0].AxisX.Interval = 50;
            chart.ChartAreas[0].AxisX.Title = "Seconds";
            chart.ChartAreas[0].AxisY.Title = "Latency (ms)";
            chart.ChartAreas[0].AxisY.Minimum = 0;
            chart.ChartAreas[0].AxisY.Maximum = 30;
            
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
            int statsWidth = chart.Width; // Use full chart width
            int statsHeight = 140; // Slightly increased height for better line spacing
            int margin = 10; // Consistent margin like the chart
            
            _statsLabel = new Label
            {
                Location = new Point(chart.Left, chart.Bottom + margin),
                Size = new Size(statsWidth, statsHeight),
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

        private void AutoDetectTargets()
        {
            try
            {
                Color[] customColors = { Color.Green, Color.Purple, Color.Brown, Color.Pink, Color.Teal, Color.Navy };
                int customColorIndex = 0;
                
                // Get default gateway
                string gateway = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
                    .Select(ni => ni.GetIPProperties()?.GatewayAddresses?.FirstOrDefault()?.Address?.ToString())
                    .FirstOrDefault(ip => !string.IsNullOrEmpty(ip));

                if (gateway != null)
                    AddTarget("Gateway", gateway, Color.Red);

                // Get primary DNS server from network configuration
                string primaryDns = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.OperationalStatus == OperationalStatus.Up && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .Select(ni => ni.GetIPProperties()?.DnsAddresses?.FirstOrDefault()?.ToString())
                    .FirstOrDefault(ip => !string.IsNullOrEmpty(ip) && !ip.StartsWith("fe80") && !ip.Contains(":"));

                if (primaryDns != null)
                    AddTarget("Primary DNS", primaryDns, Color.Blue);

                // Add Google DNS as a reliable external reference
                AddTarget("Google DNS", "8.8.8.8", Color.Orange);

                // Add custom IPs if specified (comma-separated)
                if (!string.IsNullOrWhiteSpace(txtCustomIP.Text))
                {
                    var customIPs = txtCustomIP.Text.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var customIP in customIPs)
                    {
                        var trimmedIP = customIP.Trim();
                        if (!string.IsNullOrWhiteSpace(trimmedIP))
                        {
                            var color = customColors[customColorIndex % customColors.Length];
                            AddTarget(trimmedIP, trimmedIP, color);
                            customColorIndex++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error detecting targets: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void AddTarget(string name, string ip, Color color)
        {
            if (string.IsNullOrWhiteSpace(ip)) return;

            var target = new PingTarget(name, ip, color);
            _targets.Add(target);

            // Create display name with IP resolution
            string displayName = name;
            try
            {
                // If it's already an IP address, show as "Name (IP)"
                if (System.Net.IPAddress.TryParse(ip, out _))
                {
                    displayName = $"{name} ({ip})";
                }
                else
                {
                    // If it's a hostname, resolve it and show as "Hostname (ResolvedIP)"
                    var addresses = Dns.GetHostAddresses(ip);
                    var resolvedIP = addresses.FirstOrDefault(addr => addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                    if (resolvedIP != null)
                    {
                        displayName = $"{ip} ({resolvedIP})";
                        // Update target to use resolved IP for pinging
                        target = new PingTarget(name, resolvedIP.ToString(), color);
                        _targets[_targets.Count - 1] = target;
                    }
                }
            }
            catch
            {
                // If resolution fails, just use the original name
                displayName = $"{name} ({ip})";
            }

            var series = new Series(displayName)
            {
                ChartType = SeriesChartType.FastLine,
                BorderWidth = 2,
                Color = color
            };
            chart.Series.Add(series);
        }

        private async void TimerTick(object sender, EventArgs e)
        {
            _currentSample++;
            
            // Ping targets sequentially with small delays to avoid overwhelming the network
            var tasks = new List<Task<(int index, long result)>>();
            
            for (int i = 0; i < _targets.Count; i++)
            {
                int targetIndex = i; // Capture index for closure
                tasks.Add(Task.Run(async () =>
                {
                    // Add small delay between pings to reduce network congestion
                    if (targetIndex > 0)
                    {
                        await Task.Delay(50 * targetIndex); // 50ms delay per target
                    }
                    var result = await _targets[targetIndex].PingAsync();
                    return (targetIndex, result);
                }));
            }
            
            // Wait for all pings to complete with a reasonable timeout
            try
            {
                var results = await Task.WhenAll(tasks);
                bool needsAxisUpdate = false;
                
                foreach (var (index, result) in results)
                {
                    var series = chart.Series[index];
                    
                    if (result >= 0)
                    {
                        // Successful ping - add normal point
                        series.Points.AddXY(_currentSample, result);
                    }
                    else
                    {
                        // Failed ping - add error marker at top of current Y-axis range
                        var errorPoint = series.Points.AddXY(_currentSample, chart.ChartAreas[0].AxisY.Maximum * 0.95);
                        var point = series.Points[series.Points.Count - 1];
                        point.MarkerStyle = MarkerStyle.Cross;
                        point.MarkerSize = 8;
                        point.MarkerColor = Color.Red;
                        point.ToolTip = "Timeout/Error";
                    }

                    // Remove old points and adjust chart view only when needed
                    if (series.Points.Count > MaxSamples)
                    {
                        series.Points.RemoveAt(0);
                        needsAxisUpdate = true;
                    }
                }
                
                // Only update X-axis when we start removing points (sliding window mode)
                if (needsAxisUpdate)
                {
                    var minX = _currentSample - MaxSamples + 1;
                    var maxX = _currentSample;
                    
                    chart.ChartAreas[0].AxisX.Minimum = minX;
                    chart.ChartAreas[0].AxisX.Maximum = maxX;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"TimerTick error: {ex.Message}");
                // Continue with next iteration even if this one fails
            }

            UpdateStatistics();
            AdjustYAxis();
        }

        private void AdjustYAxis()
        {
            var validValues = new List<double>();
            
            foreach (var series in chart.Series)
            {
                foreach (var point in series.Points)
                {
                    // Only consider points that aren't error markers
                    if (point.MarkerStyle == MarkerStyle.None)
                    {
                        validValues.Add(point.YValues[0]);
                    }
                }
            }
            
            if (validValues.Any())
            {
                double max = validValues.Max();
                double newMax = Math.Max(30, Math.Ceiling(max / 10.0) * 10); // Minimum 30ms, round up to nearest 10
                chart.ChartAreas[0].AxisY.Maximum = Math.Min(newMax, 200); // Cap at 200ms for readability
            }
        }

        private void UpdateStatistics()
        {
            var stats = new System.Text.StringBuilder();
            
            if (_targets.Any())
            {
                // Simple header with total samples
                stats.AppendLine($"Ping Statistics - {_targets.First().PacketsSent} samples sent");
                stats.AppendLine(new string('?', 50)); // Simple separator line
                
                // Show each target on its own line with clear, simple format
                foreach (var target in _targets)
                {
                    var displayName = target.Name.Length > 15 ? target.Name.Substring(0, 15) + "..." : target.Name;
                    
                    if (target.SuccessfulPings.Any())
                    {
                        stats.AppendLine($"{displayName,-18} {target.PacketLossPercentage:F0}% loss    {target.AvgPing:F0}ms avg    ({target.MinPing}-{target.MaxPing}ms range)");
                    }
                    else
                    {
                        stats.AppendLine($"{displayName,-18} {target.PacketLossPercentage:F0}% loss    No response yet");
                    }
                }
            }
            else
            {
                stats.AppendLine("Click Start to begin ping monitoring...");
            }
            
            _statsLabel.Text = stats.ToString();
        }

        private void ProcessCommandLineArguments()
        {
            if (_commandLineArgs == null || _commandLineArgs.Length == 0)
            {
                return; // No command line arguments, use default behavior
            }

            try
            {
                // Parse command line arguments and set custom IP text box
                var customTargets = new List<string>();
                
                foreach (var arg in _commandLineArgs)
                {
                    // Remove quotes if present and trim whitespace
                    var cleanArg = arg.Trim('"', '\'').Trim();
                    
                    if (!string.IsNullOrWhiteSpace(cleanArg))
                    {
                        customTargets.Add(cleanArg);
                    }
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
                    startTimer.Tick += (sender, e) =>
                    {
                        startTimer.Stop();
                        startTimer.Dispose();
                        
                        // Simulate button click to start pinging
                        if (btnStart.Enabled)
                        {
                            StartPinging();
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                // Show error message but don't crash the application
                MessageBox.Show($"Error processing command line arguments: {ex.Message}\r\n\r\nThe application will start normally.", 
                    "Command Line Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void StartPinging()
        {
            if (_timer == null)
            {
                _timer = new Timer { Interval = 1000 };
                _timer.Tick += TimerTick;
            }

            // Clear existing targets and chart
            _targets.Clear();
            chart.Series.Clear();
            
            // Re-detect targets (including updated custom IP)
            AutoDetectTargets();
            
            // Reset statistics for all targets
            foreach (var target in _targets)
            {
                target.ResetStatistics();
            }
            
            _currentSample = 0;
            _startTime = DateTime.Now; // Record start time for accurate timestamps
            
            // Reset chart axes - let it auto-scale initially
            chart.ChartAreas[0].AxisX.Minimum = Double.NaN; // Auto-scale
            chart.ChartAreas[0].AxisX.Maximum = Double.NaN; // Auto-scale
            
            _timer.Start();
            btnStart.Enabled = false;
            btnStop.Enabled = true;
        }

        private void btnStart_Click(object sender, EventArgs e)
        {
            StartPinging();
        }

        private void btnStop_Click(object sender, EventArgs e)
        {
            _timer?.Stop();
            btnStart.Enabled = true;
            btnStop.Enabled = false;
        }

        private void btnDownload_Click(object sender, EventArgs e)
        {
            try
            {
                // Create a bitmap of the chart and statistics area
                int exportHeight = chart.Height + _statsLabel.Height + 10; // Chart + stats + margin
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
                            using (var statsFont = new Font(_statsLabel.Font.FontFamily, _statsLabel.Font.Size))
                            using (var brush = new SolidBrush(_statsLabel.ForeColor))
                            {
                                var statsRect = new Rectangle(0, chart.Height + 5, chart.Width, _statsLabel.Height);
                                graphics.FillRectangle(new SolidBrush(_statsLabel.BackColor), statsRect);
                                graphics.DrawRectangle(Pens.Black, statsRect);
                                graphics.DrawString(_statsLabel.Text, statsFont, brush, statsRect, new StringFormat
                                {
                                    Alignment = StringAlignment.Near,
                                    LineAlignment = StringAlignment.Near
                                });
                            }
                        }
                    }
                    
                    // Save file dialog
                    using (var saveDialog = new SaveFileDialog())
                    {
                        saveDialog.Filter = "PNG Images (*.png)|*.png|JPEG Images (*.jpg)|*.jpg|Bitmap Images (*.bmp)|*.bmp";
                        saveDialog.DefaultExt = "png";
                        saveDialog.FileName = $"PingGraph_{DateTime.Now:yyyyMMdd_HHmmss}";
                        
                        if (saveDialog.ShowDialog() == DialogResult.OK)
                        {
                            System.Drawing.Imaging.ImageFormat format;
                            switch (saveDialog.FilterIndex)
                            {
                                case 2:
                                    format = System.Drawing.Imaging.ImageFormat.Jpeg;
                                    break;
                                case 3:
                                    format = System.Drawing.Imaging.ImageFormat.Bmp;
                                    break;
                                default:
                                    format = System.Drawing.Imaging.ImageFormat.Png;
                                    break;
                            }
                            
                            bitmap.Save(saveDialog.FileName, format);
                            MessageBox.Show($"Chart exported successfully to {saveDialog.FileName}", "Export Complete", 
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error exporting chart: {ex.Message}", "Export Error", 
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Cursor tracking event handlers
        private void Chart_MouseMove(object sender, MouseEventArgs e)
        {
            if (chart.Series.Count == 0) return;

            try
            {
                var chartArea = chart.ChartAreas[0];
                var xValue = chartArea.AxisX.PixelPositionToValue(e.X);
                
                _isMouseOverChart = true;
                _mouseXValue = xValue;
                _cursorDataPoints.Clear();

                // Find data points at the cursor position for each series
                foreach (var series in chart.Series)
                {
                    var closestPoint = FindClosestDataPoint(series, xValue);
                    if (closestPoint != null)
                    {
                        var yPixel = chartArea.AxisY.ValueToPixelPosition(closestPoint.YValues[0]);
                        
                        // Calculate accurate timestamp based on sample number and start time
                        var sampleNumber = (int)closestPoint.XValue;
                        var timestamp = _startTime.AddSeconds(sampleNumber);
                        
                        _cursorDataPoints.Add(new CursorDataPoint
                        {
                            SeriesName = series.Name,
                            XValue = closestPoint.XValue,
                            YValue = closestPoint.YValues[0],
                            YPixelPosition = yPixel,
                            Color = series.Color,
                            Timestamp = timestamp,
                            IsTimeout = closestPoint.MarkerStyle == MarkerStyle.Cross
                        });
                    }
                }

                chart.Invalidate(); // Trigger repaint to show cursor tracking
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
            if (!_isMouseOverChart || _cursorDataPoints.Count == 0) return;

            try
            {
                var chartArea = chart.ChartAreas[0];
                var xPixel = chartArea.AxisX.ValueToPixelPosition(_mouseXValue);
                
                // Draw vertical line at cursor position
                using (var pen = new Pen(Color.Gray, 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
                {
                    e.ChartGraphics.Graphics.DrawLine(pen, 
                        (float)xPixel, (float)(chartArea.Position.Y * e.ChartGraphics.Graphics.VisibleClipBounds.Height / 100), 
                        (float)xPixel, (float)((chartArea.Position.Y + chartArea.Position.Height) * e.ChartGraphics.Graphics.VisibleClipBounds.Height / 100));
                }

                // Draw dots on each line at cursor position
                foreach (var dataPoint in _cursorDataPoints)
                {
                    using (var brush = new SolidBrush(dataPoint.Color))
                    {
                        var dotSize = dataPoint.IsTimeout ? 8 : 6;
                        e.ChartGraphics.Graphics.FillEllipse(brush, 
                            (float)xPixel - dotSize/2, 
                            (float)dataPoint.YPixelPosition - dotSize/2, 
                            dotSize, dotSize);
                        
                        // Draw white border around dot for visibility
                        using (var borderPen = new Pen(Color.White, 1))
                        {
                            e.ChartGraphics.Graphics.DrawEllipse(borderPen,
                                (float)xPixel - dotSize/2,
                                (float)dataPoint.YPixelPosition - dotSize/2,
                                dotSize, dotSize);
                        }
                    }
                }

                // Draw tooltip with timestamp and values
                DrawCursorTooltip(e.ChartGraphics.Graphics, xPixel, _cursorDataPoints);
            }
            catch
            {
                // Ignore errors during custom painting
            }
        }

        private DataPoint FindClosestDataPoint(Series series, double xValue)
        {
            if (series.Points.Count == 0) return null;

            DataPoint closestPoint = null;
            double minDistance = double.MaxValue;

            foreach (DataPoint point in series.Points)
            {
                var distance = Math.Abs(point.XValue - xValue);
                if (distance < minDistance)
                {
                    minDistance = distance;
                    closestPoint = point;
                }
            }

            return closestPoint;
        }

        private void DrawCursorTooltip(Graphics graphics, double xPixel, List<CursorDataPoint> dataPoints)
        {
            if (dataPoints.Count == 0) return;

            var tooltipText = new System.Text.StringBuilder();
            tooltipText.AppendLine($"Time: {dataPoints[0].Timestamp:HH:mm:ss}");
            tooltipText.AppendLine($"Sample: {dataPoints[0].XValue:F0}");
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
            using (var font = new Font("Arial", 8))
            using (var brush = new SolidBrush(Color.Black))
            using (var backgroundBrush = new SolidBrush(Color.FromArgb(240, 255, 255, 200)))
            using (var borderPen = new Pen(Color.Gray, 1))
            {
                var textSize = graphics.MeasureString(text, font);
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
                var tooltipRect = new RectangleF((float)tooltipX, (float)tooltipY, tooltipWidth, tooltipHeight);
                
                graphics.FillRectangle(backgroundBrush, tooltipRect);
                graphics.DrawRectangle(borderPen, Rectangle.Round(tooltipRect));
                
                var textX = (float)tooltipX + padding;
                var textY = (float)tooltipY + padding;
                graphics.DrawString(text, font, brush, textX, textY);
            }
        }
    }

    public class PingTarget
    {
        public string Name { get; }
        public string IP { get; }
        public Color Color { get; }
        public int PacketsSent { get; private set; }
        public int PacketsLost { get; private set; }
        public double PacketLossPercentage => PacketsSent > 0 ? (PacketsLost / (double)PacketsSent) * 100 : 0;
        public List<long> SuccessfulPings { get; } = new List<long>();
        public long MinPing => SuccessfulPings.Any() ? SuccessfulPings.Min() : 0;
        public long MaxPing => SuccessfulPings.Any() ? SuccessfulPings.Max() : 0;
        public double AvgPing => SuccessfulPings.Any() ? SuccessfulPings.Average() : 0;

        // Use a buffer like CLI ping (32 bytes)
        private readonly byte[] _buffer = new byte[32];

        public PingTarget(string name, string ip, Color color)
        {
            Name = name;
            IP = ip;
            Color = color;
            
            // Initialize buffer with pattern like CLI ping
            for (int i = 0; i < _buffer.Length; i++)
            {
                _buffer[i] = (byte)(97 + (i % 23)); // Pattern: abcdefghijklmnopqrstuvw...
            }
        }

        public void ResetStatistics()
        {
            PacketsSent = 0;
            PacketsLost = 0;
            SuccessfulPings.Clear();
        }

        public async Task<long> PingAsync()
        {
            // Create a new Ping instance for each ping to avoid concurrency issues
            using (var ping = new Ping())
            {
                try
                {
                    PacketsSent++;
                    
                    // Use longer timeout (5000ms) and proper buffer like CLI ping
                    // Set TTL to 128 (Windows default) and don't fragment
                    var options = new PingOptions
                    {
                        Ttl = 128,
                        DontFragment = false
                    };
                    
                    var reply = await ping.SendPingAsync(IP, 5000, _buffer, options);
                    
                    if (reply.Status == IPStatus.Success)
                    {
                        SuccessfulPings.Add(reply.RoundtripTime);
                        return reply.RoundtripTime;
                    }
                    else
                    {
                        PacketsLost++;
                        // Log the specific failure reason for debugging
                        System.Diagnostics.Debug.WriteLine($"Ping failed to {IP}: {reply.Status}");
                        return -1;
                    }
                }
                catch (Exception ex)
                {
                    PacketsLost++;
                    // Log the exception for debugging
                    System.Diagnostics.Debug.WriteLine($"Ping exception to {IP}: {ex.Message}");
                    return -1;
                }
            }
        }
    }

    // Helper class for cursor tracking data
    public class CursorDataPoint
    {
        public string SeriesName { get; set; }
        public double XValue { get; set; }
        public double YValue { get; set; }
        public double YPixelPosition { get; set; }
        public Color Color { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsTimeout { get; set; }
    }
}
