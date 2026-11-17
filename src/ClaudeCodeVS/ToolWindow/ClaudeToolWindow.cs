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
            Caption = "Claude Code";
            _control = new ClaudeTerminalControl();
            Content = _control;
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
