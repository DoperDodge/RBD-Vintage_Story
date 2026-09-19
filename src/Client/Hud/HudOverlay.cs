using System;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace Shinimodori.Client
{
    /// <summary>
    /// The HUD, which by default does not exist (§12.6).
    ///
    /// "diegetic" shows nothing at all — the player uses /rbd status or infers it from
    /// how the world is treating them. "minimal" adds a scent glyph and a death count
    /// that appears for three seconds after an arrival. "full" is for streamers.
    /// </summary>
    public class HudOverlay : IRenderer
    {
        private readonly ICoreClientAPI capi;
        private readonly ShinimodoriClient client;

        private LoadedTexture textTexture;
        private string lastText = "";

        public double RenderOrder => 0.95;
        public int RenderRange => 0;

        public HudOverlay(ICoreClientAPI capi, ShinimodoriClient client)
        {
            this.capi = capi;
            this.client = client;
        }

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            if (stage != EnumRenderStage.Ortho) return;
            if (!client.Blessed) return;

            string mode = client.Cfg.Visuals.HudMode;
            if (mode == "diegetic")
            {
                // Even here, the death counter flashes briefly after an arrival if the
                // player asked for it — it is the one number that is about them.
                if (client.ShowDeathCounter) DrawText(FormatDeaths(), 0.5f, 0.12f, 0.6f);
                return;
            }

            if (mode == "minimal")
            {
                DrawScentGlyph();
                if (client.ShowDeathCounter) DrawText(FormatDeaths(), 0.5f, 0.12f, 0.8f);
                return;
            }

            DrawText(FormatFull(), 0.02f, 0.35f, 1f, alignLeft: true);
            DrawScentGlyph();
        }

        private string FormatDeaths() =>
            Lang.Get("shinimodori:hud-deaths", client.TotalDeaths, client.DeathsAtAnchor);

        private string FormatFull() =>
            Lang.Get("shinimodori:hud-full",
                client.TotalDeaths, client.DeathsAtAnchor,
                client.Miasma.ToString("F0"), client.MiasmaTier,
                client.Despair.ToString("F0"),
                client.AnchorAgeHours.ToString("F1"));

        /// <summary>
        /// A small dark mark beside the stability meter that fills as the scent grows.
        /// It never says what it is.
        /// </summary>
        private void DrawScentGlyph()
        {
            if (client.MiasmaTier <= 0) return;
            var tex = client.Textures;
            if (tex.Hand == 0) return;

            float size = capi.Render.FrameHeight * 0.035f;
            float x = capi.Render.FrameWidth * 0.5f + size * 3.2f;
            float y = capi.Render.FrameHeight - size * 2.4f;
            float fill = Math.Min(1f, client.MiasmaTier / 4f);

            capi.Render.GlToggleBlend(true);
            capi.Render.Render2DTexture(tex.Hand, x, y, size * 0.5f, size,
                10000f, new Vec4f(0.1f, 0.05f, 0.12f, 0.25f + fill * 0.7f));
        }

        private void DrawText(string text, float xFrac, float yFrac, float alpha, bool alignLeft = false)
        {
            if (string.IsNullOrEmpty(text)) return;

            try
            {
                if (textTexture == null || lastText != text)
                {
                    lastText = text;
                    textTexture?.Dispose();
                    var font = CairoFont.WhiteSmallText().WithFontSize(16);
                    textTexture = capi.Gui.TextTexture.GenTextTexture(text, font);
                }

                float x = capi.Render.FrameWidth * xFrac - (alignLeft ? 0 : textTexture.Width / 2f);
                float y = capi.Render.FrameHeight * yFrac;
                capi.Render.GlToggleBlend(true);
                capi.Render.Render2DTexturePremultipliedAlpha(textTexture.TextureId, x, y,
                    textTexture.Width, textTexture.Height, 10000f, new Vec4f(1, 1, 1, alpha));
            }
            catch (Exception e)
            {
                capi.Logger.Debug("[shinimodori] HUD text failed: {0}", e.Message);
            }
        }

        public void Dispose()
        {
            textTexture?.Dispose();
            textTexture = null;
        }
    }
}
