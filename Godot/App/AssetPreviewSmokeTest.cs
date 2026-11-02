using System;
using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private void RunAssetPreviewSmoke()
    {
        try
        {
            const string uri = "builtin:BuiltinAssets/BubblegumDreams/Arch";
            int selectedIndex = -1;
            for (int index = 0; index < _assetPicker.ItemCount; index++)
            {
                if (_assetPicker.GetItemMetadata(index).AsString() == uri)
                {
                    selectedIndex = index;
                    break;
                }
            }
            if (selectedIndex < 0)
                throw new InvalidOperationException($"The asset picker does not contain '{uri}'.");
            _assetPicker.Select(selectedIndex);
            _assetPicker.EmitSignal(OptionButton.SignalName.ItemSelected, selectedIndex);
            GetTree().CreateTimer(0.3).Timeout += () =>
            {
                try
                {
                    if (_assetPreview.CurrentUri != uri || !_assetPreview.Has3DPreview)
                        throw new InvalidOperationException($"The selected asset did not produce a 3D preview: {_assetPreview.CurrentUri}.");
                    SubViewport viewport = _assetPreview.GetNode<SubViewport>("AssetPreviewContainer/AssetPreviewViewport");
                    Image image = viewport.GetTexture().GetImage();
                    if (image.IsEmpty())
                        throw new InvalidOperationException("The asset preview viewport rendered an empty image.");
                    image.SavePng(ProjectSettings.GlobalizePath("res://.godot/asset-preview-proof.png"));
                    GD.Print($"Asset preview smoke passed: {_assetPreview.CurrentUri}.");
                    GetTree().Quit();
                }
                catch (Exception exception)
                {
                    GD.PushError(exception.ToString());
                    GetTree().Quit(1);
                }
            };
        }
        catch (Exception exception)
        {
            GD.PushError(exception.ToString());
            GetTree().Quit(1);
        }
    }
}
