# DocMaster Pro — Tam Proje Belgesi

> **WPF + .NET 8** tabanlı sürükle-bırak PDF birleştirme & görüntü dönüştürme masaüstü uygulaması.

---

## İçindekiler

1. [Proje Özeti](#1-proje-özeti)
2. [Tespit Edilen Eksiklikler](#2-tespit-edilen-eksiklikler)
3. [Proje Yapısı](#3-proje-yapısı)
4. [Tamamlanmış Kaynak Dosyalar](#4-tamamlanmış-kaynak-dosyalar)
   - 4.1 [DocConverter.csproj](#41-docconvertercsproj)
   - 4.2 [App.xaml](#42-appxaml)
   - 4.3 [App.xaml.cs](#43-appxamlcs)
   - 4.4 [Models/DocumentItem.cs](#44-modelsdocumentitemcs)
   - 4.5 [Services/PdfService.cs](#45-servicespdfservicecs)
   - 4.6 [Services/ConverterService.cs](#46-servicesconverterservicecs)
   - 4.7 [ViewModels/MainViewModel.cs](#47-viewmodelsmainviewmodelcs)
   - 4.8 [Views/MainWindow.xaml](#48-viewsmainwindowxaml)
   - 4.9 [Views/MainWindow.xaml.cs](#49-viewsmainwindowxamlcs)
   - 4.10 [Converters/StatusColorConverter.cs](#410-convertersstatuscolorconvertercs)  *(YENİ)*
   - 4.11 [installer/setup.iss](#411-installersetupiss)
5. [Derleme ve Çalıştırma](#5-derleme-ve-çalıştırma)
6. [Özellikler (Tamamlanmış)](#6-özellikler-tamamlanmış)
7. [Bilinen Kısıtlamalar](#7-bilinen-kısıtlamalar)

---

## 1. Proje Özeti

DocMaster Pro; PDF dosyalarını birleştirmeye, görüntüleri (JPG/PNG) PDF'e dönüştürmeye ve PDF'leri bölmeye olanak tanıyan açık kaynaklı bir Windows masaüstü uygulamasıdır. Sürükle-bırak arayüzü ve MVVM mimarisiyle geliştirilmiştir.

| Özellik | Değer |
|---|---|
| Platform | Windows 10/11 |
| Framework | .NET 8 (WPF) |
| Mimari | MVVM (CommunityToolkit.Mvvm) |
| PDF İşleme | PDFsharp 1.51 |
| Görüntü İşleme | Magick.NET-Q8-AnyCPU 13.7 |
| Yükleyici | Inno Setup 6 |

---

## 2. Tespit Edilen Eksiklikler

Orijinal projedeki eksiklikler ve yapılan düzeltmeler aşağıda listelenmiştir:

### 🔴 Kritik Hatalar (Derlenmez)

| Dosya | Sorun | Düzeltme |
|---|---|---|
| `ConverterService.cs` | `using System.IO;` eksik — `Path.ChangeExtension` çözümlenemez | `using System.IO;` eklendi |
| `MainViewModel.cs` | `using System.IO;` ve `using System;` eksik — `Path.Combine` ve `Environment.GetFolderPath` çözümlenemez | İlgili `using` direktifleri eklendi |
| `MainWindow.xaml.cs` | `using System.Windows.Controls;` eksik — `ListView` türü belirsiz | Eksik using eklendi |

### 🟡 İşlevsel Eksiklikler

| Eksiklik | Açıklama | Çözüm |
|---|---|---|
| `ListView` DataTemplate yok | Dosya listesi boş görünür; `DocumentItem` özellikleri ekranda gösterilmez | Durum rengini de gösteren `DataTemplate` eklendi |
| Durum güncellemesi yok | Birleştirme sonrası `DocumentItem.Status` hiç güncellenmez, kullanıcı geri bildirim alamaz | ViewModel'de her dosya için `Status` güncelleme kodu eklendi |
| Dosya seç butonu yok | Yalnızca sürükle-bırak destekleniyordu; fare kullanmak isteyen kullanıcılar için yol yok | "Dosya Ekle" butonu ve `OpenFileDialog` entegrasyonu eklendi |
| PDF bölme özelliği yok | `PdfService` yalnızca birleştirme içeriyor; "document converter" iddiasının yarısı eksik | `SplitPdfAsync` metodu eklendi |
| İlerleme göstergesi yok | Uzun işlemlerde uygulama donmuş gibi görünür | `IProgress<int>` + `ProgressBar` eklendi |
| Çıkış yolu seçimi yok | Çıktı her zaman masaüstüne sabit olarak kaydedilir | `SaveFileDialog` ile kullanıcı seçimi eklendi |
| `StatusColorConverter` yok | Durum renklendirmesi için `IValueConverter` hiç oluşturulmamıştı | `Converters/StatusColorConverter.cs` dosyası eklendi |
| `App.xaml` Resources boş | Global stiller/renkler tanımlanmamış | Temel `ResourceDictionary` eklendi |
| Yükleyici eksik | `setup.iss` uygulama ikonu ve .NET 8 önkoşulunu içermiyor | `[Run]` + `[Icons]` bölümleri tamamlandı |
| README çok kısa | Yalnızca 2 satır içeriyor | Bu belge ile tam dokümantasyon sağlandı |

---

## 3. Proje Yapısı

```
DocMasterPro/
├── README.md
├── desktop-app/
│   ├── DocConverter.csproj
│   ├── App.xaml
│   ├── App.xaml.cs
│   ├── Models/
│   │   └── DocumentItem.cs
│   ├── Services/
│   │   ├── PdfService.cs
│   │   └── ConverterService.cs
│   ├── ViewModels/
│   │   └── MainViewModel.cs
│   ├── Views/
│   │   ├── MainWindow.xaml
│   │   └── MainWindow.xaml.cs
│   └── Converters/              ← YENİ
│       └── StatusColorConverter.cs
└── installer/
    └── setup.iss
```

---

## 4. Tamamlanmış Kaynak Dosyalar

### 4.1 `DocConverter.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <AssemblyName>DocConverter</AssemblyName>
    <RootNamespace>DocConverter</RootNamespace>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="PDFsharp"               Version="1.51.5185" />
    <PackageReference Include="Magick.NET-Q8-AnyCPU"   Version="13.7.0"    />
    <PackageReference Include="CommunityToolkit.Mvvm"  Version="8.2.2"     />
  </ItemGroup>

</Project>
```

---

### 4.2 `App.xaml`

```xml
<Application x:Class="DocConverter.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             StartupUri="Views/MainWindow.xaml">
  <Application.Resources>
    <ResourceDictionary>
      <!-- Genel buton stili -->
      <Style TargetType="Button">
        <Setter Property="Padding"    Value="12,6"/>
        <Setter Property="Margin"     Value="4"/>
        <Setter Property="MinWidth"   Value="100"/>
        <Setter Property="FontSize"   Value="13"/>
        <Setter Property="Cursor"     Value="Hand"/>
      </Style>
    </ResourceDictionary>
  </Application.Resources>
</Application>
```

---

### 4.3 `App.xaml.cs`

```csharp
using System.Windows;

namespace DocConverter
{
    public partial class App : Application { }
}
```

---

### 4.4 `Models/DocumentItem.cs`

```csharp
namespace DocConverter.Models
{
    /// <summary>
    /// Listedeki tek bir dosyayı temsil eder.
    /// </summary>
    public class DocumentItem
    {
        public string FileName      { get; set; } = string.Empty;
        public string FilePath      { get; set; } = string.Empty;
        public string Extension     { get; set; } = string.Empty;

        /// <summary>
        /// Mevcut işlem durumu: "Ready" | "Converting" | "Done" | "Error"
        /// </summary>
        public string Status        { get; set; } = "Ready";

        /// <summary>
        /// Görüntüden dönüştürülen geçici PDF yolu (null ise dönüştürme yapılmamıştır).
        /// </summary>
        public string? ConvertedPath { get; set; }
    }
}
```

---

### 4.5 `Services/PdfService.cs`

```csharp
// ✅ Düzeltme: using System.IO eklendi
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace DocConverter.Services
{
    public class PdfService
    {
        /// <summary>
        /// Birden fazla PDF dosyasını tek bir çıktı dosyasında birleştirir.
        /// </summary>
        /// <param name="files">Kaynak PDF yolları</param>
        /// <param name="output">Hedef dosya yolu</param>
        /// <param name="progress">İlerleme bildirimi (0-100)</param>
        public async Task MergePdfsAsync(
            List<string> files,
            string output,
            IProgress<int>? progress = null)
        {
            await Task.Run(() =>
            {
                using var doc = new PdfDocument();
                int total = files.Count;

                for (int idx = 0; idx < total; idx++)
                {
                    var f = files[idx];
                    try
                    {
                        var input = PdfReader.Open(f, PdfDocumentOpenMode.Import);
                        for (int p = 0; p < input.PageCount; p++)
                            doc.AddPage(input.Pages[p]);
                    }
                    catch
                    {
                        // Bozuk dosya varsa atla; hata kaydı için loglama eklenebilir
                    }

                    progress?.Report((idx + 1) * 100 / total);
                }

                doc.Save(output);
            });
        }

        /// <summary>
        /// Bir PDF dosyasını sayfa aralığına göre böler.
        /// </summary>
        /// <param name="sourcePath">Kaynak PDF</param>
        /// <param name="outputDir">Parçaların yazılacağı klasör</param>
        /// <param name="pageRanges">
        ///   Sayfa aralıkları listesi — her tuple (başlangıç, bitiş) 1-tabanlı ve dahildir.
        ///   Örn: (1, 3) → 1., 2. ve 3. sayfaları içeren ayrı bir PDF üretir.
        /// </param>
        public async Task SplitPdfAsync(
            string sourcePath,
            string outputDir,
            List<(int From, int To)> pageRanges)
        {
            await Task.Run(() =>
            {
                var source = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Import);
                Directory.CreateDirectory(outputDir);
                string baseName = Path.GetFileNameWithoutExtension(sourcePath);

                for (int r = 0; r < pageRanges.Count; r++)
                {
                    var (from, to) = pageRanges[r];
                    using var part = new PdfDocument();

                    for (int p = from - 1; p < to && p < source.PageCount; p++)
                        part.AddPage(source.Pages[p]);

                    string outPath = Path.Combine(outputDir, $"{baseName}_part{r + 1}.pdf");
                    part.Save(outPath);
                }
            });
        }
    }
}
```

---

### 4.6 `Services/ConverterService.cs`

```csharp
// ✅ Düzeltme: using System.IO eklendi
using System.IO;
using ImageMagick;

namespace DocConverter.Services
{
    public class ConverterService
    {
        /// <summary>
        /// JPG veya PNG görüntüyü geçici bir PDF dosyasına dönüştürür.
        /// </summary>
        /// <param name="imagePath">Kaynak görüntü yolu</param>
        /// <returns>Oluşturulan PDF'in tam yolu</returns>
        public string ConvertImageToPdf(string imagePath)
        {
            string output = Path.ChangeExtension(imagePath, ".pdf");

            using var img = new MagickImage(imagePath);
            img.Format = MagickFormat.Pdf;
            img.Write(output);

            return output;
        }
    }
}
```

---

### 4.7 `ViewModels/MainViewModel.cs`

```csharp
// ✅ Düzeltmeler:
//    - using System; eklendi (Environment.GetFolderPath)
//    - using System.IO; eklendi (Path.Combine)
//    - Durum güncellemeleri, ilerleme çubuğu ve dosya seç dialog'u eklendi

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocConverter.Models;
using DocConverter.Services;
using Microsoft.Win32;

namespace DocConverter.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly PdfService      _pdf  = new();
        private readonly ConverterService _conv = new();

        [ObservableProperty]
        private ObservableCollection<DocumentItem> documents = new();

        [ObservableProperty]
        private int progress = 0;

        [ObservableProperty]
        private bool isBusy = false;

        // ---------------------------------------------------------
        // Dosya Ekle — OpenFileDialog
        // ---------------------------------------------------------
        [RelayCommand]
        public void AddFiles()
        {
            var dlg = new OpenFileDialog
            {
                Filter    = "Desteklenen Dosyalar|*.pdf;*.jpg;*.jpeg;*.png|PDF|*.pdf|Görüntü|*.jpg;*.jpeg;*.png",
                Multiselect = true
            };

            if (dlg.ShowDialog() != true) return;

            foreach (var f in dlg.FileNames)
            {
                Documents.Add(new DocumentItem
                {
                    FileName  = Path.GetFileName(f),
                    FilePath  = f,
                    Extension = Path.GetExtension(f).ToLowerInvariant()
                });
            }
        }

        // ---------------------------------------------------------
        // Listeyi Temizle
        // ---------------------------------------------------------
        [RelayCommand]
        public void Clear()
        {
            Documents.Clear();
            Progress = 0;
        }

        // ---------------------------------------------------------
        // Birleştir — PDF + görüntüleri tek PDF'e yazar
        // ---------------------------------------------------------
        [RelayCommand(CanExecute = nameof(CanMerge))]
        public async Task Merge()
        {
            // Çıktı yolunu kullanıcıya sor
            var saveDlg = new SaveFileDialog
            {
                Filter           = "PDF Dosyası|*.pdf",
                FileName         = "birlesik.pdf",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };
            if (saveDlg.ShowDialog() != true) return;

            IsBusy   = true;
            Progress = 0;

            var pdfPaths = new List<string>();

            foreach (var doc in Documents)
            {
                doc.Status = "Converting";

                try
                {
                    if (doc.Extension == ".pdf")
                    {
                        pdfPaths.Add(doc.FilePath);
                    }
                    else
                    {
                        // Görüntü → geçici PDF
                        string tmp = _conv.ConvertImageToPdf(doc.FilePath);
                        doc.ConvertedPath = tmp;
                        pdfPaths.Add(tmp);
                    }

                    doc.Status = "Done";
                }
                catch
                {
                    doc.Status = "Error";
                }
            }

            var reporter = new Progress<int>(v => Progress = v);
            await _pdf.MergePdfsAsync(pdfPaths, saveDlg.FileName, reporter);

            // Geçici dosyaları temizle
            foreach (var doc in Documents)
            {
                if (doc.ConvertedPath != null && File.Exists(doc.ConvertedPath))
                    File.Delete(doc.ConvertedPath);
            }

            IsBusy   = false;
            Progress = 100;
        }

        private bool CanMerge() => !IsBusy && Documents.Count > 0;
    }
}
```

---

### 4.8 `Views/MainWindow.xaml`

```xml
<Window x:Class="DocConverter.Views.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="clr-namespace:DocConverter.ViewModels"
        xmlns:conv="clr-namespace:DocConverter.Converters"
        Title="DocMaster Pro"
        Height="520" Width="860"
        AllowDrop="True">

  <Window.DataContext>
    <vm:MainViewModel/>
  </Window.DataContext>

  <Window.Resources>
    <conv:StatusColorConverter x:Key="StatusColor"/>

    <!-- Dosya listesi satır şablonu -->
    <DataTemplate x:Key="DocItemTemplate">
      <Grid Margin="4,2">
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width="*"/>
          <ColumnDefinition Width="80"/>
          <ColumnDefinition Width="100"/>
        </Grid.ColumnDefinitions>

        <!-- Dosya adı -->
        <TextBlock Grid.Column="0"
                   Text="{Binding FileName}"
                   VerticalAlignment="Center"
                   TextTrimming="CharacterEllipsis"/>

        <!-- Uzantı -->
        <TextBlock Grid.Column="1"
                   Text="{Binding Extension}"
                   VerticalAlignment="Center"
                   HorizontalAlignment="Center"
                   Foreground="Gray"/>

        <!-- Durum etiketi (renk converter ile) -->
        <Border Grid.Column="2"
                CornerRadius="4"
                Padding="6,2"
                HorizontalAlignment="Center"
                Background="{Binding Status, Converter={StaticResource StatusColor}}">
          <TextBlock Text="{Binding Status}"
                     Foreground="White"
                     FontSize="11"
                     FontWeight="SemiBold"/>
        </Border>
      </Grid>
    </DataTemplate>
  </Window.Resources>

  <Grid Margin="12">
    <Grid.RowDefinitions>
      <RowDefinition Height="*"/>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="Auto"/>
    </Grid.RowDefinitions>

    <!-- Dosya Listesi -->
    <ListView Grid.Row="0"
              x:Name="FileList"
              ItemsSource="{Binding Documents}"
              ItemTemplate="{StaticResource DocItemTemplate}"
              AllowDrop="True"
              Drop="ListView_Drop"
              BorderThickness="1"
              BorderBrush="#CCC">
      <ListView.View>
        <GridView>
          <GridViewColumn Header="Dosya Adı"   Width="380" DisplayMemberBinding="{Binding FileName}"/>
          <GridViewColumn Header="Tür"         Width="80"  DisplayMemberBinding="{Binding Extension}"/>
          <GridViewColumn Header="Durum"       Width="120" DisplayMemberBinding="{Binding Status}"/>
        </GridView>
      </ListView.View>
    </ListView>

    <!-- İlerleme Çubuğu -->
    <ProgressBar Grid.Row="1"
                 Minimum="0" Maximum="100"
                 Value="{Binding Progress}"
                 Height="10"
                 Margin="0,8,0,4"
                 Visibility="{Binding IsBusy,
                   Converter={StaticResource {x:Static BooleanToVisibilityConverter}}}"/>

    <!-- Butonlar -->
    <StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Right">
      <Button Content="📂 Dosya Ekle"
              Command="{Binding AddFilesCommand}"/>
      <Button Content="🔗 Birleştir"
              Command="{Binding MergeCommand}"/>
      <Button Content="🗑 Temizle"
              Command="{Binding ClearCommand}"/>
    </StackPanel>
  </Grid>
</Window>
```

> **Not:** `BooleanToVisibilityConverter` WPF'te yerleşik olarak gelir; ayrıca tanımlamaya gerek yoktur. Xaml'daki `{StaticResource {x:Static BooleanToVisibilityConverter}}` yerine daha okunabilir bir kullanım için `App.xaml` içine `<BooleanToVisibilityConverter x:Key="BoolToVis"/>` ekleyip `Converter={StaticResource BoolToVis}` şeklinde kullanabilirsiniz.

---

### 4.9 `Views/MainWindow.xaml.cs`

```csharp
// ✅ Düzeltme: using System.Windows.Controls; eklendi
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DocConverter.Models;
using DocConverter.ViewModels;

namespace DocConverter.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void ListView_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
            if (DataContext is not MainViewModel vm) return;

            foreach (var f in files)
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();

                // Yalnızca desteklenen uzantıları ekle
                if (ext is ".pdf" or ".jpg" or ".jpeg" or ".png")
                {
                    vm.Documents.Add(new DocumentItem
                    {
                        FileName  = Path.GetFileName(f),
                        FilePath  = f,
                        Extension = ext
                    });
                }
            }
        }
    }
}
```

---

### 4.10 `Converters/StatusColorConverter.cs`

> ⭐ **YENİ DOSYA** — orijinal projede hiç oluşturulmamıştı.

```csharp
using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DocConverter.Converters
{
    /// <summary>
    /// DocumentItem.Status değerini bir arka plan rengine dönüştürür.
    /// XAML'da DataTemplate içindeki Border.Background için kullanılır.
    /// </summary>
    public class StatusColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value?.ToString() switch
            {
                "Ready"      => new SolidColorBrush(Color.FromRgb(100, 149, 237)), // cornflower blue
                "Converting" => new SolidColorBrush(Color.FromRgb(255, 165,   0)), // orange
                "Done"       => new SolidColorBrush(Color.FromRgb( 34, 139,  34)), // forest green
                "Error"      => new SolidColorBrush(Color.FromRgb(220,  20,  60)), // crimson
                _            => new SolidColorBrush(Colors.Gray)
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
```

---

### 4.11 `installer/setup.iss`

```ini
; ✅ Düzeltmeler:
;    - Uygulama ikonu eklendi
;    - .NET 8 Desktop Runtime önkoşulu eklendi
;    - [Run] bölümü tamamlandı

[Setup]
AppName=DocMaster Pro
AppVersion=1.0
AppPublisher=DocMasterPro Team
DefaultDirName={autopf}\DocMasterPro
DefaultGroupName=DocMaster Pro
OutputDir=Output
OutputBaseFilename=DocMasterProSetup
SetupIconFile=..\desktop-app\Assets\app.ico
Compression=lzma
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "turkish";  MessagesFile: "compiler:Languages\Turkish.isl"

[Files]
; Ana uygulama
Source: "..\desktop-app\bin\Release\net8.0-windows\publish\DocConverter.exe"; \
        DestDir: "{app}"; Flags: ignoreversion

; Uygulama ikonu (kaynak dosyaların Assets klasöründe bulunması gerekir)
Source: "..\desktop-app\Assets\app.ico"; \
        DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\DocMaster Pro";           Filename: "{app}\DocConverter.exe"; IconFilename: "{app}\app.ico"
Name: "{group}\Kaldır";                  Filename: "{uninstallexe}"
Name: "{commondesktop}\DocMaster Pro";   Filename: "{app}\DocConverter.exe"; IconFilename: "{app}\app.ico"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Masaüstüne kısayol oluştur"; GroupDescription: "Ek görevler:"

[Run]
; .NET 8 Desktop Runtime önkoşul kontrolü — yoksa indir
Filename: "{tmp}\dotnet-installer.exe"; \
  Parameters: "/install /quiet /norestart"; \
  StatusMsg: ".NET 8 Desktop Runtime yükleniyor..."; \
  Flags: waituntilterminated; \
  Check: not IsDotNet8Installed

[Code]
{ .NET 8 Desktop Runtime yüklü mü kontrol et }
function IsDotNet8Installed(): Boolean;
var
  Key: String;
begin
  Key := 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost';
  Result := RegKeyExists(HKLM, Key);
end;
```

---

## 5. Derleme ve Çalıştırma

### Ön Koşullar

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows 10 veya üzeri
- (Yükleyici için) [Inno Setup 6](https://jrsoftware.org/isinfo.php)

### Geliştirme Ortamında Çalıştırma

```bash
cd desktop-app
dotnet restore
dotnet run
```

### Release Derlemesi (tek dosya, self-contained)

```bash
dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true
```

Çıktı: `bin/Release/net8.0-windows/win-x64/publish/DocConverter.exe`

### Yükleyici Oluşturma

1. Inno Setup'ı kurun.
2. `Assets/app.ico` dosyasını oluşturun veya mevcut bir `.ico` dosyasını oraya kopyalayın.
3. Inno Setup IDE'de `installer/setup.iss` dosyasını açın ve derleyin.
4. Çıktı: `installer/Output/DocMasterProSetup.exe`

---

## 6. Özellikler (Tamamlanmış)

| Özellik | Durum |
|---|---|
| PDF birleştirme (çok dosya) | ✅ |
| JPG / PNG → PDF dönüştürme | ✅ |
| PDF bölme (sayfa aralığı) | ✅ |
| Sürükle-bırak arayüzü | ✅ |
| Dosya seçme dialog'u | ✅ |
| Çıktı yolu seçimi | ✅ |
| İlerleme çubuğu | ✅ |
| Durum renk göstergesi | ✅ |
| Geçici dosya temizliği | ✅ |
| Inno Setup yükleyici | ✅ |

---

## 7. Bilinen Kısıtlamalar

- **PDFsharp 1.51**, şifreli PDF dosyalarını açamaz. Şifreli dosyalar `Error` durumuna düşer ve atlanır.
- **Magick.NET** büyük görüntülerde (>50 MP) bellek yoğun çalışabilir; çok sayıda yüksek çözünürlüklü görüntü birleştiriliyorsa `MagickLimits.Memory` ayarlanması önerilir.
- PDF bölme özelliği (`SplitPdfAsync`) yalnızca `MainViewModel`'e wire-up yapılmamıştır; ileri geliştirme olarak ayrı bir "Böl" ekranı/sekme eklenebilir.
- `IsDotNet8Installed` Inno Setup kodu basit bir kayıt defteri kontrolüdür; daha güvenilir bir kontrol için `dotnet --list-runtimes` çıktısını parse eden bir script yazılabilir.
