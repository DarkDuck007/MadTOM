using System.Text.Json;
using MADTOM.Plugins.SlideForge.Services;
using MADTOM.Plugins.SlideForge.ViewModels;

namespace MADTOM.Tests.Plugins.SlideForge;

public class SfFilePreviewTests
{
    [Theory]
    [InlineData("output/deck.pptx", true)]
    [InlineData("output/report.docx", true)]
    [InlineData("output/sheet.xlsx", true)]
    [InlineData("output/slides.odp", true)]
    [InlineData("output/file.pdf", true)]
    [InlineData("output/pic.png", true)]
    [InlineData("output/data.csv", false)]
    [InlineData("output/readme.md", false)]
    [InlineData("output/blob.bin", false)]
    public void Job_preview_extensions_cover_office_pdf_and_images(string path, bool previewable)
    {
        Assert.Equal(previewable, SfJsonHelpers.IsJobPreviewExt(path));
    }

    [Theory]
    [InlineData("a.md")]
    [InlineData("a.csv")]
    [InlineData("a.json")]
    [InlineData("a.html")]
    [InlineData("a.txt")]
    [InlineData("a.svg")]
    public void Text_like_files_are_read_as_text(string name)
    {
        var item = new SfJobFileItem(null, "output/" + name, name, "file", 10, "output");
        Assert.True(item.IsText);
        Assert.False(item.IsPreviewable);
    }

    [Fact]
    public void Pretty_print_keeps_persian_text_unescaped()
    {
        using var doc = JsonDocument.Parse("{\"title\":\"\\u0645\\u0637\\u0627\\u0644\\u0639\\u0647\",\"x\":\"<b>&\"}");
        string text = SfJsonHelpers.PrettyPrint(doc.RootElement);
        Assert.Contains("مطالعه", text);
        Assert.DoesNotContain("\\u0645", text);
    }

    private static (FakeSlideForgeClient fake, SfJobsViewModel vm) Make()
    {
        var fake = new FakeSlideForgeClient();
        fake.Handlers["list_jobs"] = _ => new
        {
            total = 1, limit = 50, offset = 0,
            items = new[] { new { job_id = "J-00000001", user_id = 7, username = "u", status = "completed", output_format = "pptx",
                origin = "guided", mode = "generate", title = "t", created_at = "2026-11-24T10:00:00+00:00" } },
        };
        fake.Handlers["get_job"] = _ => new { job = new { job_id = "J-00000001" }, spec = new { title = "x" }, events = new object[0], questions = new object[0], files = new object[0] };
        fake.Handlers["list_job_files"] = _ => new
        {
            items = new object[]
            {
                new { id = (long?)null, path = "output/deck.pptx", name = "deck.pptx", role = "output", size = 1, kind = "pptx", mtime = 1L },
                new { id = (long?)null, path = "output/blob.xyz", name = "blob.xyz", role = "output", size = 1, kind = "xyz", mtime = 1L },
            },
        };
        return (fake, new SfJobsViewModel(new SfRpc(fake, "node-a")));
    }

    [Fact]
    public async Task Office_file_requests_preview_mode_and_reports_unsupported_server()
    {
        var (fake, vm) = Make();
        fake.Errors["get_job_file"] = "not_supported";
        await vm.LoadAsync();
        await vm.SelectAsync(vm.Jobs[0]);
        await vm.InspectFileAsync(vm.Detail!.Files[0]);
        Assert.Equal("preview", (string)fake.LastPayload("get_job_file")!["mode"]!);
        Assert.Contains("No page preview", vm.FileViewerNotice);
    }

    [Fact]
    public async Task Unknown_extension_falls_back_to_text_read_and_shows_binary_notice()
    {
        var (fake, vm) = Make();
        fake.Handlers["get_job_file"] = _ => new { name = "blob.xyz", binary = true, truncated = false, size = 1, text = "" };
        await vm.LoadAsync();
        await vm.SelectAsync(vm.Jobs[0]);
        await vm.InspectFileAsync(vm.Detail!.Files[1]);
        Assert.Equal("text", (string)fake.LastPayload("get_job_file")!["mode"]!);
        Assert.Contains("Binary file", vm.FileViewerNotice);
    }
}
