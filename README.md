# DocMasterPro

PDF düzenleme ve dönüştürme masaüstü uygulaması.

## Özellikler

- **PDF Birleştirme** - Birden fazla PDF dosyasını tek dosyada birleştirin
- **PDF Bölme** - PDF dosyalarını sayfa aralıklarına göre bölün
- **Görüntüden PDF** - JPG, PNG, BMP, GIF, TIFF, WEBP görüntülerini PDF'e dönüştürün
- **PDF'ten Görüntü** - PDF sayfalarını JPG, PNG, BMP, TIFF görüntülere dönüştürün
- **Office'den PDF** - Word, Excel, PowerPoint dosyalarını PDF'e dönüştürün
- **PDF Düzenleme** - Sayfa silme, döndürme, sıralama, çıkarma
- **Filigran Ekleme** - PDF belgelerine filigran metni ekleme

## Teknoloji

- .NET 8.0
- WPF (Windows Presentation Foundation)
- PdfSharp - PDF işlemleri
- Magick.NET - Görüntü işleme
- CommunityToolkit.Mvvm - MVVM pattern

## Kurulum

1. Projeyi klonlayın
2. Visual Studio 2022 veya .NET 8 SDK yükleyin
3. Projeyi derleyin ve çalıştırın

```bash
git clone https://github.com/aligundogan2025-arch/DocMasterPro.git
cd DocMasterPro/desktop-app
dotnet build
dotnet run
```

## Gereksinimler

- Windows 10/11
- .NET 8.0 Runtime
- Ghostscript (PDF önizleme için önerilir)
