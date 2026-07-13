using System.Text.RegularExpressions;

namespace Muster.Cli.Reports;

public enum RosterSourceKind
{
    NrLink,
    Attachment,
    InlineYaml,
}

public sealed record RosterSource(RosterSourceKind Kind, string Value);

/// <summary>
/// Parses a (potentially hostile) GitHub issue body into a roster source and
/// reporter-supplied problem/expected text. Covers both muster's own issue-form
/// layout (GitHub renders form fields as `### Label` headings) and New Recruit's
/// auto-filed report layout (`**Label:**` markers).
/// </summary>
public sealed partial record IssueBody(RosterSource? Roster, string? Problem, string? Expected)
{
    /// <summary>Hard cap on how much of the body is ever handed to a regex.</summary>
    private const int MaxBodyLength = 65_536;

    /// <summary>Hard cap on captured Problem/Expected text.</summary>
    private const int MaxCaptureLength = 2_000;

    // Unanchored: finds a candidate NR-link substring anywhere in free text. The
    // extracted URL is later re-validated by NrShareLink.TryParse before use, so the
    // charset/host/scheme here must stay in lockstep with NrShareLink's pattern.
    [GeneratedRegex(@"https://www\.newrecruit\.eu/app/list/[A-Za-z0-9]{1,32}")]
    private static partial Regex NrLinkPattern();

    // Unanchored search variant; AttachmentClient re-validates with an anchored
    // full-match version of the same shape before making any HTTP call.
    [GeneratedRegex(@"https://github\.com/(?:user-attachments/files|[\w.-]+/[\w.-]+/files)/\d+/[A-Za-z0-9._-]+\.(?:rosz|ros|zip)")]
    private static partial Regex AttachmentPattern();

    [GeneratedRegex(@"```ya?ml\s*\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex YamlBlockPattern();

    // Matches both NR auto-report style (**Problem:**) and GitHub issue-form style
    // (### Problem heading), stopping at the next ** marker, the next ### heading, or
    // end of input.
    [GeneratedRegex(@"(?:\*\*Problem:\*\*|###\s*Problem\s*\n)\s*\n?(.*?)(?=\n\s*\*\*|\n\s*###|\z)", RegexOptions.Singleline)]
    private static partial Regex ProblemPattern();

    [GeneratedRegex(@"(?:\*\*Expected:\*\*|###\s*Expected\s*\n)\s*\n?(.*?)(?=\n\s*\*\*|\n\s*###|\z)", RegexOptions.Singleline)]
    private static partial Regex ExpectedPattern();

    public static IssueBody Parse(string body)
    {
        if (string.IsNullOrEmpty(body))
            return new IssueBody(null, null, null);

        var input = body.Length > MaxBodyLength ? body[..MaxBodyLength] : body;

        var roster = FindRoster(input);
        var problem = ExtractCapture(ProblemPattern(), input);
        var expected = ExtractCapture(ExpectedPattern(), input);

        return new IssueBody(roster, problem, expected);
    }

    private static RosterSource? FindRoster(string input)
    {
        var nrMatch = NrLinkPattern().Match(input);
        if (nrMatch.Success)
            return new RosterSource(RosterSourceKind.NrLink, nrMatch.Value);

        var attachmentMatch = AttachmentPattern().Match(input);
        if (attachmentMatch.Success)
            return new RosterSource(RosterSourceKind.Attachment, attachmentMatch.Value);

        foreach (Match yamlMatch in YamlBlockPattern().Matches(input))
        {
            var content = yamlMatch.Groups[1].Value;
            if (content.Contains("steps:", StringComparison.Ordinal))
                return new RosterSource(RosterSourceKind.InlineYaml, content);
        }

        return null;
    }

    private static string? ExtractCapture(Regex pattern, string input)
    {
        var match = pattern.Match(input);
        if (!match.Success)
            return null;

        var value = match.Groups[1].Value.Trim();
        if (value.Length == 0)
            return null;

        return value.Length > MaxCaptureLength ? value[..MaxCaptureLength] : value;
    }
}
