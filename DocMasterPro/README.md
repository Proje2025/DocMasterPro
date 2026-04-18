# DocMaster Pro

WPF + .NET 8 tabanlı sürükle-bırak PDF birleştirme, bölme, düzenleme ve dosya dönüştürme masaüstü uygulaması.

## Özellikler

- PDF birleştirme ve sayfa aralığına göre PDF bölme
- JPG / PNG / BMP / GIF / TIFF / WEBP görüntülerini PDF'e dönüştürme
- PDF sayfalarını görüntü olarak dışa aktarma
- Word, Excel, PowerPoint ve metin dosyalarını PDF'e dönüştürme
- Basit PDF düzenleme: sayfa silme, döndürme, sıralama, çıkarma ve filigran
- Sürükle-bırak arayüzü, dosya seçme dialogları, ilerleme çubuğu ve durum göstergeleri
- Geçici dosya temizliği ve Inno Setup yükleyici betiği

## Ön Koşullar

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows 10 veya üzeri
- Office dosyalarını PDF'e çevirmek için Microsoft Office
- PDF sayfalarını görüntüye çevirmek ve PDF önizleme üretmek için Ghostscript
- Yükleyici oluşturmak için [Inno Setup 6](https://jrsoftware.org/isinfo.php)

## Geliştirme

```bash
cd DocMasterPro
dotnet restore DocMasterPro.sln
dotnet build DocMasterPro.sln
dotnet test DocMasterPro.sln
dotnet run --project desktop-app/DocConverter.csproj
```

## Release Derlemesi

```bash
cd DocMasterPro/desktop-app
dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true
```

Çıktı: `bin/Release/net8.0-windows/win-x64/publish/DocConverter.exe`

## Yükleyici Oluşturma

1. Inno Setup'ı kurun.
2. `Assets/app.ico` dosyasının bulunduğunu doğrulayın.
3. Inno Setup IDE'de `installer/setup.iss` dosyasını açıp derleyin.
4. Çıktı: `installer/Output/DocMasterProSetup.exe`

## Teknoloji

| Bileşen | Teknoloji |
|---|---|
| Platform | Windows 10/11 |
| Framework | .NET 8 (WPF) |
| Mimari | MVVM (CommunityToolkit.Mvvm) |
| PDF İşleme | PDFsharp 6.2.4 |
| Görüntü İşleme | Magick.NET-Q8-AnyCPU 14.12.0 + ImageSharp 3.1.12 |
| Office Dönüşümü | Microsoft Office Interop |
| Test | xUnit + FluentAssertions |
| Yükleyici | Inno Setup 6 |

## Bilinen Kısıtlamalar

- Şifreli veya bozuk PDF dosyaları işlenemeyebilir; bu dosyalar hata olarak loglanır.
- PDF'ten görüntü üretmek için Ghostscript gereklidir ve PATH üzerinden erişilebilir olmalıdır.
- Office dönüşümleri yerel Microsoft Office kurulumuna ve COM otomasyonuna bağlıdır.
- Büyük görüntüler ve çok sayfalı PDF dosyaları bellek kullanımını artırabilir.
