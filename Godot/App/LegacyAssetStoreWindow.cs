using System;
using System.Linq;
using GBNG.Assets;
using Godot;

namespace GBNG.App;

public sealed partial class LegacyAssetStoreWindow : Window
{
    private readonly LegacyRenderableCatalog _catalog;
    private readonly Func<bool> _canAddAsset;
    private readonly Func<string, bool> _addAsset;
    private readonly Action _importModel;
    private readonly LineEdit _search = new();
    private readonly OptionButton _category = new();
    private readonly ItemList _assets = new();
    private readonly LegacyAssetPreviewControl _preview = new();
    private readonly Label _licenseDetails = new() { AutowrapMode = TextServer.AutowrapMode.Word };
    private readonly Label _status = new() { AutowrapMode = TextServer.AutowrapMode.Word };
    private readonly Button _addButton = new();
    private string _selectedUri = "";

    public LegacyAssetStoreWindow(
        LegacyRenderableCatalog catalog,
        Func<bool> canAddAsset,
        Func<string, bool> addAsset,
        Action importModel)
    {
        _catalog = catalog;
        _canAddAsset = canAddAsset;
        _addAsset = addAsset;
        _importModel = importModel;
        Title = "GBNG Asset Store · offline";
        Size = new Vector2I(1080, 700);
        CloseRequested += () => Hide();
        BuildInterface();
        RefreshCatalog();
    }

