using System.Buffers;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Sockseek.Core;

public static partial class Utils
{
    public static readonly string[] musicExtensions = new string[] { ".mp3", ".flac", ".ogg", ".m4a", ".opus", ".wav", ".aac", ".alac" };
    public static readonly string[] imageExtensions = new string[] { ".jpg", ".png", ".jpeg", ".gif", ".webp" };
    public static readonly string[] videoExtensions = new string[] { ".mp4", ".mkv", ".avi", ".mov", ".webm", ".mpeg" };


    public static bool IsMusicExtension(string extension)
    {
        return musicExtensions.Contains(extension.ToLower());
    }

    public static bool IsMusicFile(string fileName)
    {
        return musicExtensions.Contains(Path.GetExtension(fileName).ToLower());
    }

    public static bool IsImageExtension(string extension)
    {
        return imageExtensions.Contains(extension.ToLower());
    }

    public static bool IsImageFile(string fileName)
    {
        return imageExtensions.Contains(Path.GetExtension(fileName).ToLower());
    }

    public static bool IsVideoExtension(string extension)
    {
        return videoExtensions.Contains(extension.ToLower());
    }

    public static bool IsVideoFile(string fileName)
    {
        return videoExtensions.Contains(Path.GetExtension(fileName).ToLower());
    }

    public static bool IsInternetUrl(this string str)
    {
        str = str.TrimStart();
        return str.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || str.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    public static void WriteAllLines(string path, IEnumerable<string> lines, char separator)
    {
        using (var writer = new StreamWriter(path))
        {
            foreach (var line in lines)
            {
                writer.Write(line);
                writer.Write(separator);
            }
        }
    }

    public async static Task WriteAllLinesAsync(string path, IEnumerable<string> lines, char separator)
    {
        using (var writer = new StreamWriter(path))
        {
            foreach (var line in lines)
            {
                await writer.WriteAsync(line);
                await writer.WriteAsync(separator);
            }
        }
    }

    public static string GetFullPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        return Path.GetFullPath(path);
    }

    // TODO [ARCHITECTURE]: Introduce explicit path value types for local filesystem paths
    // and Soulseek remote paths. Several cross-platform bugs have come from treating those
    // as interchangeable strings and relying on ad hoc separator normalization at call sites.
    // Keep raw config paths, normalized local paths, and Soulseek paths distinct enough that
    // the compiler helps prevent accidental mixing.
    public static string GetAsPathSlsk(string fname)
    {
        return fname.Replace('\\', Path.DirectorySeparatorChar);
    }

    public static string GetFileNameSlsk(string fname)
    {
        fname = fname.Replace('\\', Path.DirectorySeparatorChar);
        return Path.GetFileName(fname);
    }

    public static string GetBaseNameSlsk(string path)
    {
        path = path.Replace('\\', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
        return Path.GetFileName(path);
    }

    public static string GetFileNameWithoutExtSlsk(string fname)
    {
        fname = fname.Replace('\\', Path.DirectorySeparatorChar);
        return Path.GetFileNameWithoutExtension(fname);
    }

    public static string GetExtensionSlsk(string fname)
    {
        fname = fname.Replace('\\', Path.DirectorySeparatorChar);
        return Path.GetExtension(fname);
    }

    public static string GetDirectoryNameSlsk(string fname)
    {
        fname = fname.Replace('\\', Path.DirectorySeparatorChar);
        var directoryName = Path.GetDirectoryName(fname);
        if (directoryName == null || directoryName == String.Empty)
        {
            return String.Empty;
        }
        else
        {
            return directoryName;
        }
    }

    public static string ExpandVariables(string path)
        => ExpandVariables(path, PathVariableContext.Empty);

    public static string ExpandVariables(string path, PathVariableContext context)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        path = NormalizedPath(path);

        if (path.Length == 0)
            return path;

        if (path[0] == '~' && (path.Length == 1 || path[1] == '/'))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = JoinPathPrefix(home, path[1..]);
        }
        else if (path.StartsWith("{bindir}") && (path.Length == 8 || path[8] == '/'))
        {
            string bindir = AppDomain.CurrentDomain.BaseDirectory;
            path = JoinPathPrefix(bindir, path[8..]);
        }
        else if (!string.IsNullOrWhiteSpace(context.ConfigDir)
            && path.StartsWith("{configdir}") && (path.Length == 11 || path[11] == '/'))
        {
            path = JoinPathPrefix(context.ConfigDir, path[11..]);
        }

