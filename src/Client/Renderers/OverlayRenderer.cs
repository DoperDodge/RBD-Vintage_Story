using System;
using Shinimodori.Config;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace Shinimodori.Client.Renderers
{
    /// <summary>
    /// Draws every full-screen effect the mod owns (§12.1).
    ///
    /// Runs twice per frame: at <see cref="EnumRenderStage.AfterPostProcessing"/> to
    /// cover the world (the Void has to hide it completely), and at
    /// <see cref="EnumRenderStage.Ortho"/> for everything that sits over the HUD.
    ///
    /// Every effect here is composed from tinted quads, so it works identically with
    /// and without the custom shaders — the shader path only ever adds to this.
    /// </summary>
    public class OverlayRenderer : IRenderer
    {
        private readonly ICoreClientAPI capi;
        private readonly ShinimodoriClient client;
        private readonly TextureFactory tex;
        private ShinimodoriConfig Cfg => client.Cfg;

        private readonly Vec4f colour = new Vec4f();
        private float[] moteSeed;
        private float time;

        public double RenderOrder => 1.0;     // last, so nothing draws over the mod
        public int RenderRange => 0;

        public OverlayRenderer(ICoreClientAPI capi, ShinimodoriClient client, TextureFactory tex)
        {
            this.capi = capi;
            this.client = client;
            this.tex = tex;
        }

        private int W => capi.Render.FrameWidth;
        private int H => capi.Render.FrameHeight;
        private float Intensity => Cfg.Visuals.EffectIntensity;

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            if (Intensity <= 0) return;
            if (stage == EnumRenderStage.AfterPostProcessing) time += dt;

            var fx = client.Effects.Top;
            if (fx == null) return;

            try
            {
                capi.Render.GlToggleBlend(true);

                if (stage == EnumRenderStage.AfterPostProcessing) RenderWorldCovering(fx);
                else if (stage == EnumRenderStage.Ortho) RenderOverHud(fx);
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[shinimodori] overlay draw failed: {0}", e.Message);
            }
        }

        // ------------------------------------------------------- covering the world

        private void RenderWorldCovering(ActiveEffect fx)
        {
            switch (fx.Kind)
            {
                case EffectKind.ReturnSequence:
                    RenderReturnStage(fx);
                    break;
                case EffectKind.TeaPartyTransition:
                    Fill(1, 1, 1, Ease(fx.Progress) * 0.95f);
                    break;
            }
        }

        /// <summary>The four beats of a return, in order (§12.1 A–D).</summary>
        private void RenderReturnStage(ActiveEffect fx)
        {
            var stage = (Net.ReturnStage)fx.Phase;
            float p = fx.Progress;

            switch (stage)
            {
                case Net.ReturnStage.Dying:
                    // Desaturate toward 20% and darken. Without a shader, a grey wash
                    // over the frozen frame reads as the colour draining out.
                    Fill(0.13f, 0.13f, 0.14f, 0.8f * p * Intensity);
                    break;

                case Net.ReturnStage.Void:
                    // Pure black: the world is not merely dark, it is gone.
                    Fill(0, 0, 0, 1f);
                    DrawMotes(p);
                    DrawWitchSilhouette(p);
                    break;

                case Net.ReturnStage.TeaParty:
                    Fill(1, 1, 1, 1f);
                    break;

                case Net.ReturnStage.Rewind:
                    // The world comes back playing backwards: hard tears, channel
                    // separation, and a clock running the wrong way.
                    Fill(0, 0, 0, Math.Max(0f, 1f - p * 1.6f));
                    DrawFrameTears(p);
                    DrawReverseClock(p);
                    break;

                case Net.ReturnStage.Arrival:
                    // Blown-out white decaying to nothing over the first half.
                    float bloom = Math.Max(0f, 1f - p * 2.2f);
                    if (bloom > 0) Fill(1, 1, 1, bloom * 0.8f * Intensity);
                    break;
            }
        }

        private void DrawMotes(float p)
        {
            int count = Cfg.Visuals.VoidMoteCount;
            if (count <= 0 || tex.Mote == 0) return;

            if (moteSeed == null || moteSeed.Length < count * 4) BuildMoteSeeds(count);

            // Three parallax layers drifting up and slightly inward. They are stars,
            // and they are also her.
            for (int i = 0; i < count; i++)
            {
                float sx = moteSeed[i * 4];
                float sy = moteSeed[i * 4 + 1];
                float size = moteSeed[i * 4 + 2];
                float layer = moteSeed[i * 4 + 3];

                float speed = 0.02f + layer * 0.05f;
                float y = (sy - time * speed) % 1f;
                if (y < 0) y += 1f;

                float inward = (0.5f - sx) * layer * 0.06f * (1f - y);
                float x = sx + inward;

                float alpha = (0.25f + layer * 0.6f) * Math.Min(1f, p * 3f);
                float px = size * (4f + layer * 10f);

                Draw(tex.Mote, x * W - px / 2, y * H - px / 2, px, px, 1, 1, 1, alpha);
            }
        }

        private void BuildMoteSeeds(int count)
        {
            var rand = new Random(1215);   // fixed: the Void looks the same every time
            moteSeed = new float[count * 4];
            for (int i = 0; i < count; i++)
            {
                moteSeed[i * 4] = (float)rand.NextDouble();
                moteSeed[i * 4 + 1] = (float)rand.NextDouble();
                moteSeed[i * 4 + 2] = 0.4f + (float)rand.NextDouble();
                moteSeed[i * 4 + 3] = (float)rand.NextDouble();
            }
        }

        /// <summary>
        /// A pale shape, centre-left, at 6% opacity. It never resolves. Bystanders in
        /// multiplayer never see it at all (§13).
        /// </summary>
        private void DrawWitchSilhouette(float p)
        {
            if (client.VoidIsBystander || tex.Silhouette == 0) return;

            // Fades in at a third through, gone by four fifths.
            float a;
            if (p < 0.33f) a = p / 0.33f;
            else if (p > 0.73f) a = Math.Max(0f, 1f - (p - 0.73f) / 0.2f);
            else a = 1f;
            if (a <= 0) return;

            float h = H * 0.55f;
            float w = h * 0.5f;
            Draw(tex.Silhouette, W * 0.32f - w / 2, H * 0.34f, w, h, 1, 1, 1, a * 0.06f * Intensity);
        }

        private void DrawFrameTears(float p)
        {
            if (tex.White == 0) return;
            var rand = new Random((int)(time * 7) * 31 + client.EffectSeed);

            // Six to eight hard tears a second, each a displaced band of the frame.
            int tears = 2 + rand.Next(3);
            for (int i = 0; i < tears; i++)
            {
                float y = (float)rand.NextDouble() * H;
                float h = 4 + (float)rand.NextDouble() * (H * 0.07f);
                float shift = ((float)rand.NextDouble() - 0.5f) * W * 0.12f * (1f - p);

                // Per-channel offset: the RGB split of a tape being scrubbed.
                Draw(tex.White, shift, y, W, h, 1f, 0.1f, 0.1f, 0.10f * Intensity);
                Draw(tex.White, -shift * 0.6f, y, W, h, 0.1f, 0.4f, 1f, 0.10f * Intensity);
            }

            if (tex.Scanline == 0) return;
            for (int i = 0; i < 24; i++)
            {
                float y = (float)rand.NextDouble() * H;
                Draw(tex.Scanline, 0, y, W, 2 + (float)rand.NextDouble() * 6, 1, 1, 1, 0.05f * Intensity);
            }
        }

        private void DrawReverseClock(float p)
        {
            if (tex.ClockFace == 0) return;
            float size = Math.Min(W, H) * 0.42f;
            Draw(tex.ClockFace, (W - size) / 2, (H - size) / 2, size, size, 1, 1, 1, 0.12f * Intensity);

            // Hands sweeping counter-clockwise, accelerating.
            float sweep = -(p * p) * 14f;
            DrawHand(size * 0.36f, sweep, 3.5f);
            DrawHand(size * 0.26f, sweep / 12f, 5f);
        }

        private void DrawHand(float length, float angle, float thickness)
        {
            if (tex.White == 0) return;
            float cx = W / 2f, cy = H / 2f;
            // Approximated as a short chain of dots: no rotation is needed, and it
            // reads correctly at 12% opacity.
            int steps = (int)(length / 3);
            for (int i = 0; i < steps; i++)
            {
                float t = i / (float)steps * length;
                float x = cx + (float)Math.Sin(angle) * t;
                float y = cy - (float)Math.Cos(angle) * t;
                Draw(tex.White, x - thickness / 2, y - thickness / 2, thickness, thickness, 1, 1, 1, 0.12f * Intensity);
            }
        }

        // ------------------------------------------------------------ over the HUD

        private void RenderOverHud(ActiveEffect fx)
        {
            switch (fx.Kind)
            {
                case EffectKind.TabooGrip: RenderGrip(fx); break;
                case EffectKind.Breakdown: RenderBreakdown(fx); break;
                case EffectKind.DeathRecall: RenderRecall(fx); break;
                case EffectKind.DespairAmbient: RenderDespair(fx); break;
                case EffectKind.MiasmaAmbient: RenderMiasma(fx); break;
                case EffectKind.ReturnSequence: RenderArrivalVignette(fx); break;
            }
        }

        /// <summary>The grip (§7.3). The most expensive effect, and the most polished.</summary>
        private void RenderGrip(ActiveEffect fx)
        {
            int stage = fx.Phase;
            float p = fx.Progress;

            // Stage 1 desaturates to 15%; stage 2 goes to zero but keeps the red — the
            // show's grayscale-with-red framing, approximated by a grey wash plus a
            // red multiply that only the red channel survives.
            float grey = stage >= 2 ? 0.92f : 0.7f;
            Fill(0.10f, 0.10f, 0.11f, grey * Math.Min(1f, p * 6f) * Intensity);
            if (stage >= 2) Fill(0.55f, 0.02f, 0.03f, 0.13f * Intensity);

            if (stage == 1) DrawCornerTendrils(p);
            if (stage >= 2) DrawConvergingHands(stage, p);
            if (stage >= 3) DrawCrush(p);

            if (stage >= 2 && tex.Vignette != 0)
                Draw(tex.Vignette, 0, 0, W, H, 1, 1, 1, (0.4f + p * 0.35f) * Intensity);
        }

        /// <summary>Two tendrils reach in from the lower corners, then withdraw.</summary>
        private void DrawCornerTendrils(float p)
        {
            if (tex.Tendril == 0) return;
            float reach = p < 0.6f ? p / 0.6f : Math.Max(0f, 1f - (p - 0.6f) / 0.4f);
            float h = H * 0.75f * Ease(reach);
            float w = W * 0.10f;
            Draw(tex.Tendril, -w * 0.2f, H - h, w, h, 1, 1, 1, 0.85f * Intensity);
            Draw(tex.Tendril, W - w * 0.8f, H - h, w, h, 1, 1, 1, 0.85f * Intensity);
        }

        /// <summary>
        /// Six to nine hands out of the screen edges, each at its own speed, all
        /// converging. One settles over the centre.
        /// </summary>
        private void DrawConvergingHands(int stage, float p)
        {
            if (tex.Hand == 0) return;
            int count = Cfg.Visuals.TabooHandCount;
            float cx = W / 2f, cy = H / 2f;

            for (int i = 0; i < count; i++)
            {
                float a = (float)(i * Math.PI * 2 / count) + client.EffectSeed * 0.001f;
                float speed = 0.6f + (i % 4) * 0.22f;          // different speeds
                float t = Ease(Math.Min(1f, p * speed));

                float startR = Math.Max(W, H) * 0.75f;
                float r = startR * (1f - t * 0.72f);

                float hh = H * (0.42f + (i % 3) * 0.07f);
                float hw = hh * 0.5f;
                float x = cx + (float)Math.Cos(a) * r - hw / 2;
                float y = cy + (float)Math.Sin(a) * r - hh / 2;

                Draw(tex.Hand, x, y, hw, hh, 1, 1, 1, (0.55f + 0.35f * t) * Intensity);
            }

            // The one that settles over the centre, and in stage 3 closes.
            float centreScale = stage >= 3 ? 1f - Ease(p) * 0.55f : 1f;
            float ch = H * 0.62f * centreScale;
            float cw = ch * 0.5f;
            Draw(tex.Hand, cx - cw / 2, cy - ch / 2, cw, ch, 1, 1, 1, Math.Min(1f, p * 2f) * 0.8f * Intensity);
        }

        /// <summary>The hand closes: the screen crushes inward and goes.</summary>
        private void DrawCrush(float p)
        {
            float e = Ease(p);
            // Black closing in from every edge.
            float inset = (1f - e) * 0.5f;
            Fill(0, 0, 0, e * 0.55f);
            if (tex.White == 0) return;
            Draw(tex.White, 0, 0, W, H * inset * 0.6f, 0, 0, 0, 1f);
            Draw(tex.White, 0, H - H * inset * 0.6f, W, H * inset * 0.6f, 0, 0, 0, 1f);
            Draw(tex.White, 0, 0, W * inset * 0.6f, H, 0, 0, 0, 1f);
            Draw(tex.White, W - W * inset * 0.6f, 0, W * inset * 0.6f, H, 0, 0, 0, 1f);

            if (p > 0.82f) Fill(0, 0, 0, (p - 0.82f) / 0.18f);
        }

        private void RenderBreakdown(ActiveEffect fx)
        {
            float p = fx.Progress;
            float pulse = 0.5f + 0.5f * (float)Math.Sin(time * 11);
            Fill(0.05f, 0.05f, 0.07f, (0.55f + 0.2f * pulse) * Intensity);
            if (tex.Vignette != 0)
                Draw(tex.Vignette, 0, 0, W, H, 1, 1, 1, (0.6f + 0.3f * pulse) * Intensity);
            // Releases into white at the very end: the moment he gets up.
            if (p > 0.88f) Fill(1, 1, 1, (p - 0.88f) / 0.12f * 0.7f);
        }

        private void RenderRecall(ActiveEffect fx)
        {
            float p = fx.Progress;
            float a = p < 0.2f ? p / 0.2f : Math.Max(0f, 1f - (p - 0.2f) / 0.8f);
            Fill(0.12f, 0.12f, 0.13f, a * 0.7f * Intensity);
            if (tex.Vignette != 0) Draw(tex.Vignette, 0, 0, W, H, 1, 1, 1, a * 0.5f * Intensity);
        }

        private void RenderDespair(ActiveEffect fx)
        {
            float d = fx.Intensity;                  // 0..1, mapped from the despair stat
            if (d <= 0.01f) return;

            // Edges desaturate first, then the tunnel closes.
            if (tex.Vignette != 0)
                Draw(tex.Vignette, 0, 0, W, H, 1, 1, 1, d * 0.55f * Intensity);
            if (d > 0.5f)
            {
                float beat = 0.5f + 0.5f * (float)Math.Sin(time * 2.2);
                Fill(0.1f, 0.1f, 0.12f, (d - 0.5f) * 0.28f * beat * Intensity);
            }
        }

        private void RenderMiasma(ActiveEffect fx)
        {
            float a = fx.Intensity;
            if (a <= 0.01f) return;
            // A dark breathing at the edges. Never enough to obstruct play.
            float breathe = 0.85f + 0.15f * (float)Math.Sin(time * 0.7);
            if (tex.Vignette != 0)
                Draw(tex.Vignette, 0, 0, W, H, 0.35f, 0.2f, 0.45f, a * 0.4f * breathe * Intensity);
        }

        /// <summary>The arrival's vignette closing to 60% and opening again (§12.1 D).</summary>
        private void RenderArrivalVignette(ActiveEffect fx)
        {
            if ((Net.ReturnStage)fx.Phase != Net.ReturnStage.Arrival) return;
            if (tex.Vignette == 0) return;
            float p = fx.Progress;
            float a = p < 0.35f ? 0.6f * (p / 0.35f) : 0.6f * Math.Max(0f, 1f - (p - 0.35f) / 0.65f);
            Draw(tex.Vignette, 0, 0, W, H, 1, 1, 1, a * Intensity);
        }

        // ----------------------------------------------------------------- drawing

        private void Fill(float r, float g, float b, float a)
        {
            if (a <= 0.001f || tex.White == 0) return;
            Draw(tex.White, 0, 0, W, H, r, g, b, a);
        }

        private void Draw(int textureId, float x, float y, float w, float h, float r, float g, float b, float a)
        {
            if (textureId == 0 || a <= 0.002f || w <= 0 || h <= 0) return;
            colour.Set(r, g, b, Math.Min(1f, a));
            capi.Render.Render2DTexture(textureId, x, y, w, h, 10000f, colour);
        }

        private static float Ease(float t) => t * t * (3f - 2f * t);

        public void Dispose() { }
    }
}
