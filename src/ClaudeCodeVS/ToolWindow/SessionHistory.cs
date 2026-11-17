using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace ClaudeCodeVS.ToolWindow
{
    internal sealed class SessionInfo
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public DateTime LastActivity { get; set; }
    }

    /// <summary>
    /// Reads the Claude Code session transcripts stored for a working directory:
    /// &lt;config&gt;\projects\&lt;encoded working directory&gt;\&lt;session id&gt;.jsonl
    /// The transcript format is internal to Claude Code, so parsing is defensive:
    /// anything unexpected is skipped instead of failing.
    /// </summary>
    internal static class SessionHistory
    {
        private const int MaxParsedLineLength = 2000000;
        private const int MaxTitleLength = 90;

        public static List<SessionInfo> Load(string workingDirectory, int maxSessions)
        {
            var sessions = new List<SessionInfo>();
            string projectDirectory = FindProjectDirectory(workingDirectory);
            if (projectDirectory == null)
            {
                return sessions;
            }

            IEnumerable<FileInfo> files = new DirectoryInfo(projectDirectory)
                .GetFiles("*.jsonl")
                .OrderByDescending(f => f.LastWriteTime);

            foreach (FileInfo file in files)
            {
                if (sessions.Count >= maxSessions)
                {
                    break;
                }

                string id = Path.GetFileNameWithoutExtension(file.Name);
                Guid parsed;
                if (!Guid.TryParse(id, out parsed))
                {
                    continue; // only plain session ids are safe to pass on a command line
                }

                string title = null;
                try
                {
                    title = ReadTitle(file.FullName);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                if (title == null)
                {
                    // No prompt found: an empty session, or a format this version does not know.
                    if (file.Length < 2048)
                    {
                        continue;
                    }

                    title = "(untitled chat)";
                }

                sessions.Add(new SessionInfo { Id = id, Title = title, LastActivity = file.LastWriteTime });
            }

            return sessions;
        }

        private static string FindProjectDirectory(string workingDirectory)
        {
            if (string.IsNullOrEmpty(workingDirectory))
            {
                return null;
            }

            string configDirectory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(configDirectory))
            {
                configDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            }

            string projects = Path.Combine(configDirectory, "projects");
            if (!Directory.Exists(projects))
            {
                return null;
            }

            // Claude Code names the folder after the working directory, replacing every
            // non-alphanumeric character with '-'. Names longer than 200 characters are
            // truncated and get a hash suffix.
            string encoded = Regex.Replace(workingDirectory.TrimEnd('\\', '/'), "[^a-zA-Z0-9]", "-");
            string prefix = encoded.Length > 200 ? encoded.Substring(0, 200) : null;

            foreach (string directory in Directory.GetDirectories(projects))
            {
                string name = Path.GetFileName(directory);
                if (string.Equals(name, encoded, StringComparison.OrdinalIgnoreCase))
                {
                    return directory;
                }

                if (prefix != null && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return directory;
                }
            }

            return null;
        }

        private static string ReadTitle(string path)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string customTitle = null;
            string aiTitle = null;
            string summary = null;
            string firstPrompt = null;

            // FileShare.ReadWrite: the session may be running and appending right now.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0 || line.Length > MaxParsedLineLength)
                    {
                        continue;
                    }

                    bool hasCustomTitle = line.IndexOf("\"customTitle\"", StringComparison.Ordinal) >= 0;
                    bool hasAiTitle = line.IndexOf("\"aiTitle\"", StringComparison.Ordinal) >= 0;
                    bool hasSummary = line.IndexOf("\"type\":\"summary\"", StringComparison.Ordinal) >= 0;
                    bool isUser = firstPrompt == null &&
                                  line.IndexOf("\"type\":\"user\"", StringComparison.Ordinal) >= 0;

                    if (!hasCustomTitle && !hasAiTitle && !hasSummary && !isUser)
                    {
                        continue;
                    }

                    Dictionary<string, object> entry;
                    try
                    {
                        entry = serializer.Deserialize<Dictionary<string, object>>(line);
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }
                    catch (InvalidOperationException)
                    {
                        continue;
                    }

                    if (entry == null)
                    {
                        continue;
                    }

                    // Later entries win: a session can be renamed more than once.
                    customTitle = GetString(entry, "customTitle") ?? customTitle;
                    aiTitle = GetString(entry, "aiTitle") ?? aiTitle;
                    if (hasSummary)
                    {
                        summary = GetString(entry, "summary") ?? summary;
                    }

                    if (isUser && GetString(entry, "type") == "user")
                    {
                        firstPrompt = GetPromptText(entry);
                    }
                }
            }

            return Clean(customTitle) ?? Clean(aiTitle) ?? Clean(summary) ?? Clean(firstPrompt);
        }

        private static string GetPromptText(Dictionary<string, object> entry)
        {
            if (IsTrue(entry, "isMeta") || IsTrue(entry, "isSidechain"))
            {
                return null;
            }

            object value;
            if (!entry.TryGetValue("message", out value))
            {
                return null;
            }

            var message = value as IDictionary<string, object>;
            if (message == null || !message.TryGetValue("content", out value))
            {
                return null;
            }

            var text = value as string;
            if (text != null)
            {
                return IsRealPrompt(text) ? text : null;
            }

            var blocks = value as IEnumerable;
            if (blocks == null)
            {
                return null;
            }

            foreach (object block in blocks)
            {
                var item = block as IDictionary<string, object>;
                if (item == null || GetString(item, "type") != "text")
                {
                    continue;
                }

                string blockText = GetString(item, "text");
                if (IsRealPrompt(blockText))
                {
                    return blockText;
                }
            }

            return null;
        }

        /// <summary>Skips wrappers such as slash-command output and injected reminders.</summary>
        private static bool IsRealPrompt(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string trimmed = text.TrimStart();
            return !trimmed.StartsWith("<", StringComparison.Ordinal) &&
                   !trimmed.StartsWith("Caveat:", StringComparison.Ordinal);
        }

        private static string GetString(IDictionary<string, object> values, string key)
        {
            object value;
            return values.TryGetValue(key, out value) ? value as string : null;
        }

        private static bool IsTrue(IDictionary<string, object> values, string key)
        {
            object value;
            return values.TryGetValue(key, out value) && value is bool && (bool)value;
        }

        private static string Clean(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            string single = Regex.Replace(text, @"\s+", " ").Trim();
            return single.Length <= MaxTitleLength ? single : single.Substring(0, MaxTitleLength - 1) + "\u2026";
        }
    }
}
