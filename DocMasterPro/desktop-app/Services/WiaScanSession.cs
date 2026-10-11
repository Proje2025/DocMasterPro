using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using DocConverter.Models;

namespace DocConverter.Services
{
    /// <summary>
    /// Windows Image Acquisition (WIA Automation) üzerinden gerçek tarayıcı oturumu.
    /// WIA nesneleri apartment-threaded olduğundan bu sınıf tek bir STA iş parçacığında kullanılmalıdır.
    /// </summary>
    internal sealed class WiaScanSession : IScanSession
    {
        internal const string FormatPng = "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}";

        private const int ScannerDeviceType = 1;

        // Cihaz özellikleri
        private const string DocumentHandlingCapabilities = "3086";
        private const string DocumentHandlingSelect = "3088";
        private const int Feeder = 0x1;
        private const int Flatbed = 0x2;
        private const int Duplex = 0x4;

        // Öğe özellikleri
        private const string CurrentIntent = "6146";
        private const string HorizontalResolution = "6147";
        private const string VerticalResolution = "6148";

        private dynamic? _manager;
        private dynamic? _device;
        private dynamic? _item;

        public int EffectiveDpi { get; private set; }

        public ScanSource EffectiveSource { get; private set; } = ScanSource.Flatbed;

        public string? Notice { get; private set; }

        public void Open(DeviceInfo scanner, ScanOptions options)
        {
            _manager = CreateDeviceManager();

            dynamic deviceInfo = FindDevice(_manager, scanner);
            try
            {
                _device = deviceInfo.Connect();
            }
            finally
            {
                Release(deviceInfo);
            }

            // Sıra önemli: belge kaynağı değişince sürücü öğe özelliklerini (DPI, renk) varsayılana
            // döndürür. Bu yüzden önce kaynak seçilir, öğe ondan sonra alınıp ayarlanır.
            EffectiveSource = SelectDocumentSource(options.Source);

            _item = _device!.Items[1];

            int intent = options.ColorMode switch
            {
                ScanColorMode.Grayscale => 2,
                ScanColorMode.BlackAndWhite => 4,
                _ => 1
            };

            int requestedDpi = (int)options.Resolution;
            TrySet(_item.Properties, CurrentIntent, intent);
            TrySet(_item.Properties, HorizontalResolution, requestedDpi);
            TrySet(_item.Properties, VerticalResolution, requestedDpi);

            EffectiveDpi = ReadInt(_item.Properties, HorizontalResolution) ?? requestedDpi;
            if (EffectiveDpi != requestedDpi)
            {
                FileLogger.LogInfo(
                    $"WIA: {requestedDpi} DPI istendi, cihaz {EffectiveDpi} DPI kullanıyor ({scanner.Name}).");
            }
        }

        public void TransferNextPage(string filePath)
        {
            if (_item == null)
                throw new InvalidOperationException("Tarayıcı oturumu açık değil.");

            dynamic? image = null;
            try
            {
                image = _item.Transfer(FormatPng);
                if (image == null)
                    throw new COMException("Tarayıcı görüntü döndürmedi.", unchecked((int)WiaErrors.PaperEmpty));

                if (File.Exists(filePath))
                    File.Delete(filePath);

                image.SaveFile(filePath);

                try
                {
                    int dpi = (int)Math.Round((double)image.HorizontalResolution);
                    if (dpi >= 50 && dpi <= 2400)
                        EffectiveDpi = dpi;
                }
                catch
                {
                    // Görüntü DPI bildirmiyorsa öğe özelliğinden okunan değer geçerli kalır.
                }
            }
            finally
            {
                Release(image);
            }
        }

        public void Dispose()
        {
            Release(_item);
            Release(_device);
            Release(_manager);
            _item = null;
            _device = null;
            _manager = null;
        }

        internal static dynamic CreateDeviceManager()
        {
            Type? type = Type.GetTypeFromProgID("WIA.DeviceManager");
            if (type == null)
                throw new ScanFailedException("Windows WIA (Windows Image Acquisition) servisi sistemde bulunamadı.");

            return Activator.CreateInstance(type)
                   ?? throw new ScanFailedException("WIA DeviceManager başlatılamadı.");
        }

        private ScanSource SelectDocumentSource(ScanSource requested)
        {
            ScanSource source = requested;
            int? capabilities = ReadInt(_device!.Properties, DocumentHandlingCapabilities);

            if (capabilities is int caps && caps != 0)
            {
                bool hasFeeder = (caps & Feeder) != 0;
                bool hasFlatbed = (caps & Flatbed) != 0;
                bool hasDuplex = (caps & Duplex) != 0;

                if (source != ScanSource.Flatbed && !hasFeeder && hasFlatbed)
                {
                    source = ScanSource.Flatbed;
                    Notice = "Bu tarayıcıda otomatik besleyici (ADF) yok; belge camdan tarandı.";
                }
                else if (source == ScanSource.Flatbed && !hasFlatbed && hasFeeder)
                {
                    source = ScanSource.FeederSingle;
                    Notice = "Bu tarayıcıda cam (düz yatak) yok; belge besleyiciden tarandı.";
                }
                else if (source == ScanSource.FeederDuplex && !hasDuplex)
                {
                    source = ScanSource.FeederSingle;
                    Notice = "Bu tarayıcı çift yüz taramayı desteklemiyor; tek yüz tarandı.";
                }
            }

            int select = source switch
            {
                ScanSource.FeederSingle => Feeder,
                ScanSource.FeederDuplex => Feeder | Duplex,
                _ => Flatbed
            };

            TrySet(_device.Properties, DocumentHandlingSelect, select);

            if (source == ScanSource.Flatbed)
                return source;

            // Besleyici gerçekten seçilmediyse (özellik yok ya da reddedildi) cam gibi davran:
            // aksi halde döngü "kağıt bitti" hatası hiç gelmeyeceği için aynı camı sonsuza dek tarar.
            int? selected = ReadInt(_device.Properties, DocumentHandlingSelect);
            if (selected is int value && (value & Feeder) != 0)
                return (value & Duplex) != 0 ? ScanSource.FeederDuplex : ScanSource.FeederSingle;

            Notice ??= "Tarayıcı besleyici seçimini kabul etmedi; tek sayfa tarandı.";
            return ScanSource.Flatbed;
        }

