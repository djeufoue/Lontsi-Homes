using System.Text;

namespace LontsiHomes.API.Services.Receipts
{
    internal static class SimpleQrCodeGenerator
    {
        private const int Version = 5;
        private const int Size = 37;
        private const int DataCodewords = 108;
        private const int ErrorCodewords = 26;
        private const int MaskPattern = 0;
        private static readonly byte[] ExpTable;
        private static readonly byte[] LogTable;

        static SimpleQrCodeGenerator()
        {
            ExpTable = new byte[512];
            LogTable = new byte[256];

            var value = 1;
            for (var i = 0; i < 255; i++)
            {
                ExpTable[i] = (byte)value;
                LogTable[value] = (byte)i;
                value <<= 1;
                if ((value & 0x100) != 0)
                {
                    value ^= 0x11D;
                }
            }

            for (var i = 255; i < ExpTable.Length; i++)
            {
                ExpTable[i] = ExpTable[i - 255];
            }
        }

        public static string CreateSvg(string payload, int scale = 5)
        {
            var payloadBytes = Encoding.UTF8.GetBytes(payload);
            if (payloadBytes.Length > 104)
            {
                return string.Empty;
            }

            var modules = Encode(payloadBytes);
            var quiet = 4;
            var imageSize = (Size + quiet * 2) * scale;
            var sb = new StringBuilder();
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ")
                .Append(imageSize)
                .Append(' ')
                .Append(imageSize)
                .Append("\" role=\"img\" aria-label=\"Receipt verification QR code\">");
            sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"#ffffff\"/>");

            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    if (!modules[y, x])
                    {
                        continue;
                    }

