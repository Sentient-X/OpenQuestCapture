#nullable enable

using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using RealityLog.Rig;

namespace RealityLog.UI
{
    /// <summary>
    /// Everything the Pod card shows, gathered once per refresh from RigCoordinator,
    /// PodSetupController, PodControls and PhoneCompanion (<see cref="PodCardView.Gather"/>), or
    /// given whole by the Editor snapshot (Assets/Editor/PodCardSnapshot.cs).
    /// </summary>
    public sealed class PodCardState
    {
        public PodStatusLines Lines;
        public PodScreen Screen = new();
        public int? HeadsetPct, LeftPct, RightPct;
        public bool Enrolled, LocalOnly, SignedIn, Recording;
        /// <summary>The control running now, if any.</summary>
        public PodControl? Running;
        /// <summary>End collection waits for its second tap.</summary>
        public bool EndArmed;
        /// <summary>The panel under the controls: End collection's question, a running control, or the last answer.</summary>
        public PodTone PanelTone;
        public string PanelHeadline = "", PanelAction = "";
        /// <summary>False when this build has no PodSetupController: the setup actions are hidden.</summary>
        public bool SetupAvailable;
        public string? SetupMessage;
        public bool SigningIn, Scanning, Enrolling;
        public string FactoryUrl = "", AuthUrl = "";
        public bool JoinCodeEntered;
        public CompanionPhase PhonePhase = CompanionPhase.Unavailable;
        public CompanionCardLines Phone;
        public bool ForgetArmed;
    }

    /// <summary>
    /// The recordings menu's Pod page: one card of rows, each fact on one row (label on the
    /// left, value in the middle, state pill on the right, one row height), its long text
    /// wrapping inside its own cell. Sections, top to bottom: the title and chip; the blocker
    /// banner (what is in the way, what clears it); Controls (Check, Start/Stop, End collection,
    /// then their answer); Battery (headset, left, right in one row); Devices; Collection;
    /// Episodes (each across the pipeline's steps); Phone; Setup (Sign in, Scan QR, Local only,
    /// Set up manually and the manual fields, collapsed until asked for). It sits in its own
    /// ScrollRect (RecordingListUI's Pod tab), so it never draws over the recordings list.
    /// The actions are PodSetupController's, RigCoordinator's, PodControls' and PhoneCompanion's.
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

        /// <summary>The one row height: a single-line row is exactly this tall; a wrapped one grows.</summary>
        public const float RowHeight = 44;
        private const float LabelWidth = 170;
        private const float StateWidth = 176;
        private const float PillHeight = 56;
        private const float ColumnGap = 14;
        // A cell's text sits this far below its row's top (and keeps this much below its last line).
        private const float CellTop = 10, CellBottom = 9;
        private const int DeviceRows = 4;
        private const int EpisodeRows = 8;

        private Sprite? sprite;
        private TMP_FontAsset? font;
        private Action<string>? onError;

        private TextMeshProUGUI title = default!;
        private Image chip = default!;
        private TextMeshProUGUI chipText = default!;
        // The blocker banner.
        private Image blockerPanel = default!;
        private TextMeshProUGUI blockerHeadline = default!, blockerAction = default!;
        // Controls.
        private TextMeshProUGUI checkText = default!, toggleText = default!, endText = default!;
        private Image checkImage = default!, toggleImage = default!, endImage = default!;
        private GameObject controlPanelObj = default!;
        private Image controlPanel = default!;
        private TextMeshProUGUI controlHeadline = default!, controlAction = default!;
        private TextMeshProUGUI headsetBattery = default!, leftBattery = default!, rightBattery = default!;
        // Devices.
        private readonly FactRow[] devices = new FactRow[DeviceRows];
        private FactRow devicesNote = default!;
        // Collection.
        private FactRow collector = default!, signInCode = default!, approveAt = default!;
        private FactRow task = default!, progress = default!, next = default!, link = default!;
        // Episodes.
        private GameObject episodesSection = default!;
        private readonly EpisodeLine[] episodes = new EpisodeLine[EpisodeRows];
        // Phone.
        private GameObject phoneSection = default!, phonePanelObj = default!;
        private Image phonePanel = default!;
        private TextMeshProUGUI phoneHeadline = default!, phoneAction = default!;
        private FactRow phoneLine = default!;
        private GameObject pairPill = default!, forgetPill = default!;
        private Image pairImage = default!;
        private TextMeshProUGUI pairText = default!, forgetText = default!;
        // Setup.
        private GameObject setupSection = default!;
        private FactRow setupLine = default!;
        private GameObject signInPill = default!;
        private TextMeshProUGUI signInText = default!, scanText = default!, localText = default!, manualText = default!;
        private Image scanImage = default!;
        private GameObject manual = default!;
        private TextMeshProUGUI factoryValue = default!, authValue = default!, joinValue = default!, enrollText = default!;
        private bool expanded;

