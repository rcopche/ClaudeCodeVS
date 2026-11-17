using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using ClaudeCodeVS.Options;
using ClaudeCodeVS.Terminal;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace ClaudeCodeVS.ToolWindow
{
    /// <summary>
    /// Content of the Claude Code tool window: a thin toolbar plus the embedded console.
    /// Built in code (no XAML) to keep the project small.
    /// </summary>
    internal sealed class ClaudeTerminalControl : UserControl, IDisposable
    {
        private const int MaxHistoryItems = 40;
        private const int MaxErrorsSent = 15;
        private const int MaxErrorPromptLength = 3500;

        // Official Windows installer of the Claude Code CLI (PowerShell).
        private const string InstallCommand = "irm https://claude.ai/install.ps1 | iex";

        private readonly ConsoleHost _host = new ConsoleHost();
        private readonly TextBlock _status = new TextBlock();
        private readonly Button _newChat;
        private readonly Button _history;
        private readonly Border _installPanel;
        private readonly Button _fixErrors;
        private readonly Button _newWindow;
        private readonly Border _notice;
        private readonly TextBlock _noticeText = new TextBlock();
        private Popup _historyPopup;
        private bool _started;
        private bool _disposed;
        private string _pendingText;

        public ClaudeTerminalControl()
        {
            _newChat = ToolbarUi.CreateToolbarButton(
                ToolbarUi.GlyphNewChat, "New chat",
                "Start a new Claude Code chat in the current solution folder");
            _newChat.Click += OnNewChatClick;

            _history = ToolbarUi.CreateToolbarButton(
                ToolbarUi.GlyphHistory, "History",
                "Show previous chats for this folder and resume one");
            _history.Click += OnHistoryClick;

            _fixErrors = ToolbarUi.CreateToolbarButton(
                ToolbarUi.GlyphFixErrors, "Fix errors",
                "Type the errors from the Error List into the chat so Claude can fix them");
            _fixErrors.Click += OnFixErrorsClick;

            _newWindow = ToolbarUi.CreateToolbarButton(
                ToolbarUi.GlyphNewWindow, null,
                "Open another Claude Code window with a chat of its own");
            _newWindow.Click += OnNewWindowClick;

            _status.Margin = new Thickness(10, 0, 6, 0);
            _status.VerticalAlignment = VerticalAlignment.Center;
            _status.TextTrimming = TextTrimming.CharacterEllipsis;
            _status.Opacity = 0.7;
            _status.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

            var barContent = new DockPanel { LastChildFill = true };
            Image logo = CreateLogo();
            if (logo != null)
            {
                DockPanel.SetDock(logo, Dock.Left);
                barContent.Children.Add(logo);
            }

            DockPanel.SetDock(_newChat, Dock.Left);
            DockPanel.SetDock(_history, Dock.Left);
            DockPanel.SetDock(_fixErrors, Dock.Left);
            DockPanel.SetDock(_newWindow, Dock.Right);
            barContent.Children.Add(_newChat);
            barContent.Children.Add(_history);
            barContent.Children.Add(_fixErrors);
            barContent.Children.Add(_newWindow);
            barContent.Children.Add(_status);

            var bar = new Border
            {
                Child = barContent,
                Padding = new Thickness(2, 3, 2, 3),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            bar.SetResourceReference(Border.BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
            bar.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ToolWindowBorderKey);

            var root = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(bar, Dock.Top);
            // The console and the first-run panel share the same area; only one is visible.
            _installPanel = CreateInstallPanel();
            var content = new Grid();
            content.Children.Add(_host);
            content.Children.Add(_installPanel);

            _notice = CreateNotice();
            DockPanel.SetDock(_notice, Dock.Top);

            root.Children.Add(bar);
            root.Children.Add(_notice);
            root.Children.Add(content);

            SetResourceReference(BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
            Content = root;

            _host.SessionExited += OnSessionExited;
            Loaded += OnLoaded;
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterOpenSolution += OnSolutionOpened;
        }

        /// <summary>Folder the current session was started in, or null before the first start.</summary>
        public string WorkingDirectory { get; private set; }

        /// <summary>
        /// Types text into the Claude Code prompt. If the session is still starting,
        /// the text is delivered as soon as the console is attached.
        /// </summary>
        public void SendText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (!_host.SendText(text))
            {
                _pendingText = (_pendingText ?? string.Empty) + text;
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_started)
            {
                _started = true;
                StartSession(null);
            }
        }

        private void OnNewChatClick(object sender, RoutedEventArgs e)
        {
            StartSession(null);
        }

        private void OnSessionExited(object sender, EventArgs e)
        {
            // Raised on a thread-pool thread.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_disposed)
                {
                    SetStatus("Session ended. Click New chat to start another one.", null);
                }
            }));
        }

        private void SetStatus(string text, string toolTip)
        {
            _status.Text = text;
            _status.ToolTip = toolTip;
        }

        /// <summary>
        /// Starts a session, replacing the current one. When <paramref name="resumeSessionId"/>
        /// is given, that previous chat is resumed instead of starting a new one.
        /// If the CLI is not installed, the first-run panel is shown instead.
        /// </summary>
        private void StartSession(string resumeSessionId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!IsCliAvailable())
            {
                ShowInstallPanel();
                return;
            }

            string directory = GetWorkingDirectory();
            RunInConsole(
                directory,
                BuildCommandLine(resumeSessionId),
                (resumeSessionId == null ? "Starting in " : "Resuming chat in ") + directory + " ...");
        }

        private void OnInstallClick(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Runs the installer in the embedded console, where its output is visible,
            // and then starts the CLI from the folder the installer puts it in.
            string commandLine =
                "powershell -NoProfile -Command \"" + InstallCommand + "\"" +
                " && \"%USERPROFILE%\\.local\\bin\\claude.exe\"";
            RunInConsole(GetWorkingDirectory(), commandLine, "Installing Claude Code ...");
        }

        /// <summary>
        /// Loads the extension icon (Resources\Icon.png, deployed next to the assembly)
        /// for the toolbar. Returns null when the file is missing or unreadable.
        /// </summary>
        private static Image CreateLogo()
        {
            try
            {
                string folder = Path.GetDirectoryName(typeof(ClaudeTerminalControl).Assembly.Location);
                if (string.IsNullOrEmpty(folder))
                {
                    return null;
                }

                string path = Path.Combine(folder, "Resources", "Icon.png");
                if (!File.Exists(path))
                {
                    return null;
                }

                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 72; // enough for an 18 px image at 400% scaling
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();

                var image = new Image
                {
                    Source = bitmap,
                    Width = 18,
                    Height = 18,
                    Margin = new Thickness(5, 0, 3, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                System.Windows.Media.RenderOptions.SetBitmapScalingMode(
                    image, System.Windows.Media.BitmapScalingMode.HighQuality);
                return image;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void ShowInstallPanel()
        {
            _host.Stop();
            _host.Visibility = Visibility.Collapsed;
            _installPanel.Visibility = Visibility.Visible;
            SetStatus("Claude Code is not installed", null);
        }

        private Border CreateInstallPanel()
        {
            var title = new TextBlock
            {
                Text = "Claude Code is not installed",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var body = new TextBlock
            {
                Text = "This window runs the Claude Code CLI, which was not found on this computer. " +
                       "Install it now with the official installer from claude.ai? The command below " +
                       "runs in this window, then Claude Code starts and asks you to sign in.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.85,
                Margin = new Thickness(0, 0, 0, 10)
            };

            var command = new TextBlock
            {
                Text = InstallCommand,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.85,
                Margin = new Thickness(0, 0, 0, 14)
            };

            Button install = ToolbarUi.CreateToolbarButton(
                ToolbarUi.GlyphDownload, "Install Claude Code",
                "Download and run the official Claude Code installer");
            install.Margin = new Thickness(0);
            install.Click += OnInstallClick;

            var installFrame = new Border
            {
                Child = install,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            installFrame.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ToolWindowBorderKey);

            var hint = new TextBlock
            {
                Text = "Already installed in another location? Set its path in Tools > Options > " +
                       "Claude Code > General > Command, then click New chat.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Opacity = 0.65,
                Margin = new Thickness(0, 14, 0, 0)
            };

            var stack = new StackPanel
            {
                MaxWidth = 440,
                Margin = new Thickness(20),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            stack.Children.Add(title);
            stack.Children.Add(body);
            stack.Children.Add(command);
            stack.Children.Add(installFrame);
            stack.Children.Add(hint);

            var panel = new Border { Child = stack, Visibility = Visibility.Collapsed };
            panel.SetResourceReference(Border.BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
            panel.SetResourceReference(TextElement.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            return panel;
        }

        /// <summary>Runs a command line in the embedded console, replacing the current session.</summary>
        private void RunInConsole(string directory, string commandLine, string startingStatus)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _installPanel.Visibility = Visibility.Collapsed;
            _host.Visibility = Visibility.Visible;
            HideNotice();

            ConsoleAppearance appearance = GetConsoleAppearance();

            SetStatus(startingStatus, directory);
            _newChat.IsEnabled = false;
            _history.IsEnabled = false;

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    await _host.StartAsync(directory, commandLine, appearance);
                    if (_host.IsRunning)
                    {
                        string name = Path.GetFileName(directory.TrimEnd('\\'));
                        SetStatus(string.IsNullOrEmpty(name) ? directory : name, directory);
                        WorkingDirectory = directory;

                        // Text queued while the console was starting stays in the console
                        // input buffer until the CLI is ready to read it.
                        string pending = _pendingText;
                        _pendingText = null;
                        if (!string.IsNullOrEmpty(pending))
                        {
                            _host.SendText(pending);
                        }
                    }
                }
                catch (Exception ex)
                {
                    SetStatus("Could not start Claude Code: " + ex.Message, null);
                }
                finally
                {
                    _newChat.IsEnabled = true;
                    _history.IsEnabled = true;
                }
            }).Task.Forget();
        }

        private Border CreateNotice()
        {
            _noticeText.VerticalAlignment = VerticalAlignment.Center;
            _noticeText.TextTrimming = TextTrimming.CharacterEllipsis;
            _noticeText.Margin = new Thickness(8, 0, 8, 0);

            Button start = ToolbarUi.CreateToolbarButton(
                ToolbarUi.GlyphNewChat, "New chat here",
                "Start a new chat in the folder of the solution that was just opened");
            start.Click += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                StartSession(null);
            };

            Button dismiss = ToolbarUi.CreateToolbarButton(
                ToolbarUi.GlyphClose, null, "Keep the current chat");
            dismiss.Click += (s, e) => HideNotice();

            var layout = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(dismiss, Dock.Right);
            DockPanel.SetDock(start, Dock.Right);
            layout.Children.Add(dismiss);
            layout.Children.Add(start);
            layout.Children.Add(_noticeText);

            var notice = new Border
            {
                Child = layout,
                Padding = new Thickness(2, 3, 2, 3),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Visibility = Visibility.Collapsed
            };
            notice.SetResourceReference(Border.BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
            notice.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ToolWindowBorderKey);
            notice.SetResourceReference(TextElement.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            return notice;
        }

        private void HideNotice()
        {
            _notice.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// A different solution was opened while a chat is running in another folder:
        /// offer a new chat there instead of silently killing the current one.
        /// </summary>
        private void OnSolutionOpened(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (_disposed || WorkingDirectory == null)
                {
                    return;
                }

                string directory = GetWorkingDirectory();
                if (string.Equals(directory, WorkingDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                string current = Path.GetFileName(WorkingDirectory.TrimEnd('\\'));
                _noticeText.Text = "Another solution was opened. This chat is still in " +
                                   (string.IsNullOrEmpty(current) ? WorkingDirectory : current) + ".";
                _noticeText.ToolTip = "Chat folder: " + WorkingDirectory + "\nSolution folder: " + directory;
                _notice.Visibility = Visibility.Visible;
            }));
        }

        private void OnNewWindowClick(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            ClaudeCodePackage package = ClaudeCodePackage.Instance;
            if (package != null)
            {
                package.OpenNewClaudeWindow();
            }
        }

        private void OnFixErrorsClick(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!_host.IsRunning)
            {
                SetStatus("Start a chat first, then send the errors.", null);
                return;
            }

            string prompt;
            try
            {
                prompt = BuildErrorPrompt(WorkingDirectory);
            }
            catch (Exception ex)
            {
                SetStatus("Could not read the Error List: " + ex.Message, null);
                return;
            }

            if (prompt == null)
            {
                SetStatus("No errors in the Error List.", null);
                return;
            }

            // Typed into the prompt without pressing Enter, so it can be reviewed first.
            _host.SendText(prompt);
        }

        /// <summary>
        /// Builds a one-line prompt from the errors in the Error List, or returns null when
        /// there are none. One line, because a line break would submit the prompt.
        /// </summary>
        private static string BuildErrorPrompt(string baseDirectory)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE80.DTE2;
            if (dte == null)
            {
                return null;
            }

            EnvDTE80.ErrorItems items = dte.ToolWindows.ErrorList.ErrorItems;
            var lines = new List<string>();
            int total = 0;
            int count = items.Count;
            for (int i = 1; i <= count; i++)
            {
                try
                {
                    EnvDTE80.ErrorItem item = items.Item(i);
                    if (item.ErrorLevel != EnvDTE80.vsBuildErrorLevel.vsBuildErrorLevelHigh)
                    {
                        continue; // warnings and messages
                    }

                    total++;
                    if (lines.Count >= MaxErrorsSent)
                    {
                        continue;
                    }

                    string file = item.FileName ?? string.Empty;
                    if (!string.IsNullOrEmpty(baseDirectory))
                    {
                        string prefix = baseDirectory.TrimEnd('\\') + "\\";
                        if (file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            file = file.Substring(prefix.Length);
                        }
                    }

                    string description = System.Text.RegularExpressions.Regex
                        .Replace(item.Description ?? string.Empty, @"\s+", " ").Trim();
                    lines.Add(file.Length == 0 ? description : file + "(" + item.Line + "): " + description);
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // An entry that cannot be read is skipped.
                }
                catch (ArgumentException)
                {
                }
            }

            if (total == 0)
            {
                return null;
            }

            var text = new System.Text.StringBuilder("Fix these build errors reported by Visual Studio: ");
            for (int n = 0; n < lines.Count; n++)
            {
                text.Append("(" + (n + 1) + ") " + lines[n] + "; ");
            }

            if (total > lines.Count)
            {
                text.Append("and " + (total - lines.Count) + " more errors not listed here. ");
            }

            string prompt = text.ToString();
            return prompt.Length <= MaxErrorPromptLength ? prompt : prompt.Substring(0, MaxErrorPromptLength);
        }

        private void OnHistoryClick(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string directory = WorkingDirectory ?? GetWorkingDirectory();
            _history.IsEnabled = false;

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                List<SessionInfo> sessions = null;
                string error = null;
                try
                {
                    // Transcripts can be large; read them off the UI thread.
                    sessions = await System.Threading.Tasks.Task.Run(
                        () => SessionHistory.Load(directory, MaxHistoryItems));
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _history.IsEnabled = true;
                if (!_disposed)
                {
                    ShowHistory(sessions, error);
                }
            }).Task.Forget();
        }

        private void ShowHistory(List<SessionInfo> sessions, string error)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var items = new StackPanel();
            if (error != null)
            {
                items.Children.Add(CreateNote("Could not read the chat history: " + error));
            }
            else if (sessions == null || sessions.Count == 0)
            {
                items.Children.Add(CreateNote("No previous chats for this folder yet."));
            }
            else
            {
                DateTime now = DateTime.Now;
                foreach (SessionInfo session in sessions)
                {
                    string sessionId = session.Id;
                    Button item = ToolbarUi.CreateListItem(session.Title, DescribeTime(session.LastActivity, now));
                    item.Click += (s, e) =>
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();
                        CloseHistory();
                        StartSession(sessionId);
                    };
                    items.Children.Add(item);
                }
            }

            var header = new TextBlock
            {
                Text = "Previous chats",
                FontSize = 11,
                Opacity = 0.65,
                Margin = new Thickness(8, 4, 8, 4)
            };

            var scroll = new ScrollViewer
            {
                Content = items,
                MaxHeight = 380,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            var layout = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(header, Dock.Top);
            layout.Children.Add(header);
            layout.Children.Add(scroll);

            var frame = new Border
            {
                Child = layout,
                Width = 380,
                Padding = new Thickness(4),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4)
            };
            frame.SetResourceReference(Border.BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
            frame.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ToolWindowBorderKey);
            frame.SetResourceReference(TextElement.ForegroundProperty, VsBrushes.ToolWindowTextKey);

            CloseHistory();

            // A Popup is its own top-level window, so it draws above the embedded console.
            _historyPopup = new Popup
            {
                Child = frame,
                PlacementTarget = _history,
                Placement = PlacementMode.Bottom,
                VerticalOffset = 2,
                StaysOpen = false,
                AllowsTransparency = true
            };
            _historyPopup.IsOpen = true;
        }

        private void CloseHistory()
        {
            if (_historyPopup != null)
            {
                _historyPopup.IsOpen = false;
                _historyPopup = null;
            }
        }

        private static TextBlock CreateNote(string text)
        {
            return new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 6, 8, 8)
            };
        }

        private static string DescribeTime(DateTime when, DateTime now)
        {
            TimeSpan age = now - when;
            string relative;
            if (age.TotalMinutes < 1)
            {
                relative = "just now";
            }
            else if (age.TotalMinutes < 60)
            {
                relative = (int)age.TotalMinutes + " min ago";
            }
            else if (age.TotalHours < 24)
            {
                relative = (int)age.TotalHours + " h ago";
            }
            else
            {
                relative = (int)age.TotalDays + " d ago";
            }

            return relative + "  \u00B7  " + when.ToString("g", CultureInfo.CurrentCulture);
        }

        /// <summary>
        /// Console font from the options page, replaced by an installed fallback when the
        /// configured one is missing on this machine.
        /// </summary>
        /// <summary>Font from the options page plus, when enabled, the colors of the VS theme.</summary>
        private static ConsoleAppearance GetConsoleAppearance()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string fontName;
            int fontSize;
            GetConsoleFont(out fontName, out fontSize);
            var appearance = new ConsoleAppearance { FontName = fontName, FontSize = fontSize };

            bool matchTheme = true;
            ClaudeCodePackage package = ClaudeCodePackage.Instance;
            if (package != null)
            {
                var options = package.GetDialogPage(typeof(GeneralOptions)) as GeneralOptions;
                if (options != null)
                {
                    matchTheme = options.MatchTheme;
                }
            }

            if (matchTheme)
            {
                try
                {
                    System.Drawing.Color background = Microsoft.VisualStudio.PlatformUI.VSColorTheme.GetThemedColor(
                        Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowBackgroundColorKey);
                    System.Drawing.Color foreground = Microsoft.VisualStudio.PlatformUI.VSColorTheme.GetThemedColor(
                        Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowTextColorKey);
                    appearance.BackgroundColor = ToColorRef(background);
                    appearance.ForegroundColor = ToColorRef(foreground);
                }
                catch (Exception)
                {
                    // Theme colors are cosmetic; keep the console defaults.
                }
            }

            return appearance;
        }

        private static int ToColorRef(System.Drawing.Color color)
        {
            return color.R | (color.G << 8) | (color.B << 16);
        }

        private static void GetConsoleFont(out string fontName, out int fontSize)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            fontName = "Cascadia Mono";
            fontSize = 16;

            ClaudeCodePackage package = ClaudeCodePackage.Instance;
            if (package != null)
            {
                var options = package.GetDialogPage(typeof(GeneralOptions)) as GeneralOptions;
                if (options != null)
                {
                    if (!string.IsNullOrWhiteSpace(options.ConsoleFont))
                    {
                        fontName = options.ConsoleFont.Trim();
                    }

                    if (options.ConsoleFontSize > 0)
                    {
                        fontSize = options.ConsoleFontSize;
                    }
                }
            }

            try
            {
                using (var installed = new System.Drawing.Text.InstalledFontCollection())
                {
                    var names = new HashSet<string>(
                        installed.Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
                    foreach (string candidate in new[] { fontName, "Cascadia Mono", "Cascadia Code", "Consolas" })
                    {
                        if (names.Contains(candidate))
                        {
                            fontName = candidate;
                            return;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Font enumeration is best effort; keep the configured name.
            }
        }

        private static string BuildCommandLine(string resumeSessionId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string command = "claude";
            string arguments = string.Empty;

            ClaudeCodePackage package = ClaudeCodePackage.Instance;
            if (package != null)
            {
                var options = package.GetDialogPage(typeof(GeneralOptions)) as GeneralOptions;
                if (options != null)
                {
                    if (!string.IsNullOrWhiteSpace(options.Command))
                    {
                        command = options.Command.Trim();
                    }

                    if (!string.IsNullOrWhiteSpace(options.Arguments))
                    {
                        arguments = options.Arguments.Trim();
                    }
                }
            }

            command = ResolveCommand(command.Trim('"'));
            if (command.IndexOf(' ') >= 0)
            {
                command = "\"" + command + "\"";
            }

            if (resumeSessionId != null)
            {
                // Resuming a specific chat replaces "continue the latest one".
                string[] kept = arguments
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(a => a != "--continue" && a != "-c")
                    .ToArray();
                arguments = ("--resume " + resumeSessionId + " " + string.Join(" ", kept)).Trim();
            }

            return arguments.Length == 0 ? command : command + " " + arguments;
        }

        /// <summary>
        /// When the default "claude" command is not on the PATH seen by Visual Studio,
        /// falls back to the usual install locations of the native installer and of npm.
        /// </summary>
        private static string ResolveCommand(string command)
        {
            if (!string.Equals(command, "claude", StringComparison.OrdinalIgnoreCase) || IsOnPath(command))
            {
                return command;
            }

            return FindInKnownLocations() ?? command;
        }

        /// <summary>True when the configured CLI can be started on this computer.</summary>
        private static bool IsCliAvailable()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string command = "claude";
            ClaudeCodePackage package = ClaudeCodePackage.Instance;
            if (package != null)
            {
                var options = package.GetDialogPage(typeof(GeneralOptions)) as GeneralOptions;
                if (options != null && !string.IsNullOrWhiteSpace(options.Command))
                {
                    command = options.Command.Trim().Trim('"');
                }
            }

            try
            {
                if (Path.IsPathRooted(command))
                {
                    return File.Exists(command);
                }
            }
            catch (ArgumentException)
            {
                return false;
            }

            if (IsOnPath(command))
            {
                return true;
            }

            return string.Equals(command, "claude", StringComparison.OrdinalIgnoreCase) &&
                   FindInKnownLocations() != null;
        }

        private static bool IsOnPath(string command)
        {
            string[] extensions = { string.Empty, ".exe", ".cmd", ".bat" };
            string pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string entry in pathVariable.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (string extension in extensions)
                {
                    try
                    {
                        if (File.Exists(Path.Combine(entry.Trim().Trim('"'), command + extension)))
                        {
                            return true;
                        }
                    }
                    catch (ArgumentException)
                    {
                        // Malformed PATH entry; ignore it.
                    }
                }
            }

            return false;
        }

        private static string FindInKnownLocations()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string[] candidates =
            {
                Path.Combine(userProfile, ".local", "bin", "claude.exe"),
                Path.Combine(appData, "npm", "claude.cmd")
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        internal static string GetWorkingDirectory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var solution = Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution;
            if (solution != null)
            {
                string directory;
                string file;
                string userOptions;
                int hr = solution.GetSolutionInfo(out directory, out file, out userOptions);
                if (ErrorHandler.Succeeded(hr) && !string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                {
                    string trimmed = directory.TrimEnd('\\');
                    // Keep "C:\" intact for a solution at a drive root.
                    return trimmed.EndsWith(":", StringComparison.Ordinal) ? trimmed + "\\" : trimmed;
                }
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterOpenSolution -= OnSolutionOpened;
            CloseHistory();
            _host.SessionExited -= OnSessionExited;
            _host.Stop();
            _host.Dispose();
        }
    }
}
