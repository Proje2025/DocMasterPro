using System.Diagnostics;
using DocConverter.Helpers;
using DocConverter.Models;
using DocConverter.Services;
using FluentAssertions;
using Xunit;

namespace DocConverter.Tests;

/// <summary>
/// Gerçek Microsoft Office ile dönüştürme. Uygulama kurulu değilse ilgili test atlanır.
/// Her test, dönüştürmeden sonra arkada fazladan bir Office işlemi kalmadığını da denetler.
/// </summary>
[Collection(OfficeTestEnvironment.Collection)]
public class OfficeAutomationTests : IDisposable
{
    private readonly string _work = OfficeTestEnvironment.NewTempDirectory("office-");

    public void Dispose() => OfficeTestEnvironment.DeleteDirectory(_work);

    [RequiresOfficeFact(OfficeApp.PowerPoint)]
    public async Task PowerPoint_ConvertsAndLeavesNoHiddenProcessBehind()
    {
        int before = Count("POWERPNT");
        string output = Path.Combine(_work, "tanitim.pdf");

        // Eğik çizgili yol: PowerPoint bunu olduğu gibi kabul etmez, servis normalleştirmelidir.
        string input = OfficeTestEnvironment.Sample("tanitim.pptx").Replace('\\', '/');
        await new OfficeConverterService().ConvertToPdfAsync(input, output);

        OfficeTestEnvironment.ReadPdf(output).Pages.Should().Be(3);
        (await SettlesAtAsync("POWERPNT", before)).Should().BeTrue("dönüştürmeden sonra gizli bir POWERPNT.EXE kalmamalı");
    }

    [RequiresOfficeFact(OfficeApp.Word)]
    public async Task Word_ConvertsAndLeavesNoHiddenProcessBehind()
    {
        int before = Count("WINWORD");
        string output = Path.Combine(_work, "sozlesme.pdf");

        await new OfficeConverterService().ConvertToPdfAsync(OfficeTestEnvironment.Sample("sozlesme.docx").Replace('\\', '/'), output);

        (int pages, string text) = OfficeTestEnvironment.ReadPdf(output);
        pages.Should().BeGreaterThanOrEqualTo(2);
        text.Should().Contain("Sözleşmesi");
        (await SettlesAtAsync("WINWORD", before)).Should().BeTrue();
    }

    [RequiresOfficeFact(OfficeApp.Excel)]
    public async Task Excel_ConvertsAndLeavesNoHiddenProcessBehind()
    {
        int before = Count("EXCEL");
        string output = Path.Combine(_work, "satis_raporu.pdf");

        await new OfficeConverterService().ConvertToPdfAsync(OfficeTestEnvironment.Sample("satis_raporu.xlsx").Replace('\\', '/'), output);

        OfficeTestEnvironment.ReadPdf(output).Pages.Should().BeGreaterThanOrEqualTo(1);
        (await SettlesAtAsync("EXCEL", before)).Should().BeTrue();
    }

    [Fact]
    public async Task StaThread_RunsTheWorkOnAnStaThread()
    {
        ApartmentState? state = null;

        await StaThread.RunAsync(() => state = Thread.CurrentThread.GetApartmentState(), "test");

        state.Should().Be(ApartmentState.STA);
    }

    [Fact]
    public async Task StaThread_PropagatesFailuresAndCancellation()
    {
        var failing = () => StaThread.RunAsync(() => throw new InvalidOperationException("olmadı"), "test");
        await failing.Should().ThrowAsync<InvalidOperationException>().WithMessage("olmadı");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        bool ran = false;
        var cancelled = () => StaThread.RunAsync(() => ran = true, "test", cts.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        ran.Should().BeFalse("iptal edilmiş iş hiç başlamamalı");
    }

    private static int Count(string processName)
    {
        Process[] processes = Process.GetProcessesByName(processName);
        foreach (Process process in processes)
            process.Dispose();
        return processes.Length;
    }

    /// <summary>
    /// Sayının eski değerine dönmesini bekler. Office çoğunlukla birkaç saniyede kapanır; tam test turunda Excel'in
    /// kapanmasının 20 saniyeden uzun sürdüğü (sonra kendiliğinden kapandığı) görüldü, nedeni bilinmiyor.
    /// Burada sınanan, işlemin kalıcı olarak açık kalmamasıdır.
    /// </summary>
    private static async Task<bool> SettlesAtAsync(string processName, int expected)
    {
        for (int attempt = 0; attempt < 180; attempt++)
        {
            if (Count(processName) <= expected)
                return true;
            await Task.Delay(500);
        }

        return false;
    }
}
