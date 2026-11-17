using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using ClaudeCodeVS.Options;
using ClaudeCodeVS.ToolWindow;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVS
{
    internal static class PackageGuids
    {
        public const string PackageString = "6f0c1b52-3d8e-4a47-9b1f-2c7a5e94d301";
        public const string CommandSetString = "6f0c1b52-3d8e-4a47-9b1f-2c7a5e94d302";
        public const string ToolWindowString = "6f0c1b52-3d8e-4a47-9b1f-2c7a5e94d303";

        public static readonly Guid CommandSet = new Guid(CommandSetString);
    }

    internal static class PackageIds
    {
        // Must match cmdidOpenClaudeWindow in ClaudeCodePackage.vsct.
        public const int OpenClaudeWindow = 0x0100;

        // Must match cmdidInsertFileReference in ClaudeCodePackage.vsct.
        public const int InsertFileReference = 0x0101;
    }

    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuids.PackageString)]
    [InstalledProductRegistration("Claude Code Tool Window (Unofficial)", "Runs the Claude Code CLI in a dockable tool window.", "0.1.0")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(ClaudeToolWindow), MultiInstances = true, Style = VsDockStyle.Tabbed, Window = ToolWindowGuids80.SolutionExplorer)]
    [ProvideOptionPage(typeof(GeneralOptions), "Claude Code", "General", 0, 0, true)]
    public sealed class ClaudeCodePackage : AsyncPackage
    {
        internal static ClaudeCodePackage Instance { get; private set; }

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            Instance = this;

            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var commandService = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService != null)
            {
                var commandId = new CommandID(PackageGuids.CommandSet, PackageIds.OpenClaudeWindow);
                commandService.AddCommand(new MenuCommand(OnOpenClaudeWindow, commandId));

                var referenceId = new CommandID(PackageGuids.CommandSet, PackageIds.InsertFileReference);
                commandService.AddCommand(new MenuCommand(OnInsertFileReference, referenceId));
            }
        }

        private void OnOpenClaudeWindow(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ShowClaudeWindow();
        }

        /// <summary>Opens one more Claude Code window, with a chat of its own.</summary>
        internal void OpenNewClaudeWindow()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            for (int id = 1; id < 20; id++)
            {
                if (FindToolWindow(typeof(ClaudeToolWindow), id, false) != null)
                {
                    continue; // this instance is already open
                }

                var window = FindToolWindow(typeof(ClaudeToolWindow), id, true) as ClaudeToolWindow;
                if (window == null || window.Frame == null)
                {
                    return;
                }

                window.Caption = "Claude Code " + (id + 1);
                ErrorHandler.ThrowOnFailure(((IVsWindowFrame)window.Frame).Show());
                return;
            }
        }

        private ClaudeToolWindow ShowClaudeWindow()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var window = FindToolWindow(typeof(ClaudeToolWindow), 0, true) as ClaudeToolWindow;
            if (window == null || window.Frame == null)
            {
                throw new NotSupportedException("Cannot create the Claude Code tool window.");
            }

            var frame = (IVsWindowFrame)window.Frame;
            ErrorHandler.ThrowOnFailure(frame.Show());
            return window;
        }

        /// <summary>
        /// Types an @-reference to the active document into the Claude Code prompt,
        /// including the selected line range when there is a selection.
        /// </summary>
        private void OnInsertFileReference(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var dte = GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            EnvDTE.Document document = dte != null ? dte.ActiveDocument : null;
            if (document == null || string.IsNullOrEmpty(document.FullName))
            {
                return;
            }

            ClaudeToolWindow window = ShowClaudeWindow();
            string reference = "@" + ToRelativePath(window.WorkingDirectory, document.FullName);

            var selection = document.Selection as EnvDTE.TextSelection;
            if (selection != null && !selection.IsEmpty)
            {
                int top = selection.TopLine;
                int bottom = selection.BottomLine;

                // A selection ending at column 1 does not include that last line.
                if (bottom > top && selection.BottomPoint.AtStartOfLine)
                {
                    bottom--;
                }

                reference += bottom > top ? "#" + top + "-" + bottom : "#" + top;
            }

            window.SendText(reference + " ");
        }

        private static string ToRelativePath(string baseDirectory, string fullPath)
        {
            if (!string.IsNullOrEmpty(baseDirectory))
            {
                string prefix = baseDirectory.TrimEnd('\\') + "\\";
                if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return fullPath.Substring(prefix.Length).Replace('\\', '/');
                }
            }

            return fullPath.Replace('\\', '/');
        }
    }
}
