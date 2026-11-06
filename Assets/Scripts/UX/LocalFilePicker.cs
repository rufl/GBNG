using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Cross-platform in-app file and folder picker. Results are delivered asynchronously;
/// cancellation returns null (or an empty selection for file dialogs).
/// </summary>
public class LocalFilePicker : MonoBehaviour
{
  enum PickerMode
  {
    OpenFiles,
    OpenFolder,
    SaveFile
  }

  static LocalFilePicker instance;

  const float ROW_HEIGHT = 38f;
  const float DOUBLE_CLICK_SECONDS = 0.4f;

  PickerMode mode;
  string currentDirectory;
  string[] extensions;
  string saveExtension;
  List<string> selectedFiles = new List<string>();
  string lastClickedPath;
  float lastClickTime;
  Action<string[]> filesCallback;
  Action<string> pathCallback;

  GameObject canvasObject;
  RectTransform listContent;
  RectTransform listViewport;
  InputField pathInput;
  TMP_InputField fileNameInput;
  TMP_Text statusText;
  TMP_Text filterText;
  TMP_Text emptyText;
  TMP_Text actionButtonText;
  Button actionButton;
  readonly List<GameObject> entryRows = new List<GameObject>();

  public static void OpenFiles(string title, string initialDirectory, string[] allowedExtensions,
    bool allowMultiple, Action<string[]> onSelected)
  {
    LocalFilePicker picker = GetInstance();
    picker.Begin(PickerMode.OpenFiles, title, initialDirectory, allowedExtensions, null,
      allowMultiple, onSelected, null, null);
  }

  public static void OpenFolder(string title, string initialDirectory, Action<string> onSelected)
  {
    LocalFilePicker picker = GetInstance();
    picker.Begin(PickerMode.OpenFolder, title, initialDirectory, null, null,
      false, null, onSelected, null);
  }

  public static void SaveFile(string title, string initialDirectory, string defaultFileName,
    string extension, Action<string> onSelected)
  {
    LocalFilePicker picker = GetInstance();
    picker.Begin(PickerMode.SaveFile, title, initialDirectory, null, extension,
      false, null, onSelected, defaultFileName);
  }

  static LocalFilePicker GetInstance()
  {
    if (instance != null) return instance;

    GameObject host = new GameObject("Local File Picker");
    DontDestroyOnLoad(host);
    instance = host.AddComponent<LocalFilePicker>();
    return instance;
  }

  void OnDestroy()
  {
    if (instance == this) instance = null;
  }

  void Update()
  {
    if (canvasObject != null && Input.GetKeyDown(KeyCode.Escape))
    {
      CancelCurrent();
    }
  }

  void Begin(PickerMode newMode, string title, string initialPath, string[] allowedExtensions,
    string extension, bool allowMultiple, Action<string[]> onFiles, Action<string> onPath,
    string defaultFileName)
  {
    if (canvasObject != null) CancelCurrent();

    mode = newMode;
    allowMultipleSelection = allowMultiple;
    extensions = NormalizeExtensions(allowedExtensions);
    saveExtension = NormalizeExtension(extension);
    filesCallback = onFiles;
    pathCallback = onPath;
    selectedFiles.Clear();
    lastClickedPath = null;
    currentDirectory = ResolveInitialDirectory(initialPath);

    EnsureEventSystem();
    BuildDialog(title, allowMultiple, defaultFileName);
    RefreshEntries(false);
  }

  static string[] NormalizeExtensions(string[] values)
  {
    if (values == null || values.Length == 0) return new string[0];
    List<string> normalized = new List<string>(values.Length);
    for (int i = 0; i < values.Length; i++)
    {
      string extension = NormalizeExtension(values[i]);
      if (extension.Length == 0 || normalized.Contains(extension)) continue;
      normalized.Add(extension);
    }
    return normalized.ToArray();
  }

  static string NormalizeExtension(string value)
  {
    if (string.IsNullOrWhiteSpace(value)) return string.Empty;
    string extension = value.Trim();
    if (extension.StartsWith("*.", StringComparison.Ordinal)) extension = extension.Substring(1);
    if (!extension.StartsWith(".", StringComparison.Ordinal)) extension = "." + extension;
    if (extension.Length <= 1 || extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
        extension.IndexOf('/') >= 0 || extension.IndexOf('\\') >= 0)
    {
      return string.Empty;
    }
    return extension;
  }