        private float forgetArmedUntil = float.NegativeInfinity;
        private const float ForgetConfirmSeconds = 4f;
        private float endArmedUntil = float.NegativeInfinity;
        private const float EndConfirmSeconds = 8f;
        private const float ResultShownSeconds = 60f;

        private RecordingManager? recorder;
        private readonly StreamLiveness liveness = new();
        private string? shownStatus;
        private StartRefusal? shownRefusal;
        private bool shownSucceeded, shownAsking;
        private PodStatusLines lines;
        private PodScreen screen = new();
        private int? headsetPct;
        /// <summary>False while the Editor snapshot feeds the card: the live sources are not read.</summary>
        private bool live = true;

        /// <summary>A fact: a label, a value that wraps in its cell, and an optional state pill.</summary>
        private sealed class FactRow
        {
            public GameObject Row = default!;
            public TextMeshProUGUI Label = default!, Value = default!;
            public Image? Pill;
            public TextMeshProUGUI? PillText;
        }

        /// <summary>An episode: its id, the six steps over its detail, and a stage pill.</summary>
        private sealed class EpisodeLine
        {
            public GameObject Row = default!;
            public TextMeshProUGUI Id = default!, Detail = default!, PillText = default!;
            public Image Pill = default!;
            public readonly Image[] Steps = new Image[PodScreenBoard.Steps.Length];
        }

        /// <summary>Builds the card into <paramref name="parent"/> (the Pod page's scroll content).</summary>
        public static PodCardView Create(Transform parent, Sprite? sprite, TMP_FontAsset? font, Action<string> onError)
        {
            var card = new GameObject("PodCard", typeof(RectTransform));
            card.transform.SetParent(parent, false);
            var view = card.AddComponent<PodCardView>();
            view.sprite = sprite;
            view.font = font;
            view.onError = onError;
            view.Build();
            view.Refresh(force: true);
            return view;
        }

        /// <summary>Shows <paramref name="state"/> and stops reading the live sources (the Editor snapshot).</summary>
        public void ShowFixed(PodCardState state)
        {
            live = false;
            Show(state);
        }

        private void Update()
        {
            if (live) Refresh(force: false);
        }

        private void Refresh(bool force)
        {
            if (!live) return;
            Show(Gather(force));
        }

