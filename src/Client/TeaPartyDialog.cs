using System;
using Shinimodori.Net;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace Shinimodori.Client
{
    /// <summary>
    /// Her table (§10.3). Parchment on a black border, her name above, and — when the
    /// server says so — a text box the player may type anything into.
    ///
    /// That box is the point of the whole scene. Everywhere else in the world, saying
    /// it costs you. Here it costs nothing, and the relief is supposed to be physical.
    /// </summary>
    public class TeaPartyDialog : GuiDialog
    {
        private readonly ShinimodoriClient client;
        private PktDialogNode current;

        public TeaPartyDialog(ICoreClientAPI capi, ShinimodoriClient client) : base(capi)
        {
            this.client = client;
        }

        public override string ToggleKeyCombinationCode => null;
        public override bool PrefersUngrabbedMouse => true;
        public override bool CaptureAllInputs() => false;
        public override EnumDialogType DialogType => EnumDialogType.Dialog;
        public override double DrawOrder => 0.3;

        public void Show(PktDialogNode node)
        {
            current = node;
            Compose();
            if (!IsOpened()) TryOpen();

            if (node.Closing)
            {
                // Let the last line land before the white takes the screen.
                capi.Event.RegisterCallback(_ => TryClose(), 2600);
            }
        }

        public void TryCloseSafe()
        {
            if (IsOpened()) TryClose();
        }

        private void Compose()
        {
            if (current == null) return;

            var font = CairoFont.WhiteSmallishText().WithFontSize(18);
            var speakerFont = CairoFont.WhiteSmallText().WithFontSize(22).WithWeight(Cairo.FontWeight.Bold);

            double width = 620;
            var textBounds = ElementBounds.Fixed(0, 40, width, 0);
            var body = current.Text ?? "";
            double textHeight = Math.Max(90, font.GetTextExtents(body).Height / RuntimeEnv.GUIScale + 48);
            textBounds = ElementBounds.Fixed(0, 40, width, textHeight);

            var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            var dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle)
                .WithFixedAlignmentOffset(0, 60);

            var composer = capi.Gui
                .CreateCompo("shinimodori-teaparty", dialogBounds)
                .AddShadedDialogBG(bgBounds, true)
                .AddDialogTitleBar(current.Speaker ?? Lang.Get("shinimodori:witch-echidna"), null)
                .BeginChildElements(bgBounds)
                .AddStaticText(body, font, textBounds, "body");

            double y = textBounds.fixedY + textBounds.fixedHeight + 10;

            if (current.AllowFreeText)
            {
                composer.AddStaticText(Lang.Get("shinimodori:tea-freetext-hint"),
                        CairoFont.WhiteDetailText(), ElementBounds.Fixed(0, y, width, 26));
                y += 30;
                composer.AddTextInput(ElementBounds.Fixed(0, y, width, 30), OnFreeTextEntered, font, "freetext");
                y += 44;
            }

            foreach (var choice in current.Choices)
            {
                string id = choice.Id;
                composer.AddButton(choice.Label, () => OnChoice(id),
                    ElementBounds.Fixed(0, y, width, 30), EnumButtonStyle.Normal);
                y += 36;
            }

            if (current.Closing && current.Choices.Count == 0)
            {
                composer.AddStaticText(Lang.Get("shinimodori:tea-closing"),
                    CairoFont.WhiteDetailText(), ElementBounds.Fixed(0, y, width, 26));
            }

            SingleComposer = composer.EndChildElements().Compose();
        }

        private bool OnChoice(string choiceId)
        {
            client.StateChannel.SendPacket(new PktDialogChoice
            {
                NodeId = current?.NodeId ?? "",
                ChoiceId = choiceId,
            });
            return true;
        }

        private void OnFreeTextEntered(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            client.StateChannel.SendPacket(new PktDialogChoice
            {
                NodeId = current?.NodeId ?? "",
                ChoiceId = "freetext",
                FreeText = text,
            });
            SingleComposer?.GetTextInput("freetext")?.SetValue("");
        }

        public override bool OnEscapePressed()
        {
            // You do not walk out on her. Leaving is a choice you make at the table.
            return true;
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();
            current = null;
        }
    }
}
