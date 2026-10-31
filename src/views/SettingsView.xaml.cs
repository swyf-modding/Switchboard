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
