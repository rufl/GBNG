using Godot;

namespace GBNG.App;

internal partial class LegacyActorRotateGizmoControl : Control
{
    public const int RingSegments = 72;
    private const float PickRadiusSquared = 144f;
    private static readonly Color XColor = new(0.91f, 0.118f, 0.145f);
    private static readonly Color YColor = new(0.416f, 0.741f, 0.271f);
    private static readonly Color ZColor = new(0.153f, 0.569f, 0.816f);
    private static readonly Color HoverColor = new(0.976f, 0.863f, 0.184f);
    private readonly Vector2[] _xRing = new Vector2[RingSegments + 1];
    private readonly Vector2[] _yRing = new Vector2[RingSegments + 1];
    private readonly Vector2[] _zRing = new Vector2[RingSegments + 1];
    private LegacyActorMoveGizmoAxis _hoveredAxis;
    private bool _handlesVisible;
    private bool _geometryDirty;

    public void SetHandlesVisible(bool visible)
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

        _handlesVisible = true;
        Visible = true;
    }

    public void SetRingPoint(LegacyActorMoveGizmoAxis axis, int index, Vector2 point)
    {
        Vector2[] ring = GetRing(axis);
        if ((uint)index >= (uint)ring.Length || ring[index] == point)
            return;
        ring[index] = point;
        _geometryDirty = true;
    }

    public void RefreshRings()
    {
        if (!_geometryDirty)
            return;
        _geometryDirty = false;
        QueueRedraw();
    }

    public void SetHoveredAxis(LegacyActorMoveGizmoAxis axis)
    {
        if (_hoveredAxis == axis)
            return;
        _hoveredAxis = axis;
        QueueRedraw();
    }

    public Vector2 GetAxisScreenPoint(LegacyActorMoveGizmoAxis axis, int sampleIndex)
    {
        int index = sampleIndex % RingSegments;
        if (index < 0)
            index += RingSegments;
        return GetRing(axis)[index];
    }

    public bool TryPickAxis(Vector2 screenPosition, out LegacyActorMoveGizmoAxis axis)
    {
        axis = LegacyActorMoveGizmoAxis.None;
        if (!_handlesVisible)
            return false;

        float nearestDistanceSquared = PickRadiusSquared;
        ConsiderRing(screenPosition, _xRing, LegacyActorMoveGizmoAxis.X, ref axis, ref nearestDistanceSquared);
        ConsiderRing(screenPosition, _yRing, LegacyActorMoveGizmoAxis.Y, ref axis, ref nearestDistanceSquared);
        ConsiderRing(screenPosition, _zRing, LegacyActorMoveGizmoAxis.Z, ref axis, ref nearestDistanceSquared);
        return axis != LegacyActorMoveGizmoAxis.None;
    }

    public override void _Draw()
    {
        if (!_handlesVisible)
            return;

        DrawRing(_xRing, XColor, LegacyActorMoveGizmoAxis.X);
        DrawRing(_yRing, YColor, LegacyActorMoveGizmoAxis.Y);
        DrawRing(_zRing, ZColor, LegacyActorMoveGizmoAxis.Z);
    }

    private void DrawRing(Vector2[] points, Color color, LegacyActorMoveGizmoAxis axis)
    {
        DrawPolyline(points, _hoveredAxis == axis ? HoverColor : color, 3f, true);
    }

    private static void ConsiderRing(
        Vector2 position,
        Vector2[] ring,
        LegacyActorMoveGizmoAxis candidate,
        ref LegacyActorMoveGizmoAxis current,
        ref float nearestDistanceSquared)
    {
        for (int index = 0; index < RingSegments; index++)
        {
            Vector2 start = ring[index];
            Vector2 segment = ring[index + 1] - start;
            float segmentLengthSquared = segment.LengthSquared();
            if (segmentLengthSquared < 0.0001f)
                continue;
            float projection = Mathf.Clamp((position - start).Dot(segment) / segmentLengthSquared, 0f, 1f);
            float distanceSquared = position.DistanceSquaredTo(start + segment * projection);
            if (distanceSquared > nearestDistanceSquared)
                continue;
            nearestDistanceSquared = distanceSquared;
            current = candidate;
        }
    }

    private Vector2[] GetRing(LegacyActorMoveGizmoAxis axis)
        => axis switch
        {
            LegacyActorMoveGizmoAxis.X => _xRing,
            LegacyActorMoveGizmoAxis.Y => _yRing,
            LegacyActorMoveGizmoAxis.Z => _zRing,
            _ => _xRing
        };
}
