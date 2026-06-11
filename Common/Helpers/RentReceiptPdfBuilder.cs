using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Common.CommunicationModels;
using Common.Enums;

namespace Common.Helpers
{
    public static class RentReceiptPdfBuilder
    {
        private const double PageWidth = 595;
        private const double PageHeight = 842;
        private const double Margin = 54;
        private static readonly Regex ViewBoxRegex = new(@"viewBox=""0 0 (?<w>[\d.]+) (?<h>[\d.]+)""", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex RectRegex = new(@"<rect fill=""#111827"" x=""(?<x>[\d.]+)"" y=""(?<y>[\d.]+)"" width=""(?<w>[\d.]+)"" height=""(?<h>[\d.]+)""", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static byte[] Build(RentReceiptDto receipt)
        {
            var content = BuildContent(receipt);
            var contentBytes = Encoding.ASCII.GetBytes(content);
            var objects = new List<byte[]>
            {
                PdfObject("<< /Type /Catalog /Pages 2 0 R >>"),
                PdfObject("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                PdfObject("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 5 0 R /F2 6 0 R >> >> /Contents 4 0 R >>"),
                PdfStreamObject(contentBytes),
                PdfObject("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
                PdfObject("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>")
            };

            using var stream = new MemoryStream();
            WriteAscii(stream, "%PDF-1.4\n%\u00e2\u00e3\u00cf\u00d3\n");

            var offsets = new List<long> { 0 };
            for (var i = 0; i < objects.Count; i++)
            {
                offsets.Add(stream.Position);
                WriteAscii(stream, $"{i + 1} 0 obj\n");
                stream.Write(objects[i], 0, objects[i].Length);
                WriteAscii(stream, "\nendobj\n");
            }

            var xref = stream.Position;
            WriteAscii(stream, $"xref\n0 {objects.Count + 1}\n");
            WriteAscii(stream, "0000000000 65535 f \n");
            for (var i = 1; i < offsets.Count; i++)
            {
                WriteAscii(stream, $"{offsets[i]:0000000000} 00000 n \n");
            }

            WriteAscii(stream, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
            return stream.ToArray();
        }

        private static string BuildContent(RentReceiptDto receipt)
        {
            var sb = new StringBuilder();
            var left = Margin;
            var right = PageWidth - Margin;
            var width = right - left;
            var primary = (0.047, 0.471, 0.435);
            var navy = (0.078, 0.125, 0.224);
            var muted = (0.376, 0.447, 0.545);
            var border = (0.835, 0.886, 0.933);

            FillRect(sb, 0, 0, PageWidth, PageHeight, 0.965, 0.98, 0.996);
            FillRect(sb, left - 14, 78, width + 28, 684, 1, 1, 1);
            StrokeRect(sb, left - 14, 78, width + 28, 684, border);

            Text(sb, "F1", 10, left, 704, "LONTSI HOMES", muted);
            Text(sb, "F2", 34, left, 670, "RECEIPT", navy, 5);
            TextRight(sb, "F2", 12, right, 706, receipt.ReceiptNumber, primary);
            TextRight(sb, "F1", 10, right, 687, $"Issued {DateLabel(receipt.IssuedAt)}", muted);
            Line(sb, left, 646, right, 646, primary, 3);

            InfoBox(sb, left, 572, 238, 58, "Tenant", receipt.TenantName, receipt.TenantEmail, border, navy, muted);
            InfoBox(sb, left + 264, 572, 238, 58, "Landlord", receipt.LandlordName, receipt.LandlordEmail, border, navy, muted);
            InfoBox(sb, left, 492, 238, 58, "Rental unit", receipt.PropertyName, receipt.ApartmentName, border, navy, muted);
            InfoBox(sb, left + 264, 492, 238, 58, "Payment", MethodLabel(receipt.Method), PaymentNote(receipt), border, navy, muted);

            FillRect(sb, left, 442, width, 28, navy.Item1, navy.Item2, navy.Item3);
            Text(sb, "F2", 11, left + 8, 452, "Description", 1, 1, 1);
            Text(sb, "F2", 11, left + 150, 452, "Period", 1, 1, 1);
            TextRight(sb, "F2", 11, right - 8, 452, "Amount", 1, 1, 1);

            Text(sb, "F1", 11, left + 8, 418, "Rent payment", navy);
            Text(sb, "F1", 11, left + 150, 418, receipt.PeriodLabel, navy);
            TextRight(sb, "F1", 11, right - 8, 418, $"{receipt.Amount:N0} {receipt.Currency}", navy);
            Line(sb, left, 405, right, 405, border, 1);

            TextRight(sb, "F1", 16, right - 106, 370, "TOTAL:", navy);
            TextRight(sb, "F2", 17, right, 370, $"{receipt.Amount:N0} {receipt.Currency}", navy);
            Line(sb, left, 328, right, 328, border, 1);

            Text(sb, "F2", 10, left, 230, "SYSTEM VERIFICATION", primary, 1.5);
            Text(sb, "F1", 10, left, 214, "Scan the QR code or open this URL to verify the receipt.", muted);
            WriteWrappedText(sb, "F1", 9, left, 198, receipt.VerificationUrl, 78, 12, muted);
            Text(sb, "F1", 8, left, 112, $"Verification stamp: {receipt.VerificationCode}", muted);

            DrawQrCode(sb, receipt.QrCodeSvg, right - 116, 118, 108);
            return sb.ToString();
        }

        private static void InfoBox(
            StringBuilder sb,
            double x,
            double y,
            double width,
            double height,
            string label,
            string value,
            string detail,
            (double R, double G, double B) border,
            (double R, double G, double B) navy,
            (double R, double G, double B) muted)
        {
            StrokeRect(sb, x, y, width, height, border);
            Text(sb, "F1", 9, x + 12, y + height - 18, label.ToUpperInvariant(), muted, 1.5);
            Text(sb, "F2", 12, x + 12, y + height - 36, value, navy);
            if (!string.IsNullOrWhiteSpace(detail))
            {
                Text(sb, "F1", 9, x + 12, y + 12, detail, muted);
            }
        }

        private static void DrawQrCode(StringBuilder sb, string svg, double left, double bottom, double size)
        {
            FillRect(sb, left, bottom, size, size, 1, 1, 1);
            StrokeRect(sb, left, bottom, size, size, (0.835, 0.886, 0.933));

            var svgContent = svg ?? string.Empty;
            var viewBox = ViewBoxRegex.Match(svgContent);
            if (!viewBox.Success ||
                !double.TryParse(viewBox.Groups["w"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var viewWidth) ||
                viewWidth <= 0)
            {
                return;
            }

            var scale = size / viewWidth;
            foreach (Match rect in RectRegex.Matches(svgContent))
            {
                if (!double.TryParse(rect.Groups["x"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var x) ||
                    !double.TryParse(rect.Groups["y"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var y) ||
                    !double.TryParse(rect.Groups["w"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var w) ||
                    !double.TryParse(rect.Groups["h"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var h))
                {
                    continue;
                }

                FillRect(sb, left + x * scale, bottom + size - (y + h) * scale, w * scale, h * scale, 0.067, 0.094, 0.153);
            }
        }

        private static string MethodLabel(PaymentMethodEnum method)
        {
            return method switch
            {
                PaymentMethodEnum.Cash => "Cash / off-platform",
                PaymentMethodEnum.Momo => "MTN Mobile Money",
                PaymentMethodEnum.OrangeMoney => "Orange Money",
                PaymentMethodEnum.Card => "Card",
                _ => method.ToString()
            };
        }

        private static string PaymentNote(RentReceiptDto receipt)
        {
            if (receipt.Method == PaymentMethodEnum.Cash)
            {
                return "Recorded by landlord";
            }

            return string.IsNullOrWhiteSpace(receipt.ProviderReceiptUrl)
                ? "Processed by platform"
                : "Provider receipt available";
        }

        private static string DateLabel(DateTimeOffset value) => value.LocalDateTime.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

        private static void Text(StringBuilder sb, string font, double size, double x, double y, string text, (double R, double G, double B) color, double charSpacing = 0)
            => Text(sb, font, size, x, y, text, color.R, color.G, color.B, charSpacing);

        private static void Text(StringBuilder sb, string font, double size, double x, double y, string text, double r, double g, double b, double charSpacing = 0)
        {
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:0.###} {1:0.###} {2:0.###} rg\n", r, g, b);
            sb.Append("BT\n");
            sb.AppendFormat(CultureInfo.InvariantCulture, "/{0} {1:0.###} Tf\n", font, size);
            if (charSpacing != 0)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, "{0:0.###} Tc\n", charSpacing);
            }

            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:0.###} {1:0.###} Td\n", x, y);
            sb.Append('(').Append(EscapePdfText(text)).Append(") Tj\nET\n");
        }

        private static void TextRight(StringBuilder sb, string font, double size, double right, double y, string text, (double R, double G, double B) color)
            => TextRight(sb, font, size, right, y, text, color.R, color.G, color.B);

        private static void TextRight(StringBuilder sb, string font, double size, double right, double y, string text, double r, double g, double b)
        {
            var estimatedWidth = EstimateTextWidth(text, size);
            Text(sb, font, size, Math.Max(Margin, right - estimatedWidth), y, text, r, g, b);
        }

        private static void WriteWrappedText(StringBuilder sb, string font, double size, double x, double y, string text, int maxChars, double lineHeight, (double R, double G, double B) color)
        {
            foreach (var line in Wrap(text, maxChars))
            {
                Text(sb, font, size, x, y, line, color);
                y -= lineHeight;
            }
        }

        private static IEnumerable<string> Wrap(string value, int maxChars)
        {
            var text = value ?? string.Empty;
            for (var index = 0; index < text.Length; index += maxChars)
            {
                yield return text.Substring(index, Math.Min(maxChars, text.Length - index));
            }
        }

        private static double EstimateTextWidth(string? text, double size)
        {
            return (text ?? string.Empty).Length * size * 0.55;
        }

        private static void FillRect(StringBuilder sb, double x, double y, double width, double height, double r, double g, double b)
        {
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:0.###} {1:0.###} {2:0.###} rg\n", r, g, b);
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:0.###} {1:0.###} {2:0.###} {3:0.###} re f\n", x, y, width, height);
        }

        private static void StrokeRect(StringBuilder sb, double x, double y, double width, double height, (double R, double G, double B) color)
        {
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:0.###} {1:0.###} {2:0.###} RG\n", color.R, color.G, color.B);
            sb.AppendFormat(CultureInfo.InvariantCulture, "0.75 w\n{0:0.###} {1:0.###} {2:0.###} {3:0.###} re S\n", x, y, width, height);
        }

        private static void Line(StringBuilder sb, double x1, double y1, double x2, double y2, (double R, double G, double B) color, double width)
        {
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:0.###} {1:0.###} {2:0.###} RG\n", color.R, color.G, color.B);
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:0.###} w\n{1:0.###} {2:0.###} m {3:0.###} {4:0.###} l S\n", width, x1, y1, x2, y2);
        }

        private static string EscapePdfText(string? value)
        {
            var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder();
            foreach (var c in normalized)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                var safe = c is >= ' ' and <= '~' ? c : '?';
                if (safe is '\\' or '(' or ')')
                {
                    builder.Append('\\');
                }

                builder.Append(safe);
            }

            return builder.ToString();
        }

        private static byte[] PdfObject(string content) => Encoding.ASCII.GetBytes(content);

        private static byte[] PdfStreamObject(byte[] content)
        {
            using var stream = new MemoryStream();
            WriteAscii(stream, $"<< /Length {content.Length} >>\nstream\n");
            stream.Write(content, 0, content.Length);
            WriteAscii(stream, "\nendstream");
            return stream.ToArray();
        }

        private static void WriteAscii(Stream stream, string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
        }
    }
}
