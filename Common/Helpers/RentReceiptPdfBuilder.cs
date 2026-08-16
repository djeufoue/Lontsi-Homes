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

            Text(sb, "F2", 9, left, 706, "LONTSI HOMES", primary, 1.2);
            Text(sb, "F1", 34, left, 668, "RECEIPT", navy, 3.2);
            var receiptReference = AbbreviateReceiptReference(receipt.ReceiptNumber);
            TextRightFitted(sb, "F2", 10, 7, right, 706, receiptReference, primary, 180);
            TextRightFitted(sb, "F1", 10, 8, right, 684, $"Issued {DateLabel(receipt.IssuedAt)}", muted, 170);
            Line(sb, left, 646, right, 646, primary, 3);

            var boxGap = 14d;
            var boxWidth = (width - boxGap) / 2d;
            InfoBox(sb, left, 558, boxWidth, 72, "Tenant", receipt.TenantName, receipt.TenantEmail, border, navy, muted);
            InfoBox(sb, left + boxWidth + boxGap, 558, boxWidth, 72, "Landlord", receipt.LandlordName, receipt.LandlordEmail, border, navy, muted);
            InfoBox(sb, left, 470, boxWidth, 72, "Rental unit", receipt.PropertyName, receipt.ApartmentName, border, navy, muted);
            InfoBox(sb, left + boxWidth + boxGap, 470, boxWidth, 72, "Payment", MethodLabel(receipt.Method), PaymentNote(receipt), border, navy, muted);

            FillRect(sb, left, 422, width, 30, navy.Item1, navy.Item2, navy.Item3);
            Text(sb, "F2", 10, left + 10, 432, "Description", 1, 1, 1);
            Text(sb, "F2", 10, left + 150, 432, "Period", 1, 1, 1);
            TextRightFitted(sb, "F2", 10, 8, right - 10, 432, "Amount", (1d, 1d, 1d), 76);

            Text(sb, "F1", 10, left + 10, 394, "Rent payment", navy);
            TextFitted(sb, "F1", 10, 8, left + 150, 394, receipt.PeriodLabel, navy, 218);
            TextRightFitted(sb, "F1", 10, 8, right - 10, 394, $"{receipt.Amount:N0} {receipt.Currency}", navy, 110);
            Line(sb, left, 380, right, 380, border, 1);

            TextRightFitted(sb, "F1", 15, 11, right - 132, 344, "Total", navy, 60);
            TextRightFitted(sb, "F2", 16, 11, right, 344, $"{receipt.Amount:N0} {receipt.Currency}", navy, 124);
            Line(sb, left, 308, right, 308, border, 1);

            Text(sb, "F2", 9, left, 236, "VERIFICATION", primary, 1.2);
            Text(sb, "F1", 10, left, 216, "Scan the QR code or open the verification link to confirm this", muted);
            Text(sb, "F1", 10, left, 202, "receipt was generated by the system.", muted);
            WriteWrappedText(sb, "F1", 9, left, 180, receipt.VerificationUrl, 348, 12, muted);
            TextFitted(sb, "F1", 8, 7, left, 112, $"Verification stamp: {receipt.VerificationCode}", muted, 345);

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
            Text(sb, "F1", 8, x + 12, y + height - 18, label.ToUpperInvariant(), muted, 1.1);
            TextFitted(sb, "F2", 12, 9, x + 12, y + height - 39, value, navy, width - 24);
            if (!string.IsNullOrWhiteSpace(detail))
            {
                TextFitted(sb, "F1", 9, 7, x + 12, y + 12, detail, muted, width - 24);
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
                return "Recorded manually by landlord";
            }

            return string.IsNullOrWhiteSpace(receipt.ProviderReceiptUrl)
                ? "Processed by platform"
                : "Provider receipt available";
        }

        private static string AbbreviateReceiptReference(string? value)
        {
            var reference = value ?? string.Empty;
            if (reference.Length <= 30)
            {
                return reference;
            }

            // Preserve both the recognizable prefix and the unique tail while keeping
            // unusually long references inside the printable receipt header.
            return $"{reference[..18]}...{reference[^9..]}";
        }

        private static string DateLabel(DateTimeOffset value) => value.LocalDateTime.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

        private static void Text(StringBuilder sb, string font, double size, double x, double y, string text, (double R, double G, double B) color, double charSpacing = 0)
            => Text(sb, font, size, x, y, text, color.R, color.G, color.B, charSpacing);

        private static void Text(StringBuilder sb, string font, double size, double x, double y, string text, double r, double g, double b, double charSpacing = 0)
        {
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:0.###} {1:0.###} {2:0.###} rg\n", r, g, b);
            sb.Append("BT\n");
            sb.AppendFormat(CultureInfo.InvariantCulture, "/{0} {1:0.###} Tf\n", font, size);
            // Text-state values such as character spacing persist between PDF text
            // objects, so always reset Tc. Without this, the letter spacing used by
            // small eyebrow labels leaks into later values and causes right overflow.
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:0.###} Tc\n", charSpacing);

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

        private static void TextFitted(
            StringBuilder sb,
            string font,
            double preferredSize,
            double minimumSize,
            double x,
            double y,
            string text,
            (double R, double G, double B) color,
            double maxWidth)
        {
            var size = FitFontSize(text, preferredSize, minimumSize, maxWidth);
            var fittedText = FitTextToWidth(text, size, maxWidth);
            Text(sb, font, size, x, y, fittedText, color);
        }

        private static void TextRightFitted(
            StringBuilder sb,
            string font,
            double preferredSize,
            double minimumSize,
            double right,
            double y,
            string text,
            (double R, double G, double B) color,
            double maxWidth)
        {
            var size = FitFontSize(text, preferredSize, minimumSize, maxWidth);
            var fittedText = FitTextToWidth(text, size, maxWidth);
            var width = EstimateTextWidth(fittedText, size);
            Text(sb, font, size, right - width, y, fittedText, color);
        }

        private static double FitFontSize(string? text, double preferredSize, double minimumSize, double maxWidth)
        {
            var measured = EstimateTextWidth(text, preferredSize);
            if (measured <= maxWidth || measured <= 0)
            {
                return preferredSize;
            }

            return Math.Max(minimumSize, preferredSize * maxWidth / measured);
        }

        private static string FitTextToWidth(string? value, double fontSize, double maxWidth)
        {
            var text = value ?? string.Empty;
            var safeWidth = Math.Max(0, maxWidth * 0.92);
            if (EstimateTextWidth(text, fontSize) <= safeWidth)
            {
                return text;
            }

            const string suffix = "...";
            var availableWidth = Math.Max(0, safeWidth - EstimateTextWidth(suffix, fontSize));
            var length = text.Length;
            while (length > 0 && EstimateTextWidth(text[..length], fontSize) > availableWidth)
            {
                length--;
            }

            return length == 0 ? suffix : $"{text[..length].TrimEnd()}{suffix}";
        }

        private static void WriteWrappedText(StringBuilder sb, string font, double size, double x, double y, string text, double maxWidth, double lineHeight, (double R, double G, double B) color)
        {
            foreach (var line in Wrap(text, size, maxWidth))
            {
                Text(sb, font, size, x, y, line, color);
                y -= lineHeight;
            }
        }

        private static IEnumerable<string> Wrap(string value, double fontSize, double maxWidth)
        {
            var text = value ?? string.Empty;
            var line = new StringBuilder();
            foreach (var character in text)
            {
                if (line.Length > 0 && EstimateTextWidth(line.ToString() + character, fontSize) > maxWidth)
                {
                    yield return line.ToString();
                    line.Clear();
                }

                line.Append(character);
            }

            if (line.Length > 0)
            {
                yield return line.ToString();
            }
        }

        private static double EstimateTextWidth(string? text, double size)
        {
            var units = 0d;
            foreach (var character in text ?? string.Empty)
            {
                units += character switch
                {
                    ' ' => 0.278,
                    'I' or 'i' or 'l' or '!' or '|' => 0.278,
                    'M' or 'W' or 'm' or 'w' => 0.833,
                    >= 'A' and <= 'Z' => 0.667,
                    >= '0' and <= '9' => 0.556,
                    '-' or ':' or '.' or ',' or '/' => 0.333,
                    _ => 0.5
                };
            }

            // Built-in Type1 font metrics vary slightly between PDF viewers. The safety
            // factor keeps fitted strings inside their assigned columns in every renderer.
            return units * size * 1.25;
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
