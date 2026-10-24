using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ShokoMpcWindows
{
    public sealed class Settings { public string ServerUrl { get; set; } public string PlayerPath { get; set; } }
    public sealed class Resolved { public string[] paths { get; set; } public string[] namedPaths { get; set; } public string completionPath { get; set; } }
    public static class Protocol
    {
        public static Uri NormalizeServer(string value)
        {
            Uri uri;
            if (String.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.TrimEnd('/'), UriKind.Absolute, out uri)
                || (uri.Scheme != "http" && uri.Scheme != "https") || !String.IsNullOrEmpty(uri.UserInfo)
                || !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment)
                || value.IndexOf('\\') >= 0 || Regex.IsMatch(uri.AbsolutePath, @"%2[fF]|%5[cC]|%2[eE]"))
                throw new ArgumentException("Enter a valid http:// or https:// Shoko server address without a query or fragment.");
            return new Uri(uri.AbsoluteUri.TrimEnd('/'));
        }
        public static string ParseTicket(string link, string trustedServer)
        {
            if (link == null || link.Length > 4096 || link.Any(c => Char.IsControl(c) || c == '"' || c == '\\'))
                throw new ArgumentException("Invalid playback link.");
            Uri uri;
            if (!Uri.TryCreate(link, UriKind.Absolute, out uri) || uri.Scheme != "shoko-mpc" || uri.Host != "play"
                || uri.AbsolutePath != "/" || uri.UserInfo != "" || !uri.IsDefaultPort || uri.Fragment != "")
                throw new ArgumentException("Unsupported playback link.");
            var query = HttpUtility.ParseQueryString(uri.Query);
            if (query.Count != 2 || query.GetValues("server") == null || query.GetValues("server").Length != 1
                || query.GetValues("ticket") == null || query.GetValues("ticket").Length != 1)
                throw new ArgumentException("Invalid playback link parameters.");
            var server = NormalizeServer(query["server"]);
            var trusted = NormalizeServer(trustedServer);
            if (server.Scheme != trusted.Scheme || server.Host != trusted.Host || server.Port != trusted.Port
                || server.AbsolutePath.TrimEnd('/') != trusted.AbsolutePath.TrimEnd('/'))
                throw new ArgumentException("This link is from a different Shoko server. Run Windows Setup to select the server you trust.");
            var ticket = query["ticket"];
            if (!Regex.IsMatch(ticket ?? "", "\\A[a-f0-9]{64}\\z")) throw new ArgumentException("Invalid playback ticket.");
            return ticket;
        }
        public static string MediaUrl(string server, string path)
        {
            var match = Regex.Match(path ?? "", "\\A/api/plugin/ShokoMpc/stream/[a-f0-9]{64}(?:/([A-Za-z0-9._~-]|%[0-9A-Fa-f]{2})+)?\\z");
            if (!match.Success || path.Length > 8192)
                throw new ArgumentException("The server returned an invalid video path.");
            var name = Uri.UnescapeDataString(path.Substring(path.LastIndexOf('/') + 1));
            if (name == "." || name == ".." || name.Any(c => Char.IsControl(c) || "<>:\"/\\|?*%".IndexOf(c) >= 0))
                throw new ArgumentException("The server returned an invalid video filename.");
            return NormalizeServer(server).AbsoluteUri.TrimEnd('/') + path;
        }
        public static string Quote(string value)
        {
            // These arguments are validated HTTP URLs, never shell expressions or file paths.
            if (value.Any(c => Char.IsControl(c) || c == '"' || c == '\\')) throw new ArgumentException("Invalid player argument.");
            return "\"" + value + "\"";
        }
        public static string CompletionUrl(string server, string path)
        {
            if (!Regex.IsMatch(path ?? "", "\\A/api/plugin/ShokoMpc/complete/[a-f0-9]{64}\\z"))
                throw new ArgumentException("The server returned an invalid watched-sync path.");
            return NormalizeServer(server).AbsoluteUri.TrimEnd('/') + path;
        }
    }
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length != 1) throw new ArgumentException("Open an episode using its MPC-HC button in Shoko.");
                var configFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
                var serializer = new JavaScriptSerializer { MaxJsonLength = 32768 };
                var settings = serializer.Deserialize<Settings>(File.ReadAllText(configFile));
                var player = settings.PlayerPath;
                if (!Path.IsPathRooted(player) || !File.Exists(player) ||
                    !new[] { "mpc-hc.exe", "mpc-hc64.exe" }.Contains(Path.GetFileName(player), StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException("MPC-HC was not found. Run Windows Setup again and select mpc-hc.exe or mpc-hc64.exe.");
                var ticket = Protocol.ParseTicket(args[0], settings.ServerUrl);
                var server = Protocol.NormalizeServer(settings.ServerUrl).AbsoluteUri.TrimEnd('/');
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                using (var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
                using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 32768 })
                {
                    var response = client.PostAsync(server + "/api/plugin/ShokoMpc/resolve/" + ticket,
                        new StringContent("")).GetAwaiter().GetResult();
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException(response.StatusCode == HttpStatusCode.Gone
                            ? "The play link expired or was already used. Click Play again in Shoko."
                            : "Shoko could not open the video (HTTP " + (int)response.StatusCode + "). Check that the plugin is installed and the server is reachable.");
                    var resolved = serializer.Deserialize<Resolved>(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                    var paths = resolved.namedPaths ?? resolved.paths;
                    if (paths == null || paths.Length < 1 || paths.Length > 16)
                        throw new InvalidOperationException("The server returned no playable video.");
                    var urls = paths.Select(p => Protocol.MediaUrl(server, p)).ToArray();
                    if (!String.IsNullOrEmpty(resolved.completionPath))
                    {
                        using (var monitor = new PlaybackMonitor(player, urls, Protocol.CompletionUrl(server, resolved.completionPath), client))
                            Application.Run(monitor);
                    }
                    else
                    {
                        var arguments = String.Join(" ", urls.Select(Protocol.Quote)) + " /play";
                        Process.Start(new ProcessStartInfo(player, arguments) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(player) });
                    }
                }
                return 0;
            }
            catch (Exception e)
            {
                MessageBox.Show(e is HttpRequestException || e is System.Threading.Tasks.TaskCanceledException
                    ? "Cannot reach your Shoko server. Check its address and your network connection, then click Play again."
                    : e.Message, "Shoko MPC-HC", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
        }
    }
}
