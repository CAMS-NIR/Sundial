// Sundial (Windows 版) — 配色与绘制小工具
//
// 移植自 macOS 版 Theme.swift。颜色数值一个不改：这些 RGB 是在浅/深两套外观下
// 逐个量过对比度才定下来的，改一位就可能掉到 WCAG 线下。

using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace Sundial.App;

/// <summary>
/// 统一配色。整套只有三组：
/// ① 珊瑚族——太阳本体、上下文条、等待/未读的圆点、刷新光波
/// ② 两个仪表的强调色——蜜金（左）与杏粉（右），以及它们更亮的发光版
/// ③ 系统语义色与中性灰
/// 原先还有一套「鼠尾草绿／琥珀／砖红」的用量三档，圆环改固定色后它只剩两处小元素在用，
/// 等于为它们养着一整套色相——已并入珊瑚族删除。
/// </summary>
public static class Theme
{
    // macOS 版用 NSColor 的动态颜色，系统切换外观时自动重解析。
    // Avalonia 没有能在 DrawingContext 里就地解析的等价物，只能把「当前是不是深色」
    // 提成一个开关，由窗口层在主题变化时写入，绘制时统一查它。
    public static bool IsDark { get; set; }

    // 系统「提高对比度」。macOS 版 PetView 里同样只是存着，绘制没用到，
    // 保留是为了两边字段对得上。
    public static bool IncreaseContrast { get; set; }

    // MARK: 吉祥物（不随外观变化，太阳在深浅底上都是这个暖珊瑚）
    public static readonly Color CoralLight = Color.FromRgb(233, 152, 115); // #E99873
    public static readonly Color CoralDeep = Color.FromRgb(196, 103, 69);   // #C46745
    public static readonly Color SleepLight = Color.FromRgb(175, 169, 163);
    public static readonly Color SleepDeep = Color.FromRgb(140, 134, 128);
    // 睡着时身体压暗的目标色：偏暖的深灰，不能用 SunDeepen（深砖红）——
    // 灰身体掺红会显得病恹恹，正是之前踩过的坑
    public static readonly Color SleepDeepen = Color.FromRgb(77, 71, 66);
    public static readonly Color FaceDark = Color.FromRgb(37, 27, 22);

    /// <summary>身体加深用的目标色：固定的深砖红。必须是固定值——早先用过一个随明暗
    /// 切换的红，深色模式下它反而更亮，于是越紧张身体越浅，正好反了。</summary>
    public static Color SunDeepen => Color.FromRgb(139, 40, 29);

    public static Color ClaudeOrange => CoralDeep;

    // MARK: 语义色
    // macOS 的 labelColor / secondaryLabelColor / tertiaryLabelColor / windowBackgroundColor
    // 在 Windows 上没有对应物，按 macOS 的实际取值抄成固定值。
    // 注意这些颜色**自带 alpha**，原版的 withAlphaComponent 是「替换」而不是「相乘」，
    // 所以 WithAlpha 也照样是替换。
    public static Color LabelColor => IsDark ? Color.FromArgb(217, 255, 255, 255) : Color.FromArgb(217, 0, 0, 0);
    public static Color SecondaryLabelColor => IsDark ? Color.FromArgb(140, 255, 255, 255) : Color.FromArgb(128, 0, 0, 0);
    public static Color TertiaryLabelColor => IsDark ? Color.FromArgb(64, 255, 255, 255) : Color.FromArgb(66, 0, 0, 0);
    public static Color WindowBackground => IsDark ? Color.FromRgb(50, 50, 50) : Color.FromRgb(236, 236, 236);

