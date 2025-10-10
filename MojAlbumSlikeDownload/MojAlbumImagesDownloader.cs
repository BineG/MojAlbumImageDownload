using HtmlAgilityPack;
using Microsoft.Extensions.Options;
using MojAlbumSlikeDownload.Config;
using System;
using System.Collections.Generic;
using System.Net;
using System.IO;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MojAlbumSlikeDownload
{
    internal class MojAlbumImagesDownloader
    {
        private readonly HttpClient _client;
        private readonly MojAlbumOptions _mojAlbumOptions;
        private readonly DownloadSettingsOptions _downloadSettings;

        private const string UsernameFieldName = "txtUserName";
        private const string PasswordFieldName = "txtPassword";
        private const string AutoLoginFieldName = "cbAutoLogin";
        private const string SubmitFieldName = "btnLogin";

        private readonly record struct AlbumRef(Uri Url, string Name);

        public MojAlbumImagesDownloader(HttpClient httpClient, IOptions<MojAlbumOptions> mojAlbumOptions, IOptions<DownloadSettingsOptions> downloadSettings)
        {
            _client = httpClient;
            _mojAlbumOptions = mojAlbumOptions.Value;
            _downloadSettings = downloadSettings.Value;
        }

        public async Task DownloadAllImages(CancellationToken cancellationToken = default)
        {
            try
            {
                var startUri = await LoginAsync(cancellationToken).ConfigureAwait(false);
                await CrawlAlbumsAndDownloadImagesAsync(startUri, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Operation was canceled by the user.");
                throw;
            }
            catch (Exception ex)
            {
                LogError("Failed to download all images.", ex);
                throw;
            }
        }

        private async Task<Uri> LoginAsync(CancellationToken cancellationToken)
        {
            var username = _mojAlbumOptions.Authentication?.Username ?? string.Empty;
            var password = _mojAlbumOptions.Authentication?.Password ?? string.Empty;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException("Username or password is not configured.");
            }

            // Prepare form data as application/x-www-form-urlencoded
            var form = new Dictionary<string, string>
            {
                [UsernameFieldName] = username,
                [PasswordFieldName] = password,
                [AutoLoginFieldName] = "1",
                [SubmitFieldName] = "prijava"
            };

            using var content = new FormUrlEncodedContent(form);
            var loginUrl = new Uri(_client.BaseAddress!, "/prijava");

            using var request = new HttpRequestMessage(HttpMethod.Post, loginUrl)
            {
                Content = content
            };

            // Optional but helpful headers
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xhtml+xml"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml", 0.9));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));
            request.Headers.Referrer = loginUrl;

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            // With AllowAutoRedirect=true, RequestMessage.RequestUri should be the final redirected URL
            var finalUri = response.RequestMessage?.RequestUri ?? new Uri(_client.BaseAddress!, "/");
            return finalUri;
        }

        private async Task CrawlAlbumsAndDownloadImagesAsync(Uri startUri, CancellationToken cancellationToken)
        {
            // Ensure root download folder exists
            var outputRoot = string.IsNullOrWhiteSpace(_downloadSettings.OutputDirectory)
                ? Path.Combine(Environment.CurrentDirectory, "downloads")
                : _downloadSettings.OutputDirectory!;
            Directory.CreateDirectory(outputRoot);

            string startHtml;
            try
            {
                // Load the page we were redirected to after login and start parsing from there
                startHtml = await GetStringAsync(startUri, cancellationToken);
            }
            catch (Exception ex)
            {
                LogError($"Failed to load start page: {startUri}", ex);
                throw;
            }

            // Parse albums and follow each CollectionLink hyperlink (text is album name)
            var albumRefs = ParseAlbumLinks(startHtml, _client.BaseAddress!);

            foreach (var album in albumRefs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string albumHtml;
                try
                {
                    albumHtml = await GetStringAsync(album.Url, cancellationToken);
                }
                catch (Exception ex)
                {
                    LogError($"Failed to load album page '{album.Name}' ({album.Url}). Skipping album.", ex);
                    continue;
                }

                IEnumerable<Uri> imageLinks;
                try
                {
                    imageLinks = ParseImageLinks(albumHtml, _client.BaseAddress!);
                }
                catch (Exception ex)
                {
                    LogError($"Failed to parse image links for album '{album.Name}'. Skipping album.", ex);
                    continue;
                }

                // Create subfolder per album name
                var albumFolderName = SanitizeFolderName(album.Name);
                var albumFolderPath = Path.Combine(outputRoot, albumFolderName);
                Directory.CreateDirectory(albumFolderPath);

                foreach (var imageLink in imageLinks)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        // Image page contains the large image behind itemprop="contentUrl" link target
                        await DownloadImagePageAsync(imageLink, albumFolderPath, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        LogError($"Failed to download image from page {imageLink}. Skipping image.", ex);
                        // continue with next image
                    }
                }
            }
        }

        private static IEnumerable<AlbumRef> ParseAlbumLinks(string html, Uri baseUri)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var results = new List<AlbumRef>();
            // Container with AlbumsBrowser and AlbumsBrowserNarrow classes
            var containers = doc.DocumentNode.SelectNodes("//div[contains(concat(' ', normalize-space(@class), ' '), ' AlbumsBrowser ') and contains(concat(' ', normalize-space(@class), ' '), ' AlbumsBrowserNarrow ')]");
            if (containers != null)
            {
                foreach (var container in containers)
                {
                    // Prefer the link that contains the album name text: div.CollectionLink a[href]
                    var anchors = container.SelectNodes(".//div[contains(concat(' ', normalize-space(@class), ' '), ' CollectionLink ')]//a[@href]");
                    if (anchors == null) continue;

                    foreach (var a in anchors)
                    {
                        var href = a.GetAttributeValue("href", null);
                        if (string.IsNullOrWhiteSpace(href)) continue;
                        var nameText = HtmlEntity.DeEntitize(a.InnerText).Trim();
                        if (string.IsNullOrWhiteSpace(nameText))
                        {
                            // fallback to title attribute or alt
                            nameText = a.GetAttributeValue("title", string.Empty);
                        }
                        if (string.IsNullOrWhiteSpace(nameText)) continue;

                        var url = new Uri(baseUri, href);
                        results.Add(new AlbumRef(url, nameText));
                    }
                }
            }
            return results;
        }

        private static IEnumerable<Uri> ParseImageLinks(string html, Uri baseUri)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var links = new List<Uri>();
            // Find <a itemprop=\"contentUrl\" href=\"...\">
            var anchors = doc.DocumentNode.SelectNodes("//a[@itemprop='contentUrl' and @href]");
            if (anchors != null)
            {
                foreach (var a in anchors)
                {
                    var href = a.GetAttributeValue("href", null);
                    if (string.IsNullOrWhiteSpace(href)) continue;
                    links.Add(new Uri(baseUri, href));
                }
            }
            return links;
        }

        private async Task DownloadImagePageAsync(Uri imagePageUrl, string albumFolderPath, CancellationToken cancellationToken)
        {
            // Load the image page. Usually the raw/original image URL is found within the page as <meta property=\"og:image\" content=...> or similar.
            var html = await GetStringAsync(imagePageUrl, cancellationToken);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Try to discover the full-size image URL.
            // Strategy 1: look for <meta property=\"og:image\" content=\"...\"> as many galleries include it
            var metaOg = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image' and @content]");
            var imageUrl = metaOg?.GetAttributeValue("content", null);

            // Fallback: sometimes the image is within <a id=\"image\" href=\"...\">, or similar; adjust if needed
            if (string.IsNullOrEmpty(imageUrl))
            {
                var aImage = doc.DocumentNode.SelectSingleNode("//a[@id='image' and @href]") ??
                             doc.DocumentNode.SelectSingleNode("//img[@id='image' and @src]");
                imageUrl = aImage?.GetAttributeValue("href", null) ?? aImage?.GetAttributeValue("src", null);
            }

            if (string.IsNullOrEmpty(imageUrl))
            {
                Console.WriteLine($"Could not find image URL in {imagePageUrl}");
                return;
            }

            var finalImageUri = new Uri(_client.BaseAddress!, imageUrl);
            await DownloadAndSaveAsync(finalImageUri, albumFolderPath, cancellationToken);
        }

        private async Task<string> GetStringAsync(Uri url, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            // Cookies (e.g., PHPSESSID, login) are captured by the HttpClientHandler's CookieContainer and will be sent automatically on subsequent requests.
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task DownloadAndSaveAsync(Uri url, string albumFolderPath, CancellationToken cancellationToken)
        {
            // Ensure album folder exists
            Directory.CreateDirectory(albumFolderPath);

            var fileName = Path.GetFileName(url.LocalPath);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = Guid.NewGuid().ToString("N") + ".jpg";

            var path = Path.Combine(albumFolderPath, fileName);

            using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var fs = File.Create(path);
            await response.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Saved: {path}");
        }

        private static string SanitizeFolderName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var ch in name)
            {
                sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
            }
            var cleaned = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? "Album" : cleaned;
        }

        private static void LogError(string message, Exception ex)
        {
            var originalColor = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"{DateTime.UtcNow:u} ERROR: {message}");
                Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Console.ForegroundColor = originalColor;
            }
        }
    }
}
