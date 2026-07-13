using System.Text;
using System.Text.Json;

namespace Muster.Cli.NewRecruit;

public sealed record NrFetchResult(string? Json, string? Error);

/// <summary>
/// Fetches a shared New Recruit list via the (undocumented) open_share_link RPC.
/// All remote failures degrade to <see cref="NrFetchResult.Error"/> — callers
/// map them to needs-info, never a crash.
/// </summary>
public sealed class NrClient(HttpMessageHandler? handler = null) : IDisposable
{
    private const long MaxResponseBytes = 5 * 1024 * 1024;
    private static readonly Uri RpcUri = new("https://www.newrecruit.eu/api/rpc");

    private readonly HttpClient _http = new(handler ?? new HttpClientHandler())
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public async Task<NrFetchResult> FetchListAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var body = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["method"] = "open_share_link",
                ["params"] = new[] { key },
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, RpcUri)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return new(null, $"New Recruit returned HTTP {(int)response.StatusCode}");

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var limited = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                limited.Write(buffer, 0, read);
                if (limited.Length > MaxResponseBytes)
                    return new(null, "response too large (>5 MB)");
            }
            var text = Encoding.UTF8.GetString(limited.ToArray()).Trim();
            if (text is "null" or "" or "[]")
                return new(null, "list not found or no longer shared (New Recruit share links expire)");
            if (!text.StartsWith('{'))
                return new(null, "New Recruit returned an unexpected response");
            return new(text, null);
        }
        catch (TaskCanceledException)
        {
            return new(null, "New Recruit did not respond within 30 seconds");
        }
        catch (HttpRequestException e)
        {
            return new(null, $"could not reach New Recruit: {e.Message}");
        }
    }

    public void Dispose() => _http.Dispose();
}
