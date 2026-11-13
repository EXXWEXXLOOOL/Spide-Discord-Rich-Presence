using DiscordRPC;
using DiscordRPC.Logging;
using Windows.Media.Control;

namespace SpideDiscordPresence;

internal static class Program
{
    private static async Task Main()
    {
        Console.Title = "Spide → Discord Rich Presence";
        var appId = Environment.GetEnvironmentVariable("DISCORD_APP_ID")
            ?? ReadSetting("discordAppId");
        var imageKey = ReadSetting("discordImageKey") ?? "spide";

        if (string.IsNullOrWhiteSpace(appId))
        {
            Console.WriteLine("Set DISCORD_APP_ID or add discordAppId to config.json.");
            Console.WriteLine("Create an application at https://discord.com/developers/applications");
            Console.WriteLine("Press any key to exit.");
            Console.ReadKey();
            return;
        }

        using var rpc = new DiscordRpcClient(appId)
        {
            Logger = new ConsoleLogger { Level = LogLevel.Warning }
        };

        if (!rpc.Initialize())
        {
            Console.WriteLine("Could not connect to Discord. Make sure the Discord desktop app is running.");
            return;
        }

        GlobalSystemMediaTransportControlsSessionManager manager;
        try
        {
            manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not access Windows media sessions: {ex.Message}");
            return;
        }

        Console.WriteLine("Connected. Waiting for Spide playback…");
        string? lastFingerprint = null;
        var hadPresence = false;

        while (true)
        {
            try
            {
                var session = manager.GetSessions()
                    .FirstOrDefault(s => s.SourceAppUserModelId.Contains("spide", StringComparison.OrdinalIgnoreCase))
                    ?? manager.GetCurrentSession();

                if (session is null)
                {
                    if (hadPresence) rpc.ClearPresence();
                    hadPresence = false;
                    lastFingerprint = null;
                }
                else
                {
                    var properties = await session.TryGetMediaPropertiesAsync();
                    var playback = session.GetPlaybackInfo();
                    var timeline = session.GetTimelineProperties();

                    var title = string.IsNullOrWhiteSpace(properties.Title) ? "Unknown track" : properties.Title;
                    var artist = string.IsNullOrWhiteSpace(properties.Artist) ? "Unknown artist" : properties.Artist;
                    var isPlaying = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    var duration = timeline.EndTime - timeline.StartTime;
                    var position = timeline.Position;
                    var fingerprint = $"{session.SourceAppUserModelId}|{title}|{artist}|{isPlaying}|{timeline.EndTime.Ticks}";

                    if (fingerprint != lastFingerprint)
                    {
                        if (isPlaying)
                        {
                            var start = DateTime.UtcNow - position;
                            var end = DateTime.UtcNow + (timeline.EndTime - position);
                            rpc.SetPresence(new RichPresence
                            {
                                Details = title,
                                State = artist,
                                Timestamps = duration > TimeSpan.Zero
                                    ? new Timestamps { Start = start, End = end }
                                    : new Timestamps { Start = start },
                                Assets = new Assets
                                {
                                    LargeImageKey = imageKey,
                                    LargeImageText = "Spide"
                                }
                            });
                        }
                        else
                        {
                            rpc.SetPresence(new RichPresence
                            {
                                Details = title,
                                State = $"{artist} • Paused",
                                Assets = new Assets
                                {
                                    LargeImageKey = imageKey,
                                    LargeImageText = "Spide"
                                }
                            });
                        }

                        lastFingerprint = fingerprint;
                        hadPresence = true;
                        Console.WriteLine($"{(isPlaying ? "▶" : "Ⅱ")} {title} — {artist}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Update error: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private static string? ReadSetting(string key)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return json.RootElement.TryGetProperty(key, out var value) ? value.GetString() : null;
        }
        catch { return null; }
    }
}
