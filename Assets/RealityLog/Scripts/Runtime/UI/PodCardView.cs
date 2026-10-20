#nullable enable

using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using RealityLog.Rig;

namespace RealityLog.UI
{
    /// <summary>
    /// The recordings menu's Pod card, first in the list and styled like a recording list item:
    /// a title with a status chip; the one blocker and the action that clears it, in a tinted
    /// panel; the collector and a sign-in code only while it is pending; then the status screen
    /// (PodScreenBoard): each rig device with its port, state pill and fix, the collection's
    /// task, progress, what the Factory expects next and its link, and each recent episode's
    /// steps through the pipeline; the paired phone (PhoneCompanion: Pair phone scans the phone's
    /// QR; Forget phone); a row of pill actions (Sign in, Scan QR, Local only, Set up manually)
    /// and the manual setup (Factory URL, Auth URL, Join code, Enroll) collapsed until asked for.
    /// The actions are PodSetupController's, RigCoordinator's and PhoneCompanion's.
    /// </summary>
    public sealed class PodCardView : MonoBehaviour
    {
        // The app's palette: RecordingListItem (card, secondary buttons, title), CalibrateButton
        // (primary), RecordingDetailPanelUI (text, dim text, good/warning/error).
        private static readonly Color CardColor = new(0.15686275f, 0.15686275f, 0.15686275f, 1f);
        private static readonly Color FieldColor = new(0.21f, 0.21f, 0.21f, 1f);
        private static readonly Color PrimaryColor = new(0.2f, 0.5f, 0.85f, 1f);
        private static readonly Color SecondaryColor = new(0.3137255f, 0.3137255f, 0.3137255f, 1f);
        private static readonly Color TitleColor = new(0.78823537f, 0.78823537f, 0.78823537f, 1f);
        private static readonly Color TextColor = new(0.9f, 0.9f, 0.9f, 1f);
        private static readonly Color DimTextColor = new(0.6f, 0.6f, 0.6f, 1f);
        private static readonly Color GoodColor = new(0.2f, 0.85f, 0.3f, 1f);
        private static readonly Color WarningColor = new(1f, 0.8f, 0.2f, 1f);
        private static readonly Color ErrorColor = new(0.95f, 0.25f, 0.2f, 1f);

        private const float LabelWidth = 160;
        private const float LineHeight = 32;
        private const float PillHeight = 56;
        private const float StateWidth = 190;
        private const int DeviceRows = 4;
        private const int EpisodeRows = 5;

        private Sprite? sprite;
        private TMP_FontAsset? font;
        private Action<string>? onError;

        private TextMeshProUGUI title = default!;
        private Image chip = default!;
        private TextMeshProUGUI chipText = default!;
        private TextMeshProUGUI collector = default!, setupLine = default!;
        private GameObject codeRow = default!, addressRow = default!, setupRow = default!;
        // The status screen.
        private Image blockerPanel = default!;
        private TextMeshProUGUI blockerHeadline = default!, blockerAction = default!;
        private GameObject devicesSection = default!, devicesNoteRow = default!;
        private TextMeshProUGUI devicesNote = default!;
        private readonly StateRow[] devices = new StateRow[DeviceRows];
        private TextMeshProUGUI taskValue = default!, progressValue = default!, nextValue = default!, linkValue = default!;
        private GameObject progressRow = default!, nextRow = default!;
        private GameObject episodesSection = default!;
        private readonly EpisodeLine[] episodes = new EpisodeLine[EpisodeRows];
        private readonly StreamLiveness liveness = new();
        private PodScreen screen = new();

        /// <summary>A device row: name, value, state pill, and the fix beneath when there is one.</summary>
        private sealed class StateRow
        {
            public GameObject Row = default!, HintRow = default!;
            public TextMeshProUGUI Name = default!, Value = default!, Hint = default!, PillText = default!;
            public Image Pill = default!;
        }

