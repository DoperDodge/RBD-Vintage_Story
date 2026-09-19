using System;
using System.Text;
using Shinimodori.Death;
using Shinimodori.Net;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace Shinimodori.Client
{
    /// <summary>
    /// Every death you have ever died, in order (§9.3).
    ///
    /// Purely for the player's own horror: it grants nothing and changes nothing.
    /// </summary>
    public class LedgerDialog : GuiDialog
    {
        private readonly PktLedger ledger;
        private int page;
        private const int PerPage = 14;

        private LedgerDialog(ICoreClientAPI capi, PktLedger ledger) : base(capi)
        {
            this.ledger = ledger;
        }

        public static void ShowFor(ICoreClientAPI capi, PktLedger ledger)
        {
            try
            {
                var dlg = new LedgerDialog(capi, ledger);
                dlg.Compose();
                dlg.TryOpen();
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[shinimodori] ledger dialog failed: {0}", e.Message);
            }
        }

        public override string ToggleKeyCombinationCode => null;
        public override bool PrefersUngrabbedMouse => true;

        private int PageCount => Math.Max(1, (ledger.Rows.Count + PerPage - 1) / PerPage);

        private void Compose()
        {
            var font = CairoFont.WhiteSmallText().WithFontSize(15);
            double width = 700;

            var sb = new StringBuilder();
            if (ledger.Rows.Count == 0) sb.AppendLine(Lang.Get("shinimodori:ledger-empty"));

            for (int i = page * PerPage; i < Math.Min(ledger.Rows.Count, (page + 1) * PerPage); i++)
            {
                var r = ledger.Rows[i];
                string cause = Lang.Get(DeathCauses.LangKey((DeathCause)r.Cause));
                sb.AppendLine(Lang.Get("shinimodori:ledger-row",
                    r.Index, cause,
                    string.IsNullOrEmpty(r.KillerName) ? "-" : r.KillerName,
                    r.X, r.Y, r.Z, (int)r.TotalHours, (int)r.MiasmaAtDeath));
            }

            double textHeight = 30 + PerPage * 20;
            var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            var composer = capi.Gui
                .CreateCompo("shinimodori-ledger", ElementStdBounds.AutosizedMainDialog)
                .AddShadedDialogBG(bgBounds, true)
                .AddDialogTitleBar(Lang.Get("shinimodori:ledger-title", ledger.Rows.Count), () => TryClose())
                .BeginChildElements(bgBounds)
                .AddStaticText(sb.ToString(), font, ElementBounds.Fixed(0, 34, width, textHeight));

            double y = 34 + textHeight + 8;
            if (PageCount > 1)
            {
                composer.AddButton("<", () => Turn(-1), ElementBounds.Fixed(0, y, 50, 26));
                composer.AddStaticText(Lang.Get("shinimodori:ledger-page", page + 1, PageCount),
                    CairoFont.WhiteDetailText(), ElementBounds.Fixed(60, y + 4, 200, 26));
                composer.AddButton(">", () => Turn(1), ElementBounds.Fixed(width - 50, y, 50, 26));
            }

            SingleComposer = composer.EndChildElements().Compose();
        }

        private bool Turn(int delta)
        {
            page = Math.Max(0, Math.Min(PageCount - 1, page + delta));
            Compose();
            return true;
        }
    }
}
