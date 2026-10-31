using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Switchboard
{
    /// <summary>
    /// What GitHub last said about every mod in the list, read from the catalogue repository.
    /// </summary>
    /// <remarks>
    /// The alternative is asking GitHub about each entry at startup, which is one or two calls per
    /// mod against an unauthenticated limit of 60 an hour. The list costs twenty calls as it stands,
    /// so the third refresh in an hour fails for every row at once and looks like twelve broken mods.
    /// The catalogue's own weekly job already resolves all of this with a token; this reads what it
    /// wrote, which is one static file on a CDN and counts against nothing.
    ///
    /// A miss is not an error. An entry approved after the last snapshot, or a snapshot that could
    /// not be fetched at all, falls back to asking GitHub, so the list is never emptier than it was.
    /// </remarks>
    internal sealed class ReleaseIndex
    {
        private readonly Dictionary<string, Entry> _entries;
        private readonly DateTimeOffset? _generatedAt;

        private ReleaseIndex(Dictionary<string, Entry> entries, DateTimeOffset? generatedAt)
        {
            _entries = entries;
            _generatedAt = generatedAt;
        }

        /// <summary>When the snapshot was written, or null when it did not say.</summary>
        public DateTimeOffset? GeneratedAt { get { return _generatedAt; } }

        public int Count { get { return _entries.Count; } }

        /// <summary>
        /// The release one entry names, or null when the snapshot has nothing usable for it.
        /// </summary>
        /// <param name="tag">
        /// The tag the list pins, or null for the newest release. A snapshot entry whose pinned tag
        /// is a different release from the one being asked for answers null: the list was edited
        /// after the snapshot was taken, and the API is the only thing that knows about the new pin.
        /// </param>
        public ReleaseInfo Find(ModSource source, string tag)
        {
            if (source == null) return null;

            Entry entry;
            if (!_entries.TryGetValue(source.Owner + "/" + source.Repo, out entry)) return null;

            var release = tag == null ? entry.Newest : entry.Pinned;
            if (release == null) return null;
            if (tag != null && !string.Equals(release.Tag, tag, StringComparison.Ordinal)) return null;

            return release.ToReleaseInfo(source);
        }

        /// <summary>Read the snapshot, or null when it is missing, unreadable or not a snapshot.</summary>
        public static ReleaseIndex Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                return null;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;

                DateTimeOffset? generated = null;
                var stamp = GitHubJson.Text(root, "generatedAt");
                DateTimeOffset parsed;
                if (stamp != null && DateTimeOffset.TryParse(stamp, out parsed)) generated = parsed;

                JsonElement mods;
                if (!root.TryGetProperty("mods", out mods) || mods.ValueKind != JsonValueKind.Array) return null;

                var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                foreach (var mod in mods.EnumerateArray())
                {
                    if (mod.ValueKind != JsonValueKind.Object) continue;

                    var owner = GitHubJson.Text(mod, "owner");
                    var repo = GitHubJson.Text(mod, "repo");
                    if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo)) continue;

                    JsonElement value;
                    var pinned = mod.TryGetProperty("pinned", out value) ? Release.FromJson(value) : null;
                    var newest = mod.TryGetProperty("newest", out value) ? Release.FromJson(value) : null;

                    entries[owner + "/" + repo] = new Entry(pinned, newest);
                }

                return new ReleaseIndex(entries, generated);
            }
        }

        private sealed class Entry
        {
            public Entry(Release pinned, Release newest)
            {
                Pinned = pinned;
                Newest = newest;
            }

            public Release Pinned { get; private set; }
            public Release Newest { get; private set; }
        }

        private sealed class Release
        {
            public string Tag { get; private set; }
            public string Asset { get; private set; }
            public string Url { get; private set; }
            public long Size { get; private set; }
            public string Sha256 { get; private set; }
            public string Page { get; private set; }

            /// <summary>One release out of the snapshot, or null when it does not have what an install needs.</summary>
            public static Release FromJson(JsonElement value)
            {
                if (value.ValueKind != JsonValueKind.Object) return null;

                var tag = GitHubJson.Text(value, "tag");
                var asset = GitHubJson.Text(value, "asset");
                var url = GitHubJson.Text(value, "url");

                // The same bar the API path holds a release to: a tag, a zip, and an address this
                // tool is willing to fetch. A snapshot that fails any of them is a miss, not a
                // special case in the install path.
                if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(asset)) return null;
                if (!asset.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return null;
                if (!Http.IsAcceptable(url)) return null;

                long size;
                var sizeText = value.TryGetProperty("size", out var rawSize) ? rawSize.ToString() : null;
                if (!long.TryParse(sizeText, out size)) size = -1;

                return new Release
                {
                    Tag = tag,
                    Asset = asset,
                    Url = url,
                    Size = size,
                    Sha256 = GitHubJson.Text(value, "sha256"),
                    Page = GitHubJson.Text(value, "page")
                };
            }

            public ReleaseInfo ToReleaseInfo(ModSource source)
            {
                return new ReleaseInfo(source, Tag, Page, new ReleaseAsset(Asset, Size, Url, Sha256));
            }
        }
    }
}