    // MARK: 两个仪表的固定色 + 那一侧光芒尖端的发光色
    //
    // 关键教训：光芒的渐变必须**越往外越亮**。之前渐变到深酒红/深紫，
    // 深色压在暖色身体上，看着就是一块淤青——「太阳生病了」。
    // 太阳是发光体，光芒越往尖上越亮才对。所以每侧两个色：
    // 环用饱和一点的（要在玻璃上够 3:1），光芒尖用更亮的（画在太阳身上，不受背景约束）。
    //
    // 不再按用量换色：那件事由中间的数字、弧长和太阳的表情/身体深浅一起报。
    // 左右各一个色成了身份标识——不用读标签就知道哪边是哪个。
    /// <summary>左侧仪表（5 小时）——蜜金。浅色档压暗到刚好过 3:1。</summary>
    public static Color RingLeft => IsDark ? Color.FromRgb(242, 186, 76) : Color.FromRgb(188, 118, 18);
    public static Color GlowLeft => IsDark ? Color.FromRgb(255, 218, 146) : Color.FromRgb(255, 206, 110);
    /// <summary>右侧仪表（每周）——杏粉。</summary>
    public static Color RingRight => IsDark ? Color.FromRgb(236, 139, 150) : Color.FromRgb(198, 84, 96);
    public static Color GlowRight => IsDark ? Color.FromRgb(255, 182, 187) : Color.FromRgb(255, 158, 164);


    /// <summary>替换 alpha（对齐 NSColor.withAlphaComponent 的语义：替换，不是相乘）。</summary>
    public static Color WithAlpha(Color c, double a) =>
        Color.FromArgb((byte)Math.Clamp(Math.Round(a * 255), 0, 255), c.R, c.G, c.B);

    /// <summary>对齐 NSColor.blended(withFraction:of:)：逐分量线性插值。</summary>
    public static Color Blend(Color c, double fraction, Color other)
    {
        var f = Math.Clamp(fraction, 0, 1);
        static byte Mix(byte a, byte b, double f) => (byte)Math.Clamp(Math.Round(a + (b - a) * f), 0, 255);
        return Color.FromArgb(Mix(c.A, other.A, f), Mix(c.R, other.R, f),
                              Mix(c.G, other.G, f), Mix(c.B, other.B, f));
    }

    /// <summary>缓入缓出，用于所有动画过渡。</summary>
    public static double EaseInOut(double x)
    {
        var t = Math.Clamp(x, 0, 1);
        return t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
    }

    /// <summary>指数平滑：让数值变化连续跟随而不是瞬间跳变。</summary>
    public static double SmoothStep(double current, double target, double dt, double rate = 6)
    {
        var k = 1 - Math.Exp(-rate * dt);
        return current + (target - current) * k;
    }

    // MARK: 文字
    // 等宽数字：macOS 用 monospacedDigitSystemFont（只有数字等宽，字母仍是系统字体）。
    // Windows 上没有这个概念，退而求其次挑一款等宽字体；写成逗号分隔的候选列表，
    // 让 Avalonia 自己回退（Windows 上是 Consolas，Mac 上跑测试时是 Menlo）。
    private static readonly FontFamily MonoFamily = new("Consolas, Menlo, Courier New");

    /// <summary>
    /// 对齐 Swift 版的 drawText(_:in:font:color:align:lineBreak:)：
    /// 文字从 rect 的**左上角**开始画（原视图 isFlipped，Avalonia 同样是 y 向下，语义一致），
    /// 宽度即换行/截断宽度。
    /// </summary>
    public static void DrawText(DrawingContext ctx, string text, Rect rect,
                                double size, FontWeight weight, Color color,
                                TextAlignment align = TextAlignment.Left,
                                bool wrap = false, bool monoDigits = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        var ft = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(monoDigits ? MonoFamily : FontFamily.Default, FontStyle.Normal, weight),
            size,
            new SolidColorBrush(color))
        {
            MaxTextWidth = Math.Max(1, rect.Width),
            TextAlignment = align,
            // 默认单行截断（对齐 NSLineBreakMode.byTruncatingTail）；只有错误文案要换行
            Trimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        };
        if (!wrap)
        {
            ft.MaxLineCount = 1;          // Avalonia 叫 MaxLineCount，不是 WPF 那个 MaxLines
        }
        else
        {
            // NSString.draw(in:) 会把超出矩形的部分裁掉，Avalonia 默认只限宽不限高，
            // 换行后的错误文案能长到 65pt，直接盖住下面的「双击登录」按钮。
            // 只在换行分支限高：单行那些矩形高度是按 macOS 行高手调的（好几个只有 11–12pt，
            // 比 Windows 字体的行高还矮），一并限高会把整行文字直接吃掉。
            ft.MaxTextHeight = Math.Max(1, rect.Height);
        }
        ctx.DrawText(ft, new Point(rect.X, rect.Y));
    }
}
