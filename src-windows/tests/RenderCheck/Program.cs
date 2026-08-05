using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia.Platform;
using Sundial.App;
using Sundial.Core;

// Headless rendering: draw PetRenderer into a bitmap and save it out as a PNG
AppBuilder.Configure<Application>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

void Shot(int left, int right, bool busy, string path, bool hover = false, int ctxPct = 39, bool unread = false)
{
    var m = new PetModel { Loading = false, Hovered = hover };
    m.Rows = new List<UsageRow> {
        new("5 hours", left, DateTimeOffset.Now.AddHours(1), 0),
        new("Weekly · Fable", right, DateTimeOffset.Now.AddDays(2), 1),
    };
    if (busy || unread)
        m.Sessions = new List<SessionActivity> {
            new() { Id = "x", Title = "Example session", Busy = busy, Unread = unread,
                    Since = DateTimeOffset.Now.AddSeconds(-95),
                    FinishedAt = unread ? DateTimeOffset.Now.AddSeconds(-90) : null,
                    CtxTokens = ctxPct * 10_000, CtxLimit = 1_000_000 },
        };

    var r = new PetRenderer(m);
    for (int i = 0; i < 200; i++) r.Advance(0.033);

    var size = new Size(198, (busy || unread) ? (hover ? 300 : 136) : 88);
    var px = new PixelSize((int)size.Width * 2, (int)size.Height * 2);
    var rtb = new RenderTargetBitmap(px, new Vector(192, 192));
    using (var ctx = rtb.CreateDrawingContext())
    {
        ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(250, 248, 245)), null,
                          new Rect(0, 0, size.Width, size.Height));
        r.Render(ctx, new Rect(0, 0, size.Width, size.Height));
    }
    rtb.Save(path);
    Console.WriteLine($"  wrote {path}");
}

var outDir = args.Length > 0 ? args[0] : ".";
Console.WriteLine("rendering…");
Shot(15, 10, true, Path.Combine(outDir, "win_a.png"));
Shot(70, 40, true, Path.Combine(outDir, "win_b.png"));
Shot(94, 60, true, Path.Combine(outDir, "win_c.png"));
Shot(94, 60, false, Path.Combine(outDir, "win_d.png"));
// The session dial: folded, open (dial centred with its caption), and the unread halo
Shot(8, 40, true, Path.Combine(outDir, "win_e.png"), hover: true, ctxPct: 8);
Shot(8, 40, true, Path.Combine(outDir, "win_f.png"), hover: true, ctxPct: 98);
Shot(8, 40, false, Path.Combine(outDir, "win_g.png"), unread: true, ctxPct: 62);
Console.WriteLine("done");
