namespace MojAlbumSlikeDownload.Config
{
    public class MojAlbumOptions
    {
        public AuthenticationOptions Authentication { get; set; } = new();
    }

    public class AuthenticationOptions
    {
        public string? Username { get; set; }
        public string? Password { get; set; }
    }
}
