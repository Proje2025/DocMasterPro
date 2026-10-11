using System.Net;
using System.Security.Cryptography;
using System.Text;
using DocConverter.Models;
using DocConverter.Services;
using FluentAssertions;
using Xunit;

namespace DocConverter.Tests;

/// <summary>
/// Güncelleme kurulum dosyası yalnızca resmî depodan alınır ve yayınlanan dosyayla aynı olduğu doğrulanmadan çalıştırılmaz.
/// </summary>
public class UpdateInstallerVerificationTests
{
    private const string Official = "https://github.com/Proje2025/DocMasterPro/releases/download/v1.3.0/";
    private const string Digest = "f793d4012d08f25d017d3ed5a3bd253974e4adae0bb72c889131eed0e296345a";

    [Fact]
    public void SelectInstaller_PicksTheSetupFile_NotOtherExecutablesOrArchives()
    {
        var release = new GitHubReleaseDto
        {
            Assets = new[]
            {
                new GitHubReleaseAssetDto { Name = "DocConverter.exe", BrowserDownloadUrl = Official + "DocConverter.exe", Size = 1 },
                new GitHubReleaseAssetDto { Name = "DocMasterPro-win-Portable.zip", BrowserDownloadUrl = Official + "DocMasterPro-win-Portable.zip", Size = 2 },
                new GitHubReleaseAssetDto { Name = "DocMasterPro-win-Setup.exe", BrowserDownloadUrl = Official + "DocMasterPro-win-Setup.exe", Size = 3, Digest = "sha256:" + Digest },
            }
        };

        GitHubReleaseAssetDto? installer = UpdateService.SelectInstaller(release);

        installer.Should().NotBeNull();
        installer!.Name.Should().Be("DocMasterPro-win-Setup.exe");
    }

    [Fact]
    public void SelectInstaller_IgnoresASetupFileHostedOutsideTheOfficialRepository()
    {
        var release = new GitHubReleaseDto
        {
            Assets = new[]
            {
                new GitHubReleaseAssetDto
                {
                    Name = "DocMasterPro-win-Setup.exe",
                    BrowserDownloadUrl = "https://github.com/baska-hesap/DocMasterPro/releases/download/v9.9.9/DocMasterPro-win-Setup.exe",
                },
            }
        };

        UpdateService.SelectInstaller(release).Should().BeNull();
        UpdateService.SelectInstaller(new GitHubReleaseDto()).Should().BeNull();
    }

    [Theory]
    [InlineData("https://github.com/ahiska03/DocMasterPro/releases/download/v9/DocMasterPro-win-Setup.exe")]
    [InlineData("http://github.com/Proje2025/DocMasterPro/releases/download/v1/DocMasterPro-win-Setup.exe")]
    [InlineData("https://github.com.sahte.example/Proje2025/DocMasterPro/releases/download/v1/DocMasterPro-win-Setup.exe")]
    [InlineData("https://sahte.example/?u=https://github.com/Proje2025/DocMasterPro/releases/download/v1/DocMasterPro-win-Setup.exe")]
    [InlineData("https://github.com/Proje2025/DocMasterPro/releases/download/v1/DocConverter.exe")]
    [InlineData("https://github.com/Proje2025/DocMasterPro/releases/download/v1/DocMasterPro-win-Portable.zip")]
    [InlineData("")]
    [InlineData(null)]
    public void UntrustedInstallerAddress_IsRejected(string? url)
    {
        UpdateService.IsTrustedInstallerUrl(url).Should().BeFalse();
    }

    [Fact]
    public void OfficialInstallerAddress_IsAccepted()
    {
        UpdateService.IsTrustedInstallerUrl(Official + "DocMasterPro-win-Setup.exe").Should().BeTrue();
    }

