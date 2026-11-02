using System.IO;
using GBNG.Assets;
using Godot;

namespace GBNG.App;

public sealed partial class LegacyAssetPreviewControl : VBoxContainer
{
    private readonly Label _assetName = new() { AutowrapMode = TextServer.AutowrapMode.Word };
    private readonly Label _previewStatus = new() { AutowrapMode = TextServer.AutowrapMode.Word };
    private readonly SubViewport _viewport;
    private readonly SubViewportContainer _viewportContainer;
    private readonly TextureRect _imagePreview;
    private Node3D? _previewRoot;

    public string CurrentUri { get; private set; } = "";
    public bool Has3DPreview => _viewportContainer.Visible;

    public LegacyAssetPreviewControl()
    {
        Name = "LegacyAssetPreview";
        AddThemeConstantOverride("separation", 4);
        AddChild(new Label { Text = "Asset preview" });
        AddChild(_assetName);

        _viewport = new SubViewport
        {
            Name = "AssetPreviewViewport",
            Size = new Vector2I(320, 180),
            OwnWorld3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible
        };
        _viewportContainer = new SubViewportContainer
        {
            Name = "AssetPreviewContainer",
            CustomMinimumSize = new Vector2(0f, 150f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Stretch = true,
            Visible = false
        };
        _viewportContainer.AddChild(_viewport);
        AddChild(_viewportContainer);

        _imagePreview = new TextureRect
        {
            Name = "AssetImagePreview",
            CustomMinimumSize = new Vector2(0f, 150f),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Visible = false
        };
        AddChild(_imagePreview);
        AddChild(_previewStatus);
    }

    public void ShowAsset(RenderableEntry? entry)
    {
        if (entry is null)
        {
            ClearPreview();
            CurrentUri = "";
            _assetName.Text = "No asset selected";
            _previewStatus.Text = "No matching assets.";
            return;
        }
        if (entry.Uri == CurrentUri)
            return;

        ClearPreview();
        CurrentUri = entry.Uri;
        _assetName.Text = entry.Uri;
        string? modelPath = entry.SelectPrimaryModel();
        Node3D? modelRoot = modelPath is null ? null : LegacyRenderableCatalog.LoadModelInstance(modelPath);
        if (modelRoot is not null)
        {
            if (ShowModel(modelRoot))
            {
                _previewStatus.Text = $"3D model: {Path.GetFileName(modelPath)}";
                return;
            }
        }
        if (TryShowImage(entry))
            return;
        _previewStatus.Text = "No converted model or image preview is available.";
    }

    private bool ShowModel(Node3D modelRoot)
    {
        var previewRoot = new Node3D { Name = "PreviewWorld" };
        _previewRoot = previewRoot;
        _viewport.AddChild(previewRoot);
        previewRoot.AddChild(modelRoot);
        modelRoot.ForceUpdateTransform();

        Aabb? bounds = null;
        if (modelRoot is MeshInstance3D rootMesh && rootMesh.Mesh is not null)
            ExpandMeshBounds(rootMesh, ref bounds);
        foreach (Node node in modelRoot.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (node == modelRoot || node is not MeshInstance3D meshInstance || meshInstance.Mesh is null)
                continue;
            ExpandMeshBounds(meshInstance, ref bounds);
        }

        if (bounds is null)
        {
            ClearPreview();
            return false;
        }

        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color("#17212c"),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color("#b9c8da"),
            AmbientLightEnergy = 0.8f
        };
        previewRoot.AddChild(new WorldEnvironment { Environment = environment });
        previewRoot.AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-48f, -32f, 0f),
            LightEnergy = 1.4f
        });

        Aabb modelBounds = bounds.Value;
        Vector3 center = modelBounds.Position + modelBounds.Size * 0.5f;
        float radius = Mathf.Max(modelBounds.Size.Length() * 0.5f, 0.1f);
        var camera = new Camera3D { Current = true, Fov = 35f, Near = 0.01f, Far = radius * 40f };
        float distance = radius / Mathf.Tan(Mathf.DegToRad(camera.Fov * 0.5f)) * 1.2f;
        camera.Position = center + new Vector3(distance, distance * 0.65f, distance);
        previewRoot.AddChild(camera);
        camera.LookAt(center, Vector3.Up);
        _viewportContainer.Visible = true;
        return true;
    }

    private static void ExpandMeshBounds(MeshInstance3D meshInstance, ref Aabb? bounds)
    {
        meshInstance.ForceUpdateTransform();
        Aabb localBounds = meshInstance.GetAabb();
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 localPoint = localBounds.Position + new Vector3(
                (corner & 1) == 0 ? 0f : localBounds.Size.X,
                (corner & 2) == 0 ? 0f : localBounds.Size.Y,
                (corner & 4) == 0 ? 0f : localBounds.Size.Z);
            Vector3 worldPoint = meshInstance.ToGlobal(localPoint);
            bounds = bounds is null ? new Aabb(worldPoint, Vector3.Zero) : bounds.Value.Expand(worldPoint);
        }
    }

    private bool TryShowImage(RenderableEntry entry)
    {
        foreach (string imagePath in entry.ImagePaths)
        {
            Texture2D? texture = ResourceLoader.Load<Texture2D>(imagePath);
            if (texture is null)
                continue;
            _imagePreview.Texture = texture;
            _imagePreview.Visible = true;
            _previewStatus.Text = $"Image: {Path.GetFileName(imagePath)}";
            return true;
        }
        return false;
    }

    private void ClearPreview()
    {
        if (_previewRoot is not null && GodotObject.IsInstanceValid(_previewRoot))
            _previewRoot.QueueFree();
        _previewRoot = null;
        _viewportContainer.Visible = false;
        _imagePreview.Visible = false;
        _imagePreview.Texture = null;
    }
}
