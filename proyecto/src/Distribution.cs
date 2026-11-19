using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace EmpireBay
{
    public sealed class GithubAsset
    {
        public string name { get; set; }
        public string state { get; set; }
        public string browser_download_url { get; set; }
        public string digest { get; set; }
        public long size { get; set; }
    }

    public sealed class Release
    {
        public string tag_name { get; set; }
        public string name { get; set; }
        public bool draft { get; set; }
        public bool prerelease { get; set; }
        public GithubAsset[] assets { get; set; }

        public GithubAsset Asset(string file)
        {
            return (assets ?? new GithubAsset[0]).SingleOrDefault(a => a.name == file && a.state == "uploaded");
        }

        public Version NumericVersion()
        {
            Version value;
            string tag = (tag_name ?? "").TrimStart('v','V');
            if (!Regex.IsMatch(tag, @"\A\d+\.\d+\.\d+\z") || !Version.TryParse(tag, out value))
                return null;
            return value;
        }
    }

    public sealed class Distribution
    {
        public const string Repo = "AgustinPrS/mafiarol";
        public const string Api = "https://api.github.com/repos/" + Repo + "/releases/latest";
        public const string Launcher = "MafiaRolLauncher.exe";
        public const long LauncherLimit = 96L * 1024 * 1024;
        public const long ClientLimit = 4L * 1024 * 1024 * 1024;
        readonly HttpClient http;

        public Distribution(HttpMessageHandler handler)
        {
            http = new HttpClient(handler);
            http.Timeout = Timeout.InfiniteTimeSpan;
            http.DefaultRequestHeaders.UserAgent.ParseAdd("EmpireBayLauncher/1.0");
        }

        public static Distribution Online()
        {
            return new Distribution(new HttpClientHandler { AllowAutoRedirect = false });
        }

        static bool AllowedHost(Uri url, bool github)
        {
            if (!Settings.IsHttps(url.AbsoluteUri)) return false;
            if (!github) return true;
            return url.Host == "api.github.com" || url.Host == "github.com" ||
                   url.Host == "release-assets.githubusercontent.com" || url.Host == "objects.githubusercontent.com" ||
                   url.Host == "github-releases.githubusercontent.com";
        }

        async Task<HttpResponseMessage> Open(Uri url, bool github, CancellationToken token)
        {
            for (int i = 0; i <= 6; i++)
            {
                if (!AllowedHost(url, github)) throw new InvalidDataException("Destino de descarga no válido.");
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    if (url.Host == "api.github.com")
                        request.Headers.Accept.ParseAdd("application/vnd.github+json");
                    var result = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    int status = (int)result.StatusCode;
                    if (status >= 300 && status < 400 && result.Headers.Location != null)
                    {
                        url = new Uri(url, result.Headers.Location);
                        result.Dispose();
                        continue;
                    }
                    if (result.IsSuccessStatusCode) return result;
                    using (result)
                    {
                        if (result.StatusCode == HttpStatusCode.NotFound && url.AbsoluteUri == Api) return null;
                        if (status == 403 || status == 429)
                            throw new IOException("El servicio limitó el acceso. Volvé a intentar más tarde.");
                        throw new IOException("El servicio respondió HTTP " + status + ".");
                    }
                }
            }
            throw new IOException("La descarga tiene demasiadas redirecciones.");
        }

        public async Task<Release> Latest(CancellationToken token)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using (var response = await Open(new Uri(Api), true, timeout.Token).ConfigureAwait(false))
                {
                    if (response == null) return null;
                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var ms = new MemoryStream())
                    {
                        await Copy(input, ms, 1024 * 1024, -1, null, timeout.Token).ConfigureAwait(false);
                        var release = new JavaScriptSerializer().Deserialize<Release>(Encoding.UTF8.GetString(ms.ToArray()));
                        if (release == null || release.draft || release.prerelease) return null;
                        return release;
                    }
                }
            }
        }

        public Task GetAsset(Release release, GithubAsset asset, string dest, long limit, Action<int> progress, CancellationToken token)
        {
            string expected = "https://github.com/" + Repo + "/releases/download/" +
                              Uri.EscapeDataString(release.tag_name) + "/" + Uri.EscapeDataString(asset.name);
            if (asset.browser_download_url != expected || asset.size < 1 || asset.size > limit ||
                !Regex.IsMatch(asset.digest ?? "", @"\Asha256:[a-fA-F0-9]{64}\z"))
                throw new InvalidDataException("GitHub no proporcionó una descarga verificable.");
            return Fetch(expected, asset.digest.Substring(7), dest, limit, asset.size, true, progress, token);
        }

        public async Task Fetch(string url, string sha256, string dest, long limit, long expectedBytes,
                                bool github, Action<int> progress, CancellationToken token)
        {
            using (var watchdog = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                watchdog.CancelAfter(TimeSpan.FromSeconds(45));
                using (var response = await Open(new Uri(url), github, watchdog.Token).ConfigureAwait(false))
                {
                    long size = response.Content.Headers.ContentLength ?? -1;
                    if (size > limit || (expectedBytes >= 0 && size >= 0 && expectedBytes != size))
                        throw new InvalidDataException("El tamaño de la descarga no coincide.");
                    long total = expectedBytes >= 0 ? expectedBytes : size;
                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var file = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var sha = SHA256.Create())
                    using (var hashing = new CryptoStream(file, sha, CryptoStreamMode.Write))
                    {
                        long bytes = await Copy(input, hashing, limit, total, progress, watchdog.Token, watchdog).ConfigureAwait(false);
                        hashing.FlushFinalBlock();
                        if (bytes == 0 || (total >= 0 && total != bytes) ||
                            !String.Equals(Hex(sha.Hash), sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("El archivo descargado no coincide con el publicado.");
                    }
                }
            }
        }

        static async Task<long> Copy(Stream input, Stream output, long limit, long total, Action<int> progress,
                                     CancellationToken token, CancellationTokenSource watchdog = null)
        {
            byte[] buffer = new byte[81920];
            long bytes = 0;
            int last = -2;
            using (token.Register(input.Dispose))
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    int n = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                    if (n == 0) break;
                    if (watchdog != null) watchdog.CancelAfter(TimeSpan.FromSeconds(45));
                    bytes += n;
                    if (bytes > limit) throw new InvalidDataException("La descarga supera el tamaño permitido.");
                    await output.WriteAsync(buffer, 0, n, token).ConfigureAwait(false);
                    int percent = total > 0 ? (int)Math.Min(100, bytes * 100 / total) : -1;
                    if (progress != null && percent != last) progress(percent);
                    last = percent;
                }
            }
            return bytes;
        }

        public static string Hex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }
    }
}
