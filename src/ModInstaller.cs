using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ScamWYF.Setup;

namespace Switchboard
{
    /// <summary>One file that would be written, and what is already there.</summary>
    internal sealed class InstallStep
    {
        public ModFile File;
        public string SourcePath;
        public string DestinationPath;

        /// <summary>The version read out of the downloaded file's metadata, when it has one.</summary>
        public string Version;

        /// <summary>The name its [BepInPlugin] claims, when it has one.</summary>
        public string PluginName;

        public string Guid;

        /// <summary>Set when a file is already at the destination and would be replaced.</summary>
        public string ReplacingVersion;

        public bool IsReplacement { get { return ReplacingVersion != null; } }
    }

    /// <summary>
    /// Everything that would happen, decided before anything happens.
    /// </summary>
    /// <remarks>
    /// The plan is built and shown, and only then applied. A mod is code the game will load on the next
    /// launch, so the user is told what the files are, where they are going, and what is being replaced -
    /// with the name and version read out of the file itself rather than out of the URL, which is the only
    /// version worth trusting.
    /// </remarks>
    internal sealed class InstallPlan
    {
        public ReleaseInfo Release;
        public List<InstallStep> Steps = new List<InstallStep>();
        public List<string> Warnings = new List<string>();

        /// <summary>Optional setup steps, verified present in the download. Never null.</summary>
        public List<PostInstallStep> Scripts = new List<PostInstallStep>();

        /// <summary>
        /// The release the mod list lists, set only when this install deliberately installs a
        /// different one - the newest release, chosen by hand over the reviewed one. Null for every
        /// install the list asked for.
        /// </summary>
        /// <remarks>
        /// Load-bearing rather than decorative: the catalogue's hashes describe the listed release,
        /// so they are not applied here, and the confirmation reads this to say in words that nothing
        /// below has been checked against a review. A plan that skipped those checks silently would
        /// leave the confirmation claiming the checks passed.
        /// </remarks>
        public string ListedVersion;
    }

    /// <summary>
    /// Fetching a mod, and putting it in the game folder.
    /// </summary>
    /// <remarks>
    /// The order is the whole design: download to a temporary folder, unpack, read the metadata with Cecil,
    /// build a plan, show it, and only then copy. Nothing is written into the game folder until a person
    /// has seen what is about to be written, and nothing is ever loaded to find out what a file is -
    /// <see cref="PluginScanner"/> exists precisely so that a mod's code does not run in this process.
    ///
    /// Installing is a copy rather than a move, and the temporary folder is left for the caller to delete.
    /// A copy leaves the downloaded artefact alone, which is what makes it safe to re-run: a second
    /// attempt re-downloads rather than half-installing.
    /// </remarks>
    internal static class ModInstaller
    {
        /// <summary>Where downloads are unpacked. Under TEMP, so it is not inside the game folder.</summary>
        internal static string WorkingFolder(string token)
        {
            var folder = Path.Combine(Path.GetTempPath(), "scamwyf-mods-" + token);
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>Throw away a working folder, ignoring whatever is still held open.</summary>
        internal static void Discard(string folder)
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            catch (Exception)
            {
                // A leftover temp folder is untidy, not dangerous, and failing an install over it would
                // be the wrong trade.
            }
        }

        /// <summary>What a fetch produced, and where it put it.</summary>
    internal sealed class Fetched
    {
        /// <summary>
        /// The temp folder holding the zip and the unpacked copy. This is what Discard takes.
        /// </summary>
        /// <remarks>
        /// Deliberately not the unpacked subfolder. Discarding that leaves the downloaded zip and its
        /// parent behind, so every install would quietly add a copy to %TEMP% and nothing would ever
        /// reclaim it - which is the unbounded growth this project's own docs call out as the reason a
        /// delete has to actually delete.
        /// </remarks>
        public string WorkingFolder;

        public string UnpackedFolder;

        public string Sha256;
    }

