using Scanse;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SweepTest
{
    public partial class SweepView : Form
    {
        private bool _ContinuePolling;

        private object _PacketBaton = new object();
        private List<ScanPacket> _LastPackets;

        public SweepView()
        {
            InitializeComponent();
        }

        private void deviceToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!_ContinuePolling)
            {
                string[] commPorts = SerialPort.GetPortNames();
                connectToolStripMenuItem.DropDownItems.Clear();
                if (commPorts.Length > 0)
                {
                    connectToolStripMenuItem.Enabled = true;
                    foreach (string commPort in commPorts)
                    {
                        var menuItem = new ToolStripMenuItem(commPort, null, connectToDevice_Click);
                        connectToolStripMenuItem.DropDownItems.Add(menuItem);
                    }
                }
                else
                {
                    connectToolStripMenuItem.Enabled = false;
                }
            }
        }

        private void connectToDevice_Click(object sender, EventArgs e)
        {
            connectToolStripMenuItem.Enabled = false;
            disconnectToolStripMenuItem.Enabled = true;
            string commPort = (sender as ToolStripMenuItem).Text;
            _ContinuePolling = true;
            var t = new Thread(() => PollSweep(commPort));
            t.Start();
        }

        private void disconnectToolStripMenuItem_Click(object sender, EventArgs e)
        {
            disconnectToolStripMenuItem.Enabled = false;
            _ContinuePolling = false;
        }

        private void PollSweep(string commPort)
        {
            bool success;
            Sweep sweep = null;

            while (_ContinuePolling)
            {
                UpdateStatus("Connecting to device...");
                sweep = new Sweep(commPort);

                UpdateStatus("Connected to device; resetting...");
                sweep.Reset();

                UpdateStatus("Device reset; waiting for response...");
                DateTime stime = DateTime.UtcNow;
                while (sweep.GetSampleRate() <= 0)
                {
                    if ((DateTime.UtcNow - stime).TotalSeconds > 15)
                    {
                        UpdateStatus("Timeout while waiting for response after resetting device");
                        _ContinuePolling = false;
                        break;
                    }
                }
                if (!_ContinuePolling)
                    break;

                UpdateStatus("Received response; setting motor speed...");
                success = sweep.SetMotorSpeed(MotorSpeed.Speed5Hz);
                if (!success)
                {
                    UpdateStatus("Failed to set motor speed");
                    break;
                }

                UpdateStatus("Motor speed set; verifying motor speed...");
                int speed = sweep.GetMotorSpeed();
                if (speed < 0)
                {
                    UpdateStatus("Could not verify motor speed");
                    break;
                }

                UpdateStatus("Motor speed " + speed + " Hz; verifying sample rate...");
                int sampleRate = sweep.GetSampleRate();
                if (sampleRate < 0)
                {
                    UpdateStatus("Could not verify sample rate");
                    break;
                }

                UpdateStatus("Sampling rate " + sampleRate + " Hz; starting scan...");
                success = sweep.StartScanning();
                if (!success)
                {
                    UpdateStatus("Could not start scan");
                    break;
                }

                UpdateStatus("Reading scan data");
                var packets = new List<ScanPacket>();
                while (_ContinuePolling)
                {
                    ScanPacket packet = sweep.GetReading();
                    if (packet == null)
                    {
                        UpdateStatus("Could not get reading");
                        _ContinuePolling = false;
                        break;
                    }

                    if (packet.IsSync())
                    {
                        if (packets.Count > 0)
                            DisplayPackets(packets);
                        packets.Clear();
                    }
                    packets.Add(packet);
                }
            }

            UpdateStatus("Closing device (" + tsslStatus.Text + ")", () => disconnectToolStripMenuItem.Enabled = false);
            sweep.Dispose();

            if (!this.IsDisposed)
                this.Invoke(new Action(() =>
                {
                    tsslStatus.Text = tsslStatus.Text.Substring(tsslStatus.Text.IndexOf('(') + 1);
                    tsslStatus.Text = "Device closed (" + tsslStatus.Text;
                    connectToolStripMenuItem.Enabled = true;
                }));
        }

        private void UpdateStatus(string status, Action additionalAction = null)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => UpdateStatus(status, additionalAction)));
            }
            else
            {
                System.Diagnostics.Debug.Print(status);
                tsslStatus.Text = status;
                additionalAction?.Invoke();
            }
        }

        private void DisplayPackets(List<ScanPacket> packets)
        {
            lock (_PacketBaton)
                _LastPackets = packets.ToList();
            pbPreview.Invalidate();
        }

        private void pbPreview_Paint(object sender, PaintEventArgs e)
        {
            e.Graphics.Clear(pbPreview.BackColor);

            Point center = new Point(pbPreview.ClientSize.Width / 2, pbPreview.ClientSize.Height / 2);

            const int CENTER_RADIUS = 3;
            e.Graphics.DrawEllipse(Pens.Black, center.X - CENTER_RADIUS, center.Y - CENTER_RADIUS, 2 * CENTER_RADIUS + 1, 2 * CENTER_RADIUS + 1);

            if (_LastPackets != null)
            {
                const int PACKET_RADIUS = 5;
                lock (_PacketBaton)
                {
                    float r = Math.Min(pbPreview.ClientSize.Width, pbPreview.ClientSize.Height) / 2;
                    float maxDist = _LastPackets.Max(p => p.GetDistanceCentimeters());
                    foreach (ScanPacket p in _LastPackets)
                    {
                        double theta = p.GetAngleDegrees() * Math.PI / 180;
                        float dx = (float)(p.GetDistanceCentimeters() * r * Math.Sin(theta) / maxDist);
                        float dy = (float)(-p.GetDistanceCentimeters() * r * Math.Cos(theta) / maxDist);
                        using (var pen = new Pen(Color.FromArgb(p.GetSignalStrength(), 0, 0), 2))
                            e.Graphics.DrawEllipse(pen, center.X + dx - PACKET_RADIUS, center.Y + dy - PACKET_RADIUS, 2 * PACKET_RADIUS + 1, 2 * PACKET_RADIUS + 1);
                    }
                }
            }
        }

        private void SweepView_FormClosing(object sender, FormClosingEventArgs e)
        {
            _ContinuePolling = false;
        }
    }
}
