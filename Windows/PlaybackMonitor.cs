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
            if (!TryPosition(payload, out position)) return -1;
            return position >= Duration * 0.9 ? Current : -1;
        }
        public bool TryPosition(string payload, out double position)
        {
            position = 0;
            return Current >= 0 && Double.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out position)
                && !Double.IsNaN(position) && !Double.IsInfinity(position) && position >= 0 && position <= Duration + 2;
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
        const uint Connect = 0x50000000, State = 0x50000001, PlayMode = 0x50000002, NowPlaying = 0x50000003,
            Position = 0x50000007, Disconnect = 0x5000000B, GetNowPlaying = 0xA0003002, GetPosition = 0xA0003004, SetPosition = 0xA0002000;
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
        readonly string progressUrl;
        readonly double[] resume, positions, durations;
        readonly long[] revisions;
        readonly bool[] resumed;
        readonly int[] seekAttempts;
        readonly DateTime[] seekAt, savedAt;
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
        bool launched, disconnected, sending, exiting, warned, stopped, paused;
        uint playerProcess;

        public PlaybackMonitor(string player, string[] urls, string completion, HttpClient client, string progressUrl = null, double[] resume = null)
        {
            this.player = player; this.urls = urls; this.completion = completion; this.client = client;
            this.progressUrl = progressUrl; this.resume = resume ?? new double[urls.Length];
            positions = new double[urls.Length]; durations = new double[urls.Length]; revisions = new long[urls.Length];
            resumed = new bool[urls.Length]; seekAttempts = new int[urls.Length]; seekAt = new DateTime[urls.Length]; savedAt = new DateTime[urls.Length];
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
            else if (command == PlayMode) { stopped = text == "2"; paused = text == "1"; }
            else if (command == NowPlaying) { progress.NowPlaying(text); lastMetadata = DateTime.UtcNow; }
            else if (command == Position && DateTime.UtcNow - lastMetadata < TimeSpan.FromSeconds(3))
            {
                var part = progress.Current;
                double seconds;
                if (!stopped && progress.TryPosition(text, out seconds) && !saved[part])
                {
                    if (progressUrl == null) { if (seconds >= progress.Duration * 0.9) pending[part] = true; }
                    else
                    {
                        // Keep a reached completion threshold queued even if the user seeks backwards during a retry.
                        if (pending[part] && revisions[part] > 0 && positions[part] >= durations[part] * 0.9) return;
                        // Do not overwrite the stored bookmark with the player's initial zero before seeking.
                        if (!resumed[part])
                        {
                            var target = ResumeTarget(part);
                            if (target > 0 && (seekAttempts[part] == 0 || Math.Abs(seconds - target) > 5)) return;
                            resumed[part] = true;
                        }
                        if (revisions[part] == 0 || Math.Abs(positions[part] - seconds) >= 0.25)
                        {
                            positions[part] = seconds; durations[part] = progress.Duration;
                            revisions[part]++; pending[part] = true;
                        }
                    }
                }
            }
            else if (command == Disconnect) { disconnected = true; progress.Clear(); }
        }
        double ResumeTarget(int part) { return resume[part] < progress.Duration * 0.9 ? resume[part] : 0; }
        void Ask(uint command, string text = "")
        {
            var payload = Marshal.StringToHGlobalUni(text);
            try
            {
                var data = new CopyData { Command = IntPtr.Size == 8 ? new IntPtr((long)command) : new IntPtr(unchecked((int)command)), Bytes = (text.Length + 1) * 2, Data = payload };
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
                else
                {
                    Ask(GetNowPlaying);
                    var current = progress.Current;
                    if (progressUrl != null && current >= 0 && !resumed[current] && ResumeTarget(current) > 0 && DateTime.UtcNow >= seekAt[current])
                    {
                        if (seekAttempts[current] >= 3) { Fail("MPC-HC could not restore the saved position. Your Shoko bookmark has been kept. Reopen the episode to try again."); return; }
                        seekAttempts[current]++; seekAt[current] = DateTime.UtcNow.AddSeconds(3);
                        Ask(SetPosition, ResumeTarget(current).ToString("0.###", CultureInfo.InvariantCulture));
                    }
                    Ask(GetPosition);
                }
            }
            if (DateTime.UtcNow - lastReply > TimeSpan.FromSeconds(60)) disconnected = true;
            var part = Enumerable.Range(0, pending.Length).Where(i => pending[i] && DateTime.UtcNow >= retryAt[i]
                && (progressUrl == null || disconnected || stopped || paused || positions[i] >= durations[i] * 0.9 || DateTime.UtcNow - savedAt[i] >= TimeSpan.FromSeconds(5))).DefaultIfEmpty(-1).First();
            if (part >= 0 && !sending && DateTime.UtcNow >= retryAt[part])
            {
                sending = true;
                var revision = revisions[part];
                var finished = progressUrl == null || positions[part] >= durations[part] * 0.9;
                try
                {
                    var body = progressUrl == null ? "" : new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new
                        { sequence = revision, seconds = positions[part], duration = durations[part] });
                    using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
                    using (var response = await client.PostAsync((progressUrl ?? completion) + "/" + part, content))
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            saved[part] = finished; pending[part] = !finished && revisions[part] != revision;
                            savedAt[part] = DateTime.UtcNow; failures[part] = 0;
                        }
                        else if (response.StatusCode == System.Net.HttpStatusCode.Conflict) { ExitThread(); }
                        else if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500) { Fail("Shoko could not save playback progress (HTTP " + (int)response.StatusCode + "). Reopen this episode from Shoko to reconnect syncing."); }
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
            if (failures[part] >= 6) { Fail("Could not reach Shoko to save playback progress after several attempts. Your latest position may not be saved. Reopen this episode from Shoko to reconnect syncing."); return; }
            retryAt[part] = DateTime.UtcNow.AddSeconds(Math.Min(30, Math.Pow(2, failures[part])));
        }
        void Fail(string message)
        {
            if (!warned) { warned = true; MessageBox.Show(message, "Shoko playback sync", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            ExitThread();
        }
        protected override void ExitThreadCore()
        {
            exiting = true; timer.Stop(); timer.Dispose(); receiver.DestroyHandle(); base.ExitThreadCore();
        }
    }
}
