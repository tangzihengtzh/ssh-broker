using System.Text;

namespace SshBroker.Core;

// A documented command-file syntax, not a Tcl interpreter: quotes, literal braces, comments.
public static class ScriptTokenizer
{
    public static string[] Tokenize(string line)
    {
        var result = new List<string>(); var token = new StringBuilder();
        char quote = '\0'; int braces = 0; bool started = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (braces > 0)
            {
                if (c == '{') { braces++; token.Append(c); }
                else if (c == '}') { if (--braces > 0) token.Append(c); }
                else token.Append(c);
                continue;
            }
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else if (c == '\\' && quote == '"' && i + 1 < line.Length)
                {
                    var next = line[++i];
                    token.Append(next switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '"' => '"', '\\' => '\\', _ => next });
                }
                else token.Append(c);
                continue;
            }
            if (c == '#' && !started) break;
            if (char.IsWhiteSpace(c))
            {
                if (started) { result.Add(token.ToString()); token.Clear(); started = false; }
            }
            else if (c is '"' or '\'') { quote = c; started = true; }
            else if (c == '{') { braces = 1; started = true; }
            else { token.Append(c); started = true; }
        }
        if (quote != '\0' || braces != 0) throw new BrokerException("script_syntax", "Unterminated quote or brace.");
        if (started) result.Add(token.ToString());
        return result.ToArray();
    }
}