        return path;
    }

    private static string JoinPathPrefix(string prefix, string suffix)
    {
        var parts = suffix.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? prefix : Path.Join([prefix, .. parts]);
    }

    public static List<string> FromCsv(string csvLine)
    {
        var items = new List<string>();
        var currentItem = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < csvLine.Length; i++)
        {
            char c = csvLine[i];

            if (c == '"' && (i == 0 || csvLine[i - 1] != '\\'))
            {
                if (inQuotes && i + 1 < csvLine.Length && csvLine[i + 1] == '"')
                {
                    currentItem.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                items.Add(currentItem.ToString());
                currentItem.Clear();
            }
            else
            {
                currentItem.Append(c);
            }
        }
        items.Add(currentItem.ToString());

        return items;
    }

    public static decimal Normalize(this double value)
    {
        return ((decimal)value) / 1.000000000000000000000000000000000m;
    }

    public static int FileCountRecursive(string directory)
    {
        if (!Directory.Exists(directory))
            return 0;

        int count = Directory.GetFiles(directory).Length;
        foreach (string subDirectory in Directory.GetDirectories(directory))
            count += FileCountRecursive(subDirectory);

        return count;
    }

    public static void Move(string sourceFilePath, string destinationFilePath)
    {
        if (File.Exists(sourceFilePath))
        {
            if (File.Exists(destinationFilePath))
            {
                if (Path.GetFullPath(sourceFilePath) == Path.GetFullPath(destinationFilePath))
                {
                    return;
                }
                File.Delete(destinationFilePath);
            }
            File.Move(sourceFilePath, destinationFilePath);
        }
        else
        {
            throw new IOException($"Source file does not exist at path: {sourceFilePath}");
        }
    }

    public static void DeleteAncestorsIfEmpty(string startDir, string root)
    {
        string x = NormalizedPath(Path.GetFullPath(root));
        string y = NormalizedPath(startDir);

        if (x.Length == 0)
            return;

        while (y.StartsWith(x + '/') && FileCountRecursive(y) == 0)
        {
            Directory.Delete(y, true);

            string prev = y;
            y = NormalizedPath(Path.GetDirectoryName(y) ?? "");

            if (prev.Length == y.Length)
                break;
        }
    }

    public static void MoveAndDeleteParent(string oldPath, string newPath, string recurseUntil)
    {
        if (Path.GetFullPath(oldPath) != Path.GetFullPath(newPath))
        {
            var newParent = Path.GetDirectoryName(newPath);
            if (!string.IsNullOrEmpty(newParent))
                Directory.CreateDirectory(newParent);
            Move(oldPath, newPath);
            var oldParent = Path.GetDirectoryName(oldPath);
            if (!string.IsNullOrEmpty(oldParent))
                DeleteAncestorsIfEmpty(oldParent, recurseUntil);
        }
    }

    public static void DeleteFileAndParentsIfEmpty(string filepath, string recurseUntil)
    {
        File.Delete(filepath);
        var parent = Path.GetDirectoryName(filepath);
        if (!string.IsNullOrEmpty(parent))
            DeleteAncestorsIfEmpty(parent, recurseUntil);
    }

    public static bool EqualsAny(this string input, string[] values, StringComparison comparison = StringComparison.Ordinal)
    {
        foreach (var value in values)
        {
            if (input.Equals(value, comparison))
                return true;
        }
        return false;
    }

    public static string Replace(this string s, string[] separators, string newVal)
    {
        if (s.Length == 0)
            return s;

        foreach (var sep in separators)
            s = s.Replace(sep, newVal);

        return s;
    }

    public static string UnHtmlString(this string s)
    {
        s = WebUtility.HtmlDecode(s);
        string[] zeroWidthChars = { "\u200B", "\u200C", "\u200D", "\u00AD", "\u200E", "\u200F" };
        foreach (var zwChar in zeroWidthChars)
            s = s.Replace(zwChar, "");

        s = s.Replace('\u00A0', ' ');
        return s;
    }

    private static readonly SearchValues<char> s_windowsInvalid =
        SearchValues.Create([':', '|', '?', '>', '<', '*', '"', '/', '\\']);

    private static readonly SearchValues<char> s_windowsInvalidNoSlash =
        SearchValues.Create([':', '|', '?', '>', '<', '*', '"']);

    private static readonly SearchValues<char> s_platformInvalid =
        SearchValues.Create(Path.GetInvalidFileNameChars());

    private static readonly SearchValues<char> s_platformInvalidNoSlash =
        SearchValues.Create(Path.GetInvalidFileNameChars()
            .Where(static c => c is not '/' and not '\\')
            .ToArray());

    private static SearchValues<char> InvalidSet(bool windows, bool removeSlash) => windows
        ? (removeSlash ? s_windowsInvalid : s_windowsInvalidNoSlash)
        : (removeSlash ? s_platformInvalid : s_platformInvalidNoSlash);

    public static string ReplaceInvalidChars(this string str, char replaceChar, bool windows = false, bool removeSlash = true)
    {
        if (str.Length == 0)
            return str;

        var invalid = InvalidSet(windows, removeSlash);
        int first = str.AsSpan().IndexOfAny(invalid);
        if (first < 0)
            return str;

        return string.Create(str.Length, (str, replaceChar, invalid, first), static (dst, s) =>
        {
            s.str.AsSpan().CopyTo(dst);
            dst[s.first] = s.replaceChar;
            for (int i = s.first + 1; i < dst.Length; i++)
                if (s.invalid.Contains(dst[i]))
                    dst[i] = s.replaceChar;
        });
    }

    public static string ReplaceInvalidChars(this string str, string replaceStr, bool windows = false, bool removeSlash = true)
    {
        if (str.Length == 0)
            return str;

        var invalid = InvalidSet(windows, removeSlash);
        int first = str.AsSpan().IndexOfAny(invalid);
        if (first < 0)
            return str;

        if (replaceStr.Length == 1)
            return str.ReplaceInvalidChars(replaceStr[0], windows, removeSlash);

        if (replaceStr.Length == 0)
        {
            int keep = first;
            for (int i = first; i < str.Length; i++)
                if (!invalid.Contains(str[i])) keep++;

            return string.Create(keep, (str, invalid, first), static (dst, s) =>
            {
                s.str.AsSpan()[..s.first].CopyTo(dst);
                int di = s.first;
                for (int i = s.first; i < s.str.Length; i++)
                    if (!s.invalid.Contains(s.str[i]))
                        dst[di++] = s.str[i];
            });
        }

        var sb = new StringBuilder(str.Length * 2);
        sb.Append(str, 0, first);
        sb.Append(replaceStr);
        for (int i = first + 1; i < str.Length; i++)
        {
            if (invalid.Contains(str[i])) sb.Append(replaceStr);
            else sb.Append(str[i]);
        }
        return sb.ToString();
    }

    public static string CleanPath(this string fullPath, string replaceWith)
    {
        fullPath = Utils.NormalizedPath(fullPath);

        string[] pathParts = fullPath.Split('/');

        foreach (char badChar in Path.GetInvalidPathChars())
        {
            if (badChar != ':')
            {
                pathParts[0] = pathParts[0].Replace(badChar.ToString(), replaceWith);
            }
        }

        var chars = Path.GetInvalidFileNameChars();

        for (int i = 1; i < pathParts.Length; i++)
        {
            foreach (char badChar in chars)
            {
                pathParts[i] = pathParts[i].Replace(badChar.ToString(), replaceWith);
            }
        }

        if (pathParts.Length > 0)
            pathParts[pathParts.Length - 1] = pathParts[pathParts.Length - 1].TrimEnd('.');

        return string.Join('/', pathParts.Select(a => a.Trim()));
    }

    private static readonly SearchValues<char> s_special = SearchValues.Create(
        [';', ':', '：', '\'', '"', '|', '?', '？', '!', '！', '<', '>', '*', '/', '\\', '+',
        '[', ']', '{', '}', '(', ')', '-',
        '–', '—', '―',
        '&', '%', '^', '$', '#', '＃', '@', '+', '=', '`', '~', '_',
        '；', '：', '｜', '？', '！', '＜', '＞', '／', '＼', '［', '］', '｛', '｝', '＝', '｀', '＿',
        '꞉', 'ː',
        '\u2018', '\u2019', '"', '"',
        '•', '·',
        '【', '】', '「', '」', '『', '』', '《', '》',
        '～', '«', '»', '“', '”', '＂', '＄', '％', '＆', '＇', '（', '）', '＊', '＋', '，', '－', '．']);

    public static string ReplaceSpecialChars(this string str, string replaceStr)
    {
        if (str.Length == 0)
            return str;

        int first = str.AsSpan().IndexOfAny(s_special);
        if (first < 0)
            return str;

        if (replaceStr.Length == 1)
        {
            char rc = replaceStr[0];
            return string.Create(str.Length, (str, rc, first), static (dst, s) =>
            {
                s.str.AsSpan().CopyTo(dst);
                dst[s.first] = s.rc;
                for (int i = s.first + 1; i < dst.Length; i++)
                    if (s_special.Contains(dst[i]))
                        dst[i] = s.rc;
            });
        }

        if (replaceStr.Length == 0)
        {
            int keep = first;
            for (int i = first; i < str.Length; i++)
                if (!s_special.Contains(str[i])) keep++;

            return string.Create(keep, (str, first), static (dst, s) =>
            {
                s.str.AsSpan()[..s.first].CopyTo(dst);
                int di = s.first;
                for (int i = s.first; i < s.str.Length; i++)
                    if (!s_special.Contains(s.str[i]))
                        dst[di++] = s.str[i];
            });
        }

        var sb = new StringBuilder(str.Length * 2);
        sb.Append(str, 0, first);
        sb.Append(replaceStr);
        for (int i = first + 1; i < str.Length; i++)
        {
            if (s_special.Contains(str[i])) sb.Append(replaceStr);
            else sb.Append(str[i]);
        }
        return sb.ToString();
    }

    public static bool IsSpecialChar(char c) => s_special.Contains(c);

    public static string RemoveFt(this string str, bool removeParentheses = true)
    {
        if (str.Length == 0)
            return str;

        int ftIndex = str.IndexOf("feat.", StringComparison.OrdinalIgnoreCase);
        if (ftIndex == -1)
            ftIndex = str.IndexOf("ft.", StringComparison.OrdinalIgnoreCase);
        if (ftIndex == -1)
            return str;

        if (removeParentheses && ftIndex > 0)
        {
            char closeChar = str[ftIndex - 1] switch
            {
                '(' => ')',
                '[' => ']',
                _ => '\0',
            };

            if (closeChar != '\0')
            {
                int openIdx = ftIndex - 1;
                int closeIdx = str.IndexOf(closeChar, ftIndex);
                if (closeIdx != -1)
                {
                    int add = 0;
                    if (openIdx > 0 && closeIdx < str.Length - 1 && char.IsWhiteSpace(str[openIdx - 1]) && char.IsWhiteSpace(str[closeIdx + 1]))
                        add = 1;
                    return str.Remove(openIdx, closeIdx - openIdx + 1 + add).TrimEnd();
                }
            }
        }

        var separatorIdx = str.IndexOf(" - ", ftIndex);
        return separatorIdx != -1
            ? str.Remove(ftIndex, separatorIdx - ftIndex + 1).TrimEnd()
            : str[..ftIndex].TrimEnd();
    }

    public static string RemoveConsecutiveWs(this string input)
    {
        if (input.Length == 0)
            return string.Empty;

        char[]? buffer = null;
        int index = 0;
        bool previousWhitespace = false;

        for (int i = 0; i < input.Length; i++)
        {
            char ch = input[i];
            bool isWhitespace = char.IsWhiteSpace(ch);

            if (isWhitespace && previousWhitespace)
            {
                if (buffer == null)
                {
                    buffer = input.ToCharArray();
                    index = i;
                }
                continue;
            }

            if (buffer != null)
                buffer[index++] = ch;

            previousWhitespace = isWhitespace;
        }

        return buffer == null ? input : new string(buffer, 0, index);
    }

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex SquareBracketsRegex();

    public static string RemoveSquareBrackets(this string str)
    {
        if (str.Length == 0)
            return str;
        if (!str.Contains('['))
            return str;
        return SquareBracketsRegex().Replace(str, "").Trim();
    }

    public static bool ContainsIgnoreCase(this string s, string other)
    {
        return s.Contains(other, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBoundaryChar(char c)
        => c is '-' or '|' or '.' or '\\' or '/' or '_' or '—' or '–' or '―'
            or '(' or ')' or '[' or ']' or '{' or '}' or '<' or '>'
            or '「' or '」' or '【' or '】' or '『' or '』' or '《' or '》'
            or ',' or ':' or '?' or '!' or ';'
            or '@' or '#' or '*' or '=' or '+'
            or '\'' or '"' or '\u2018' or '\u2019' or '\u201C' or '\u201D'
            or '$' or '^' or '&' or '`' or '~' or '～' or '%'
            or '•' or '·' or '«' or '»';

    public static bool ContainsWithBoundary(this string str, string? value, bool ignoreCase = false)
    {
        if (string.IsNullOrEmpty(value))
            return true;
        if (str.Length == 0)
            return false;

        var comp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        int index = 0;
        while ((index = str.IndexOf(value, index, comp)) != -1)
        {
            bool hasLeftBoundary = index == 0 || char.IsWhiteSpace(str[index - 1]) || IsBoundaryChar(str[index - 1]);
            bool hasRightBoundary = index + value.Length >= str.Length || char.IsWhiteSpace(str[index + value.Length]) || IsBoundaryChar(str[index + value.Length]);

            if (hasLeftBoundary && hasRightBoundary)
                return true;

            index++;
        }

        return false;
    }

    public static bool ContainsWithBoundaryIgnoreWs(this string str, string? value, bool ignoreCase = false, bool acceptLeftDigit = false)
    {
        if (string.IsNullOrEmpty(value))
            return true;
        if (str.Length == 0)
            return false;

        var comp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        int index = 0;
        while ((index = str.IndexOf(value, index, comp)) != -1)
        {
            int leftIndex = index - 1;
            while (leftIndex >= 0 && char.IsWhiteSpace(str[leftIndex]))
                leftIndex--;

            bool hasLeftBoundary = leftIndex < 0 || acceptLeftDigit && leftIndex < index - 1 && char.IsDigit(str[leftIndex]) || IsBoundaryChar(str[leftIndex]);

            int rightIndex = index + value.Length;
            while (rightIndex < str.Length && char.IsWhiteSpace(str[rightIndex]))
                rightIndex++;

            bool hasRightBoundary = rightIndex >= str.Length || IsBoundaryChar(str[rightIndex]);

            if (hasLeftBoundary && hasRightBoundary)
                return true;

            index++;
        }

        return false;
    }

    [GeneratedRegex(@"\[(.*?)\]|\((.*?)\)")]
    private static partial Regex BracketsRegex();

    public static bool ContainsInBrackets(this string str, string searchTerm, bool ignoreCase = false)
    {
        if (str.Length == 0 && searchTerm.Length > 0)
            return false;

        var matches = BracketsRegex().Matches(str);
        var comp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        foreach (Match match in matches)
        {
            if (match.Value.Contains(searchTerm, comp))
                return true;
        }

        return false;
    }

    public static bool ContainsInBracketsOptimized(this string str, string searchTerm, bool ignoreCase = false)
    {
        if (str.Length == 0 && searchTerm.Length > 0)
            return false;

        var comp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        int depth = 0;
        int searchTermLen = searchTerm.Length;

        for (int i = 0; i < str.Length; i++)
        {
            char c = str[i];

            if (c == '[' || c == '(')
            {
                depth++;
            }
            else if (c == ']' || c == ')')
            {
                depth--;
            }

            if (depth > 0 && i + searchTermLen <= str.Length)
            {
                if (str.Substring(i, searchTermLen).Equals(searchTerm, comp))
                    return true;
            }
        }

        return false;
    }

    public static Dictionary<K, V> ToSafeDictionary<T, K, V>(this IEnumerable<T> source, Func<T, K> keySelector, Func<T, V> valSelector) where K : notnull
    {
        var d = new Dictionary<K, V>();
        foreach (var element in source)
        {
            if (!d.ContainsKey(keySelector(element)))
                d.Add(keySelector(element), valSelector(element));
        }
        return d;
    }

    public static int IndexOf<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
    {
        var index = 0;
        foreach (var item in source)
        {
            if (predicate.Invoke(item))
            {
                return index;
            }
            index++;
        }

        return -1;
    }

    public static int Levenshtein(string source, string target)
    {
        if (source.Length == 0)
            return target.Length;
        if (target.Length == 0)
            return source.Length;

        if (source.Length > target.Length)
            (source, target) = (target, source);

        var previousRow = new int[source.Length + 1];
        for (var i = 0; i <= source.Length; i++)
            previousRow[i] = i;

        for (var j = 1; j <= target.Length; j++)
        {
            int previousDiagonal = previousRow[0];
            previousRow[0] = j;

            for (var i = 1; i <= source.Length; i++)
            {
                int deletion = previousRow[i] + 1;
                int insertion = previousRow[i - 1] + 1;
                int substitution = previousDiagonal + (source[i - 1] == target[j - 1] ? 0 : 1);

                previousDiagonal = previousRow[i];
                previousRow[i] = Math.Min(Math.Min(deletion, insertion), substitution);
            }
        }

        return previousRow[source.Length];
    }

    public static string GreatestCommonPath(IEnumerable<string> paths)
    {
        string? path = paths.FirstOrDefault();

        if (path == null || path.Length == 0)
            return "";

        static int commonPathIndex(string path1, string path2, int maxIndex)
        {
            var minLength = Math.Min(path1.Length, Math.Min(path2.Length, maxIndex));
            var commonPathLength = 0;
            for (int i = 0; i < minLength; i++)
            {
                if ((path1[i] == '/' || path1[i] == '\\') && (path2[i] == '/' || path2[i] == '\\'))
                    commonPathLength = i + 1;
                else if (path1[i] != path2[i])
                    break;
            }
            return commonPathLength;
        }

        int index = path.Length;

        foreach (var p in paths.Skip(1))
            index = commonPathIndex(path, p, index);

        return path[..index];
    }

    public static string GreatestCommonDirectory(IEnumerable<string> paths)
    {
        if (paths.Skip(1).Any())
            return NormalizedPath(GreatestCommonPath(paths));
        else
            return NormalizedPath(Path.GetDirectoryName(paths.First().TrimEnd('/').TrimEnd('\\')) ?? "");
    }

    public static string GreatestCommonDirectorySlsk(IEnumerable<string> paths)
    {
        if (paths.Skip(1).Any())
            return Utils.GreatestCommonPath(paths).Replace('/', '\\').TrimEnd('\\');
        else
            return Utils.GetDirectoryNameSlsk(paths.First()).Replace('/', '\\').TrimEnd('\\');
    }

    public static string NormalizedPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        path = path.Replace('\\', '/').TrimEnd('/').Trim();

        while (path.Contains("//"))
            path = path.Replace("//", "/");

        return path;
    }

    public static bool IsInDirectory(string path, string dir, bool strict)
    {
        path = NormalizedPath(path);
        dir = NormalizedPath(dir);
        return strict ? path.StartsWith(dir + '/') : path.StartsWith(dir);
    }

    public static (string? Artist, string Title) SplitArtistAndTitle(string inputTitle)
    {
        if (string.IsNullOrEmpty(inputTitle))
        {
            return (null, inputTitle);
        }

        const string separator = " - ";
        inputTitle = inputTitle.Replace(" — ", separator).Replace(" -- ", separator);

        int separatorLength = separator.Length;
        int lastValidSeparatorIndex = -1;
        int validSeparatorCount = 0;
        int parenLevel = 0;
        int bracketLevel = 0;

        for (int i = 0; i < inputTitle.Length; i++)
        {
            char c = inputTitle[i];

            bool isPotentialSeparator = (i <= inputTitle.Length - separatorLength) &&
                                        string.CompareOrdinal(inputTitle, i, separator, 0, separatorLength) == 0;

            if (isPotentialSeparator && parenLevel == 0 && bracketLevel == 0)
            {
                validSeparatorCount++;
                lastValidSeparatorIndex = i;
                if (validSeparatorCount > 1) break;
                i += separatorLength - 1;
                continue;
            }

            if (c == '(') parenLevel++;
            else if (c == ')') parenLevel = Math.Max(0, parenLevel - 1);
            else if (c == '[') bracketLevel++;
            else if (c == ']') bracketLevel = Math.Max(0, bracketLevel - 1);
        }

        if (validSeparatorCount == 1)
        {
            string potentialArtist = inputTitle[..lastValidSeparatorIndex].Trim();
            string potentialTitle = inputTitle[(lastValidSeparatorIndex + separatorLength)..].Trim();

            if (potentialArtist.Length > 0 && potentialTitle.Length > 0)
            {
                return (potentialArtist, potentialTitle);
            }
        }

        return (null, inputTitle);
    }

    public static bool SequenceEqualUpToPermutation<T>(this IEnumerable<T> list1, IEnumerable<T> list2) where T : notnull
    {
        var cnt = new Dictionary<T, int>();
        foreach (T s in list1)
        {
            if (cnt.TryGetValue(s, out int count))
                cnt[s] = count + 1;
            else
                cnt[s] = 1;
        }
        foreach (T s in list2)
        {
            if (cnt.TryGetValue(s, out int count))
                cnt[s] = count - 1;
            else
                return false;
        }
        return cnt.Values.All(c => c == 0);
    }

    public static bool RemoveDiacriticsIfExist(this string s, out string res)
    {
        res = s.RemoveDiacritics();
        return !ReferenceEquals(res, s);
    }

    public static char RemoveDiacritics(this char c)
    {
        if (diacriticChars.TryGetValue(c, out var res)) return res;
        return c;
    }

    public static string RemoveDiacritics(this string s)
    {
        if (s.Length == 0)
            return s;

        int first = -1;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c >= 128 && diacriticChars.ContainsKey(c))
            {
                first = i;
                break;
            }
        }

        if (first < 0)
            return s;

        return string.Create(s.Length, (s, first), static (dst, state) =>
        {
            state.s.AsSpan().CopyTo(dst);

            if (diacriticChars.TryGetValue(dst[state.first], out char firstReplacement))
                dst[state.first] = firstReplacement;

            for (int i = state.first + 1; i < dst.Length; i++)
                if (dst[i] >= 128 && diacriticChars.TryGetValue(dst[i], out char replacement))
                    dst[i] = replacement;
        });
    }

    static readonly Dictionary<char, char> diacriticChars = new()
    {
        { 'ä', 'a' }, { 'æ', 'a' }, { 'ǽ', 'a' }, { 'œ', 'o' }, { 'ö', 'o' }, { 'ü', 'u' },
        { 'Ä', 'A' }, { 'Ü', 'U' }, { 'Ö', 'O' }, { 'À', 'A' }, { 'Á', 'A' }, { 'Â', 'A' },
        { 'Ã', 'A' }, { 'Å', 'A' }, { 'Ǻ', 'A' }, { 'Ā', 'A' }, { 'Ă', 'A' }, { 'Ą', 'A' },
        { 'Ǎ', 'A' }, { 'Ά', 'A' }, { 'Ả', 'A' }, { 'Ạ', 'A' }, { 'Ầ', 'A' }, { 'Ấ', 'A' },
        { 'Ẫ', 'A' }, { 'Ẩ', 'A' }, { 'Ậ', 'A' }, { 'à', 'a' }, { 'á', 'a' }, { 'â', 'a' },
        { 'ã', 'a' }, { 'å', 'a' }, { 'ǻ', 'a' }, { 'ā', 'a' }, { 'ă', 'a' }, { 'ą', 'a' },
        { 'ǎ', 'a' }, { 'ả', 'a' }, { 'ạ', 'a' }, { 'Ç', 'C' }, { 'Ć', 'C' }, { 'Ĉ', 'C' },
        { 'Ċ', 'C' }, { 'Č', 'C' }, { 'ç', 'c' }, { 'ć', 'c' }, { 'ĉ', 'c' }, { 'ċ', 'c' },
        { 'č', 'c' }, { 'Ð', 'D' }, { 'Ď', 'D' }, { 'Đ', 'D' }, { 'ð', 'd' }, { 'ď', 'd' },
        { 'đ', 'd' }, { 'È', 'E' }, { 'É', 'E' }, { 'Ê', 'E' }, { 'Ë', 'E' }, { 'Ē', 'E' },
        { 'Ĕ', 'E' }, { 'Ė', 'E' }, { 'Ę', 'E' }, { 'Ě', 'E' }, { 'Έ', 'E' }, { 'Ẽ', 'E' },
        { 'Ẻ', 'E' }, { 'Ẹ', 'E' }, { 'Ề', 'E' }, { 'Ế', 'E' }, { 'Ễ', 'E' }, { 'Ể', 'E' },
        { 'Ệ', 'E' }, { 'è', 'e' }, { 'é', 'e' }, { 'ê', 'e' }, { 'ë', 'e' }, { 'ē', 'e' },
        { 'ĕ', 'e' }, { 'ė', 'e' }, { 'ę', 'e' }, { 'ě', 'e' }, { 'ẽ', 'e' }, { 'ẻ', 'e' },
        { 'ẹ', 'e' }, { 'Ĝ', 'G' }, { 'Ğ', 'G' }, { 'Ġ', 'G' }, { 'Ģ', 'G' }, { 'ĝ', 'g' },
        { 'ğ', 'g' }, { 'ġ', 'g' }, { 'ģ', 'g' }, { 'Ĥ', 'H' }, { 'Ħ', 'H' }, { 'Ή', 'H' },
        { 'ĥ', 'h' }, { 'ħ', 'h' }, { 'Ì', 'I' }, { 'Í', 'I' }, { 'Î', 'I' }, { 'Ï', 'I' },
        { 'Ĩ', 'I' }, { 'Ī', 'I' }, { 'Ĭ', 'I' }, { 'Ǐ', 'I' }, { 'Į', 'I' }, { 'İ', 'I' },
        { 'Ί', 'I' }, { 'Ϊ', 'I' }, { 'Ỉ', 'I' }, { 'Ị', 'I' }, { 'Ї', 'I' }, { 'ì', 'i' },
        { 'í', 'i' }, { 'î', 'i' }, { 'ï', 'i' }, { 'ĩ', 'i' }, { 'ī', 'i' }, { 'ĭ', 'i' },
        { 'ǐ', 'i' }, { 'į', 'i' }, { 'ı', 'i' }, { 'ΰ', 'y' }, { 'Ĵ', 'J' }, { 'ĵ', 'j' },
        { 'Ķ', 'K' }, { 'ķ', 'k' }, { 'Ĺ', 'L' }, { 'Ļ', 'L' }, { 'Ľ', 'L' }, { 'Ŀ', 'L' },
        { 'Ł', 'L' }, { 'ĺ', 'l' }, { 'ļ', 'l' }, { 'ľ', 'l' }, { 'ŀ', 'l' }, { 'ł', 'l' },
        { 'Ñ', 'N' }, { 'Ń', 'N' }, { 'Ņ', 'N' }, { 'Ň', 'N' }, { 'ñ', 'n' }, { 'ń', 'n' },
        { 'ņ', 'n' }, { 'ň', 'n' }, { 'ŉ', 'n' }, { 'Ò', 'O' }, { 'Ó', 'O' }, { 'Ô', 'O' },
        { 'Õ', 'O' }, { 'Ō', 'O' }, { 'Ŏ', 'O' }, { 'Ǒ', 'O' }, { 'Ő', 'O' }, { 'Ơ', 'O' },
        { 'Ø', 'O' }, { 'Ǿ', 'O' }, { 'Ό', 'O' }, { 'Ỏ', 'O' }, { 'Ọ', 'O' }, { 'Ồ', 'O' },
        { 'Ố', 'O' }, { 'Ỗ', 'O' }, { 'Ổ', 'O' }, { 'Ộ', 'O' }, { 'Ờ', 'O' }, { 'Ớ', 'O' },
        { 'Ỡ', 'O' }, { 'Ở', 'O' }, { 'Ợ', 'O' }, { 'ò', 'o' }, { 'ó', 'o' }, { 'ô', 'o' },
        { 'õ', 'o' }, { 'ō', 'o' }, { 'ŏ', 'o' }, { 'ǒ', 'o' }, { 'ő', 'o' }, { 'ơ', 'o' },
        { 'ø', 'o' }, { 'ǿ', 'o' }, { 'º', 'o' }, { 'ό', 'o' }, { 'ỏ', 'o' }, { 'ọ', 'o' },
        { 'ồ', 'o' }, { 'ố', 'o' }, { 'ỗ', 'o' }, { 'ổ', 'o' }, { 'ộ', 'o' }, { 'ờ', 'o' },
        { 'ớ', 'o' }, { 'ỡ', 'o' }, { 'ở', 'o' }, { 'ợ', 'o' }, { 'Ŕ', 'R' }, { 'Ŗ', 'R' },
        { 'Ř', 'R' }, { 'ŕ', 'r' }, { 'ŗ', 'r' }, { 'ř', 'r' }, { 'Ś', 'S' }, { 'Ŝ', 'S' },
        { 'Ş', 'S' }, { 'Ș', 'S' }, { 'Š', 'S' }, { 'ś', 's' }, { 'ŝ', 's' }, { 'ş', 's' },
        { 'ș', 's' }, { 'š', 's' }, { 'Ț', 'T' }, { 'Ţ', 'T' }, { 'Ť', 'T' }, { 'Ŧ', 'T' },
        { 'Т', 'T' }, { 'ț', 't' }, { 'ţ', 't' }, { 'ť', 't' }, { 'ŧ', 't' }, { 'Ù', 'U' },
        { 'Ú', 'U' }, { 'Û', 'U' }, { 'Ũ', 'U' }, { 'Ū', 'U' }, { 'Ŭ', 'U' }, { 'Ů', 'U' },
        { 'Ű', 'U' }, { 'Ų', 'U' }, { 'Ư', 'U' }, { 'Ǔ', 'U' }, { 'Ǖ', 'U' }, { 'Ǘ', 'U' },
        { 'Ǚ', 'U' }, { 'Ǜ', 'U' }, { 'Ủ', 'U' }, { 'Ụ', 'U' }, { 'Ừ', 'U' }, { 'ё', 'e' },
        { 'Ứ', 'U' }, { 'Ữ', 'U' }, { 'Ử', 'U' }, { 'Ự', 'U' }, { 'ù', 'u' }, { 'ú', 'u' },
        { 'û', 'u' }, { 'ũ', 'u' }, { 'ū', 'u' }, { 'ŭ', 'u' }, { 'ů', 'u' }, { 'ű', 'u' },
        { 'ų', 'u' }, { 'ư', 'u' }, { 'ǔ', 'u' }, { 'ǖ', 'u' }, { 'ǘ', 'u' }, { 'ǚ', 'u' },
        { 'ǜ', 'u' }, { 'ủ', 'u' }, { 'ụ', 'u' }, { 'ừ', 'u' }, { 'ứ', 'u' }, { 'ữ', 'u' },
        { 'ử', 'u' }, { 'ự', 'u' }, { 'Ý', 'Y' }, { 'Ÿ', 'Y' }, { 'Ŷ', 'Y' }, { 'Ύ', 'Y' },
        { 'Ϋ', 'Y' }, { 'Ỳ', 'Y' }, { 'Ỹ', 'Y' }, { 'Ỷ', 'Y' }, { 'Ỵ', 'Y' }, { 'й', 'и' },
        { 'ý', 'y' }, { 'ÿ', 'y' }, { 'ŷ', 'y' }, { 'ỳ', 'y' }, { 'ỹ', 'y' }, { 'ỷ', 'y' },
        { 'ỵ', 'y' }, { 'Ŵ', 'W' }, { 'ŵ', 'w' }, { 'Ź', 'Z' }, { 'Ż', 'Z' }, { 'Ž', 'Z' },
        { 'ź', 'z' }, { 'ż', 'z' }, { 'ž', 'z' }, { 'Æ', 'A' }, { 'ß', 's' }, { 'Œ', 'O' },
        { 'Ё', 'E' },
    };
}