        /// <summary>An episode row: its id, the six pipeline steps, a stage pill, and its detail beneath.</summary>
        private sealed class EpisodeLine
        {
            public GameObject Row = default!, DetailRow = default!;
            public TextMeshProUGUI Id = default!, Detail = default!, PillText = default!;
            public Image Pill = default!;
            public readonly Image[] Steps = new Image[PodScreenBoard.Steps.Length];
        }
        private TextMeshProUGUI code = default!, address = default!;
        private GameObject signInPill = default!;
        private TextMeshProUGUI signInText = default!, scanText = default!, localText = default!, manualText = default!;
        private Image scanImage = default!, localImage = default!;
        private GameObject manual = default!;
        private TextMeshProUGUI factoryValue = default!, authValue = default!, joinValue = default!, enrollText = default!;
        private bool expanded;
        // The phone companion: a panel while pairing or after a failure, the Phone line, its pills.
        private GameObject phoneSection = default!, phonePanelObj = default!;
        private Image phonePanel = default!;
        private TextMeshProUGUI phoneHeadline = default!, phoneAction = default!, phoneLine = default!;
        private GameObject pairPill = default!, forgetPill = default!;
        private Image pairImage = default!;
        private TextMeshProUGUI pairText = default!, forgetText = default!;
        private float forgetArmedUntil = float.NegativeInfinity;
        private const float ForgetConfirmSeconds = 4f;

        private string? shownStatus;
        private StartRefusal? shownRefusal;
        private bool shownSucceeded;
        private bool shownAsking;
        private PodStatusLines lines;

        /// <summary>Builds the card first in <paramref name="parent"/> (the list's content).</summary>
        public static PodCardView Create(Transform parent, Sprite? sprite, TMP_FontAsset? font, Action<string> onError)
        {
            var card = new GameObject("PodCard", typeof(RectTransform));
            card.transform.SetParent(parent, false);
            card.transform.SetAsFirstSibling();
            var view = card.AddComponent<PodCardView>();
            view.sprite = sprite;
            view.font = font;
            view.onError = onError;
            view.Build();
            view.Refresh(force: true);
            return view;
        }

