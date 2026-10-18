using System.Globalization;
using System.Text;

namespace AiCtrl.Core.Config;

internal static class AcYamlSerializer
{
    public static string Serialize(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var root = new YamlMap();
        foreach (var pair in values)
        {
            Insert(root, pair.Key.Split(':'), 0, pair.Value);
        }

        var builder = new StringBuilder();
        if (root.Children.TryGetValue(AcConfigValidator.ConfigVersionKey, out var versionNode))
        {
            RenderNode(builder, AcConfigValidator.ConfigVersionKey, versionNode, string.Empty);
        }

        foreach (var child in root.Children
            .Where(child => !child.Key.Equals(AcConfigValidator.ConfigVersionKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(child => child.Key, StringComparer.Ordinal))
        {
            RenderNode(builder, child.Key, child.Value, string.Empty);
        }

        return builder.ToString();
    }

    private static void Insert(YamlNode node, string[] segments, int index, string? value)
    {
        var segment = segments[index];
        var isLeaf = index == segments.Length - 1;

        switch (node)
        {
            case YamlMap map:
                if (isLeaf)
                {
                    if (!map.Children.TryAdd(segment, new YamlScalar(value)))
                    {
                        throw new InvalidOperationException($"설정 경로 충돌: {string.Join(':', segments)}");
                    }

                    return;
                }

                if (!map.Children.TryGetValue(segment, out var mapChild))
                {
                    mapChild = CreateContainer(segments[index + 1]);
                    map.Children[segment] = mapChild;
                }

                Insert(mapChild, segments, index + 1, value);
                return;

            case YamlSequence sequence:
                var sequenceIndex = ParseIndex(segment, segments);
                if (isLeaf)
                {
                    if (!sequence.Children.TryAdd(sequenceIndex, new YamlScalar(value)))
                    {
                        throw new InvalidOperationException($"설정 경로 충돌: {string.Join(':', segments)}");
                    }

                    return;
                }

                if (!sequence.Children.TryGetValue(sequenceIndex, out var sequenceChild))
                {
                    sequenceChild = CreateContainer(segments[index + 1]);
                    sequence.Children[sequenceIndex] = sequenceChild;
                }

                Insert(sequenceChild, segments, index + 1, value);
                return;

            default:
                throw new InvalidOperationException($"설정 경로 충돌: {string.Join(':', segments)}");
        }
    }

    private static YamlNode CreateContainer(string nextSegment) =>
        IsIndex(nextSegment) ? new YamlSequence() : new YamlMap();

    private static bool IsIndex(string segment)
    {
        if (segment.Length == 0)
        {
            return false;
        }

        if (segment.Length > 1 && segment[0] == '0')
        {
            return false;
        }

        foreach (var character in segment)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static int ParseIndex(string segment, string[] segments) =>
        IsIndex(segment)
            ? int.Parse(segment, CultureInfo.InvariantCulture)
            : throw new InvalidOperationException($"시퀀스 인덱스가 올바르지 않습니다: {string.Join(':', segments)}");

    private static void RenderNode(StringBuilder builder, string key, YamlNode node, string indent)
    {
        switch (node)
        {
            case YamlScalar scalar:
                builder.Append(indent).Append(key).Append(": ").Append(FormatScalar(scalar.Value)).Append('\n');
                break;

            case YamlMap map when map.Children.Count == 0:
                builder.Append(indent).Append(key).Append(": {}\n");
                break;

            case YamlSequence sequence when sequence.Children.Count == 0:
                builder.Append(indent).Append(key).Append(": []\n");
                break;

            case YamlMap map:
                builder.Append(indent).Append(key).Append(":\n");
                foreach (var child in map.Children.OrderBy(child => child.Key, StringComparer.Ordinal))
                {
                    RenderNode(builder, child.Key, child.Value, indent + "  ");
                }

                break;

            case YamlSequence sequence:
                EnsureContiguousSequence(sequence, key);
                builder.Append(indent).Append(key).Append(":\n");
                foreach (var child in sequence.Children.OrderBy(child => child.Key))
                {
                    RenderSequenceItem(builder, child.Value, indent + "  ");
                }

                break;
        }
    }

    private static void RenderSequenceItem(StringBuilder builder, YamlNode node, string indent)
    {
        switch (node)
        {
            case YamlScalar scalar:
                builder.Append(indent).Append("- ").Append(FormatScalar(scalar.Value)).Append('\n');
                break;

            case YamlMap map when map.Children.Count == 0:
                builder.Append(indent).Append("- {}\n");
                break;

            case YamlMap map:
                RenderMapItem(builder, map, indent);
                break;

            case YamlSequence sequence when sequence.Children.Count == 0:
                builder.Append(indent).Append("- []\n");
                break;

            case YamlSequence:
                throw new InvalidOperationException("중첩 시퀀스는 지원하지 않습니다.");
        }
    }

    private static void RenderMapItem(StringBuilder builder, YamlMap map, string indent)
    {
        var first = true;
        foreach (var child in map.Children.OrderBy(child => child.Key, StringComparer.Ordinal))
        {
            var lineIndent = first ? indent + "- " : indent + "  ";
            first = false;
            RenderNode(builder, child.Key, child.Value, lineIndent);
        }
    }

    private static void EnsureContiguousSequence(YamlSequence sequence, string key)
    {
        var expected = 0;
        foreach (var index in sequence.Children.Keys.OrderBy(index => index))
        {
            if (index != expected)
            {
                throw new InvalidOperationException($"시퀀스 인덱스가 연속적이지 않습니다: {key}");
            }

            expected++;
        }
    }

    private static string FormatScalar(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "''";
        }

        if (IsPlainSafe(value))
        {
            return value;
        }

        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static bool IsPlainSafe(string value)
    {
        if (value[0] == ' ' || value[^1] == ' ')
        {
            return false;
        }

        foreach (var character in value)
        {
            var allowed = char.IsAsciiLetterOrDigit(character) ||
                character is '_' or '.' or '/' or '+' or '-' or ' ';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    private abstract class YamlNode;

    private sealed class YamlMap : YamlNode
    {
        public Dictionary<string, YamlNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class YamlSequence : YamlNode
    {
        public Dictionary<int, YamlNode> Children { get; } = [];
    }

    private sealed class YamlScalar(string? value) : YamlNode
    {
        public string? Value { get; } = value;
    }
}
