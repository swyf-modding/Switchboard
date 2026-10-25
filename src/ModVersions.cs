using System;
using System.Collections.Generic;
using System.IO;
using ScamWYF.Setup;

namespace Switchboard
{
    /// <summary>
    /// The dlls in the game's BepInEx folders, read once for a whole pass over the mod list.
    /// </summary>
    /// <remarks>
    /// The Get mods tab wants a version out of the file it believes a mod installed, for every row. Cecil
    /// reads a whole assembly to answer that, so doing it per row would read the same folders once per
    /// entry - and the shared library is in most entries. One scan per folder per refresh is the same
    /// information for a fraction of the work.
    /// </remarks>
    internal sealed class LocalFiles
    {
        private readonly List<PluginEntry> _plugins;
        private readonly List<PluginEntry> _core;

        private LocalFiles(List<PluginEntry> plugins, List<PluginEntry> core)
        {
            _plugins = plugins;
            _core = core;
        }

        /// <summary>Both folders under one install, or an empty set when there is no install.</summary>
        public static LocalFiles Read(GameInstall install)
        {
            var none = new List<PluginEntry>();

            if (install == null) return new LocalFiles(none, none);

            return new LocalFiles(
                PluginScanner.Scan(install.PluginsFolder, null),
                PluginScanner.Scan(install.BepInExCore, null));
        }

        /// <summary>
        /// One file by the folder the catalogue puts it in. Null when it is not there, which is the
        /// answer for a mod that is not installed.
        /// </summary>
        public PluginEntry Find(ModFolder folder, string fileName)
        {
            return (folder == ModFolder.Core ? _core : _plugins).FirstOrDefaultNamed(fileName);
        }
    }

    /// <summary>
    /// What of one catalogue entry is on disk, and what the file there says it is.
    /// </summary>
    /// <remarks>
    /// Presence and the reviewed bytes, in one pass, because they answer the same question: is this mod
    /// installed, and is what is installed the release the list recorded. The version is read from the
    /// file rather than from the release tag, for the same reason the install confirmation shows it -
    /// the version inside the dll is the one that will load.
    /// </remarks>
    internal sealed class InstalledMod
    {
        /// <summary>How many files the entry installs.</summary>
        public int Expected { get; private set; }

        /// <summary>How many of them are in the game folder.</summary>
        public int Present { get; private set; }

        /// <summary>How many present files the catalogue can be compared against.</summary>
        public int Hashed { get; private set; }

        /// <summary>How many of those are still the bytes the catalogue recorded.</summary>
        public int Matching { get; private set; }

        /// <summary>
        /// The version inside the entry's own plugin file on disk, or null. Falls back to the shared
        /// library's version when the entry ships no plugin of its own, which is the whole of the
        /// shared library's entry.
        /// </summary>
        public string Version { get; private set; }

        /// <summary>Whether anything of this entry is on disk at all.</summary>
        public bool Any { get { return Present > 0; } }

        /// <summary>Whether every file the entry installs is there.</summary>
        public bool Complete { get { return Expected > 0 && Present == Expected; } }

        /// <summary>
        /// Whether every file that could be checked is still the one the list recorded. True when there
        /// was nothing to check, which is the unpinned case rather than a clean bill of health - the
        /// callers that care also look at <see cref="Hashed"/>.
        /// </summary>
        public bool Intact { get { return Hashed == 0 || Matching == Hashed; } }

        /// <summary>Read one entry's files against an install. Never throws.</summary>
        public static InstalledMod Read(ModSource source, GameInstall install, LocalFiles local)
        {
            var result = new InstalledMod { Expected = source.Files.Length };
            if (install == null || local == null) return result;

            string fromPlugin = null;
            string fromAny = null;

            foreach (var file in source.Files)
            {
                var path = Path.Combine(ModCatalogue.FolderFor(install, file.Folder), file.FileName);
                if (!File.Exists(path)) continue;

                result.Present++;

                // Only a mod's own code, and only what the catalogue can be compared against. The shared
                // library is skipped: several entries ship their own copy, so whichever was installed
                // last is the one on disk, and calling that a mismatch would be wrong.
                if (file.IsPinned && file.Folder == ModFolder.Plugins)
                {
                    result.Hashed++;
                    try
                    {
                        if (string.Equals(ModInstaller.Sha256Of(path), file.Sha256,
                                          StringComparison.OrdinalIgnoreCase))
                        {
                            result.Matching++;
                        }
                    }
                    catch (Exception)
                    {
                        // A file that cannot be read is not a file that matches.
                    }
                }

                var entry = local.Find(file.Folder, file.FileName);
                var version = entry != null ? entry.DisplayVersion : null;
                if (string.IsNullOrEmpty(version)) continue;

                if (fromAny == null) fromAny = version;
                if (fromPlugin == null && file.ExpectsPluginAttribute) fromPlugin = version;
            }

            result.Version = fromPlugin ?? fromAny;
            return result;
        }
    }