        private void Build()
        {
            var background = gameObject.AddComponent<Image>();
            Rounded(background, CardColor);
            var layout = gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(20, 20, 14, 18);
            layout.spacing = 4;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var fitter = gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // Title and chip.
            var header = Row("Header", transform, 44, 12);
            title = Text("Title", header, 26, TitleColor, flexible: true);
            var chipObj = new GameObject("Chip", typeof(RectTransform));
            chipObj.transform.SetParent(header, false);
            chip = chipObj.AddComponent<Image>();
            Rounded(chip, Color.clear);
            var chipSize = chipObj.AddComponent<LayoutElement>();
            chipSize.minWidth = chipSize.preferredWidth = 150;
            chipSize.preferredHeight = 36;
            chipText = Text("Text", chipObj.transform, 18, Color.white, flexible: false);
            Stretch(chipText.rectTransform);
            chipText.fontStyle = FontStyles.Bold;
            chipText.alignment = TextAlignmentOptions.Center;
            Space(transform, 4);

            // The blocker: what is in the way and what clears it, tinted by how bad it is.
            var blocker = new GameObject("Blocker", typeof(RectTransform));
            blocker.transform.SetParent(transform, false);
            blockerPanel = blocker.AddComponent<Image>();
            Rounded(blockerPanel, FieldColor);
            var blockerLayout = blocker.AddComponent<VerticalLayoutGroup>();
            blockerLayout.padding = new RectOffset(18, 18, 12, 14);
            blockerLayout.spacing = 2;
            blockerLayout.childControlWidth = true;
            blockerLayout.childControlHeight = true;
            blockerLayout.childForceExpandWidth = true;
            blockerLayout.childForceExpandHeight = false;
            blockerHeadline = Text("Headline", blocker.transform, 24, TextColor, flexible: false);
            blockerHeadline.fontStyle = FontStyles.Bold;
            Wrap(blockerHeadline);
            blockerAction = Text("Action", blocker.transform, 21, TextColor, flexible: false);
            Wrap(blockerAction);
            Space(transform, 8);

            collector = Field(transform, "Collector", out _);
            codeRow = Row("SignInCode", transform, 52, 12).gameObject;
            Label(codeRow.transform, "Sign-in code");
            code = Text("Code", codeRow.transform, 34, Color.white, flexible: true);
            code.fontStyle = FontStyles.Bold;
            address = Field(transform, "Approve at", out addressRow);

            // Devices: one row per role, a pill for its state and its fix beneath.
            devicesSection = Section(transform, "Devices");
            for (var i = 0; i < DeviceRows; i++) devices[i] = BuildStateRow(devicesSection.transform);
            devicesNote = Field(devicesSection.transform, "", out devicesNoteRow);
            devicesNote.color = DimTextColor;
            devicesNote.fontSize = 19;

            // The collection: task, progress, what the Factory expects next, and the link.
            var collection = Section(transform, "Collection");
            taskValue = Field(collection.transform, "Task", out _);
            progressValue = Field(collection.transform, "Progress", out progressRow);
            nextValue = Field(collection.transform, "Next", out nextRow);
            linkValue = Field(collection.transform, "Factory", out _);

            // Episodes: the newest first, each across the pipeline's steps.
            episodesSection = Section(transform, "Episodes");
            var legend = Row("Legend", episodesSection.transform, 22, 6);
            Fixed(legend, LabelWidth - 6);
            foreach (var step in PodScreenBoard.Steps)
            {
                var label = Text(step, legend, 15, DimTextColor, flexible: true);
                label.alignment = TextAlignmentOptions.Center;
                label.text = step;
            }
            Fixed(legend, StateWidth);
            for (var i = 0; i < EpisodeRows; i++) episodes[i] = BuildEpisodeLine(episodesSection.transform);

            // The phone companion: pairing (a panel says what to do), the paired phone, its pills.
            phoneSection = Section(transform, "Phone");
            phonePanelObj = new GameObject("PhonePanel", typeof(RectTransform));
            phonePanelObj.transform.SetParent(phoneSection.transform, false);
            phonePanel = phonePanelObj.AddComponent<Image>();
            Rounded(phonePanel, FieldColor);
            var phonePanelLayout = phonePanelObj.AddComponent<VerticalLayoutGroup>();
            phonePanelLayout.padding = new RectOffset(18, 18, 12, 14);
            phonePanelLayout.spacing = 2;
            phonePanelLayout.childControlWidth = true;
            phonePanelLayout.childControlHeight = true;
            phonePanelLayout.childForceExpandWidth = true;
            phonePanelLayout.childForceExpandHeight = false;
            phoneHeadline = Text("Headline", phonePanelObj.transform, 24, TextColor, flexible: false);
            phoneHeadline.fontStyle = FontStyles.Bold;
            Wrap(phoneHeadline);
            phoneAction = Text("Action", phonePanelObj.transform, 21, TextColor, flexible: false);
            Wrap(phoneAction);
            phoneLine = Field(phoneSection.transform, "Phone", out _);
            var phoneActions = Row("PhoneActions", phoneSection.transform, PillHeight, 12);
            pairPill = Pill(phoneActions, "PairPhone", PrimaryColor, () => PhoneCompanion.Instance?.PairOrCancel(), out pairText, out pairImage);
            forgetPill = Pill(phoneActions, "ForgetPhone", SecondaryColor, ForgetPhone, out forgetText, out _);

            Space(transform, 6);
            setupLine = Field(transform, "Setup", out setupRow);
            Space(transform, 10);

            // Actions, one row of pills.
            var actions = Row("Actions", transform, PillHeight, 12);
            signInPill = Pill(actions, "SignIn", PrimaryColor, () => PodSetupController.Instance?.SignIn(), out signInText, out _);
            Pill(actions, "ScanQr", PrimaryColor, () =>
            {
                if (PodSetupController.Instance is not { } setup) return;
                if (setup.Scanning) setup.CancelScan();
                else setup.StartScan();
            }, out scanText, out scanImage);
            Pill(actions, "LocalOnly", SecondaryColor, ToggleLocalOnly, out localText, out localImage);
            var manualPill = Pill(actions, "SetUpManually", FieldColor, () => { expanded = !expanded; Refresh(force: false); },
                out manualText, out _);
            manualText.fontSize = 20;
            manualText.fontStyle = FontStyles.Normal;
            manualText.color = TextColor;
            var manualSize = manualPill.GetComponent<LayoutElement>();
            manualSize.flexibleWidth = 0;
            manualSize.preferredWidth = 210;

            // Manual setup, collapsed until asked for.
            manual = new GameObject("ManualSetup", typeof(RectTransform));
            manual.transform.SetParent(transform, false);
            var manualLayout = manual.AddComponent<VerticalLayoutGroup>();
            manualLayout.padding = new RectOffset(0, 0, 12, 0);
            manualLayout.spacing = 6;
            manualLayout.childControlWidth = true;
            manualLayout.childControlHeight = true;
            manualLayout.childForceExpandWidth = true;
            manualLayout.childForceExpandHeight = false;
            factoryValue = EditableField(manual.transform, "Factory URL", () => PodSetupController.Instance?.EditFactoryUrl());
            authValue = EditableField(manual.transform, "Auth URL", () => PodSetupController.Instance?.EditAuthUrl());
            joinValue = EditableField(manual.transform, "Join code", () => PodSetupController.Instance?.EditJoinCode());
            var enrollRow = Row("EnrollRow", manual.transform, PillHeight, 12);
            Pill(enrollRow, "Enroll", PrimaryColor, () => PodSetupController.Instance?.Enroll(), out enrollText, out _);
            manual.SetActive(false);
        }

