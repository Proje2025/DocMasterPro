using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DocConverter.Models;

namespace DocConverter.Services
{
    public class DeviceDiscoveryService
    {
        // Fujitsu fi-6230 & fi-6230Z Hardware USB Identifiers
        private static readonly string[] FujitsuHwIds = {
            "VID_04C5&PID_1155", // fi-6230
            "VID_04C5&PID_1175", // fi-6230Z
            "VID_04C5&PID_114F", // fi-6130
            "VID_04C5&PID_1174"  // fi-6130Z
        };

        /// <summary>
        /// Bilgisayara bağlı yerel (USB/PnP) ve ağdaki tüm yazıcı ve tarayıcıları keşfeder.
        /// </summary>
        public async Task<List<DeviceInfo>> DiscoverAllDevicesAsync(
            bool scanNetwork = true,
            CancellationToken cancellationToken = default,
            IProgress<string>? progress = null)
        {
            var results = new List<DeviceInfo>();

            // 1. Yerel Windows Yazıcılarını Bul
            progress?.Report("Yerel ve sanal yazıcılar taranıyor...");
            var printers = await Task.Run(() => DiscoverInstalledPrinters(cancellationToken), cancellationToken);
            results.AddRange(printers);

            // 2. WMI ve PnP üzerinden Tarayıcıları & Takılı Cihazları Bul (Fujitsu fi-6230 dahil)
            progress?.Report("USB ve WIA tarayıcı aygıtları taranıyor...");
            var scanners = await Task.Run(() => DiscoverScannersAndPnpDevices(cancellationToken), cancellationToken);
            foreach (var sc in scanners)
            {
                if (!results.Any(r => r.Name.Equals(sc.Name, StringComparison.OrdinalIgnoreCase) ||
                                     (!string.IsNullOrEmpty(sc.SerialOrHardwareId) && r.SerialOrHardwareId == sc.SerialOrHardwareId)))
                {
                    results.Add(sc);
                }
            }

            // 3. Yerel Ağdaki Yazıcı ve Çok Fonksiyonlu Cihazları Tara (Ricoh SP 4510SF dahil)
            if (scanNetwork)
            {
                progress?.Report("Yerel ağdaki yazıcılar ve Ricoh SP 4510SF taranıyor...");
                var netDevices = await DiscoverNetworkPrintersAsync(cancellationToken, progress);
                foreach (var nd in netDevices)
                {
                    if (!results.Any(r => r.IpAddress == nd.IpAddress && !string.IsNullOrEmpty(nd.IpAddress)))
                    {
                        results.Add(nd);
                    }
                }
            }

            // Model presetlerini işaretle
            foreach (var d in results)
            {
                d.PresetModel = IdentifyPresetModel(d);
            }

            progress?.Report($"Keşif tamamlandı: Toplam {results.Count} cihaz bulundu.");
            return results;
        }

