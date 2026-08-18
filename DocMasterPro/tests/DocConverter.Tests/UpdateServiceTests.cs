using System;
using System.Globalization;
using System.Windows;
using DocConverter.Converters;
using DocConverter.Models;
using DocConverter.Services;
using DocConverter.ViewModels;
using FluentAssertions;
using Xunit;

namespace DocConverter.Tests
{
    public class UpdateServiceTests
    {
        [Theory]
        [InlineData("1.1.1", "1.1.0", true)]
        [InlineData("1.2.0", "1.1.0", true)]
        [InlineData("2.0.0", "1.9.9", true)]
        [InlineData("v1.1.5", "1.1.0", true)]
        [InlineData("v2.0.0-beta", "1.1.0", true)]
        [InlineData("1.0.5", "1.1.0", false)]
        [InlineData("1.1.0", "1.1.0", false)]
        [InlineData("v1.1.0", "1.1.0", false)]
        [InlineData("", "1.1.0", false)]
        [InlineData("1.1.0", "", false)]
        [InlineData(null, "1.1.0", false)]
        public void IsNewerVersion_ComparesCorrectly(string? latest, string? current, bool expectedResult)
        {
            // Act
            bool result = UpdateService.IsNewerVersion(latest!, current!);

            // Assert
            result.Should().Be(expectedResult);
        }

        [Fact]
        public void UpdateProgressInfo_FormatsBytesAndSpeedCorrectly()
        {
            // Arrange
            var progress = new UpdateProgressInfo
            {
                Percentage = 45,
                BytesReceived = 45 * 1024 * 1024,
                TotalBytesToReceive = 100 * 1024 * 1024,
                DownloadSpeedBytesPerSec = 5 * 1024 * 1024
            };

            // Assert
            progress.FormattedBytesText.Should().Contain("45 MB / 100 MB");
            progress.FormattedSpeedText.Should().Be("5 MB/s");
            progress.FormattedTimeRemaining.Should().Be("~11 sn");
        }

        [Fact]
        public void UpdateStateVisibilityConverter_ShowsTargetState()
        {
            // Arrange
            var converter = new UpdateStateVisibilityConverter();

            // Act
            var visibleResult = converter.Convert(
                UpdateState.Downloading,
                typeof(Visibility),
                "Downloading,Installing",
                CultureInfo.InvariantCulture);

            var collapsedResult = converter.Convert(
                UpdateState.UpToDate,
                typeof(Visibility),
                "Downloading,Installing",
                CultureInfo.InvariantCulture);

            // Assert
            visibleResult.Should().Be(Visibility.Visible);
            collapsedResult.Should().Be(Visibility.Collapsed);
        }

        [Fact]
        public void UpdateViewModel_InitializesWithCurrentVersion()
        {
            // Arrange & Act
            var vm = new UpdateViewModel();

            // Assert
            vm.State.Should().Be(UpdateState.Checking);
            vm.CurrentVersion.Should().NotBeNullOrWhiteSpace();
            vm.IsBusy.Should().BeFalse();
        }
    }
}
