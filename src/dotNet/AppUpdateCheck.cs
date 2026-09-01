using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet
{
    /// <summary>
    /// "Is there a newer version of the app?" — answered at launch, at most once a day, and never acted on.
    ///
    /// NOTIFY ONLY. This downloads nothing and installs nothing; the entire outcome is a version string the
    /// Preferences footer may render as a link to the releases page. That is the same contract as the monthly
    /// module check, and it can be switched off in Preferences for the same reason: it reaches the network
    /// without being asked.
    ///
    /// The version is read from the content catalog the app already fetches, rather than the GitHub releases
    /// API. Three reasons: the catalog is plumbing that already exists and is already TLS-pinned to the project
    /// repo, the releases API rate-limits unauthenticated callers to 60/hour per IP (shared by everyone behind
    /// one NAT), and a catalog miss degrades to "no answer" instead of an error to explain.
    /// </summary>
    internal static class AppUpdateCheck
    {
        /// <summary>How stale a stored answer may be before launch asks again.</summary>
        internal static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

        /// <summary>Where the clickable version link goes. The releases page, not a direct asset: the user
        /// should see the notes and the checksums before downloading anything.</summary>
        internal const string ReleasesUrl =
            "https://github.com/" + RemoteCatalogClient.Owner + "/" + RemoteCatalogClient.Repository + "/releases";

        /// <summary>
        /// Compare two version strings the way a human reads them: 1.9.10 is NEWER than 1.9.9, which string
        /// comparison gets backwards. Missing components count as 0, so "1.9" and "1.9.0" are the same
        /// version. Anything unparseable answers false — a garbled catalog must never nag.
        /// </summary>
        internal static bool IsNewer(string candidate, string current)
        {
            int[] a = ParseVersion(candidate);
            int[] b = ParseVersion(current);
            if (a == null || b == null) return false;
            for (int i = 0; i < 4; i++)
            {
                if (a[i] > b[i]) return true;
                if (a[i] < b[i]) return false;
            }
            return false;
        }

        /// <summary>Four components, or null when the text is not a version. Deliberately strict: a build
        /// suffix ("1.9.8-beta") is not something this should guess about, so it is refused.</summary>
        internal static int[] ParseVersion(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string trimmed = text.Trim();
            if (trimmed.Length > 0 && (trimmed[0] == 'v' || trimmed[0] == 'V')) trimmed = trimmed.Substring(1);
            if (trimmed.Length == 0 || trimmed.Length > 32) return null;

            string[] parts = trimmed.Split('.');
            if (parts.Length == 0 || parts.Length > 4) return null;
            var result = new int[4];
            for (int i = 0; i < parts.Length; i++)
            {
                int n;
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n)) return null;
                if (n < 0 || n > 99999) return null;
                result[i] = n;
            }
            return result;
        }

        /// <summary>The text the footer shows: "1.9.7 -&gt; 1.9.8" when something newer is known, else the
        /// plain running version. Pure so the rendering rule is testable without a window or a network.</summary>
        internal static string FooterText(string current, string latestKnown)
        {
            string running = string.IsNullOrWhiteSpace(current) ? "" : current.Trim();
            if (IsNewer(latestKnown, running)) return running + " → " + latestKnown.Trim();
            return "v" + running;
        }

        /// <summary>True when the footer should be a clickable link rather than a muted label.</summary>
        internal static bool OffersUpdate(string current, string latestKnown)
        {
            return IsNewer(latestKnown, current);
        }

        /// <summary>Whether launch should go to the network at all: the setting is on AND the stored answer is
        /// older than the interval. Split out as a pure decision so "at most once a day" is testable.</summary>
        internal static bool ShouldCheck(bool enabled, DateTimeOffset lastCheckUtc, DateTimeOffset nowUtc)
        {
            if (!enabled) return false;
            if (lastCheckUtc == DateTimeOffset.MinValue) return true;
            if (lastCheckUtc > nowUtc) return true;    // a clock that moved backwards: treat as never checked
            return nowUtc - lastCheckUtc >= CheckInterval;
        }

        /// <summary>
        /// Run the check if it is due, and record the outcome. Fire-and-forget from startup: every failure is
        /// swallowed, because "could not reach GitHub" is not something to interrupt a pet app over.
        ///
        /// The stamp is written even when the answer is "nothing newer", so an offline machine backs off for a
        /// day instead of retrying on every launch.
        /// </summary>
        internal static async Task MaybeCheckAsync(LocalData data, string currentVersion, CancellationToken token)
        {
            if (data == null) return;
            try
            {
                if (!ShouldCheck(data.GetAppUpdateCheck(), data.GetAppUpdateLastCheckUtc(), DateTimeOffset.UtcNow))
                    return;

                string latest = await RemoteCatalogClient.FetchAppVersionAsync(token).ConfigureAwait(false);
                // Store only a version that is genuinely newer. Anything else stores "" so a downgrade in the
                // catalog (or a nonsense value) clears an old nag rather than pinning it forever.
                string keep = IsNewer(latest, currentVersion) ? (latest ?? "").Trim() : "";
                data.SetAppUpdateResult(DateTimeOffset.UtcNow, keep);
            }
            catch { }
        }
    }
}
