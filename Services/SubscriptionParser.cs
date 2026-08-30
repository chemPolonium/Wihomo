using System.Text;

namespace Wihomo.Services;

/// <summary>
/// 从订阅响应中提取 rules 段。订阅内容可能是明文 YAML、base64 或 URL-safe base64。
/// </summary>
public static class SubscriptionParser
{
    public sealed record SubscriptionParseResult(List<string> Rules);

    public static SubscriptionParseResult Parse(string content)
    {
        var rules = new List<string>();
        if (string.IsNullOrWhiteSpace(content))
        {
            return new SubscriptionParseResult(rules);
        }

        var normalized = DecodeSubscriptionText(content);
        var lines = normalized.Replace("\r\n", "\n").Split('\n');
        var section = string.Empty;
        var sectionIndent = -1;

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            var indent = CountLeadingSpaces(rawLine);
            if (section.Length > 0 && indent <= sectionIndent && !trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                section = string.Empty;
                sectionIndent = -1;
            }

            if (string.Equals(trimmed, "rules:", StringComparison.OrdinalIgnoreCase))
            {
                section = "rules";
                sectionIndent = indent;
                continue;
            }

            if (section == "rules" && trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                var candidate = trimmed[2..].Trim();
                if (IsRuleCandidate(candidate))
                {
                    rules.Add(candidate);
                }
            }
        }

        return new SubscriptionParseResult(
            rules
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList());
    }

    public static bool IsRuleCandidate(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.Contains(',') || text.Contains("MATCH") || text.Contains("FINAL"))
        {
            return true;
        }

        return text.StartsWith("DOMAIN", StringComparison.Ordinal)
            || text.StartsWith("IP-CIDR", StringComparison.Ordinal)
            || text.StartsWith("SRC-IP-CIDR", StringComparison.Ordinal)
            || text.StartsWith("GEOIP", StringComparison.Ordinal)
            || text.StartsWith("GEOSITE", StringComparison.Ordinal)
            || text.StartsWith("PROCESS-NAME", StringComparison.Ordinal)
            || text.StartsWith("URL-REGEX", StringComparison.Ordinal)
            || text.StartsWith("RULE-SET", StringComparison.Ordinal)
            || text.StartsWith("AND", StringComparison.Ordinal)
            || text.StartsWith("OR", StringComparison.Ordinal)
            || text.StartsWith("NOT", StringComparison.Ordinal);
    }

    public static string DecodeSubscriptionText(string content)
    {
        var normalized = content.Trim();
        if (!LooksLikeBase64(normalized))
        {
            return content;
        }

        try
        {
            var cleaned = new string(normalized.Where(c => !char.IsWhiteSpace(c)).ToArray());
            var remainder = cleaned.Length % 4;
            if (remainder == 2)
            {
                cleaned += "==";
            }
            else if (remainder == 3)
            {
                cleaned += "=";
            }
            else if (remainder == 1)
            {
                return content;
            }

            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cleaned));
            return decoded.Contains('\0') ? content : decoded;
        }
        catch
        {
            return content;
        }
    }

    private static int CountLeadingSpaces(string text)
    {
        var index = 0;
        while (index < text.Length && text[index] == ' ')
        {
            index++;
        }

        return index;
    }

    private static bool LooksLikeBase64(string text)
    {
        if (text.Length < 20)
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '+' && ch != '/' && ch != '='
                && ch != '\n' && ch != '\r' && ch != ' ' && ch != '\t')
            {
                return false;
            }
        }

        return true;
    }
}
