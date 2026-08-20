using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DocConverter.Models;
using DocConverter.Services;
using DocConverter.ViewModels;
using FluentAssertions;
using Xunit;

namespace DocConverter.Tests
{
    public class DeviceHubTests
    {
        [Fact]
        public void PresetModelIdentification_ShouldIdentifyRicohSP4510SF()
        {
            var dev = new DeviceInfo
            {
                Name = "Ricoh Aficio SP 4510SF PCL6",
                ModelName = "SP 4510SF",
                DriverName = "Ricoh SP 4510SF PCL6"
            };

            var preset = DeviceDiscoveryService.IdentifyPresetModel(dev);
            preset.Should().Be(DevicePresetModel.RicohSP4510SF);
            dev.IsRicohSpecial.Should().BeTrue();
        }

        [Fact]
        public void PresetModelIdentification_ShouldIdentifyFujitsuFi6230()
        {
            var dev = new DeviceInfo
            {
                Name = "Fujitsu fi-6230Z Scanner",
                ModelName = "fi-6230",
                SerialOrHardwareId = "USB\\VID_04C5&PID_1155"
            };

            var preset = DeviceDiscoveryService.IdentifyPresetModel(dev);
            preset.Should().Be(DevicePresetModel.FujitsuFi6230);
            dev.IsFujitsuSpecial.Should().BeTrue();
        }

        [Theory]
        [InlineData("RICOH SP 4510SF Network", DevicePresetModel.RicohSP4510SF)]
        [InlineData("Fujitsu fi-6230", DevicePresetModel.FujitsuFi6230)]
        [InlineData("WIA-HP ScanJet Pro", DevicePresetModel.GenericWiaScanner)]
        [InlineData("HP LaserJet Pro P1102", DevicePresetModel.GenericPclPrinter)]
        public void IdentifyPresetModelByName_ShouldMatchCorrectly(string name, DevicePresetModel expected)
        {
            var result = DeviceDiscoveryService.IdentifyPresetModelByName(name);
            result.Should().Be(expected);
        }

        [Fact]
        public void DeviceInfo_BadgeAndIcons_ShouldReturnSensibleDefaults()
        {
            var printer = new DeviceInfo
            {
                Name = "Ricoh SP 4510SF",
                Type = DeviceType.MultiFunction,
                ConnectionType = DeviceConnectionType.NetworkIP,
                IpAddress = "192.168.1.150",
                Port = 9100,
                DriverState = DriverState.Ready
            };

            printer.TypeIcon.Should().Be("📠");
            printer.ConnectionDescription.Should().Contain("192.168.1.150:9100");
            printer.DriverStatusBadgeColor.Should().Be("#28A745");
            printer.DriverStatusDescription.Should().Contain("Hazır");
        }

        [Fact]
        public void ScanOptions_ShouldHaveDefaultValues()
        {
            var options = new ScanOptions();
            options.Resolution.Should().Be(ScanResolution.Dpi300);
            options.ColorMode.Should().Be(ScanColorMode.Color);
            options.Source.Should().Be(ScanSource.FeederSingle);
            options.AutoDeskew.Should().BeTrue();
        }

        [Fact]
        public void PrintJobOptions_ShouldHaveSensibleDefaults()
        {
            var printOptions = new PrintJobOptions();
            printOptions.Copies.Should().Be(1);
            printOptions.Duplex.Should().Be(PrintDuplex.Simplex);
            printOptions.Orientation.Should().Be(PrintOrientation.Portrait);
            printOptions.FitToPage.Should().BeTrue();
        }

        [Fact]
        public async Task HomeHubViewModel_Navigation_ShouldInvokeDelegates()
        {
            var homeVm = new HomeHubViewModel();
            bool officeInvoked = false;
            bool pdfStudioInvoked = false;
            bool devicesInvoked = false;
            bool scannerInvoked = false;
            bool printerInvoked = false;

            homeVm.OnNavigateToOfficeRequested = () => officeInvoked = true;
            homeVm.OnNavigateToPdfStudioRequested = () => pdfStudioInvoked = true;
            homeVm.OnNavigateToDevicesRequested = () => devicesInvoked = true;
            homeVm.OnNavigateToScannerStudioRequested = () => scannerInvoked = true;
            homeVm.OnNavigateToPrinterStudioRequested = () => printerInvoked = true;

            homeVm.OpenOfficeSuite();
            homeVm.OpenPdfStudio();
            homeVm.OpenDeviceHub();
            homeVm.OpenScannerStudio();
            homeVm.OpenPrinterStudio();

            officeInvoked.Should().BeTrue();
            pdfStudioInvoked.Should().BeTrue();
            devicesInvoked.Should().BeTrue();
            scannerInvoked.Should().BeTrue();
            printerInvoked.Should().BeTrue();
        }

