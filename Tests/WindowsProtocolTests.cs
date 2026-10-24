using System;
using ShokoMpcWindows;
public class WindowsProtocolTests
{
    static int passed;
    static void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; }
    static void Reject(string link, string server) { try { Protocol.ParseTicket(link, server); } catch (ArgumentException) { passed++; return; } throw new Exception("Invalid link accepted"); }
    public static void Main()
    {
        var server = "http://localhost:30222";
        var ticket = new string('a', 64);
        var prefix = "shoko-mpc://play/?server=" + Uri.EscapeDataString(server);
        var link = prefix + "&ticket=" + ticket;
        Check(Protocol.ParseTicket(link, server) == ticket, "valid ticket");
        Reject(link, "http://other-server:30222"); Reject(link, "https://localhost:30222");
        Reject(link, "http://localhost:8111"); Reject(link, server + "/prefix");
        Reject(prefix + "&ticket=x", server); Reject(link + "&ticket=" + ticket, server);
        Reject(link + "&run=cmd.exe", server); Reject(link + "#fragment", server);
        Reject(link.Replace("play/", "play:123/"), server); Reject(link.Replace("play/", "play/other"), server);
        Reject(link.Replace("shoko-mpc:", "https:"), server); Reject(link + "\" /help", server);
        Check(Protocol.MediaUrl(server, "/api/plugin/ShokoMpc/stream/" + ticket) == server + "/api/plugin/ShokoMpc/stream/" + ticket, "same origin stream URL");
        var named = "/api/plugin/ShokoMpc/stream/" + ticket + "/" + Uri.EscapeDataString("[Group] Show - 01 日本語.mkv");
        Check(Protocol.MediaUrl(server, named) == server + named, "named Unicode stream URL");
        foreach (var suffix in new[] { "..", "%2E%2E", "%2Fother", "%5Cother", "%252Fother", "%22%20%2Fhelp", "x?command=1", "x#fragment", "%0Aheader", "%xx" })
        { try { Protocol.MediaUrl(server, "/api/plugin/ShokoMpc/stream/" + ticket + "/" + suffix); } catch (ArgumentException) { passed++; continue; } throw new Exception("Invalid filename accepted: " + suffix); }
        foreach (var path in new[] { "https://evil.test/", "/api/plugin/ShokoMpc/stream/../../x", "\\\\host\\file", "/api/plugin/ShokoMpc/stream/" + ticket + "?command=1" })
        { try { Protocol.MediaUrl(server, path); } catch (ArgumentException) { passed++; continue; } throw new Exception("Invalid media path accepted"); }
        Check(Protocol.Quote("http://localhost/a") == "\"http://localhost/a\"", "player URL quoted");
        var completion = "/api/plugin/ShokoMpc/complete/" + ticket;
        Check(Protocol.CompletionUrl(server, completion) == server + completion, "watched endpoint pinned to server");
        foreach (var path in new[] { "https://evil.test/", completion + "/0", completion + "?run=1", "/api/plugin/ShokoMpc/stream/" + ticket })
        { try { Protocol.CompletionUrl(server, path); } catch (ArgumentException) { passed++; continue; } throw new Exception("Invalid completion path accepted"); }
        var tracking = new PlaybackProgress(new[] { server + named, server + named + "2" });
        Check(tracking.Position("99") == -1, "no progress before media identification");
        tracking.NowPlaying("Title\\|part|author|description|" + server + named + "|100.0");
        Check(tracking.Current == 0 && tracking.Position("89.99") == -1, "escaped metadata and below threshold");
        Check(tracking.Position("90") == 0 && tracking.Position("100") == 0, "ninety percent threshold");
        Check(tracking.Position("NaN") == -1 && tracking.Position("Infinity") == -1 && tracking.Position("-1") == -1 && tracking.Position("9999") == -1, "invalid positions ignored");
        tracking.NowPlaying("t|a|d|https://other.test/movie|100");
        Check(tracking.Position("100") == -1, "unrelated media cannot complete episode");
        tracking.NowPlaying("t|a|d|" + server + named + "2|200");
        Check(tracking.Position("180") == 1, "multipart index follows current media");
        tracking.Clear(); Check(tracking.Position("190") == -1, "closing or switching media clears previous progress");
        foreach (var duration in new[] { "0", "-2", "NaN", "Infinity", "missing" })
        { tracking.NowPlaying("t|a|d|" + server + named + "|" + duration); Check(tracking.Position("100") == -1, "invalid duration ignored"); }
        Console.WriteLine(passed + " Windows protocol checks passed.");
    }
}