        /// <summary>The card's state from the live sources; the status is parsed only when it changed.</summary>
        private PodCardState Gather(bool force)
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
                headsetPct = HeadsetBattery(status);
                screen = PodScreenBoard.Build(status, lastStart, succeeded, liveness, DateTime.UtcNow);
            }

            var coordinator = RigCoordinator.Instance;
            var controls = PodControls.Instance;
            recorder ??= FindFirstObjectByType<RecordingManager>();
            var state = new PodCardState
            {
                Lines = lines,
                Screen = screen,
                HeadsetPct = headsetPct,
                LeftPct = controls?.Controllers.Left,
                RightPct = controls?.Controllers.Right,
                Enrolled = coordinator is { Enrolled: true },
                LocalOnly = coordinator?.LocalOnly ?? false,
                SignedIn = coordinator is { CollectorSignedInNow: true },
                Recording = recorder != null ? recorder.State != RecordingState.Idle : screen.Recording,
                Running = controls?.Running,
                SetupAvailable = setup != null,
                SigningIn = setup is { SigningIn: true },
                Scanning = setup is { Scanning: true },
                Enrolling = setup is { Busy: true, Scanning: false, SigningIn: false },
                FactoryUrl = setup?.FactoryUrl ?? "",
                AuthUrl = setup?.AuthUrl ?? "",
                JoinCodeEntered = setup != null && setup.JoinCode.Length > 0,
                ForgetArmed = Time.unscaledTime < forgetArmedUntil,
            };
            state.SetupMessage = setup == null ? null : !state.Enrolled ? setup.Status : setup.StepMessage;

            var canEnd = state.Running == null && !state.Recording && state.Enrolled && !state.LocalOnly;
            if (!canEnd) endArmedUntil = float.NegativeInfinity;
            state.EndArmed = Time.unscaledTime < endArmedUntil;
            if (state.EndArmed)
            {
                state.PanelTone = PodTone.Warning;
                state.PanelHeadline = PodControlWords.EndQuestion(screen.Credited, screen.Requested);
                state.PanelAction = "Tap End now to end it: its episodes are reviewed and uploaded as when it fills";
            }
            else if (state.Running is PodControl busy)
            {
                state.PanelTone = PodTone.Neutral;
                state.PanelHeadline = PodControlWords.Running(busy);
            }
            else if (controls?.Last is PodControlResult result && Time.unscaledTime - controls.LastAt < ResultShownSeconds)
            {
                state.PanelTone = result.Ok ? PodTone.Good : PodTone.Error;
                state.PanelHeadline = PodControlWords.Line(result);
            }

            var companion = PhoneCompanion.Instance;
            state.PhonePhase = companion?.Phase ?? CompanionPhase.Unavailable;
            state.Phone = CompanionCard.Describe(state.PhonePhase, companion?.PhoneName ?? "", companion?.OperatorName ?? "",
                companion?.PodLabel ?? "", companion?.Error ?? "");
            return state;
        }

        // ---- Showing a state ----

        private void Show(PodCardState state)
        {
            ShowHeader(state);
            ShowBlocker(state.Screen);
            ShowControls(state);
            ShowDevices(state.Screen);
            ShowCollection(state);
            ShowEpisodes(state.Screen);
            ShowPhone(state);
            ShowSetup(state);
        }

        private void ShowHeader(PodCardState state)
        {
            var name = state.Lines.PodName ?? "";
            Set(title, name.Length > 0 ? "<color=#FFFFFF><b>Pod</b></color>   " + name : "<color=#FFFFFF><b>Pod</b></color>");
            var tint = state.Lines.Readiness switch
            {
                PodReadiness.Ready => GoodColor,
                PodReadiness.NotReady => WarningColor,
                PodReadiness.Refused => ErrorColor,
                _ => DimTextColor,
            };
            chip.color = Faint(tint, 0.18f);
            chipText.color = tint;
            Set(chipText, state.Lines.ReadinessText ?? "");
        }

        private void ShowBlocker(PodScreen s)
        {
            var tone = Tint(s.Blocker.Tone);
            blockerPanel.color = Faint(tone, 0.16f);
            blockerHeadline.color = s.Blocker.Tone == PodTone.Neutral ? TextColor : tone;
            Set(blockerHeadline, s.Blocker.Headline ?? "");
            Set(blockerAction, s.Blocker.Action ?? "");
            Show(blockerAction.gameObject, !string.IsNullOrEmpty(s.Blocker.Action));
        }

        private void ShowControls(PodCardState state)
        {
            var running = state.Running;
            Set(checkText, running == PodControl.Check ? PodControlWords.Running(PodControl.Check) : "Check");
            ShowEnabled(checkImage, checkText, SecondaryColor, running == null && !state.Recording);
            Set(toggleText, running is PodControl.Start or PodControl.Stop ? PodControlWords.Running(running.Value) : state.Recording ? "Stop" : "Start");
            // Start dims while it would certainly be refused (the blocker says why); a tap still says why.
            ShowEnabled(toggleImage, toggleText, state.Recording ? ErrorColor : PrimaryColor,
                running == null && (state.Recording || !state.Screen.StartBlocked));
            var canEnd = running == null && !state.Recording && state.Enrolled && !state.LocalOnly;
            Set(endText, running == PodControl.EndCollection ? PodControlWords.Running(PodControl.EndCollection) : state.EndArmed ? "End now" : "End collection");
            ShowEnabled(endImage, endText, state.EndArmed ? ErrorColor : SecondaryColor, canEnd);

            var panel = state.PanelHeadline.Length > 0;
            Show(controlPanelObj, panel);
            if (panel)
            {
                var tint = Tint(state.PanelTone);
                controlPanel.color = Faint(tint, 0.16f);
                controlHeadline.color = state.PanelTone == PodTone.Neutral ? TextColor : tint;
                Set(controlHeadline, state.PanelHeadline);
                Show(controlAction.gameObject, state.PanelAction.Length > 0);
                Set(controlAction, state.PanelAction);
            }

            ShowCharge(headsetBattery, "Headset", state.HeadsetPct);
            ShowCharge(leftBattery, "Left", state.LeftPct);
            ShowCharge(rightBattery, "Right", state.RightPct);
        }

        private void ShowDevices(PodScreen s)
        {
            for (var i = 0; i < DeviceRows; i++)
            {
                var row = devices[i];
                var on = i < s.Devices.Count;
                Show(row.Row, on);
                if (!on) continue;
                var device = s.Devices[i];
                Set(row.Label, device.Name ?? "");
                var hint = device.Hint ?? "";
                var hintColor = device.Tone == PodTone.Error ? ErrorColor : WarningColor;
                Set(row.Value, hint.Length > 0
                    ? (device.Value ?? "") + "\n<size=88%><color=#" + ColorUtility.ToHtmlStringRGB(hintColor) + ">" + hint + "</color></size>"
                    : device.Value ?? "");
                SetPill(row.Pill!, row.PillText!, device.Pill ?? "", device.Tone);
            }
            Show(devicesNote.Row, !string.IsNullOrEmpty(s.DevicesNote));
            Set(devicesNote.Value, s.DevicesNote ?? "");
        }

        private void ShowCollection(PodCardState state)
        {
            var s = state.Screen;
            var pending = (state.Lines.SignInCode ?? "").Length > 0;
            Set(collector.Value, pending ? "sign-in pending: approve the code below" : PodStatusBoard.Field(state.Lines.Collector ?? "").value);
            Show(signInCode.Row, pending);
            Set(signInCode.Value, state.Lines.SignInCode ?? "");
            Show(approveAt.Row, pending && (state.Lines.SignInAddress ?? "").Length > 0);
            Set(approveAt.Value, state.Lines.SignInAddress ?? "");

            Set(task.Value, s.Task ?? "");
            Show(progress.Row, !string.IsNullOrEmpty(s.Progress));
            Set(progress.Value, s.Progress ?? "");
            Show(next.Row, !string.IsNullOrEmpty(s.Next));
            Set(next.Value, s.Next ?? "");
            Set(link.Value, s.Link ?? "");
            link.Value.color = s.LinkTone == PodTone.Neutral ? TextColor : Tint(s.LinkTone);
        }

        private void ShowEpisodes(PodScreen s)
        {
            Show(episodesSection, s.Episodes.Count > 0);
            for (var i = 0; i < EpisodeRows; i++)
            {
                var line = episodes[i];
                var on = i < s.Episodes.Count;
                Show(line.Row, on);
                if (!on) continue;
                var episode = s.Episodes[i];
                Set(line.Id, episode.Id ?? "");
                for (var step = 0; step < line.Steps.Length; step++)
                {
                    line.Steps[step].color = step == episode.FailedAt ? ErrorColor
                        : step < episode.Reached ? GoodColor
                        : step == episode.Reached && episode.FailedAt < 0 && episode.Reached < line.Steps.Length ? Faint(WarningColor, 0.55f)
                        : FieldColor;
                }
                SetPill(line.Pill, line.PillText, episode.Stage ?? "", episode.Tone);
                line.Detail.color = episode.Tone == PodTone.Error ? ErrorColor : DimTextColor;
                Set(line.Detail, episode.Detail ?? "");
                Show(line.Detail.gameObject, !string.IsNullOrEmpty(episode.Detail));
            }
        }

        /// <summary>The Phone section from the companion's phase, in CompanionCard's words.</summary>
        private void ShowPhone(PodCardState state)
        {
            var card = state.Phone;
            Show(phoneSection, state.PhonePhase != CompanionPhase.Unavailable);
            if (state.PhonePhase == CompanionPhase.Unavailable) return;
            var panel = (card.Headline ?? "").Length > 0;
            Show(phonePanelObj, panel);
            if (panel)
            {
                var tone = Tint(card.PanelTone);
                phonePanel.color = Faint(tone, 0.16f);
                phoneHeadline.color = card.PanelTone == PodTone.Neutral ? TextColor : tone;
                Set(phoneHeadline, card.Headline ?? "");
                Set(phoneAction, card.Action ?? "");
            }
            Set(phoneLine.Value, card.Line ?? "");
            phoneLine.Value.color = card.LineTone == PodTone.Neutral ? TextColor : Tint(card.LineTone);
            Show(pairPill, (card.PairPill ?? "").Length > 0);
            Set(pairText, card.PairPill ?? "");
            pairImage.color = card.PairPrimary ? PrimaryColor : SecondaryColor;
            Show(forgetPill, card.ShowForget);
            Set(forgetText, state.ForgetArmed ? "Tap again to forget" : "Forget phone");
        }

        private void ShowSetup(PodCardState state)
        {
            Show(setupSection, state.SetupAvailable);
            if (!state.SetupAvailable) return;
            // Not enrolled, setting up is the next thing to do: the section follows the blocker
            // (which points at it); once enrolled it goes back to the end.
            var index = state.Enrolled ? transform.childCount - 1 : blockerPanel.transform.GetSiblingIndex() + 1;
            if (setupSection.transform.GetSiblingIndex() != index) setupSection.transform.SetSiblingIndex(index);
            Show(setupLine.Row, state.SetupMessage != null);
            Set(setupLine.Value, state.SetupMessage ?? "");
            Show(signInPill, state.Enrolled && !state.LocalOnly && !state.SignedIn);
            Set(signInText, state.SigningIn ? "Asking..." : "Sign in");
            Set(scanText, state.Scanning ? "Cancel scan" : "Scan QR");
            scanImage.color = state.Enrolled ? SecondaryColor : PrimaryColor;
            Set(localText, state.LocalOnly ? "Local only: On" : "Local only: Off");
            Set(manualText, expanded ? "Hide setup" : "Set up manually");
            Show(manual, expanded);
            if (expanded)
            {
                Set(factoryValue, state.FactoryUrl);
                Set(authValue, state.AuthUrl);
                Set(joinValue, state.JoinCodeEntered ? "entered" : "tap to type");
                Set(enrollText, state.Enrolling ? "Enrolling..." : "Enroll");
            }
        }

        private static Color Tint(PodTone tone) => tone switch
        {
            PodTone.Good => GoodColor,
            PodTone.Warning => WarningColor,
            PodTone.Error => ErrorColor,
            _ => DimTextColor,
        };

        private static Color Faint(Color c, float alpha) => new(c.r, c.g, c.b, alpha);

        /// <summary>A state pill as the title's chip draws it: the tone's colour faint behind, bold in front.</summary>
        private static void SetPill(Image pill, TextMeshProUGUI text, string word, PodTone tone)
        {
            var tint = Tint(tone);
            pill.color = Faint(tint, 0.18f);
            text.color = tint;
            Set(text, word);
            Show(pill.gameObject, word.Length > 0);
        }

        /// <summary>"Left 64%", tinted by how low it is; dim when it cannot be read.</summary>
        private static void ShowCharge(TextMeshProUGUI text, string label, int? pct)
        {
            Set(text, "<color=#999999>" + label + "</color>  " + PodControlWords.Percent(pct));
            text.color = pct is int p ? p <= 15 ? ErrorColor : p <= 30 ? WarningColor : GoodColor : DimTextColor;
        }

        /// <summary>A pill that cannot act now is drawn as a field, its word dimmed; it still answers a tap with why.</summary>
        private static void ShowEnabled(Image pill, TextMeshProUGUI text, Color color, bool enabled)
        {
            pill.color = enabled ? color : FieldColor;
            text.color = enabled ? Color.white : DimTextColor;
        }

        // ---- Actions ----

        private void StartOrStop()
        {
            recorder ??= FindFirstObjectByType<RecordingManager>();
            var recording = recorder != null ? recorder.State != RecordingState.Idle : screen.Recording;
            PodControls.Instance?.Run(recording ? PodControl.Stop : PodControl.Start);
        }

        /// <summary>End collection asks for a second tap, with how far the collection got.</summary>
        private void EndCollection()
        {
            if (Time.unscaledTime < endArmedUntil)
            {
                endArmedUntil = float.NegativeInfinity;
                PodControls.Instance?.Run(PodControl.EndCollection);
                return;
            }
            recorder ??= FindFirstObjectByType<RecordingManager>();
            if (recorder != null && recorder.State != RecordingState.Idle)
            {
                // Refused with its reason, on the card and the phone.
                PodControls.Instance?.Run(PodControl.EndCollection);
                return;
            }
            endArmedUntil = Time.unscaledTime + EndConfirmSeconds;
        }

        /// <summary>deviceReport.battery_percent from the rig status; null when unread.</summary>
        private static int? HeadsetBattery(string status)
        {
            try
            {
                return HeadsetStatusMap.Battery(Newtonsoft.Json.Linq.JToken.Parse(status) as Newtonsoft.Json.Linq.JObject);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return null;
            }
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

        // ---- Building ----

        private void Build()
        {
            var background = gameObject.AddComponent<Image>();
            Rounded(background, CardColor);
            background.raycastTarget = false;
            Column(gameObject, new RectOffset(24, 24, 16, 22), 6);

            // Title and chip.
            var header = Row("Header", transform, 52);
            title = Text("Title", header, 28, TitleColor, Cell.Flexible);
            (chip, chipText) = StateCell(header, 210, 40, 19);

            // The blocker: what is in the way and what clears it, tinted by how bad it is.
            var blocker = Panel("Blocker", transform, out blockerPanel);
            blockerHeadline = Text("Headline", blocker, 24, TextColor, Cell.Wrapping);
            blockerHeadline.fontStyle = FontStyles.Bold;
            blockerAction = Text("Action", blocker, 21, TextColor, Cell.Wrapping);

            // Controls: Check · Start/Stop · End collection, their answer, then the battery row.
            var controls = Section(transform, "Controls");
            var actions = Row("ControlActions", controls, PillHeight);
            Pill(actions, "Check", SecondaryColor, () => PodControls.Instance?.Run(PodControl.Check), out checkText, out checkImage);
            Pill(actions, "StartStop", PrimaryColor, StartOrStop, out toggleText, out toggleImage);
            Pill(actions, "EndCollection", SecondaryColor, EndCollection, out endText, out endImage);
            controlPanelObj = Panel("ControlPanel", controls, out controlPanel).gameObject;
            controlHeadline = Text("Headline", controlPanelObj.transform, 22, TextColor, Cell.Wrapping);
            controlHeadline.fontStyle = FontStyles.Bold;
            controlAction = Text("Action", controlPanelObj.transform, 20, TextColor, Cell.Wrapping);
            controlPanelObj.SetActive(false);
            var battery = Row("Battery", controls, RowHeight);
            Label(battery, "Battery");
            headsetBattery = Text("Headset", battery, 21, TextColor, Cell.Flexible);
            leftBattery = Text("Left", battery, 21, TextColor, Cell.Flexible);
            rightBattery = Text("Right", battery, 21, TextColor, Cell.Flexible);

            // Devices: one row per role, its fix wrapped under its value.
            var devicesSection = Section(transform, "Devices");
            for (var i = 0; i < DeviceRows; i++) devices[i] = Fact(devicesSection, "Device", withPill: true);
            devicesNote = Fact(devicesSection, "", withPill: false);
            devicesNote.Value.color = DimTextColor;
            devicesNote.Value.fontSize = 19;

            // The collection: who collects, the task, progress, what the Factory expects next, the link.
            var collection = Section(transform, "Collection");
            collector = Fact(collection, "Collector", withPill: false);
            signInCode = Fact(collection, "Sign-in code", withPill: false);
            signInCode.Value.fontSize = 32;
            signInCode.Value.fontStyle = FontStyles.Bold;
            signInCode.Value.color = Color.white;
            approveAt = Fact(collection, "Approve at", withPill: false);
            task = Fact(collection, "Task", withPill: false);
            progress = Fact(collection, "Progress", withPill: false);
            next = Fact(collection, "Next", withPill: false);
            link = Fact(collection, "Factory", withPill: false);

            // Episodes: the newest first, each across the pipeline's steps.
            var episodesTransform = Section(transform, "Episodes");
            episodesSection = episodesTransform.gameObject;
            BuildLegend(episodesTransform);
            for (var i = 0; i < EpisodeRows; i++) episodes[i] = BuildEpisodeLine(episodesTransform);

            // The phone companion: a panel while pairing or after a failure, the Phone line, its pills.
            var phone = Section(transform, "Phone");
            phoneSection = phone.gameObject;
            phonePanelObj = Panel("PhonePanel", phone, out phonePanel).gameObject;
            phoneHeadline = Text("Headline", phonePanelObj.transform, 22, TextColor, Cell.Wrapping);
            phoneHeadline.fontStyle = FontStyles.Bold;
            phoneAction = Text("Action", phonePanelObj.transform, 20, TextColor, Cell.Wrapping);
            phoneLine = Fact(phone, "Phone", withPill: false);
            var phoneActions = Row("PhoneActions", phone, PillHeight);
            pairPill = Pill(phoneActions, "PairPhone", PrimaryColor, () => PhoneCompanion.Instance?.PairOrCancel(), out pairText, out pairImage);
            forgetPill = Pill(phoneActions, "ForgetPhone", SecondaryColor, ForgetPhone, out forgetText, out _);

            // Setup: the step's message, the setup actions, and the manual fields when asked for.
            var setup = Section(transform, "Setup");
            setupSection = setup.gameObject;
            setupLine = Fact(setup, "Status", withPill: false);
            var setupActions = Row("SetupActions", setup, PillHeight);
            signInPill = Pill(setupActions, "SignIn", PrimaryColor, () => PodSetupController.Instance?.SignIn(), out signInText, out _);
            Pill(setupActions, "ScanQr", PrimaryColor, () =>
            {
                if (PodSetupController.Instance is not { } controller) return;
                if (controller.Scanning) controller.CancelScan();
                else controller.StartScan();
            }, out scanText, out scanImage);
            Pill(setupActions, "LocalOnly", SecondaryColor, ToggleLocalOnly, out localText, out _);
            Pill(setupActions, "SetUpManually", FieldColor, () => { expanded = !expanded; Refresh(force: false); }, out manualText, out _);
            manualText.color = TextColor;

            manual = new GameObject("ManualSetup", typeof(RectTransform));
            manual.transform.SetParent(setup, false);
            Column(manual, new RectOffset(0, 0, 4, 0), 6);
            factoryValue = EditableField(manual.transform, "Factory URL", () => PodSetupController.Instance?.EditFactoryUrl());
            authValue = EditableField(manual.transform, "Auth URL", () => PodSetupController.Instance?.EditAuthUrl());
            joinValue = EditableField(manual.transform, "Join code", () => PodSetupController.Instance?.EditJoinCode());
            var enrollRow = Row("EnrollRow", manual.transform, PillHeight);
            Pill(enrollRow, "Enroll", PrimaryColor, () => PodSetupController.Instance?.Enroll(), out enrollText, out _);
            manual.SetActive(false);
        }

        /// <summary>The step names over the episode rows' bars, in the same columns.</summary>
        private void BuildLegend(Transform parent)
        {
            var legend = Row("Legend", parent, 26);
            Gap(legend, LabelWidth);
            var steps = new GameObject("Steps", typeof(RectTransform));
            steps.transform.SetParent(legend, false);
            Flexible(steps);
            var layout = steps.AddComponent<HorizontalLayoutGroup>();
            Configure(layout, 4);
            foreach (var step in PodScreenBoard.Steps)
            {
                var label = Text(step, steps.transform, 15, DimTextColor, Cell.Flexible);
                label.alignment = TextAlignmentOptions.Center;
                label.text = step;
            }
            Gap(legend, StateWidth);
        }

        private EpisodeLine BuildEpisodeLine(Transform parent)
        {
            var line = new EpisodeLine();
            var row = Row("Episode", parent, RowHeight);
            line.Row = row.gameObject;
            line.Id = Text("Id", row, 19, TextColor, Cell.Label);
            // The middle: the six step bars, then the detail wrapped beneath them.
            var middle = new GameObject("Steps", typeof(RectTransform));
            middle.transform.SetParent(row, false);
            Flexible(middle);
            Column(middle, new RectOffset(0, 0, 0, 0), 0);
            var bars = Row("Bars", middle.transform, RowHeight);
            bars.GetComponent<HorizontalLayoutGroup>().spacing = 4;
            for (var i = 0; i < line.Steps.Length; i++)
            {
                // A step: a short rounded bar centred in its cell, under the legend's word.
                var cell = new GameObject("Step", typeof(RectTransform));
                cell.transform.SetParent(bars, false);
                var cellSize = cell.AddComponent<LayoutElement>();
                cellSize.minWidth = 0;
                cellSize.preferredWidth = 1;
                cellSize.flexibleWidth = 1;
                cellSize.minHeight = cellSize.preferredHeight = RowHeight;
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
            line.Detail = Text("Detail", middle.transform, 18, DimTextColor, Cell.Wrapping);
            line.Detail.margin = new Vector4(0, 0, 0, CellBottom);
            line.Detail.GetComponent<LayoutElement>().minHeight = 0;
            (line.Pill, line.PillText) = StateCell(row, StateWidth, 34, 17);
            return line;
        }

        /// <summary>A fact row: label | value (wraps) | state pill (or an empty cell of the same width).</summary>
        private FactRow Fact(Transform parent, string label, bool withPill)
        {
            var fact = new FactRow();
            var row = Row(label.Length > 0 ? label : "Note", parent, RowHeight);
            fact.Row = row.gameObject;
            fact.Label = Label(row, label);
            fact.Value = Text("Value", row, 21, TextColor, Cell.Wrapping);
            if (withPill)
            {
                (fact.Pill, fact.PillText) = StateCell(row, StateWidth, 34, 17);
            }
            return fact;
        }

        /// <summary>
        /// A fixed-width cell one row tall with a state pill centred in it: the pill never moves
        /// off its row, whatever the row's other cells wrap to.
        /// </summary>
        private (Image, TextMeshProUGUI) StateCell(Transform row, float width, float height, float fontSize)
        {
            var cell = new GameObject("StateCell", typeof(RectTransform));
            cell.transform.SetParent(row, false);
            var size = cell.AddComponent<LayoutElement>();
            size.minWidth = size.preferredWidth = width;
            size.minHeight = size.preferredHeight = Mathf.Max(RowHeight, height);
            var pillObj = new GameObject("State", typeof(RectTransform));
            pillObj.transform.SetParent(cell.transform, false);
            var rect = (RectTransform)pillObj.transform;
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(1, 1);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(0, height);
            rect.anchoredPosition = new Vector2(0, -Mathf.Max(RowHeight, height) / 2);
            var image = pillObj.AddComponent<Image>();
            Rounded(image, Color.clear);
            image.raycastTarget = false;
            var text = Text("Text", pillObj.transform, fontSize, Color.white, Cell.Free);
            Stretch(text.rectTransform);
            text.rectTransform.sizeDelta = new Vector2(-14, 0);
            text.fontStyle = FontStyles.Bold;
            text.alignment = TextAlignmentOptions.Center;
            text.enableAutoSizing = true;
            text.fontSizeMin = 12;
            text.fontSizeMax = fontSize;
            return (image, text);
        }

        /// <summary>A titled group: a dim heading over its rows.</summary>
        private Transform Section(Transform parent, string heading)
        {
            var section = new GameObject(heading, typeof(RectTransform));
            section.transform.SetParent(parent, false);
            Column(section, new RectOffset(0, 0, 14, 0), 2);
            var text = Text("Heading", section.transform, 18, TitleColor, Cell.Free);
            text.fontStyle = FontStyles.Bold;
            text.text = heading.ToUpperInvariant();
            text.characterSpacing = 4;
            var size = text.gameObject.AddComponent<LayoutElement>();
            size.minHeight = size.preferredHeight = 30;
            return section.transform;
        }

        /// <summary>A tinted, rounded panel whose height follows its wrapped lines.</summary>
        private Transform Panel(string name, Transform parent, out Image image)
        {
            var panel = new GameObject(name, typeof(RectTransform));
            panel.transform.SetParent(parent, false);
            image = panel.AddComponent<Image>();
            Rounded(image, FieldColor);
            image.raycastTarget = false;
            Column(panel, new RectOffset(18, 18, 12, 14), 2);
            return panel.transform;
        }

        private static void Column(GameObject obj, RectOffset padding, float spacing)
        {
            var layout = obj.AddComponent<VerticalLayoutGroup>();
            layout.padding = padding;
            layout.spacing = spacing;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
        }

        private static void Configure(HorizontalLayoutGroup layout, float spacing)
        {
            layout.spacing = spacing;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
        }

        /// <summary>A row: its cells side by side, at least <paramref name="height"/> tall, taller when a cell wraps.</summary>
        private static Transform Row(string name, Transform parent, float height)
        {
            var row = new GameObject(name, typeof(RectTransform));
            row.transform.SetParent(parent, false);
            Configure(row.AddComponent<HorizontalLayoutGroup>(), ColumnGap);
            var size = row.AddComponent<LayoutElement>();
            size.minHeight = height;
            return row.transform;
        }

        private static void Gap(Transform row, float width)
        {
            var gap = new GameObject("Gap", typeof(RectTransform));
            gap.transform.SetParent(row, false);
            var size = gap.AddComponent<LayoutElement>();
            size.minWidth = size.preferredWidth = width;
        }

        private static void Flexible(GameObject obj)
        {
            var size = obj.AddComponent<LayoutElement>();
            size.minWidth = 0;
            size.preferredWidth = 1;
            size.flexibleWidth = 1;
        }

        private void Rounded(Image image, Color color)
        {
            image.sprite = sprite;
            image.type = sprite != null ? Image.Type.Sliced : Image.Type.Simple;
            image.color = color;
        }

        /// <summary>How a text sits in its row.</summary>
        private enum Cell
        {
            /// <summary>Sized by its parent (a pill's word, a heading).</summary>
            Free,
            /// <summary>The label column: fixed width, one row tall, wraps.</summary>
            Label,
            /// <summary>Takes the row's spare width; one line, ellipsized.</summary>
            Flexible,
            /// <summary>Takes the row's spare width and wraps; the row grows to fit it.</summary>
            Wrapping,
        }

        private TextMeshProUGUI Text(string name, Transform parent, float size, Color color, Cell cell)
        {
            var obj = new GameObject(name, typeof(RectTransform));
            obj.transform.SetParent(parent, false);
            var text = obj.AddComponent<TextMeshProUGUI>();
            if (font != null) text.font = font;
            text.fontSize = size;
            text.color = color;
            text.raycastTarget = false;
            text.richText = true;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.margin = Vector4.zero;
            if (cell == Cell.Free) return text;
            // Every cell's first line starts at the same height, so label, value and pill line up
            // on the row's first line however far the value wraps.
            text.alignment = TextAlignmentOptions.TopLeft;
            text.margin = new Vector4(0, CellTop, 0, CellBottom);
            var element = obj.AddComponent<LayoutElement>();
            element.minHeight = RowHeight;
            switch (cell)
            {
                case Cell.Label:
                    element.minWidth = element.preferredWidth = LabelWidth;
                    text.textWrappingMode = TextWrappingModes.Normal;
                    text.overflowMode = TextOverflowModes.Overflow;
                    break;
                case Cell.Flexible:
                    element.minWidth = 0;
                    element.preferredWidth = 1;
                    element.flexibleWidth = 1;
                    break;
                case Cell.Wrapping:
                    // Width from the row, never from the text; height from the wrapped text.
                    element.minWidth = 0;
                    element.preferredWidth = 1;
                    element.flexibleWidth = 1;
                    text.textWrappingMode = TextWrappingModes.Normal;
                    text.overflowMode = TextOverflowModes.Overflow;
                    break;
            }
            return text;
        }

        private TextMeshProUGUI Label(Transform row, string label)
        {
            var text = Text("Label", row, 20, DimTextColor, Cell.Label);
            text.text = label;
            return text;
        }

        private TextMeshProUGUI EditableField(Transform parent, string label, Action onClick)
        {
            var row = Row(label, parent, 50);
            var image = row.gameObject.AddComponent<Image>();
            Rounded(image, FieldColor);
            var layout = row.GetComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(16, 16, 3, 3);
            var button = row.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(() => onClick());
            Label(row, label);
            return Text("Value", row, 21, TextColor, Cell.Flexible);
        }

        /// <summary>A pill like the Calibrate button: the rounded sprite, a fill, bold white text.</summary>
        private GameObject Pill(Transform parent, string name, Color color, Action onClick, out TextMeshProUGUI text, out Image image)
        {
            var pill = new GameObject(name, typeof(RectTransform));
            pill.transform.SetParent(parent, false);
            image = pill.AddComponent<Image>();
            Rounded(image, color);
            image.raycastTarget = true;
            var button = pill.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(() => onClick());
            var size = pill.AddComponent<LayoutElement>();
            size.minWidth = 0;
            size.preferredWidth = 1;
            size.flexibleWidth = 1;
            size.minHeight = size.preferredHeight = PillHeight;
            text = Text("Text", pill.transform, 22, Color.white, Cell.Free);
            Stretch(text.rectTransform);
            text.rectTransform.sizeDelta = new Vector2(-20, 0);
            text.fontStyle = FontStyles.Bold;
            text.alignment = TextAlignmentOptions.Center;
            text.enableAutoSizing = true;
            text.fontSizeMin = 14;
            text.fontSizeMax = 22;
            return pill;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.sizeDelta = Vector2.zero;
            rect.anchoredPosition = Vector2.zero;
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
