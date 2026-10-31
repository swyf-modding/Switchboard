using System;

namespace Switchboard
{
    /// <summary>
    /// What the Browse tab offers for one entry, and what it says about it, with no controls involved.
    /// </summary>
    /// <remarks>
    /// Out of the view because two review rounds found bugs in exactly this logic and nothing could
    /// reach it: the button read "Update to v1.3" while installing the pinned release, and "partly
    /// installed" counted the shared library as progress toward a mod that was not there. Both are
    /// strings decided from state, and state is testable.
    /// </remarks>
    internal static class ModActions
    {
        /// <summary>Whether there is anything to do: installed, complete and unchanged means there is not.</summary>
        public static bool CanInstall(InstalledMod installed)
        {
            if (installed == null) return true;

            // Intact matters: a file that no longer matches the recorded hash is the one case where
            // installing the reviewed release again is the fix, and no other button restores it. The
            // newest-release link installs something nobody reviewed, which is not a repair.
            return !installed.Any || installed.NeedsRepair || !installed.Intact;
        }

        /// <summary>
        /// The label on the one button that installs. Always the release the list reviewed; the newest
        /// release is a link elsewhere, because a call to action that skips the recorded hashes is a
        /// mislabel however it is worded.
        /// </summary>
        public static string AcceptLabel(InstalledMod installed, ModSource source, string listedTag,
                                         string installedVersion)
        {
            if (installed != null && installed.NeedsRepair) return "Repair installation";

            if (installed != null && installed.Any && !installed.Intact)
            {
                return "Reinstall reviewed (" + listedTag + ")";
            }

            var version = string.IsNullOrEmpty(installedVersion) ? listedTag : installedVersion;

            if (installed != null && installed.Any) return "Installed (" + version + ")";

            return source != null && source.IsPinned
                ? "Install reviewed (" + listedTag + ")"
                : "Install (" + listedTag + ")";
        }

        /// <summary>The right-hand column: what is on disk, and whether anything is worth doing about it.</summary>
        public static string Status(InstalledMod installed, ModVersions versions)
        {
            if (installed == null || !installed.Any) return "not installed";

            // Installed but short of a file it ships, which is the only case a repair helps with.
            if (installed.NeedsRepair)
            {
                return installed.Missing == 1
                    ? "needs repair (1 file missing)"
                    : "needs repair (" + installed.Missing + " files missing)";
            }

            if (!installed.Intact) return "modified";

            var text = string.IsNullOrEmpty(installed.Version) ? "installed" : installed.Version + " installed";
            if (versions != null && versions.LocalDiffers) text = versions.Local + " installed";
            if (versions != null && versions.LatestIsNewer) text += " - update to " + versions.Latest;

            return text;
        }
    }
}
