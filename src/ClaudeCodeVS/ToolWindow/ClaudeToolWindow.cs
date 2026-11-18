using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS.ToolWindow
{
    [Guid(PackageGuids.ToolWindowString)]
    public sealed class ClaudeToolWindow : ToolWindowPane
    {
        private readonly ClaudeTerminalControl _control;

        public ClaudeToolWindow() : base(null)
        {
            Caption = "Claude Code" + VersionSuffix;
            // The control can read and change the tab caption (the rename button).
            _control = new ClaudeTerminalControl(() => Caption, name => Caption = name);
            Content = _control;
        }

        private static string _versionSuffix;

        /// <summary>
        /// " v0.1.2" style suffix with the extension version, or an empty string when it
        /// cannot be read. The version comes from the extension.vsixmanifest installed next
        /// to the assembly, so it always matches what was built and published.
        /// </summary>
        internal static string VersionSuffix
        {
            get
            {
                if (_versionSuffix == null)
                {
                    _versionSuffix = string.Empty;
                    try
                    {
                        string folder = System.IO.Path.GetDirectoryName(typeof(ClaudeToolWindow).Assembly.Location);
                        string manifest = System.IO.Path.Combine(folder ?? string.Empty, "extension.vsixmanifest");
                        if (System.IO.File.Exists(manifest))
                        {
                            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
                                System.IO.File.ReadAllText(manifest), "<Identity[^>]*\\sVersion=\"([^\"]+)\"");
                            if (match.Success)
                            {
                                _versionSuffix = " v" + match.Groups[1].Value;
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // The version is informative only.
                    }
                }

                return _versionSuffix;
            }
        }

        /// <summary>Folder of the running session, or the folder a new session would use.</summary>
        internal string WorkingDirectory
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return _control.WorkingDirectory ?? ClaudeTerminalControl.GetWorkingDirectory();
            }
        }

        internal void SendText(string text)
        {
            _control.SendText(text);
        }

        /// <summary>
        /// Visual Studio shows every keyboard message to the active tool window first.
        /// Keys typed in the terminal are handed to it here; otherwise Visual Studio would
        /// take Esc, Ctrl+V and other keys as its own shortcuts.
        /// </summary>
        protected override bool PreProcessMessage(ref System.Windows.Forms.Message m)
        {
            if (_control.PreProcessKeyMessage(m.HWnd, m.Msg, m.WParam, m.LParam))
            {
                return true;
            }

            return base.PreProcessMessage(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _control.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
