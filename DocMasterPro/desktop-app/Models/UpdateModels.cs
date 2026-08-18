using System;
using System.Text.Json.Serialization;
using Velopack;

namespace DocConverter.Models
{
    public enum UpdateState
    {
        Idle,
        Checking,
        UpToDate,
        UpdateAvailable,
        Downloading,
        Installing,
        Success,
        Error
    }

    public enum UpdateInstallMethod
    {
        Velopack,
        DirectInstaller
    }

    public sealed class UpdateProgressInfo
    {
        public int Percentage { get; set; }
        public string StageTitle { get; set; } = "Güncelleme İşleniyor...";
        public string StageDescription { get; set; } = "";
        public long BytesReceived { get; set; }
        public long? TotalBytesToReceive { get; set; }
        public double DownloadSpeedBytesPerSec { get; set; }

        public string FormattedBytesText
        {
            get
            {
                string received = FormatBytes(BytesReceived);
                if (TotalBytesToReceive.HasValue && TotalBytesToReceive.Value > 0)
                {
                    string total = FormatBytes(TotalBytesToReceive.Value);
                    return $"{received} / {total}";
                }
                return received;
            }
        }

        public string FormattedSpeedText
        {
            get
            {
                if (DownloadSpeedBytesPerSec <= 0) return "-- MB/s";
                return $"{FormatBytes((long)DownloadSpeedBytesPerSec)}/s";
            }
        }

        public string FormattedTimeRemaining
        {
            get
            {
                if (!TotalBytesToReceive.HasValue || DownloadSpeedBytesPerSec <= 0) return "";
                long remainingBytes = TotalBytesToReceive.Value - BytesReceived;
                if (remainingBytes <= 0) return "Tamamlanıyor...";

                double seconds = remainingBytes / DownloadSpeedBytesPerSec;
                if (seconds < 60)
                    return $"~{(int)Math.Ceiling(seconds)} sn";
                int minutes = (int)(seconds / 60);
                int sec = (int)(seconds % 60);
                return $"~{minutes} dk {sec} sn";
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "0 B";
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            double dbl = bytes;
            while (dbl >= 1024 && i < suffixes.Length - 1)
            {
                dbl /= 1024;
                i++;
            }
            return $"{dbl:0.##} {suffixes[i]}";
        }
    }

    public sealed class UpdateCheckResult
    {
        public bool HasUpdate { get; set; }
        public string CurrentVersion { get; set; } = "1.0.0";
        public string LatestVersion { get; set; } = "1.0.0";
        public string ReleaseTitle { get; set; } = "";
        public string ReleaseNotes { get; set; } = "";
        public DateTime? PublishedAt { get; set; }
        public string? SetupDownloadUrl { get; set; }
        public long? SetupFileSize { get; set; }
        public string? HtmlUrl { get; set; }
        public UpdateInfo? VelopackUpdate { get; set; }
        public UpdateInstallMethod RecommendedMethod { get; set; } = UpdateInstallMethod.DirectInstaller;
        public string? ErrorMessage { get; set; }
    }

    public sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("published_at")]
        public DateTime? PublishedAt { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("assets")]
        public GitHubReleaseAssetDto[]? Assets { get; set; }
    }

    public sealed class GitHubReleaseAssetDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        [JsonPropertyName("content_type")]
        public string? ContentType { get; set; }
    }
}
