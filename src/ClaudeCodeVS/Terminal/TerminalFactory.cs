using System;
using System.Runtime.CompilerServices;
using ClaudeCodeVS.Options;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS.Terminal
{
    internal static class TerminalFactory
    {
        /// <summary>Why the classic console is in use although the modern terminal was requested; null otherwise.</summary>
        public static string FallbackReason { get; private set; }

        public static ITerminalHost Create()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            bool useClassic = false;
            ClaudeCodePackage package = ClaudeCodePackage.Instance;
            if (package != null)
            {
                var options = package.GetDialogPage(typeof(GeneralOptions)) as GeneralOptions;
                if (options != null)
                {
                    useClassic = options.UseClassicConsole;
                }
            }

            if (!useClassic)
            {
                try
                {
                    ITerminalHost modern = CreateModern();
                    FallbackReason = null;
                    return modern;
                }
                catch (Exception ex)
                {
                    // The terminal control ships with Visual Studio and its version varies;
                    // any failure to load or create it falls back to the classic console.
                    FallbackReason = ex.GetType().Name + ": " + ex.Message;
                }
            }

            return new ConsoleHost();
        }

        // Not inlined: the types of Microsoft.Terminal.Wpf are resolved when this method is
        // compiled by the runtime, so a load failure is thrown inside the try block above.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ITerminalHost CreateModern()
        {
            return new ModernTerminal();
        }
    }
}
