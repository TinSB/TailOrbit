// 二维码（ISO/IEC 18004）：字节模式、纠错等级 M、版本 1～10（最多 213 字节），放分享链接够用；八种掩码里挑罚分最低的
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace TailOrbit
{
    static class Qr
    {
        // 纠错等级 M，下标是版本号：总码字数、纠错块数、每块纠错码字数、校正图形的中心坐标
        static readonly int[] Raw = { 0, 26, 44, 70, 100, 134, 172, 196, 242, 292, 346 };
        static readonly int[] Blocks = { 0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5 };
        static readonly int[] EccLen = { 0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };
        static readonly int[][] Align =
        {
            null, new int[0], new[] { 6, 18 }, new[] { 6, 22 }, new[] { 6, 26 }, new[] { 6, 30 },
            new[] { 6, 34 }, new[] { 6, 22, 38 }, new[] { 6, 24, 42 }, new[] { 6, 26, 46 }, new[] { 6, 28, 50 }
        };

        // 模块矩阵 [行, 列]，true 是深色；太长放不下返回 null
        public static bool[,] Encode(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            int ver = 1;
            while (ver <= 10 && 12 + (ver < 10 ? 0 : 8) + bytes.Length * 8 > DataLen(ver) * 8) ver++;
            if (ver > 10) return null;
            int size = ver * 4 + 17;
            var m = new bool[size, size];
            var fn = new bool[size, size];   // 功能区：定位、时序、校正、格式和版本信息，数据不放这里
            Functions(m, fn, ver);
            Place(m, fn, Interleave(Pack(bytes, ver), ver));
            int best = 0, low = int.MaxValue;
            for (int k = 0; k < 8; k++)
            {
                Mask(m, fn, k);
                Format(m, fn, k);
                int score = Penalty(m);
                if (score < low) { low = score; best = k; }
                Mask(m, fn, k);   // 再异或一次就还原
            }
            Mask(m, fn, best);
            Format(m, fn, best);
            return m;
        }

        static int DataLen(int ver)
        {
            return Raw[ver] - Blocks[ver] * EccLen[ver];
        }

        // 模式、长度、内容，再补结束符和填充字节
        static byte[] Pack(byte[] bytes, int ver)
        {
            var bits = new List<bool>();
            Action<int, int> put = (v, n) => { for (int i = n - 1; i >= 0; i--) bits.Add(((v >> i) & 1) != 0); };
            put(4, 4);
            put(bytes.Length, ver < 10 ? 8 : 16);
            foreach (byte b in bytes) put(b, 8);
            put(0, Math.Min(4, DataLen(ver) * 8 - bits.Count));
            put(0, (8 - bits.Count % 8) % 8);
            var data = new byte[DataLen(ver)];
            for (int i = 0; i < bits.Count; i++)
                if (bits[i]) data[i >> 3] |= (byte)(0x80 >> (i & 7));
            for (int i = bits.Count / 8, k = 0; i < data.Length; i++, k++) data[i] = (byte)(k % 2 == 0 ? 0xEC : 0x11);
            return data;
        }

        // 分块各算纠错码，再按列交错：先排各块的数据，再排各块的纠错码
        static byte[] Interleave(byte[] data, int ver)
        {
            int nb = Blocks[ver], ecc = EccLen[ver], raw = Raw[ver];
            int shortCount = nb - raw % nb, shortLen = raw / nb;   // 短块的总长（数据＋纠错）；长块多一个数据字节
            var div = Divisor(ecc);
            var blocks = new List<byte[]>();
            for (int i = 0, k = 0; i < nb; i++)
            {
                int len = shortLen - ecc + (i < shortCount ? 0 : 1);
                var dat = new byte[len];
                Array.Copy(data, k, dat, 0, len);
                k += len;
                var block = new byte[shortLen + 1];
                Array.Copy(dat, block, len);
                Array.Copy(Remainder(dat, div), 0, block, block.Length - ecc, ecc);
                blocks.Add(block);
            }
            var result = new byte[raw];
            int n = 0;
            for (int i = 0; i <= shortLen; i++)
                for (int j = 0; j < nb; j++)
                    if (i != shortLen - ecc || j >= shortCount) result[n++] = blocks[j][i];   // 短块在这一格是空的
            return result;
        }

        // GF(256) 乘法，本原多项式 0x11D
        static int Mul(int x, int y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return z;
        }

        static byte[] Divisor(int degree)
        {
            var r = new byte[degree];
            r[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < degree; j++)
                {
                    r[j] = (byte)Mul(r[j], root);
                    if (j + 1 < degree) r[j] ^= r[j + 1];
                }
                root = Mul(root, 2);
            }
            return r;
        }

        static byte[] Remainder(byte[] data, byte[] div)
        {
            var r = new byte[div.Length];
            foreach (byte b in data)
            {
                int f = b ^ r[0];
                Array.Copy(r, 1, r, 0, r.Length - 1);
                r[r.Length - 1] = 0;
                for (int i = 0; i < r.Length; i++) r[i] ^= (byte)Mul(div[i], f);
            }
            return r;
        }

        static void Functions(bool[,] m, bool[,] fn, int ver)
        {
            int size = m.GetLength(0);
            for (int i = 0; i < size; i++)
            {
                Set(m, fn, 6, i, i % 2 == 0);   // 时序：第 6 行、第 6 列明暗相间
                Set(m, fn, i, 6, i % 2 == 0);
            }
            Finder(m, fn, 3, 3);
            Finder(m, fn, 3, size - 4);
            Finder(m, fn, size - 4, 3);
            var a = Align[ver];
            for (int i = 0; i < a.Length; i++)
                for (int j = 0; j < a.Length; j++)
                {
                    if ((i == 0 && j == 0) || (i == 0 && j == a.Length - 1) || (i == a.Length - 1 && j == 0)) continue;   // 跟定位图形重叠的三个角不放
                    for (int dr = -2; dr <= 2; dr++)
                        for (int dc = -2; dc <= 2; dc++)
                            Set(m, fn, a[i] + dr, a[j] + dc, Math.Max(Math.Abs(dr), Math.Abs(dc)) != 1);
                }
            Format(m, fn, 0);   // 先占住格式信息的位置，挑好掩码再写真的
            if (ver < 7) return;
            int rem = ver;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            int bits = ver << 12 | rem;
            for (int i = 0; i < 18; i++)
            {
                bool bit = ((bits >> i) & 1) != 0;
                int far = size - 11 + i % 3, near = i / 3;
                Set(m, fn, near, far, bit);   // 右上角
                Set(m, fn, far, near, bit);   // 左下角
            }
        }

        // 定位图形：7×7 的回字，外面一圈白边
        static void Finder(bool[,] m, bool[,] fn, int row, int col)
        {
            int size = m.GetLength(0);
            for (int dr = -4; dr <= 4; dr++)
                for (int dc = -4; dc <= 4; dc++)
                {
                    int r = row + dr, c = col + dc, d = Math.Max(Math.Abs(dr), Math.Abs(dc));
                    if (r >= 0 && r < size && c >= 0 && c < size) Set(m, fn, r, c, d != 2 && d != 4);
                }
        }

        // 格式信息：纠错等级 M（两位 00）＋掩码号，BCH 校验后异或 0x5412；左上一份，右上和左下拼成另一份
        static void Format(bool[,] m, bool[,] fn, int mask)
        {
            int size = m.GetLength(0), rem = mask;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = (mask << 10 | rem) ^ 0x5412;
            Func<int, bool> bit = i => ((bits >> i) & 1) != 0;
            for (int i = 0; i <= 5; i++) Set(m, fn, i, 8, bit(i));
            Set(m, fn, 7, 8, bit(6));
            Set(m, fn, 8, 8, bit(7));
            Set(m, fn, 8, 7, bit(8));
            for (int i = 9; i < 15; i++) Set(m, fn, 8, 14 - i, bit(i));
            for (int i = 0; i < 8; i++) Set(m, fn, 8, size - 1 - i, bit(i));
            for (int i = 8; i < 15; i++) Set(m, fn, size - 15 + i, 8, bit(i));
            Set(m, fn, size - 8, 8, true);   // 固定的一个深色点
        }

        // 数据从右下角开始，两列一组之字形上下走，跳过第 6 列的时序线和功能区
        static void Place(bool[,] m, bool[,] fn, byte[] code)
        {
            int size = m.GetLength(0), i = 0;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                bool up = ((right + 1) & 2) == 0;
                for (int v = 0; v < size; v++)
                    for (int j = 0; j < 2; j++)
                    {
                        int col = right - j, row = up ? size - 1 - v : v;
                        if (fn[row, col] || i >= code.Length * 8) continue;
                        m[row, col] = ((code[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                        i++;
                    }
            }
        }

        static void Mask(bool[,] m, bool[,] fn, int k)
        {
            int size = m.GetLength(0);
            for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++)
                {
                    if (fn[r, c]) continue;
                    bool flip;
                    switch (k)
                    {
                        case 0: flip = (r + c) % 2 == 0; break;
                        case 1: flip = r % 2 == 0; break;
                        case 2: flip = c % 3 == 0; break;
                        case 3: flip = (r + c) % 3 == 0; break;
                        case 4: flip = (r / 2 + c / 3) % 2 == 0; break;
                        case 5: flip = r * c % 2 + r * c % 3 == 0; break;
                        case 6: flip = (r * c % 2 + r * c % 3) % 2 == 0; break;
                        default: flip = ((r + c) % 2 + r * c % 3) % 2 == 0; break;
                    }
                    if (flip) m[r, c] = !m[r, c];
                }
        }

        // 罚分：同色连成长条、2×2 同色块、像定位图形的 1:1:3:1:1、深浅比例偏离一半
        static int Penalty(bool[,] m)
        {
            int size = m.GetLength(0), score = 0, dark = 0;
            for (int pass = 0; pass < 2; pass++)
                for (int a = 0; a < size; a++)
                {
                    int run = 0;
                    bool last = false;
                    for (int b = 0; b < size; b++)
                    {
                        bool cur = At(m, pass, a, b);
                        if (b > 0 && cur == last) run++;
                        else
                        {
                            if (run >= 5) score += run - 2;
                            run = 1;
                            last = cur;
                        }
                        if (b + 6 < size && cur && !At(m, pass, a, b + 1) && At(m, pass, a, b + 2) && At(m, pass, a, b + 3) && At(m, pass, a, b + 4) && !At(m, pass, a, b + 5) && At(m, pass, a, b + 6)
                            && (Light(m, pass, a, b - 4, b) || Light(m, pass, a, b + 7, b + 11))) score += 40;
                    }
                    if (run >= 5) score += run - 2;
                }
            for (int r = 0; r + 1 < size; r++)
                for (int c = 0; c + 1 < size; c++)
                    if (m[r, c] == m[r, c + 1] && m[r, c] == m[r + 1, c] && m[r, c] == m[r + 1, c + 1]) score += 3;
            foreach (bool v in m) if (v) dark++;
            int total = size * size;
            return score + Math.Abs(dark * 20 - total * 10) / total * 10;
        }

        static bool At(bool[,] m, int pass, int a, int b)
        {
            return pass == 0 ? m[a, b] : m[b, a];
        }

        // [from, to) 这一段全是浅色（码外面算浅色）
        static bool Light(bool[,] m, int pass, int a, int from, int to)
        {
            int size = m.GetLength(0);
            for (int i = from; i < to; i++)
                if (i >= 0 && i < size && At(m, pass, a, i)) return false;
            return true;
        }

        static void Set(bool[,] m, bool[,] fn, int r, int c, bool dark)
        {
            m[r, c] = dark;
            fn[r, c] = true;
        }
    }

    // 画二维码：格子对齐到整数个屏幕像素，每行连着的深色格合成一个长条，边缘不糊、不留缝
    class QrView : FrameworkElement
    {
        readonly bool[,] m;
        readonly Brush ink;

        public QrView(bool[,] modules, Brush ink)
        {
            m = modules;
            this.ink = ink;
            SnapsToDevicePixels = true;
            RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        }

        protected override void OnRender(DrawingContext dc)
        {
            int n = m.GetLength(0);
            double side = Math.Min(ActualWidth, ActualHeight), dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double cell = Math.Max(1, Math.Floor(side * dpi / n)) / dpi;
            double off = Math.Floor((side - cell * n) / 2 * dpi) / dpi;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
                for (int r = 0; r < n; r++)
                    for (int c = 0; c < n; c++)
                    {
                        if (!m[r, c]) continue;
                        int end = c;
                        while (end + 1 < n && m[r, end + 1]) end++;
                        double x0 = off + c * cell, x1 = off + (end + 1) * cell, y0 = off + r * cell, y1 = y0 + cell;
                        ctx.BeginFigure(new Point(x0, y0), true, true);
                        ctx.LineTo(new Point(x1, y0), false, false);
                        ctx.LineTo(new Point(x1, y1), false, false);
                        ctx.LineTo(new Point(x0, y1), false, false);
                        c = end;
                    }
            g.Freeze();
            dc.DrawGeometry(ink, null, g);
        }
    }
}
