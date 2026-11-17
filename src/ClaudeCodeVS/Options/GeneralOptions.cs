using System.ComponentModel;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS.Options
{
    /// <summary>Tools &gt; Options &gt; Claude Code &gt; General.</summary>
    public class GeneralOptions : DialogPage
    {
        private string _command = "claude";
        private string _arguments = string.Empty;
        private string _consoleFont = "Cascadia Mono";
        private int _consoleFontSize = 16;
        private bool _matchTheme = true;
        private bool _showStatusLine = true;

        [Category("Claude Code")]
        [DisplayName("Command")]
        [Description("Command or full path used to start the Claude Code CLI. Default: claude (resolved through PATH).")]
        [DefaultValue("claude")]
        public string Command
        {
            get { return _command; }
            set { _command = value; }
        }

        [Category("Claude Code")]
        [DisplayName("Arguments")]
        [Description("Extra arguments passed to the CLI, for example: --continue")]
        [DefaultValue("")]
        public string Arguments
        {
            get { return _arguments; }
            set { _arguments = value; }
        }

        [Category("Console")]
        [DisplayName("Font")]
        [Description("Font of the embedded console. It needs block-drawing characters; Cascadia Mono has them. Applied on the next New chat.")]
        [DefaultValue("Cascadia Mono")]
        public string ConsoleFont
        {
            get { return _consoleFont; }
            set { _consoleFont = value; }
        }

        [Category("Console")]
        [DisplayName("Font size")]
        [Description("Character height of the embedded console, in pixels. Applied on the next New chat.")]
        [DefaultValue(16)]
        public int ConsoleFontSize
        {
            get { return _consoleFontSize; }
            set { _consoleFontSize = value; }
        }

        [Category("Console")]
        [DisplayName("Match Visual Studio theme")]
        [Description("Use the tool window background and text colors of the current Visual Studio theme in the embedded console. Applied on the next New chat.")]
        [DefaultValue(true)]
        public bool MatchTheme
        {
            get { return _matchTheme; }
            set { _matchTheme = value; }
        }

        [Category("Claude Code")]
        [DisplayName("Show usage status line")]
        [Description("Show a line at the bottom of the console with the context window usage and, when the account reports them, the 5-hour and 7-day plan limits. Not applied when you already have a statusLine in your Claude Code settings. Applied on the next New chat.")]
        [DefaultValue(true)]
        public bool ShowStatusLine
        {
            get { return _showStatusLine; }
            set { _showStatusLine = value; }
        }
    }
}
