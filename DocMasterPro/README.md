# DocMaster Pro

WPF + .NET 8 tabanlı sürükle-bırak PDF birleştirme ve görüntü dönüştürme masaüstü uygulaması.

## Özellikler

- PDF birleştirme (çoklu dosya)
- JPG / PNG görüntüleri PDF'e dönüştürme
- PDF bölme (sayfa aralığı ile)
- Sürükle-bırak arayüzü
- Dosya seçme dialogu
- Çıktı yolu seçimi
- İlerleme çubuğu
- Durum renk göstergesi
- Geçici dosya otomatik temizliği
- Inno Setup yükleyici

## Ön Koşullar

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows 10 veya üzeri
- (Yükleyici için) [Inno Setup 6](https://jrsoftware.org/isinfo.php)

## Geliştirme

```bash
cd DocMasterPro/desktop-app
dotnet restore
dotnet run
```

## Release Derlemesi

```bash
dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true
```

Çıktı: `bin/Release/net8.0-windows/win-x64/publish/DocConverter.exe`

## Yükleyici Oluşturma

1. Inno Setup'ı kurun.
2. `Assets/app.ico` dosyasını oluşturun.
3. Inno Setup IDE'de `installer/setup.iss` dosyasını açıp derleyin.
4. Çıktı: `installer/Output/DocMasterProSetup.exe`

## Teknoloji

| Bileşen | Teknoloji |
|---|---|
| Platform | Windows 10/11 |
| Framework | .NET 8 (WPF) |
| Mimari | MVVM (CommunityToolkit.Mvvm) |
| PDF İşleme | PDFsharp 1.51 |
| Görüntü İşleme | Magick.NET-Q8-AnyCPU 13.7 |
| Yükleyici | Inno Setup 6 |

## Bilinen Kısıtlamalar

- PDFsharp 1.51 şifreli PDF dosyalarını açamaz; bu dosyalar "Error" durumuna düşer ve atlanır.
- Magick.NET büyük görüntülerde (>50 MP) bellek yoğun çalışabilir.
- Inno Setup `[Run]` bölümündeki .NET Runtime kontrolü basit kayıt defteri tabanlıdır.
