using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace AgentReadonly.Services
{
    public class ReadOnlyTools
    {
        private const int DefaultReadLimit = 256 * 1024;
        private const int DefaultSearchLimit = 100;
        private const int MaxFileForSearch = 5 * 1024 * 1024;

        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public ReadOnlyTools(string root)
        {
            Root = Path.GetFullPath(root);
        }

        public string Root { get; private set; }

        public List<Dictionary<string, object>> Definitions()
        {
            return new List<Dictionary<string, object>>
            {
                FunctionTool("list_files", "List files and directories under the selected codebase folder.",
                    ObjectSchema(new Dictionary<string, object>
                    {
                        { "path", StringProp("Directory path relative to the selected codebase folder.") },
                        { "max_entries", NumberProp("Maximum number of entries to return.") }
                    }, new string[0])),
                FunctionTool("read_file", "Read a text file under the selected codebase folder. Optional line slicing is supported.",
                    ObjectSchema(new Dictionary<string, object>
                    {
                        { "path", StringProp("File path relative to the selected codebase folder.") },
                        { "start_line", NumberProp("1-based first line to read.") },
                        { "max_lines", NumberProp("Maximum number of lines to return.") },
                        { "max_bytes", NumberProp("Maximum bytes to read.") }
                    }, new[] { "path" })),
                FunctionTool("search_text", "Search text files recursively under the selected codebase folder.",
                    ObjectSchema(new Dictionary<string, object>
                    {
                        { "pattern", StringProp("Substring or regular expression to search for.") },
                        { "path", StringProp("Directory path relative to the selected codebase folder.") },
                        { "regex", BoolProp("Treat pattern as a regular expression.") },
                        { "max_results", NumberProp("Maximum number of matches to return.") }
                    }, new[] { "pattern" }))
            };
        }

        public string Run(string name, string argumentsJson)
        {
            Dictionary<string, object> args = DecodeArgs(argumentsJson);
            switch (name)
            {
                case "list_files":
                    return ListFiles(args);
                case "read_file":
                    return ReadFile(args);
                case "search_text":
                    return SearchText(args);
                default:
                    throw new InvalidOperationException("Unknown readonly tool: " + name);
            }
        }

        private Dictionary<string, object> DecodeArgs(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, object>();

            object parsed = serializer.DeserializeObject(json);
            string encoded = parsed as string;
            if (encoded != null)
                parsed = serializer.DeserializeObject(encoded);

            Dictionary<string, object> map = parsed as Dictionary<string, object>;
            if (map == null)
                throw new InvalidOperationException("Tool arguments were not a JSON object.");
            return map;
        }

        private string ListFiles(Dictionary<string, object> args)
        {
            string dir = Resolve(GetString(args, "path"));
            if (!Directory.Exists(dir))
                throw new DirectoryNotFoundException(dir);

            int limit = GetInt(args, "max_entries", 200);
            if (limit <= 0 || limit > 1000)
                limit = 200;

            List<string> entries = new List<string>();
            foreach (string path in Directory.GetFileSystemEntries(dir))
                entries.Add(Path.GetFileName(path) + (Directory.Exists(path) ? "/" : ""));
            entries.Sort(StringComparer.OrdinalIgnoreCase);

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < entries.Count && i < limit; i++)
                builder.AppendLine(entries[i]);
            if (entries.Count > limit)
                builder.AppendLine("... truncated after " + limit + " entries");
            return builder.ToString();
        }

        private string ReadFile(Dictionary<string, object> args)
        {
            string requestedPath = GetString(args, "path");
            if (string.IsNullOrWhiteSpace(requestedPath))
                throw new InvalidOperationException("path is required");

            string path = Resolve(requestedPath);
            if (!File.Exists(path))
                throw new FileNotFoundException(path);

            int maxBytes = GetInt(args, "max_bytes", DefaultReadLimit);
            if (maxBytes <= 0 || maxBytes > DefaultReadLimit)
                maxBytes = DefaultReadLimit;

            byte[] data = File.ReadAllBytes(path);
            bool truncated = data.Length > maxBytes;
            if (truncated)
            {
                byte[] limited = new byte[maxBytes];
                Buffer.BlockCopy(data, 0, limited, 0, maxBytes);
                data = limited;
            }

            string text = DecodeText(data);
            int startLine = GetInt(args, "start_line", 0);
            int maxLines = GetInt(args, "max_lines", 0);
            if (startLine > 0 || maxLines > 0)
                text = SliceLines(text, Math.Max(1, startLine), maxLines <= 0 ? 200 : maxLines);

            if (truncated)
                text += Environment.NewLine + "... truncated at " + maxBytes + " bytes";
            return text;
        }

        private string SearchText(Dictionary<string, object> args)
        {
            string pattern = GetString(args, "pattern");
            if (string.IsNullOrEmpty(pattern))
                throw new InvalidOperationException("pattern is required");

            string baseDir = Resolve(GetString(args, "path"));
            if (!Directory.Exists(baseDir))
                throw new DirectoryNotFoundException(baseDir);

            int limit = GetInt(args, "max_results", DefaultSearchLimit);
            if (limit <= 0 || limit > 1000)
                limit = DefaultSearchLimit;

            bool regex = GetBool(args, "regex", false);
            Regex compiled = null;
            if (regex)
                compiled = new Regex(pattern, RegexOptions.Compiled);

            List<string> results = new List<string>();
            foreach (string file in EnumerateFilesSafe(baseDir))
            {
                if (results.Count >= limit)
                    break;
                if (!IsTextLike(file))
                    continue;

                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                }
                catch
                {
                    continue;
                }

                if (info.Length > MaxFileForSearch || LooksBinary(file))
                    continue;

                string[] lines;
                try
                {
                    lines = DecodeText(File.ReadAllBytes(file)).Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
                }
                catch
                {
                    continue;
                }

                for (int i = 0; i < lines.Length && results.Count < limit; i++)
                {
                    string line = lines[i];
                    bool matched = compiled != null ? compiled.IsMatch(line) : line.IndexOf(pattern, StringComparison.Ordinal) >= 0;
                    if (matched)
                        results.Add(RelativePath(file) + ":" + (i + 1) + ":" + line);
                }
            }

            return results.Count == 0 ? "no matches" : string.Join(Environment.NewLine, results.ToArray());
        }

        private IEnumerable<string> EnumerateFilesSafe(string start)
        {
            Stack<string> stack = new Stack<string>();
            stack.Push(start);

            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                string[] subdirs;
                string[] files;
                try
                {
                    subdirs = Directory.GetDirectories(dir);
                    files = Directory.GetFiles(dir);
                }
                catch
                {
                    continue;
                }

                foreach (string file in files)
                    yield return file;

                foreach (string subdir in subdirs)
                {
                    string name = Path.GetFileName(subdir);
                    if (ShouldSkipDirectory(name))
                        continue;
                    stack.Push(subdir);
                }
            }
        }

        private bool ShouldSkipDirectory(string name)
        {
            return string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "vendor", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "target", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "dist", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "build", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase);
        }

        private string Resolve(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                path = ".";

            string candidate = Path.IsPathRooted(path) ? path : Path.Combine(Root, path);
            string full = Path.GetFullPath(candidate);
            string rootWithSlash = Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            if (!string.Equals(full, Root, StringComparison.OrdinalIgnoreCase)
                && !full.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("path escapes selected codebase folder: " + path);

            return full;
        }

        private string RelativePath(string path)
        {
            Uri rootUri = new Uri(Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar);
            Uri fileUri = new Uri(path);
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(fileUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        private bool IsTextLike(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".exe":
                case ".dll":
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".gif":
                case ".zip":
                case ".tar":
                case ".gz":
                case ".7z":
                case ".pdf":
                case ".ico":
                    return false;
                default:
                    return true;
            }
        }

        private bool LooksBinary(string path)
        {
            try
            {
                byte[] data = File.ReadAllBytes(path);
                int limit = Math.Min(data.Length, 8192);
                for (int i = 0; i < limit; i++)
                    if (data[i] == 0)
                        return true;
            }
            catch
            {
                return true;
            }
            return false;
        }

        private string DecodeText(byte[] data)
        {
            try
            {
                return new UTF8Encoding(false, true).GetString(data);
            }
            catch
            {
                return Encoding.Default.GetString(data);
            }
        }

        private string SliceLines(string text, int startLine, int maxLines)
        {
            string[] lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            int start = Math.Max(0, startLine - 1);
            if (start >= lines.Length)
                return "";

            int count = Math.Min(maxLines, lines.Length - start);
            string[] selected = new string[count];
            Array.Copy(lines, start, selected, 0, count);
            return string.Join(Environment.NewLine, selected);
        }

        private string GetString(Dictionary<string, object> args, string name)
        {
            object value;
            return args.TryGetValue(name, out value) && value != null ? Convert.ToString(value) : "";
        }

        private int GetInt(Dictionary<string, object> args, string name, int defaultValue)
        {
            object value;
            if (!args.TryGetValue(name, out value) || value == null)
                return defaultValue;
            try
            {
                return Convert.ToInt32(value);
            }
            catch
            {
                return defaultValue;
            }
        }

        private bool GetBool(Dictionary<string, object> args, string name, bool defaultValue)
        {
            object value;
            if (!args.TryGetValue(name, out value) || value == null)
                return defaultValue;
            try
            {
                return Convert.ToBoolean(value);
            }
            catch
            {
                return defaultValue;
            }
        }

        private Dictionary<string, object> FunctionTool(string name, string description, Dictionary<string, object> parameters)
        {
            return new Dictionary<string, object>
            {
                { "type", "function" },
                { "name", name },
                { "description", description },
                { "parameters", parameters },
                { "strict", false }
            };
        }

        private Dictionary<string, object> ObjectSchema(Dictionary<string, object> properties, string[] required)
        {
            return new Dictionary<string, object>
            {
                { "type", "object" },
                { "properties", properties },
                { "required", required },
                { "additionalProperties", false }
            };
        }

        private Dictionary<string, object> StringProp(string description)
        {
            return new Dictionary<string, object> { { "type", "string" }, { "description", description } };
        }

        private Dictionary<string, object> NumberProp(string description)
        {
            return new Dictionary<string, object> { { "type", "integer" }, { "description", description } };
        }

        private Dictionary<string, object> BoolProp(string description)
        {
            return new Dictionary<string, object> { { "type", "boolean" }, { "description", description } };
        }
    }
}