    public void RefreshCatalog()
    {
        int previousCategory = _category.Selected;
        string previousCategoryName = previousCategory > 0 ? _category.GetItemText(previousCategory) : "";
        _category.Clear();
        _category.AddItem("All categories");
        foreach (string category in _catalog.Entries
            .Select(GetCategory)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            _category.AddItem(category);

        int categoryIndex = 0;
        for (int index = 1; index < _category.ItemCount; index++)
        {
            if (_category.GetItemText(index) != previousCategoryName)
                continue;
            categoryIndex = index;
            break;
        }
        _category.Select(categoryIndex);
        RefreshAssets(selectFirstIfNeeded: IsInsideTree() && Visible);
    }

    private void BuildInterface()
    {
        var margin = new MarginContainer { AnchorRight = 1f, AnchorBottom = 1f };
        margin.AddThemeConstantOverride("margin_left", 14);
        margin.AddThemeConstantOverride("margin_top", 14);
        margin.AddThemeConstantOverride("margin_right", 14);
        margin.AddThemeConstantOverride("margin_bottom", 14);
        AddChild(margin);

        var layout = new VBoxContainer();
        layout.AddThemeConstantOverride("separation", 10);
        margin.AddChild(layout);
        layout.AddChild(new Label
        {
            Text = "Browse the built-in Game Builder catalog or models imported into this project. This store is offline: it uses no account, remote search, or third-party asset service.",
            AutowrapMode = TextServer.AutowrapMode.Word
        });

        var filters = new HBoxContainer();
        _search.Name = "AssetStoreSearch";
        _search.PlaceholderText = "Search assets by name or path…";
        _search.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _search.TextChanged += _ => RefreshAssets(selectFirstIfNeeded: true);
        filters.AddChild(_search);
        _category.Name = "AssetStoreCategory";
        _category.CustomMinimumSize = new Vector2(220f, 0f);
        _category.ItemSelected += _ => RefreshAssets(selectFirstIfNeeded: true);
        filters.AddChild(_category);
        layout.AddChild(filters);

        var columns = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        layout.AddChild(columns);
        var left = new VBoxContainer { CustomMinimumSize = new Vector2(330f, 0f) };
        columns.AddChild(left);
        _assets.Name = "AssetStoreItems";
        _assets.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _assets.ItemSelected += _ => ShowSelectedAsset();
        left.AddChild(_assets);

        var rightScroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        columns.AddChild(rightScroll);
        var right = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        right.AddThemeConstantOverride("separation", 8);
        rightScroll.AddChild(right);
        _preview.CustomMinimumSize = new Vector2(0f, 240f);
        right.AddChild(_preview);
        _licenseDetails.Name = "AssetStoreLicenseDetails";
        right.AddChild(_licenseDetails);

        var actions = new HBoxContainer();
        var import = new Button { Name = "AssetStoreImportModel", Text = "Import local model…" };
        import.Pressed += () =>
        {
            Hide();
            _importModel();
        };
        actions.AddChild(import);
        _addButton.Name = "AssetStoreAddActor";
        _addButton.Text = "Add to project";
        _addButton.Pressed += AddSelectedAsset;
        actions.AddChild(_addButton);
        layout.AddChild(actions);
        _status.Name = "AssetStoreStatus";
        layout.AddChild(_status);
    }

    private void RefreshAssets(bool selectFirstIfNeeded)
    {
        string selectedUri = _selectedUri;
        string search = _search.Text.Trim();
        string category = _category.Selected > 0 ? _category.GetItemText(_category.Selected) : "";
        _assets.Clear();
        int selectedIndex = -1;
        foreach (RenderableEntry entry in _catalog.Entries.OrderBy(item => item.Uri, StringComparer.OrdinalIgnoreCase))
        {
            if (category.Length > 0 && GetCategory(entry) != category)
                continue;
            if (search.Length > 0 && !entry.Uri.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;

            _assets.AddItem($"{GetAssetName(entry)}  ·  {GetCategory(entry)}");
            _assets.SetItemMetadata(_assets.ItemCount - 1, entry.Uri);
            if (entry.Uri == selectedUri)
                selectedIndex = _assets.ItemCount - 1;
        }

        if (selectedIndex < 0 && selectFirstIfNeeded && _assets.ItemCount > 0)
            selectedIndex = 0;
        if (selectedIndex >= 0)
        {
            _assets.Select(selectedIndex);
            ShowSelectedAsset();
        }
        else
        {
            _selectedUri = "";
            _preview.ShowAsset(null);
            _licenseDetails.Text = "Select an asset to view its preview and license evidence.";
        }

        int bundledCount = _catalog.Entries.Count(entry => !entry.Uri.StartsWith("custom:", StringComparison.Ordinal));
        int importedCount = _catalog.Entries.Count - bundledCount;
        _status.Text = _assets.ItemCount == 0
            ? "No assets match this search."
            : $"{_assets.ItemCount} shown · {bundledCount} bundled · {importedCount} imported · offline";
        _addButton.Disabled = _assets.ItemCount == 0 || _selectedUri.Length == 0 || !_canAddAsset();
    }

    private void ShowSelectedAsset()
    {
        int[] selectedItems = _assets.GetSelectedItems();
        if (selectedItems.Length == 0)
        {
            _selectedUri = "";
            _preview.ShowAsset(null);
            _licenseDetails.Text = "Select an asset to view its preview and license evidence.";
            _addButton.Disabled = true;
            return;
        }

        string uri = _assets.GetItemMetadata(selectedItems[0]).AsString();
        if (!_catalog.TryGet(uri, out RenderableEntry entry))
            return;
        _selectedUri = uri;
        _preview.ShowAsset(entry);
        string sourcePath = entry.SelectPrimaryModel()
            ?? entry.ImagePaths.FirstOrDefault()
            ?? entry.PrefabPath;
        string licenseEvidence = uri.StartsWith("custom:", StringComparison.Ordinal)
            ? "Imported model. GBNG does not verify its license; check the source terms before reuse or sharing."
            : "Repository inventory records Apache License 2.0 evidence. Review the entry in tools/asset-inventory/ before redistribution.";
        _licenseDetails.Text = $"{uri}\nSource: {sourcePath}\n{licenseEvidence}";
        _addButton.Disabled = !_canAddAsset();
    }

    private void AddSelectedAsset()
    {
        if (_selectedUri.Length == 0 || !_canAddAsset())
            return;
        _status.Text = _addAsset(_selectedUri)
            ? $"Added {GetAssetName(_selectedUri)} to the project."
            : "Asset was not added; check project state and asset synchronization.";
    }

    private static string GetAssetName(RenderableEntry entry) => GetAssetName(entry.Uri);

    private static string GetAssetName(string uri)
    {
        int separator = uri.LastIndexOf('/');
        return separator >= 0 ? uri[(separator + 1)..] : uri;
    }

    private static string GetCategory(RenderableEntry entry)
    {
        if (entry.Uri.StartsWith("custom:", StringComparison.Ordinal))
            return "Imported";
        string relativePath = entry.Uri.StartsWith("builtin:", StringComparison.Ordinal)
            ? entry.Uri["builtin:".Length..].TrimStart('/')
            : entry.Uri;
        string[] segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 1 && segments[0] == "BuiltinAssets")
            return segments.Length > 2 ? segments[1] : "General";
        return segments.Length > 1 ? segments[0] : "Built-in";
    }
}
