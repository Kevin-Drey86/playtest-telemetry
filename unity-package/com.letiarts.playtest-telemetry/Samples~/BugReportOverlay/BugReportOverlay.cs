using LetiArts.PlaytestTelemetry;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// A deliberately plain bug-report form (uGUI, built in code so no prefab is needed). Open it with a
/// three-finger tap or a device shake (F8 in the Editor). The game team is expected to reskin it.
/// The gestures use the legacy Input Manager; with only the new Input System enabled they are
/// disabled, so call Show() from your own UI instead.
/// </summary>
public class BugReportOverlay : MonoBehaviour
{
    const float ShakeThreshold = 2.5f;      // acceleration magnitude in g
    const float ShakeCooldown = 1.5f;

    GameObject panel;
    InputField noteField;
    Toggle screenshotToggle;
    float lastShake = -10f;

    void Awake()
    {
        Build();
        panel.SetActive(false);
    }

    void Update()
    {
#if ENABLE_LEGACY_INPUT_MANAGER
        if (Input.touchCount == 3 && (Input.GetTouch(0).phase == TouchPhase.Began ||
                                      Input.GetTouch(1).phase == TouchPhase.Began ||
                                      Input.GetTouch(2).phase == TouchPhase.Began))
        {
            Show();
        }
        else if (Input.acceleration.sqrMagnitude > ShakeThreshold * ShakeThreshold && Time.unscaledTime - lastShake > ShakeCooldown)
        {
            lastShake = Time.unscaledTime;
            Show();
        }
        if (Input.GetKeyDown(KeyCode.F8)) Show();
#endif
    }

    public void Show()
    {
        noteField.text = "";
        panel.SetActive(true);
    }

    void Send()
    {
        string note = noteField.text;
        bool screenshot = screenshotToggle.isOn;
        panel.SetActive(false);                       // hide first so the form is not in the screenshot
        PlaytestTelemetry.ReportBug(note, screenshot);
    }

    void Build()
    {
        var canvasGo = new GameObject("BugReportCanvas");
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32767;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080, 1920);
        canvasGo.AddComponent<GraphicRaycaster>();

#if ENABLE_LEGACY_INPUT_MANAGER
#pragma warning disable CS0618 // FindFirstObjectByType does not exist in early 2021.3
        if (FindObjectOfType<EventSystem>() == null)
#pragma warning restore CS0618
        {
            var es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
            es.AddComponent<StandaloneInputModule>();
        }
#endif

        panel = NewImage("Panel", canvasGo.transform, new Color(0f, 0f, 0f, 0.85f));
        Stretch(panel.GetComponent<RectTransform>(), 0, 0, 0, 0);

        NewText("Title", panel.transform, "Report a bug", 56, TextAnchor.MiddleCenter, new Vector2(0.05f, 0.82f), new Vector2(0.95f, 0.92f));

        GameObject inputGo = NewImage("Note", panel.transform, Color.white);
        SetAnchors(inputGo.GetComponent<RectTransform>(), new Vector2(0.05f, 0.45f), new Vector2(0.95f, 0.80f));
        noteField = inputGo.AddComponent<InputField>();
        Text inputText = NewText("Text", inputGo.transform, "", 44, TextAnchor.UpperLeft, Vector2.zero, Vector2.one);
        inputText.color = Color.black;
        Stretch(inputText.rectTransform, 16, 16, 16, 16);
        Text placeholder = NewText("Placeholder", inputGo.transform, "What went wrong?", 44, TextAnchor.UpperLeft, Vector2.zero, Vector2.one);
        placeholder.color = new Color(0.4f, 0.4f, 0.4f);
        Stretch(placeholder.rectTransform, 16, 16, 16, 16);
        noteField.textComponent = inputText;
        noteField.placeholder = placeholder;
        noteField.lineType = InputField.LineType.MultiLineNewline;

        GameObject toggleGo = NewImage("ScreenshotToggle", panel.transform, new Color(1f, 1f, 1f, 0.2f));
        SetAnchors(toggleGo.GetComponent<RectTransform>(), new Vector2(0.05f, 0.36f), new Vector2(0.95f, 0.43f));
        screenshotToggle = toggleGo.AddComponent<Toggle>();
        GameObject check = NewImage("Check", toggleGo.transform, Color.green);
        SetAnchors(check.GetComponent<RectTransform>(), new Vector2(0.02f, 0.2f), new Vector2(0.1f, 0.8f));
        screenshotToggle.graphic = check.GetComponent<Image>();
        screenshotToggle.targetGraphic = toggleGo.GetComponent<Image>();
        screenshotToggle.isOn = true;
        NewText("Label", toggleGo.transform, "Include screenshot", 40, TextAnchor.MiddleLeft, new Vector2(0.13f, 0f), Vector2.one);

        Button send = NewButton("Send", panel.transform, "Send", new Vector2(0.52f, 0.25f), new Vector2(0.95f, 0.33f));
        send.onClick.AddListener(Send);
        Button cancel = NewButton("Cancel", panel.transform, "Cancel", new Vector2(0.05f, 0.25f), new Vector2(0.48f, 0.33f));
        cancel.onClick.AddListener(() => panel.SetActive(false));
    }

    static Font DefaultFont()
    {
#if UNITY_2022_2_OR_NEWER
        return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
#else
        return Resources.GetBuiltinResource<Font>("Arial.ttf");
#endif
    }

    static GameObject NewImage(string name, Transform parent, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = color;
        return go;
    }

    static Text NewText(string name, Transform parent, string content, int size, TextAnchor anchor, Vector2 min, Vector2 max)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Text));
        go.transform.SetParent(parent, false);
        var text = go.GetComponent<Text>();
        text.font = DefaultFont();
        text.text = content;
        text.fontSize = size;
        text.alignment = anchor;
        text.color = Color.white;
        SetAnchors(text.rectTransform, min, max);
        return text;
    }

    static Button NewButton(string name, Transform parent, string label, Vector2 min, Vector2 max)
    {
        GameObject go = NewImage(name, parent, new Color(0.2f, 0.45f, 0.9f));
        SetAnchors(go.GetComponent<RectTransform>(), min, max);
        var button = go.AddComponent<Button>();
        button.targetGraphic = go.GetComponent<Image>();
        NewText("Label", go.transform, label, 44, TextAnchor.MiddleCenter, Vector2.zero, Vector2.one);
        return button;
    }

    static void SetAnchors(RectTransform rt, Vector2 min, Vector2 max)
    {
        rt.anchorMin = min;
        rt.anchorMax = max;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    static void Stretch(RectTransform rt, float left, float bottom, float right, float top)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(left, bottom);
        rt.offsetMax = new Vector2(-right, -top);
    }
}