    /// <summary>
    /// Download a release's zip and unpack it.
    /// </summary>
    public static Fetched Fetch(ReleaseAsset asset, string token, Action<string> status)
    {
        var folder = WorkingFolder(token);

        if (status != null) status("Downloading " + asset.Name + " (" + Http.Describe(asset.Size) + ")...");

        var zipPath = Path.Combine(folder, asset.Name);
        var actual = Http.Download(asset.DownloadUrl, zipPath, null);

        // GitHub's digest is from the same place as the file, so this catches a mangled download and a
        // CDN that served something else - not a compromised release. Reported either way, because a
        // number the user can check is worth more than silence.
        if (!string.IsNullOrEmpty(asset.Sha256) &&
            !string.Equals(asset.Sha256, actual, StringComparison.OrdinalIgnoreCase))
        {
            Discard(folder);
            throw new IOException("the download does not match the checksum GitHub published for it, so it " +
                                  "has been discarded and nothing was installed. Either the download was " +
                                  "corrupted or the file served was not the one published");
        }

        if (status != null) status("Unpacking...");

        var unpacked = Path.Combine(folder, "unpacked");
        Directory.CreateDirectory(unpacked);
        Unpack(zipPath, unpacked);

        return new Fetched
        {
            WorkingFolder = folder,
            UnpackedFolder = unpacked,
            Sha256 = actual
        };
    }

        /// <summary>
        /// Unpack a zip, refusing any entry that would land outside the destination.
        /// </summary>
        /// <remarks>
        /// Zip-slip: an entry named "..\..\Windows\System32\..." is legal in the archive format and would
        /// otherwise write wherever it liked. Every entry's full path is checked against the destination
        /// before anything is written, so a hostile archive cannot escape the temp folder - let alone the
        /// game directory.
        /// </remarks>
        internal static void Unpack(string zipPath, string destination)
        {
            var root = Path.GetFullPath(destination);
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                root += Path.DirectorySeparatorChar;
            }

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries)
                {
                    // '\' is a separator on Windows and an ordinary character on Linux, so an entry like
                    // "..\..\x" escapes on one platform and not the other. Both separators are normalised
                    // before the check, so the answer does not depend on which machine unpacks the zip.
                    var name = entry.FullName.Replace(ZipSeparator, Path.DirectorySeparatorChar);
                    var target = Path.GetFullPath(Path.Combine(root, name));

                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException("the archive contains an entry that would write outside the " +
                                              "unpack folder (" + entry.FullName + "). It was refused");
                    }

