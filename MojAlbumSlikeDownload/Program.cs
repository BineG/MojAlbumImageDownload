using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MojAlbumSlikeDownload;
using MojAlbumSlikeDownload.Config;
using MojAlbumSlikeDownload.Utilities;
using System.Net;

// Create the host builder
var builder = Host.CreateApplicationBuilder(args);

// Configure configuration sources: appsettings.json and user secrets
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddUserSecrets<Program>(optional: true);

// Bind and register options
builder.Services.Configure<MojAlbumOptions>(builder.Configuration.GetSection("MojAlbum"));
builder.Services.Configure<DownloadSettingsOptions>(builder.Configuration.GetSection("DownloadSettings"));

// Register a named HttpClient with a dedicated CookieContainer for authenticated sessions
builder.Services.AddHttpClient("MojAlbum", (sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<DownloadSettingsOptions>>().Value;
    var baseUrl = string.IsNullOrWhiteSpace(opts.BaseUrl) ? Constants.BaseUrl : opts.BaseUrl.TrimEnd('/');
    client.BaseAddress = new Uri(baseUrl);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("MojAlbumSlikeDownload/1.0 (+https://mojalbum.com)");
    client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
})
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
    AllowAutoRedirect = true,
    UseCookies = true,
    CookieContainer = new CookieContainer(),
});

// Wire services to use the named HttpClient instance so cookies are shared
builder.Services.AddSingleton(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var httpClient = factory.CreateClient("MojAlbum");
    return new ImageDownloader(httpClient);
});

builder.Services.AddSingleton(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var httpClient = factory.CreateClient("MojAlbum");
    var ma = sp.GetRequiredService<IOptions<MojAlbumOptions>>();
    var dl = sp.GetRequiredService<IOptions<DownloadSettingsOptions>>();
    return new MojAlbumImagesDownloader(httpClient, ma, dl);
});

using var host = builder.Build();

//var downloader = host.Services.GetRequiredService<MojAlbumImagesDownloader>();

MojAlbumOptions mojAlbumOptions = host.Services.GetRequiredService<IOptions<MojAlbumOptions>>().Value;

if (mojAlbumOptions.Authentication == null)
{
    mojAlbumOptions.Authentication = new AuthenticationOptions();
}

if (string.IsNullOrWhiteSpace(mojAlbumOptions.Authentication.Username))
{
    Console.WriteLine("Vnesite uporabniško ime za MojAlbum:");
    mojAlbumOptions.Authentication!.Username = Console.ReadLine();
}

if (string.IsNullOrWhiteSpace(mojAlbumOptions.Authentication.Password))
{
    Console.WriteLine("Vnesite geslo za MojAlbum:");
    mojAlbumOptions.Authentication!.Password = Console.ReadLine();
}

var downloader = new MojAlbumImagesDownloader(
    host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("MojAlbum"),
    Options.Create(mojAlbumOptions),
    host.Services.GetRequiredService<IOptions<DownloadSettingsOptions>>()
);

await downloader.DownloadAllImages();