        private void Update() => Refresh(force: false);

        private void Refresh(bool force)
        {
            var status = RigCoordinator.StatusJsonOrDefault;
            var lastStart = PodStartLog.Shared.Last;
            var succeeded = PodStartLog.Shared.Succeeded;
            var setup = PodSetupController.Instance;
            var asking = setup is { SigningIn: true };
            // The status string is replaced, never mutated: parse only when something changed.
            if (force || !ReferenceEquals(status, shownStatus) || !ReferenceEquals(lastStart, shownRefusal)
                || succeeded != shownSucceeded || asking != shownAsking)
            {
                shownStatus = status;
                shownRefusal = lastStart;
                shownSucceeded = succeeded;
                shownAsking = asking;
                lines = PodStatusBoard.Lines(status, lastStart, succeeded, asking);
                screen = PodScreenBoard.Build(status, lastStart, succeeded, liveness, DateTime.UtcNow);
                ShowStatus();
                ShowScreen();
            }
            ShowControls(setup);
        }

        private void ShowStatus()
        {
            Set(title, lines.PodName.Length > 0 ? "<color=#FFFFFF><b>Pod</b></color>   " + lines.PodName : "<color=#FFFFFF><b>Pod</b></color>");
            var tint = lines.Readiness switch
            {
                PodReadiness.Ready => GoodColor,
                PodReadiness.NotReady => WarningColor,
                PodReadiness.Refused => ErrorColor,
                _ => DimTextColor,
            };
            chip.color = new Color(tint.r, tint.g, tint.b, 0.18f);
            chipText.color = tint;
            Set(chipText, lines.ReadinessText);

            var pending = lines.SignInCode.Length > 0;
            Set(collector, pending ? "sign-in pending: approve the code below" : PodStatusBoard.Field(lines.Collector).value);
            Show(codeRow, pending);
            Set(code, lines.SignInCode);
            Show(addressRow, pending && lines.SignInAddress.Length > 0);
            Set(address, lines.SignInAddress);
        }

