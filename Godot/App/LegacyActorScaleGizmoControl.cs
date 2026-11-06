using Godot;

namespace GBNG.App;

internal partial class LegacyActorScaleGizmoControl : Control
{
    private const float PickPadding = 14f;
    private const float ArrowHeadLength = 11f;
    private const float ArrowHeadHalfWidth = 5f;
    private static readonly Color DefaultColor = new(0.153f, 0.569f, 0.816f);
    private static readonly Color SelectedColor = new(0.18f, 0.95f, 0.42f);
    private Vector2 _center;
    private float _radius;
    private bool _handleVisible;
    private bool _hovered;

    public void SetHandle(bool visible, Vector2 center, float radius)
    {
        bool geometryChanged = _center != center || _radius != radius;
        _center = center;
        _radius = radius;
        if (_handleVisible != visible)
        {
            _handleVisible = visible;
            Visible = visible;
            geometryChanged = true;
        }
        if (!visible && _hovered)
        {
            _hovered = false;
            geometryChanged = true;
        }
        if (geometryChanged)
            QueueRedraw();
    }

    public void SetHovered(bool hovered)
    {
        if (_hovered == hovered)
            return;
        _hovered = hovered;
        QueueRedraw();
    }

    public bool TryPick(Vector2 screenPosition)
        => _handleVisible
            && screenPosition.DistanceSquaredTo(_center) <= Mathf.Pow(_radius + PickPadding, 2f);

    public override void _Draw()
    {
        if (!_handleVisible)
            return;

        Color color = _hovered ? SelectedColor : DefaultColor;
        DrawArc(_center, _radius, 0f, Mathf.Tau, 48, color, 4f, true);
        DrawCircle(_center, 5f, color);
        for (int index = 0; index < 4; index++)
        {
            float angle = Mathf.Tau * index / 4f;
            Vector2 direction = new(Mathf.Cos(angle), Mathf.Sin(angle));
            Vector2 tip = _center + direction * _radius;
            Vector2 basePoint = _center + direction * (_radius * 0.62f);
            Vector2 perpendicular = direction.Rotated(Mathf.Pi * 0.5f);
            DrawLine(basePoint, tip, color, 3f, true);
            DrawLine(tip, tip - direction * ArrowHeadLength + perpendicular * ArrowHeadHalfWidth, color, 3f, true);
            DrawLine(tip, tip - direction * ArrowHeadLength - perpendicular * ArrowHeadHalfWidth, color, 3f, true);
        }
    }
}