                    sb.Append("<rect fill=\"#111827\" x=\"")
                        .Append((x + quiet) * scale)
                        .Append("\" y=\"")
                        .Append((y + quiet) * scale)
                        .Append("\" width=\"")
                        .Append(scale)
                        .Append("\" height=\"")
                        .Append(scale)
                        .Append("\"/>");
                }
            }

            sb.Append("</svg>");
            return sb.ToString();
        }

        private static bool[,] Encode(byte[] payload)
        {
            var data = BuildDataCodewords(payload);
            var ecc = BuildErrorCorrection(data, ErrorCodewords);
            var codewords = data.Concat(ecc).ToArray();
            var bits = new List<int>(codewords.Length * 8);
            foreach (var codeword in codewords)
            {
                AppendBits(bits, codeword, 8);
            }

            var modules = new bool?[Size, Size];
            var function = new bool[Size, Size];

            DrawFunctionPatterns(modules, function);
            DrawData(modules, function, bits);
            DrawFormatBits(modules, function);

            var result = new bool[Size, Size];
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    result[y, x] = modules[y, x] == true;
                }
            }

            return result;
        }

        private static byte[] BuildDataCodewords(byte[] payload)
        {
            var bits = new List<int>(DataCodewords * 8);
            AppendBits(bits, 0b0100, 4);
            AppendBits(bits, payload.Length, 8);

            foreach (var b in payload)
            {
                AppendBits(bits, b, 8);
            }

            var capacityBits = DataCodewords * 8;
            var terminator = Math.Min(4, capacityBits - bits.Count);
            for (var i = 0; i < terminator; i++)
            {
                bits.Add(0);
            }

            while (bits.Count % 8 != 0)
            {
                bits.Add(0);
            }

            var pad = true;
            while (bits.Count < capacityBits)
            {
                AppendBits(bits, pad ? 0xEC : 0x11, 8);
                pad = !pad;
            }

            var data = new byte[DataCodewords];
            for (var i = 0; i < data.Length; i++)
            {
                var value = 0;
                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value << 1) | bits[i * 8 + bit];
                }

                data[i] = (byte)value;
            }

            return data;
        }

        private static byte[] BuildErrorCorrection(byte[] data, int degree)
        {
            var generator = BuildGeneratorPolynomial(degree);
            var result = new byte[degree];

            foreach (var value in data)
            {
                var factor = (byte)(value ^ result[0]);
                Array.Copy(result, 1, result, 0, degree - 1);
                result[degree - 1] = 0;

                if (factor == 0)
                {
                    continue;
                }

                for (var i = 0; i < degree; i++)
                {
                    result[i] ^= Multiply(generator[i], factor);
                }
            }

            return result;
        }

        private static byte[] BuildGeneratorPolynomial(int degree)
        {
            var generator = new byte[] { 1 };
            for (var i = 0; i < degree; i++)
            {
                var next = new byte[generator.Length + 1];
                for (var j = 0; j < generator.Length; j++)
                {
                    next[j] ^= generator[j];
                    next[j + 1] ^= Multiply(generator[j], ExpTable[i]);
                }

                generator = next;
            }

            return generator.Skip(1).ToArray();
        }

        private static byte Multiply(byte x, byte y)
        {
            if (x == 0 || y == 0)
            {
                return 0;
            }

            return ExpTable[LogTable[x] + LogTable[y]];
        }

        private static void DrawFunctionPatterns(bool?[,] modules, bool[,] function)
        {
            DrawFinder(modules, function, 0, 0);
            DrawFinder(modules, function, Size - 7, 0);
            DrawFinder(modules, function, 0, Size - 7);
            DrawAlignment(modules, function, 30, 30);

            for (var i = 8; i < Size - 8; i++)
            {
                SetFunction(modules, function, i, 6, i % 2 == 0);
                SetFunction(modules, function, 6, i, i % 2 == 0);
            }

            SetFunction(modules, function, 8, Version * 4 + 9, true);
            ReserveFormatAreas(modules, function);
        }

        private static void DrawFinder(bool?[,] modules, bool[,] function, int left, int top)
        {
            for (var dy = -1; dy <= 7; dy++)
            {
                for (var dx = -1; dx <= 7; dx++)
                {
                    var x = left + dx;
                    var y = top + dy;
                    if (x < 0 || x >= Size || y < 0 || y >= Size)
                    {
                        continue;
                    }

                    var dark = dx >= 0 && dx <= 6 && dy >= 0 && dy <= 6 &&
                               (dx == 0 || dx == 6 || dy == 0 || dy == 6 ||
                                (dx >= 2 && dx <= 4 && dy >= 2 && dy <= 4));
                    SetFunction(modules, function, x, y, dark);
                }
            }
        }

        private static void DrawAlignment(bool?[,] modules, bool[,] function, int centerX, int centerY)
        {
            for (var dy = -2; dy <= 2; dy++)
            {
                for (var dx = -2; dx <= 2; dx++)
                {
                    var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    SetFunction(modules, function, centerX + dx, centerY + dy, distance != 1);
                }
            }
        }

        private static void ReserveFormatAreas(bool?[,] modules, bool[,] function)
        {
            for (var i = 0; i <= 8; i++)
            {
                if (i != 6)
                {
                    SetFunction(modules, function, 8, i, false);
                    SetFunction(modules, function, i, 8, false);
                }
            }

            for (var i = 0; i < 8; i++)
            {
                SetFunction(modules, function, Size - 1 - i, 8, false);
                SetFunction(modules, function, 8, Size - 1 - i, false);
            }
        }

        private static void DrawData(bool?[,] modules, bool[,] function, IReadOnlyList<int> bits)
        {
            var bitIndex = 0;
            var upward = true;

            for (var right = Size - 1; right >= 1; right -= 2)
            {
                if (right == 6)
                {
                    right--;
                }

                for (var vert = 0; vert < Size; vert++)
                {
                    var y = upward ? Size - 1 - vert : vert;
                    for (var dx = 0; dx < 2; dx++)
                    {
                        var x = right - dx;
                        if (function[y, x])
                        {
                            continue;
                        }

                        var dark = bitIndex < bits.Count && bits[bitIndex] == 1;
                        bitIndex++;

                        if (((x + y) & 1) == 0)
                        {
                            dark = !dark;
                        }

                        modules[y, x] = dark;
                    }
                }

                upward = !upward;
            }
        }

        private static void DrawFormatBits(bool?[,] modules, bool[,] function)
        {
            var format = CalculateFormatBits();
            for (var i = 0; i <= 5; i++)
            {
                SetFunction(modules, function, 8, i, Bit(format, i));
            }

            SetFunction(modules, function, 8, 7, Bit(format, 6));
            SetFunction(modules, function, 8, 8, Bit(format, 7));
            SetFunction(modules, function, 7, 8, Bit(format, 8));

            for (var i = 9; i < 15; i++)
            {
                SetFunction(modules, function, 14 - i, 8, Bit(format, i));
            }

            for (var i = 0; i < 8; i++)
            {
                SetFunction(modules, function, Size - 1 - i, 8, Bit(format, i));
            }

            for (var i = 8; i < 15; i++)
            {
                SetFunction(modules, function, 8, Size - 15 + i, Bit(format, i));
            }
        }

        private static int CalculateFormatBits()
        {
            var data = (0b01 << 3) | MaskPattern;
            var value = data << 10;
            for (var i = 14; i >= 10; i--)
            {
                if (((value >> i) & 1) != 0)
                {
                    value ^= 0x537 << (i - 10);
                }
            }

            return ((data << 10) | value) ^ 0x5412;
        }

        private static bool Bit(int value, int index) => ((value >> index) & 1) != 0;

        private static void SetFunction(bool?[,] modules, bool[,] function, int x, int y, bool dark)
        {
            modules[y, x] = dark;
            function[y, x] = true;
        }

        private static void AppendBits(ICollection<int> bits, int value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                bits.Add((value >> i) & 1);
            }
        }
    }
}
