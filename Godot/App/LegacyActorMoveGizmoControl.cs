using Godot;

namespace GBNG.App;

internal enum LegacyActorMoveGizmoAxis
{
    None,
    X,
    Y,
    Z
}

internal partial class LegacyActorMoveGizmoControl : Control
{
    private const float PickRadiusSquared = 100f;
    private static readonly Color XColor = new(0.91f, 0.118f, 0.145f);
    private static readonly Color YColor = new(0.416f, 0.741f, 0.271f);
    private static readonly Color ZColor = new(0.153f, 0.569f, 0.816f);
    private static readonly Color HoverColor = new(0.976f, 0.863f, 0.184f);
    private Vector2 _origin;
    private Vector2 _xEnd;
    private Vector2 _yEnd;
    private Vector2 _zEnd;
    private LegacyActorMoveGizmoAxis _hoveredAxis;
    private bool _handlesVisible;

    public void SetHandles(bool visible, Vector2 origin, Vector2 xEnd, Vector2 yEnd, Vector2 zEnd)
    {
        if (!visible)
        {
            if (_handlesVisible || Visible)
            {
                _handlesVisible = false;
                _hoveredAxis = LegacyActorMoveGizmoAxis.None;
                Visible = false;
                QueueRedraw();
            }
            return;
        }

        bool changed = !_handlesVisible
            || _origin != origin
            || _xEnd != xEnd
            || _yEnd != yEnd
            || _zEnd != zEnd;
        _handlesVisible = true;
        _origin = origin;
        _xEnd = xEnd;
        _yEnd = yEnd;
        _zEnd = zEnd;
        Visible = true;
        if (changed)
            QueueRedraw();
    }

    public void SetHoveredAxis(LegacyActorMoveGizmoAxis axis)
    {
        if (_hoveredAxis == axis)
            return;
        _hoveredAxis = axis;
        QueueRedraw();
    }

    public Vector2 GetAxisScreenDirection(LegacyActorMoveGizmoAxis axis)
        => (axis switch
        {
            LegacyActorMoveGizmoAxis.X => _xEnd,
            LegacyActorMoveGizmoAxis.Y => _yEnd,
            LegacyActorMoveGizmoAxis.Z => _zEnd,
            _ => _origin
        }) - _origin;

    public bool TryPickAxis(Vector2 screenPosition, out LegacyActorMoveGizmoAxis axis)
    {
        axis = LegacyActorMoveGizmoAxis.None;
        if (!_handlesVisible)
            return false;

        float nearestDistanceSquared = PickRadiusSquared;
        ConsiderAxis(screenPosition, _xEnd, LegacyActorMoveGizmoAxis.X, ref axis, ref nearestDistanceSquared);
        ConsiderAxis(screenPosition, _yEnd, LegacyActorMoveGizmoAxis.Y, ref axis, ref nearestDistanceSquared);
        ConsiderAxis(screenPosition, _zEnd, LegacyActorMoveGizmoAxis.Z, ref axis, ref nearestDistanceSquared);
        return axis != LegacyActorMoveGizmoAxis.None;
    }

    public override void _Draw()
    {
        if (!_handlesVisible)
            return;

        DrawAxis(_xEnd, XColor, LegacyActorMoveGizmoAxis.X);
        DrawAxis(_yEnd, YColor, LegacyActorMoveGizmoAxis.Y);
        DrawAxis(_zEnd, ZColor, LegacyActorMoveGizmoAxis.Z);
        DrawCircle(_origin, 6f, new Color(0.92f, 0.94f, 0.97f));
        DrawCircle(_origin, 3f, new Color(0.16f, 0.2f, 0.25f));
    }

    private void DrawAxis(Vector2 end, Color color, LegacyActorMoveGizmoAxis axis)
    {
        if (_hoveredAxis == axis)
            color = HoverColor;
        Vector2 direction = end - _origin;
        if (direction.LengthSquared() < 4f)
            return;
        direction = direction.Normalized();
        Vector2 side = new(-direction.Y, direction.X);
        DrawLine(_origin, end, color, 4f, true);
        DrawLine(end, end - direction * 12f + side * 5f, color, 4f, true);
        DrawLine(end, end - direction * 12f - side * 5f, color, 4f, true);
    }

    private void ConsiderAxis(
        Vector2 position,
        Vector2 end,
        LegacyActorMoveGizmoAxis candidate,
        ref LegacyActorMoveGizmoAxis current,
        ref float nearestDistanceSquared)
    {
        Vector2 segment = end - _origin;
        float segmentLengthSquared = segment.LengthSquared();
        if (segmentLengthSquared < 225f)
            return;

        float projection = (position - _origin).Dot(segment) / segmentLengthSquared;
        if (projection < 0.16f || projection > 1.15f)
            return;
        Vector2 closestPoint = _origin + segment * Mathf.Clamp(projection, 0f, 1f);
        float distanceSquared = position.DistanceSquaredTo(closestPoint);
        if (distanceSquared > nearestDistanceSquared)
            return;

        nearestDistanceSquared = distanceSquared;
        current = candidate;
    }
}
