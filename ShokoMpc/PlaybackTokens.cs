using System.Security.Cryptography;

namespace ShokoMpc;

public sealed record PlaybackGrant(int UserId, int[] FileIds, DateTimeOffset ExpiresAt);

// Opaque capabilities are kept in memory. No Shoko API key is sent to Windows or a media URL.
public sealed class PlaybackTokens
{
    private readonly Dictionary<string, PlaybackGrant> launches = new();
    private readonly Dictionary<string, PlaybackGrant> streams = new();
    private readonly object gate = new();
    private readonly TimeProvider clock;
    public PlaybackTokens() : this(TimeProvider.System) { }
    public PlaybackTokens(TimeProvider clock) => this.clock = clock;
    public static bool ValidToken(string? token) => token is { Length: 64 } && token.All(Uri.IsHexDigit);
    public string IssueLaunch(int userId, int[] fileIds)
    {
        if (fileIds.Length is < 1 or > 16 || fileIds.Any(x => x <= 0))
        {
            throw new ArgumentException("Choose between one and sixteen valid video files.");
        }

        lock (gate)
        {
            Prune();
            if (launches.Count >= 1000 || launches.Values.Count(x => x.UserId == userId) >= 20)
            {
                throw new InvalidOperationException("Too many pending launches. Wait ninety seconds and try again.");
            }

            var token = NewToken();
            launches.Add(token, new(userId, fileIds.Distinct().ToArray(), clock.GetUtcNow().AddSeconds(90)));
            return token;
        }
    }
    public PlaybackGrant? Redeem(string token)
    {
        lock (gate)
        {
            Prune();
            return ValidToken(token) && launches.Remove(token, out var grant) ? grant : null;
        }
    }
    public string IssueStream(int userId, int fileId)
    {
        lock (gate)
        {
            Prune();
            if (streams.Count >= 10000)
            {
                throw new InvalidOperationException("Playback session limit reached.");
            }

            var token = NewToken();
            streams.Add(token, new(userId, [fileId], clock.GetUtcNow().AddHours(12)));
            return token;
        }
    }
    public PlaybackGrant? FindStream(string token)
    {
        lock (gate)
        {
            Prune();
            return ValidToken(token) && streams.TryGetValue(token, out var grant) ? grant : null;
        }
    }
    private void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var key in launches.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key).ToArray())
        {
            launches.Remove(key);
        }

        foreach (var key in streams.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key).ToArray())
        {
            streams.Remove(key);
        }
    }
    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
