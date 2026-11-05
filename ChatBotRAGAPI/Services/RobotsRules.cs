namespace ChatBotRAGAPI.Services;

/// <summary>
/// Minimal robots.txt support for the crawler (RFC 9309 subset): the group for our user agent, or else the
/// "*" group; Allow/Disallow path prefixes with "*" and "$" wildcards; the longest matching rule wins and
/// Allow wins ties. Anything unparseable is ignored, so a broken robots.txt allows everything.
/// </summary>
public sealed class RobotsRules
{
    public const string UserAgent = "ChatBotRAGAPI";

    private readonly List<(string Pattern, bool Allow)> _rules;

    private RobotsRules(List<(string, bool)> rules) => _rules = rules;

    public static RobotsRules AllowAll { get; } = new([]);

    public static RobotsRules Parse(string? robotsTxt)
    {
        if (string.IsNullOrWhiteSpace(robotsTxt)) return AllowAll;

        var groups = new List<(List<string> Agents, List<(string, bool)> Rules)>();
        List<string>? agents = null;
        List<(string, bool)>? rules = null;
        var lastWasAgent = false;

        foreach (var rawLine in robotsTxt.Split('\n'))
        {
            var line = rawLine.Split('#')[0].Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var field = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();

            if (field == "user-agent")
            {
                // Consecutive User-agent lines share one group.
                if (!lastWasAgent)
                {
                    agents = [];
                    rules = [];
                    groups.Add((agents, rules));
                }
                agents!.Add(value.ToLowerInvariant());
                lastWasAgent = true;
                continue;
            }

            lastWasAgent = false;
            if (rules is null) continue; // rules before any User-agent line
            if (field == "disallow" && value.Length > 0) rules.Add((value, false)); // empty Disallow = allow all
            else if (field == "allow" && value.Length > 0) rules.Add((value, true));
        }

        var ours = groups.Where(g => g.Agents.Any(a => a != "*" && UserAgent.StartsWith(a, StringComparison.OrdinalIgnoreCase))).ToList();
        var chosen = ours.Count > 0 ? ours : groups.Where(g => g.Agents.Contains("*")).ToList();
        return new RobotsRules(chosen.SelectMany(g => g.Rules).ToList());
    }

    /// <param name="pathAndQuery">E.g. "/blog/post?id=1".</param>
    public bool IsAllowed(string pathAndQuery)
    {
        var best = (Length: -1, Allow: true);
        foreach (var (pattern, allow) in _rules)
        {
            if (!Matches(pattern, pathAndQuery)) continue;
            if (pattern.Length > best.Length || (pattern.Length == best.Length && allow))
                best = (pattern.Length, allow);
        }
        return best.Allow;
    }

    private static bool Matches(string pattern, string path)
    {
        var anchored = pattern.EndsWith('$');
        if (anchored) pattern = pattern[..^1];

        var parts = pattern.Split('*');
        if (!path.StartsWith(parts[0], StringComparison.Ordinal)) return false;
        if (parts.Length == 1) return !anchored || path.Length == parts[0].Length;

        // Middle parts match left to right, as early as possible.
        var pos = parts[0].Length;
        for (var i = 1; i < parts.Length - 1; i++)
        {
            var found = path.IndexOf(parts[i], pos, StringComparison.Ordinal);
            if (found < 0) return false;
            pos = found + parts[i].Length;
        }

        // "$" pins the last part to the end of the path ("/*.pdf$" matches "/a.pdf/b.pdf" but not "/a.pdf.html").
        var last = parts[^1];
        return anchored
            ? path.Length - last.Length >= pos && path.EndsWith(last, StringComparison.Ordinal)
            : path.IndexOf(last, pos, StringComparison.Ordinal) >= 0;
    }
}
