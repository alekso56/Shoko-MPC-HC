using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ShokoMpcWindows
{
    // Only URLs from this launch can reach the 90% threshold. Unknown media clears the match.
    public sealed class PlaybackProgress
    {
        readonly Uri[] media;
        public int Current { get; private set; }
        public double Duration { get; private set; }
        public PlaybackProgress(string[] urls) { media = urls.Select(x => new Uri(x)).ToArray(); Clear(); }
        public void Clear() { Current = -1; Duration = 0; }
        public void NowPlaying(string payload)
        {
            Clear();
            var fields = SplitFields(payload);
            Uri uri; double duration;
            if (fields.Length != 5 || !Uri.TryCreate(fields[3], UriKind.Absolute, out uri)
                || !Double.TryParse(fields[4], NumberStyles.Float, CultureInfo.InvariantCulture, out duration)
                || Double.IsNaN(duration) || Double.IsInfinity(duration) || duration <= 0) return;
            Current = Array.FindIndex(media, x => x.Equals(uri));
            if (Current >= 0) Duration = duration;
        }
        public int Position(string payload)
        {
            double position;
            if (Current < 0 || !Double.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out position)
                || Double.IsNaN(position) || Double.IsInfinity(position) || position < 0 || position > Duration + 2) return -1;
            return position >= Duration * 0.9 ? Current : -1;
        }
        public static string[] SplitFields(string value)
        {
            var fields = new List<string>(); var current = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] == '\\' && i + 1 < value.Length && value[i + 1] == '|') { current.Append('|'); i++; }
                else if (value[i] == '|') { fields.Add(current.ToString()); current.Clear(); }
                else current.Append(value[i]);
            }
            fields.Add(current.ToString()); return fields.ToArray();
        }
    }

    public sealed class PlaybackMonitor : ApplicationContext
    {
        const uint Connect = 0x50000000, State = 0x50000001, NowPlaying = 0x50000003,
            Position = 0x50000007, Disconnect = 0x5000000B, GetNowPlaying = 0xA0003002, GetPosition = 0xA0003004;
        const int CopyDataMessage = 0x4A;
        [StructLayout(LayoutKind.Sequential)] struct CopyData { public IntPtr Command; public int Bytes; public IntPtr Data; }
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr sender, ref CopyData data, uint flags, uint timeout, out IntPtr result);

        sealed class Receiver : NativeWindow
        {
            readonly PlaybackMonitor owner;
            public Receiver(PlaybackMonitor owner) { this.owner = owner; CreateHandle(new CreateParams { Caption = "Shoko MPC-HC watched sync", Parent = new IntPtr(-3) }); }
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == CopyDataMessage) { owner.Receive(message.WParam, message.LParam); message.Result = new IntPtr(1); return; }
                base.WndProc(ref message);
            }
        }
        readonly string player;
        readonly string[] urls;
        readonly string completion;
        readonly HttpClient client;
        readonly Receiver receiver;
        readonly Timer timer = new Timer { Interval = 1000 };
        readonly PlaybackProgress progress;
        readonly bool[] pending, saved;
        readonly int[] failures;
        readonly DateTime[] retryAt;
        readonly DateTime started = DateTime.UtcNow;
        DateTime lastReply = DateTime.UtcNow, lastMetadata = DateTime.MinValue;
        IntPtr playerWindow;
        bool launched, disconnected, sending, exiting, warned;
        uint playerProcess;

        public PlaybackMonitor(string player, string[] urls, string completion, HttpClient client)
        {
            this.player = player; this.urls = urls; this.completion = completion; this.client = client;
            progress = new PlaybackProgress(urls);
            pending = new bool[urls.Length]; saved = new bool[urls.Length]; failures = new int[urls.Length]; retryAt = new DateTime[urls.Length];
            receiver = new Receiver(this);
            timer.Tick += Tick; timer.Start();
        }
        void Receive(IntPtr sender, IntPtr pointer)
        {
            if (exiting || sender == IntPtr.Zero) return;
            uint pid; GetWindowThreadProcessId(sender, out pid);
            if (playerWindow == IntPtr.Zero)
            {
                try { using (var process = Process.GetProcessById((int)pid)) { if (!String.Equals(process.MainModule.FileName, player, StringComparison.OrdinalIgnoreCase)) return; } }
                catch { return; }
            }
            else if (sender != playerWindow || pid != playerProcess) return;
            var data = (CopyData)Marshal.PtrToStructure(pointer, typeof(CopyData));
            if (data.Bytes < 2 || data.Bytes > 65536 || data.Bytes % 2 != 0 || data.Data == IntPtr.Zero) return;
            var text = Marshal.PtrToStringUni(data.Data, data.Bytes / 2);
            if (text[text.Length - 1] != '\0') return;
            text = text.Substring(0, text.Length - 1);
            var command = unchecked((uint)data.Command.ToInt64());
            if (playerWindow == IntPtr.Zero && command != Connect) return;
            lastReply = DateTime.UtcNow;
            if (command == Connect) { playerWindow = sender; playerProcess = pid; }
            else if (command == State && text != "2") { progress.Clear(); lastMetadata = DateTime.MinValue; }
            else if (command == NowPlaying) { progress.NowPlaying(text); lastMetadata = DateTime.UtcNow; }
            else if (command == Position && DateTime.UtcNow - lastMetadata < TimeSpan.FromSeconds(3))
            {
                var part = progress.Position(text);
                if (part >= 0 && !saved[part]) pending[part] = true;
            }
            else if (command == Disconnect) { disconnected = true; progress.Clear(); }
        }
        void Ask(uint command)
        {
            var payload = Marshal.StringToHGlobalUni("");
            try
            {
                var data = new CopyData { Command = IntPtr.Size == 8 ? new IntPtr((long)command) : new IntPtr(unchecked((int)command)), Bytes = 2, Data = payload };
                IntPtr result;
                SendMessageTimeout(playerWindow, CopyDataMessage, receiver.Handle, ref data, 2, 500, out result);
            }
            finally { Marshal.FreeHGlobal(payload); }
        }
        async void Tick(object sender, EventArgs args)
        {
            if (exiting) return;
            if (!launched)
            {
                launched = true;
                try
                {
                    var arguments = String.Join(" ", urls.Select(Protocol.Quote)) + " /play /slave " + receiver.Handle.ToInt64().ToString(CultureInfo.InvariantCulture);
                    using (Process.Start(new ProcessStartInfo(player, arguments) { UseShellExecute = false, WorkingDirectory = System.IO.Path.GetDirectoryName(player) })) { }
                }
                catch (Exception error) { Fail("Could not start MPC-HC: " + error.Message); return; }
            }
            if (playerWindow != IntPtr.Zero && !disconnected)
            {
                if (!IsWindow(playerWindow)) disconnected = true;
                else { Ask(GetNowPlaying); Ask(GetPosition); }
            }
            if (DateTime.UtcNow - lastReply > TimeSpan.FromSeconds(60)) disconnected = true;
            var part = Array.FindIndex(pending, x => x);
            if (part >= 0 && !sending && DateTime.UtcNow >= retryAt[part])
            {
                sending = true;
                try
                {
                    using (var response = await client.PostAsync(completion + "/" + part, new StringContent("")))
                    {
                        if (response.IsSuccessStatusCode) { pending[part] = false; saved[part] = true; }
                        else if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500) { Fail("Shoko could not save watched status (HTTP " + (int)response.StatusCode + "). Mark this episode watched manually in Shoko."); }
                        else Retry(part);
                    }
                }
                catch (HttpRequestException) { Retry(part); }
                catch (TaskCanceledException) { Retry(part); }
                finally { sending = false; }
            }
            if (!exiting && ((saved.All(x => x)) || (disconnected && !pending.Any(x => x)) || DateTime.UtcNow - started > TimeSpan.FromHours(12)))
                ExitThread();
        }
        void Retry(int part)
        {
            failures[part]++;
            if (failures[part] >= 6) { Fail("Could not reach Shoko to save watched status after several attempts. Mark this episode watched manually in Shoko."); return; }
            retryAt[part] = DateTime.UtcNow.AddSeconds(Math.Min(30, Math.Pow(2, failures[part])));
        }
        void Fail(string message)
        {
            if (!warned) { warned = true; MessageBox.Show(message, "Shoko watched sync", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            ExitThread();
        }
        protected override void ExitThreadCore()
        {
            exiting = true; timer.Stop(); timer.Dispose(); receiver.DestroyHandle(); base.ExitThreadCore();
        }
    }
}
