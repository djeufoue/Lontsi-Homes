using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
            var pages = BuildContent(receipt, language);

            var objects = new List<byte[]>
            {
                PdfObject("<< /Type /Catalog /Pages 2 0 R >>"),
                PdfObject($"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pages.Count).Select(index => $"{5 + index * 2} 0 R"))}] /Count {pages.Count} >>"),
                PdfObject("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
                PdfObject("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>")
            };
            for (var index = 0; index < pages.Count; index++)
            {
                var content = new StringBuilder(pages[index]);
                Text(content, "F1", 8, Margin, 16,
                    language == PlatformLanguage.French ? $"Page {index + 1} sur {pages.Count}" : $"Page {index + 1} of {pages.Count}",
                    index == pages.Count - 1 ? (1d, 0.98, 0.95) : (0.396, 0.286, 0.282));
                objects.Add(PdfObject($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {6 + index * 2} 0 R >>"));
                objects.Add(PdfStreamObject(Encoding.ASCII.GetBytes(content.ToString())));
            }

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

        private static List<string> BuildContent(RentReceiptDto receipt, PlatformLanguage language)
        {
            var isFrench = language == PlatformLanguage.French;
            var culture = CultureInfo.GetCultureInfo(language.ToCultureName());
            var sb = new StringBuilder();
            var pages = new List<string>();
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
                (PageWidth - MeasureTextWidth(fittedTitle, titleSize, "F1") - (fittedTitle.Length - 1) * 2.8) / 2,
                780,
                fittedTitle,
                ink,
                2.8);

            const string brand = "LONTSI HOMES";
            const double brandFontSize = 15;
            const double brandMarkSize = 26;
            const double brandGap = 8;
            var brandGroupWidth = brandMarkSize + brandGap + MeasureTextWidth(brand, brandFontSize, "F2");
            var brandGroupLeft = (PageWidth - brandGroupWidth) / 2;
            DrawBrandMark(sb, brandGroupLeft, 736, brandMarkSize, (0.541, 0.624, 0.235), ink, paper);
            Text(sb, "F2", brandFontSize, brandGroupLeft + brandMarkSize + brandGap, 744, brand, ink);
            var tagline = isFrench ? "GESTION IMMOBILIERE ET SERVICES DE LOCATION" : "PROPERTY MANAGEMENT AND RENT SERVICES";
            var taglineSize = FitFontSize(tagline, 8, 6.5, width - 90);
            Text(sb, "F2", taglineSize, (PageWidth - MeasureTextWidth(tagline, taglineSize, "F2") - (tagline.Length - 1) * 0.9) / 2, 728, tagline, muted, 0.9);
            Line(sb, left, 704, right, 704, accent, 1.2);

            var border = (0.85, 0.8, 0.76);
            var top = 686d;
            void Fact(string label, string value, double height = 26, bool boxed = false, bool total = false)
            {
                var bottom = top - height;
                var background = total ? accent : paperAlt;
                if (boxed || total)
                {
                    FillRect(sb, left, bottom, width, height, background.Item1, background.Item2, background.Item3);
                    StrokeRect(sb, left, bottom, width, height, border);
                    Line(sb, left + 170, bottom, left + 170, top, border, 0.5);
                }
                else Line(sb, left, bottom, right, bottom, border, 0.5);
                var color = total ? (1d, 0.98, 0.95) : ink;
                TextFitted(sb, total ? "F2" : "F1", 10, 8, left + 10, bottom + 9, label, color, 150);
                TextRightFitted(sb, total ? "F2" : "F1", total ? 12 : 10, 7,
                    right - 10, bottom + 9, value, color, width - 195);
                top = bottom;
            }
            void Heading(string label)
            {
                top -= 23;
                Text(sb, "F2", 11, left, top, label, accent);
                top -= 12;
            }

            {
                Fact(isFrench ? "Facture" : "Invoice", receipt.ReceiptNumber, boxed: true);
                Fact(isFrench ? "Date du paiement" : "Payment date", DateLabel(receipt.PaymentDate, language), boxed: true);
                Fact(isFrench ? "Statut" : "Status", receipt.Status == PaymentStatusEnum.Success
                    ? (isFrench ? "PAYE" : "PAID") : receipt.Status.ToString().ToUpperInvariant(), boxed: true);

                Heading(isFrench ? "Informations de location" : "Rental information");
                Fact(isFrench ? "Locataire" : "Tenant", receipt.TenantName);
                Fact(isFrench ? "Propriete" : "Property", receipt.PropertyName);
                Fact(isFrench ? "Logement" : "Rental unit", receipt.ApartmentName);
                var period = receipt.Lines.Count > 0
                    ? $"{receipt.Lines.Min(line => line.PeriodStart).ToString("dd MMM yyyy", culture)} - {receipt.Lines.Max(line => line.PeriodEnd).ToString("dd MMM yyyy", culture)}"
                    : PeriodLabel(receipt, culture);
                Fact(isFrench ? "Periode reglee" : "Paid period", period);
                if (!string.IsNullOrWhiteSpace(receipt.LandlordName))
                    Fact(isFrench ? "Bailleur" : "Landlord", receipt.LandlordName, 23);
                if (!string.IsNullOrWhiteSpace(receipt.LandlordEmail))
                    Fact(isFrench ? "Courriel du bailleur" : "Landlord email", receipt.LandlordEmail, 23);

                Heading(isFrench ? "Paiement" : "Payment");
                Fact(isFrench ? "Loyer" : "Rent", AmountLabel(receipt, culture), 25, boxed: true);
                Fact(isFrench ? "Mode de paiement" : "Payment method", MethodLabel(receipt.Method, language), 25, boxed: true);
                Fact(isFrench ? "Total paye" : "Total paid", AmountLabel(receipt, culture), 29, total: true);
                TextFitted(sb, "F1", 8, 7, left, top - 16, PaymentNote(receipt, language), muted, width);
                TextFitted(sb, "F1", 8, 7, left, top - 30,
                    (isFrench ? "Date d'emission : " : "Issue date: ") + DateLabel(receipt.IssuedAt, language), muted, width);
            }
            // Keep short itemizations on the summary page; reserve space for the
            // final verification band so it never collides with a table row.
            const double contentBottom = 170;
            void ContinuePage()
            {
                pages.Add(sb.ToString());
                sb.Clear();
                FillRect(sb, 0, 0, PageWidth, PageHeight, paper.Item1, paper.Item2, paper.Item3);
                TextFitted(sb, "F2", 11, 8, left, 787,
                    (isFrench ? "Facture " : "Invoice ") + receipt.ReceiptNumber + (isFrench ? " - suite" : " - continued"), ink, width);
                Line(sb, left, 772, right, 772, border, 0.75);
                top = 752;
            }
            void TableHeader()
            {
                Text(sb, "F2", 11, left, top, isFrench ? "Detail des periodes reglees" : "Paid period details", accent);
                top -= 38;
                FillRect(sb, left, top, width, 26, accent.Item1, accent.Item2, accent.Item3);
                Text(sb, "F2", 9, left + 10, top + 9, isFrench ? "PERIODE" : "PERIOD", (1d, 0.98, 0.95));
                TextRightFitted(sb, "F2", 9, 7, right - 114, top + 9, isFrench ? "MONTANT" : "AMOUNT", (1d, 0.98, 0.95), 95);
                TextRightFitted(sb, "F2", 9, 7, right - 10, top + 9, isFrench ? "PAYE" : "PAID", (1d, 0.98, 0.95), 95);
            }
            if (receipt.Lines.Count > 1)
            {
                top -= 48;
                if (top - 38 - 23 < contentBottom) ContinuePage();
                TableHeader();
                foreach (var line in receipt.Lines.OrderBy(line => line.PeriodStart))
                {
                    if (top - 23 < contentBottom)
                    {
                        ContinuePage();
                        TableHeader();
                    }
                    top -= 23;
                    FillRect(sb, left, top, width, 23, paperAlt.Item1, paperAlt.Item2, paperAlt.Item3);
                    Line(sb, left, top, right, top, border, 0.5);
                    TextFitted(sb, "F1", 9, 7, left + 10, top + 8,
                        $"{line.PeriodStart.ToString("dd MMM yyyy", culture)} - {line.PeriodEnd.ToString("dd MMM yyyy", culture)}", ink, 265);
                    TextRightFitted(sb, "F1", 9, 7, right - 114, top + 8,
                        $"{line.PeriodAmount.ToString("N0", culture)} {receipt.Currency}", ink, 95);
                    TextRightFitted(sb, "F2", 9, 7, right - 10, top + 8,
                        $"{line.PaidAmount.ToString("N0", culture)} {receipt.Currency}", ink, 95);
                }
            }

            // Verification appears only on the final page, not on every page.
            FillRect(sb, 0, 0, PageWidth, 150, footer.Item1, footer.Item2, footer.Item3);
            Text(sb, "F2", 8, left, 127, isFrench ? "INFORMATIONS DU LOCATAIRE" : "TENANT INFORMATION", 1, 0.98, 0.95, 0.8);
            TextFitted(sb, "F2", 12, 9, left, 107, receipt.TenantName, (1d, 0.98, 0.95), 315);

            var tenantLineY = 89d;
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
                tenantLineY -= 14;
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
                tenantLineY -= 20;
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
            DrawQrCode(sb, receipt.QrCodeSvg, right - 106, 32, 96);
            pages.Add(sb.ToString());
            return pages;
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
            var estimatedWidth = MeasureTextWidth(text, size, font);
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
            var width = MeasureTextWidth(fittedText, size, font);
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

        // Standard Helvetica advance widths, ASCII 32-126, in thousandths of an em.
        // Use real metrics for alignment; conservative estimates above remain for fitting.
        private static readonly int[] RegularWidths = { 278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556, 1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556, 333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584 };
        private static readonly int[] BoldWidths = { 278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611, 975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556, 333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611, 611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584 };

        private static double MeasureTextWidth(string text, double size, string font)
        {
            var widths = font == "F2" ? BoldWidths : RegularWidths;
            return NormalizePdfText(text).Sum(character => widths[character - ' ']) * size / 1000d;
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
            => NormalizePdfText(value).Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

        private static string NormalizePdfText(string? value)
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

                var safe = char.IsWhiteSpace(c) ? ' ' : c is >= ' ' and <= '~' ? c : '?';
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