        /// <summary>
        /// Windows'ta kurulu yazıcıları listeler
        /// </summary>
        public List<DeviceInfo> DiscoverInstalledPrinters(CancellationToken cancellationToken = default)
        {
            var list = new List<DeviceInfo>();
            string defaultPrinterName = string.Empty;

            try
            {
                var settings = new PrinterSettings();
                defaultPrinterName = settings.PrinterName ?? string.Empty;
            }
            catch
            {
                // default printer not set
            }

            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Printer");
                    using var collection = searcher.Get();

                    foreach (ManagementObject printer in collection)
                    {
                        if (cancellationToken.IsCancellationRequested) break;

                        string name = printer["Name"]?.ToString() ?? "Bilinmeyen Yazıcı";
                        string portName = printer["PortName"]?.ToString() ?? "";
                        string driverName = printer["DriverName"]?.ToString() ?? "";
                        bool isNetwork = (bool?)printer["Network"] ?? false;
                        bool isDefault = (bool?)printer["Default"] ?? (name.Equals(defaultPrinterName, StringComparison.OrdinalIgnoreCase));
                        string status = printer["PrinterStatus"]?.ToString() ?? "Hazır";

                        var connType = DeviceConnectionType.USB;
                        string ip = "";
                        int port = 9100;

                        if (portName.StartsWith("IP_", StringComparison.OrdinalIgnoreCase) ||
                            portName.StartsWith("192.", StringComparison.OrdinalIgnoreCase) ||
                            portName.StartsWith("10.", StringComparison.OrdinalIgnoreCase) ||
                            portName.StartsWith("172.", StringComparison.OrdinalIgnoreCase))
                        {
                            connType = DeviceConnectionType.NetworkIP;
                            ip = portName.Replace("IP_", "", StringComparison.OrdinalIgnoreCase);
                        }
                        else if (name.Contains("PDF", StringComparison.OrdinalIgnoreCase) ||
                                 name.Contains("XPS", StringComparison.OrdinalIgnoreCase) ||
                                 portName.StartsWith("PORTPROMPT", StringComparison.OrdinalIgnoreCase))
                        {
                            connType = DeviceConnectionType.Virtual;
                        }
                        else if (isNetwork || portName.StartsWith("WSD", StringComparison.OrdinalIgnoreCase))
                        {
                            connType = DeviceConnectionType.SharedWsd;
                        }

                        var dev = new DeviceInfo
                        {
                            Id = $"PRN_{name.GetHashCode():X8}",
                            Name = name,
                            DriverName = driverName,
                            Type = name.Contains("4510", StringComparison.OrdinalIgnoreCase) ? DeviceType.MultiFunction : DeviceType.Printer,
                            ConnectionType = connType,
                            IpAddress = ip,
                            Port = port,
                            DriverState = DriverState.Ready,
                            IsDefault = isDefault,
                            IsOnline = true,
                            StatusMessage = "Kullanıma Hazır",
                            Manufacturer = GetManufacturerFromName(name)
                        };

                        dev.PresetModel = IdentifyPresetModel(dev);
                        list.Add(dev);
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.LogError("DiscoverInstalledPrinters WMI Error", ex);
                // Fallback to PrinterSettings
                try
                {
                    foreach (string pName in PrinterSettings.InstalledPrinters)
                    {
                        var dev = new DeviceInfo
                        {
                            Id = $"PRN_{pName.GetHashCode():X8}",
                            Name = pName,
                            Type = DeviceType.Printer,
                            ConnectionType = DeviceConnectionType.USB,
                            DriverState = DriverState.Ready,
                            IsDefault = pName.Equals(defaultPrinterName, StringComparison.OrdinalIgnoreCase),
                            IsOnline = true,
                            StatusMessage = "Hazır"
                        };
                        dev.PresetModel = IdentifyPresetModel(dev);
                        list.Add(dev);
                    }
                }
                catch
                {
                    // Fallback
                }
            }

            return list;
        }

