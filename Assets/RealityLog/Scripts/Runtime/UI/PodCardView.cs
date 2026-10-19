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
    /// a title with a status chip, label/value lines (PodStatusBoard's words), a sign-in code only
    /// while it is pending, a row of pill actions (Sign in, Scan QR, Local only, Set up manually)
    /// and the manual setup (Factory URL, Auth URL, Join code, Enroll) collapsed until asked for.
    /// The actions are PodSetupController's and RigCoordinator's, unchanged.
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

        private Sprite? sprite;
        private TMP_FontAsset? font;
        private Action<string>? onError;

        private TextMeshProUGUI title = default!;
        private Image chip = default!;
        private TextMeshProUGUI chipText = default!;
        private TextMeshProUGUI collector = default!, task = default!, rig = default!, refusal = default!, episode = default!, setupLine = default!;
        private GameObject refusalRow = default!, codeRow = default!, addressRow = default!, setupRow = default!;
        private TextMeshProUGUI code = default!, address = default!;
        private GameObject signInPill = default!;
        private TextMeshProUGUI signInText = default!, scanText = default!, localText = default!, manualText = default!;
        private Image scanImage = default!, localImage = default!;
        private GameObject manual = default!;
        private TextMeshProUGUI factoryValue = default!, authValue = default!, joinValue = default!, enrollText = default!;
        private bool expanded;

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

            collector = Field(transform, "Collector", out _);
            codeRow = Row("SignInCode", transform, 52, 12).gameObject;
            Label(codeRow.transform, "Sign-in code");
            code = Text("Code", codeRow.transform, 34, Color.white, flexible: true);
            code.fontStyle = FontStyles.Bold;
            address = Field(transform, "Approve at", out addressRow);
            task = Field(transform, "Task", out _);
            rig = Field(transform, "Rig", out _);
            refusal = Field(transform, "Last refusal", out refusalRow);
            episode = Field(transform, "Last episode", out _);
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
                ShowStatus();
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
            Set(task, PodStatusBoard.Field(lines.Task).value);
            Set(rig, PodStatusBoard.Field(lines.Rig).value);
            Show(refusalRow, lines.HasRefusal);
            Set(refusal, PodStatusBoard.Field(lines.LastRefusal).value);
            Set(episode, PodStatusBoard.Field(lines.LastEpisode).value);
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
            localImage.color = localOnly ? SecondaryColor : FieldColor;
            localText.color = localOnly ? Color.white : DimTextColor;

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
