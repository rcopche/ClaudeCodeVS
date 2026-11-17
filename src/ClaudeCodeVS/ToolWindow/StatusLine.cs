using System;
using System.IO;
using System.Text;

namespace ClaudeCodeVS.ToolWindow
{
    /// <summary>
    /// Gives the sessions started by this extension a Claude Code status line (the fixed
    /// line at the bottom of the console) showing context and plan usage.
    ///
    /// Claude Code runs a command after each response and shows what it prints. The command
    /// and the script it runs are written to %LOCALAPPDATA%\ClaudeCodeVS and handed to the
    /// CLI with --settings, so the user's own Claude Code settings files are not modified.
    /// </summary>
    internal static class StatusLine
    {
        // Windows PowerShell script. ASCII only: Windows PowerShell reads a script without
        // a byte order mark in the system code page.
        private const string Script = @"$ErrorActionPreference = 'SilentlyContinue'
$j = $input | Out-String | ConvertFrom-Json
$parts = @()

if ($j.model.display_name) { $parts += $j.model.display_name }

$cw = $j.context_window
if ($cw -and $cw.used_percentage -ne $null) {
    $used = [int][math]::Round([double]$cw.used_percentage)
    $filled = [math]::Max(0, [math]::Min(10, [int][math]::Floor($used / 10)))
    $bar = ('#' * $filled) + ('-' * (10 - $filled))
    $text = 'context [{0}] {1}%' -f $bar, $used
    if ($cw.total_input_tokens -and $cw.context_window_size) {
        $text += ' ({0:N0}k of {1:N0}k tokens)' -f ($cw.total_input_tokens / 1000), ($cw.context_window_size / 1000)
    }
    $parts += $text
} else {
    $parts += 'context --'
}

$rl = $j.rate_limits
if ($rl.five_hour -and $rl.five_hour.used_percentage -ne $null) {
    $parts += '5h limit {0:N0}%' -f [double]$rl.five_hour.used_percentage
}
if ($rl.seven_day -and $rl.seven_day.used_percentage -ne $null) {
    $parts += '7d limit {0:N0}%' -f [double]$rl.seven_day.used_percentage
}

Write-Host ($parts -join '  |  ')
";

        /// <summary>
        /// Writes the script and the settings file if needed and returns the settings file
        /// path, or null when the status line should not be added.
        /// </summary>
        public static string EnsureSettingsFile()
        {
            try
            {
                if (UserHasOwnStatusLine())
                {
                    return null;
                }

                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeCodeVS");
                Directory.CreateDirectory(folder);

                string scriptFile = Path.Combine(folder, "statusline.ps1");
                string settingsFile = Path.Combine(folder, "statusline-settings.json");

                // Forward slashes: the command may run through Git Bash, which treats
                // backslashes as escape characters.
                string scriptPath = scriptFile.Replace('\\', '/').Replace("\"", string.Empty);
                string json =
                    "{\"statusLine\":{\"type\":\"command\",\"command\":" +
                    "\"powershell -NoProfile -ExecutionPolicy Bypass -File \\\"" + scriptPath + "\\\"\"}}";

                WriteIfChanged(scriptFile, Script.Replace("\r\n", "\n").Replace("\n", "\r\n"));
                WriteIfChanged(settingsFile, json);
                return settingsFile;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (System.Security.SecurityException)
            {
                return null;
            }
        }

        /// <summary>A status line the user configured themselves always wins.</summary>
        private static bool UserHasOwnStatusLine()
        {
            string configDirectory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(configDirectory))
            {
                configDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            }

            string userSettings = Path.Combine(configDirectory, "settings.json");
            return File.Exists(userSettings) &&
                   File.ReadAllText(userSettings).IndexOf("\"statusLine\"", StringComparison.Ordinal) >= 0;
        }

        private static void WriteIfChanged(string path, string content)
        {
            var encoding = new UTF8Encoding(false);
            if (File.Exists(path) && File.ReadAllText(path, encoding) == content)
            {
                return;
            }

            File.WriteAllText(path, content, encoding);
        }
    }
}