                    if (entry.Length == 0 && entry.Name.Length == 0)
                    {
                        // A directory entry.
                        Directory.CreateDirectory(target);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target));

                    // ExtractToFile rather than ExtractToDirectory, so the per-entry check above is the
                    // one that decides.
                    entry.ExtractToFile(target, true);
                }
            }
        }

        /// <summary>
        /// Work out what installing this would do, and refuse anything that should not be installed.
        /// </summary>
        /// <remarks>
        /// Refusals are refused rather than installed-with-a-warning, because a file that is not what the
        /// catalogue said it was is either a broken download or something that should not be near a game
        /// folder. Either way the answer is the same: stop and say why.
        /// </remarks>
        /// <param name="installLatest">
        /// True when the user chose GitHub's newest release over the one the list pins. The pin and the
        /// recorded hashes describe a different release, so they cannot check this one; the plan says
        /// so instead of pretending they did, and every structural check - the files being there, being
        /// assemblies, carrying a plugin - still runs.
        /// </param>
        public static InstallPlan Plan(ReleaseInfo release, string unpackedFolder, GameInstall install,
                                       bool installLatest = false)
        {
            if (release == null) throw new ArgumentNullException("release");
            if (install == null) throw new ArgumentNullException("install");

            var plan = new InstallPlan { Release = release };

            // The release has to be the one the catalogue listed. Fetching the pin makes this true
            // already, and this checks it anyway: the URL path and any later change to the fetch
            // would otherwise be able to install a different release under a pinned entry's name.
            if (installLatest)
            {
                // Only a pinned entry has anything to deviate from. An unpinned one already installs
                // the newest release, so reaching here means the caller took the wrong path.
                if (!release.Source.IsPinned)
                {
                    throw new IOException("this entry does not pin a release, so the newest one is " +
                                          "already what it installs");
                }

                plan.ListedVersion = release.Source.ApprovedVersion;
            }
            else if (release.Source.IsPinned &&
                     !string.Equals(release.TagName, release.Source.ApprovedVersion, StringComparison.Ordinal))
            {
                throw new IOException("this is " + release.TagName + ", and the mod list lists " +
                                      release.Source.ApprovedVersion + ". Nothing was installed");
            }

            foreach (var wanted in release.Source.Files)
            {
                var source = Path.Combine(unpackedFolder, wanted.FileName);

                if (!File.Exists(source))
                {
                    throw new IOException("the download did not contain " + wanted.FileName + ". " +
                                          DescribeAvailable(unpackedFolder));
                }

                // The file has to be the bytes somebody reviewed, when the catalogue says what
                // those are. Checked before anything else looks at it, so a wrong file is never
                // scanned, described or shown as if it were the right one. Skipped when the user
                // chose a release the catalogue never recorded a hash for - there is nothing to
                // compare, and hashing to prove nothing would only look like a check.
                if (plan.ListedVersion == null) VerifyPinned(wanted.FileName, wanted.Sha256, Sha256Of(source));

                // The same reader the Mods tab uses, so a file that would list badly is caught before it
                // is installed rather than after. Its code is not executed.
                var entry = PluginScanner.Scan(Path.GetDirectoryName(source), null)
                    .FirstOrDefaultNamed(wanted.FileName);

                if (entry == null)
                {
                    throw new IOException(wanted.FileName + " could not be read as a .NET assembly, so it was " +
                                          "not installed");
                }

                if (!entry.IsAssembly)
                {
                    throw new IOException(wanted.FileName + " could not be read: " + entry.LoadError);
                }

                if (wanted.ExpectsPluginAttribute && string.IsNullOrEmpty(entry.Guid))
                {
                    throw new IOException(wanted.FileName + " has no [BepInPlugin] attribute, which means " +
                                          "BepInEx would not load it. It was not installed");
                }

                var destination = Path.Combine(
                    ModCatalogue.FolderFor(install, wanted.Folder), wanted.FileName);

                var step = new InstallStep
                {
                    File = wanted,
                    SourcePath = source,
                    DestinationPath = destination,
                    Version = entry.DisplayVersion,
                    PluginName = entry.DisplayName,
                    Guid = entry.Guid
                };

                if (File.Exists(destination))
                {
                    // What is there now, read the same way, so the comparison is like for like.
                    var existing = PluginScanner.Scan(Path.GetDirectoryName(destination), null)
                        .FirstOrDefaultNamed(wanted.FileName);

                    step.ReplacingVersion = existing != null && !string.IsNullOrEmpty(existing.DisplayVersion)
                        ? existing.DisplayVersion
                        : "a copy with no version";

                    if (string.Equals(step.ReplacingVersion, step.Version, StringComparison.Ordinal))
                    {
                        plan.Warnings.Add(wanted.FileName + " is already version " + step.Version +
                                          ". Reinstalling changes nothing.");
                    }
                }

                plan.Steps.Add(step);
            }

            if (plan.Steps.Count == 0) throw new IOException("there was nothing in the download to install");

            // The catalogue's optional setup steps, resolved against this download. Each script must be
            // sitting in the unpacked folder under its bare name: a declared script the release does not
            // contain fails here rather than being fetched from anywhere else, for the same reason a
            // missing dll does - the catalogue and the release disagree, and going looking would turn a
            // typo into a download from wherever the fixer pointed.
            foreach (var declared in release.Source.PostInstall)
            {
                plan.Scripts.Add(ResolveScript(unpackedFolder, declared, plan.ListedVersion == null));
            }

            return plan;
        }

        /// <summary>
        /// A script's text is capped, because the prompt shows it before asking.
        /// </summary>
        internal const long MaxScriptBytes = 64L * 1024;

        /// <summary>
        /// The separator a Windows-authored zip entry may carry. The archive format specifies '/',
        /// but archives built on Windows routinely use '\', and it has to be understood as a separator
        /// on every platform or a traversal would be refused on Windows and allowed on Linux.
        /// </summary>
        private const char ZipSeparator = '\\';

        /// <param name="verifyHash">
        /// False when the install is of a release the catalogue has no hash for - the newest one, chosen
        /// by hand. The step is then carried with no hash, so nothing later claims it was checked.
        /// </param>
        private static PostInstallStep ResolveScript(string unpackedFolder, PostInstallStep declared,
                                                     bool verifyHash)
        {
            var path = Path.Combine(unpackedFolder, declared.ScriptName);

            if (!File.Exists(path))
            {
                throw new IOException("the download did not contain " + declared.ScriptName + ", which the " +
                                      "mod list promises as a setup step. " + DescribeAvailable(unpackedFolder));
            }

            if (new FileInfo(path).Length > MaxScriptBytes)
            {
                throw new IOException(declared.ScriptName + " is larger than any setup step should be, so it " +
                                      "was refused rather than shown and run");
            }

            // A script is the only thing in an install that reaches a shell, so it is the one that
            // most needs to be the reviewed bytes. Checked before it is read for the prompt.
            if (verifyHash) VerifyPinned(declared.ScriptName, declared.Sha256, Sha256Of(path));

            string text;
            try
            {
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                throw new IOException(declared.ScriptName + " could not be read: " + ex.Message);
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new IOException(declared.ScriptName + " is empty, so there is nothing to run");
            }

            return new PostInstallStep(declared.ScriptName, declared.Description, path, text,
                                       verifyHash ? declared.Sha256 : null);
        }

        /// <summary>
        /// Carry out a plan.
        /// </summary>
        /// <remarks>
        /// The only method that writes into the game folder. Everything it does was shown first.
        /// </remarks>
        public static string[] Apply(InstallPlan plan)
        {
            var written = new List<string>();

            foreach (var step in plan.Steps)
            {
                var folder = Path.GetDirectoryName(step.DestinationPath);
                Directory.CreateDirectory(folder);

                File.Copy(step.SourcePath, step.DestinationPath, true);
                written.Add(Path.GetFileName(step.DestinationPath));
            }

            return written.ToArray();
        }

        /// <summary>Whether anything would be replaced, which is what the confirmation is about.</summary>
        public static bool ReplacesAnything(InstallPlan plan)
        {
            foreach (var step in plan.Steps)
            {
                if (step.IsReplacement) return true;
            }
            return false;
        }

        /// <summary>The SHA-256 of a file on disk, lower-case hex.</summary>
        internal static string Sha256Of(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                return Http.ToHex(sha.ComputeHash(stream));
            }
        }

        /// <summary>
        /// Check a downloaded file against the hash the catalogue recorded for it.
        /// </summary>
        /// <remarks>
        /// This is the check the whole arrangement exists to make possible: GitHub's digest comes
        /// from the same place as the file, while this one was written down when a person looked at
        /// it. A mismatch is refused rather than warned about.
        /// </remarks>
        private static void VerifyPinned(string fileName, string expected, string actual)
        {
            if (expected == null) return;

            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(fileName + " does not match the hash the mod list recorded for it, " +
                                      "so nothing was installed. Either the release was replaced after it was " +
                                      "reviewed, or the mod list is out of date");
            }
        }

        /// <summary>A multi-line description of the plan, for the confirmation box.</summary>
        /// <remarks>
        /// Short by design. Every line here is one a person has to read before saying yes, so the ones
        /// that repeat what an earlier line already said are cut: the release is named once, with the
        /// zip and its size beside it, rather than again at the bottom.
        /// </remarks>
        /// <summary>
        /// The short version: what is about to be installed and whether anybody checked it.
        /// </summary>
        /// <remarks>
        /// The dialog that asks for consent shows this and puts <see cref="Describe"/> behind a
        /// disclosure. Everything in the long form is worth being able to read and not worth reading
        /// by default: a wall of hashes, paths and capabilities is skimmed, and a skimmed
        /// confirmation is one that gets clicked through without deciding anything.
        /// </remarks>
        public static string DescribeSummary(InstallPlan plan)
        {
            if (plan == null) throw new ArgumentNullException("plan");

            var source = plan.Release.Source;
            var text = new StringBuilder();

            text.Append(source.DisplayName + " " + plan.Release.TagName +
                        ", " + Http.Describe(plan.Release.Package.Size));

            if (ModCatalogue.IsAccountName(source.Repo) && ModCatalogue.IsAccountName(source.Owner))
            {
                text.Append(", from " + source.QualifiedName + " on GitHub");
                if (source.IsThirdParty) text.Append(" (third-party)");
            }

            text.AppendLine(".");
            text.AppendLine();

            if (plan.ListedVersion != null)
            {
                text.AppendLine("Not reviewed: " + plan.Release.TagName + " is newer than the " +
                                plan.ListedVersion + " the list reviewed, so no hash applies.");
            }
            else if (source.IsVerified)
            {
                text.AppendLine("Reviewed: every file matches the hash the list recorded for it.");
            }
            else if (source.IsPinned)
            {
                text.AppendLine("Partly reviewed: the list pins " + source.ApprovedVersion +
                                ", but not every file is hashed.");
            }
            else
            {
                text.AppendLine("Not reviewed: this entry names no release and no hashes.");
            }

            text.Append("Changes take effect on the next launch.");
            return text.ToString();
        }

        public static string Describe(InstallPlan plan)
        {
            var text = new StringBuilder();
            var source = plan.Release.Source;
            var package = plan.Release.Package;

            text.AppendLine(source.DisplayName + "  " + plan.Release.TagName +
                            "   (" + package.Name + ", " + Http.Describe(package.Size) + ")");

            // Which repository the files come from. Named for every catalogue install, and marked for a
            // third-party one: unlike the project's own releases there is no shared review behind it.
            // Skipped when the source names no real repository, which is the URL path - that dialog
            // already names its host in its own words.
            if (ModCatalogue.IsAccountName(source.Repo) && ModCatalogue.IsAccountName(source.Owner))
            {
                text.Append(source.QualifiedName + " on GitHub");
                if (source.IsThirdParty) text.Append(", third-party");
                text.AppendLine(".");
            }

            text.AppendLine();

            // Whether this is the release somebody reviewed, said before the files rather than
            // after them: it is the sentence that decides how much the rest of the box means. An
            // install of the newest release over a pin is the loudest case, so it is first - the
            // pin is still in the list, and this is the one dialog that has to say it was left behind.
            if (plan.ListedVersion != null)
            {
                text.AppendLine("Not reviewed: " + plan.Release.TagName + " is newer than the " +
                                plan.ListedVersion + " the list reviewed, so no hash applies.");
            }
            else if (source.IsVerified)
            {
                text.AppendLine("Reviewed: every file matches the hash the list recorded for it.");
            }
            else if (source.IsPinned)
            {
                text.AppendLine("Partly reviewed: the list pins " + source.ApprovedVersion +
                                ", but not every file is hashed.");
            }
            else
            {
                text.AppendLine("Not reviewed: this entry names no release and no hashes.");
            }

            text.AppendLine();

            foreach (var step in plan.Steps)
            {
                // Relative and built with Path.Combine, so the confirmation shows the same separator
                // the install will actually use on this platform rather than a Windows backslash.
                var folder = step.File.Folder == ModFolder.Core
                    ? Path.Combine("BepInEx", "core")
                    : Path.Combine("BepInEx", "plugins");
                var identity = string.IsNullOrEmpty(step.PluginName)
                    ? step.File.FileName
                    : step.PluginName + "  " + step.Version;

                text.AppendLine("  " + identity);
                text.AppendLine("    -> " + Path.Combine(folder, step.File.FileName));

                // What the catalogue's scan found it able to reach. Shown for every file, because a
                // capability that is never mentioned is one nobody can notice appearing. Left out for a
                // release the catalogue never scanned: the list's capabilities belong to the listed
                // release, and showing them against a different one would be a claim about bytes
                // nobody has looked at.
                if (plan.ListedVersion == null)
                {
                    var capabilities = ModCatalogue.DescribeCapabilities(step.File);
                    if (capabilities != null) text.AppendLine("    " + capabilities);
                }

                if (step.IsReplacement) text.AppendLine("    replaces " + step.ReplacingVersion);
            }

            // The optional steps, named before anything is agreed to: each is offered separately after
            // installing, and each can be denied. Saying so here is what makes the later prompt expected
            // rather than a surprise tacked onto an install.
            foreach (var script in plan.Scripts)
            {
                text.AppendLine();
                text.AppendLine("Optional setup: " + script.ScriptName);
                text.AppendLine("  " + script.Description);
                text.AppendLine("  Offered after installing; it can be skipped.");
                if (script.IsPinned) text.AppendLine("  Pinned: hashed before it is offered.");
            }

            // Only where it is the strongest number in the box. A reviewed install has per-file hashes,
            // which say more; this one comes from the same place as the file, so it proves the download
            // was not mangled and nothing else.
            if (package.Sha256 != null && (plan.ListedVersion != null || !source.IsVerified))
            {
                text.AppendLine();
                text.AppendLine("Digest " + package.Sha256 + " (GitHub's, not a review.)");
            }

            return text.ToString();
        }

        private static string DescribeAvailable(string folder)
        {
            var names = new List<string>();
            foreach (var file in Directory.GetFiles(folder)) names.Add(Path.GetFileName(file));
            names.Sort();

            return names.Count == 0
                ? "The download was empty."
                : "It contained: " + string.Join(", ", names.ToArray());
        }
    }

    /// <summary>Small helper so the installer can pick one file out of a scan without a Linq predicate dance.</summary>
    internal static class PluginEntryLookup
    {
        public static PluginEntry FirstOrDefaultNamed(this List<PluginEntry> entries, string fileName)
        {
            foreach (var entry in entries)
            {
                if (string.Equals(Path.GetFileName(entry.Path), fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }
            return null;
        }
    }
}