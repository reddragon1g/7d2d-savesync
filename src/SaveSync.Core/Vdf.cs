using System.Text;

namespace SaveSync.Core;

/// <summary>
/// Minimal reader for Valve's KeyValues text format, enough for libraryfolders.vdf and
/// localconfig.vdf. Parsed properly rather than regex-scraped, because the result decides where a
/// save lives and a wrong answer there is not a cosmetic bug.
/// </summary>
public static class Vdf
{
    public sealed class Node
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Node? Child(params string[] path)
        {
            var cur = this;
            foreach (var p in path)
            {
                if (!cur.Children.TryGetValue(p, out var next)) return null;
                cur = next;
            }
            return cur;
        }

        public string? Value(string key) => Values.TryGetValue(key, out var v) ? v : null;
    }

    public static Node Parse(string text)
    {
        int i = 0;
        var root = new Node();
        ParseInto(text, ref i, root, depth: 0);
        return root;
    }

    public static Node? ParseFile(string path)
    {
        try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void ParseInto(string s, ref int i, Node node, int depth)
    {
        if (depth > 64) return; // malformed or hostile input; bail rather than blow the stack

        while (true)
        {
            var key = NextToken(s, ref i);
            if (key is null || key == "}") return;
            if (key == "{") continue; // stray brace; tolerate

            int save = i;
            var next = NextToken(s, ref i);
            if (next is null) return;

            if (next == "{")
            {
                var child = new Node();
                ParseInto(s, ref i, child, depth + 1);
                node.Children[key] = child;
            }
            else if (next == "}")
            {
                i = save;
                return;
            }
            else
            {
                node.Values[key] = next;
            }
        }
    }

    private static string? NextToken(string s, ref int i)
    {
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            break;
        }
        if (i >= s.Length) return null;

        char ch = s[i];
        if (ch == '{' || ch == '}') { i++; return ch.ToString(); }

        if (ch == '"')
        {
            i++;
            var sb = new StringBuilder();
            while (i < s.Length && s[i] != '"')
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    i++;
                    sb.Append(s[i] switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        '\\' => '\\',
                        '"' => '"',
                        var other => other,
                    });
                }
                else
                {
                    sb.Append(s[i]);
                }
                i++;
            }
            i++; // closing quote
            return sb.ToString();
        }

        int start = i;
        while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '{' && s[i] != '}') i++;
        return s[start..i];
    }
}
