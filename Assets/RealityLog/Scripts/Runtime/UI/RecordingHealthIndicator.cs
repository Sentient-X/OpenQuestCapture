#nullable enable

using UnityEngine;
using UnityEngine.UI;

namespace RealityLog.UI
{
    public class RecordingHealthIndicator : MonoBehaviour
    {
        private Image? dotImage;

        private static readonly Color GoodColor = new Color(0.2f, 0.85f, 0.3f);
        private static readonly Color WarningColor = new Color(1f, 0.8f, 0.2f);
        private static readonly Color ErrorColor = new Color(0.95f, 0.25f, 0.2f);

        private void Awake()
        {
            dotImage = GetComponent<Image>();
        }

        public void SetHealth(HealthLevel level)
        {
            if (dotImage == null)
                dotImage = GetComponent<Image>();

            if (dotImage != null)
            {
                dotImage.color = level switch
                {
                    HealthLevel.Good => GoodColor,
                    HealthLevel.Warning => WarningColor,
                    HealthLevel.Error => ErrorColor,
                    _ => GoodColor
                };
            }
        }

        public static GameObject CreateDot(Transform parent, HealthLevel level, float size = 16f)
        {
            var go = new GameObject("HealthDot", typeof(RectTransform), typeof(Image), typeof(RecordingHealthIndicator));
            go.transform.SetParent(parent, false);

            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(size, size);

            var indicator = go.GetComponent<RecordingHealthIndicator>();
            indicator.SetHealth(level);

            return go;
        }
    }
}
