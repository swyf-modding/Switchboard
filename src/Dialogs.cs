using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Switchboard
{
    /// <summary>How insistent a dialog is, which picks the glyph and its colour.</summary>
    internal enum DialogSeverity
    {
        Question,
        Information,
        Warning,
        Error
    }

    /// <summary>What to do about one mod, from the popup that describes it.</summary>
    internal enum ModChoice
    {
        Close,
        Reviewed,
        Newest
    }

    /// <summary>
    /// The switchboard's modal dialogs, built from the theme.
    /// </summary>
    /// <remarks>
    /// WPF's MessageBox is a Win32 dialog and cannot be themed, so a dark window would put a light grey
    /// box in the middle of itself. Avalonia has no MessageBox at all, so the same reasoning applies for
    /// the same reason: these are built from the same styles as the rest of the UI.
    ///
    /// Written out in code rather than XAML because the body text varies and a dialog that has to be
    /// assembled from parts at runtime does not benefit from markup.
    ///
    /// They are asynchronous now, and that is the one real change from the .NET Framework version. A
    /// modal dialog blocks the message loop in WPF, so <c>ShowDialog</c> could return a bool; Avalonia's
    /// <c>ShowDialog</c> is a task, and blocking on it from the UI thread would deadlock. So every caller
    /// awaits instead, and the call sites read as <c>await Dialogs.ConfirmAsync(...)</c>.
    ///
    /// <see cref="ConfirmAsync"/> defaults to Cancel being the default button. Three of its callers ask
    /// "replace these files, there is no undo", and Enter meaning yes on those is how files get lost.
    /// </remarks>
    internal static class Dialogs
    {
        /// <summary>
        /// Everything about one mod, and the two ways to install it.
        /// </summary>
        /// <remarks>
        /// The description and the warning are the dialog. Capabilities sit inside the warning rather
        /// than behind a disclosure, because "this can read your files and use the network" is the
        /// sentence that decides whether somebody wants it, and a fact you have to go looking for is
        /// one that gets skipped.
        ///
        /// The newest release is a link rather than a button. It installs code nobody checked, so it
        /// should take a second look to find, and it should never look like the thing to press.
        /// </remarks>
        public static Task<ModChoice> ModAsync(string name, string description, string source,
                                               string capabilities, string acceptText, bool canInstall,
                                               string newestTag)
        {
            var choice = ModChoice.Close;

            var window = Shell(name, 620, out var accept, out var cancel, out var buttons);
            accept.Content = acceptText;
            accept.IsEnabled = canInstall;
            accept.Click += delegate { choice = ModChoice.Reviewed; window.Close(); };
            accept.IsDefault = canInstall;

            cancel.Content = "Close";
            cancel.IsCancel = true;
            cancel.Click += delegate { choice = ModChoice.Close; window.Close(); };

            var body = new StackPanel { Spacing = 10 };

            if (!string.IsNullOrEmpty(description))
            {
                body.Children.Add(new TextBlock
                {
                    Text = description,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Palette.Brush(Palette.Text),
                    FontSize = 12.5
                });
            }

            if (!string.IsNullOrEmpty(source))
            {
                body.Children.Add(new TextBlock
                {
                    Text = source,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Palette.Brush(Palette.Muted),
                    FontSize = 11
                });
            }

            // The warning, with what the files can reach inside it. This is the only place on the
            // window that says it: repeated on the page it became furniture.
            var warning = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11.5,
                Foreground = Palette.Brush(Palette.Warn),
                Text = "Installing runs new code inside your game. Only install what you trust."
            };

            var band = new StackPanel { Spacing = 4 };
            band.Children.Add(warning);

            if (!string.IsNullOrEmpty(capabilities))
            {
                band.Children.Add(new TextBlock
                {
                    Text = "It can " + capabilities + ".",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11.5,
                    Foreground = Palette.Brush(Palette.Warn)
                });
            }

            body.Children.Add(new Border
            {
                Background = Palette.Brush(Palette.AccentMuted),
                BorderBrush = Palette.Brush(Palette.Warn),
                BorderThickness = new Thickness(2, 0, 0, 0),
                Padding = new Thickness(10, 8, 10, 8),
                Child = band
            });

            // The newest release, as a link. Deliberately quiet: small, muted, underlined, and after
            // the two buttons rather than among them.
            if (newestTag != null)
            {
                var link = new Button
                {
                    Content = new TextBlock
                    {
                        Text = "install the newest release instead (" + newestTag + ", not reviewed)",
                        FontSize = 10.5,
                        TextDecorations = TextDecorations.Underline,
                        Foreground = Palette.Brush(Palette.Faint)
                    },
                    Theme = Palette.ControlTheme("GhostButton"),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                link.Click += delegate { choice = ModChoice.Newest; window.Close(); };
                body.Children.Add(link);
            }

            body.Children.Add(buttons);

            window.Content = new Border { Padding = new Thickness(18, 16, 18, 16), Child = body };

            var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (owner != null && owner != window && owner.IsVisible)
            {
                window.ShowDialog(owner);
            }
            else
            {
                window.Show();
            }

            // The caller awaits the window closing rather than the dialog, because two of the three
            // answers close it too: a TaskCompletionSource keeps that one shape.
            var closed = new TaskCompletionSource<ModChoice>();
            window.Closed += delegate { closed.TrySetResult(choice); };
            return closed.Task;
        }

        /// <summary>A window with the theme applied and a right-aligned button row, ready to fill in.</summary>
        private static Window Shell(string title, double width, out Button accept, out Button cancel,
                                    out StackPanel buttons)
        {
            var window = new Window
            {
                Title = title,
                Icon = App.NewWindowIcon(),
                Width = width,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Background = Palette.Brush(Palette.Surface),
                FontSize = 12,
                Foreground = Palette.Brush(Palette.Text)
            };

            Avalonia.Input.KeyboardNavigation.SetTabNavigation(window, Avalonia.Input.KeyboardNavigationMode.Cycle);

            accept = MakeButton("", false, false, false);
            cancel = MakeButton("", false, false, false);

            buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Thickness(0, 8, 0, 0)
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(accept);

            return window;
        }

        /// <summary>Ask a yes/no question. Returns true only if the accept button was chosen.</summary>
        public static Task<bool> ConfirmAsync(string title, string message, string acceptText,
                                              DialogSeverity severity = DialogSeverity.Question)
        {
            return ShowAsync(title, message, severity, acceptText, "Cancel", defaultAccept: false);
        }

        /// <summary>
        /// Ask whether to do an optional thing, where declining moves on rather than aborting.
        /// </summary>
        /// <remarks>
        /// "Cancel" would be the wrong word here: declining one setup step offers the next one, so the
        /// button says Skip. Still defaults away from running, like every other confirmation.
        /// </remarks>
        public static Task<bool> ConfirmSkippableAsync(string title, string message, string acceptText,
                                                       DialogSeverity severity = DialogSeverity.Question)
        {
            return ShowAsync(title, message, severity, acceptText, "Skip", defaultAccept: false);
        }

        /// <summary>Ask for one line of text. Null when it was cancelled.</summary>
        public static Task<string> PromptAsync(string title, string message, string placeholder)
        {
            var window = Shell(title, 560, out var accept, out var cancel, out var buttons);
            accept.Content = "Continue";
            cancel.Content = "Cancel";
            accept.IsCancel = false;
            cancel.IsCancel = true;

            var box = new TextBox
            {
                Watermark = placeholder,
                Margin = new Thickness(0, 10, 0, 0)
            };

            accept.Click += delegate { window.Close(); };
            cancel.Click += delegate { box.Text = null; window.Close(); };

            var body = new StackPanel();
            body.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Palette.Brush(Palette.Text)
            });
            body.Children.Add(box);
            body.Children.Add(buttons);

            window.Content = new Border { Padding = new Thickness(18, 16, 18, 16), Child = body };
            window.Opened += delegate { box.Focus(); };

            var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            var closed = new TaskCompletionSource<string>();
            window.Closed += delegate { closed.TrySetResult(box.Text); };
            if (owner != null && owner != window && owner.IsVisible) window.ShowDialog(owner);
            else window.Show();
            return closed.Task;
        }

        /// <summary>Say something that needs acknowledging.</summary>
        public static Task InformAsync(string title, string message)
        {
            return ShowAsync(title, message, DialogSeverity.Information, "OK", null, defaultAccept: true);
        }

        /// <summary>Report a failure.</summary>
        public static Task ErrorAsync(string title, string message)
        {
            return ShowAsync(title, message, DialogSeverity.Error, "OK", null, defaultAccept: true);
        }

        private static async Task<bool> ShowAsync(string title, string message, DialogSeverity severity,
                                                  string acceptText, string cancelText, bool defaultAccept)
        {
            var answer = false;

            var window = new Window
            {
                Title = title,
                Icon = App.NewWindowIcon(),
                Width = 520,
                SizeToContent = SizeToContent.Height,
                MinHeight = 170,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Background = Palette.Brush(Palette.Surface),
                FontSize = 12,
                Foreground = Palette.Brush(Palette.Text)
            };

            var accept = MakeButton(acceptText, isDefault: defaultAccept, isCancel: false,
                                    accent: severity == DialogSeverity.Error);
            var cancel = cancelText == null
                ? null
                : MakeButton(cancelText, isDefault: !defaultAccept, isCancel: true, accent: false);

            accept.Click += delegate { answer = true; window.Close(); };
            if (cancel != null) cancel.Click += delegate { answer = false; window.Close(); };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };

            // Cancel first, so it sits closest to nothing in particular, and Enter still goes to whichever
            // button is marked default rather than to the first one on the row.
            if (cancel != null) buttons.Children.Add(cancel);
            buttons.Children.Add(accept);

            var text = new TextBlock
            {
                Text = message ?? "",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Palette.Brush(Palette.Text)
            };

            // Scrolled rather than capped: the install plan is the thing the user is supposed to read
            // before agreeing to run new code, so clipping its tail would defeat the confirmation.
            var body = new ScrollViewer
            {
                Content = text,
                MaxHeight = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            var glyph = Glyph(severity);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            if (glyph != null)
            {
                Grid.SetColumn(glyph, 0);
                grid.Children.Add(glyph);
            }

            var right = new StackPanel();
            right.Children.Add(body);
            right.Children.Add(buttons);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);

            var padding = new Border
            {
                Padding = new Thickness(18, 16, 18, 16),
                Child = grid
            };

            window.Content = padding;
            window.Opened += delegate
            {
                // Focus the default rather than nothing, so Enter works before the user clicks anything.
                (defaultAccept ? accept : cancel ?? accept).Focus();
            };

            var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (owner != null && owner != window && owner.IsVisible)
            {
                await window.ShowDialog(owner);
                return answer;
            }

            // No owner to be modal to - a dialog raised before the main window exists, or after it has
            // closed. Shown non-modally but still awaited, so a caller that needs an answer still gets one.
            var closed = new TaskCompletionSource<bool>();
            window.Closed += delegate { closed.TrySetResult(true); };
            window.Show();
            await closed.Task;
            return answer;
        }

        /// <summary>A button in the window's theme, without the row plumbing.</summary>
        private static Button MakeButton(string text, bool isDefault, bool isCancel, bool accent)
        {
            return new Button
            {
                Content = text,
                MinWidth = 96,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = isDefault,
                // Escape closes the dialog as "no". On the OK-only dialogs there is no cancel button, so
                // the OK button is not marked cancel either - that would let Escape close without an
                // answer, which reads as "declined" and is wrong when there was nothing to decline.
                IsCancel = isCancel,
                Theme = Palette.ControlTheme(accent ? "AccentButton" : "FlatButton")
            };
        }

        private static TextBlock Glyph(DialogSeverity severity)
        {
            string character;
            string key;

            switch (severity)
            {
                case DialogSeverity.Error:
                    character = "✕";
                    key = Palette.Bad;
                    break;
                case DialogSeverity.Warning:
                    character = "!";
                    key = Palette.Warn;
                    break;
                case DialogSeverity.Question:
                    character = "?";
                    key = Palette.Accent;
                    break;
                default:
                    character = "i";
                    key = Palette.Muted;
                    break;
            }

            return new TextBlock
            {
                Text = character,
                FontSize = 18,
                FontWeight = FontWeight.Bold,
                Foreground = Palette.Brush(key),
                HorizontalAlignment = HorizontalAlignment.Left
            };
        }
    }
}
