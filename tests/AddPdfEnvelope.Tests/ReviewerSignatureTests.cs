using System.Diagnostics;
using System.Text.Json;
using AddPdfEnvelope;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using Xunit;
using Path = System.IO.Path;

public class ReviewerSignatureTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingOrBlankReviewerPreservesLegacySignatureArea(string? reviewer)
    {
        var result = await GenerateCover(reviewer);

        Assert.Equal(2, result.PageCount);
        Assert.Equal(new[] { "Author (AR)", "Approver (AR)" }, result.Labels);
        Assert.Equal(new[] { "SIGN-1-END", "SIGN-2-END" }, result.Tags);
        Assert.Equal(80f, result.LabelY[0] - result.LabelY[1], 2);
        Assert.Equal(20f, result.LabelY[0] - result.TagY[0], 2);
        Assert.Equal(20f, result.LabelY[1] - result.TagY[1], 2);

        if (reviewer != null) {
            var legacy = await GenerateCover(null);
            Assert.Equal(legacy.Content, result.Content);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfiguredReviewerAddsExactlyOneSlotBetweenAuthorAndApprover(bool addTags)
    {
        var legacy = await GenerateCover(null, addTags);
        var result = await GenerateCover("QA", addTags);

        Assert.Equal(2, result.PageCount);
        Assert.Equal(new[] { "Author (AR)", "Reviewer (QA)", "Approver (AR)" }, result.Labels);
        Assert.Equal(legacy.LabelY[0], result.LabelY[0]);
        Assert.Equal(160f / 3, result.LabelY[0] - result.LabelY[1], 2);
        Assert.Equal(160f / 3, result.LabelY[1] - result.LabelY[2], 2);
        Assert.Equal(addTags ? new[] { "SIGN-1-END", "SIGN-2-END", "SIGN-3-END" } : Array.Empty<string>(), result.Tags);
        for (var i = 0; i < result.TagY.Length; i++) {
            Assert.Equal(20f, result.LabelY[i] - result.TagY[i], 2);
        }
    }

    [Fact]
    public async Task HiddenSignatureAreaDoesNotRenderReviewerOrTags()
    {
        var result = await GenerateCover("QA", showSignatureArea: false);

        Assert.Empty(result.Labels);
        Assert.Empty(result.Tags);
    }

    private static async Task<CoverResult> GenerateCover(string? reviewer, bool addTags = true, bool showSignatureArea = true)
    {
        var directory = Directory.CreateTempSubdirectory("AddPdfEnvelope-tests-");
        try {
            // Configuration is resolved beside the executable, so isolate its files too.
            var assemblyPath = typeof(Globals).Assembly.Location;
            var binaryDirectory = Path.GetDirectoryName(assemblyPath)!;
            foreach (var source in Directory.EnumerateFiles(binaryDirectory, "*", SearchOption.AllDirectories)) {
                var target = Path.Combine(directory.FullName, Path.GetRelativePath(binaryDirectory, source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }
            var coverSettings = new Dictionary<string, object> {
                ["ShowSignatureArea"] = showSignatureArea,
                ["AddAdobeSignTags"] = addTags,
                ["AdobeSignTagPattern"] = "SIGN-{sigNum}-END"
            };
            if (reviewer != null) {
                coverSettings["RoleReviewer"] = reviewer;
            }
            var configuration = new { PdfEnvelope = new { CoverPage = coverSettings } };
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "appsettings.json"), JsonSerializer.Serialize(configuration));
            var inputPath = Path.Combine(directory.FullName, "input.pdf");
            var outputPath = Path.Combine(directory.FullName, "output.pdf");
            using (var input = new PdfDocument(new PdfWriter(inputPath))) {
                input.AddNewPage(PageSize.A4);
            }

            var startInfo = new ProcessStartInfo("dotnet") {
                WorkingDirectory = directory.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] {
                Path.Combine(directory.FullName, Path.GetFileName(assemblyPath)),
                "-f", inputPath, "-o", outputPath, "-y"
            }) {
                startInfo.ArgumentList.Add(argument);
            }
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start CLI.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) {
                process.Kill(entireProcessTree: true);
                throw;
            }
            Assert.True(process.ExitCode == 0, $"CLI exited {process.ExitCode}:\n{await stdout}\n{await stderr}");

            using var pdf = new PdfDocument(new PdfReader(outputPath));
            var listener = new SignatureTextListener();
            new PdfCanvasProcessor(listener).ProcessPageContent(pdf.GetFirstPage());
            return new CoverResult(
                pdf.GetNumberOfPages(),
                listener.Labels.Select(item => item.Text).ToArray(),
                listener.Labels.Select(item => item.Y).ToArray(),
                listener.Tags.Select(item => item.Text).ToArray(),
                listener.Tags.Select(item => item.Y).ToArray(),
                pdf.GetFirstPage().GetContentBytes());
        }
        finally {
            directory.Delete(recursive: true);
        }
    }

    private sealed record CoverResult(int PageCount, string[] Labels, float[] LabelY, string[] Tags, float[] TagY, byte[] Content);

    private sealed class SignatureTextListener : IEventListener
    {
        public List<(string Text, float Y)> Labels { get; } = [];
        public List<(string Text, float Y)> Tags { get; } = [];

        public void EventOccurred(IEventData data, EventType type)
        {
            if (data is not TextRenderInfo text) {
                return;
            }
            var value = text.GetText();
            var y = text.GetBaseline().GetStartPoint().Get(Vector.I2);
            if (value.StartsWith("Author (") || value.StartsWith("Reviewer (") || value.StartsWith("Approver (")) {
                Labels.Add((value, y));
            }
            if (value.StartsWith("SIGN-")) {
                Tags.Add((value, y));
            }
        }

        public ICollection<EventType> GetSupportedEvents() => new[] { EventType.RENDER_TEXT };
    }
}
