using System.Globalization;
using System.Text;

namespace DemoOcr.Tests;

internal static class TestPdfFactory
{
    public static byte[] Create(params PdfTestPage[] pages)
    {
        if (pages.Length == 0)
        {
            throw new ArgumentException("At least one page is required.", nameof(pages));
        }

        var objects = new List<byte[]>();
        AddAscii("<< /Type /Catalog /Pages 2 0 R >>");

        int firstPageObject = 3;
        int fontObject = firstPageObject + pages.Length * 2;
        int imageObject = fontObject + 1;
        string kids = string.Join(" ", Enumerable.Range(0, pages.Length)
            .Select(index => $"{firstPageObject + index * 2} 0 R"));
        AddAscii($"<< /Type /Pages /Kids [{kids}] /Count {pages.Length} >>");

        foreach ((PdfTestPage page, int index) in pages.Select((page, index) => (page, index)))
        {
            int pageObject = firstPageObject + index * 2;
            int contentObject = pageObject + 1;
            string resources = page.ImageOnly
                ? $"<< /XObject << /Im1 {imageObject} 0 R >> >>"
                : $"<< /Font << /F1 {fontObject} 0 R >> >>";
            AddAscii(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(page.Width)} {F(page.Height)}] " +
                $"/Resources {resources} /Contents {contentObject} 0 R >>");

            string content = page.ImageOnly
                ? "q 72 0 0 72 24 24 cm /Im1 Do Q"
                : string.IsNullOrEmpty(page.Text)
                    ? string.Empty
                    : $"BT /F1 18 Tf 72 {F(page.Height - 72)} Td ({Escape(page.Text)}) Tj ET";
            byte[] contentBytes = Encoding.ASCII.GetBytes(content);
            AddStream($"<< /Length {contentBytes.Length} >>", contentBytes);
        }

        AddAscii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        if (pages.Any(page => page.ImageOnly))
        {
            byte[] pixels = Encoding.ASCII.GetBytes("FF0000>");
            AddStream(
                $"<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB " +
                $"/BitsPerComponent 8 /Filter /ASCIIHexDecode /Length {pixels.Length} >>",
                pixels);
        }

        using var output = new MemoryStream();
        WriteAscii(output, "%PDF-1.4\n");
        var offsets = new List<long> { 0 };
        for (int index = 0; index < objects.Count; index++)
        {
            offsets.Add(output.Position);
            WriteAscii(output, $"{index + 1} 0 obj\n");
            output.Write(objects[index]);
            WriteAscii(output, "\nendobj\n");
        }

        long xref = output.Position;
        WriteAscii(output, $"xref\n0 {objects.Count + 1}\n");
        WriteAscii(output, "0000000000 65535 f \n");
        foreach (long offset in offsets.Skip(1))
        {
            WriteAscii(output, $"{offset:0000000000} 00000 n \n");
        }

        WriteAscii(
            output,
            $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();

        void AddAscii(string value) => objects.Add(Encoding.ASCII.GetBytes(value));

        void AddStream(string dictionary, byte[] bytes)
        {
            using var stream = new MemoryStream();
            WriteAscii(stream, dictionary + "\nstream\n");
            stream.Write(bytes);
            WriteAscii(stream, "\nendstream");
            objects.Add(stream.ToArray());
        }
    }

    private static string F(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Escape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);

    private static void WriteAscii(Stream stream, string value)
        => stream.Write(Encoding.ASCII.GetBytes(value));
}

internal sealed record PdfTestPage(
    string? Text = null,
    bool ImageOnly = false,
    double Width = 300,
    double Height = 400);
