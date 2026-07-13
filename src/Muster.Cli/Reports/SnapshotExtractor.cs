using System.Text.Json;
using System.Text.RegularExpressions;

namespace Muster.Cli.Reports;

/// <summary>
/// Extracts the executable-spec snapshot from a GitHub issue's comments, as rendered by
/// <see cref="ReplyRenderer"/> (Task 10). Input is the raw JSON array produced by
/// <c>gh api /repos/{o}/{r}/issues/{n}/comments</c> — a list of objects each with a
/// <c>body</c> string.
/// </summary>
public static partial class SnapshotExtractor
{
    /// <summary>Defensive cap on how much of a single comment body is scanned.</summary>
    private const int MaxBodyLength = 256 * 1024;

    // Byte-compatible with ReplyRenderer's emitted snapshot block — verified against Task 10's
    // output by its reviewer. Do not reformat without re-verifying against ReplyRenderer.Render.
    [GeneratedRegex(@"<!-- muster:snapshot -->\s*```ya?ml\s*\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex SnapshotPattern();

    /// <summary>
    /// Returns the fenced-yaml content of the <c>&lt;!-- muster:snapshot --&gt;</c> block in a
    /// single comment body (e.g. the sticky report comment's current or previous text), or
    /// <see langword="null"/> if the marker is absent.
    /// </summary>
    public static string? ExtractFromBody(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        if (body.Length > MaxBodyLength)
        {
            body = body[..MaxBodyLength];
        }

        var match = SnapshotPattern().Match(body);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Returns the fenced-yaml content of the LAST comment (in array order) containing a
    /// <c>&lt;!-- muster:snapshot --&gt;</c> marker, or <see langword="null"/> if none is
    /// found or <paramref name="commentsJson"/> is not a well-formed JSON array.
    /// </summary>
    public static string? ExtractLatest(string commentsJson)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(commentsJson);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? latest = null;
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!item.TryGetProperty("body", out var bodyProp) || bodyProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var body = bodyProp.GetString();
                if (string.IsNullOrEmpty(body))
                {
                    continue;
                }

                var fromBody = ExtractFromBody(body);
                if (fromBody is not null)
                {
                    latest = fromBody;
                }
            }

            return latest;
        }
    }
}
