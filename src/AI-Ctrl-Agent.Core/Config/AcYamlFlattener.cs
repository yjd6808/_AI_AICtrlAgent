using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace AiCtrl.Core.Config;

internal static class AcYamlFlattener
{
    public static AcYamlDocument Flatten(string yamlText, string filePath)
    {
        ArgumentNullException.ThrowIfNull(yamlText);

        CheckTabIndentation(yamlText, filePath);

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var lines = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var parser = new Parser(new StringReader(yamlText));

        try
        {
            parser.MoveNext();
            if (parser.Current is not StreamStart)
            {
                throw Fail(filePath, parser.Current, "YAML 스트림이 올바르게 시작하지 않습니다.");
            }

            parser.MoveNext();
            var documentCount = 0;

            while (parser.Current is not (StreamEnd or null))
            {
                if (parser.Current is DocumentStart)
                {
                    documentCount++;
                    if (documentCount > 1)
                    {
                        throw Fail(filePath, parser.Current, "다중 문서(---)는 허용하지 않습니다.");
                    }

                    parser.MoveNext();
                    if (parser.Current is MappingStart)
                    {
                        ParseMapping(parser, filePath, string.Empty, values, lines);
                    }
                    else if (parser.Current is not DocumentEnd)
                    {
                        throw Fail(filePath, parser.Current, "설정 최상위는 매핑(mapping)이어야 합니다.");
                    }
                }

                parser.MoveNext();
            }
        }
        catch (YamlException ex)
        {
            throw new AcConfigException(filePath, (int)ex.Start.Line, (int)ex.Start.Column, "YAML 구문 오류: " + ex.Message, ex);
        }

        return new AcYamlDocument(values, lines);
    }

    private static void ParseMapping(
        IParser parser,
        string filePath,
        string prefix,
        Dictionary<string, string?> values,
        Dictionary<string, int> lines)
    {
        VerifyNode(filePath, parser.Current);
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        parser.MoveNext();

        while (parser.Current is not MappingEnd)
        {
            if (parser.Current is not Scalar key)
            {
                throw Fail(filePath, parser.Current, "매핑 키는 스칼라여야 합니다.");
            }

            VerifyNode(filePath, key);
            if (!seenKeys.Add(key.Value))
            {
                throw Fail(filePath, key, $"중복 키 '{key.Value}'");
            }

            var path = prefix.Length == 0 ? key.Value : $"{prefix}:{key.Value}";
            lines[path] = (int)key.Start.Line;
            parser.MoveNext();
            ParseValue(parser, filePath, path, values, lines);
        }

        parser.MoveNext();
    }

    private static void ParseSequence(
        IParser parser,
        string filePath,
        string prefix,
        Dictionary<string, string?> values,
        Dictionary<string, int> lines)
    {
        VerifyNode(filePath, parser.Current);
        parser.MoveNext();
        var index = 0;

        while (parser.Current is not SequenceEnd)
        {
            ParseValue(parser, filePath, $"{prefix}:{index}", values, lines);
            index++;
        }

        parser.MoveNext();
    }

    private static void ParseValue(
        IParser parser,
        string filePath,
        string path,
        Dictionary<string, string?> values,
        Dictionary<string, int> lines)
    {
        switch (parser.Current)
        {
            case Scalar scalar:
                VerifyNode(filePath, scalar);
                values[path] = scalar.Value;
                parser.MoveNext();
                break;

            case MappingStart:
                ParseMapping(parser, filePath, path, values, lines);
                break;

            case SequenceStart:
                ParseSequence(parser, filePath, path, values, lines);
                break;

            case AnchorAlias:
                throw Fail(filePath, parser.Current, "별칭(*alias)은 허용하지 않습니다.");

            default:
                throw Fail(filePath, parser.Current, "지원하지 않는 YAML 구조입니다.");
        }
    }

    private static void VerifyNode(string filePath, ParsingEvent? parsingEvent)
    {
        if (parsingEvent is NodeEvent node && !node.Anchor.IsEmpty)
        {
            throw Fail(filePath, parsingEvent, "앵커(&anchor)는 허용하지 않습니다.");
        }
    }

    private static void CheckTabIndentation(string text, string filePath)
    {
        var lineNumber = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            lineNumber++;
            var line = rawLine.TrimEnd('\r');
            var index = 0;

            while (index < line.Length && (line[index] == ' ' || line[index] == '\t'))
            {
                index++;
            }

            if (index > 0 && line[..index].Contains('\t'))
            {
                throw new AcConfigException(filePath, lineNumber, 1, "탭 들여쓰기는 허용하지 않습니다.");
            }
        }
    }

    private static AcConfigException Fail(string filePath, ParsingEvent? parsingEvent, string reason) =>
        new(filePath, (int)(parsingEvent?.Start.Line ?? 0), (int)(parsingEvent?.Start.Column ?? 0), reason);
}

internal sealed record AcYamlDocument(
    Dictionary<string, string?> Values,
    Dictionary<string, int> KeyLines);
