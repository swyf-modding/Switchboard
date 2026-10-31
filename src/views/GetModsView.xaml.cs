using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Switchboard.Views
{
    /// <summary>
    /// One catalogue row. <see cref="Release"/> is what the install path uses; the rest is for display.
    /// </summary>
    /// <remarks>
    /// Observable because the installed column is re-read from disk after every install and after any
    /// change on the Mods tab. The old version mutated the sub-item text in place, which worked but left
    /// no way to tell the list that it had changed.
    ///
    /// A row carries three versions, not one: the release the list makes installable, GitHub's newest
    /// release, and what is in the file on disk. They are held together in <see cref="Versions"/> so the
    /// columns and the sentence under the list cannot disagree about which of them differ.
    /// </remarks>
    internal sealed class ReleaseRow : INotifyPropertyChanged
    {
        private readonly string _newestProblem;
        private InstalledMod _installed;
        private ModVersions _versions;

        public ReleaseRow(ReleaseInfo release, ReleaseInfo newest, string newestProblem, InstalledMod installed)
        {
            Release = release;
            Newest = newest;
            _newestProblem = newestProblem;
            _installed = installed;
            _versions = new ModVersions(release.Source, newest != null ? newest.TagName : null,
                                        installed != null ? installed.Version : null);
        }

        /// <summary>The release Install installs: the pinned one when the entry pins one.</summary>
        public ReleaseInfo Release { get; private set; }

        /// <summary>
        /// GitHub's newest release, when it could be read. Null when the lookup failed - which is a
        /// row that still offers the listed release rather than one that disappears from the list.
        /// </summary>
        public ReleaseInfo Newest { get; private set; }

        /// <summary>Why GitHub's newest release could not be read, or null.</summary>
        public string NewestProblem { get { return _newestProblem; } }

        public ModVersions Versions { get { return _versions; } }

        /// <summary>
        /// The name with its owner when the mod comes from outside the project: a third-party entry
        /// has no shared review behind it, so the list says whose repository it points at.
        /// </summary>
        public string Name
        {
            get
            {
                return Release.Source.IsThirdParty
                    ? Release.Source.DisplayName + " (" + Release.Source.Owner + ")"
                    : Release.Source.DisplayName;
            }
        }
        public string Tag { get { return Release.TagName; } }
        public string Size { get { return Http.Describe(Release.Package.Size); } }
        public string What { get { return Release.Source.Summary; } }

        /// <summary>
        /// Whether this entry is pinned to a reviewed release, in the words the list uses. The
        /// list is where a person chooses, so it is where the difference has to be visible.
        /// </summary>
        public string Reviewed
        {
            get
            {
                if (Release.Source.IsVerified) return "yes";
                if (Release.Source.IsPinned) return "partly";
                return "no";
            }
        }

        /// <summary>
        /// GitHub's newest release, and whether it is the release the list lists. The suffix is the
        /// deviation this column exists to show: "v1.4.0 (newer)" beside a listed "v1.2.1", rather
        /// than two tags the eye has to compare itself.
        /// </summary>
        public string Latest
        {
            get
            {
                if (_versions.Latest == null) return "unknown";
                if (!_versions.LatestDiffers) return _versions.Latest;

                return _versions.Latest + (_versions.LatestIsNewer ? " (newer)" : " (differs)");
            }
        }

        /// <summary>Whether any of the entry's files are on disk.</summary>
        public bool IsInstalled { get { return _installed != null && _installed.Any; } }

        /// <summary>The version inside the installed file, when there is one.</summary>
        public string InstalledVersion { get { return _installed != null ? _installed.Version : null; } }

        /// <summary>
        /// The right-hand column, in the words a person uses: what is on disk, and whether the list
        /// has moved on. The version comparison is the same one the tab used to spread over three
        /// columns; there is nowhere else for it to be wrong now.
        /// </summary>
        public string Status
        {
            get
            {
                if (_installed == null || !_installed.Any) return "not installed";
                if (!_installed.Complete) return "partly installed";
                if (!_installed.Intact) return "modified";

                var version = string.IsNullOrEmpty(_installed.Version) ? "installed" : _installed.Version + " installed";
                if (_versions.LocalDiffers) version = _versions.Local + " installed";
                if (_versions.LatestIsNewer) version += " - update to " + _versions.Latest;

                return version;
            }
        }

        public IBrush StatusBrush
        {
            get
            {
                if (_installed == null || !_installed.Any) return Palette.Brush(Palette.Muted);
                if (!_installed.Intact) return Palette.Brush(Palette.Warn);
                return Palette.Brush(_versions.LatestIsNewer || _versions.LocalDiffers ? Palette.Accent : Palette.Text);
            }
        }

        /// <summary>Whether this row offers installing GitHub's newest release over the pin.</summary>
        public bool CanInstallLatest
        {
            get { return Newest != null && _versions.LatestIsNewer; }
        }

        /// <summary>
        /// What is installed, in the terms that matter: the version inside the file, and whether that
        /// is the release the list lists. "installed" on its own hides the interesting case - the right
        /// files, the wrong release - and that is the one a person needs told.
        /// </summary>
        public string Installed
        {
            get
            {
                if (_installed == null || !_installed.Any) return "not installed";

                if (!_installed.Complete)
                {
                    return "partly (" + _installed.Present + "/" + _installed.Expected + ")";
                }

                if (!_installed.Intact)
                {
                    return (string.IsNullOrEmpty(_installed.Version) ? "installed" : _installed.Version) + " (differs)";
                }

                var version = string.IsNullOrEmpty(_installed.Version) ? "installed" : _installed.Version;
                return _versions.LocalDiffers ? version + " (not listed)" : version;
            }
        }

        /// <summary>
        /// The Latest cell goes loud only when it differs, so a list of current mods stays quiet and
        /// the one entry out of step is what the eye lands on.
        /// </summary>
        public IBrush LatestBrush
        {
            get { return Palette.Brush(_versions.LatestDiffers ? Palette.Accent : Palette.Text); }
        }

        /// <summary>Same rule for the installed column: only a deviation is coloured.</summary>
        public IBrush InstalledBrush
        {
            get
            {
                var loud = _installed != null && _installed.Any && (!_installed.Intact || _versions.LocalDiffers);
                return Palette.Brush(loud ? Palette.Warn : Palette.Text);
            }
        }

        /// <summary>
        /// Re-read what is on disk. Called after an install, and after the Mods tab changes something;
        /// the network answers in this row have not changed, so neither has the latest release.
        /// </summary>
        public void SetInstalled(InstalledMod installed)
        {
            _installed = installed;
            _versions = new ModVersions(Release.Source, Newest != null ? Newest.TagName : null,
                                        installed != null ? installed.Version : null);
            Raise("Installed");
            Raise("LatestBrush");
            Raise("InstalledBrush");
            Raise("Status");
            Raise("StatusBrush");
        }

        /// <summary>
        /// The three versions for the chosen row, and only the parts that are not the plain case.
        /// </summary>
        /// <remarks>
        /// The columns are for scanning; this is for the row that has been chosen. It is the only place
        /// that says a version out loud rather than making two cells be compared, and the only place
        /// that can say what the button above it would do - so it carries the versions, then the
        /// deviations, separated rather than written out as sentences. A row that is current says four
        /// things and stops; the paragraphs this replaced said the same four things in forty words, on a
        /// line that sits under the table all the time.
        /// </remarks>
        public string Describe()
        {
            var parts = new List<string>(6);

            parts.Add(Name);
            parts.Add(_versions.Listed != null ? "listed " + _versions.Listed : "no listed release");
            parts.Add("newest " + (_versions.Latest ?? "unknown"));

            if (_installed == null || !_installed.Any) parts.Add("not installed");
            else if (!_installed.Complete)
                parts.Add(_installed.Present + "/" + _installed.Expected + " files installed");
            else if (string.IsNullOrEmpty(_installed.Version)) parts.Add("installed");
            else parts.Add(_installed.Version + " installed");

            if (_versions.LatestIsNewer) parts.Add("install latest takes the unreviewed " + _versions.Latest);
            else if (_versions.LatestDiffers) parts.Add("nothing newer than the pin");

            if (_installed != null && _installed.Any && !_installed.Intact)
                parts.Add("a file is not the one the list recorded");

            if (_versions.LocalDiffers) parts.Add("installed is not the listed release");
            else if (_versions.LocalIsLatest && _versions.LatestDiffers) parts.Add("installed is the newest");

            if (_newestProblem != null) parts.Add("newest unreadable: " + _newestProblem);

            return string.Join(" \u00b7 ", parts.ToArray());
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string property)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(property));
        }
    }

    /// <summary>
    /// The Get mods tab: fetch a release from GitHub, or install a dll from a URL.
    /// </summary>
    /// <remarks>
    /// Nothing is installed without a confirmation that names the files, the version read out of each
    /// file, the folder each is going into, and anything it would replace. The version in the URL is not
    /// shown as the version, because the version inside the file is the one that will actually load.
    ///
    /// The dialogs are asynchronous here, which is the one shape that changed from the .NET Framework
    /// build: Avalonia's ShowDialog is a task, so every step that asks a question is awaited rather than
    /// blocking. The network work still runs on a background thread, and the confirmation that follows it
    /// runs back on the UI thread.
    /// </remarks>
    public partial class GetModsView : UserControl
    {
        public MainWindow Host { get; set; }

        private readonly ObservableCollection<ReleaseRow> _rows = new ObservableCollection<ReleaseRow>();
        private volatile bool _busy;

        private string _pendingOwner;
        private string _pendingRepo;

        public GetModsView()
        {
            InitializeComponent();
            PurposeText.Text = Purpose;
            List.ItemsSource = _rows;

            // The tab is not looked at until it is opened, and the catalogue is not fetched until
            // then either. Without this the tab would be a blank table for as long as nobody opened
            // it, which reads as broken rather than as "not looked at yet".
            Reload();
        }

        /// <summary>
        /// Re-read what is on disk and update the installed column, without going near the network.
        /// </summary>
        /// <remarks>
        /// Called after an install, and after the Mods tab changes something. The column is derived from
        /// the filesystem, so re-reading it is a handful of File.Exists calls plus one Cecil pass over
        /// each folder per refresh - and doing that instead of re-running the whole lookup is the
        /// difference between the row updating immediately and the user being told a mod they just
        /// installed is "not installed" until they switch tabs and back.
        ///
        /// Deliberately does not touch the status line or re-fetch releases: an install has just put a
        /// message there worth reading, and the tag numbers have not changed.
        /// </remarks>
        public void RefreshInstalled()
        {
            var local = LocalFiles.Read(Host.Install);

            foreach (var row in _rows)
            {
                row.SetInstalled(InstalledMod.Read(row.Release.Source, Host.Install, local));
            }

        }

        public void Reload()
        {
            Summary.Text = "Not checked yet.";
            ToolTip.SetTip(Summary, null);
            _rows.Clear();
            ShowEmpty("Not checked yet", "The catalogue is read from GitHub when this tab is opened.");
            UpdateButtons();
        }

        /// <summary>
        /// What this tab is for, in one line. Shown under the tab strip.
        /// </summary>
        /// <remarks>
        /// The warning at the end is not decoration. Installing a mod puts code in the game folder
        /// that the game will load and run, and that is worth saying at the top of the tab rather
        /// than only in the small print at the bottom of it.
        /// </remarks>
        public string Purpose
        {
            get
            {
                return "Mods the catalogue carries. Click one for what it does and how to install it.";
            }
        }

        /// <summary>
        /// The first few names from a list, and a count of the rest.
        /// </summary>
        /// <remarks>
        /// The status line names every problem in full, so this does not have to: it names enough that a
        /// person recognises their list and stops. The alternative is a report that is a paragraph long
        /// for a catalogue of nine mods, which is how a status line stops being read.
        /// </remarks>
        private static string NameFew(List<string> names)
        {
            const int shown = 3;

            if (names.Count <= shown) return string.Join(", ", names.ToArray());

            return string.Join(", ", names.GetRange(0, shown).ToArray()) + " and " + (names.Count - shown) + " more";
        }

        /// <summary>
        /// The checked answer in as many words as fit on one line: how many entries, whether the pins
        /// hold, and a count for each kind of thing that is not as listed. No names.
        /// </summary>
        /// <remarks>
        /// It sits under a table and is repeated on the window's status bar, so it is read at a
        /// glance or not at all. Which entry is out of step is already in the columns - Latest says
        /// "(newer)", Installed says "(not listed)", and the selected row says the details - so
        /// naming them here was the same information twice, as a paragraph.
        /// </remarks>
        private string ShortReport(int reviewed, List<string> behind, List<string> problems,
                                   List<string> unreadable, List<string> notListed, bool fallback)
        {
            var text = new StringBuilder();
            text.Append(_rows.Count).Append(_rows.Count == 1 ? " mod" : " mods");
            if (fallback) text.Append(" (built-in list)");

            // Said only when there is something to count: "all pinned" about no mods at all is the
            // kind of sentence that makes a person stop believing the status line.
            if (_rows.Count > 0)
            {
                if (reviewed == _rows.Count) text.Append(" \u00b7 all pinned");
                else if (reviewed == 0) text.Append(" \u00b7 none pinned");
                else
                {
                    text.Append(" \u00b7 ").Append(reviewed).Append(" of ").Append(_rows.Count)
                        .Append(" pinned");
                }
            }

            if (behind.Count > 0) text.Append(" \u00b7 ").Append(behind.Count).Append(" newer");
            if (problems.Count > 0) text.Append(" \u00b7 ").Append(problems.Count).Append(" unreadable");
            if (unreadable.Count > 0) text.Append(" \u00b7 ").Append(unreadable.Count).Append(" newest unknown");
            if (notListed.Count > 0)
                text.Append(" \u00b7 ").Append(notListed.Count).Append(" not the listed release");

            return text.ToString();
        }

        /// <summary>
        /// The same answer spelled out, for the tooltip: the list it came from, the count of reviewed
        /// entries, and every name and reason behind the counts in the line above.
        /// </summary>
        /// <remarks>
        /// Every clause ends its own sentence. It used to concatenate a list of names straight into
        /// the next count, so a failed read ("...or have no published release") ran into the results
        /// ("3 have a newer release...") with nothing between them.
        /// </remarks>
        private string LongReport(string listNote, int reviewed, List<string> behind,
                                  List<string> problems, List<string> unreadable, List<string> notListed)
        {
            var text = new StringBuilder();
            text.Append(_rows.Count).Append(_rows.Count == 1 ? " mod" : " mods")
                .Append(" checked against GitHub, from the ").Append(listNote).Append(".");

            if (_rows.Count > 0)
            {
                if (reviewed == _rows.Count) text.Append(" Every one is pinned to a reviewed release.");
                else if (reviewed == 0)
                    text.Append(" None are pinned to a reviewed release, so Install says so for each.");
                else
                {
                    text.Append(" ").Append(reviewed).Append(" of ").Append(_rows.Count)
                        .Append(" are pinned to a reviewed release; the rest are listed without pins.");
                }
            }

            if (problems.Count > 0)
            {
                text.Append(" ").Append(problems.Count).Append(" could not be read: ")
                    .Append(NameFew(problems)).Append(".");
            }

            if (behind.Count > 0)
            {
                text.Append(" ").Append(behind.Count)
                    .Append(" have a newer release than the list lists: ")
                    .Append(NameFew(behind)).Append(".");
            }

            if (notListed.Count > 0)
            {
                text.Append(" ").Append(notListed.Count)
                    .Append(" have something installed that is not the listed release: ")
                    .Append(NameFew(notListed)).Append(".");
            }

            if (unreadable.Count > 0)
            {
                text.Append(" GitHub's newest release could not be read for ").Append(unreadable.Count)
                    .Append(", so it is not compared: ").Append(NameFew(unreadable)).Append(".");
            }

            return text.ToString();
        }

        /// <summary>Show the empty state in place of the table, with the given words.</summary>
        private void ShowEmpty(string title, string hint)
        {
            EmptyTitle.Text = title;
            EmptyHint.Text = hint;
            Empty.IsVisible = true;
            List.IsVisible = false;
        }

        /// <summary>Put the list back, once there is something in it.</summary>
        private void HideEmpty()
        {
            if (_rows.Count == 0) return;
            Empty.IsVisible = false;
            List.IsVisible = true;
        }

        /// <summary>One row, as its entry is read. Stays in catalogue order because the pass is ordered.</summary>
        private void AddRow(ReleaseRow row)
        {
            _rows.Add(row);
            HideEmpty();
        }

        public void OnTabShown()
        {
            if (_pendingOwner != null && _rows.Count > 0 && !_busy)
            {
                TryStartPendingInstall();
                return;
            }

            if (_rows.Count == 0 && !_busy) CheckForUpdates();
        }

        /// <summary>
        /// Install one catalogue mod, as asked by the website's install button.
        /// </summary>
        /// <remarks>
        /// The catalogue has to be read before the row exists, so a request that arrives
        /// before the list does is kept and started when the lookup lands. When the list
        /// is already here the install starts at once. Either way it is the normal
        /// InstallSelected path: same confirmation, same checks, no shortcut.
        /// </remarks>
        public void RequestInstall(string owner, string repo)
        {
            owner = (owner ?? "").Trim();
            repo = (repo ?? "").Trim();

            if (owner.Length == 0 || repo.Length == 0) return;

            _pendingOwner = owner;
            _pendingRepo = repo;

            if (Host == null || !Host.Install.HasBepInEx)
            {
                var missed = _pendingOwner + "/" + _pendingRepo;
                _pendingOwner = null;
                _pendingRepo = null;
                Failed("No BepInEx install found, so " + missed + " has nowhere to go yet. Run the Setup tab first, then try the install button again.");
                return;
            }

            if (_busy) return;

            if (_rows.Count == 0)
            {
                CheckForUpdates();
                return;
            }

            TryStartPendingInstall();
        }

        private void TryStartPendingInstall()
        {
            var owner = _pendingOwner;
            var repo = _pendingRepo;
            if (owner == null || repo == null) return;

            ReleaseRow match = null;
            foreach (var row in _rows)
            {
                if (string.Equals(row.Release.Source.Owner, owner, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(row.Release.Source.Repo, repo, StringComparison.OrdinalIgnoreCase))
                {
                    match = row;
                    break;
                }
            }

            if (match == null)
            {
                _pendingOwner = null;
                _pendingRepo = null;
                Failed(owner + "/" + repo + " is not in the catalogue this Switchboard is showing. It may have been renamed or removed.");
                return;
            }

            _pendingOwner = null;
            _pendingRepo = null;

            List.SelectedItem = match;
            UpdateButtons();
            InstallSelected();
        }

        private void UpdateButtons()
        {
            // Nothing on this tab can do anything without a loader to put a mod into, so the whole
            // install area is off rather than waiting to fail. It used to be left enabled in that
            // state, because the path that found no loader returned before this ran - so the
            // Install button offered to install a mod that had nowhere to go.
            var canInstall = Host != null && Host.Install.HasBepInEx;

            CheckButton.IsEnabled = !_busy && canInstall;
            ExternalButton.IsEnabled = !_busy && canInstall;

            WhereText.Text = canInstall
                ? "Mods are installed into " + Host.Install.PluginsFolder + "."
                : "";
        }

        private ReleaseRow Selected
        {
            get { return List.SelectedItem as ReleaseRow; }
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateButtons();
        }

        /// <summary>
        /// Put the selected row's three versions under the list, or say nothing when there is no
        /// selection. Cleared rather than left stale: a sentence about the previous row under a blank
        /// table is worse than no sentence.
        /// </summary>
        private void OnCheck(object sender, Avalonia.Interactivity.RoutedEventArgs e) { CheckForUpdates(); }

        private void OnExternal(object sender, Avalonia.Interactivity.RoutedEventArgs e) { InstallFromUrl(); }

        /// <summary>
        /// One click on a mod opens what it is and the way to install it.
        /// </summary>
        private void OnRowTapped(object sender, Avalonia.Input.TappedEventArgs e)
        {
            var row = Selected;
            if (row == null) return;

            ShowMod(row);
        }

        private async void ShowMod(ReleaseRow row)
        {
            var source = row.Release.Source;
            var installed = row.IsInstalled;

            string accept;
            bool canInstall = !installed;

            if (installed && row.CanInstallLatest)
            {
                accept = "Update to " + row.Newest.TagName;
                canInstall = true;
            }
            else if (installed)
            {
                accept = "Installed (" + (string.IsNullOrEmpty(row.InstalledVersion) ? row.Release.TagName : row.InstalledVersion) + ")";
            }
            else
            {
                accept = source.IsPinned ? "Install reviewed (" + row.Release.TagName + ")" : "Install " + row.Release.TagName;
            }

            var choice = await Dialogs.ModAsync(
                row.Name,
                source.Summary,
                ModCatalogue.IsAccountName(source.Owner) && ModCatalogue.IsAccountName(source.Repo)
                    ? source.QualifiedName + " on GitHub" + (source.IsThirdParty ? ", third-party" : "")
                    : null,
                Capabilities(row),
                accept,
                canInstall,
                row.CanInstallLatest ? row.Newest.TagName : null);

            if (choice == ModChoice.Reviewed) Install(row.Release, false);
            else if (choice == ModChoice.Newest) Install(row.Newest, true);
        }

        /// <summary>What installing this entry's files lets the game do, in one line.</summary>
        private static string Capabilities(ReleaseRow row)
        {
            // Union, in catalogue order: two files of one mod reach the same things, and saying so
            // twice reads like a longer list of powers than it is.
            var words = new List<string>();
            foreach (var file in row.Release.Source.Files)
            {
                foreach (var capability in file.Capabilities)
                {
                    if (!words.Contains(capability)) words.Add(capability);
                }
            }

            if (words.Count == 0) return null;

            var described = new List<string>(words.Count);
            foreach (var capability in words) described.Add(ModCatalogue.DescribeCapability(capability));
            return string.Join(", ", described.ToArray());
        }

        private void CheckForUpdates()
        {
            if (!Host.Install.HasBepInEx)
            {
                Summary.Text = "There is nowhere to put a mod yet.";
                ToolTip.SetTip(Summary, null);
                ShowEmpty("No loader installed",
                    "BepInEx has to be installed before a mod has somewhere to go. The Setup tab " +
                    "installs it, and then this tab works.");
                UpdateButtons();
                return;
            }

            SetBusy(true, "Checking GitHub for the listed releases...");

            Run(delegate
            {
                var found = new List<Checked>();
                var problems = new List<string>();

                // The live list first; the built-in one when it cannot be reached, with the reason kept
                // so the tab says which list it is showing rather than silently serving a stale one.
                string fallback;
                var sources = ModCatalogue.FetchSources(out fallback);
                var listNote = fallback == null
                    ? "live list"
                    : "built-in list (the live one could not be read: " + fallback + ")";

                // Read once, off the UI thread: Cecil opens every dll in both folders, and doing that per
                // row on the thread that draws the window is the difference between a list that appears
                // and a window that stalls while it does.
                var local = LocalFiles.Read(Host.Install);

                // Empty and saying so, before the first request rather than after the last one: a table
                // still showing the previous answer while it fetches a new one is indistinguishable from
                // one that is not fetching anything.
                OnUi(delegate
                {
                    _rows.Clear();
                    ShowEmpty("Checking", "Reading each entry's release from GitHub.");
                    Summary.Text = "Checking " + sources.Count + " entries...";
                });

                var done = 0;
                foreach (var source in sources)
                {
                    done++;
                    var current = source;
                    var position = done;
                    OnUi(delegate
                    {
                        Summary.Text = "Checking " + current.DisplayName + " (" + position + " of " +
                                       sources.Count + ")...";
                        ToolTip.SetTip(Summary, null);
                    });

                    try
                    {
                        var listed = ModCatalogue.FetchLatest(source);
                        ReleaseInfo newest = listed;
                        string unknown = null;

                        // A pinned entry is the only one where the newest release can be a different
                        // release from the one the list makes installable, so it is the only one worth
                        // a second request - and the only one where the answer is still unknown when
                        // that request fails. An unpinned entry already installs the newest release, so
                        // the tag it just fetched is the answer.
                        //
                        // A failure here is a note on the row, not a missing row: the listed release is
                        // still installable, and saying that is more use than dropping the mod because a
                        // comparison could not be made.
                        if (source.IsPinned)
                        {
                            newest = null;
                            try
                            {
                                newest = ModCatalogue.FetchNewest(source);
                            }
                            catch (Exception ex)
                            {
                                unknown = ex.Message;
                            }
                        }

                        found.Add(new Checked { Release = listed, Newest = newest, NewestProblem = unknown });

                        // Added as it arrives. The pass is two requests per mod, and a list that fills in
                        // over several seconds answers "is it working" without a spinner.
                        var row = new ReleaseRow(listed, newest, unknown,
                                                 InstalledMod.Read(listed.Source, Host.Install, local));
                        OnUi(delegate { AddRow(row); });
                    }
                    catch (Exception ex)
                    {
                        // One unreachable repository must not hide the others. Named by owner and repo
                        // rather than display name, so two entries with the same name stay tellable apart.
                        problems.Add(source.QualifiedName + ": " + ex.Message);
                    }
                }

                OnUi(delegate
                {
                    HideEmpty();

                    var reviewed = 0;
                    foreach (var release in found)
                    {
                        if (release.Release.Source.IsVerified) reviewed++;
                    }

                    // The deviations, tallied per kind. What is counted here is also what the
                    // columns already show - a row's Latest cell says "(newer)", its Installed cell
                    // says "(not listed)" - so the line doesn't repeat the names, it says how many
                    // rows are worth looking at and leaves the list to say which.
                    var behind = new List<string>();
                    var unreadable = new List<string>();
                    var notListed = new List<string>();

                    foreach (var row in _rows)
                    {
                        if (row.CanInstallLatest)
                        {
                            behind.Add(row.Release.Source.DisplayName + " " + row.Newest.TagName + " (lists " +
                                       row.Versions.Listed + ")");
                        }

                        if (row.NewestProblem != null) unreadable.Add(row.Release.Source.DisplayName);

                        if (row.Versions.LocalDiffers) notListed.Add(row.Release.Source.DisplayName);
                    }

                    // One line, because it is read on a status bar and under a table that it must not
                    // push off the screen. Two versions of the same answer: this for the eye, and the
                    // whole sentence - names, reasons, the fallback list's error - behind the tooltip.
                    // The long one used to be on screen, and nine mods of detail is nine mods of
                    // paragraph where the answer was "everything is fine" or "three things are not".
                    var summary = ShortReport(reviewed, behind, problems, unreadable, notListed, fallback != null);
                    var report = LongReport(listNote, reviewed, behind, problems, unreadable, notListed);

                    // Passed to SetBusy rather than assigned separately, so the status line is told what
                    // actually happened. It used to keep saying "Checking GitHub..." after the answer had
                    // arrived, which is the sort of thing that makes a person press the button again.
                    SetBusy(false, summary);
                    ToolTip.SetTip(Summary, report);

                    if (_rows.Count > 0) List.SelectedIndex = 0;

                    // An empty catalogue is a real possibility and a different thing from a failure
                    // to read it, so the empty state says which one this is.
                    if (_rows.Count == 0)
                    {
                        // Named, not listed: ten unreachable repositories used to be ten paragraphs here,
                        // which drew over the buttons below rather than being read.
                        ShowEmpty("Nothing could be read",
                            problems.Count > 0
                                ? "None of the " + problems.Count + " entries answered. The usual reason is " +
                                  "GitHub's hourly limit on this address; the whole list is in the tooltip."
                                : "The catalogue is empty at the moment. Check again later.");

                        if (problems.Count > 0) ToolTip.SetTip(EmptyHint, string.Join(Environment.NewLine, problems.ToArray()));
                    }
                    else
                    {
                        Empty.IsVisible = false;
                    }

                    UpdateButtons();
        
                    // A website request that arrived before the list did starts now that
                    // the rows exist. Inside the same UI turn, so no second refresh.
                    if (_pendingOwner != null) TryStartPendingInstall();
                });
            });
        }

        /// <summary>
        /// One entry as the refresh found it: what the list installs, what GitHub has, and why that
        /// second answer is missing when it is.
        /// </summary>
        /// <remarks>
        /// A flat record rather than three parallel lists, because the three answers are built on the
        /// download thread and read on the UI thread in the same order - and a slip in that order would
        /// put one mod's newest release in another mod's row.
        /// </remarks>
        private sealed class Checked
        {
            public ReleaseInfo Release;
            public ReleaseInfo Newest;
            public string NewestProblem;
        }

        private void InstallSelected()
        {
            var row = Selected;
            if (row == null) return;

            Install(row.Release, false);
        }

        /// <summary>
        /// Install GitHub's newest release instead of the one the list pins.
        /// </summary>
        /// <remarks>
        /// The row already holds that release, because the refresh fetched it to compare the tags, so
        /// this asks GitHub for nothing new. What it does ask for is a different install: the
        /// catalogue's hashes describe the listed release and cannot check this one, so the plan is
        /// built in the mode that says so out loud rather than the one that quietly skips the checks.
        /// </remarks>
        private void InstallLatestSelected()
        {
            var row = Selected;
            if (row == null || !row.CanInstallLatest) return;

            Install(row.Newest, true);
        }

        /// <summary>
        /// Fetch one release, plan it, and confirm it. The only install path either button uses.
        /// </summary>
        /// <param name="latestInstead">
        /// True for the newest release chosen over the listed one - the plan then carries the listed
        /// version so its confirmation can say the hashes did not apply.
        /// </param>
        private void Install(ReleaseInfo release, bool latestInstead)
        {
            string running;
            if (ModManager.GameRunning(out running))
            {
                ShowInform("Close the game first",
                    "The game is running (" + running + ").\n\nBepInEx keeps plugin files locked until it " +
                    "exits, so close the game first.");
                return;
            }

            var token = NewToken();

            // A download is a download, but this one is not the release the list offered, and the
            // status line is where that first becomes visible rather than only in the confirmation.
            SetBusy(true, latestInstead
                ? "Preparing " + release.Source.DisplayName + " " + release.TagName +
                  ", which is not the listed release..."
                : "Preparing " + release.Source.DisplayName + "...");

            Run(delegate
            {
                string folder = null;
                try
                {
                    var fetched = ModInstaller.Fetch(release.Package, token, note => Status(note));
                    folder = fetched.WorkingFolder;

                    var plan = ModInstaller.Plan(release, fetched.UnpackedFolder, Host.Install, latestInstead);
                    var work = folder;

                    OnUi(delegate
                    {
                        SetBusy(false, null);
                        ApplyPlan(plan, work);
                    });
                }
                catch (Exception ex)
                {
                    var message = ex.Message;
                    var work = folder;
                    OnUi(delegate
                    {
                        SetBusy(false, null);
                        if (work != null) ModInstaller.Discard(work);
                        Failed(message);
                    });
                }
            });
        }

        /// <summary>
        /// Show the plan, and install only on an explicit yes.
        /// </summary>
        /// <remarks>
        /// Default is Cancel, and the wording is about what will happen to the disk rather than about
        /// whether the user is sure. Replacing an existing file gets its own confirmation, because that is
        /// the only irreversible part - an install into an empty folder can be undone by deleting a file.
        /// </remarks>
        /// <summary>
        /// Write the plan. The popup that offered the install was the consent, so this does not ask
        /// again; replacing files is still confirmed, because that is the only part with no undo.
        /// </summary>
        private async void ApplyPlan(InstallPlan plan, string folder)
        {
            try
            {
                if (plan.Warnings.Count > 0)
                {
                    Status(string.Join(" ", plan.Warnings.ToArray()));
                }

                if (ModInstaller.ReplacesAnything(plan))
                {
                    var replaced = new List<string>();
                    foreach (var step in plan.Steps)
                    {
                        if (step.IsReplacement) replaced.Add(step.File.FileName + " (" + step.ReplacingVersion + ")");
                    }

                    var second = await Dialogs.ConfirmAsync("Replace existing files?",
                        "This replaces " + string.Join(", ", replaced.ToArray()) + ". " +
                        "There is no undo.",
                        "Replace", DialogSeverity.Warning);

                    if (!second)
                    {
                        ModInstaller.Discard(folder);
                        Status("cancelled - nothing was installed");
                        return;
                    }
                }

                try
                {
                    var written = ModInstaller.Apply(plan);
                    Status("installed " + string.Join(", ", written));
                    RefreshInstalled();
                }
                catch (Exception ex)
                {
                    ModInstaller.Discard(folder);
                    Failed(ex.Message);
                    Host.RefreshModViews();
                    return;
                }

                await OfferPostInstall(plan, folder);
            }
            catch (Exception ex)
            {
                ModInstaller.Discard(folder);
                Failed(ex.Message);
            }
        }

        /// <summary>
        /// Discard the download's working folder. It survives until every optional step is answered,
        /// because the scripts run out of it - discarding it with the install would strand them.
        /// </summary>
        private void FinishInstall(string folder)
        {
            if (folder != null) ModInstaller.Discard(folder);
            Host.RefreshModViews();
        }

        /// <summary>
        /// Offer each optional setup step in turn, after the files are installed.
        /// </summary>
        /// <remarks>
        /// One prompt per step, each deniable on its own: a stream mod's ChatBridge setup can be skipped
        /// while another step still runs, and denying everything leaves the installed files exactly as
        /// they were. Nothing here runs without an explicit yes on its own prompt, and the prompt shows
        /// the script before asking.
        ///
        /// A loop rather than the old recursion, because each step is awaited: the next prompt only
        /// appears once the previous answer, and any script it ran, is finished.
        /// </remarks>
        private async Task OfferPostInstall(InstallPlan plan, string folder)
        {
            foreach (var step in plan.Scripts)
            {
                await OfferStep(step);
            }

            FinishInstall(folder);
        }

        private async Task OfferStep(PostInstallStep step)
        {
            if (!await Dialogs.ConfirmSkippableAsync("Run optional setup?", PromptFor(step), "Run",
                                                     DialogSeverity.Warning))
            {
                Status("skipped " + step.ScriptName + " - the installed files are unchanged");
                return;
            }

            string running;
            if (ModManager.GameRunning(out running))
            {
                // Re-checked per step rather than once per install: the game may have been started in
                // between, and a setup script writing at a running game is refused the same way a file
                // move is.
                Host.Status("The game is running (" + running + "), so " + step.ScriptName +
                            " was not run.", false);
                return;
            }

            SetBusy(true, "Running " + step.ScriptName + "...");

            var run = await CollectScript(step);

            SetBusy(false, null);
            await ReportScript(step, run.Lines, run.ExitCode);
        }

        /// <summary>What one script run produced, once it has finished.</summary>
        private sealed class ScriptRun
        {
            public int ExitCode;
            public List<string> Lines;
        }

        /// <summary>
        /// Run a mod's setup script on a background thread and wait for it.
        /// </summary>
        /// <remarks>
        /// A TaskCompletionSource rather than a plain thread and a callback: the caller is an async flow
        /// now, and it has to wait for the exit code before it can offer the next step. The lines are
        /// collected under a lock because the process's two reader threads fan into one callback.
        /// </remarks>
        private async Task<ScriptRun> CollectScript(PostInstallStep step)
        {
            var collected = new List<string>();
            var gate = new object();
            var done = new TaskCompletionSource<int>();

            new Thread(delegate ()
            {
                try
                {
                    ScriptRunner.RunScript(step.ScriptPath, Host.Install.Path,
                        line => { lock (gate) collected.Add(line); },
                        exitCode => done.TrySetResult(exitCode));
                }
                catch (Exception ex)
                {
                    lock (gate) collected.Add("Could not run " + step.ScriptName + ": " + ex.Message);
                    done.TrySetResult(-1);
                }
            })
            {
                IsBackground = true
            }.Start();

            var exitCode = await done.Task;

            List<string> snapshot;
            lock (gate) snapshot = new List<string>(collected);

            return new ScriptRun { ExitCode = exitCode, Lines = snapshot };
        }

        /// <summary>What the prompt shows: what the step does, then the script itself.</summary>
        private static string PromptFor(PostInstallStep step)
        {
            var text = new StringBuilder();

            text.AppendLine(step.Description);
            text.AppendLine();

            var full = step.ScriptText ?? "";
            var lineCount = full.Length == 0 ? 0 : full.Split('\n').Length;

            const int maxChars = 2000;
            var shown = full.Length > maxChars
                ? full.Substring(0, maxChars) + Environment.NewLine + "... (truncated; " +
                  full.Length + " characters in full)"
                : full;

            text.AppendLine("The script (" + step.ScriptName + ", " + lineCount + " lines) reads:");
            text.AppendLine();
            text.AppendLine(shown);
            text.AppendLine();
            text.AppendLine("It runs now, as you, with the game folder passed as -GameDir. " +
                            "Skipping changes nothing already installed.");

            return text.ToString();
        }

        /// <summary>What happened, as a dialog plus a status line.</summary>
        private async Task ReportScript(PostInstallStep step, List<string> lines, int exitCode)
        {
            var tail = lines.Count <= 20 ? lines : lines.GetRange(lines.Count - 20, 20);
            var output = tail.Count == 0
                ? "It printed nothing."
                : string.Join(Environment.NewLine, tail.ToArray());

            if (exitCode == 0)
            {
                Status(step.ScriptName + " finished.");
                await Dialogs.InformAsync(step.ScriptName + " finished", output);
                return;
            }

            Host.Status(step.ScriptName + " reported problems (exit " + exitCode + ").", false);
            await Dialogs.ErrorAsync(step.ScriptName + " reported problems",
                                     "Exit code " + exitCode + "." + Environment.NewLine +
                                     Environment.NewLine + output);
        }

        private async void InstallFromUrl()
        {
            var entered = await Dialogs.PromptAsync("Install from a URL",
                "A link to a .zip release asset. For mods the list does not carry, so nothing here has " +
                "been reviewed and no hash can be checked against one.", "https://github.com/...");

            if (entered == null) return;
            var url = entered.Trim();
            if (url.Length == 0) return;

            if (!Http.IsAcceptable(url))
            {
                Failed(Http.ExplainRefusal(url));
                return;
            }

            if (!Host.Install.HasBepInEx)
            {
                Failed("No BepInEx install found, so there is nowhere to put a mod. Run the Setup tab first.");
                return;
            }

            string running;
            if (ModManager.GameRunning(out running))
            {
                Failed("The game is running (" + running + "). Close it first - BepInEx keeps plugin files " +
                       "locked until it exits.");
                return;
            }

            // A guessed file name is the one thing that can be got wrong here, and it decides which folder
            // the file lands in. Guessing from the URL is better than making the user type a path, but it
            // is a guess, so it is shown in the confirmation rather than assumed.
            var fileName = GuessFileName(url);
            if (string.IsNullOrEmpty(fileName))
            {
                Failed("The URL does not end in a file name, so there is nothing to call the download. " +
                       "Use a link to a .dll or a .zip.");
                return;
            }

            var token = NewToken();
            SetBusy(true, "Downloading...");

            Run(delegate
            {
                string folder = null;
                try
                {
                    var fetched = ModInstaller.Fetch(
                        new ReleaseAsset(fileName, 0, url, null),
                        token,
                        note => Status(note));

                    folder = fetched.WorkingFolder;

                    var isZip = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

                    var steps = isZip ? FromZip(fetched.UnpackedFolder) : Single(fetched.UnpackedFolder, fileName);
                    if (steps.Count == 0) throw new IOException("nothing installable was found in the download");

                    var plan = new InstallPlan { Steps = steps };
                    var source = new ModSource("(url)", Path.GetFileNameWithoutExtension(fileName),
                        "Downloaded from " + HostOf(url), new ModFile[0]);
                    plan.Release = new ReleaseInfo(source, "(no version)", null,
                        new ReleaseAsset(fileName, 0, url, fetched.Sha256));

                    var work = folder;
                    OnUi(delegate
                    {
                        SetBusy(false, null);
                        ConfirmUrl(plan, work, HostOf(url), steps[0].Guid == null);
                    });
                }
                catch (Exception ex)
                {
                    var message = ex.Message;
                    var work = folder;
                    OnUi(delegate
                    {
                        SetBusy(false, null);
                        if (work != null) ModInstaller.Discard(work);
                        Failed(message);
                    });
                }
            });
        }

        private List<InstallStep> Single(string folder, string fileName)
        {
            var steps = new List<InstallStep>();
            var path = Path.Combine(folder, fileName);

            if (!File.Exists(path)) throw new IOException("the download did not arrive as " + fileName);

            var entry = PluginScanner.Scan(folder, null).FirstOrDefaultNamed(fileName);
            if (entry == null) throw new IOException(fileName + " could not be read as a .NET assembly");

            steps.Add(new InstallStep
            {
                File = new ModFile(fileName, ModFolder.Plugins, true),
                SourcePath = path,
                DestinationPath = Path.Combine(Host.Install.PluginsFolder, fileName),
                Version = entry.DisplayVersion,
                PluginName = entry.DisplayName,
                Guid = entry.Guid,
                ReplacingVersion = ExistingVersion(fileName)
            });

            return steps;
        }

        private List<InstallStep> FromZip(string folder)
        {
            var steps = new List<InstallStep>();

            foreach (var file in Directory.GetFiles(folder, "*.dll"))
            {
                var name = Path.GetFileName(file);
                var entry = PluginScanner.Scan(folder, null).FirstOrDefaultNamed(name);
                if (entry == null) continue;

                var wantsPlugin = !string.IsNullOrEmpty(entry.Guid);

                steps.Add(new InstallStep
                {
                    File = new ModFile(name, wantsPlugin ? ModFolder.Plugins : ModFolder.Core, wantsPlugin),
                    SourcePath = file,
                    DestinationPath = Path.Combine(
                        ModCatalogue.FolderFor(Host.Install, wantsPlugin ? ModFolder.Plugins : ModFolder.Core), name),
                    Version = entry.DisplayVersion,
                    PluginName = entry.DisplayName,
                    Guid = entry.Guid,
                    ReplacingVersion = ExistingVersion(name)
                });
            }

            return steps;
        }

        private string ExistingVersion(string fileName)
        {
            var folders = new[] { Host.Install.PluginsFolder, Host.Install.BepInExCore };

            foreach (var folder in folders)
            {
                var path = Path.Combine(folder, fileName);
                if (!File.Exists(path)) continue;

                var entry = PluginScanner.Scan(folder, null).FirstOrDefaultNamed(fileName);
                return entry != null && !string.IsNullOrEmpty(entry.DisplayVersion)
                    ? entry.DisplayVersion
                    : "a copy with no version";
            }

            return null;
        }

        /// <summary>
        /// The URL install's confirmation.
        /// </summary>
        /// <remarks>
        /// Separate from the catalogue's because there is no list behind this one. The host is named, the
        /// file's own name and version are shown, and a file with no [BepInPlugin] is called out - it would
        /// be copied into core, which is harmless, but it will also do nothing, and a user who does not
        /// know that will conclude the mod is broken.
        /// </remarks>
        private async void ConfirmUrl(InstallPlan plan, string folder, string host, bool looksInert)
        {
            try
            {
                var text = new StringBuilder();

                // Short, like the catalogue's: the host and the files are the facts, and the rest of the
                // box is the warning that there is nothing behind this URL but the URL.
                text.AppendLine("There is no list behind this URL - the game will load and run whatever it serves.");
                text.AppendLine();

                foreach (var step in plan.Steps)
                {
                    // Relative and built with Path.Combine, so the separator matches this platform.
                    var folderName = Path.Combine("BepInEx",
                        step.File.Folder == ModFolder.Core ? "core" : "plugins");
                    var identity = string.IsNullOrEmpty(step.PluginName)
                        ? step.File.FileName
                        : step.PluginName + "  " + step.Version;

                    text.AppendLine("  " + identity);
                    text.AppendLine("    -> " + Path.Combine(folderName, step.File.FileName));

                    if (step.IsReplacement) text.AppendLine("    replaces " + step.ReplacingVersion);
                }

                text.AppendLine();
                text.AppendLine("SHA-256 " + plan.Release.Package.Sha256);

                if (looksInert)
                {
                    text.AppendLine();
                    text.AppendLine("No [BepInPlugin]: this is a library. Harmless, and it does nothing on its own.");
                }

                if (!await Dialogs.ConfirmAsync("Install from " + host + "?", text.ToString(), "Install",
                                                DialogSeverity.Warning))
                {
                    ModInstaller.Discard(folder);
                    Status("cancelled - nothing was installed");
                    return;
                }

                try
                {
                    var written = ModInstaller.Apply(plan);
                    Status("installed " + string.Join(", ", written));
                    RefreshInstalled();
                }
                catch (Exception ex)
                {
                    Failed(ex.Message);
                }
                finally
                {
                    ModInstaller.Discard(folder);
                }

                Host.RefreshModViews();
            }
            catch (Exception ex)
            {
                ModInstaller.Discard(folder);
                Failed(ex.Message);
            }
        }

        private static string GuessFileName(string url)
        {
            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed)) return null;

            var name = Path.GetFileName(parsed.AbsolutePath);
            if (string.IsNullOrEmpty(name)) return null;

            if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return name;
        }

        private static string HostOf(string url)
        {
            Uri parsed;
            return Uri.TryCreate(url, UriKind.Absolute, out parsed) ? parsed.Host : url;
        }

        private static string NewToken()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        /// <summary>
        /// Run work off the UI thread.
        /// </summary>
        /// <remarks>
        /// A download on the UI thread would freeze the window for its duration and look like a hang.
        /// The thread is background so a download in flight cannot keep the process alive on exit.
        /// </remarks>
        private void Run(Action work)
        {
            new Thread(delegate ()
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    OnUi(delegate
                    {
                        SetBusy(false, null);
                        Failed(ex.Message);
                    });
                }
            })
            {
                IsBackground = true
            }.Start();
        }

        private void OnUi(Action action)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                action();
                return;
            }

            try
            {
                Dispatcher.UIThread.Post(action);
            }
            catch (Exception)
            {
                // The window closed while a download was in flight. There is nobody left to tell.
            }
        }

        private void SetBusy(bool busy, string message)
        {
            // Marshalled even though every current caller is already on the UI thread. This writes to
            // three controls and the window's enabled state, and it is the first thing a new caller
            // reaches for after a worker completes - so it guards itself rather than relying on each
            // call site remembering.
            OnUi(delegate
            {
                _busy = busy;

                if (message != null)
                {
                    Summary.Text = message;
                    // A tooltip is the long version of one particular answer; the next short
                    // message replaces that answer, so it replaces its tooltip with it.
                    ToolTip.SetTip(Summary, null);
                    Host.Status(message, true);
                }

                UpdateButtons();
                Host.SetBusy(busy);
                Host.Pump();
            });
        }

        private void Status(string message)
        {
            OnUi(delegate
            {
                Summary.Text = message;
                ToolTip.SetTip(Summary, null);
                Host.Status(message, true);
            });
        }

        private void Failed(string message)
        {
            // Marshalled for the same reason as SetBusy: a failure is reported from whichever thread
            // the work was started on, and this one ends in a modal dialog.
            OnUi(delegate
            {
                Summary.Text = message;
                Host.Status(message, false);
                ShowError(message);
            });
        }

        /// <summary>
        /// Report a failure without letting the reporting itself become the failure.
        /// </summary>
        /// <remarks>
        /// async void because there is nothing to await here and no caller that could: a dialog is the
        /// end of the failure path. The try/catch is what keeps an exception in the dialog from becoming
        /// an unhandled one on the UI thread, which would take the window down.
        /// </remarks>
        private async void ShowError(string message)
        {
            try
            {
                await Dialogs.ErrorAsync("Could not install", message);
            }
            catch (Exception)
            {
                // Nothing left to show it with.
            }
        }

        private async void ShowInform(string title, string message)
        {
            try
            {
                await Dialogs.InformAsync(title, message);
            }
            catch (Exception)
            {
            }
        }
    }
}
