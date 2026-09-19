using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;

namespace Shinimodori.Client
{
    /// <summary>
    /// Draws every texture the mod needs at runtime with Cairo.
    ///
    /// Nothing is shipped as an image file, which keeps the mod free of any asset that
    /// could have come from somewhere it shouldn't (§0.4 of the plan), and means the
    /// effects scale cleanly to any resolution.
    /// </summary>
    public class TextureFactory : IDisposable
    {
        private readonly ICoreClientAPI capi;
        private readonly Dictionary<string, int> cache = new Dictionary<string, int>();

        public TextureFactory(ICoreClientAPI capi) { this.capi = capi; }

        /// <summary>A flat white quad, tinted at draw time. The workhorse of the overlay path.</summary>
        public int White => Get("white", 4, 4, ctx =>
        {
            ctx.SetSourceRGBA(1, 1, 1, 1);
            ctx.Paint();
        });

        /// <summary>A soft round dot: the motes drifting in the Void.</summary>
        public int Mote => Get("mote", 32, 32, ctx =>
        {
            using (var g = new RadialGradient(16, 16, 0, 16, 16, 16))
            {
                g.AddColorStop(0, new Color(1, 1, 1, 1));
                g.AddColorStop(0.35, new Color(1, 1, 1, 0.55));
                g.AddColorStop(1, new Color(1, 1, 1, 0));
                ctx.SetSource(g);
                ctx.Arc(16, 16, 16, 0, Math.PI * 2);
                ctx.Fill();
            }
        });

        /// <summary>A vignette: transparent in the middle, black at the edge.</summary>
        public int Vignette => Get("vignette", 256, 256, ctx =>
        {
            using (var g = new RadialGradient(128, 128, 40, 128, 128, 170))
            {
                g.AddColorStop(0, new Color(0, 0, 0, 0));
                g.AddColorStop(0.65, new Color(0, 0, 0, 0.35));
                g.AddColorStop(1, new Color(0, 0, 0, 1));
                ctx.SetSource(g);
                ctx.Paint();
            }
        });

        /// <summary>
        /// A four-fingered hand, reaching. Drawn rather than traced, and deliberately
        /// wrong: too few fingers, too long, no thumb where one should be.
        /// </summary>
        public int Hand => Get("hand", 128, 256, ctx =>
        {
            ctx.SetSourceRGBA(0, 0, 0, 1);

            // Palm
            ctx.MoveTo(34, 256);
            ctx.CurveTo(18, 200, 20, 168, 40, 150);
            ctx.CurveTo(70, 132, 96, 140, 104, 162);
            ctx.CurveTo(112, 190, 104, 226, 94, 256);
            ctx.ClosePath();
            ctx.Fill();

            // Four fingers, each a different length, each slightly bent.
            DrawFinger(ctx, 40, 152, 30, 14);
            DrawFinger(ctx, 60, 146, 12, 16);
            DrawFinger(ctx, 80, 148, 6, 15);
            DrawFinger(ctx, 98, 156, 46, 12);
        });

        /// <summary>A clock face with no numbers, for the rewind overlay.</summary>
        public int ClockFace => Get("clock", 256, 256, ctx =>
        {
            ctx.SetSourceRGBA(1, 1, 1, 0.9);
            ctx.LineWidth = 3;
            ctx.Arc(128, 128, 110, 0, Math.PI * 2);
            ctx.Stroke();

            for (int i = 0; i < 12; i++)
            {
                double a = i * Math.PI / 6;
                double inner = i % 3 == 0 ? 88 : 98;
                ctx.MoveTo(128 + Math.Cos(a) * inner, 128 + Math.Sin(a) * inner);
                ctx.LineTo(128 + Math.Cos(a) * 108, 128 + Math.Sin(a) * 108);
                ctx.LineWidth = i % 3 == 0 ? 5 : 2;
                ctx.Stroke();
            }
        });

        /// <summary>A vertical smear, for the rewind's frame tears.</summary>
        public int Scanline => Get("scanline", 8, 64, ctx =>
        {
            using (var g = new LinearGradient(0, 0, 0, 64))
            {
                g.AddColorStop(0, new Color(1, 1, 1, 0));
                g.AddColorStop(0.5, new Color(1, 1, 1, 0.5));
                g.AddColorStop(1, new Color(1, 1, 1, 0));
                ctx.SetSource(g);
                ctx.Paint();
            }
        });

        /// <summary>
        /// A figure at the edge of sight: a pale silhouette that never resolves.
        /// Small, low contrast, and gone before the camera turns (§12.5).
        /// </summary>
        public int Silhouette => Get("silhouette", 128, 256, ctx =>
        {
            using (var g = new LinearGradient(0, 0, 0, 256))
            {
                g.AddColorStop(0, new Color(1, 1, 1, 0.85));
                g.AddColorStop(1, new Color(1, 1, 1, 0.1));
                ctx.SetSource(g);
            }
            ctx.MoveTo(64, 26);
            ctx.CurveTo(84, 26, 88, 52, 80, 70);      // head and shoulders
            ctx.CurveTo(108, 86, 114, 140, 108, 256);
            ctx.LineTo(20, 256);
            ctx.CurveTo(14, 140, 20, 86, 48, 70);
            ctx.CurveTo(40, 52, 44, 26, 64, 26);
            ctx.ClosePath();
            ctx.Fill();
        });

        /// <summary>A soft horizontal band, used for blood-like runs and tendrils.</summary>
        public int Tendril => Get("tendril", 32, 256, ctx =>
        {
            using (var g = new LinearGradient(0, 0, 0, 256))
            {
                g.AddColorStop(0, new Color(0, 0, 0, 0.95));
                g.AddColorStop(0.7, new Color(0, 0, 0, 0.5));
                g.AddColorStop(1, new Color(0, 0, 0, 0));
                ctx.SetSource(g);
            }
            ctx.MoveTo(16, 0);
            ctx.CurveTo(2, 70, 30, 140, 14, 210);
            ctx.CurveTo(12, 230, 18, 244, 16, 256);
            ctx.LineWidth = 13;
            ctx.LineCap = LineCap.Round;
            ctx.Stroke();
        });

        private static void DrawFinger(Context ctx, double x, double y, double bend, double width)
        {
            ctx.MoveTo(x, y);
            ctx.CurveTo(x - bend * 0.4, y - 48, x + bend * 0.5, y - 92, x + bend, y - 128);
            ctx.LineWidth = width;
            ctx.LineCap = LineCap.Round;
            ctx.Stroke();
        }

        private int Get(string key, int w, int h, Action<Context> draw)
        {
            if (cache.TryGetValue(key, out int id)) return id;

            try
            {
                using (var surface = new ImageSurface(Format.Argb32, w, h))
                using (var ctx = new Context(surface))
                {
                    ctx.Operator = Operator.Source;
                    ctx.SetSourceRGBA(0, 0, 0, 0);
                    ctx.Paint();
                    ctx.Operator = Operator.Over;
                    draw(ctx);
                    surface.Flush();
                    id = capi.Gui.LoadCairoTexture(surface, true);
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[shinimodori] Could not draw texture '{0}': {1}. " +
                                    "That effect will be skipped.", key, e.Message);
                id = 0;
            }

            cache[key] = id;
            return id;
        }

        public void Dispose()
        {
            foreach (var id in cache.Values)
                if (id != 0) { try { capi.Gui.DeleteTexture(id); } catch { } }
            cache.Clear();
        }
    }
}
