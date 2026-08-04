using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia.Platform;
using Sundial.App;
using Sundial.Core;

// 无显示器渲染：把 PetRenderer 画进位图存成 PNG
AppBuilder.Configure<Application>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

void Shot(int left, int right, bool busy, string path)
{
    var m = new PetModel { Loading = false };
    m.Rows = new List<UsageRow> {
        new("5 小时", left, DateTimeOffset.Now.AddHours(1), 0),
        new("每周 · Fable", right, DateTimeOffset.Now.AddDays(2), 1),
    };
    if (busy)
        m.Sessions = new List<SessionActivity> {
            new() { Id = "x", Title = "示例会话", Busy = true,
                    Since = DateTimeOffset.Now.AddSeconds(-95),
                    CtxTokens = 392_982, CtxLimit = 1_000_000 },
        };

    var r = new PetRenderer(m);
    for (int i = 0; i < 200; i++) r.Advance(0.033);

    var size = new Size(198, busy ? 136 : 88);
    var px = new PixelSize((int)size.Width * 2, (int)size.Height * 2);
    var rtb = new RenderTargetBitmap(px, new Vector(192, 192));
    using (var ctx = rtb.CreateDrawingContext())
    {
        ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(250, 248, 245)), null,
                          new Rect(0, 0, size.Width, size.Height));
        r.Render(ctx, new Rect(0, 0, size.Width, size.Height));
    }
    rtb.Save(path);
    Console.WriteLine($"  写出 {path}");
}

var outDir = args.Length > 0 ? args[0] : ".";
Console.WriteLine("渲染中…");
Shot(15, 10, true, Path.Combine(outDir, "win_a.png"));
Shot(70, 40, true, Path.Combine(outDir, "win_b.png"));
Shot(94, 60, true, Path.Combine(outDir, "win_c.png"));
Shot(94, 60, false, Path.Combine(outDir, "win_d.png"));
Console.WriteLine("完成");
