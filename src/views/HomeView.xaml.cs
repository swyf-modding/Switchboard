using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using ScamWYF.Setup;

namespace Switchboard.Views
{
    /// <summary>
    /// What the window opens on once the loader is installed: is the game ready, what is in there,
    /// and a way to get more.
    /// </summary>
    internal partial class HomeView : UserControl
    {
        private readonly ObservableCollection<ModRow> _rows = new ObservableCollection<ModRow>();

        public HomeView()
        {
            InitializeComponent();
            List.ItemsSource = _rows;
        }

        public MainWindow Host { get; set; }

        public void Reload()
        {
            var install = Host != null ? Host.Install : null;

            GameText.Text = install == null || !install.Exists
                ? "No game found. Set SWYG_GAME_DIR or pass the game folder as an argument."
                : install.Path;

            LoaderText.Text = install == null || !install.HasBepInEx
                ? "BepInEx is not installed yet."
                : "BepInEx " + GameInstall.BepInExVersion + " and the corlib override are in place.";

            FixButton.IsVisible = install != null && install.Exists && !install.HasBepInEx;

            string problem = install == null || !install.Exists
                ? null
                : !install.HasBepInEx
                    ? "Mods need BepInEx. The Settings tab installs it, and there is nothing else to do."
                    : !install.HasCorlibOverride
                        ? "The unstripped corlib is missing, so BepInEx cannot start. Settings can repair that."
                        : null;

            ProblemText.Text = problem ?? "";
            ProblemText.IsVisible = problem != null;

            _rows.Clear();
            if (install != null && install.Exists && install.HasBepInEx)
            {
                foreach (var entry in Scan(install))
                {
                    _rows.Add(new ModRow(entry));
                }
            }

            InstalledHeading.Text = _rows.Count == 0 ? "Installed" : "Installed (" + _rows.Count + ")";
            Empty.IsVisible = _rows.Count == 0;
            List.IsVisible = _rows.Count > 0;
        }

        private static IEnumerable<PluginEntry> Scan(GameInstall install)
        {
            var entries = PluginScanner.Scan(install.PluginsFolder, null);

            // The disabled folder is part of the answer to "what have I got": a mod that is switched
            // off is still installed, and hiding it makes the count disagree with the Mods tab.
            var disabled = Path.Combine(install.BepInExRoot, "plugins_disabled");
            if (Directory.Exists(disabled)) entries.AddRange(PluginScanner.Scan(disabled, null));

            return entries.OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase);
        }

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            if (Host != null) Host.OpenTab(2);
        }

        private void OnFix(object sender, RoutedEventArgs e)
        {
            if (Host != null) Host.OpenTab(3);
        }

        private void OnFindMods(object sender, RoutedEventArgs e)
        {
            if (Host != null) Host.OpenTab(2);
        }

        private void OnRowActivated(object sender, TappedEventArgs e)
        {
            if (Host != null) Host.OpenTab(1);
        }
    }
}
