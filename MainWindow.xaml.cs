using AgentReadonly.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace AgentReadonly
{
    public partial class MainWindow : Window
    {
        private readonly List<ChatMessage> messages = new List<ChatMessage>();
        private AppSettings settings;
        private AgentSession session;
        private CancellationTokenSource activeRequest;
        private readonly UsageLedger usageLedger = new UsageLedger();
        private readonly UpdateChecker updateChecker = new UpdateChecker();
        private readonly UpdateInstaller updateInstaller = new UpdateInstaller();
        private UsageSummary usageSummary = new UsageSummary();
        private double sessionSpendUsd;
        private CancellationTokenSource updateLoop;
        private UpdateCheckResult pendingUpdate;
        private bool updateInstalling;
        private bool updatePromptShown;

        public MainWindow()
        {
            InitializeComponent();
            AppLog.Info("MainWindow initializing.");

            TranscriptBox.Document = new FlowDocument();
            settings = AppSettings.Load();
            usageSummary = usageLedger.LoadSummary();
            ModelBox.Text = settings.Model;
            ApplyFontSize();
            UpdateFolderText();
            UpdateSpendText();
            SetStatus("");

            Loaded += delegate
            {
                AppLog.Info("MainWindow loaded: project_root=" + (settings.ProjectRoot ?? "") + " model=" + settings.Model + " font_size=" + settings.FontSize);
                PromptBox.Focus();
                if (string.IsNullOrWhiteSpace(settings.ProjectRoot) || !Directory.Exists(settings.ProjectRoot))
                    ChooseFolder();
                StartUpdateLoop();
            };

            Closed += delegate
            {
                if (updateLoop != null)
                {
                    updateLoop.Cancel();
                    updateLoop.Dispose();
                    updateLoop = null;
                }
            };
        }

        private void Send_Click(object sender, RoutedEventArgs e)
        {
            SendPrompt();
        }

        private void PromptBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                SendPrompt();
            }
            else if (e.Key == Key.OemPlus && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                ChangeFont(1);
            }
            else if (e.Key == Key.OemMinus && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                ChangeFont(-1);
            }
        }

        private async void SendPrompt()
        {
            if (activeRequest != null)
                return;

            string prompt = PromptBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(prompt))
                return;

            if (!ValidateReady())
                return;

            AppLog.Info("SendPrompt accepted: prompt_chars=" + prompt.Length + " project_root=" + settings.ProjectRoot);
            string apiKey;
            try
            {
                apiKey = AppSettings.ReadApiKey();
            }
            catch (Exception ex)
            {
                AppLog.Error("Failed to read API key.", ex);
                AddSystemMessage(ex.Message + Environment.NewLine + "Expected file: " + AppPaths.ApiKeyPath);
                return;
            }

            string model = ModelBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(model))
                model = "gpt-5.5";

            settings.Model = model;
            settings.Save();

            if (session == null)
            {
                AppLog.Info("Creating new AgentSession for model=" + model + " project_root=" + settings.ProjectRoot);
                session = new AgentSession(model, settings.ProjectRoot, apiKey, AppSettings.ReadContext());
            }

            PromptBox.Clear();
            AddUserMessage(prompt);
            SetBusy(true);

            activeRequest = new CancellationTokenSource();
            try
            {
                string answer = await session.SendAsync(
                    prompt,
                    text => Dispatcher.Invoke(delegate { SetStatus(text); }),
                    RecordUsage,
                    activeRequest.Token);

                AddAssistantMessage(string.IsNullOrWhiteSpace(answer) ? "(No text response.)" : answer);
            }
            catch (OperationCanceledException)
            {
                AppLog.Warn("SendPrompt canceled by user.");
                AddSystemMessage("Request canceled.");
            }
            catch (Exception ex)
            {
                AppLog.Error("SendPrompt failed.", ex);
                AddSystemMessage("Error: " + ex.Message);
            }
            finally
            {
                activeRequest.Dispose();
                activeRequest = null;
                SetBusy(false);
                SetStatus("Ready");
                PromptBox.Focus();
                AppLog.Info("SendPrompt finished.");
            }
        }

        private bool ValidateReady()
        {
            if (string.IsNullOrWhiteSpace(settings.ProjectRoot) || !Directory.Exists(settings.ProjectRoot))
            {
                AddSystemMessage("Choose a codebase folder before asking a question.");
                AppLog.Warn("Validation failed: missing or invalid project root=" + (settings.ProjectRoot ?? ""));
                ChooseFolder();
                return false;
            }
            return true;
        }

        private void ChooseFolder_Click(object sender, RoutedEventArgs e)
        {
            ChooseFolder();
        }

        private void ChooseFolder()
        {
            using (Forms.FolderBrowserDialog dialog = new Forms.FolderBrowserDialog())
            {
                dialog.Description = "Choose the codebase folder agent-readonly may inspect";
                dialog.ShowNewFolderButton = false;
                if (!string.IsNullOrWhiteSpace(settings.ProjectRoot) && Directory.Exists(settings.ProjectRoot))
                    dialog.SelectedPath = settings.ProjectRoot;

                if (dialog.ShowDialog() == Forms.DialogResult.OK && Directory.Exists(dialog.SelectedPath))
                {
                    settings.ProjectRoot = dialog.SelectedPath;
                    settings.Save();
                    session = null;
                    UpdateFolderText();
                    AppLog.Info("Codebase folder selected: " + settings.ProjectRoot);
                    AddSystemMessage("Codebase folder set to: " + settings.ProjectRoot);
                }
            }
        }

        private void ModelBox_LostFocus(object sender, RoutedEventArgs e)
        {
            string model = ModelBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(model))
                model = "gpt-5.5";
            if (!string.Equals(settings.Model, model, StringComparison.Ordinal))
            {
                settings.Model = model;
                settings.Save();
                session = null;
                AppLog.Info("Model changed: " + model);
                AddSystemMessage("Model set to: " + model);
            }
            ModelBox.Text = model;
        }

        private void DecreaseFont_Click(object sender, RoutedEventArgs e)
        {
            ChangeFont(-1);
        }

        private void IncreaseFont_Click(object sender, RoutedEventArgs e)
        {
            ChangeFont(1);
        }

        private void ChangeFont(double delta)
        {
            settings.FontSize = Math.Max(12, Math.Min(42, settings.FontSize + delta));
            settings.Save();
            ApplyFontSize();
            RenderAllMessages();
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            messages.Clear();
            TranscriptBox.Document.Blocks.Clear();
            session = null;
            AppLog.Info("Transcript cleared and session reset.");
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            if (activeRequest != null)
            {
                AppLog.Warn("Stop requested by user.");
                activeRequest.Cancel();
            }
        }

        private async void Update_Click(object sender, RoutedEventArgs e)
        {
            await InstallPendingUpdateAsync();
        }

        private async Task InstallPendingUpdateAsync()
        {
            if (pendingUpdate == null || updateInstalling)
                return;

            updateInstalling = true;
            UpdateButton.IsEnabled = false;
            UpdateButton.Content = "Updating...";
            AddSystemMessage("Downloading update. The app will restart when it is ready.");

            try
            {
                await updateInstaller.StageAndLaunchAsync(pendingUpdate, Process.GetCurrentProcess().Id, CancellationToken.None);
                AppLog.Info("Update staged, shutting down for replacement.");
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                updateInstalling = false;
                UpdateButton.IsEnabled = true;
                UpdateButton.Content = "Update";
                AppLog.Error("Update failed.", ex);
                AddSystemMessage("Update failed: " + ex.Message);
            }
        }

        private void StartUpdateLoop()
        {
            if (updateLoop != null)
                return;
            if (!Environment.Is64BitProcess)
            {
                AppLog.Info("Update checks skipped because this is not a 64-bit process.");
                return;
            }

            updateLoop = new CancellationTokenSource();
            CancellationToken token = updateLoop.Token;
            Task.Run(async delegate
            {
                while (!token.IsCancellationRequested)
                {
                    await CheckForUpdatesOnce(token);
                    try
                    {
                        await Task.Delay(TimeSpan.FromMinutes(10), token);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            }, token);
        }

        private async Task CheckForUpdatesOnce(CancellationToken token)
        {
            try
            {
                UpdateCheckResult result = await updateChecker.CheckAsync(token);
                if (token.IsCancellationRequested)
                    return;

                await Dispatcher.InvokeAsync(delegate
                {
                    ApplyUpdateCheckResult(result);
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppLog.Error("Update check failed.", ex);
            }
        }

        private void ApplyUpdateCheckResult(UpdateCheckResult result)
        {
            if (updateInstalling)
                return;

            if (result != null && result.IsUpdateAvailable)
            {
                pendingUpdate = result;
                string remoteCommit = result.RemoteManifest == null ? "" : (result.RemoteManifest.commit ?? "");
                string shortCommit = remoteCommit.Length > 12 ? remoteCommit.Substring(0, 12) : remoteCommit;
                UpdateButton.ToolTip = "Install latest build " + shortCommit;
                UpdateButton.Content = "Update";
                UpdateButton.IsEnabled = true;
                UpdateButton.Visibility = Visibility.Visible;

                if (!updatePromptShown)
                {
                    updatePromptShown = true;
                    MessageBoxResult promptResult = MessageBox.Show(
                        this,
                        "A new update is ready. Install it now? The app will restart after the update is staged.",
                        "Update available",
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Information);

                    if (promptResult == MessageBoxResult.OK)
                        _ = InstallPendingUpdateAsync();
                }
            }
            else
            {
                pendingUpdate = null;
                UpdateButton.Visibility = Visibility.Collapsed;
            }
        }

        private void SetBusy(bool busy)
        {
            SendButton.IsEnabled = !busy;
            StopButton.IsEnabled = busy;
            ChooseFolderButton.IsEnabled = !busy;
            ModelBox.IsEnabled = !busy;
            PromptBox.IsEnabled = !busy;
            if (!updateInstalling && pendingUpdate != null)
                UpdateButton.IsEnabled = !busy;
            ThinkingOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetStatus(string text)
        {
            PromptStatusText.Text = text;
            ThinkingText.Text = string.IsNullOrWhiteSpace(text) ? "Thinking..." : text;
        }

        private void UpdateFolderText()
        {
            FolderPathText.Text = string.IsNullOrWhiteSpace(settings.ProjectRoot)
                ? "No codebase folder selected"
                : settings.ProjectRoot;
        }

        private void RecordUsage(UsageEntry entry)
        {
            UsageSummary summary = usageLedger.Add(entry);
            Dispatcher.Invoke(delegate
            {
                sessionSpendUsd += entry.CostUsd;
                usageSummary = summary;
                UpdateSpendText();
            });
        }

        private void UpdateSpendText()
        {
            SpendText.Text =
                "Spent: session " + FormatUsd(sessionSpendUsd) +
                "  today " + FormatUsd(usageSummary.TodayUsd) +
                "  30d " + FormatUsd(usageSummary.Last30DaysUsd) +
                (usageSummary.HasUnpricedUsage ? "  + unpriced usage" : "");
        }

        private string FormatUsd(double value)
        {
            if (value <= 0)
                return "$0.00";
            if (value < 0.01)
                return "$" + value.ToString("0.0000", CultureInfo.InvariantCulture);
            return "$" + value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private void ApplyFontSize()
        {
            FontSizeText.Text = settings.FontSize.ToString("0");
            PromptBox.FontSize = settings.FontSize;
            ModelBox.FontSize = Math.Max(14, settings.FontSize - 2);
            TranscriptBox.FontSize = settings.FontSize;
            TranscriptBox.Document.FontSize = settings.FontSize;
            TranscriptBox.Document.FontFamily = new FontFamily("Segoe UI");
        }

        private void AddUserMessage(string text)
        {
            messages.Add(new ChatMessage("You", text));
            RenderAllMessages();
        }

        private void AddAssistantMessage(string text)
        {
            messages.Add(new ChatMessage("Agent", text));
            RenderAllMessages();
        }

        private void AddSystemMessage(string text)
        {
            messages.Add(new ChatMessage("System", text));
            RenderAllMessages();
        }

        private void RenderAllMessages()
        {
            TranscriptBox.Document.Blocks.Clear();
            foreach (ChatMessage message in messages)
                RenderMessage(message);
            TranscriptBox.ScrollToEnd();
        }

        private void RenderMessage(ChatMessage message)
        {
            Paragraph heading = new Paragraph();
            heading.Margin = new Thickness(0, 10, 0, 4);
            heading.Inlines.Add(new Bold(new Run(message.Role)));
            heading.Foreground = message.Role == "System" ? Brush("#5E6A70") : Brush("#1F2528");
            TranscriptBox.Document.Blocks.Add(heading);

            if (message.Role == "Agent")
                RenderMarkdownLike(message.Text);
            else
                AddTextParagraph(message.Text);
        }

        private void RenderMarkdownLike(string text)
        {
            string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            List<string> paragraph = new List<string>();
            List<string> code = null;

            foreach (string line in lines)
            {
                string fenceCandidate = line.TrimStart();
                if (fenceCandidate.StartsWith("```", StringComparison.Ordinal))
                {
                    if (code == null)
                    {
                        FlushParagraph(paragraph);
                        code = new List<string>();
                    }
                    else
                    {
                        AddCodeBlock(code);
                        code = null;
                    }
                    continue;
                }

                if (code != null)
                {
                    code.Add(line);
                }
                else
                {
                    string headingText;
                    int headingLevel;
                    if (TryParseHeading(line, out headingText, out headingLevel))
                    {
                        FlushParagraph(paragraph);
                        AddHeading(headingText, headingLevel);
                    }
                    else
                    {
                    paragraph.Add(line);
                    }
                }
            }

            if (code != null)
            {
                        paragraph.Add("```");
                        paragraph.AddRange(code);
            }
            FlushParagraph(paragraph);
        }

        private void FlushParagraph(List<string> paragraph)
        {
            if (paragraph.Count == 0)
                return;

            string text = string.Join(Environment.NewLine, paragraph.ToArray()).Trim();
            paragraph.Clear();
            if (!string.IsNullOrWhiteSpace(text))
                AddTextParagraph(text);
        }

        private bool TryParseHeading(string line, out string text, out int level)
        {
            text = "";
            level = 0;

            string trimmed = line.TrimStart();
            int hashes = 0;
            while (hashes < trimmed.Length && hashes < 6 && trimmed[hashes] == '#')
                hashes++;

            if (hashes == 0 || hashes >= trimmed.Length || trimmed[hashes] != ' ')
                return false;

            text = trimmed.Substring(hashes + 1).Trim();
            level = hashes;
            return text.Length > 0;
        }

        private void AddHeading(string text, int level)
        {
            Paragraph heading = new Paragraph();
            heading.Margin = new Thickness(0, level <= 2 ? 14 : 10, 0, 6);
            heading.Foreground = Brush("#1F2528");
            heading.FontSize = Math.Max(settings.FontSize + (level <= 1 ? 6 : level == 2 ? 4 : 2), settings.FontSize);
            heading.FontWeight = FontWeights.SemiBold;
            AddFormattedInlineText(heading, text);
            TranscriptBox.Document.Blocks.Add(heading);
        }

        private void AddTextParagraph(string text)
        {
            foreach (string chunk in Regex.Split(text, @"\n\s*\n"))
            {
                string trimmed = chunk.Trim();
                if (trimmed.Length == 0)
                    continue;

                Paragraph paragraph = new Paragraph();
                paragraph.Margin = new Thickness(0, 0, 0, 8);
                paragraph.LineHeight = settings.FontSize * 1.35;
                paragraph.Foreground = Brush("#1F2528");
                AddFormattedInlineText(paragraph, trimmed);
                TranscriptBox.Document.Blocks.Add(paragraph);
            }
        }

        private void AddFormattedInlineText(Paragraph paragraph, string text)
        {
            int index = 0;
            while (index < text.Length)
            {
                int boldStart = text.IndexOf("**", index, StringComparison.Ordinal);
                int codeStart = text.IndexOf("`", index, StringComparison.Ordinal);
                int nextStart = NextMarkupStart(boldStart, codeStart);

                if (nextStart < 0)
                {
                    paragraph.Inlines.Add(new Run(text.Substring(index)));
                    return;
                }

                if (nextStart > index)
                    paragraph.Inlines.Add(new Run(text.Substring(index, nextStart - index)));

                if (nextStart == boldStart)
                {
                    int boldEnd = text.IndexOf("**", boldStart + 2, StringComparison.Ordinal);
                    if (boldEnd < 0)
                    {
                        paragraph.Inlines.Add(new Run("**"));
                        index = boldStart + 2;
                    }
                    else
                    {
                        paragraph.Inlines.Add(new Bold(new Run(text.Substring(boldStart + 2, boldEnd - boldStart - 2))));
                        index = boldEnd + 2;
                    }
                }
                else
                {
                    int codeEnd = text.IndexOf("`", codeStart + 1, StringComparison.Ordinal);
                    if (codeEnd < 0)
                    {
                        paragraph.Inlines.Add(new Run("`"));
                        index = codeStart + 1;
                    }
                    else
                    {
                        Run run = new Run(text.Substring(codeStart + 1, codeEnd - codeStart - 1));
                        run.FontFamily = new FontFamily("Consolas");
                        run.Background = Brush("#ECEFED");
                        paragraph.Inlines.Add(run);
                        index = codeEnd + 1;
                    }
                }
            }
        }

        private int NextMarkupStart(int boldStart, int codeStart)
        {
            if (boldStart < 0)
                return codeStart;
            if (codeStart < 0)
                return boldStart;
            return Math.Min(boldStart, codeStart);
        }

        private void AddCodeBlock(List<string> rawLines)
        {
            string header = "Code";
            List<string> lines = new List<string>(rawLines);
            if (lines.Count > 0 && lines[0].StartsWith("FILE ", StringComparison.OrdinalIgnoreCase))
            {
                header = lines[0];
                lines.RemoveAt(0);
            }

            Border border = new Border();
            border.Background = Brush("#FFFFFF");
            border.BorderBrush = Brush("#C8CCC6");
            border.BorderThickness = new Thickness(1);
            border.CornerRadius = new CornerRadius(4);
            border.Margin = new Thickness(0, 4, 0, 12);
            border.Padding = new Thickness(0);

            StackPanel panel = new StackPanel();
            border.Child = panel;

            DockPanel headerPanel = new DockPanel();
            headerPanel.Background = Brush("#ECEFED");
            headerPanel.LastChildFill = true;
            headerPanel.Margin = new Thickness(0);

            Button copyButton = new Button();
            copyButton.Content = "Copy";
            copyButton.Padding = new Thickness(8, 2, 8, 2);
            copyButton.Margin = new Thickness(8, 6, 8, 6);
            copyButton.Click += delegate { Clipboard.SetText(string.Join(Environment.NewLine, rawLines.ToArray())); };
            DockPanel.SetDock(copyButton, Dock.Right);
            headerPanel.Children.Add(copyButton);

            TextBlock title = new TextBlock();
            title.Text = header;
            title.FontFamily = new FontFamily("Consolas");
            title.FontWeight = FontWeights.SemiBold;
            title.Foreground = Brush("#263238");
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.VerticalAlignment = VerticalAlignment.Center;
            title.Margin = new Thickness(10, 6, 8, 6);
            headerPanel.Children.Add(title);
            panel.Children.Add(headerPanel);

            StackPanel codePanel = new StackPanel();
            codePanel.Background = Brush("#FFFFFF");
            panel.Children.Add(codePanel);

            for (int i = 0; i < lines.Count; i++)
                AddCodeRow(codePanel, lines[i], i + 1);

            TranscriptBox.Document.Blocks.Add(new BlockUIContainer(border));
        }

        private void AddCodeRow(StackPanel panel, string rawLine, int fallbackLine)
        {
            CodeLine line = ParseCodeLine(rawLine, fallbackLine);
            string background = "#FFFFFF";
            if (line.Kind == CodeLineKind.Add)
                background = "#E6F3EA";
            else if (line.Kind == CodeLineKind.Remove)
                background = "#F8E7E5";
            else if (line.Kind == CodeLineKind.Important)
                background = "#FFF4C2";

            Border border = new Border();
            border.Background = Brush(background);
            border.BorderBrush = Brush("#E1E4E0");
            border.BorderThickness = new Thickness(0, panel.Children.Count == 0 ? 0 : 1, 0, 0);

            DockPanel row = new DockPanel();
            border.Child = row;

            TextBlock lineNumber = new TextBlock();
            lineNumber.Text = line.LineNumber;
            lineNumber.Width = 58;
            lineNumber.TextAlignment = TextAlignment.Right;
            lineNumber.FontFamily = new FontFamily("Consolas");
            lineNumber.FontSize = Math.Max(12, settings.FontSize - 2);
            lineNumber.Foreground = Brush("#738087");
            lineNumber.Padding = new Thickness(8, 4, 10, 4);
            DockPanel.SetDock(lineNumber, Dock.Left);
            row.Children.Add(lineNumber);

            TextBlock block = new TextBlock();
            block.Text = line.Code;
            block.FontFamily = new FontFamily("Consolas");
            block.FontSize = Math.Max(12, settings.FontSize - 2);
            block.Foreground = Brush("#1E2428");
            block.TextWrapping = TextWrapping.Wrap;
            block.Padding = new Thickness(8, 4, 8, 4);
            row.Children.Add(block);

            panel.Children.Add(border);
        }

        private CodeLine ParseCodeLine(string raw, int fallbackLine)
        {
            CodeLine line = new CodeLine();
            line.Kind = CodeLineKind.Normal;
            line.LineNumber = fallbackLine.ToString();

            string text = raw ?? "";
            string trimmed = text.TrimStart();

            if (trimmed.StartsWith("ADD ", StringComparison.OrdinalIgnoreCase))
            {
                line.Kind = CodeLineKind.Add;
                text = trimmed.Substring(4);
            }
            else if (trimmed.StartsWith("REMOVE ", StringComparison.OrdinalIgnoreCase))
            {
                line.Kind = CodeLineKind.Remove;
                text = trimmed.Substring(7);
            }
            else if (trimmed.StartsWith("+", StringComparison.Ordinal))
            {
                line.Kind = CodeLineKind.Add;
                text = trimmed.Substring(1).TrimStart();
            }
            else if (trimmed.StartsWith("-", StringComparison.Ordinal))
            {
                line.Kind = CodeLineKind.Remove;
                text = trimmed.Substring(1).TrimStart();
            }
            else if (trimmed.StartsWith(">", StringComparison.Ordinal))
            {
                line.Kind = CodeLineKind.Important;
                text = trimmed.Substring(1).TrimStart();
            }

            Match match = Regex.Match(text, @"^\s*(\d+)\s*[:|]?\s?(.*)$");
            if (match.Success)
            {
                line.LineNumber = match.Groups[1].Value;
                line.Code = match.Groups[2].Value;
            }
            else
            {
                line.Code = text;
            }

            return line;
        }

        private SolidColorBrush Brush(string color)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }

        private class ChatMessage
        {
            public ChatMessage(string role, string text)
            {
                Role = role;
                Text = text;
            }

            public string Role { get; private set; }
            public string Text { get; private set; }
        }

        private class CodeLine
        {
            public CodeLineKind Kind { get; set; }
            public string LineNumber { get; set; }
            public string Code { get; set; }
        }

        private enum CodeLineKind
        {
            Normal,
            Important,
            Add,
            Remove
        }
    }
}
