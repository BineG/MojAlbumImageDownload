using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace MojAlbumSlikeDownload.Utilities
{
    internal class ImageDownloader
    {
        private readonly HttpClient _client;

        public ImageDownloader(HttpClient httpClient)
        {
            _client = httpClient;
        }

        private async Task<(byte[]? Content, Uri? FinalUri)> TryDownloadImage(string url, int maxRedirects = 10, CancellationToken cancellationToken = default)
        {
            Uri current = new Uri(url);

            for (int i = 0; i <= maxRedirects; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                // If the injected HttpClient has AllowAutoRedirect=true, we likely land here with a 2xx and a final RequestUri.
                if (IsRedirectStatus(response.StatusCode))
                {
                    var location = response.Headers.Location;
                    if (location == null)
                        return (null, current);

                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    continue;
                }

                if (response.IsSuccessStatusCode)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                    return (bytes, response.RequestMessage?.RequestUri ?? current);
                }

                // Non-success (e.g., 404/410/500) – avoid throwing; just return null.
                Console.WriteLine($"Status {response.StatusCode}, cannot download {url}");
                return (null, current);
            }

            Console.WriteLine($"Max redirects exceeded for {url}");
            return (null, null);
        }

        private static bool IsRedirectStatus(HttpStatusCode status) =>
            status == HttpStatusCode.MovedPermanently ||      // 301
            status == HttpStatusCode.Redirect ||               // 302
            status == HttpStatusCode.SeeOther ||               // 303
            status == HttpStatusCode.TemporaryRedirect ||      // 307
            status == HttpStatusCode.PermanentRedirect;        // 308
    }
}