        private void ShowScreen()
        {
            var tone = Tint(screen.Blocker.Tone);
            blockerPanel.color = new Color(tone.r, tone.g, tone.b, 0.16f);
            blockerHeadline.color = screen.Blocker.Tone == PodTone.Neutral ? TextColor : tone;
            Set(blockerHeadline, screen.Blocker.Headline);
            Set(blockerAction, screen.Blocker.Action);

            for (var i = 0; i < DeviceRows; i++)
            {
                var row = devices[i];
                var on = i < screen.Devices.Count;
                Show(row.Row, on);
                Show(row.HintRow, on && screen.Devices[i].Hint.Length > 0);
                if (!on) continue;
                var device = screen.Devices[i];
                Set(row.Name, device.Name);
                Set(row.Value, device.Value);
                SetPill(row.Pill, row.PillText, device.Pill, device.Tone);
                row.Hint.color = Tint(device.Tone == PodTone.Error ? PodTone.Error : PodTone.Warning);
                Set(row.Hint, device.Hint);
            }
            Show(devicesNoteRow, screen.DevicesNote.Length > 0);
            Set(devicesNote, screen.DevicesNote);

            Set(taskValue, screen.Task);
            Show(progressRow, screen.Progress.Length > 0);
            Set(progressValue, screen.Progress);
            Show(nextRow, screen.Next.Length > 0);
            Set(nextValue, screen.Next);
            Set(linkValue, screen.Link);
            linkValue.color = screen.LinkTone == PodTone.Neutral ? TextColor : Tint(screen.LinkTone);

            Show(episodesSection, screen.Episodes.Count > 0);
            for (var i = 0; i < EpisodeRows; i++)
            {
                var line = episodes[i];
                var on = i < screen.Episodes.Count;
                Show(line.Row, on);
                Show(line.DetailRow, on && screen.Episodes[i].Detail.Length > 0);
                if (!on) continue;
                var episode = screen.Episodes[i];
                Set(line.Id, episode.Id);
                for (var step = 0; step < line.Steps.Length; step++)
                {
                    line.Steps[step].color = step == episode.FailedAt ? ErrorColor
                        : step < episode.Reached ? GoodColor
                        : step == episode.Reached && episode.FailedAt < 0 && episode.Reached < line.Steps.Length ? new Color(WarningColor.r, WarningColor.g, WarningColor.b, 0.55f)
                        : FieldColor;
                }
                SetPill(line.Pill, line.PillText, episode.Stage, episode.Tone);
                line.Detail.color = episode.Tone == PodTone.Error ? ErrorColor : DimTextColor;
                Set(line.Detail, episode.Detail);
            }
        }

        private static Color Tint(PodTone tone) => tone switch
        {
            PodTone.Good => GoodColor,
            PodTone.Warning => WarningColor,
            PodTone.Error => ErrorColor,
            _ => DimTextColor,
        };

        /// <summary>A state pill as the title's chip draws it: the tone's colour faint behind, bold in front.</summary>
        private static void SetPill(Image pill, TextMeshProUGUI text, string word, PodTone tone)
        {
            var tint = Tint(tone);
            pill.color = new Color(tint.r, tint.g, tint.b, 0.18f);
            text.color = tint;
            Set(text, word);
        }

