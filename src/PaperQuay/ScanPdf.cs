using System.Drawing.Imaging;
using System.Text;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;

namespace PaperQuay;

internal static class ScanPdf
{
    public static bool CompressPageImage(PdfPage page, string imagePath, long? quality = null)
    {
        var images = page.Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/XObject");
        if (images?.Elements.Keys.Count != 1 || imagePath.Length == 0) return false;
        var image = images.Elements.GetDictionary(images.Elements.Keys.Single());
        if (image?.Stream is null || image.Elements.GetName("/Subtype") != "/Image" ||
            image.Elements.GetName("/ColorSpace") != "/DeviceRGB" ||
            image.Elements.GetName("/Filter") != "/FlateDecode" ||
            image.Elements.GetInteger("/BitsPerComponent") != 8 ||
            image.Elements.ContainsKey("/Mask") || image.Elements.ContainsKey("/SMask") ||
            image.Elements.ContainsKey("/Decode"))
            return false;

        using var bitmap = new Bitmap(imagePath);
        if (bitmap.Width != image.Elements.GetInteger("/Width") ||
            bitmap.Height != image.Elements.GetInteger("/Height"))
            return false;
        var jpeg = EncodeJpeg(bitmap, quality ?? new PaperCorrection(bitmap).PdfQuality);
        if (jpeg.Length >= image.Stream.Length) return false;

        image.Stream.Value = jpeg;
        image.Elements.SetInteger("/Length", jpeg.Length);
        image.Elements.SetName("/Filter", "/DCTDecode");
        image.Elements.Remove("/DecodeParms");
        return true;
    }

    public static void WriteImageOnly(string imagePath, string pdfPath, int dpi, long? quality = null)
    {
        using var bitmap = new Bitmap(imagePath);
        var jpeg = EncodeJpeg(bitmap, quality ?? new PaperCorrection(bitmap).PdfQuality);
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Width = XUnit.FromPoint(bitmap.Width * 72d / dpi);
        page.Height = XUnit.FromPoint(bitmap.Height * 72d / dpi);
        var image = new PdfDictionary(document);
        image.Elements.SetName("/Type", "/XObject");
        image.Elements.SetName("/Subtype", "/Image");
        image.Elements.SetName("/ColorSpace", "/DeviceRGB");
        image.Elements.SetName("/Filter", "/DCTDecode");
        image.Elements.SetInteger("/BitsPerComponent", 8);
        image.Elements.SetInteger("/Width", bitmap.Width);
        image.Elements.SetInteger("/Height", bitmap.Height);
        image.CreateStream(jpeg);
        document.Internals.AddObject(image);
        var xObjects = new PdfDictionary(document);
        xObjects.Elements.SetReference("/Im0", image);
        page.Resources.Elements.SetObject("/XObject", xObjects);
        var placement = FormattableString.Invariant($"q {page.Width.Point:0.####} 0 0 {page.Height.Point:0.####} 0 0 cm /Im0 Do Q");
        page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(placement));
        document.Save(pdfPath);
    }

    private static byte[] EncodeJpeg(Bitmap bitmap, long quality)
    {
        using var rgb = bitmap.PixelFormat == PixelFormat.Format24bppRgb ? null : PaperCorrection.CopyToRgb(bitmap);
        using var stream = new MemoryStream();
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        (rgb ?? bitmap).Save(stream, ImageCodecInfo.GetImageEncoders().Single(codec => codec.FormatID == ImageFormat.Jpeg.Guid),
            parameters);
        return stream.ToArray();
    }
}
