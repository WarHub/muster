using System.Text.RegularExpressions;

namespace Muster.Cli.Reports;

/// <summary>
/// Downloads an allowlisted GitHub issue attachment. Mirrors <see cref="Muster.Cli.NewRecruit.NrClient"/>'s
/// streaming 5&#160;MB cap and graceful-error contract. All remote failures degrade to an
/// error result — callers map them to needs-info, never a crash.
/// </summary>
public sealed partial class AttachmentClient(HttpMessageHandler? handler = null) : IDisposable
{
    private const long MaxResponseBytes = 5 * 1024 * 1024;

    // Anchored full-match re-validation, independent of IssueBody's search pattern:
    // defense in depth so a caller-supplied URL that doesn't match the allowlisted
    // shape is rejected before any network activity occurs.
    [GeneratedRegex(@"^https://github\.com/(?:user-attachments/files|[\w.-]+/[\w.-]+/files)/\d+/[A-Za-z0-9._-]+\.(?:rosz|ros|zip)$")]
    private static partial Regex AttachmentUrlPattern();

    private readonly HttpClient _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public async Task<(byte[]? Data, string? Error)> DownloadAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url) || !AttachmentUrlPattern().IsMatch(url.Trim()))
            return (null, "attachment URL is not an allowlisted GitHub attachment link");

        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return (null, $"GitHub returned HTTP {(int)response.StatusCode}");

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var limited = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                limited.Write(buffer, 0, read);
                if (limited.Length > MaxResponseBytes)
                    return (null, "attachment too large (>5 MB)");
            }
            return (limited.ToArray(), null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "GitHub did not respond within 30 seconds");
        }
        catch (HttpRequestException e)
        {
            return (null, $"could not reach GitHub: {e.Message}");
        }
        catch (IOException e)
        {
            return (null, $"connection to GitHub failed: {e.Message}");
        }
    }

    public void Dispose() => _http.Dispose();
}