        private void ShowControls(PodSetupController? setup)
        {
            var coordinator = RigCoordinator.Instance;
            var enrolled = coordinator is { Enrolled: true };
            var localOnly = coordinator?.LocalOnly ?? true;
            var signedIn = coordinator is { CollectorSignedInNow: true };

            // The setup line: a step's message, or how to start while not enrolled.
            var message = setup == null ? null : !enrolled ? setup.Status : setup.StepMessage;
            Show(setupRow, message != null);
            if (message != null) Set(setupLine, message);

            Show(signInPill, setup != null && enrolled && !localOnly && !signedIn);
            Set(signInText, setup is { SigningIn: true } ? "Asking..." : "Sign in");
            Set(scanText, setup is { Scanning: true } ? "Cancel scan" : "Scan QR");
            scanImage.color = enrolled ? SecondaryColor : PrimaryColor;
            Set(localText, localOnly ? "Local only: On" : "Local only: Off");
            // A live toggle in either state, styled like the other secondary pills.
            localImage.color = SecondaryColor;
            localText.color = Color.white;

            ShowPhone();

            Set(manualText, expanded ? "Hide setup" : "Set up manually");
            Show(manual, expanded && setup != null);
            if (expanded && setup != null)
            {
                Set(factoryValue, setup.FactoryUrl);
                Set(authValue, setup.AuthUrl);
                Set(joinValue, setup.JoinCode.Length > 0 ? "entered" : "tap to type");
                Set(enrollText, setup.Busy && !setup.Scanning && !setup.SigningIn ? "Enrolling..." : "Enroll");
            }
        }

        /// <summary>The Phone section from the companion's phase, in CompanionCard's words.</summary>
        private void ShowPhone()
        {
            var companion = PhoneCompanion.Instance;
            var phase = companion?.Phase ?? CompanionPhase.Unavailable;
            var card = CompanionCard.Describe(phase, companion?.PhoneName ?? "", companion?.OperatorName ?? "",
                companion?.PodLabel ?? "", companion?.Error ?? "");
            Show(phoneSection, phase != CompanionPhase.Unavailable);
            if (phase == CompanionPhase.Unavailable) return;
            var panel = card.Headline.Length > 0;
            Show(phonePanelObj, panel);
            if (panel)
            {
                var tone = Tint(card.PanelTone);
                phonePanel.color = new Color(tone.r, tone.g, tone.b, 0.16f);
                phoneHeadline.color = card.PanelTone == PodTone.Neutral ? TextColor : tone;
                Set(phoneHeadline, card.Headline);
                Set(phoneAction, card.Action);
            }
            Set(phoneLine, card.Line);
            phoneLine.color = card.LineTone == PodTone.Neutral ? TextColor : Tint(card.LineTone);
            Show(pairPill, card.PairPill.Length > 0);
            Set(pairText, card.PairPill);
            pairImage.color = card.PairPrimary ? PrimaryColor : SecondaryColor;
            Show(forgetPill, card.ShowForget);
            Set(forgetText, Time.unscaledTime < forgetArmedUntil ? "Tap again to forget" : "Forget phone");
        }

        /// <summary>Forget phone asks for a second tap: a forgotten phone pairs again only from a fresh QR.</summary>
        private void ForgetPhone()
        {
            if (Time.unscaledTime < forgetArmedUntil)
            {
                forgetArmedUntil = float.NegativeInfinity;
                PhoneCompanion.Instance?.Forget();
                return;
            }
            forgetArmedUntil = Time.unscaledTime + ForgetConfirmSeconds;
        }

        private void ToggleLocalOnly()
        {
            var coordinator = RigCoordinator.Instance;
            if (coordinator is null) return;
            var error = coordinator.SetLocalOnly(!coordinator.LocalOnly);
            if (error != null) onError?.Invoke(error);
            shownStatus = null; // Local only is in the status: show the change at once.
            Refresh(force: true);
        }

        // ---- Building blocks ----

        /// <summary>A titled group: a dim heading over its rows.</summary>
        private GameObject Section(Transform parent, string title)
        {
            var section = new GameObject(title, typeof(RectTransform));
            section.transform.SetParent(parent, false);
            var layout = section.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(0, 0, 12, 0);
            layout.spacing = 4;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var heading = Text("Heading", section.transform, 19, TitleColor, flexible: false);
            heading.fontStyle = FontStyles.Bold;
            heading.text = title.ToUpperInvariant();
            heading.characterSpacing = 4;
            var size = heading.gameObject.AddComponent<LayoutElement>();
            size.minHeight = size.preferredHeight = 28;
            return section;
        }

