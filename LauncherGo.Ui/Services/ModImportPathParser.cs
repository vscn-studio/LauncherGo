using System.Text;

namespace LauncherGo.Ui.Services;

internal static class ModImportPathParser
{
    internal static string Quote(string path) =>
        $"\"{path.Replace("\"", "\"\"")}\"";

    internal static IReadOnlyList<string> Parse(string raw)
    {
        var trimmedRaw = raw.Trim();
        if (File.Exists(trimmedRaw) || Directory.Exists(trimmedRaw))
            return [trimmedRaw];

        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        while (index < raw.Length)
        {
            while (index < raw.Length && (char.IsWhiteSpace(raw[index]) || raw[index] is ';' or '|'))
                index++;
            if (index >= raw.Length)
                break;

            var value = new StringBuilder();
            if (raw[index] == '\"')
            {
                index++;
                while (index < raw.Length)
                {
                    if (raw[index] == '\"')
                    {
                        if (index + 1 < raw.Length && raw[index + 1] == '\"')
                        {
                            value.Append('\"');
                            index += 2;
                            continue;
                        }

                        index++;
                        break;
                    }

                    value.Append(raw[index++]);
                }
            }
            else
            {
                while (index < raw.Length && !char.IsWhiteSpace(raw[index]) && raw[index] is not (';' or '|'))
                    value.Append(raw[index++]);
            }

            var path = value.ToString().Trim();
            if (path.Length > 0 && seen.Add(path))
                paths.Add(path);
        }

        return paths;
    }
}
