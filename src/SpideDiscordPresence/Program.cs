using System.IO.Pipes;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace SpideDiscordPresence;

internal static class Program
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly Dictionary<string, string> ArtworkCache = new();
    private static readonly Dictionary<string, DateTimeOffset> ArtworkRetryAfter = new();

    private static async Task Main()
    {
        Console.Title = "Spide → Discord Rich Presence";
        var appId = Environment.GetEnvironmentVariable("DISCORD_APP_ID") ?? ReadSetting("discordAppId");
        if (string.IsNullOrWhiteSpace(appId))
        {
            Console.WriteLine("Set DISCORD_APP_ID or add discordAppId to config.json.");
            Console.ReadKey();
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

        await using var rpc = new DiscordIpcClient(appId);
        if (!await rpc.ConnectAsync())
        {
            Console.WriteLine("Could not connect to Discord. Start the Discord desktop app and try again.");
            return;
        }

        Console.WriteLine("Connected to Discord. Waiting for Spide playback…");
        string? lastFingerprint = null;
        while (true)
        {
            try
            {
                var session = manager.GetSessions()
                    .FirstOrDefault(s => s.SourceAppUserModelId.Contains("spide", StringComparison.OrdinalIgnoreCase))
                    ?? manager.GetCurrentSession();

                if (session is null)
                {
                    if (lastFingerprint is not null) await rpc.SetActivityAsync(null);
                    lastFingerprint = null;
                }
                else
                {
                    var properties = await session.TryGetMediaPropertiesAsync();
                    var playback = session.GetPlaybackInfo();
                    var timeline = session.GetTimelineProperties();

                    var title = string.IsNullOrWhiteSpace(properties.Title) ? "Unknown track" : properties.Title.Trim();
                    var artist = string.IsNullOrWhiteSpace(properties.Artist) ? "Unknown artist" : properties.Artist.Trim();
                    var playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    var duration = timeline.EndTime - timeline.StartTime;
                    var position = timeline.Position;
                    var imageUrl = await GetArtworkUrlAsync(properties.Thumbnail);
                    var fingerprint = $"{session.SourceAppUserModelId}|{title}|{artist}|{playing}|{timeline.EndTime.Ticks}|{imageUrl}";

                    if (fingerprint != lastFingerprint)
                    {
                        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        object? timestamps = playing && duration > TimeSpan.Zero
                            ? new { start = now - (long)position.TotalSeconds, end = now + (long)(duration - position).TotalSeconds }
                            : null;

                        var activity = new
                        {
                            type = 2,
                            details = title,
                            state = playing ? artist : $"{artist} • Paused",
                            timestamps,
                            assets = string.IsNullOrWhiteSpace(imageUrl) ? null : new
                            {
                                large_image = imageUrl,
                                large_text = $"{title} — {artist}"
                            }
                        };

                        await rpc.SetActivityAsync(activity);
                        lastFingerprint = fingerprint;
                        Console.WriteLine($"{(playing ? "▶" : "Ⅱ")} {title} — {artist} [{session.SourceAppUserModelId}]");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Update error: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }

    private static async Task<string?> GetArtworkUrlAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null) return null;

        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            if (stream.Size == 0 || stream.Size > 8 * 1024 * 1024) return null;

            var buffer = new Windows.Storage.Streams.Buffer(checked((uint)stream.Size));
            await stream.ReadAsync(buffer, checked((uint)stream.Size), InputStreamOptions.None);
            using var reader = DataReader.FromBuffer(buffer);
            var bytes = new byte[buffer.Length];
            reader.ReadBytes(bytes);

            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (ArtworkCache.TryGetValue(hash, out var cached)) return cached;
            if (ArtworkRetryAfter.TryGetValue(hash, out var retryAfter) && retryAfter > DateTimeOffset.UtcNow)
                return null;

            var extension = stream.ContentType.Contains("png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpg";

            // Litterbox accepts anonymous uploads and returns a temporary public URL.
            // Catbox is retained as a fallback in case the temporary-upload endpoint is unavailable.
            foreach (var endpoint in new[]
            {
                ("https://litterbox.catbox.moe/resources/internals/api.php", true),
                ("https://catbox.moe/user/api.php", false)
            })
            {
                try
                {
                    using var form = new MultipartFormDataContent();
                    form.Add(new StringContent("fileupload"), "reqtype");
                    if (endpoint.Item2) form.Add(new StringContent("72h"), "time");
                    var file = new ByteArrayContent(bytes);
                    form.Add(file, "fileToUpload", $"spide-cover.{extension}");

                    using var response = await Http.PostAsync(endpoint.Item1, form);
                    var body = (await response.Content.ReadAsStringAsync()).Trim();
                    response.EnsureSuccessStatusCode();
                    if (!Uri.TryCreate(body, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps)
                        throw new IOException($"Image host returned an unexpected response: {body}");

                    ArtworkCache[hash] = body;
                    ArtworkRetryAfter.Remove(hash);
                    Console.WriteLine("Track cover uploaded successfully.");
                    return body;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Cover host {new Uri(endpoint.Item1).Host} failed: {DescribeException(ex)}");
                }
            }

            // Avoid uploading the same failed cover every three seconds.
            ArtworkRetryAfter[hash] = DateTimeOffset.UtcNow.AddMinutes(2);
            Console.WriteLine("Cover upload paused for 2 minutes; track metadata will continue updating.");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not read Spide cover: {DescribeException(ex)}");
            return null;
        }
    }

    private static string DescribeException(Exception ex)
    {
        var messages = new List<string>();
        for (Exception? current = ex; current is not null; current = current.InnerException)
            messages.Add($"{current.GetType().Name}: {current.Message}");
        return string.Join(" -> ", messages);
    }

    private static string? ReadSetting(string key)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            return json.RootElement.TryGetProperty(key, out var value) ? value.GetString() : null;
        }
        catch { return null; }
    }

    private sealed class DiscordIpcClient : IAsyncDisposable
    {
        private readonly string _appId;
        private NamedPipeClientStream? _pipe;

        public DiscordIpcClient(string appId) => _appId = appId;

        public async Task<bool> ConnectAsync()
        {
            for (var i = 0; i < 10; i++)
            {
                var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await pipe.ConnectAsync(timeout.Token);
                    _pipe = pipe;
                    await WriteFrameAsync(0, new { v = 1, client_id = _appId });
                    await ReadFrameAsync();
                    return true;
                }
                catch
                {
                    pipe.Dispose();
                    _pipe = null;
                }
            }
            return false;
        }

        public Task SetActivityAsync(object? activity) =>
            WriteFrameAsync(1, new
            {
                cmd = "SET_ACTIVITY",
                args = new { pid = Environment.ProcessId, activity },
                nonce = Guid.NewGuid().ToString("N")
            });

        private async Task WriteFrameAsync(int opcode, object payload)
        {
            if (_pipe is null) throw new IOException("Discord IPC is disconnected.");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            var header = new byte[8];
            BitConverter.GetBytes(opcode).CopyTo(header, 0);
            BitConverter.GetBytes(bytes.Length).CopyTo(header, 4);
            await _pipe.WriteAsync(header);
            await _pipe.WriteAsync(bytes);
            await _pipe.FlushAsync();
        }

        private async Task ReadFrameAsync()
        {
            if (_pipe is null) throw new IOException("Discord IPC is disconnected.");
            var header = new byte[8];
            await ReadExactlyAsync(_pipe, header);
            var length = BitConverter.ToInt32(header, 4);
            if (length < 0 || length > 1024 * 1024) throw new IOException("Invalid Discord IPC frame.");
            var payload = new byte[length];
            await ReadExactlyAsync(_pipe, payload);
        }

        private static async Task ReadExactlyAsync(Stream stream, byte[] buffer)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset));
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_pipe is not null)
            {
                try { await SetActivityAsync(null); } catch { }
                _pipe.Dispose();
            }
        }
    }
}