        private StateRow BuildStateRow(Transform parent)
        {
            var row = new StateRow();
            var line = Row("Device", parent, 38, 12);
            row.Row = line.gameObject;
            row.Name = Text("Name", line, 21, TextColor, flexible: false);
            var nameSize = row.Name.gameObject.AddComponent<LayoutElement>();
            nameSize.minWidth = nameSize.preferredWidth = LabelWidth;
            row.Value = Text("Value", line, 20, DimTextColor, flexible: true);
            (row.Pill, row.PillText) = StatePill(line);
            var hint = Row("Fix", parent, 28, 12);
            row.HintRow = hint.gameObject;
            Fixed(hint, LabelWidth);
            row.Hint = Text("Hint", hint, 19, WarningColor, flexible: true);
            return row;
        }

        private EpisodeLine BuildEpisodeLine(Transform parent)
        {
            var line = new EpisodeLine();
            var row = Row("Episode", parent, 38, 6);
            line.Row = row.gameObject;
            line.Id = Text("Id", row, 19, TextColor, flexible: false);
            var idSize = line.Id.gameObject.AddComponent<LayoutElement>();
            idSize.minWidth = idSize.preferredWidth = LabelWidth - 6;
            for (var i = 0; i < line.Steps.Length; i++)
            {
                // A step: a short rounded bar centred in its cell, under the legend's word.
                var cell = new GameObject("Step", typeof(RectTransform));
                cell.transform.SetParent(row, false);
                var cellSize = cell.AddComponent<LayoutElement>();
                cellSize.minWidth = 0;
                cellSize.preferredWidth = 1;
                cellSize.flexibleWidth = 1;
                cellSize.minHeight = cellSize.preferredHeight = 38;
                var bar = new GameObject("Bar", typeof(RectTransform));
                bar.transform.SetParent(cell.transform, false);
                var rect = (RectTransform)bar.transform;
                rect.anchorMin = new Vector2(0.06f, 0.5f);
                rect.anchorMax = new Vector2(0.94f, 0.5f);
                rect.sizeDelta = new Vector2(0, 10);
                line.Steps[i] = bar.AddComponent<Image>();
                Rounded(line.Steps[i], FieldColor);
                line.Steps[i].raycastTarget = false;
            }
            (line.Pill, line.PillText) = StatePill(row);
            var detail = Row("Detail", parent, 26, 12);
            line.DetailRow = detail.gameObject;
            Fixed(detail, LabelWidth - 6);
            line.Detail = Text("Detail", detail, 18, DimTextColor, flexible: true);
            return line;
        }

        /// <summary>A state pill like the title's chip, at the end of a row.</summary>
        private (Image, TextMeshProUGUI) StatePill(Transform row)
        {
            var obj = new GameObject("State", typeof(RectTransform));
            obj.transform.SetParent(row, false);
            var image = obj.AddComponent<Image>();
            Rounded(image, Color.clear);
            image.raycastTarget = false;
            var size = obj.AddComponent<LayoutElement>();
            size.minWidth = size.preferredWidth = StateWidth;
            size.minHeight = size.preferredHeight = 32;
            var text = Text("Text", obj.transform, 17, Color.white, flexible: false);
            Stretch(text.rectTransform);
            text.rectTransform.sizeDelta = new Vector2(-16, 0);
            text.fontStyle = FontStyles.Bold;
            text.alignment = TextAlignmentOptions.Center;
            return (image, text);
        }

        /// <summary>A fixed-width gap in a row, so a line beneath lines up with the values above.</summary>
        private static void Fixed(Transform row, float width)
        {
            var gap = new GameObject("Indent", typeof(RectTransform));
            gap.transform.SetParent(row, false);
            var size = gap.AddComponent<LayoutElement>();
            size.minWidth = size.preferredWidth = width;
        }