    [Theory]
    [InlineData("sha256:" + Digest, Digest)]
    [InlineData("SHA256:F793D4012D08F25D017D3ED5A3BD253974E4ADAE0BB72C889131EED0E296345A", Digest)]
    [InlineData("sha1:da39a3ee5e6b4b0d3255bfef95601890afd80709", null)]
    [InlineData("sha256:kisa", null)]
    [InlineData("sha256:zz93d4012d08f25d017d3ed5a3bd253974e4adae0bb72c889131eed0e296345a", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Digest_IsUsedOnlyWhenItIsAWellFormedSha256(string? digest, string? expected)
    {
        UpdateService.ParseSha256Digest(digest).Should().Be(expected);
    }

    [Fact]
    public async Task DownloadedFile_MustMatchThePublishedSizeAndHash()
    {
        byte[] content = Encoding.UTF8.GetBytes("kurulum dosyası içeriği");
        string hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        byte[] tampered = content.ToArray();
        tampered[3] ^= 0x01;

        (await UpdateService.MatchesAsync(new MemoryStream(content), content.Length, hash, default)).Should().BeTrue();
        (await UpdateService.MatchesAsync(new MemoryStream(content), null, hash.ToUpperInvariant(), default)).Should().BeTrue();
        (await UpdateService.MatchesAsync(new MemoryStream(tampered), content.Length, hash, default)).Should().BeFalse("içerik değişmiş");
        (await UpdateService.MatchesAsync(new MemoryStream(content), content.Length + 1, hash, default)).Should().BeFalse("boyut farklı");
    }

    [Fact]
    public async Task CheckForUpdates_AsksOnlyTheOfficialRepository_EvenWhenItIsUnreachable()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var service = new UpdateService(new HttpClient(handler));

        UpdateCheckResult result = await service.CheckForUpdatesAsync();

        handler.Requests.Should().NotBeEmpty();
        handler.Requests.Should().OnlyContain(url => url.StartsWith("https://api.github.com/repos/Proje2025/DocMasterPro/"),
            "başka bir hesaptaki kopyaya düşülmemeli");
        result.SetupDownloadUrl.Should().BeNull();
        result.HasUpdate.Should().BeFalse();
    }

    [Fact]
    public async Task CheckForUpdates_ReadsTheInstallerAddressAndDigestFromTheRelease()
    {
        string json = $$"""
            {
              "tag_name": "v99.0.0",
              "name": "DocMaster Pro v99.0.0",
              "assets": [
                { "name": "DocConverter.exe", "size": 10, "browser_download_url": "{{Official}}DocConverter.exe", "digest": "sha256:{{new string('a', 64)}}" },
                { "name": "DocMasterPro-win-Setup.exe", "size": 95037673, "browser_download_url": "{{Official}}DocMasterPro-win-Setup.exe", "digest": "sha256:{{Digest}}" }
              ]
            }
            """;
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        var service = new UpdateService(new HttpClient(handler));

        UpdateCheckResult result = await service.CheckForUpdatesAsync();

        result.HasUpdate.Should().BeTrue();
        result.SetupDownloadUrl.Should().Be(Official + "DocMasterPro-win-Setup.exe");
        result.SetupFileSize.Should().Be(95037673);
        result.SetupSha256.Should().Be(Digest);
    }

    [Fact]
    public async Task Install_RefusesAnInstallerItCannotVerify()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) });
        var service = new UpdateService(new HttpClient(handler));
        var progress = new Progress<UpdateProgressInfo>();

        var withoutDigest = () => service.DownloadAndInstallUpdateAsync(
            new UpdateCheckResult { SetupDownloadUrl = Official + "DocMasterPro-win-Setup.exe", SetupFileSize = 3 }, progress);
        var foreignAddress = () => service.DownloadAndInstallUpdateAsync(
            new UpdateCheckResult { SetupDownloadUrl = "https://sahte.example/DocMasterPro-win-Setup.exe", SetupSha256 = Digest }, progress);

        await withoutDigest.Should().ThrowAsync<InvalidOperationException>();
        await foreignAddress.Should().ThrowAsync<InvalidOperationException>();
        handler.Requests.Should().BeEmpty("doğrulanamayacak dosya hiç indirilmez");
    }

    [Fact]
    public async Task Install_DeletesAndDoesNotRunADownloadThatDoesNotMatch()
    {
        byte[] served = Encoding.UTF8.GetBytes("beklenenden farklı içerik");
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(served) });
        var service = new UpdateService(new HttpClient(handler));
        string downloaded = Path.Combine(Path.GetTempPath(), "DocMasterProUpdates", "DocMasterPro-Setup.exe");

        var act = () => service.DownloadAndInstallUpdateAsync(
            new UpdateCheckResult { SetupDownloadUrl = Official + "DocMasterPro-win-Setup.exe", SetupFileSize = served.Length, SetupSha256 = Digest },
            new Progress<UpdateProgressInfo>());

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("doğrulanamadı");
        File.Exists(downloaded).Should().BeFalse("doğrulanmayan kurulum dosyası diskte bırakılmaz ve çalıştırılmaz");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(_respond(request));
        }
    }
}
