namespace PingGrapher
{
    partial class MainForm
    {
        private System.ComponentModel.IContainer components = null;
        private System.Windows.Forms.Button btnStart;
        private System.Windows.Forms.Button btnStop;
        private System.Windows.Forms.Button btnDownload;
        private System.Windows.Forms.TextBox txtCustomIP;
        private System.Windows.Forms.Label lblCustomIP;
        private System.Windows.Forms.DataVisualization.Charting.Chart chart;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
                components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.btnStart = new System.Windows.Forms.Button();
            this.btnStop = new System.Windows.Forms.Button();
            this.btnDownload = new System.Windows.Forms.Button();
            this.txtCustomIP = new System.Windows.Forms.TextBox();
            this.lblCustomIP = new System.Windows.Forms.Label();
            this.chart = new System.Windows.Forms.DataVisualization.Charting.Chart();

            ((System.ComponentModel.ISupportInitialize)(this.chart)).BeginInit();
            this.SuspendLayout();

            // lblCustomIP
            this.lblCustomIP.AutoSize = true;
            this.lblCustomIP.Location = new System.Drawing.Point(12, 17);
            this.lblCustomIP.Name = "lblCustomIP";
            this.lblCustomIP.Text = "IP,Hostname:";
            this.lblCustomIP.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            this.lblCustomIP.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left;

            // txtCustomIP
            this.txtCustomIP.Location = new System.Drawing.Point(95, 14);
            this.txtCustomIP.Name = "txtCustomIP";
            this.txtCustomIP.Size = new System.Drawing.Size(405, 20);
            this.txtCustomIP.Text = "google.com, youtube.com";
            this.txtCustomIP.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;

            // btnStart
            this.btnStart.Location = new System.Drawing.Point(510, 12);
            this.btnStart.Size = new System.Drawing.Size(80, 26);
            this.btnStart.Name = "btnStart";
            this.btnStart.Text = "Start";
            this.btnStart.UseVisualStyleBackColor = true;
            this.btnStart.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
            this.btnStart.Click += new System.EventHandler(this.btnStart_Click);

            // btnStop
            this.btnStop.Location = new System.Drawing.Point(600, 12);
            this.btnStop.Size = new System.Drawing.Size(80, 26);
            this.btnStop.Name = "btnStop";
            this.btnStop.Text = "Stop";
            this.btnStop.Enabled = false;
            this.btnStop.UseVisualStyleBackColor = true;
            this.btnStop.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
            this.btnStop.Click += new System.EventHandler(this.btnStop_Click);

            // btnDownload
            this.btnDownload.Location = new System.Drawing.Point(690, 12);
            this.btnDownload.Size = new System.Drawing.Size(80, 26);
            this.btnDownload.Name = "btnDownload";
            this.btnDownload.Text = "Download";
            this.btnDownload.UseVisualStyleBackColor = true;
            this.btnDownload.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
            this.btnDownload.Click += new System.EventHandler(this.btnDownload_Click);
            
            // chart
            this.chart.Location = new System.Drawing.Point(12, 50);
            this.chart.Size = new System.Drawing.Size(758, 200);
            this.chart.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            this.chart.ChartAreas.Add(new System.Windows.Forms.DataVisualization.Charting.ChartArea());

            // MainForm
            this.ClientSize = new System.Drawing.Size(782, 412);
            this.MinimumSize = new System.Drawing.Size(600, 400);
            this.Controls.Add(this.btnStart);
            this.Controls.Add(this.btnStop);
            this.Controls.Add(this.btnDownload);
            this.Controls.Add(this.txtCustomIP);
            this.Controls.Add(this.lblCustomIP);
            this.Controls.Add(this.chart);
            this.Text = "Multi-Host Ping Grapher";

            ((System.ComponentModel.ISupportInitialize)(this.chart)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();
        }
    }
}