        /// <summary>
        /// Seçilen cihazı WIA listesinde bulur. Önce birebir kimlik, sonra ad eşleşmesi aranır;
        /// eşleşme yoksa ve seçilen aygıt bir tarayıcıysa sistemdeki ilk WIA tarayıcısına düşülür.
        /// </summary>
        private static dynamic FindDevice(dynamic manager, DeviceInfo scanner)
        {
            dynamic deviceInfos = manager.DeviceInfos;
            int count = (int)deviceInfos.Count;

            var candidates = new List<(dynamic Info, string Id, string Name)>();
            for (int i = 1; i <= count; i++)
            {
                dynamic info = deviceInfos[i];
                try
                {
                    if ((int)info.Type != ScannerDeviceType)
                        continue;

                    string id = (string)info.DeviceID;
                    string name = Convert.ToString(info.Properties["Name"].Value) ?? string.Empty;
                    candidates.Add((info, id, name));
                }
                catch (Exception ex)
                {
                    FileLogger.LogError("WIA cihaz bilgisi okunamadı", ex);
                }
            }

            if (candidates.Count == 0)
            {
                throw new ScanFailedException(
                    "Windows'ta kayıtlı hiçbir WIA tarayıcı bulunamadı.\n\n" +
                    $"'{scanner.Name}' ile tarama yapabilmek için:\n" +
                    "1. Tarayıcının açık ve USB / ağ kablosunun takılı olduğunu kontrol edin.\n" +
                    "2. Cihazın WIA sürücüsünün (ör. PaperStream IP WIA) kurulu olduğundan emin olun.\n" +
                    "3. Windows 'Yazıcılar ve tarayıcılar' listesinde cihazın göründüğünü doğrulayın.");
            }

            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrEmpty(scanner.SerialOrHardwareId) &&
                    string.Equals(candidate.Id, scanner.SerialOrHardwareId, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate.Info;
                }
            }

            foreach (var candidate in candidates)
            {
                if (NameMatches(candidate.Name, scanner))
                    return candidate.Info;
            }

            if (scanner.Type == DeviceType.Scanner || candidates.Count == 1)
            {
                FileLogger.LogInfo(
                    $"WIA: '{scanner.Name}' adla eşleşmedi, '{candidates[0].Name}' kullanılıyor.");
                return candidates[0].Info;
            }

            var names = new List<string>();
            foreach (var candidate in candidates)
                names.Add(candidate.Name);

            throw new ScanFailedException(
                $"'{scanner.Name}' bir WIA tarayıcısı olarak tanınamadı.\n\n" +
                $"Sistemdeki tarayıcılar:\n• {string.Join("\n• ", names)}\n\n" +
                "Lütfen listeden bir tarayıcı seçin.");
        }

        private static bool NameMatches(string wiaName, DeviceInfo scanner)
        {
            if (string.IsNullOrWhiteSpace(wiaName))
                return false;

            if (!string.IsNullOrWhiteSpace(scanner.Name) &&
                (wiaName.Contains(scanner.Name, StringComparison.OrdinalIgnoreCase) ||
                 scanner.Name.Contains(wiaName, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(scanner.ModelName) &&
                wiaName.Contains(scanner.ModelName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (scanner.IsFujitsuSpecial &&
                (wiaName.Contains("fi-", StringComparison.OrdinalIgnoreCase) ||
                 wiaName.Contains("Fujitsu", StringComparison.OrdinalIgnoreCase) ||
                 wiaName.Contains("PFU", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return scanner.IsRicohSpecial &&
                   (wiaName.Contains("4510", StringComparison.OrdinalIgnoreCase) ||
                    wiaName.Contains("Ricoh", StringComparison.OrdinalIgnoreCase));
        }

        private static bool TrySet(dynamic properties, string propertyId, int value)
        {
            try
            {
                dynamic property = properties[propertyId];
                if (property == null)
                    return false;

                property.Value = value;
                return true;
            }
            catch
            {
                // Özellik salt okunur, desteklenmiyor ya da değer aralık dışında.
                return false;
            }
        }

        private static int? ReadInt(dynamic properties, string propertyId)
        {
            try
            {
                dynamic property = properties[propertyId];
                if (property == null)
                    return null;

                return Convert.ToInt32(property.Value);
            }
            catch
            {
                return null;
            }
        }

        internal static void Release(object? comObject)
        {
            try
            {
                if (comObject != null && Marshal.IsComObject(comObject))
                    Marshal.FinalReleaseComObject(comObject);
            }
            catch
            {
                // Zaten serbest bırakılmış.
            }
        }
    }
}