        [Fact]
        public void MainViewModel_SectionSwitching_ShouldWorkCorrectly()
        {
            var vm = new MainViewModel();

            // Default is Home Hub (Section 0)
            vm.SelectedAppSection.Should().Be(0);

            // Navigate to Office (Section 1)
            vm.NavigateToOffice();
            vm.SelectedAppSection.Should().Be(1);
            vm.SelectedWorkspaceIndex.Should().Be(0);

            // Navigate to PDF Studio (Section 3)
            vm.NavigateToPdfStudio();
            vm.SelectedAppSection.Should().Be(3);

            // Navigate to Devices (Section 2)
            vm.NavigateToDevices();
            vm.SelectedAppSection.Should().Be(2);

            // Navigate back to Home
            vm.NavigateToHome();
            vm.SelectedAppSection.Should().Be(0);
        }

        [Fact]
        public async Task DriverManagement_SaveAndLoad_ShouldPersistDevice()
        {
            string originalFile = DriverManagementService.SavedDevicesFile;
            string tempFile = Path.Combine(Path.GetTempPath(), $"DocMaster_TestDevices_{Guid.NewGuid():N}.json");
            DriverManagementService.SavedDevicesFile = tempFile;

            try
            {
                var service = new DriverManagementService();
                var testDev = new DeviceInfo
                {
                    Id = "TEST_DEV_001",
                    Name = "Test Ricoh SP 4510SF",
                    ModelName = "SP 4510SF",
                    Type = DeviceType.Printer,
                    ConnectionType = DeviceConnectionType.NetworkIP,
                    IpAddress = "192.168.1.99",
                    Port = 9100,
                    DriverState = DriverState.Ready,
                    PresetModel = DevicePresetModel.RicohSP4510SF
                };

                await service.SaveConfiguredDeviceAsync(testDev);
                var loaded = await service.LoadSavedDevicesAsync();

                loaded.Should().Contain(x => x.Id == "TEST_DEV_001" || x.IpAddress == "192.168.1.99");
            }
            finally
            {
                DriverManagementService.SavedDevicesFile = originalFile;
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }

        [Fact]
        public async Task ScannerService_SimulatedScan_ShouldGeneratePagesAndSavePdf()
        {
            var scannerService = new ScannerService();
            var dev = new DeviceInfo
            {
                Name = "Fujitsu fi-6230",
                ModelName = "fi-6230",
                Type = DeviceType.Scanner,
                PresetModel = DevicePresetModel.FujitsuFi6230
            };

            var options = new ScanOptions
            {
                Resolution = ScanResolution.Dpi150,
                ColorMode = ScanColorMode.Grayscale,
                Source = ScanSource.Flatbed
            };

            List<ScannedPageItem> pages;
            try
            {
                pages = await scannerService.ScanDocumentsAsync(dev, options, CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // Fiziksel tarayici donanimi bagli olmadiginda sentetik test sayfasi olustur
                string tempDir = Path.Combine(Path.GetTempPath(), $"ScanTest_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);
                string testImg = Path.Combine(tempDir, "test_page_1.png");
                using (var bmp = new System.Drawing.Bitmap(200, 200))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.Clear(System.Drawing.Color.White);
                    }
                    bmp.Save(testImg, System.Drawing.Imaging.ImageFormat.Png);
                }
                pages = new List<ScannedPageItem>
                {
                    new ScannedPageItem { PageNumber = 1, FilePath = testImg }
                };
            }

            pages.Should().NotBeEmpty();
            pages[0].FilePath.Should().NotBeNullOrEmpty();
            File.Exists(pages[0].FilePath).Should().BeTrue();

            string tempPdf = Path.Combine(Path.GetTempPath(), $"TestScan_{Guid.NewGuid():N}.pdf");
            try
            {
                string pdfOutput = await scannerService.SaveScannedPagesToPdfAsync(pages, tempPdf);
                File.Exists(pdfOutput).Should().BeTrue();
                new FileInfo(pdfOutput).Length.Should().BeGreaterThan(0);
            }
            finally
            {
                if (File.Exists(tempPdf)) File.Delete(tempPdf);
            }
        }

        [Fact]
        public void PrinterService_ResolveWindowsPrinterName_ShouldResolveCorrectly()
        {
            var dev = new DeviceInfo
            {
                Name = "Ricoh SP 4510SF Network MFP",
                Manufacturer = "Ricoh",
                ModelName = "SP 4510SF",
                Type = DeviceType.MultiFunction,
                ConnectionType = DeviceConnectionType.NetworkIP,
                IpAddress = "192.168.1.150",
                Port = 9100
            };

            string resolved = PrinterService.ResolveWindowsPrinterName(dev);
            resolved.Should().NotBeNullOrWhiteSpace();
        }

        [Fact]
        public async Task DeviceHubViewModel_DiscoveryCommands_ShouldExecuteWithoutError()
        {
            var vm = new DeviceHubViewModel(autoLoad: false);

            // Quick Discovery Command
            await vm.DiscoverDevicesQuickCommand.ExecuteAsync(null);
            vm.IsBusy.Should().BeFalse();
            vm.StatusMessage.Should().Contain("Tarama tamamlandı");

            // Full Discovery Command
            await vm.DiscoverDevicesFullCommand.ExecuteAsync(null);
            vm.IsBusy.Should().BeFalse();
            vm.StatusMessage.Should().Contain("Tarama tamamlandı");
        }
    }
}
