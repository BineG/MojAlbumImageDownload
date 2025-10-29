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
                // After login, go to the page with "/albumi" appended to the current URL
                var albumsUri = AppendPathSegment(startUri, "albumi");
                await CrawlAlbumsAndDownloadImagesAsync(albumsUri, cancellationToken).ConfigureAwait(false);
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
            var baseOutputRoot = string.IsNullOrWhiteSpace(_downloadSettings.OutputDirectory)
                ? Path.Combine(Environment.CurrentDirectory, "downloads")
                : _downloadSettings.OutputDirectory!;

            // Append the input username as a subfolder under the base output path
            var rawUsername = _mojAlbumOptions.Authentication?.Username ?? string.Empty;
            var usernameFolder = SanitizeFolderName(rawUsername);
            if (string.IsNullOrWhiteSpace(usernameFolder)) usernameFolder = "user";
            var outputRoot = Path.Combine(baseOutputRoot, usernameFolder);
            Directory.CreateDirectory(outputRoot);

            var currentUri = startUri;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string pageHtml;
                try
                {
                    // Load the current album list page
                    pageHtml = await GetStringAsync(currentUri, cancellationToken);
                }
                catch (Exception ex)
                {
                    LogError($"Failed to load page: {currentUri}", ex);
                    throw;
                }

                // Parse albums and follow each CollectionLink hyperlink (text is album name)
                var albumRefs = ParseAlbumLinks(pageHtml, currentUri);

                foreach (var album in albumRefs)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Create subfolder per album name once
                    var albumFolderName = SanitizeFolderName(album.Name);
                    var albumFolderPath = Path.Combine(outputRoot, albumFolderName);
                    Directory.CreateDirectory(albumFolderPath);

                    // Each album can have its own paging of pictures; iterate through all pages
                    var albumPageUri = album.Url;
                    while (true)
                    {
                        string albumHtml;
                        try
                        {
                            albumHtml = await GetStringAsync(albumPageUri, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            LogError($"Failed to load album page '{album.Name}' ({albumPageUri}). Skipping album.", ex);
                            break; // skip to next album
                        }

                        IEnumerable<Uri> imageLinks;
                        try
                        {
                            // Resolve image links relative to the current album page URL
                            imageLinks = ParseImageLinks(albumHtml, albumPageUri);
                        }
                        catch (Exception ex)
                        {
                            LogError($"Failed to parse image links for album '{album.Name}'. Skipping album page.", ex);
                            imageLinks = Array.Empty<Uri>();
                        }

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

                        // Move to next page within the album if available
                        var nextAlbumPageUri = TryGetNextPageUri(albumHtml, albumPageUri);
                        if (nextAlbumPageUri == null)
                        {
                            break; // no more pages in album
                        }

                        // Resolve redirects to avoid loops (/album/2 -> /album)
                        var effectiveAlbumNext = await ResolveEffectiveUriAsync(nextAlbumPageUri, cancellationToken).ConfigureAwait(false);
                        if (effectiveAlbumNext == albumPageUri)
                        {
                            break; // resolved to same URL -> stop album paging
                        }

                        albumPageUri = effectiveAlbumNext;
                    }
                }

                // Pager: find the current link and then the next hyperlink sibling
                var nextPageUri = TryGetNextPageUri(pageHtml, currentUri);
                if (nextPageUri == null)
                {
                    // No next page -> stop
                    break;
                }

                // Resolve the effective URL after any redirect (e.g., /albumi/2 -> /albumi)
                var effectiveNextUri = await ResolveEffectiveUriAsync(nextPageUri, cancellationToken).ConfigureAwait(false);
                if (effectiveNextUri == currentUri)
                {
                    // Resolved to the same page -> stop
                    break;
                }

                currentUri = effectiveNextUri;
            }
        }

        private static IEnumerable<AlbumRef> ParseAlbumLinks(string html, Uri baseUri)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var results = new List<AlbumRef>();

            // Directly find anchors inside CollectionLink containers across the page
            var anchors = doc.DocumentNode.SelectNodes("//div[contains(concat(' ', normalize-space(@class), ' '), ' CollectionLink ')]/a[@href]");
            if (anchors != null)
            {
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

                    // Build initial absolute/relative URI, then append "povecaj" using the helper if needed
                    var initialUri = new Uri(baseUri, href);
                    var path = initialUri.AbsolutePath.TrimEnd('/');
                    var finalUri = path.EndsWith("povecaj", StringComparison.OrdinalIgnoreCase)
                        ? initialUri
                        : AppendPathSegment(initialUri, "povecaj");

                    links.Add(finalUri);
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

        // Resolve the final effective URL after any server redirects
        private async Task<Uri> ResolveEffectiveUriAsync(Uri url, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return response.RequestMessage?.RequestUri ?? url;
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

        private static Uri AppendPathSegment(Uri uri, string segment)
        {
            if (uri == null) throw new ArgumentNullException(nameof(uri));
            if (string.IsNullOrWhiteSpace(segment)) return uri;

            // Ensure we treat the base as a directory to append a child segment
            var baseText = uri.ToString();
            if (!baseText.EndsWith("/"))
            {
                baseText += "/";
            }
            return new Uri(new Uri(baseText), segment.Trim('/'));
        }

        private static Uri? TryGetNextPageUri(string html, Uri currentUri)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Find the pager container
            var pager = doc.DocumentNode.SelectSingleNode("//div[contains(concat(' ', normalize-space(@class), ' '), ' Pager ')]");
            if (pager == null) return null;

            // Find the current page link and then the next sibling link
            var current = pager.SelectSingleNode(".//a[contains(concat(' ', normalize-space(@class), ' '), ' PagerCurrent ')]");
            if (current == null) return null;

            var next = current.SelectSingleNode("following-sibling::a[1][@href]");
            if (next == null) return null;

            var href = next.GetAttributeValue("href", null);
            if (string.IsNullOrWhiteSpace(href)) return null;

            return new Uri(currentUri, href);
        }
    }
}
