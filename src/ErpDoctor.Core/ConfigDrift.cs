using System.Text.Json;
using System.Text.RegularExpressions;

namespace ErpDoctor.Core;

public enum ConfigDriftKind
{
    MissingLeft,
    MissingRight,
    Different,
    TypeChanged
}

public sealed record ConfigDriftEntry(
    string Path,
    ConfigDriftKind Kind,
    string LeftValue,
    string RightValue,
    bool IsSensitive);

public sealed record ConfigDriftReport(
    string LeftLabel,
    string RightLabel,
    IReadOnlyList<ConfigDriftEntry> Differences);

public static class JsonConfigDriftAnalyzer
{
    public const string RedactedValue = "[SET]";
    public const string MissingValue = "[MISSING]";

    private static readonly Regex InlineSecretPattern = new(
        @"(?i)\b(password|pwd|token|secret|api[-_ ]?key|authorization|client[-_ ]?secret|access[-_ ]?key)\b\s*[:=]\s*(?:Bearer\s+)?[^;,\s&]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BearerTokenPattern = new(
        @"(?i)\bBearer\s+[A-Za-z0-9._~+/\-=]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ConfigDriftReport Compare(
        string leftJson,
        string rightJson,
        string leftLabel = "left",
        string rightLabel = "right",
        IEnumerable<string>? ignorePrefixes = null)
    {
        ArgumentNullException.ThrowIfNull(leftJson);
        ArgumentNullException.ThrowIfNull(rightJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(leftLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(rightLabel);

        var documentOptions = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        };

        using var leftDocument = JsonDocument.Parse(leftJson, documentOptions);
        using var rightDocument = JsonDocument.Parse(rightJson, documentOptions);

        var leftNodes = Flatten(leftDocument.RootElement);
        var rightNodes = Flatten(rightDocument.RootElement);
        var ignored = NormalizeIgnorePrefixes(ignorePrefixes);

        var paths = leftNodes.Keys
            .Concat(rightNodes.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !ShouldIgnore(path, ignored))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var candidates = new List<DriftCandidate>();
        foreach (var path in paths)
        {
            var hasLeft = leftNodes.TryGetValue(path, out var left);
            var hasRight = rightNodes.TryGetValue(path, out var right);

            if (!hasLeft)
            {
                var sensitive = right!.Sensitive || IsSensitivePath(path);
                candidates.Add(new DriftCandidate(
                    new ConfigDriftEntry(
                        path,
                        ConfigDriftKind.MissingLeft,
                        MissingValue,
                        Display(right, sensitive),
                        sensitive),
                    IsContainer(right.Kind)));
                continue;
            }

            if (!hasRight)
            {
                var sensitive = left!.Sensitive || IsSensitivePath(path);
                candidates.Add(new DriftCandidate(
                    new ConfigDriftEntry(
                        path,
                        ConfigDriftKind.MissingRight,
                        Display(left, sensitive),
                        MissingValue,
                        sensitive),
                    IsContainer(left.Kind)));
                continue;
            }

            var isSensitive = left!.Sensitive || right!.Sensitive || IsSensitivePath(path);
            if (left.Kind != right.Kind)
            {
                candidates.Add(new DriftCandidate(
                    new ConfigDriftEntry(
                        path,
                        ConfigDriftKind.TypeChanged,
                        Display(left, isSensitive),
                        Display(right, isSensitive),
                        isSensitive),
                    SuppressesDescendants: true));
                continue;
            }

            if (!string.Equals(left.ComparableValue, right.ComparableValue, StringComparison.Ordinal))
            {
                candidates.Add(new DriftCandidate(
                    new ConfigDriftEntry(
                        path,
                        ConfigDriftKind.Different,
                        Display(left, isSensitive),
                        Display(right, isSensitive),
                        isSensitive),
                    SuppressesDescendants: false));
            }
        }

        var suppressors = candidates
            .Where(candidate => candidate.SuppressesDescendants)
            .Select(candidate => candidate.Entry.Path)
            .ToArray();

        var differences = candidates
            .Where(candidate => !suppressors.Any(parent =>
                !string.Equals(parent, candidate.Entry.Path, StringComparison.OrdinalIgnoreCase) &&
                IsDescendant(candidate.Entry.Path, parent)))
            .Select(candidate => candidate.Entry)
            .ToArray();

        return new ConfigDriftReport(leftLabel, rightLabel, differences);
    }

    private static Dictionary<string, ConfigNode> Flatten(JsonElement root)
    {
        var nodes = new Dictionary<string, ConfigNode>(StringComparer.OrdinalIgnoreCase);
        Visit(root, "$", inheritedSensitive: false, nodes);
        return nodes;
    }

    private static void Visit(
        JsonElement element,
        string path,
        bool inheritedSensitive,
        IDictionary<string, ConfigNode> nodes)
    {
        var sensitive = inheritedSensitive || IsSensitivePath(path);
        nodes[path] = new ConfigNode(
            element.ValueKind,
            Comparable(element),
            Render(element),
            sensitive);

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var childPath = path == "$"
                    ? property.Name
                    : $"{path}:{property.Name}";
                Visit(property.Value, childPath, sensitive, nodes);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                var childPath = path == "$"
                    ? $"[{index}]"
                    : $"{path}[{index}]";
                Visit(item, childPath, sensitive, nodes);
                index++;
            }
        }
    }

    private static string Comparable(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "<object>",
        JsonValueKind.Array => "<array>",
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        JsonValueKind.Undefined => "undefined",
        _ => element.GetRawText()
    };

    private static string Render(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{...}",
        JsonValueKind.Array => "[...]",
        JsonValueKind.String => SanitizeInlineSecrets(element.GetString() ?? string.Empty),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        JsonValueKind.Undefined => "undefined",
        _ => SanitizeInlineSecrets(element.GetRawText())
    };

    private static string Display(ConfigNode node, bool sensitive)
    {
        if (!sensitive)
        {
            return node.DisplayValue;
        }

        return node.Kind == JsonValueKind.Null ? "[NULL]" : RedactedValue;
    }

    private static string SanitizeInlineSecrets(string value)
    {
        var sanitized = InlineSecretPattern.Replace(value, "$1=[REDACTED]");
        return BearerTokenPattern.Replace(sanitized, "Bearer [REDACTED]");
    }

    private static bool IsSensitivePath(string path)
    {
        var normalized = new string(path
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

        return normalized.Contains("connectionstrings", StringComparison.Ordinal) ||
               normalized.Contains("password", StringComparison.Ordinal) ||
               normalized.Contains("pwd", StringComparison.Ordinal) ||
               normalized.Contains("token", StringComparison.Ordinal) ||
               normalized.Contains("secret", StringComparison.Ordinal) ||
               normalized.Contains("apikey", StringComparison.Ordinal) ||
               normalized.Contains("authorization", StringComparison.Ordinal) ||
               normalized.Contains("clientsecret", StringComparison.Ordinal) ||
               normalized.Contains("privatekey", StringComparison.Ordinal) ||
               normalized.Contains("accesskey", StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> NormalizeIgnorePrefixes(IEnumerable<string>? prefixes)
    {
        if (prefixes is null)
        {
            return Array.Empty<string>();
        }

        return prefixes
            .Select(prefix => prefix.Trim().TrimEnd(':'))
            .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool ShouldIgnore(string path, IReadOnlyList<string> prefixes) =>
        prefixes.Any(prefix =>
            string.Equals(path, prefix, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(prefix + "[", StringComparison.OrdinalIgnoreCase));

    private static bool IsDescendant(string path, string parent) =>
        path.StartsWith(parent + ":", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(parent + "[", StringComparison.OrdinalIgnoreCase);

    private static bool IsContainer(JsonValueKind kind) =>
        kind is JsonValueKind.Object or JsonValueKind.Array;

    private sealed record ConfigNode(
        JsonValueKind Kind,
        string ComparableValue,
        string DisplayValue,
        bool Sensitive);

    private sealed record DriftCandidate(
        ConfigDriftEntry Entry,
        bool SuppressesDescendants);
}