        /// <summary>Lets a line wrap, its height following its text (the blocker's lines).</summary>
        private static void Wrap(TextMeshProUGUI text)
        {
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Overflow;
            text.alignment = TextAlignmentOptions.TopLeft;
        }

        private void Rounded(Image image, Color color)
        {
            image.sprite = sprite;
            image.type = sprite != null ? Image.Type.Sliced : Image.Type.Simple;
            image.color = color;
        }

        private static Transform Row(string name, Transform parent, float height, float spacing)
        {
            var row = new GameObject(name, typeof(RectTransform));
            row.transform.SetParent(parent, false);
            var layout = row.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = spacing;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            var size = row.AddComponent<LayoutElement>();
            size.minHeight = size.preferredHeight = height;
            return row.transform;
        }

        private static void Space(Transform parent, float height)
        {
            var space = new GameObject("Space", typeof(RectTransform));
            space.transform.SetParent(parent, false);
            var size = space.AddComponent<LayoutElement>();
            size.minHeight = size.preferredHeight = height;
        }

        private TextMeshProUGUI Text(string name, Transform parent, float size, Color color, bool flexible)
        {
            var obj = new GameObject(name, typeof(RectTransform));
            obj.transform.SetParent(parent, false);
            var text = obj.AddComponent<TextMeshProUGUI>();
            if (font != null) text.font = font;
            text.fontSize = size;
            text.color = color;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.raycastTarget = false;
            if (flexible)
            {
                // Widths come from the row, never from the text, so a long value ellipsizes.
                var element = obj.AddComponent<LayoutElement>();
                element.minWidth = 0;
                element.preferredWidth = 1;
                element.flexibleWidth = 1;
            }
            return text;
        }

        private void Label(Transform row, string label)
        {
            var text = Text("Label", row, 20, DimTextColor, flexible: false);
            text.text = label;
            var element = text.gameObject.AddComponent<LayoutElement>();
            element.minWidth = element.preferredWidth = LabelWidth;
        }

        private TextMeshProUGUI Field(Transform parent, string label, out GameObject row)
        {
            var transformRow = Row(label, parent, LineHeight, 12);
            row = transformRow.gameObject;
            Label(transformRow, label);
            return Text("Value", transformRow, 22, TextColor, flexible: true);
        }

        private TextMeshProUGUI EditableField(Transform parent, string label, Action onClick)
        {
            var row = Row(label, parent, 48, 12);
            var image = row.gameObject.AddComponent<Image>();
            Rounded(image, FieldColor);
            var layout = row.GetComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(16, 16, 0, 0);
            var button = row.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(() => onClick());
            Label(row, label);
            return Text("Value", row, 22, TextColor, flexible: true);
        }

        /// <summary>A pill like the Calibrate button: the rounded sprite, a fill, bold white text.</summary>
        private GameObject Pill(Transform parent, string name, Color color, Action onClick, out TextMeshProUGUI text, out Image image)
        {
            var pill = new GameObject(name, typeof(RectTransform));
            pill.transform.SetParent(parent, false);
            image = pill.AddComponent<Image>();
            Rounded(image, color);
            var button = pill.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(() => onClick());
            var size = pill.AddComponent<LayoutElement>();
            size.minWidth = 0;
            size.preferredWidth = 1;
            size.flexibleWidth = 1;
            size.minHeight = size.preferredHeight = PillHeight;
            text = Text("Text", pill.transform, 22, Color.white, flexible: false);
            Stretch(text.rectTransform);
            text.rectTransform.sizeDelta = new Vector2(-24, 0);
            text.fontStyle = FontStyles.Bold;
            text.alignment = TextAlignmentOptions.Center;
            return pill;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.sizeDelta = Vector2.zero;
        }

        private static void Set(TextMeshProUGUI text, string value)
        {
            if (text.text != value) text.text = value;
        }

        private static void Show(GameObject obj, bool on)
        {
            if (obj.activeSelf != on) obj.SetActive(on);
        }
    }
}