        /// <summary>
        /// USB / PnP ve WIA üzerinden bağlı tarayıcıları bulur
        /// </summary>
        public List<DeviceInfo> DiscoverScannersAndPnpDevices(CancellationToken cancellationToken = default)
        {
            var list = new List<DeviceInfo>();

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return list;

            try
            {
                // 1. WIA Aygıt Yöneticisi Sorgusu
                var wiaScanners = DiscoverWiaScanners();
                list.AddRange(wiaScanners);

                // 2. PnP Donanım Kimlikleri ile Takılı ama Sürücüsü Eksik/Yüklü Fujitsu & Tarayıcıları Sorgula
                using var pnpSearcher = new ManagementObjectSearcher(
                    "SELECT DeviceID, Name, Description, PNPClass, Status FROM Win32_PnPEntity WHERE " +
                    "PNPClass = 'Image' OR PNPClass = 'USB' OR " +
                    "Description LIKE '%Scanner%' OR Description LIKE '%ScanSnap%' OR Description LIKE '%Fujitsu%' OR " +
                    "Description LIKE '%Ricoh%' OR Name LIKE '%fi-6230%' OR Name LIKE '%SP 4510%'");

                using var pnpCollection = pnpSearcher.Get();

                foreach (ManagementObject obj in pnpCollection)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    string deviceId = obj["DeviceID"]?.ToString() ?? "";
                    string name = obj["Name"]?.ToString() ?? obj["Description"]?.ToString() ?? "";
                    string status = obj["Status"]?.ToString() ?? "OK";
                    string pnpClass = obj["PNPClass"]?.ToString() ?? "";

                    bool isFujitsu = FujitsuHwIds.Any(hw => deviceId.Contains(hw, StringComparison.OrdinalIgnoreCase)) ||
                                     name.Contains("fi-6230", StringComparison.OrdinalIgnoreCase) ||
                                     name.Contains("6230", StringComparison.OrdinalIgnoreCase);

                    bool isRicoh = deviceId.Contains("4510", StringComparison.OrdinalIgnoreCase) ||
                                   name.Contains("4510", StringComparison.OrdinalIgnoreCase) ||
                                   name.Contains("SP4510", StringComparison.OrdinalIgnoreCase);

                    if (isFujitsu)
                    {
                        // Check if already in list
                        if (!list.Any(x => x.IsFujitsuSpecial))
                        {
                            bool isDriverReady = status.Equals("OK", StringComparison.OrdinalIgnoreCase) &&
                                                 pnpClass.Equals("Image", StringComparison.OrdinalIgnoreCase);

                            var fujitsuDev = new DeviceInfo
                            {
                                Id = $"SCN_FUJITSU_6230",
                                Name = "Fujitsu fi-6230 / fi-6230Z High-Speed Scanner",
                                Manufacturer = "Fujitsu / Ricoh",
                                ModelName = "fi-6230",
                                Type = DeviceType.Scanner,
                                ConnectionType = DeviceConnectionType.USB,
                                SerialOrHardwareId = deviceId,
                                DriverState = isDriverReady ? DriverState.Ready : DriverState.Missing,
                                StatusMessage = isDriverReady ? "Taramaya Hazır (ADF / Flatbed)" : "Sürücü Kurulumu Gerekli",
                                PresetModel = DevicePresetModel.FujitsuFi6230
                            };
                            list.Add(fujitsuDev);
                        }
                    }
                    else if (isRicoh && !list.Any(x => x.IsRicohSpecial))
                    {
                        var ricohDev = new DeviceInfo
                        {
                            Id = $"MFP_RICOH_4510",
                            Name = "Ricoh SP 4510SF Multifunction",
                            Manufacturer = "Ricoh",
                            ModelName = "SP 4510SF",
                            Type = DeviceType.MultiFunction,
                            ConnectionType = DeviceConnectionType.USB,
                            SerialOrHardwareId = deviceId,
                            DriverState = status.Equals("OK", StringComparison.OrdinalIgnoreCase) ? DriverState.Ready : DriverState.Missing,
                            StatusMessage = "Kullanıma Hazır",
                            PresetModel = DevicePresetModel.RicohSP4510SF
                        };
                        list.Add(ricohDev);
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.LogError("DiscoverScannersAndPnpDevices Error", ex);
            }

            return list;
        }

        /// <summary>
        /// WIA COM arayüzü ile kayıtlı tarayıcıları sorgular
        /// </summary>
        public List<DeviceInfo> DiscoverWiaScanners()
        {
            var list = new List<DeviceInfo>();

            try
            {
                Type? deviceManagerType = Type.GetTypeFromProgID("WIA.DeviceManager");
                if (deviceManagerType == null) return list;

                dynamic? deviceManager = Activator.CreateInstance(deviceManagerType);
                if (deviceManager == null) return list;

                dynamic deviceInfos = deviceManager.DeviceInfos;
                int count = (int)deviceInfos.Count;

                for (int i = 1; i <= count; i++)
                {
                    dynamic info = deviceInfos[i];
                    int type = (int)info.Type;

                    // WIA: 1 = ScannerDeviceType, 2 = CameraDeviceType, 3 = VideoDeviceType
                    if (type == 1)
                    {
                        string id = (string)info.DeviceID;
                        dynamic properties = info.Properties;

                        string name = GetWiaPropertyValue(properties, "Name") ?? "WIA Tarayıcı";
                        string desc = GetWiaPropertyValue(properties, "Description") ?? name;
                        string mfg = GetWiaPropertyValue(properties, "Manufacturer") ?? "Standart Tarayıcı";

                        var dev = new DeviceInfo
                        {
                            Id = $"WIA_{id.GetHashCode():X8}",
                            Name = name,
                            Manufacturer = mfg,
                            ModelName = desc,
                            Type = DeviceType.Scanner,
                            ConnectionType = DeviceConnectionType.USB,
                            DriverState = DriverState.Ready,
                            SerialOrHardwareId = id,
                            StatusMessage = "Taramaya Hazır (WIA 2.0)",
                            PresetModel = IdentifyPresetModelByName(name)
                        };

                        list.Add(dev);
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.LogError("WIA DeviceManager scan warning", ex);
            }

            return list;
        }

        private static string? GetWiaPropertyValue(dynamic properties, string propName)
        {
            try
            {
                dynamic prop = properties[propName];
                return prop?.Value?.ToString();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Yerel ağdaki (Subnet) yazıcıları ve Ricoh SP 4510SF cihazını keşfeder
        /// </summary>
        public async Task<List<DeviceInfo>> DiscoverNetworkPrintersAsync(
            CancellationToken cancellationToken = default,
            IProgress<string>? progress = null)
        {
            var list = new List<DeviceInfo>();

            try
            {
                var localIps = GetLocalIPv4Addresses();
                if (!localIps.Any())
                {
                    progress?.Report("Aktif yerel ağ bağlantısı bulunamadı.");
                    return list;
                }

                var subnets = localIps.Select(ip =>
                {
                    var parts = ip.Split('.');
                    return parts.Length == 4 ? $"{parts[0]}.{parts[1]}.{parts[2]}." : null;
                }).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();

                var allCandidateIps = new List<string>();
                foreach (var subnet in subnets)
                {
                    for (int i = 1; i <= 254; i++)
                    {
                        allCandidateIps.Add($"{subnet}{i}");
                    }
                }

                int totalIps = allCandidateIps.Count;
                int scannedCount = 0;
                using var semaphore = new SemaphoreSlim(40); // 40 paralel sorgu

                var tasks = allCandidateIps.Select(async ip =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        if (cancellationToken.IsCancellationRequested) return null;

                        // Port 9100 (RAW JetDirect), 631 (IPP) veya 515 (LPR)
                        bool isRawPortOpen = await IsPortOpenAsync(ip, 9100, 300, cancellationToken);
                        bool isIppPortOpen = !isRawPortOpen && await IsPortOpenAsync(ip, 631, 300, cancellationToken);
                        bool isLprPortOpen = !isRawPortOpen && !isIppPortOpen && await IsPortOpenAsync(ip, 515, 300, cancellationToken);

                        int current = Interlocked.Increment(ref scannedCount);
                        if (current % 15 == 0 || current == totalIps)
                        {
                            progress?.Report($"Ağ taranıyor... ({current}/{totalIps})");
                        }

                        if (isRawPortOpen || isIppPortOpen || isLprPortOpen)
                        {
                            int activePort = isRawPortOpen ? 9100 : (isIppPortOpen ? 631 : 515);
                            var dev = await ProbeNetworkDeviceDetailsAsync(ip, activePort, cancellationToken);
                            return dev;
                        }
                    }
                    catch
                    {
                        // Ignore timeout / unreachable
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                    return null;
                });

                var results = await Task.WhenAll(tasks);
                foreach (var r in results.Where(x => x != null))
                {
                    list.Add(r!);
                }
            }
            catch (Exception ex)
            {
                FileLogger.LogError("DiscoverNetworkPrintersAsync error", ex);
            }

            return list;
        }

        private async Task<DeviceInfo> ProbeNetworkDeviceDetailsAsync(string ip, int port, CancellationToken cancellationToken)
        {
            string hostName = ip;
            string modelName = "Ağ Yazıcısı";
            string manufacturer = "Genel";
            var preset = DevicePresetModel.GenericPclPrinter;

            try
            {
                var entry = await Dns.GetHostEntryAsync(ip);
                if (!string.IsNullOrEmpty(entry?.HostName))
                {
                    hostName = entry.HostName;
                }
            }
            catch
            {
                // DNS lookup failed
            }

            // Ricoh SP 4510SF veya Ricoh MFP kontrolü
            if (hostName.Contains("RICOH", StringComparison.OrdinalIgnoreCase) ||
                hostName.Contains("4510", StringComparison.OrdinalIgnoreCase) ||
                hostName.Contains("AFICIO", StringComparison.OrdinalIgnoreCase))
            {
                preset = DevicePresetModel.RicohSP4510SF;
                manufacturer = "Ricoh";
                modelName = "Ricoh SP 4510SF (Ağ / PCL6)";
            }
            else
            {
                modelName = $"Ağ Yazıcısı ({hostName})";
            }

            return new DeviceInfo
            {
                Id = $"NET_{ip.Replace(".", "_")}_{port}",
                Name = preset == DevicePresetModel.RicohSP4510SF ? "Ricoh SP 4510SF Network MFP" : modelName,
                Manufacturer = manufacturer,
                ModelName = modelName,
                Type = preset == DevicePresetModel.RicohSP4510SF ? DeviceType.MultiFunction : DeviceType.Printer,
                ConnectionType = DeviceConnectionType.NetworkIP,
                IpAddress = ip,
                Port = port,
                DriverState = DriverState.Ready,
                IsOnline = true,
                StatusMessage = "Ağ Bağlantısı Aktif (Port 9100 RAW)",
                PresetModel = preset
            };
        }

        public static async Task<bool> IsPortOpenAsync(string host, int port, int timeoutMs, CancellationToken cancellationToken)
        {
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(host, port);
                var delayTask = Task.Delay(timeoutMs, cancellationToken);

                var completedTask = await Task.WhenAny(connectTask, delayTask);
                if (completedTask == connectTask && client.Connected)
                {
                    return true;
                }
            }
            catch
            {
                // Port closed or error
            }
            return false;
        }

        private static List<string> GetLocalIPv4Addresses()
        {
            var ips = new List<string>();
            try
            {
                foreach (var netInterface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (netInterface.OperationalStatus == OperationalStatus.Up &&
                        netInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    {
                        var ipProps = netInterface.GetIPProperties();
                        foreach (var addr in ipProps.UnicastAddresses)
                        {
                            if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                                !IPAddress.IsLoopback(addr.Address))
                            {
                                ips.Add(addr.Address.ToString());
                            }
                        }
                    }
                }
            }
            catch
            {
                // Fallback
            }
            return ips;
        }

        public static DevicePresetModel IdentifyPresetModel(DeviceInfo d)
        {
            if (d.PresetModel != DevicePresetModel.None)
                return d.PresetModel;

            return IdentifyPresetModelByName(d.Name + " " + d.ModelName + " " + d.DriverName);
        }

        public static DevicePresetModel IdentifyPresetModelByName(string text)
        {
            if (string.IsNullOrEmpty(text)) return DevicePresetModel.None;

            if (text.Contains("4510", StringComparison.OrdinalIgnoreCase) ||
                (text.Contains("Ricoh", StringComparison.OrdinalIgnoreCase) && text.Contains("SP", StringComparison.OrdinalIgnoreCase)))
            {
                return DevicePresetModel.RicohSP4510SF;
            }

            if (text.Contains("6230", StringComparison.OrdinalIgnoreCase) ||
                (text.Contains("Fujitsu", StringComparison.OrdinalIgnoreCase) && text.Contains("fi-", StringComparison.OrdinalIgnoreCase)))
            {
                return DevicePresetModel.FujitsuFi6230;
            }

            if (text.Contains("Scan", StringComparison.OrdinalIgnoreCase) || text.Contains("WIA", StringComparison.OrdinalIgnoreCase))
            {
                return DevicePresetModel.GenericWiaScanner;
            }

            return DevicePresetModel.GenericPclPrinter;
        }

        private static string GetManufacturerFromName(string name)
        {
            if (name.Contains("Ricoh", StringComparison.OrdinalIgnoreCase)) return "Ricoh";
            if (name.Contains("Fujitsu", StringComparison.OrdinalIgnoreCase)) return "Fujitsu";
            if (name.Contains("HP", StringComparison.OrdinalIgnoreCase)) return "HP";
            if (name.Contains("Canon", StringComparison.OrdinalIgnoreCase)) return "Canon";
            if (name.Contains("Epson", StringComparison.OrdinalIgnoreCase)) return "Epson";
            if (name.Contains("Brother", StringComparison.OrdinalIgnoreCase)) return "Brother";
            if (name.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) return "Microsoft";
            if (name.Contains("Adobe", StringComparison.OrdinalIgnoreCase)) return "Adobe";
            return "Standart";
        }
    }
}
