using System;
using Avalonia.Controls;

namespace Switchboard.Views
{
    /// <summary>The checks, the repairs and the log, behind one tab.</summary>
    internal partial class SettingsView : UserControl
    {
        public SettingsView()
        {
            InitializeComponent();

            // The log is read when somebody wants it, which is when they switch to it: it is a file the
            // game writes, so whatever it held when this page was built is stale by the time it is read.
            Sections.SelectionChanged += delegate
            {
                if (Sections.SelectedIndex == 1) LogPage.Reload();
            };
        }

        /// <summary>Switch to the repair controls, for a caller that arrived to fix something.</summary>
        public void ShowInstall()
        {
            Sections.SelectedIndex = 0;
            SetupPage.Reload();
        }

        public MainWindow Host
        {
            get { return SetupPage.Host; }
            set
            {
                SetupPage.Host = value;
                LogPage.Host = value;
            }
        }

        public SetupView Setup { get { return SetupPage; } }

        public LogView Log { get { return LogPage; } }

        public void Reload()
        {
            SetupPage.Reload();
        }
    }
}
