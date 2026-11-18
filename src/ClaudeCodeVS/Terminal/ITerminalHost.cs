using System;
using System.Threading.Tasks;
using System.Windows;

namespace ClaudeCodeVS.Terminal
{
    /// <summary>
    /// The terminal shown in the tool window. Two implementations exist: the modern one
    /// (Visual Studio's terminal control over a pseudo console) and the classic one
    /// (a re-parented console window), kept as a fallback.
    /// </summary>
    internal interface ITerminalHost : IDisposable
    {
        /// <summary>The element to place in the tool window.</summary>
        FrameworkElement View { get; }

        bool IsRunning { get; }

        /// <summary>
        /// A guess at whether the program is working right now: it keeps printing although
        /// nothing was typed. Used only to highlight the Stop button.
        /// </summary>
        bool IsBusy { get; }

        /// <summary>Raised (possibly on a background thread) when the shell process exits on its own.</summary>
        event EventHandler SessionExited;

        /// <summary>Starts a session, replacing the current one. Call on the UI thread.</summary>
        Task StartAsync(string workingDirectory, string commandLine, ConsoleAppearance appearance);

        void Stop();

        /// <summary>Types text into the terminal. Returns false when no session is attached.</summary>
        bool SendText(string text);

        /// <summary>Gives the keyboard focus to the terminal after the current input message.</summary>
        void FocusSoon();

        /// <summary>
        /// Gives the terminal the chance to take a keyboard message before Visual Studio
        /// treats it as a shortcut. Returns true when the message was consumed.
        /// </summary>
        bool PreProcessKeyMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