  static string ResolveInitialDirectory(string requestedPath)
  {
    string candidate = requestedPath;
    if (string.IsNullOrWhiteSpace(candidate))
    {
      candidate = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    try
    {
      if (!string.IsNullOrWhiteSpace(candidate))
      {
        candidate = Path.GetFullPath(candidate);
        if (Directory.Exists(candidate)) return candidate;
      }
    }
    catch (Exception)
    {
      // Fall through to a directory that is expected to exist on this platform.
    }

    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    try
    {
      if (!string.IsNullOrWhiteSpace(home) && Directory.Exists(home)) return Path.GetFullPath(home);
      return Path.GetFullPath(Environment.CurrentDirectory);
    }
    catch (Exception)
    {
      return Path.GetPathRoot(Environment.CurrentDirectory);
    }
  }

  static void EnsureEventSystem()
  {
    if (UnityEngine.Object.FindObjectOfType<EventSystem>() != null) return;
    GameObject eventSystem = new GameObject("Local File Picker EventSystem",
      typeof(EventSystem), typeof(StandaloneInputModule));
    DontDestroyOnLoad(eventSystem);
  }

  void BuildDialog(string title, bool allowMultiple, string defaultFileName)
  {
    canvasObject = new GameObject("Local File Picker Canvas",
      typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
    DontDestroyOnLoad(canvasObject);

    Canvas canvas = canvasObject.GetComponent<Canvas>();
    canvas.renderMode = RenderMode.ScreenSpaceOverlay;
    canvas.overrideSorting = true;
    canvas.sortingOrder = 32000;

    CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
    scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
    scaler.referenceResolution = new Vector2(1920f, 1080f);
    scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
    scaler.matchWidthOrHeight = 0.5f;

    Image scrim = CreateImage("Scrim", canvasObject.transform, new Color(0f, 0f, 0f, 0.76f));
    SetAnchors((RectTransform)scrim.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero,
      new Vector2(0.5f, 0.5f));

    Image panel = CreateImage("Picker Panel", canvasObject.transform, new Color(0.105f, 0.125f, 0.14f, 1f));
    SetFixedRect((RectTransform)panel.transform, Vector2.zero, new Vector2(1100f, 760f));

    CreateText("Title", title, panel.transform, 28f, Color.white, TextAlignmentOptions.Center,
      new Vector2(0f, 330f), new Vector2(1040f, 46f));

    CreatePathControls(panel.transform);
    filterText = CreateText("Filter Info", string.Empty, panel.transform, 16f,
      new Color(0.77f, 0.83f, 0.85f), TextAlignmentOptions.Left,
      new Vector2(0f, 228f), new Vector2(1020f, 24f));
    filterText.rectTransform.pivot = new Vector2(0.5f, 0.5f);

    BuildFileList(panel.transform);
    statusText = CreateText("Status", string.Empty, panel.transform, 16f,
      new Color(0.85f, 0.9f, 0.92f), TextAlignmentOptions.Left,
      new Vector2(0f, -278f), new Vector2(1020f, 28f));
    statusText.rectTransform.pivot = new Vector2(0.5f, 0.5f);

    if (mode == PickerMode.SaveFile)
    {
      fileNameInput = CreateInput("File Name", panel.transform, new Vector2(-95f, -318f),
        new Vector2(640f, 42f), defaultFileName ?? string.Empty, "File name");
      fileNameInput.gameObject.SetActive(true);
    }
    else
    {
      fileNameInput = null;
    }

    string actionLabel = mode == PickerMode.SaveFile ? "Save" :
      mode == PickerMode.OpenFolder ? "Select this folder" : (allowMultiple ? "Open (0)" : "Open");
    actionButton = CreateButton("Picker Action", actionLabel, panel.transform,
      new Vector2(mode == PickerMode.OpenFolder ? 290f : 330f, -352f),
      new Vector2(mode == PickerMode.OpenFolder ? 220f : 160f, 46f),
      new Color(0.25f, 0.59f, 0.25f));
    actionButtonText = actionButton.GetComponentInChildren<TMP_Text>();
    actionButton.onClick.AddListener(ConfirmSelection);

    Button cancelButton = CreateButton("Cancel", "Cancel", panel.transform,
      new Vector2(470f, -352f), new Vector2(120f, 46f), new Color(0.32f, 0.36f, 0.38f));
    cancelButton.onClick.AddListener(CancelCurrent);

    if (mode == PickerMode.OpenFiles)
    {
      filterText.text = extensions.Length == 0
        ? (allowMultiple ? "Choose one or more files" : "Choose a file")
        : "Allowed: " + string.Join(", ", extensions) + (allowMultiple ? " — choose one or more files" : "");
    }
    else if (mode == PickerMode.OpenFolder)
    {
      filterText.text = "Open a folder, then select the current folder";
    }
    else
    {
      filterText.text = "Enter a file name. Existing files require overwrite confirmation.";
    }
  }

  void CreatePathControls(Transform parent)
  {
    Button upButton = CreateButton("Up", "Up", parent, new Vector2(-490f, 275f),
      new Vector2(70f, 42f), new Color(0.27f, 0.32f, 0.34f));
    upButton.onClick.AddListener(NavigateUp);

    pathInput = CreatePathInput(parent, new Vector2(-55f, 275f),
      new Vector2(770f, 42f), currentDirectory);
    pathInput.onEndEdit.AddListener(NavigateToPath);

    Button refreshButton = CreateButton("Refresh", "Refresh", parent, new Vector2(370f, 275f),
      new Vector2(72f, 42f), new Color(0.27f, 0.32f, 0.34f));
    refreshButton.onClick.AddListener(() => RefreshEntries(true));

    Button goButton = CreateButton("Go", "Go", parent, new Vector2(485f, 275f),
      new Vector2(86f, 42f), new Color(0.27f, 0.42f, 0.48f));
    goButton.onClick.AddListener(() => NavigateToPath(pathInput.text));
  }

  void BuildFileList(Transform parent)
  {
    Image viewportImage = CreateImage("File List Viewport", parent, new Color(0.065f, 0.08f, 0.09f, 1f));
    listViewport = (RectTransform)viewportImage.transform;
    SetFixedRect(listViewport, new Vector2(0f, -20f), new Vector2(1040f, 474f));
    viewportImage.gameObject.AddComponent<RectMask2D>();

    ScrollRect scroll = viewportImage.gameObject.AddComponent<ScrollRect>();
    scroll.viewport = listViewport;
    scroll.horizontal = false;
    scroll.vertical = true;
    scroll.inertia = false;
    scroll.movementType = ScrollRect.MovementType.Clamped;

    GameObject contentObject = new GameObject("Entries", typeof(RectTransform));
    listContent = (RectTransform)contentObject.transform;
    listContent.SetParent(listViewport, false);
    listContent.anchorMin = new Vector2(0f, 1f);
    listContent.anchorMax = new Vector2(1f, 1f);
    listContent.pivot = new Vector2(0.5f, 1f);
    listContent.anchoredPosition = Vector2.zero;
    listContent.sizeDelta = new Vector2(0f, 0f);
    scroll.content = listContent;

    emptyText = CreateText("Empty State", "No matching files or folders.", viewportImage.transform,
      18f, new Color(0.65f, 0.7f, 0.72f), TextAlignmentOptions.Center,
      Vector2.zero, new Vector2(900f, 42f));
    emptyText.rectTransform.pivot = new Vector2(0.5f, 0.5f);
  }

  static Image CreateImage(string name, Transform parent, Color color)
  {
    GameObject obj = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
    obj.transform.SetParent(parent, false);
    Image image = obj.GetComponent<Image>();
    image.color = color;
    return image;
  }

  static TMP_Text CreateText(string name, string value, Transform parent, float fontSize, Color color,
    TextAlignmentOptions alignment, Vector2 position, Vector2 size)
  {
    GameObject obj = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
    obj.transform.SetParent(parent, false);
    RectTransform rect = (RectTransform)obj.transform;
    rect.anchorMin = new Vector2(0.5f, 0.5f);
    rect.anchorMax = new Vector2(0.5f, 0.5f);
    rect.pivot = new Vector2(0.5f, 0.5f);
    rect.anchoredPosition = position;
    rect.sizeDelta = size;

    TMP_Text text = obj.GetComponent<TMP_Text>();
    text.text = value;
    text.font = TMP_Settings.defaultFontAsset;
    text.fontSize = fontSize;
    text.color = color;
    text.alignment = alignment;
    text.textWrappingMode = TextWrappingModes.NoWrap;
    text.raycastTarget = false;
    return text;
  }

  static Button CreateButton(string name, string label, Transform parent, Vector2 position,
    Vector2 size, Color color)
  {
    Image image = CreateImage(name, parent, color);
    SetFixedRect((RectTransform)image.transform, position, size);
    Button button = image.gameObject.AddComponent<Button>();
    button.targetGraphic = image;
    ColorBlock colors = button.colors;
    colors.normalColor = color;
    colors.highlightedColor = Color.Lerp(color, Color.white, 0.18f);
    colors.pressedColor = Color.Lerp(color, Color.black, 0.18f);
    colors.selectedColor = colors.highlightedColor;
    colors.disabledColor = new Color(color.r, color.g, color.b, 0.45f);
    button.colors = colors;

    TMP_Text text = CreateText("Label", label, image.transform, 18f, Color.white,
      TextAlignmentOptions.Center, Vector2.zero, Vector2.zero);
    text.rectTransform.anchorMin = Vector2.zero;
    text.rectTransform.anchorMax = Vector2.one;
    text.rectTransform.offsetMin = new Vector2(5f, 2f);
    text.rectTransform.offsetMax = new Vector2(-5f, -2f);
    return button;
  }

  static TMP_InputField CreateInput(string name, Transform parent, Vector2 position,
    Vector2 size, string initialValue, string placeholderValue)
  {
    Image background = CreateImage(name, parent, new Color(0.035f, 0.045f, 0.05f, 1f));
    SetFixedRect((RectTransform)background.transform, position, size);
    background.gameObject.SetActive(false);
    TMP_InputField input = background.gameObject.AddComponent<TMP_InputField>();
    input.targetGraphic = background;
    input.lineType = TMP_InputField.LineType.SingleLine;
    input.characterLimit = 4096;

    GameObject viewportObject = new GameObject("Text Viewport", typeof(RectTransform), typeof(RectMask2D));
    viewportObject.transform.SetParent(background.transform, false);
    RectTransform viewport = (RectTransform)viewportObject.transform;
    viewport.anchorMin = Vector2.zero;
    viewport.anchorMax = Vector2.one;
    viewport.offsetMin = new Vector2(10f, 5f);
    viewport.offsetMax = new Vector2(-10f, -5f);
    input.textViewport = viewport;

    TMP_Text text = CreateText("Text", string.Empty, viewport, 18f, Color.white,
      TextAlignmentOptions.MidlineLeft, Vector2.zero, Vector2.zero);
    text.rectTransform.anchorMin = Vector2.zero;
    text.rectTransform.anchorMax = Vector2.one;
    text.rectTransform.offsetMin = Vector2.zero;
    text.rectTransform.offsetMax = Vector2.zero;
    text.textWrappingMode = TextWrappingModes.PreserveWhitespaceNoWrap;
    input.textComponent = text;
    input.fontAsset = text.font;

    TMP_Text placeholder = CreateText("Placeholder", placeholderValue, viewport, 18f,
      new Color(0.55f, 0.6f, 0.62f), TextAlignmentOptions.MidlineLeft,
      Vector2.zero, Vector2.zero);
    placeholder.rectTransform.anchorMin = Vector2.zero;
    placeholder.rectTransform.anchorMax = Vector2.one;
    placeholder.rectTransform.offsetMin = Vector2.zero;
    placeholder.rectTransform.offsetMax = Vector2.zero;
    input.placeholder = placeholder;
    input.text = initialValue;
    placeholder.gameObject.SetActive(string.IsNullOrEmpty(initialValue));
    background.gameObject.SetActive(true);
    input.ForceLabelUpdate();
    return input;
  }
  static InputField CreatePathInput(Transform parent, Vector2 position, Vector2 size, string initialValue)
  {
    Image background = CreateImage("Current Path", parent, new Color(0.035f, 0.045f, 0.05f, 1f));
    SetFixedRect((RectTransform)background.transform, position, size);
    background.gameObject.SetActive(false);

    InputField input = background.gameObject.AddComponent<InputField>();
    input.targetGraphic = background;
    input.lineType = InputField.LineType.SingleLine;
    input.characterLimit = 4096;

    GameObject viewportObject = new GameObject("Text Viewport", typeof(RectTransform), typeof(RectMask2D));
    viewportObject.transform.SetParent(background.transform, false);
    RectTransform viewport = (RectTransform)viewportObject.transform;
    viewport.anchorMin = Vector2.zero;
    viewport.anchorMax = Vector2.one;
    viewport.offsetMin = new Vector2(10f, 5f);
    viewport.offsetMax = new Vector2(-10f, -5f);

    GameObject textObject = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
    textObject.transform.SetParent(viewport, false);
    RectTransform textRect = (RectTransform)textObject.transform;
    textRect.anchorMin = Vector2.zero;
    textRect.anchorMax = Vector2.one;
    textRect.offsetMin = Vector2.zero;
    textRect.offsetMax = Vector2.zero;

    Text text = textObject.GetComponent<Text>();
    TMP_FontAsset defaultFont = TMP_Settings.defaultFontAsset;
    text.font = defaultFont != null && defaultFont.sourceFontFile != null
      ? defaultFont.sourceFontFile
      : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
    text.fontSize = 18;
    text.color = Color.white;
    text.alignment = TextAnchor.MiddleLeft;
    text.horizontalOverflow = HorizontalWrapMode.Overflow;
    text.verticalOverflow = VerticalWrapMode.Truncate;
    text.raycastTarget = false;
    input.textComponent = text;
    input.text = initialValue;

    background.gameObject.SetActive(true);
    input.ForceLabelUpdate();
    return input;
  }

  static void SetFixedRect(RectTransform rect, Vector2 position, Vector2 size)
  {
    rect.anchorMin = new Vector2(0.5f, 0.5f);
    rect.anchorMax = new Vector2(0.5f, 0.5f);
    rect.pivot = new Vector2(0.5f, 0.5f);
    rect.anchoredPosition = position;
    rect.sizeDelta = size;
  }

  static void SetAnchors(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax,
    Vector2 offsetMin, Vector2 offsetMax, Vector2 pivot)
  {
    rect.anchorMin = anchorMin;
    rect.anchorMax = anchorMax;
    rect.pivot = pivot;
    rect.offsetMin = offsetMin;
    rect.offsetMax = offsetMax;
  }

  void NavigateUp()
  {
    DirectoryInfo parent = Directory.GetParent(currentDirectory);
    if (parent != null) NavigateToPath(parent.FullName);
  }

  void NavigateToPath(string requestedPath)
  {
    if (string.IsNullOrWhiteSpace(requestedPath))
    {
      SetStatus("Enter a folder path.", true);
      return;
    }

    try
    {
      string fullPath = Path.GetFullPath(requestedPath.Trim());
      if (!Directory.Exists(fullPath))
      {
        SetStatus("Folder does not exist: " + fullPath, true);
        return;
      }
      currentDirectory = fullPath;
      if (pathInput != null && !string.Equals(pathInput.text, fullPath, StringComparison.Ordinal))
      {
        pathInput.text = fullPath;
        pathInput.ForceLabelUpdate();
      }
      selectedFiles.Clear();
      RefreshEntries(false);
    }
    catch (Exception exception)
    {
      SetStatus("Cannot open folder: " + exception.Message, true);
    }
  }

  void RefreshEntries(bool clearSelection)
  {
    if (canvasObject == null || listContent == null) return;
    if (clearSelection) selectedFiles.Clear();

    for (int i = 0; i < entryRows.Count; i++) Destroy(entryRows[i]);
    entryRows.Clear();

    if (!Directory.Exists(currentDirectory))
    {
      SetStatus("Folder does not exist: " + currentDirectory, true);
      return;
    }

    try
    {
      List<string> directories = new List<string>(Directory.GetDirectories(currentDirectory));
      directories.Sort(StringComparer.OrdinalIgnoreCase);
      List<string> files = new List<string>();
      if (mode != PickerMode.OpenFolder)
      {
        string[] foundFiles = Directory.GetFiles(currentDirectory);
        for (int i = 0; i < foundFiles.Length; i++)
        {
          if (FileMatchesFilters(foundFiles[i], extensions)) files.Add(foundFiles[i]);
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
      }

      int row = 0;
      for (int i = 0; i < directories.Count; i++)
      {
        string path = directories[i];
        AddEntryRow(path, true, row++);
      }
      for (int i = 0; i < files.Count; i++)
      {
        string path = files[i];
        AddEntryRow(path, false, row++);
      }

      listContent.sizeDelta = new Vector2(0f, row * ROW_HEIGHT);
      listContent.anchoredPosition = Vector2.zero;
      emptyText.gameObject.SetActive(row == 0);
      SetStatus(row == 0 ? "No matching files or folders." : "");
      if (actionButton != null) UpdateActionState();
    }
    catch (Exception exception)
    {
      listContent.sizeDelta = Vector2.zero;
      emptyText.gameObject.SetActive(true);
      SetStatus("Cannot list folder: " + exception.Message, true);
    }
  }

  static bool FileMatchesFilters(string path, string[] allowedExtensions)
  {
    if (allowedExtensions == null || allowedExtensions.Length == 0) return true;
    string extension = Path.GetExtension(path);
    for (int i = 0; i < allowedExtensions.Length; i++)
    {
      if (string.Equals(extension, allowedExtensions[i], StringComparison.OrdinalIgnoreCase)) return true;
    }
    return false;
  }

  void AddEntryRow(string path, bool isDirectory, int index)
  {
    string label = isDirectory ? "[DIR]  " + Path.GetFileName(path) : Path.GetFileName(path);
    if (!isDirectory && selectedFiles.Contains(path)) label = "[x]  " + label;

    Button rowButton = CreateButton("Entry " + index, label, listContent,
      new Vector2(0f, -index * ROW_HEIGHT - ROW_HEIGHT * 0.5f),
      new Vector2(1018f, ROW_HEIGHT - 2f),
      isDirectory ? new Color(0.17f, 0.2f, 0.21f) : new Color(0.12f, 0.15f, 0.16f));
    RectTransform rowRect = (RectTransform)rowButton.transform;
    rowRect.anchorMin = new Vector2(0.5f, 1f);
    rowRect.anchorMax = new Vector2(0.5f, 1f);
    rowRect.pivot = new Vector2(0.5f, 0.5f);

    string capturedPath = path;
    bool capturedIsDirectory = isDirectory;
    rowButton.onClick.AddListener(() => OnEntryClicked(capturedPath, capturedIsDirectory));
    entryRows.Add(rowButton.gameObject);
  }

  void OnEntryClicked(string path, bool isDirectory)
  {
    if (isDirectory)
    {
      NavigateToPath(path);
      return;
    }
    if (mode != PickerMode.OpenFiles) return;

    if (allowMultipleSelection)
    {
      if (selectedFiles.Contains(path)) selectedFiles.Remove(path);
      else selectedFiles.Add(path);
      lastClickedPath = null;
      RefreshEntries(false);
      return;
    }

    bool doubleClick = lastClickedPath == path && Time.unscaledTime - lastClickTime <= DOUBLE_CLICK_SECONDS;
    selectedFiles.Clear();
    selectedFiles.Add(path);
    lastClickedPath = path;
    lastClickTime = Time.unscaledTime;
    RefreshEntries(false);
    if (doubleClick) ConfirmSelection();
  }

  bool allowMultipleSelection;

  void UpdateActionState()
  {
    if (actionButton == null) return;
    if (mode == PickerMode.OpenFiles)
    {
      bool allowMultiple = allowMultipleSelection;
      actionButton.interactable = selectedFiles.Count > 0;
      actionButtonText.text = allowMultiple ? "Open (" + selectedFiles.Count + ")" : "Open";
    }
    else
    {
      actionButton.interactable = true;
      actionButtonText.text = mode == PickerMode.SaveFile ? "Save" : "Select this folder";
    }
  }

  void ConfirmSelection()
  {
    if (mode == PickerMode.OpenFiles)
    {
      if (selectedFiles.Count == 0) return;
      FinishFiles(selectedFiles.ToArray());
      return;
    }

    if (mode == PickerMode.OpenFolder)
    {
      FinishPath(currentDirectory);
      return;
    }

    SaveSelection();
  }

  void SaveSelection()
  {
    string fileName = fileNameInput != null ? fileNameInput.text.Trim() : string.Empty;
    if (string.IsNullOrWhiteSpace(fileName))
    {
      SetStatus("Enter a file name.", true);
      return;
    }
    if (fileName == "." || fileName == ".." || Path.IsPathRooted(fileName) ||
        fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
        fileName.IndexOf('/') >= 0 || fileName.IndexOf('\\') >= 0)
    {
      SetStatus("Enter a file name, not a path.", true);
      return;
    }
    if (!string.IsNullOrEmpty(saveExtension) &&
        !fileName.EndsWith(saveExtension, StringComparison.OrdinalIgnoreCase))
    {
      fileName += saveExtension;
    }

    string fullPath;
    try
    {
      fullPath = Path.GetFullPath(Path.Combine(currentDirectory, fileName));
    }
    catch (Exception exception)
    {
      SetStatus("Invalid file name: " + exception.Message, true);
      return;
    }

    if (File.Exists(fullPath))
    {
      ShowOverwriteConfirmation(fullPath);
      return;
    }
    FinishPath(fullPath);
  }

  void ShowOverwriteConfirmation(string fullPath)
  {
    Image scrim = CreateImage("Overwrite Scrim", canvasObject.transform, new Color(0f, 0f, 0f, 0.68f));
    SetAnchors((RectTransform)scrim.transform, Vector2.zero, Vector2.one, Vector2.zero,
      Vector2.zero, new Vector2(0.5f, 0.5f));
    Image prompt = CreateImage("Overwrite Confirmation", canvasObject.transform,
      new Color(0.16f, 0.19f, 0.2f, 1f));
    SetFixedRect((RectTransform)prompt.transform, Vector2.zero, new Vector2(600f, 220f));
    CreateText("Overwrite Message", "Replace existing file?\n" + Path.GetFileName(fullPath),
      prompt.transform, 22f, Color.white, TextAlignmentOptions.Center,
      new Vector2(0f, 38f), new Vector2(560f, 100f));

    Button overwrite = CreateButton("Overwrite", "Overwrite", prompt.transform,
      new Vector2(155f, -70f), new Vector2(180f, 46f), new Color(0.65f, 0.31f, 0.22f));
    Button keep = CreateButton("Keep Existing", "Cancel", prompt.transform,
      new Vector2(-155f, -70f), new Vector2(180f, 46f), new Color(0.32f, 0.36f, 0.38f));
    overwrite.onClick.AddListener(() => FinishPath(fullPath));
    keep.onClick.AddListener(() =>
    {
      Destroy(prompt.gameObject);
      Destroy(scrim.gameObject);
      SetStatus("Save cancelled; existing file was not changed.");
    });
  }

  void SetStatus(string message, bool isError = false)
  {
    if (statusText == null) return;
    statusText.text = message ?? string.Empty;
    statusText.color = isError ? new Color(1f, 0.53f, 0.45f) : new Color(0.85f, 0.9f, 0.92f);
  }

  void CancelCurrent()
  {
    if (mode == PickerMode.OpenFiles) FinishFiles(null);
    else FinishPath(null);
  }

  void FinishFiles(string[] result)
  {
    Action<string[]> callback = filesCallback;
    filesCallback = null;
    pathCallback = null;
    CloseDialog();
    if (callback != null) callback(result);
  }

  void FinishPath(string result)
  {
    Action<string> callback = pathCallback;
    filesCallback = null;
    pathCallback = null;
    CloseDialog();
    if (callback != null) callback(result);
  }

  void CloseDialog()
  {
    GameObject oldCanvas = canvasObject;
    canvasObject = null;
    listContent = null;
    listViewport = null;
    pathInput = null;
    fileNameInput = null;
    statusText = null;
    filterText = null;
    emptyText = null;
    actionButton = null;
    actionButtonText = null;
    entryRows.Clear();
    selectedFiles.Clear();
    if (oldCanvas != null) Destroy(oldCanvas);
  }
}
