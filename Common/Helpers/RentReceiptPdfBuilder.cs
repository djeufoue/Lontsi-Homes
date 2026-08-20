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
            => Build(receipt, PlatformLanguage.English);

        public static byte[] Build(RentReceiptDto receipt, PlatformLanguage language)
        {
            var content = BuildContent(receipt, language);
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

        private static string BuildContent(RentReceiptDto receipt, PlatformLanguage language)
        {
            var isFrench = language == PlatformLanguage.French;
            var culture = CultureInfo.GetCultureInfo(language.ToCultureName());
            var sb = new StringBuilder();
            var left = Margin;
            var right = PageWidth - Margin;
            var width = right - left;
            var paper = (0.953, 0.929, 0.875);
            var paperAlt = (1d, 0.992, 0.973);
            var accent = (0.443, 0.314, 0.31);
            var ink = (0.396, 0.286, 0.282);
            var muted = (0.56, 0.45, 0.43);
            var footer = (0.66, 0.553, 0.537);

            // The invoice paper fills the complete PDF media box. Previously a dark
            // canvas was painted first and the paper was inset, which appeared as a
            // thick black border in PDF readers and when printing.
            FillRect(sb, 0, 0, PageWidth, PageHeight, paper.Item1, paper.Item2, paper.Item3);

            var invoiceTitle = isFrench ? "FACTURE DE LOYER" : "RENT INVOICE";
            var titleSize = FitFontSize(invoiceTitle, 31, 21, width - 70);
            var fittedTitle = FitTextToWidth(invoiceTitle, titleSize, width - 70);
            Text(
                sb,
                "F1",
                titleSize,
                (PageWidth - EstimateTextWidth(fittedTitle, titleSize)) / 2,
                735,
                fittedTitle,
                ink,
                2.8);

            const string brand = "LONTSI HOMES";
            const double brandFontSize = 15;
            const double brandMarkSize = 26;
            const double brandGap = 8;
            var brandGroupWidth = brandMarkSize + brandGap + EstimateTextWidth(brand, brandFontSize);
            var brandGroupLeft = (PageWidth - brandGroupWidth) / 2;
            DrawBrandMark(sb, brandGroupLeft, 691, brandMarkSize, (0.541, 0.624, 0.235), ink, paper);
            Text(sb, "F2", brandFontSize, brandGroupLeft + brandMarkSize + brandGap, 699, brand, ink);
            var tagline = isFrench ? "GESTION IMMOBILIERE ET SERVICES DE LOCATION" : "PROPERTY MANAGEMENT AND RENT SERVICES";
            var taglineSize = FitFontSize(tagline, 8, 6.5, width - 90);
            Text(sb, "F2", taglineSize, (PageWidth - EstimateTextWidth(tagline, taglineSize)) / 2, 683, tagline, muted, 0.9);
            Line(sb, left, 659, right, 659, accent, 1.2);

            var receiptReference = AbbreviateReceiptReference(receipt.ReceiptNumber);

            Text(sb, "F2", 8, left, 628, isFrench ? "NUMERO DE FACTURE" : "INVOICE NUMBER", accent, 0.8);
            TextFitted(sb, "F2", 11, 8, left, 610, receiptReference, ink, 230);
            if (!string.IsNullOrWhiteSpace(receipt.LandlordName))
            {
                TextFitted(sb, "F1", 9, 7, left, 590, receipt.LandlordName, muted, 230);
            }
            if (!string.IsNullOrWhiteSpace(receipt.LandlordEmail))
            {
                TextFitted(sb, "F1", 8.5, 7, left, 574, receipt.LandlordEmail, muted, 230);
            }

            TextRightFitted(sb, "F2", 8, 7, right, 628, isFrench ? "DATE D'EMISSION" : "ISSUE DATE", accent, 150);
            TextRightFitted(sb, "F2", 11, 8, right, 610, DateLabel(receipt.IssuedAt, language), ink, 150);
            if (!string.IsNullOrWhiteSpace(receipt.PropertyName))
            {
                TextRightFitted(sb, "F1", 9, 7, right, 590, receipt.PropertyName, muted, 210);
            }
            if (!string.IsNullOrWhiteSpace(receipt.ApartmentName))
            {
                TextRightFitted(sb, "F1", 8.5, 7, right, 574, receipt.ApartmentName, muted, 210);
            }

            FillRect(sb, left, 510, width, 32, accent.Item1, accent.Item2, accent.Item3);
            Text(sb, "F2", 9, left + 10, 521, isFrench ? "DESCRIPTION" : "DESCRIPTION", 1, 0.98, 0.95);
            Text(sb, "F2", 9, left + 190, 521, isFrench ? "PERIODE" : "PERIOD", 1, 0.98, 0.95);
            Text(sb, "F2", 9, right - 138, 521, isFrench ? "QTE" : "QTY", 1, 0.98, 0.95);
            TextRightFitted(sb, "F2", 9, 7, right - 10, 521, "TOTAL", (1d, 0.98, 0.95), 92);

            FillRect(sb, left, 458, width, 52, paperAlt.Item1, paperAlt.Item2, paperAlt.Item3);
            Text(sb, "F1", 10, left + 10, 481, isFrench ? "Loyer du logement" : "Rent for rental unit", ink);
            TextFitted(sb, "F1", 9, 7, left + 190, 481, PeriodLabel(receipt, culture), ink, 180);
            Text(sb, "F1", 10, right - 125, 481, "1", ink);
            TextRightFitted(sb, "F1", 10, 8, right - 10, 481, AmountLabel(receipt, culture), ink, 105);

            Text(sb, "F2", 8, left, 414, isFrench ? "PAIEMENT" : "PAYMENT", accent, 0.8);
            TextFitted(sb, "F2", 10, 8, left, 395, MethodLabel(receipt.Method, language), ink, 220);
            TextFitted(sb, "F1", 8.5, 7, left, 378, PaymentNote(receipt, language), muted, 220);
            TextFitted(
                sb,
                "F1",
                8.5,
                7,
                left,
                361,
                isFrench ? $"Date du paiement : {DateLabel(receipt.PaymentDate, language)}" : $"Payment date: {DateLabel(receipt.PaymentDate, language)}",
                muted,
                220);

            TextRightFitted(sb, "F2", 8, 7, right, 414, isFrench ? "MONTANT PAYE" : "AMOUNT PAID", accent, 145);
            TextRightFitted(sb, "F2", 18, 12, right, 386, AmountLabel(receipt, culture), ink, 185);
            FillRect(sb, right - 58, 352, 58, 20, accent.Item1, accent.Item2, accent.Item3);
            TextRightFitted(sb, "F2", 8, 7, right - 10, 359, isFrench ? "PAYEE" : "PAID", (1d, 0.98, 0.95), 42);

            FillRect(sb, 0, 0, PageWidth, 294, footer.Item1, footer.Item2, footer.Item3);
            Text(sb, "F2", 8, left, 260, isFrench ? "INFORMATIONS DU LOCATAIRE" : "TENANT INFORMATION", 1, 0.98, 0.95, 0.8);
            TextFitted(sb, "F2", 14, 10, left, 235, receipt.TenantName, (1d, 0.98, 0.95), 315);

            var tenantLineY = 211d;
            if (!string.IsNullOrWhiteSpace(receipt.TenantPhone))
            {
                TextFitted(
                    sb,
                    "F1",
                    9,
                    7,
                    left,
                    tenantLineY,
                    isFrench ? $"Telephone : {receipt.TenantPhone}" : $"Phone: {receipt.TenantPhone}",
                    (1d, 0.98, 0.95),
                    315);
                tenantLineY -= 18;
            }
            if (!string.IsNullOrWhiteSpace(receipt.TenantEmail))
            {
                TextFitted(
                    sb,
                    "F1",
                    9,
                    7,
                    left,
                    tenantLineY,
                    isFrench ? $"Courriel : {receipt.TenantEmail}" : $"Email: {receipt.TenantEmail}",
                    (1d, 0.98, 0.95),
                    315);
                tenantLineY -= 26;
            }

            Text(sb, "F2", 8, left, tenantLineY, isFrench ? "VERIFICATION" : "VERIFICATION", 1, 0.98, 0.95, 0.8);
            TextFitted(
                sb,
                "F1",
                8.5,
                7,
                left,
                tenantLineY - 18,
                isFrench ? "Scannez le code QR pour verifier cette facture." : "Scan the QR code to verify this invoice.",
                (1d, 0.98, 0.95),
                315);

            // Align the QR card with the top of the tenant-information block so the
            // footer keeps the same visual grid as the browser invoice.
            DrawQrCode(sb, receipt.QrCodeSvg, right - 112, 166, 106);
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

        private static void DrawBrandMark(
            StringBuilder sb,
            double left,
            double bottom,
            double size,
            (double R, double G, double B) olive,
            (double R, double G, double B) charcoal,
            (double R, double G, double B) knockout)
        {
            (double X, double Y) Point(double x, double y) => (left + x * size, bottom + y * size);

            FillPolygon(
                sb,
                new[]
                {
                    Point(0.04, 0.67), Point(0.50, 1.00), Point(0.96, 0.68),
                    Point(0.96, 0.49), Point(0.50, 0.80), Point(0.04, 0.48)
                },
                olive);

            FillPolygon(
                sb,
                new[]
                {
                    Point(0.08, 0.13), Point(0.08, 0.63), Point(0.27, 0.53),
                    Point(0.27, 0.31), Point(0.47, 0.31), Point(0.47, 0.13)
                },
                charcoal);

            FillPolygon(
                sb,
                new[]
                {
                    Point(0.54, 0.64), Point(0.72, 0.54), Point(0.72, 0.38),
                    Point(0.84, 0.38), Point(0.84, 0.56), Point(0.96, 0.49),
                    Point(0.96, 0.13), Point(0.84, 0.13), Point(0.84, 0.25),
                    Point(0.72, 0.25), Point(0.72, 0.13), Point(0.54, 0.13)
                },
                charcoal);

            var windowSize = size * 0.075;
            FillRect(sb, left + size * 0.30, bottom + size * 0.48, windowSize, windowSize, knockout.R, knockout.G, knockout.B);
            FillRect(sb, left + size * 0.39, bottom + size * 0.48, windowSize, windowSize, knockout.R, knockout.G, knockout.B);
            FillRect(sb, left + size * 0.30, bottom + size * 0.39, windowSize, windowSize, knockout.R, knockout.G, knockout.B);
            FillRect(sb, left + size * 0.39, bottom + size * 0.39, windowSize, windowSize, knockout.R, knockout.G, knockout.B);
        }

        private static string MethodLabel(PaymentMethodEnum method, PlatformLanguage language)
        {
            var isFrench = language == PlatformLanguage.French;
            return method switch
            {
                PaymentMethodEnum.Cash => isFrench ? "Especes / hors plateforme" : "Cash / off-platform",
                PaymentMethodEnum.Momo => "MTN Mobile Money",
                PaymentMethodEnum.OrangeMoney => "Orange Money",
                PaymentMethodEnum.Card => isFrench ? "Carte" : "Card",
                _ => method.ToString()
            };
        }

        private static string PaymentNote(RentReceiptDto receipt, PlatformLanguage language)
        {
            var isFrench = language == PlatformLanguage.French;
            if (receipt.Method == PaymentMethodEnum.Cash)
            {
                return isFrench ? "Enregistre manuellement par le bailleur" : "Recorded manually by landlord";
            }

            return string.IsNullOrWhiteSpace(receipt.ProviderReceiptUrl)
                ? (isFrench ? "Traite par la plateforme" : "Processed by platform")
                : (isFrench ? "Recu du fournisseur disponible" : "Provider receipt available");
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

        private static string DateLabel(DateTimeOffset value, PlatformLanguage language)
            => value.LocalDateTime.ToString("dd MMM yyyy", CultureInfo.GetCultureInfo(language.ToCultureName()));

        private static string PeriodLabel(RentReceiptDto receipt, CultureInfo culture)
        {
            if (!receipt.PeriodStart.HasValue || !receipt.PeriodEnd.HasValue)
            {
                return receipt.PeriodLabel;
            }

            return $"{receipt.PeriodStart.Value.ToString("dd MMM yyyy", culture)} - {receipt.PeriodEnd.Value.ToString("dd MMM yyyy", culture)}";
        }

        private static string AmountLabel(RentReceiptDto receipt, CultureInfo culture)
            => $"{receipt.Amount.ToString("N0", culture).Replace('\u00a0', ' ').Replace('\u202f', ' ')} {receipt.Currency}";

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

        private static void FillPolygon(
            StringBuilder sb,
            IReadOnlyList<(double X, double Y)> points,
            (double R, double G, double B) color)
        {
            if (points.Count < 3)
            {
                return;
            }

            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:0.###} {1:0.###} {2:0.###} rg\n", color.R, color.G, color.B);
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:0.###} {1:0.###} m\n", points[0].X, points[0].Y);
            for (var index = 1; index < points.Count; index++)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, "{0:0.###} {1:0.###} l\n", points[index].X, points[index].Y);
            }

            sb.Append("h f\n");
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
