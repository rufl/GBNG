using System;
using Godot;

namespace GBNG.App;

internal sealed class LegacyTooltipPolicy : IDisposable
{
    private const string OriginalTooltipMetadata = "gbng_original_tooltip_text";

    private readonly SceneTree _sceneTree;
    private bool _enabled = true;

    public LegacyTooltipPolicy(SceneTree sceneTree)
    {
        _sceneTree = sceneTree;
        _sceneTree.NodeAdded += OnNodeAdded;
    }

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled)
            return;

        _enabled = enabled;
        ApplyToTree(_sceneTree.Root);
    }

    public void SetTooltip(Control control, string text)
    {
        if (_enabled)
        {
            control.TooltipText = text;
            return;
        }

        if (string.IsNullOrEmpty(text))
        {
            if (control.HasMeta(OriginalTooltipMetadata))
                control.RemoveMeta(OriginalTooltipMetadata);
            control.TooltipText = string.Empty;
            return;
        }

        control.SetMeta(OriginalTooltipMetadata, text);
        control.TooltipText = string.Empty;
    }

    public void Dispose()
    {
        _sceneTree.NodeAdded -= OnNodeAdded;
    }

    private void OnNodeAdded(Node node)
    {
        if (!_enabled && node is Control control)
            SuppressTooltip(control);
    }

    private void ApplyToTree(Node node)
    {
        if (node is Control control)
        {
            if (_enabled)
                RestoreTooltip(control);
            else
                SuppressTooltip(control);
        }

        for (int childIndex = 0; childIndex < node.GetChildCount(); childIndex++)
            ApplyToTree(node.GetChild(childIndex));
    }

    private static void SuppressTooltip(Control control)
    {
        if (!control.HasMeta(OriginalTooltipMetadata) && !string.IsNullOrEmpty(control.TooltipText))
            control.SetMeta(OriginalTooltipMetadata, control.TooltipText);
        control.TooltipText = string.Empty;
    }

    private static void RestoreTooltip(Control control)
    {
        if (!control.HasMeta(OriginalTooltipMetadata))
            return;

        control.TooltipText = control.GetMeta(OriginalTooltipMetadata).AsString();
        control.RemoveMeta(OriginalTooltipMetadata);
    }
}