    /// <summary>
    /// The three versions one entry can be described by: the release the mod list lists, the newest
    /// release GitHub has, and the version in the file that is on disk.
    /// </summary>
    /// <remarks>
    /// One object rather than three columns doing their own comparisons, because every interesting
    /// question is between two of them - is the list behind the author, is the disk behind the list, is
    /// the disk the newest release - and answering one of those in the view and another in a click
    /// handler is how a row ends up disagreeing with itself about whether a mod needs updating.
    ///
    /// Nothing here talks to the network or to a control: the caller fetches, passes what it got, and
    /// the answers are the same for the list and for the detail line under it.
    /// </remarks>
    internal sealed class ModVersions
    {
        public ModVersions(ModSource source, string latest, string local)
        {
            // The pin, not the tag being installed: "what the list says" is the thing both of the
            // other two are measured against.
            Listed = source != null ? source.ApprovedVersion : null;
            Latest = string.IsNullOrEmpty(latest) ? null : latest;
            Local = string.IsNullOrEmpty(local) ? null : local;
        }

        /// <summary>The release the list pins, or null when the entry is not pinned.</summary>
        public string Listed { get; private set; }

        /// <summary>GitHub's newest published release, or null when it was not read.</summary>
        public string Latest { get; private set; }

        /// <summary>The version in the file on disk, or null when there is none.</summary>
        public string Local { get; private set; }

        /// <summary>
        /// Whether GitHub's newest release is a different release from the one the list pins.
        /// </summary>
        /// <remarks>
        /// Unpinned entries are never different: the release the list makes installable is the newest
        /// one by definition, so there is nothing to deviate from.
        /// </remarks>
        public bool LatestDiffers
        {
            get { return Listed != null && Latest != null && !VersionTag.Same(Listed, Latest); }
        }

        /// <summary>
        /// Whether the newest release is newer than the pin - which is the case worth offering an
        /// install for. A pin that is not the newest release is either ahead of it (a rolled-back
        /// release, or a stable pin and a prerelease newer than it) or unreadable, and neither is
        /// something to push a user towards.
        /// </summary>
        public bool LatestIsNewer
        {
            get { return LatestDiffers && UpdateChecker.IsUpdateAvailable(Listed, Latest); }
        }

        /// <summary>Whether what is installed is the release the list pins.</summary>
        public bool LocalDiffers
        {
            get { return Listed != null && Local != null && !VersionTag.Same(Listed, Local); }
        }

        /// <summary>Whether what is installed is GitHub's newest release.</summary>
        public bool LocalIsLatest
        {
            get { return Latest != null && Local != null && VersionTag.Same(Local, Latest); }
        }
    }

    /// <summary>
    /// Whether two version strings name the same release.
    /// </summary>
    /// <remarks>
    /// The tag is "v1.2.1" and the version stamped into the dll is "1.2.1+g5022e10", and those are the
    /// same release. So a string comparison would report a deviation on every single row, which is worse
    /// than reporting none. The parsing is <see cref="UpdateChecker"/>'s, deliberately: two readers of
    /// "what version is this" that disagree about a leading v or build metadata would be the same bug
    /// in two places, and the switchboard's own update check is where that rule is already pinned down.
    /// </remarks>
    internal static class VersionTag
    {
        public static bool Same(string left, string right)
        {
            if (left == null || right == null) return false;

            var one = left.Trim();
            var two = right.Trim();
            if (string.Equals(one, two, StringComparison.OrdinalIgnoreCase)) return true;

            int[] leftNumbers, rightNumbers;
            string leftPrerelease, rightPrerelease;
            UpdateChecker.Parse(one, out leftNumbers, out leftPrerelease);
            UpdateChecker.Parse(two, out rightNumbers, out rightPrerelease);

            // Nothing readable on either side: equal only if they were equal as text, which was
            // answered above.
            if (leftNumbers.Length == 0 || rightNumbers.Length == 0) return false;

            // "1.2.1" and "1.2.1.0" are the same release; the shorter one is not missing a number, it
            // just stops, and the rest is zero.
            var width = Math.Max(leftNumbers.Length, rightNumbers.Length);
            for (var i = 0; i < width; i++)
            {
                var a = i < leftNumbers.Length ? leftNumbers[i] : 0;
                var b = i < rightNumbers.Length ? rightNumbers[i] : 0;
                if (a != b) return false;
            }

            return string.Equals(leftPrerelease ?? "", rightPrerelease ?? "", StringComparison.OrdinalIgnoreCase);
        }
    }
}
